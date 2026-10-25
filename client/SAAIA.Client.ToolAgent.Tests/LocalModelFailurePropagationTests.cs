using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class LocalModelFailurePropagationTests
{
    [Theory]
    [InlineData("native", "fr", "intégrateur")]
    [InlineData("completion", "en", "integrator")]
    [InlineData("stream", "fr", "intégrateur")]
    public async Task Local_transport_origin_reaches_actionable_terminal_and_outer_turn_without_false_success(string kind, string language, string action)
    {
        const string privateMessage = "PRIVATE transport endpoint or response must not enter metadata";
        var original = new HttpRequestException(privateMessage);
        using var http = new HttpClient(new FailureHandler((_, _) => Task.FromException<HttpResponseMessage>(original)));
        var provider = Provider(http);
        LlmCallMetrics? metrics = null;
        provider.CallCompleted += value => metrics = value;
        string turnId;
        using (var ui = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            turnId = ui.TurnId;
            using (var borrowed = ClientDiagnostics.Journal.BeginTurn())
            {
                var failure = await Assert.ThrowsAsync<LocalLlmUnavailableException>(async () =>
                {
                    if (kind == "native")
                        await provider.CompleteAsync(new[] { new SourceBackedAgentMessage("user", "fixture") }, [], 64, CancellationToken.None);
                    else if (kind == "stream")
                        await provider.StreamAsync(new[] { (role: "user", content: "fixture") }, false, _ => { }, CancellationToken.None);
                    else
                        await provider.CompleteAsync(new[] { (role: "user", content: "fixture") }, false, CancellationToken.None);
                });
                Assert.Same(original, failure.InnerException);
                Assert.DoesNotContain(privateMessage, failure.Message);
                Assert.Equal(LocalLlmUnavailableException.ReasonCode, metrics!.ErrorCode);
                var result = await CompleteTerminal(provider, failure, language);
                Assert.Contains(action, result.finalAnswer);
                Assert.Null(result.sourcesPayload);
                borrowed.Complete();
            }
            ui.Complete();
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TurnId == turnId);
        Assert.Equal("failed", record.Outcome);
        Assert.Equal(LocalLlmUnavailableException.ReasonCode, record.Reason);
        foreach (var operation in new[] { "agent.source_backed_pipeline.active.terminal", "agent.turn.final", "turn.end" })
        {
            var probe = Assert.Single(record.Events, item => item.Operation == operation);
            Assert.Equal("failed", probe.Outcome);
            Assert.Equal(LocalLlmUnavailableException.ReasonCode, probe.Reason);
        }
        Assert.DoesNotContain(privateMessage, JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task Backend_http_failure_is_a_pipeline_failure_and_never_claims_local_model_unavailability()
    {
        using var http = new HttpClient(new FailureHandler((_, _) => throw new InvalidOperationException("LLM must not be called")));
        string turnId;
        using (var ui = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            turnId = ui.TurnId;
            var result = await CompleteTerminal(Provider(http), new HttpRequestException("backend connection failed"), "en");
            Assert.DoesNotContain("local model", result.finalAnswer, StringComparison.OrdinalIgnoreCase);
            ui.Complete();
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TurnId == turnId);
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("pipeline_execution_failed", record.Reason);
    }

    [Fact]
    public async Task User_cancellation_at_model_transport_is_not_reclassified_as_model_unavailable()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FailureHandler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        }));
        var provider = Provider(http);
        LlmCallMetrics? metrics = null;
        provider.CallCompleted += value => metrics = value;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(
            new[] { (role: "user", content: "fixture") }, false, cancellation.Token));
        Assert.Equal("cancelled", metrics!.ErrorCode);
    }

    private static LocalLlmProvider Provider(HttpClient http)
    {
        var transport = new OpenAiLlmClient(http);
        transport.ConfigureEndpoint(new OpenAiCompatibleEndpointOptions("http://in-memory.invalid/v1", "fixture", "local", null,
            OpenAiCompatibleDialect.LlamaCpp, null, ensureLocalRuntime: false));
        return new LocalLlmProvider(transport, new LlmProviderDescriptor(LlmProviderMode.Local, "local", "fake-http", "fixture", null, false, false));
    }

    private static async Task<(bool handled, string finalAnswer, object? sourcesPayload)> CompleteTerminal(ILlmProvider provider, Exception failure, string language)
    {
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), provider, new ToolMemory());
        var method = typeof(ToolAgentOrchestrator).GetMethod("CompleteFailedSourceBackedPipelineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<(bool handled, string finalAnswer, object? sourcesPayload)>)method.Invoke(orchestrator,
            new object?[] { "synthetic question", "rag.answer", "router_plan", language, Stopwatch.StartNew(), failure, CancellationToken.None, null, null })!;
        return await task;
    }

    private sealed class FailureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
