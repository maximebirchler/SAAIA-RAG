using System.Diagnostics;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class BackendDiagnosticsLoggerFlowTests
{
    [Fact]
    public async Task A_child_repository_probe_keeps_the_parent_logger_without_an_otel_exporter()
    {
        var logger = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        using (var parent = BackendDiagnostics.Start("test.publication", logger))
        {
            await Task.Yield();
            using (var bridge = new Activity("third_party.database"))
            {
                bridge.Start();
                using var child = BackendDiagnostics.Start("test.repository");
                child.Set("line_count", 107);
                child.Complete();
            }
            parent.Complete();
        }
        Assert.Equal(2, logger.Events.Count);
        Assert.Single(logger.Events, item => Equals(item.Values["Operation"], "test.repository"));
    }

    [Fact]
    public void An_explicit_child_logger_overrides_the_inherited_logger()
    {
        var outer = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        var inner = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        using (var parent = BackendDiagnostics.Start("test.outer", outer))
        {
            using (var child = BackendDiagnostics.Start("test.inner", inner)) child.Complete();
            parent.Complete();
        }
        Assert.Equal("test.outer", Assert.Single(outer.Events).Values["Operation"]);
        Assert.Equal("test.inner", Assert.Single(inner.Events).Values["Operation"]);
    }

    [Fact]
    public void An_explicit_unrelated_trace_does_not_inherit_the_ambient_logger()
    {
        var logger = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        using (var parent = BackendDiagnostics.Start("test.ambient", logger))
        {
            var unrelated = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            using (var child = BackendDiagnostics.Start("test.unrelated", parent: unrelated)) child.Complete();
            parent.Complete();
        }
        Assert.Equal("test.ambient", Assert.Single(logger.Events).Values["Operation"]);
    }

    [Fact]
    public async Task Parallel_publications_do_not_cross_their_repository_loggers()
    {
        var first = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        var second = new DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests>();
        await Task.WhenAll(Run(first, "first"), Run(second, "second"));
        Assert.Equal(2, first.Events.Count);
        Assert.Equal(2, second.Events.Count);
        Assert.All(first.Events, entry => Assert.StartsWith("first.", (string)entry.Values["Operation"]!));
        Assert.All(second.Events, entry => Assert.StartsWith("second.", (string)entry.Values["Operation"]!));

        static async Task Run(DiagnosticRecordingLogger<BackendDiagnosticsLoggerFlowTests> logger, string prefix)
        {
            using var parent = BackendDiagnostics.Start(prefix + ".publication", logger);
            await Task.Yield();
            using (var child = BackendDiagnostics.Start(prefix + ".repository")) child.Complete();
            parent.Complete();
        }
    }
}
