using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class ExplicitGeneralRouteFallbackTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Bonjour, peux-tu afficher la réponse de contrôle du streaming ?")]
    [InlineData("Comment vas-tu ?")]
    public async Task Accepted_general_route_reaches_the_general_stream_without_documentary_fallbacks(string question)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new RecordingApiHandler();
        using var http = new HttpClient(handler);
        var api = new ApiClient(http);
        api.Configure("http://127.0.0.1:1", "synthetic-general-route-key", "synthetic-user");
        var llm = new RoutedLlm(documentary: false);
        var memory = WarmMemory();
        var agent = new ToolAgentOrchestrator(api, llm, memory, new AppSettings { ActiveMode = "strict" });
        var deltas = new List<string>();
        var previousContentMode = Environment.GetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT");
        try
        {
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", "false");
            (string finalAnswer, object? sourcesPayload) result;
            string traceId;
            using (var turn = ClientDiagnostics.Journal.BeginTurn(independent: true))
            {
                traceId = ClientDiagnostics.Journal.CurrentTraceId!;
                result = await agent.RunAsync([], question, timeout.Token, onDelta: deltas.Add);
                turn.Complete();
            }

            Assert.Equal(2, llm.NativeCalls);
            Assert.Equal(1, llm.StreamCalls);
            Assert.Equal(RoutedLlm.GeneralAnswer, result.finalAnswer);
            Assert.Equal(new[] { "Bonjour. ", "Le flux de contrôle est terminé." }, deltas);
            Assert.Null(result.sourcesPayload);
            Assert.Null(agent.LastAdvancedAnalysisHandoff);
            Assert.Empty(handler.Requests);
            Assert.Empty(memory.LastToolNames);
            Assert.DoesNotContain(memory.Execution.LastRagTraceEvents,
                trace => trace.Contains("source_backed_pipeline.active.start", StringComparison.Ordinal));

            var expectedSteps = new[] { "exact_item_fallback", "documentary_probe_fallback" };
            var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
            var skippedProbes = record.Events
                .Where(probe => probe.Operation == "agent.source_backed_fallback.skipped").ToArray();
            Assert.Equal(expectedSteps, skippedProbes.Select(probe => probe.Identifiers["source_step"]));
            Assert.All(skippedProbes, probe =>
            {
                Assert.Equal("router_general_no_tools", probe.Reason);
                Assert.Single(probe.Identifiers);
                Assert.Empty(probe.Measurements);
            });
            var skippedTraces = memory.Execution.LastRagTraceEvents
                .Where(trace => trace.Contains("event=source_backed_fallback.skipped ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(expectedSteps.Length, skippedTraces.Length);
            for (var index = 0; index < expectedSteps.Length; index++)
                Assert.Contains($"source_step=\"{expectedSteps[index]}\"", skippedTraces[index], StringComparison.Ordinal);
            var publicCorrectionProbes = JsonSerializer.Serialize(skippedProbes) + string.Join("\n", skippedTraces);
            Assert.DoesNotContain(question, publicCorrectionProbes, StringComparison.Ordinal);
            Assert.DoesNotContain("query=", publicCorrectionProbes, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", previousContentMode);
            output.WriteLine("Native router calls={0}; stream calls={1}; HTTP requests={2}",
                llm.NativeCalls, llm.StreamCalls, string.Join(",", handler.Requests));
            output.WriteLine("Trace={0}", string.Join("\n", memory.Execution.LastRagTraceEvents));
        }
    }

    [Fact]
    public async Task Accepted_documentary_route_still_reaches_its_real_retrieval_branch()
    {
        using var stopAfterSearch = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new RecordingApiHandler(stopAfterSearch);
        using var http = new HttpClient(handler);
        var api = new ApiClient(http);
        api.Configure("http://127.0.0.1:1", "synthetic-documentary-key", "synthetic-user");
        var llm = new RoutedLlm(documentary: true);
        var agent = new ToolAgentOrchestrator(api, llm, WarmMemory(), new AppSettings { ActiveMode = "strict" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync(
            [], "Quelle est la valeur du code ORION ?", stopAfterSearch.Token));

        Assert.True(llm.NativeCalls >= 2);
        Assert.Contains(handler.Requests, request => request.Contains("/rag/", StringComparison.Ordinal));
        Assert.Equal(0, llm.StreamCalls);
        Assert.Null(agent.LastAdvancedAnalysisHandoff);
        output.WriteLine("Documentary route reached retrieval: {0}", string.Join(",", handler.Requests));
    }

    private static ToolMemory WarmMemory() => new()
    {
        CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { LoadedAtUtc = DateTimeOffset.UtcNow }
    };

    private sealed class RecordingApiHandler(CancellationTokenSource? stopAfterSearch = null) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (stopAfterSearch is not null && path.StartsWith("/rag/", StringComparison.Ordinal))
            {
                // The assertion concerns routing/admission, not the semantic
                // quality of a generated answer. Stop at the real HTTP boundary.
                stopAfterSearch.Cancel();
                throw new OperationCanceledException(stopAfterSearch.Token);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RoutedLlm(bool documentary) : ILlmClient, ISourceBackedAgentLlmClient
    {
        internal const string GeneralAnswer = "Bonjour. Le flux de contrôle est terminé.";
        internal int NativeCalls { get; private set; }
        internal int StreamCalls { get; private set; }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens,
            CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            string name;
            JsonElement args;
            if (NativeCalls <= 2)
            {
                name = documentary ? "submit_source_backed_route" : "submit_operational_route";
                args = NativeCalls == 1 ? JsonSerializer.SerializeToElement(new { })
                    : documentary ? JsonSerializer.SerializeToElement(new
                    {
                        tool = "search", intent = "answer", query = "ORION",
                        answerUnitType = "valeur du code", answerUnitMode = "content_claim", selectionPolicy = "single_item",
                        useFocusedDocument = false, questionFocus = "content", namedReferenceKind = "none",
                        document = (string?)null, pool = 2, count = 1
                    }) : JsonSerializer.SerializeToElement(new { intent = "chat.general", toolName = "none", toolArgs = new { } });
            }
            else if (documentary)
            {
                name = Assert.Single(tools, tool => tool.Name == "rag_search").Name;
                args = JsonSerializer.SerializeToElement(new { query = "ORION", topK = 1 });
            }
            else
            {
                throw new InvalidOperationException("Unexpected documentary LLM call after an accepted general route.");
            }
            Assert.Contains(tools, tool => tool.Name == name);
            return Task.FromResult(new SourceBackedAgentCompletion("",
                [new SourceBackedAgentToolCall("route-" + NativeCalls, name, args)], "tool_calls"));
        }

        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => throw new InvalidOperationException("This route must reach the general streaming writer, not a completion fallback.");

        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages,
            bool forceJson, Action<string> onDelta, CancellationToken ct)
        {
            StreamCalls++;
            ct.ThrowIfCancellationRequested();
            onDelta("Bonjour. ");
            onDelta("Le flux de contrôle est terminé.");
            return Task.CompletedTask;
        }
    }
}
