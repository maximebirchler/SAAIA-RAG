using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClassifiedSourceWorkContractTests
{
    [Fact]
    public async Task Invalid_context_check_transfers_without_claiming_context_is_missing()
    {
        var llm = new TypedContextRouter(false, true, true);
        var agent = CreateAgent(llm, out var memory);
        var result = await agent.RunAsync([], "Quelle règle documentaire s'applique ?", CancellationToken.None);
        var handoff = Assert.IsType<SAAIA.Contracts.AdvancedAnalysisHandoffEnvelope>(agent.LastAdvancedAnalysisHandoff);
        Assert.Equal("instance_context_check_unconfirmed_outside_local_envelope", handoff.ReasonCode);
        Assert.Null(memory.PendingClarification);
        Assert.Null(result.sourcesPayload);
        Assert.Empty(memory.LastToolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supplied_instance_context_transfers_before_retrieval_and_excludes_assistant_inventions(bool sourceClassification)
    {
        const string context = "Le prototype est assemblé et hors service, avant sa mise en production.";
        var llm = new TypedContextRouter(false, sourceClassification);
        var agent = CreateAgent(llm, out var memory);
        var result = await agent.RunAsync([("user", context), ("assistant", "Fabricated state: already certified."),
                ("assistant", "Additional fabricated conclusion: everything approved.")],
            "Notre dispositif peut-il être mis en service maintenant ?", CancellationToken.None);
        var handoff = Assert.IsType<SAAIA.Contracts.AdvancedAnalysisHandoffEnvelope>(agent.LastAdvancedAnalysisHandoff);
        Assert.Equal("before_retrieval", handoff.TransferStage);
        Assert.Equal("user_instance_context_outside_local_envelope", handoff.ReasonCode);
        Assert.Equal("user_instance_context", handoff.Load.QuestionFocus);
        Assert.Contains(context, handoff.RequestText);
        Assert.DoesNotContain("already certified", handoff.RequestText);
        Assert.DoesNotContain("everything approved", handoff.RequestText);
        Assert.Contains("not documentary evidence", handoff.RequestText);
        Assert.Empty(memory.LastToolNames);
        Assert.Null(result.sourcesPayload);
        Assert.Equal(1, llm.NativeCalls);
    }

    [Fact]
    public async Task Actual_clarification_reply_preserves_original_request_and_resumes_to_advanced_analysis()
    {
        const string request = "Notre dispositif peut-il être mis en service maintenant ?";
        const string context = "Il est assemblé, hors service, avant sa première mise en production.";
        var llm = new TypedContextRouter(true);
        var agent = CreateAgent(llm, out var memory);
        var first = await agent.RunAsync([], request, CancellationToken.None);
        Assert.NotNull(memory.PendingClarification);
        Assert.Null(agent.LastAdvancedAnalysisHandoff);
        Assert.Equal(1, llm.NativeCalls);
        var second = await agent.RunAsync([("user", request), ("assistant", first.finalAnswer)], context, CancellationToken.None);
        Assert.Null(memory.PendingClarification);
        var handoff = Assert.IsType<SAAIA.Contracts.AdvancedAnalysisHandoffEnvelope>(agent.LastAdvancedAnalysisHandoff);
        Assert.Contains(request, handoff.RequestText);
        Assert.Contains(context, handoff.RequestText);
        Assert.Contains("CURRENT_USER_TURN", handoff.RequestText);
        Assert.Equal("before_retrieval", handoff.TransferStage);
        Assert.Null(second.sourcesPayload);
        Assert.Empty(memory.LastToolNames);
        Assert.Equal(2, llm.Classifications);
        Assert.Equal(2, llm.NativeCalls);
    }

    private static ToolAgentOrchestrator CreateAgent(ILlmClient llm, out ToolMemory memory)
    {
        memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { LoadedAtUtc = DateTimeOffset.UtcNow }
        };
        return new ToolAgentOrchestrator(new ApiClient(), llm, memory, new AppSettings { ActiveMode = "strict" });
    }

    [Fact]
    public async Task Valid_source_classification_cannot_request_a_user_document_selection_before_observation()
    {
        var llm = new UnadvertisedQuestionRouter();
        var agent = new ToolAgentOrchestrator(new ApiClient(), llm, new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });
        var method = typeof(ToolAgentOrchestrator).GetMethod("RouterAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<RouterPlan>)method.Invoke(agent,
            new object[] { new List<(string role, string content)> { ("system", "User preference: strict documentary mode. Avoid invention. If missing sources, say so and ask 1 clarification question."), ("user", "Earlier context.") },
                "Trouve dans le corpus une préparation documentée.", CancellationToken.None, false })!;
        var plan = await task;
        Assert.False(plan.NeedClarification);
        Assert.NotNull(plan.SourceBackedMission);
        Assert.Equal("rag.search", Assert.Single(plan.ToolCalls).Name);
        Assert.Equal(2, llm.NativeCalls); // One rejected question, one bounded repair.
        Assert.All(llm.AdvertisedNames, names => Assert.DoesNotContain("request_missing_user_input", names));
    }

    private sealed class UnadvertisedQuestionRouter
        : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public int NativeCalls { get; private set; }
        public List<string[]> AdvertisedNames { get; } = [];
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, LlmStructuredOutputContract contract,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_user_document_binding_v1")
                return Task.FromResult(new SourceBackedAgentCompletion("{\"copiedDocumentIdentity\":\"\",\"userRequiresParticularDocument\":false}", [], "stop"));
            if (contract.Name == "saaia_user_instance_context_v1")
                return Task.FromResult(new SourceBackedAgentCompletion("{\"actualContextSupplied\":false}", [], "stop"));
            Assert.StartsWith("saaia_work_family_", contract.Name);
            Assert.DoesNotContain(messages, message => message.Role == "user" && message.Content!.Contains("User preference:"));
            return Task.FromResult(new SourceBackedAgentCompletion("{\"family\":\"answer\"}", [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            AdvertisedNames.Add(tools.Select(t => t.Name).ToArray());
            var name = NativeCalls == 1 ? "request_missing_user_input" : "submit_source_backed_route";
            object args = NativeCalls == 1 ? new
            {
                question = "Quels fichiers voulez-vous examiner ?", userTextAnchor = "le corpus",
                missingInformation = "The user must select files before retrieval.", resumeRoute = "source_backed"
            } : new
            {
                tool = "search", intent = "answer", query = "préparation documentée",
                answerUnitType = "préparation", answerUnitMode = "named_item", selectionPolicy = "single_item",
                useFocusedDocument = false, questionFocus = "content", namedReferenceKind = "none",
                document = "", pool = 6, count = 1
            };
            return Task.FromResult(new SourceBackedAgentCompletion("",
                [new SourceBackedAgentToolCall("route-" + NativeCalls, name, JsonSerializer.SerializeToElement(args))], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class TypedContextRouter(bool clarifyFirst, bool classifyAsSource = false, bool malformedContext = false)
        : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public int Classifications { get; private set; }
        public int NativeCalls { get; private set; }
        public int ContextCalls { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, LlmStructuredOutputContract contract,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_user_document_binding_v1")
                return Task.FromResult(new SourceBackedAgentCompletion("{\"copiedDocumentIdentity\":\"\",\"userRequiresParticularDocument\":false}", [], "stop"));
            if (contract.Name == "saaia_user_instance_context_v1")
            {
                ContextCalls++;
                if (malformedContext)
                    return Task.FromResult(new SourceBackedAgentCompletion("{\"wrongField\":true}", [], "stop"));
                Assert.DoesNotContain(messages, message => message.Content!.Contains("already certified"));
                return Task.FromResult(new SourceBackedAgentCompletion(JsonSerializer.Serialize(new
                    { actualContextSupplied = !(clarifyFirst && Classifications == 1) }), [], "stop"));
            }
            Classifications++;
            Assert.Equal("saaia_work_family_v6", contract.Name);
            if (clarifyFirst && Classifications == 2)
                Assert.Contains(messages, message => message.Content!.Contains("CURRENT_USER_TURN"));
            var family = classifyAsSource ? "answer" : "missing_instance_facts";
            return Task.FromResult(new SourceBackedAgentCompletion(JsonSerializer.Serialize(new { family }), [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            if (tools.Count == 1 && tools[0].Name == "request_missing_user_input")
                return Task.FromResult(new SourceBackedAgentCompletion("", [new SourceBackedAgentToolCall("missing", tools[0].Name,
                    JsonSerializer.SerializeToElement(new { question = "Quel est l'état actuel du dispositif ?",
                        userTextAnchor = "Notre dispositif", missingInformation = "Actual configuration and state not supplied.",
                        resumeRoute = "source_backed" }))], "tool_calls"));
            Assert.Equal("submit_source_backed_route", Assert.Single(tools).Name);
            return Task.FromResult(new SourceBackedAgentCompletion("",
                [new SourceBackedAgentToolCall("application", "submit_source_backed_route", JsonSerializer.SerializeToElement(new
                {
                    tool = "search", intent = "answer", query = "conditions de mise en service",
                    answerUnitType = "condition", answerUnitMode = "content_claim", selectionPolicy = "single_item",
                    useFocusedDocument = false, questionFocus = "content", namedReferenceKind = "none", document = "", pool = 6, count = 1
                }))], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
