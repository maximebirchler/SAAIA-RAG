using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class GroundedAnswerUnitContractTests
{
    [Fact]
    public async Task A_changed_unit_count_uses_the_existing_route_repair_without_publishing_the_drift()
    {
        var llm = new RecordingRouter { DriftFirstCount = true };
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), llm, new ToolMemory(), new AppSettings { ActiveMode = "strict" });
        var plan = await orchestrator.RouteOnlyForTests("Donne un exemple de procédure.", CancellationToken.None);
        Assert.Equal(1, llm.UnitCalls);
        Assert.Equal(2, llm.NativeCalls);
        Assert.Equal(1, plan.SourceBackedMission!.AtomicEvidenceCount);
        Assert.Contains("native_source_route_answer_units_changed", llm.LastUserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clarification_cannot_erase_a_counted_request_at_the_multi_item_boundary()
    {
        var llm = new CountedObjectClarificationRouter();
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });

        var plan = await orchestrator.RouteOnlyForTests(
            CountedObjectClarificationRouter.Question,
            CancellationToken.None);

        Assert.Equal(1, llm.UnitCalls);
        Assert.Equal(2, llm.NativeCalls);
        Assert.False(plan.NeedClarification);
        Assert.True(
            plan.SourceBackedMission is not null,
            JsonSerializer.Serialize(new
            {
                plan.Intent,
                Tools = plan.ToolCalls.Select(static call => call.Name),
                llm.LastUserMessage
            }));
        Assert.Equal(5, plan.SourceBackedMission!.AtomicEvidenceCount);
        Assert.Equal("explicit_set", plan.SourceBackedMission.SelectionPolicy);
        Assert.Contains(
            "native_clarification_route_contract_invalid",
            llm.LastUserMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_document_bounded_extraction_accepts_the_content_claim_transport_normalization()
    {
        var llm = new NamedDocumentBoundedExtractionRouter();
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });

        var plan = await orchestrator.RouteOnlyForTests(
            NamedDocumentBoundedExtractionRouter.Question,
            CancellationToken.None);

        Assert.Equal(1, llm.UnitCalls);
        Assert.Equal(1, llm.NativeCalls);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            plan.SourceBackedMission);
        Assert.Equal(6, mission.AtomicEvidenceCount);
        Assert.Equal("explicit_set", mission.SelectionPolicy);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.Equal("Guide.pdf", mission.RequestedDocumentName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standalone_grounded_units_constrain_the_next_route_without_changing_its_query(bool priorConversation)
    {
        var llm = new RecordingRouter();
        var memory = new ToolMemory();
        if (priorConversation) memory.LastAssistantAnswer = "Une réponse antérieure.";
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), llm, memory, new AppSettings { ActiveMode = "strict" });
        var plan = await orchestrator.RouteOnlyForTests("Donne un exemple de procédure.", CancellationToken.None);
        Assert.Equal(priorConversation ? 0 : 1, llm.UnitCalls);
        Assert.Equal(1, llm.NativeCalls);
        Assert.Equal("rag.answer", plan.Intent);
        if (!priorConversation)
        {
            var properties = llm.LastTool!.Parameters.GetProperty("properties");
            Assert.Equal("procedure", properties.GetProperty("answerUnitType").GetProperty("enum")[0].GetString());
            Assert.Single(properties.GetProperty("answerUnitMode").GetProperty("enum").EnumerateArray());
            Assert.Equal("named_item", properties.GetProperty("answerUnitMode").GetProperty("enum")[0].GetString());
            Assert.Equal("single_item", properties.GetProperty("selectionPolicy").GetProperty("enum")[0].GetString());
            Assert.Equal(1, properties.GetProperty("count").GetProperty("minimum").GetInt32());
            Assert.Equal(1, properties.GetProperty("count").GetProperty("maximum").GetInt32());
            Assert.Equal(1, plan.SourceBackedMission!.AtomicEvidenceCount);
        }
        Assert.Equal("procédure observée", Assert.Single(plan.ToolCalls).Args.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("one", 1, "un exemple", true)]
    [InlineData("one", 2, "un exemple", false)]
    [InlineData("exact", 1, "un exemple", false)]
    [InlineData("exact", 3, "trois exemples", true)]
    [InlineData("open", 3, "des exemples", true)]
    [InlineData("open", 0, "des exemples", false)]
    [InlineData("exact", 81, "81 exemples", false)]
    [InlineData("unknown", 1, "un exemple", false)]
    [InlineData("one", 1, "invented request", false)]
    public void Unit_reader_requires_literal_request_anchor_and_coherent_contract(string quantity, int count, string anchor, bool expected)
    {
        var question = anchor == "invented request" ? "Donne un exemple." : "Donne " + anchor + ".";
        var content = JsonSerializer.Serialize(new { requestAnchor = anchor, requestedParts = new[] { anchor }, unitType = "procedure", outputKind = "object", quantityKind = quantity, count });
        Assert.Equal(expected, Read(new SourceBackedAgentCompletion(content, [], "stop"), question));
    }

    [Theory]
    [InlineData("{\"requestAnchor\":\"un exemple\"", "length")]
    [InlineData("{\"requestAnchor\":\"un exemple\",\"requestedParts\":[\"un exemple\"],\"unitType\":\"procedure\",\"outputKind\":\"object\",\"quantityKind\":\"one\",\"count\":1,\"query\":\"invented\"}", "stop")]
    [InlineData("{\"requestAnchor\":\"un exemple\",\"requestedParts\":[\"un exemple\"],\"unitType\":\"procedure\",\"outputKind\":\"object\",\"quantityKind\":\"one\",\"count\":1}", "length")]
    public void Unit_reader_does_not_accept_incomplete_or_extended_protocol(string content, string finish)
        => Assert.False(Read(new SourceBackedAgentCompletion(content, [], finish), "Donne un exemple."));

    [Fact]
    public void Factual_requested_parts_raise_an_undercounted_single_unit()
    {
        const string question = "Donne le principe et la limite applicable.";
        var completion = new SourceBackedAgentCompletion(
            JsonSerializer.Serialize(new
            {
                requestAnchor = "le principe et la limite",
                requestedParts = new[] { "le principe", "la limite" },
                unitType = "facts",
                outputKind = "facts",
                quantityKind = "one",
                count = 1
            }),
            [],
            "stop");
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedAnswerUnits",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = [completion, question, null];

        Assert.True((bool)method!.Invoke(null, args)!);
        var units = Assert.IsAssignableFrom<object>(args[2]);
        var unitsType = units.GetType();
        Assert.Equal(2, unitsType.GetProperty("Count")!.GetValue(units));
        Assert.Equal(
            "explicit_set",
            unitsType.GetProperty("SelectionPolicy")!.GetValue(units));
    }

    [Fact]
    public void Unnamed_homogeneous_collection_keeps_one_literal_part_and_the_explicit_count()
    {
        const string question = "Donne les six fonctions dans l'ordre.";
        var completion = new SourceBackedAgentCompletion(
            JsonSerializer.Serialize(new
            {
                requestAnchor = "les six fonctions",
                requestedParts = new[] { "les six fonctions" },
                unitType = "fonction",
                outputKind = "facts",
                quantityKind = "exact",
                count = 6
            }),
            [],
            "stop");
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedAnswerUnits",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = [completion, question, null];

        Assert.True((bool)method!.Invoke(null, args)!);
        var units = Assert.IsAssignableFrom<object>(args[2]);
        var unitsType = units.GetType();
        Assert.Equal(6, unitsType.GetProperty("Count")!.GetValue(units));
        Assert.Equal(
            new[] { "les six fonctions" },
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                unitsType.GetProperty("RequestedParts")!.GetValue(units)));
    }

    [Fact]
    public void Counted_object_descriptors_collapse_to_the_literal_group_without_changing_count()
    {
        const string question =
            "Fais-moi 5 repas étudiant pas trop chers à partir des PDF.";
        var completion = new SourceBackedAgentCompletion(
            JsonSerializer.Serialize(new
            {
                requestAnchor = "5 repas étudiant pas trop chers",
                requestedParts = new[]
                {
                    "repas",
                    "étudiant",
                    "pas trop chers"
                },
                unitType = "repas",
                outputKind = "object",
                quantityKind = "exact",
                count = 5
            }),
            [],
            "stop");
        var units = ReadUnits(completion, question);
        var unitsType = units.GetType();

        Assert.Equal(5, unitsType.GetProperty("Count")!.GetValue(units));
        Assert.Equal(
            new[] { "5 repas étudiant pas trop chers" },
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                unitsType.GetProperty("RequestedParts")!.GetValue(units)));
    }

    [Fact]
    public void Counted_objects_keep_explicit_parts_when_cardinality_matches_count()
    {
        const string question =
            "Propose un ordinateur portable et un écran documentés.";
        var completion = new SourceBackedAgentCompletion(
            JsonSerializer.Serialize(new
            {
                requestAnchor = "un ordinateur portable et un écran",
                requestedParts = new[]
                {
                    "un ordinateur portable",
                    "un écran"
                },
                unitType = "équipement",
                outputKind = "object",
                quantityKind = "exact",
                count = 2
            }),
            [],
            "stop");
        var units = ReadUnits(completion, question);
        var unitsType = units.GetType();

        Assert.Equal(
            new[] { "un ordinateur portable", "un écran" },
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                unitsType.GetProperty("RequestedParts")!.GetValue(units)));
    }

    [Fact]
    public void Unit_prompt_forbids_inventing_labels_for_counted_homogeneous_members()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "BuildGroundedAnswerUnitPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var prompt = Assert.IsType<string>(method!.Invoke(null, null));

        Assert.Contains("unnamed homogeneous members", prompt, StringComparison.Ordinal);
        Assert.Contains("never invent member labels", prompt, StringComparison.Ordinal);
        Assert.Contains("keeps the object and its constraints together", prompt, StringComparison.Ordinal);
        Assert.Contains("do not split adjectives, audience, cost, use or other constraints", prompt, StringComparison.Ordinal);
    }

    private static bool Read(SourceBackedAgentCompletion completion, string question)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryReadGroundedAnswerUnits", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = [completion, question, null];
        return (bool)method.Invoke(null, args)!;
    }

    private static object ReadUnits(
        SourceBackedAgentCompletion completion,
        string question)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedAnswerUnits",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = [completion, question, null];
        Assert.True((bool)method!.Invoke(null, args)!);
        return Assert.IsAssignableFrom<object>(args[2]);
    }

    private sealed class RecordingRouter : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public bool DriftFirstCount { get; init; }
        public string LastUserMessage { get; private set; } = "";
        public int UnitCalls { get; private set; }
        public int NativeCalls { get; private set; }
        public SourceBackedAgentToolDefinition? LastTool { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_work_family_v1")
                return Task.FromResult(new SourceBackedAgentCompletion("{\"family\":\"answer\"}", [], "stop"));
            Assert.Equal("saaia_answer_units_v2", contract.Name);
            UnitCalls++;
            Assert.Equal(1, UnitCalls);
            Assert.False(SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(contract.Name));
            Assert.Equal(384, maxTokens);
            Assert.Equal("CURRENT_REQUEST: Donne un exemple de procédure.", messages[1].Content);
            Assert.Equal(new[] { "requestAnchor", "requestedParts", "unitType", "outputKind", "quantityKind", "count" },
                contract.Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
            return Task.FromResult(new SourceBackedAgentCompletion(
                "{\"requestAnchor\":\"un exemple de procédure\",\"requestedParts\":[\"un exemple de procédure\"],\"unitType\":\"procedure\",\"outputKind\":\"object\",\"quantityKind\":\"one\",\"count\":1}", [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            Assert.True(NativeCalls <= (DriftFirstCount ? 2 : 1));
            LastUserMessage = messages.Last(m => m.Role == "user").Content!;
            LastTool = Assert.Single(tools, tool => tool.Name == "submit_source_backed_route");
            Assert.Equal(NativeCalls == 1
                ? new[] { "submit_source_backed_route", "request_missing_user_input" }
                : new[] { "submit_source_backed_route" }, tools.Select(tool => tool.Name));
            var drift = DriftFirstCount && NativeCalls == 1;
            var args = JsonSerializer.SerializeToElement(new
            {
                tool = "search",
                intent = "answer",
                query = "procédure observée",
                answerUnitType = "procedure",
                answerUnitMode = "named_item",
                selectionPolicy = drift ? "explicit_set" : "single_item",
                useFocusedDocument = false,
                questionFocus = "content",
                namedReferenceKind = "none",
                document = (string?)null,
                pool = 5,
                count = drift ? 3 : 1
            });
            return Task.FromResult(new SourceBackedAgentCompletion("", [new SourceBackedAgentToolCall("route", "submit_source_backed_route", args)], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct) => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountedObjectClarificationRouter
        : ILlmClient, ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient
    {
        public const string Question =
            "Fais-moi 5 repas étudiant pas trop chers à partir des PDF.";

        public int UnitCalls { get; private set; }
        public int NativeCalls { get; private set; }
        public string LastUserMessage { get; private set; } = string.Empty;

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
            UnitCalls++;
            return Task.FromResult(new SourceBackedAgentCompletion(
                JsonSerializer.Serialize(new
                {
                    requestAnchor = "5 repas étudiant pas trop chers",
                    requestedParts = new[]
                    {
                        "5 repas étudiant pas trop chers"
                    },
                    unitType = "repas",
                    outputKind = "object",
                    quantityKind = "exact",
                    count = 5
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
            LastUserMessage = messages.Last(m => m.Role == "user").Content!;
            if (NativeCalls == 1)
            {
                Assert.Contains(tools, static tool =>
                    tool.Name == "request_missing_user_input");
                return Task.FromResult(new SourceBackedAgentCompletion(
                    "",
                    [new SourceBackedAgentToolCall(
                        "clarify",
                        "request_missing_user_input",
                        JsonSerializer.SerializeToElement(new
                        {
                            question =
                                "Quels types de repas faut-il privilégier ?",
                            userTextAnchor = Question,
                            missingInformation =
                                "Le type de repas n'est pas précisé.",
                            resumeRoute = "source_backed"
                        }))],
                    "tool_calls"));
            }

            Assert.Equal(2, NativeCalls);
            Assert.DoesNotContain(tools, static tool =>
                tool.Name == "request_missing_user_input");
            return Task.FromResult(new SourceBackedAgentCompletion(
                "",
                [new SourceBackedAgentToolCall(
                    "route",
                    "submit_source_backed_route",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = "search",
                        intent = "answer",
                        query = "repas étudiant pas trop chers",
                        answerUnitType = "repas",
                        answerUnitMode = "named_item",
                        selectionPolicy = "explicit_set",
                        useFocusedDocument = false,
                        questionFocus = "content",
                        namedReferenceKind = "none",
                        document = (string?)null,
                        pool = 8,
                        count = 5
                    }))],
                "tool_calls"));
        }

        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct) => throw new NotSupportedException();

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NamedDocumentBoundedExtractionRouter
        : ILlmClient, ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient
    {
        public const string Question =
            "Liste, dans l'ordre indiqué par Guide.pdf, les 6 fonctions principales et cite le document.";

        public int UnitCalls { get; private set; }
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
            UnitCalls++;
            return Task.FromResult(new SourceBackedAgentCompletion(
                JsonSerializer.Serialize(new
                {
                    requestAnchor = "les 6 fonctions principales",
                    requestedParts = new[] { "les 6 fonctions principales" },
                    unitType = "fonction",
                    outputKind = "object",
                    quantityKind = "exact",
                    count = 6
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
            return Task.FromResult(new SourceBackedAgentCompletion(
                "",
                [new SourceBackedAgentToolCall(
                    "route",
                    "submit_source_backed_route",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = "search",
                        intent = "answer",
                        query = "six fonctions principales",
                        answerUnitType = "fonction",
                        answerUnitMode = "named_item",
                        selectionPolicy = "explicit_set",
                        useFocusedDocument = false,
                        questionFocus = "content",
                        namedReferenceKind = "document",
                        document = "Guide.pdf",
                        pool = 10,
                        count = 6
                    }))],
                "tool_calls"));
        }

        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct) => throw new NotSupportedException();

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
