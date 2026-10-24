using System.Net;
using SAAIA.Client.Core.Diagnostics;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class LlmCallDiagnosticsTests
{
    [Theory]
    [InlineData(nameof(RagChatAgent.RunAsync))]
    [InlineData(nameof(RagChatAgent.ExecuteDirectCommandAsync))]
    public void Both_application_entry_points_install_the_same_owned_subscription(string method)
        => Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(RagChatAgent), method),
            call => call.DeclaringType == typeof(LlmCallDiagnosticSubscription));

    [Fact]
    public async Task Shared_provider_routes_each_call_once_to_its_explicit_turn_even_with_one_trace_and_no_callback_context()
    {
        var journal = new ClientDiagnosticJournal();
        var provider = new MetricsProvider();
        string firstId;
        string secondId;
        using (var first = journal.BeginTurn("session-a", independent: true))
        {
            firstId = first.TurnId;
            using var firstSubscription = new LlmCallDiagnosticSubscription(provider, first);
            using (var second = journal.BeginTurn("session-b", independent: true))
            {
                secondId = second.TurnId;
                Assert.Equal(first.TraceId, second.TraceId);
                Assert.NotEqual(first.TurnId, second.TurnId);
                using var secondSubscription = new LlmCallDiagnosticSubscription(provider, second);
                Task delivery;
                using (ExecutionContext.SuppressFlow())
                    delivery = Task.Run(() =>
                    {
                        provider.Publish(Metrics(first, "call-a", 11));
                        provider.Publish(Metrics(second, "call-b", 22));
                        provider.Publish(Metrics(first, "unattributable", 999) with { TurnId = null });
                    });
                await delivery;
                second.Complete();
            }
            first.Complete();
        }
        var records = journal.Last(2);
        var firstCall = Assert.Single(records.Single(record => record.TurnId == firstId).Events,
            entry => entry.Operation == "model.call");
        var secondCall = Assert.Single(records.Single(record => record.TurnId == secondId).Events,
            entry => entry.Operation == "model.call");
        Assert.Equal("call-a", firstCall.Identifiers["requestId"]);
        Assert.Equal(11, firstCall.Measurements["inputTokens"]);
        Assert.Equal("call-b", secondCall.Identifiers["requestId"]);
        Assert.Equal(22, secondCall.Measurements["inputTokens"]);
        Assert.Equal(0, provider.ObserverCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_broken_call_observer_cannot_replace_a_provider_result_or_original_exception(bool fail)
    {
        var original = new InvalidOperationException("original provider failure");
        using var http = new HttpClient(new CompletionHandler(fail ? original : null));
        var transport = new OpenAiLlmClient(http);
        transport.ConfigureEndpoint(new OpenAiCompatibleEndpointOptions(
            "http://in-memory.invalid/v1", "fixture", "local", null,
            OpenAiCompatibleDialect.LlamaCpp, null, ensureLocalRuntime: false));
        var provider = new LocalLlmProvider(transport,
            new LlmProviderDescriptor(LlmProviderMode.Local, "fixture", "fake-http", "fixture", null, false, false));
        var observed = new List<LlmCallMetrics>();
        provider.CallCompleted += _ => throw new IOException("broken observer");
        provider.CallCompleted += observed.Add;
        var before = ClientDiagnostics.Journal.CollectorFailures;
        using var turn = ClientDiagnostics.Journal.BeginTurn(independent: true);
        var task = provider.CompleteAsync(new[] { (role: "user", content: "fixture") }, false, CancellationToken.None);
        if (fail) Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
        else Assert.Equal("fixture answer", await task);
        var metric = Assert.Single(observed);
        Assert.Equal(turn.TraceId, metric.TraceId);
        Assert.Equal(turn.TurnId, metric.TurnId);
        Assert.Equal(!fail, metric.Success);
        Assert.True(ClientDiagnostics.Journal.CollectorFailures > before);
        turn.Complete(fail ? "failed" : "success");
    }

    private static LlmCallMetrics Metrics(ClientDiagnosticJournal.TurnScope turn, string request, int tokens)
        => new(DateTimeOffset.UtcNow, request, turn.TraceId, LlmProviderMode.Local, "fixture", "fixture",
            LlmLogicalRole.Writer, 1, null, new(tokens, 1), null, true, 0, null, turn.TurnId);

    private sealed class CompletionHandler(Exception? failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => failure is not null ? Task.FromException<HttpResponseMessage>(failure)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"fixture answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""")
                });
    }

    private sealed class MetricsProvider : ILlmProvider
    {
        public LlmProviderDescriptor Descriptor => new(LlmProviderMode.Local, "fixture", "fixture", "fixture", null, false, false);
        public event Action<LlmCallMetrics>? CallCompleted;
        internal int ObserverCount => CallCompleted?.GetInvocationList().Length ?? 0;
        internal void Publish(LlmCallMetrics metrics) => CallCompleted?.Invoke(metrics);
        public void ConfigureGeneration(double temperature, int maximumOutputTokens) => throw new NotSupportedException();
        public IDisposable BeginTurn(string? correlationId = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct) => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false) => throw new NotSupportedException();
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null) => throw new NotSupportedException();
        public Task<int?> CountInputTokensAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, CancellationToken ct, bool requireToolCall = false) => throw new NotSupportedException();
        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
