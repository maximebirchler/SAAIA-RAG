using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class NativeMissingUserInputContractTests
{
    private const string ToolName = "request_missing_user_input";
    private const string Question = "Compare ces deux documents.";
    private const string Clarification = "Quels sont les titres des deux documents à comparer ?";

    [Theory]
    [InlineData("submit_source_backed_route")]
    [InlineData("submit_document_overview_route")]
    [InlineData("submit_source_backed_grid_route")]
    public void Documentary_router_can_request_essential_missing_input(string family)
        => Assert.Equal(new[] { family, ToolName },
            ToolAgentOrchestrator.BuildNativeRouterSecondStageToolNamesForTests(family, true));

    [Fact]
    public void Open_question_has_no_invented_choices_and_schedules_no_retrieval()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(), Question, ToolName, Arguments().ToJsonString());
        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Plan.NeedClarification);
        Assert.Equal(RouterPlanOrigin.Llm, result.Plan.Origin);
        Assert.Equal(Clarification, Assert.Single(result.Plan.ClarificationQuestions));
        Assert.Empty(result.Plan.Clarification!.Options);
        Assert.Equal("source_backed", result.Plan.Clarification.ResumeRoute);
        Assert.Empty(result.Plan.ToolCalls);
        Assert.Null(result.Plan.SourceBackedMission);
    }

    [Fact]
    public void Explicit_pdf_identity_cannot_be_requested_again_as_missing_input()
    {
        const string request =
            "Donne les conclusions de a755_document_absent_paraphrase_20260909.pdf avec la page source.";
        var arguments = new JsonObject
        {
            ["question"] =
                "Quel est le titre exact du document ou le nom de la source ?",
            ["userTextAnchor"] =
                "a755_document_absent_paraphrase_20260909.pdf",
            ["missingInformation"] =
                "Le document référencé n'est pas présent dans le corpus.",
            ["resumeRoute"] = "source_backed"
        };

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(), request, ToolName, arguments.ToJsonString());

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_missing_user_input_explicit_document_identity_already_supplied",
            result.FailureReason);
        Assert.Empty(result.Plan.ToolCalls);
    }

    [Fact]
    public void Explicit_first_pdf_does_not_hide_a_genuinely_missing_second_document()
    {
        const string request =
            "Compare Guide-A.pdf avec l'autre document.";
        var arguments = new JsonObject
        {
            ["question"] = "Quel est le nom du second document ?",
            ["userTextAnchor"] = "l'autre document",
            ["missingInformation"] =
                "La seconde référence documentaire manque dans la demande.",
            ["resumeRoute"] = "source_backed"
        };

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(), request, ToolName, arguments.ToJsonString());

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Plan.NeedClarification);
        Assert.Equal(
            "Quel est le nom du second document ?",
            Assert.Single(result.Plan.ClarificationQuestions));
    }

    [Fact]
    public async Task Redundant_pdf_identity_clarification_is_repaired_to_source_backed_route()
    {
        const string request =
            "Donne les conclusions de a755_document_absent_paraphrase_20260909.pdf avec la page source.";
        var llm = new RedundantDocumentIdentityRouter(request);
        var sut = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });

        var plan = await sut.RouteOnlyForTests(request, CancellationToken.None);

        Assert.Equal(2, llm.NativeCalls);
        Assert.False(plan.NeedClarification);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            plan.SourceBackedMission);
        Assert.Equal(
            "a755_document_absent_paraphrase_20260909.pdf",
            mission.RequestedDocumentName);
        Assert.Equal("document", mission.NamedReferenceKind);
        Assert.Equal("rag.search", Assert.Single(plan.ToolCalls).Name);
    }

    [Theory]
    [InlineData("empty_question")]
    [InlineData("long_question")]
    [InlineData("invented_anchor")]
    [InlineData("missing_reason")]
    [InlineData("bad_resume")]
    [InlineData("extra_field")]
    [InlineData("numeric_question")]
    public void Missing_input_payload_must_be_bounded_and_anchored_to_actual_request(string mutation)
    {
        var args = Arguments();
        switch (mutation)
        {
            case "empty_question": args["question"] = ""; break;
            case "long_question": args["question"] = new string('x', 301); break;
            case "invented_anchor": args["userTextAnchor"] = "Documents Atlas et Boreal"; break;
            case "missing_reason": args.Remove("missingInformation"); break;
            case "bad_resume": args["resumeRoute"] = "invented_route"; break;
            case "extra_field": args["options"] = new JsonArray("Invented document"); break;
            case "numeric_question": args["question"] = 42; break;
        }
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(), Question, ToolName, args.ToJsonString());
        Assert.False(result.Accepted);
        Assert.Empty(result.Plan.ToolCalls);
    }

    [Fact]
    public async Task Prior_output_units_do_not_force_retrieval_when_model_requests_missing_user_input()
    {
        var llm = new MissingInputRouter();
        var sut = new ToolAgentOrchestrator(new ApiClient(), llm, new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });
        var plan = await sut.RouteOnlyForTests(Question, CancellationToken.None);
        Assert.Equal(1, llm.NativeCalls);
        Assert.Equal(1, llm.UnitCalls);
        Assert.True(plan.NeedClarification);
        Assert.Equal(Clarification, Assert.Single(plan.ClarificationQuestions));
        Assert.Empty(plan.ToolCalls);
        Assert.Null(plan.SourceBackedMission);
    }

    private static JsonObject Arguments() => new()
    {
        ["question"] = Clarification,
        ["userTextAnchor"] = Question,
        ["missingInformation"] = "Les deux références ne figurent ni dans la demande ni dans la conversation.",
        ["resumeRoute"] = "source_backed"
    };

    private sealed class MissingInputRouter : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public int NativeCalls { get; private set; }
        public int UnitCalls { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_work_family_v1")
                return Task.FromResult(new SourceBackedAgentCompletion("{\"family\":\"answer\"}", [], "stop"));
            Assert.Equal("saaia_answer_units_v2", contract.Name);
            UnitCalls++;
            return Task.FromResult(new SourceBackedAgentCompletion(JsonSerializer.Serialize(new
            {
                requestAnchor = Question, requestedParts = new[] { Question }, unitType = "comparaison", outputKind = "facts", quantityKind = "one", count = 1
            }), [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            Assert.Equal(1, NativeCalls);
            Assert.Contains(tools, tool => tool.Name == ToolName);
            Assert.True(maxTokens <= 384);
            return Task.FromResult(new SourceBackedAgentCompletion("",
                [new SourceBackedAgentToolCall("missing-input", ToolName, JsonSerializer.SerializeToElement(Arguments()))], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RedundantDocumentIdentityRouter(string request)
        : ILlmClient, ISourceBackedAgentLlmClient,
            ISourceBackedAgentStructuredLlmClient
    {
        public int NativeCalls { get; private set; }

        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_work_family_v1")
            {
                return Task.FromResult(new SourceBackedAgentCompletion(
                    "{\"family\":\"answer\"}", [], "stop"));
            }

            Assert.Equal("saaia_answer_units_v2", contract.Name);
            return Task.FromResult(new SourceBackedAgentCompletion(
                JsonSerializer.Serialize(new
                {
                    requestAnchor = request,
                    requestedParts = new[] { "conclusions avec page source" },
                    unitType = "conclusion documentée",
                    outputKind = "facts",
                    quantityKind = "one",
                    count = 1
                }),
                [],
                "stop"));
        }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            NativeCalls++;
            if (NativeCalls == 1)
            {
                Assert.Contains(tools, tool => tool.Name == ToolName);
                return Task.FromResult(new SourceBackedAgentCompletion(
                    "",
                    [new SourceBackedAgentToolCall(
                        "missing-input",
                        ToolName,
                        JsonSerializer.SerializeToElement(new
                        {
                            question =
                                "Quel est le titre exact du document ou le nom de la source ?",
                            userTextAnchor =
                                "a755_document_absent_paraphrase_20260909.pdf",
                            missingInformation =
                                "Le document référencé n'est pas présent dans le corpus.",
                            resumeRoute = "source_backed"
                        }))],
                    "tool_calls"));
            }

            Assert.Equal(2, NativeCalls);
            Assert.DoesNotContain(tools, tool => tool.Name == ToolName);
            Assert.DoesNotContain(
                tools,
                tool => tool.Name == "request_user_clarification");
            return Task.FromResult(new SourceBackedAgentCompletion(
                "",
                [new SourceBackedAgentToolCall(
                    "source-route",
                    "submit_source_backed_route",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = "search",
                        intent = "answer",
                        query = "conclusions page source",
                        answerUnitType = "conclusion documentée",
                        answerUnitMode = "content_claim",
                        selectionPolicy = "single_item",
                        useFocusedDocument = false,
                        questionFocus = "content",
                        namedReferenceKind = "document",
                        document =
                            "a755_document_absent_paraphrase_20260909.pdf",
                        pool = 6,
                        count = 1
                    }))],
                "tool_calls"));
        }

        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct)
            => throw new NotSupportedException();
    }
}
