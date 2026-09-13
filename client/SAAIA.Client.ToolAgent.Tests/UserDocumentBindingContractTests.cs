using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class UserDocumentBindingContractTests
{
    [Fact]
    public async Task User_selected_document_without_identity_asks_which_document_before_retrieval()
    {
        var agent = Create(new BindingRouter("", true, "document"), out var memory);
        var result = await agent.RunAsync([], "Donne la conclusion du document que je veux vérifier.", CancellationToken.None);
        Assert.Contains("document", result.finalAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("référence", result.finalAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(memory.PendingClarification);
        Assert.Null(agent.LastAdvancedAnalysisHandoff);
        Assert.Empty(memory.LastToolNames);
        Assert.Null(result.sourcesPayload);
    }

    [Theory]
    [InlineData(false, "other")]
    [InlineData(true, "corpus_candidates")]
    public async Task Open_corpus_work_does_not_require_a_user_document_selection(bool particular, string kind)
    {
        var agent = Create(new BindingRouter("", particular, kind), out _);
        var plan = await Route(agent, [], "Choisis un document du corpus et donne sa conclusion.");
        Assert.False(plan.NeedClarification);
        Assert.NotNull(plan.SourceBackedMission);
    }

    [Fact]
    public async Task Literal_document_identity_corrects_false_missing_instance_classification()
    {
        var agent = Create(new BindingRouter("Guide.pdf", true, "document", worldFamily: true), out _);
        var plan = await Route(agent, [], "Donne la conclusion de Guide.pdf.");
        Assert.False(plan.NeedClarification);
        Assert.NotNull(plan.SourceBackedMission);
    }

    [Theory]
    [InlineData("Donne la conclusion de Guide.pdf.")]
    [InlineData("Give the conclusion of Guide.pdf.")]
    [InlineData("Da la conclusión de Guide.pdf.")]
    [InlineData("Dê a conclusão de Guide.pdf.")]
    [InlineData("Nenne das Fazit von Guide.pdf.")]
    [InlineData("Fornisci la conclusione di Guide.pdf.")]
    public async Task Explicit_pdf_identity_cannot_be_requested_again_even_when_both_extractors_omit_it(string request)
    {
        var agent = Create(new BindingRouter("", true, "document"), out _);
        var plan = await Route(agent, [], request);
        Assert.False(plan.NeedClarification);
        Assert.NotNull(plan.SourceBackedMission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ungrounded_or_invalid_identity_checks_transfer_without_inventing_missing_information(bool malformed)
    {
        var agent = Create(new BindingRouter("Fabricated.pdf", true, "document", malformed: malformed), out var memory);
        var result = await agent.RunAsync([], "Donne la conclusion du document que je veux vérifier.", CancellationToken.None);
        var handoff = Assert.IsType<SAAIA.Contracts.AdvancedAnalysisHandoffEnvelope>(agent.LastAdvancedAnalysisHandoff);
        Assert.Equal("user_reference_check_unconfirmed_outside_local_envelope", handoff.ReasonCode);
        Assert.Null(memory.PendingClarification);
        Assert.Empty(memory.LastToolNames);
        Assert.Null(result.sourcesPayload);
    }

    [Fact]
    public async Task Document_identity_from_user_history_is_available_but_assistant_identity_is_not()
    {
        var agent = Create(new BindingRouter("Guide.pdf", true, "document"), out _);
        var userPlan = await Route(agent, [("user", "Le document que je choisis est Guide.pdf.")], "Donne sa conclusion.");
        Assert.False(userPlan.NeedClarification);
        Assert.NotNull(userPlan.SourceBackedMission);
        var assistantAgent = Create(new BindingRouter("Guide.pdf", true, "document"), out _);
        var assistantPlan = await Route(assistantAgent, [("assistant", "I invented the filename Guide.pdf.")], "Donne sa conclusion.");
        Assert.NotNull(assistantPlan.SourceBackedMission);
        Assert.Equal("user_reference_context", assistantPlan.SourceBackedMission!.QuestionFocus);
    }

    private static ToolAgentOrchestrator Create(ILlmClient llm, out ToolMemory memory)
    {
        memory = new ToolMemory { CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { LoadedAtUtc = DateTimeOffset.UtcNow } };
        return new ToolAgentOrchestrator(new ApiClient(), llm, memory, new AppSettings { ActiveMode = "strict" });
    }
    private static async Task<RouterPlan> Route(ToolAgentOrchestrator agent, List<(string role, string content)> history, string request)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("RouterAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<RouterPlan>)method.Invoke(agent, [history, request, CancellationToken.None, false])!;
    }

    private sealed class BindingRouter(string identity, bool particular, string kind, bool worldFamily = false, bool malformed = false)
        : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            object result = contract.Name switch
            {
                "saaia_user_document_binding_v1" when malformed => new { wrong = true },
                "saaia_user_document_binding_v1" => new { copiedDocumentIdentity = identity, userRequiresParticularDocument = particular },
                "saaia_document_target_v1" => new { copiedIdentity = "", objectKind = kind },
                "saaia_user_instance_context_v1" => new { actualContextSupplied = false },
                _ when contract.Name.StartsWith("saaia_work_family_") => new { family = worldFamily ? "missing_instance_facts" : "answer" },
                _ => new { }
            };
            return Task.FromResult(new SourceBackedAgentCompletion(JsonSerializer.Serialize(result), [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            var missing = tools.Count == 1 && tools[0].Name == "request_missing_user_input";
            var name = missing ? "request_missing_user_input" : "submit_source_backed_route";
            object args = missing ? new { question = "Quel document souhaitez-vous vérifier ?", userTextAnchor = "le document que je veux vérifier",
                missingInformation = "The particular document the user selected has not been identified.", resumeRoute = "source_backed" }
                : new { tool = "search", intent = "answer", query = "conclusion documentaire", answerUnitType = "fact",
                    answerUnitMode = "content_claim", selectionPolicy = "single_item", useFocusedDocument = false,
                    questionFocus = "content", namedReferenceKind = "none", document = "", pool = 6, count = 1 };
            return Task.FromResult(new SourceBackedAgentCompletion("", [new SourceBackedAgentToolCall("route", name, JsonSerializer.SerializeToElement(args))], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct) => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct) => throw new NotSupportedException();
    }
}
