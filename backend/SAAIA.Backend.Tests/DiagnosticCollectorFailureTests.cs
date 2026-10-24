using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

[Collection("RetrievalDiagnosticIsolation")]
public sealed class DiagnosticCollectorFailureTests
{
    [Fact]
    public void A_failing_stop_observer_cannot_leave_a_stopped_operation_as_the_parent_of_the_next_one()
    {
        using var parent = new Activity("test.live_parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => throw new IOException("test stop observer failure")
        };
        ActivitySource.AddActivityListener(listener);
        using (var operation = BackendDiagnostics.Start("test.stop_observer")) operation.Complete();
        Assert.Same(parent, Activity.Current);
        using (var next = BackendDiagnostics.Start("test.next_after_stop_failure"))
        {
            Assert.Equal(parent.SpanId, Activity.Current?.ParentSpanId);
            next.Complete();
        }
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData("sampler")]
    [InlineData("started")]
    [InlineData("stopped")]
    public async Task Retrieval_collectors_cannot_prevent_or_replace_a_real_phase_result(string failingCallback)
    {
        var previous = Activity.Current;
        var failures = BackendDiagnostics.CollectionFailures;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => failingCallback == "sampler"
                ? throw new IOException("test sampler failure") : ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { if (failingCallback == "started") throw new IOException("test start failure"); },
            ActivityStopped = _ => { if (failingCallback == "stopped") throw new IOException("test stop failure"); }
        };
        ActivitySource.AddActivityListener(listener);
        var executed = false;
        var result = await new RetrievalPhaseDiagnostics(null).MeasureAsync("test.retrieval_collector", "test",
            () => { executed = true; return Task.FromResult(42); });
        Assert.True(executed);
        Assert.Equal(42, result.Result);
        Assert.True(BackendDiagnostics.CollectionFailures > failures);
        Assert.Same(previous, Activity.Current);
    }

    [Fact]
    public void A_failing_retrieval_metric_listener_is_visible_and_preserves_the_business_exception()
    {
        var failures = BackendDiagnostics.CollectionFailures;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == RetrievalTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new IOException("test metric failure"));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new IOException("test metric failure"));
        listener.Start();
        Assert.Null(Record.Exception(() => RetrievalTelemetry.RecordFailedSearch("focused", false, false, 1,
            new InvalidOperationException("original business failure"))));
        Assert.True(BackendDiagnostics.CollectionFailures > failures);
    }

    [Fact]
    public void A_sampling_listener_failure_is_visible_and_does_not_prevent_the_operation()
    {
        using var parent = new Activity("test.parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        var failures = BackendDiagnostics.CollectionFailures;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => throw new IOException("broken sampler")
        };
        ActivitySource.AddActivityListener(listener);
        var logger = new DiagnosticRecordingLogger<DiagnosticCollectorFailureTests>();
        using (var operation = BackendDiagnostics.Start("test.sampler_failure", logger)) operation.Complete();
        Assert.Equal("success", Assert.Single(logger.Events).Values["Outcome"]);
        Assert.Equal(parent.TraceId.ToHexString(), logger.Events[0].Values["TraceId"]);
        Assert.True(BackendDiagnostics.CollectionFailures > failures);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public void A_current_activity_callback_failure_is_isolated_from_business_execution()
    {
        var previous = Activity.Current;
        var logger = new DiagnosticRecordingLogger<DiagnosticCollectorFailureTests>();
        void OnChanged(object? sender, ActivityChangedEventArgs args)
        {
            if (args.Current?.OperationName == "test.current_callback_failure") throw new IOException("broken activity observer");
        }
        Activity.CurrentChanged += OnChanged;
        try
        {
            var error = Record.Exception(() =>
            {
                using var operation = BackendDiagnostics.Start("test.current_callback_failure", logger);
                operation.Complete();
            });
            Assert.Null(error);
            Assert.Equal("success", Assert.Single(logger.Events).Values["Outcome"]);
            Assert.Same(previous, Activity.Current);
        }
        finally { Activity.CurrentChanged -= OnChanged; Activity.Current = previous; }
    }

    [Fact]
    public void A_broken_sink_cannot_replace_the_business_result_or_leave_its_activity_current()
    {
        var parent = Activity.Current;
        var failures = BackendDiagnostics.CollectionFailures;
        var operation = BackendDiagnostics.Start("test.broken_sink", new ThrowingLogger());
        operation.Complete();
        var error = Record.Exception(operation.Dispose);
        Assert.Null(error);
        Assert.True(BackendDiagnostics.CollectionFailures > failures);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public void Oversize_metadata_and_field_overflow_are_bounded_and_declared()
    {
        var logger = new DiagnosticRecordingLogger<DiagnosticCollectorFailureTests>();
        using (var operation = BackendDiagnostics.Start("test.bounds", logger))
        {
            operation.Set("large_identifier", new string('x', 2_000));
            operation.Set("ids", Enumerable.Range(0, 80).ToArray());
            for (var index = 0; index < 110; index++) operation.Set("count_" + index, index);
            operation.Complete();
        }
        using var fields = JsonDocument.Parse((string)Assert.Single(logger.Events).Values["Measurements"]!);
        Assert.Equal(512, fields.RootElement.GetProperty("large_identifier").GetString()!.Length);
        Assert.Equal(64, fields.RootElement.GetProperty("ids").GetArrayLength());
        Assert.Equal(16, fields.RootElement.GetProperty("diagnostic_dropped_measurements").GetInt32());
        Assert.Equal(2, fields.RootElement.GetProperty("diagnostic_truncated_values").GetInt32());
    }

    [Fact]
    public void Concurrent_disposal_emits_one_terminal_record_and_late_updates_are_ignored()
    {
        var logger = new DiagnosticRecordingLogger<DiagnosticCollectorFailureTests>();
        var operation = BackendDiagnostics.Start("test.concurrent_dispose", logger);
        operation.Complete();
        Parallel.For(0, 32, _ => operation.Dispose());
        operation.Complete("failed", "too_late");
        operation.Set("late_field", 99);
        operation.Dispose();
        var record = Assert.Single(logger.Events);
        Assert.Equal("success", record.Values["Outcome"]);
        Assert.DoesNotContain("late_field", (string)record.Values["Measurements"]!);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("test sink is unavailable");
    }
}
