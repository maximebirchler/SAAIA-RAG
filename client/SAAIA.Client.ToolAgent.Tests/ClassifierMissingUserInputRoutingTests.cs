using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClassifierMissingUserInputRoutingTests
{
    [Theory]
    [InlineData("Notre fonction de sécurité doit-elle être validée dès maintenant ?")]
    [InlineData("Must our safety function be validated now?")]
    [InlineData("Muss unsere Sicherheitsfunktion jetzt validiert werden?")]
    [InlineData("¿Debe nuestra función de seguridad validarse ahora?")]
    [InlineData("A nossa função de segurança deve ser validada agora?")]
    [InlineData("La nostra funzione di sicurezza deve essere validata ora?")]
    public async Task Classifier_clarification_bypasses_answer_units_and_schedules_no_retrieval(string request)
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { LoadedAtUtc = DateTimeOffset.UtcNow }
        };
        var llm = new MissingInputRouter(request);
        var agent = new ToolAgentOrchestrator(new ApiClient(), llm, memory,
            new AppSettings { ActiveMode = "strict" });
        var result = await agent.RunAsync([], request, CancellationToken.None);
        Assert.Equal(1, llm.StructuredCalls);
        Assert.Equal(0, llm.NativeCalls);
        Assert.EndsWith("?", result.finalAnswer.Trim());
        Assert.DoesNotContain("regulatory", result.finalAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.sourcesPayload);
        Assert.Null(agent.LastAdvancedAnalysisHandoff);
        Assert.NotNull(memory.PendingClarification);
        Assert.Equal(request, memory.PendingClarification.OriginalUserMessage);
        Assert.Empty(memory.LastToolNames);
    }

    private sealed class MissingInputRouter(string request)
        : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public const string OpenQuestion = "What is the current configuration and project phase of this installation?";
        public int StructuredCalls { get; private set; }
        public int NativeCalls { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, LlmStructuredOutputContract contract,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            StructuredCalls++;
            Assert.Equal(1, StructuredCalls); // No answer-unit extraction for a clarification.
            Assert.Contains("missing_instance_facts", contract.Schema.GetProperty("properties")
                .GetProperty("family").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains(messages, m => m.Role == "user" && m.Content!.Contains(request, StringComparison.Ordinal));
            return Task.FromResult(new SourceBackedAgentCompletion("{\"family\":\"missing_instance_facts\"}", [], "stop"));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            Assert.Equal(1, NativeCalls);
            Assert.Equal("request_missing_user_input", Assert.Single(tools).Name);
            return Task.FromResult(new SourceBackedAgentCompletion("",
                [new SourceBackedAgentToolCall("missing", "request_missing_user_input", JsonSerializer.SerializeToElement(new
                {
                    question = OpenQuestion, userTextAnchor = request,
                    missingInformation = "The user's actual configuration and project phase are not supplied; general rules cannot establish them.",
                    resumeRoute = "source_backed"
                }))], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
