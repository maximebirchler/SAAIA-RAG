using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task Throwing_trace_sink_preserves_the_real_run_result_and_reports_collection_failure()
    {
        var options = IsolatedTraceRunOptions();
        var intake = IsolatedRunIntake("trace collector failure");
        SourceBackedPipelineResult baseline;
        string baselineTraceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            baselineTraceId = ClientDiagnostics.Journal.CurrentTraceId!;
            baseline = await new SourceBackedAgentV2Runner(new ScriptedAgentLlm(),
                new ScriptedToolExecutor(SearchResultAtPage(7, "source content")), options)
                .RunAsync(intake, CancellationToken.None);
            turn.Complete();
        }
        var baselineRecord = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == baselineTraceId);
        var baselineHealth = Assert.Single(baselineRecord.Events, item => item.Operation == "agent.collector_health");
        Assert.Equal("no_observer_failures", baselineHealth.Reason);
        Assert.Equal(0, baselineHealth.Measurements["trace_sink_configured"]);
        Assert.Equal(0, baselineHealth.Measurements["trace_sink_failures"]);
        SourceBackedPipelineResult result;
        string traceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            traceId = ClientDiagnostics.Journal.CurrentTraceId!;
            result = await new SourceBackedAgentV2Runner(new ScriptedAgentLlm(),
                new ScriptedToolExecutor(SearchResultAtPage(7, "source content")), options,
                traceSink: _ => throw new IOException("private observer failure"))
                .RunAsync(intake, CancellationToken.None);
            turn.Complete();
        }
        Assert.Equal(baseline.JudgeDecision.Decision, result.JudgeDecision.Decision);
        Assert.Equal(baseline.IsSourceVerified, result.IsSourceVerified);
        Assert.Equal(baseline.EvidenceBundle.Items.Select(item => (item.EvidenceId, item.DocId, item.PageStart, item.Excerpt)),
            result.EvidenceBundle.Items.Select(item => (item.EvidenceId, item.DocId, item.PageStart, item.Excerpt)));
        Assert.Equal(baseline.TraceEvents.Select(trace => trace.EventName), result.TraceEvents.Select(trace => trace.EventName));
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
        var health = Assert.Single(record.Events, item => item.Operation == "agent.collector_health");
        Assert.Equal("degraded", health.Outcome);
        Assert.Equal(1, health.Measurements["trace_sink_configured"]);
        Assert.Equal(result.TraceEvents.Count, health.Measurements["trace_sink_failures"]);
        Assert.DoesNotContain("private observer", System.Text.Json.JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task Throwing_trace_sink_cannot_replace_the_original_tool_exception()
    {
        var expected = new InvalidOperationException("original tool exception");
        string traceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            traceId = ClientDiagnostics.Journal.CurrentTraceId!;
            var runner = new SourceBackedAgentV2Runner(new ScriptedAgentLlm(),
                new FailingTraceRunToolExecutor(expected), IsolatedTraceRunOptions(),
                traceSink: _ => throw new IOException("observer failure"));
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RunAsync(IsolatedRunIntake("failed tool"), CancellationToken.None));
            Assert.Same(expected, actual);
            turn.Complete("failed", "tool_failed");
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
        var health = Assert.Single(record.Events, item => item.Operation == "agent.collector_health");
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("degraded", health.Outcome);
        Assert.True(health.Measurements["trace_sink_failures"] > 0);
    }

    private static SourceBackedAgentV2Options IsolatedTraceRunOptions() => Options() with
    {
        MaximumTurns = 1,
        SeparateActionAndWriter = true,
        RequireEvidenceSelectionBeforeWriter = true,
        StructuredSemanticPlanningEnabled = true,
        SemanticCandidateStrategyEnabled = false,
        SemanticCandidateDefinitionEnabled = false,
        SemanticCandidateAuditEnabled = false
    };

    private sealed class FailingTraceRunToolExecutor(Exception failure) : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName,
            System.Text.Json.JsonElement arguments, CancellationToken ct) => Task.FromException<ToolResults>(failure);
    }
}
