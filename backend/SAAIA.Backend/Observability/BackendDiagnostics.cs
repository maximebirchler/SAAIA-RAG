using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SAAIA.Backend;

/// <summary>
/// Content-free operation diagnostics. Local structured logs remain available when OTLP export is disabled.
/// IDs belong to logs/spans, never to metric dimensions. Callers own outcome and safe numeric/identifier fields.
/// </summary>
internal static class BackendDiagnostics
{
    internal const string ActivitySourceName = "SAAIA.Backend.Operations";
    internal const string MeterName = "SAAIA.Backend.Operations";
    internal static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("saaia.operation.duration", "ms");
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("saaia.operation.completed");
    private static long _collectionFailures;
    internal static long CollectionFailures => Interlocked.Read(ref _collectionFailures);
    internal static void RecordCollectionFailure() => Interlocked.Increment(ref _collectionFailures);

    internal static Operation Start(string operation, ILogger? logger = null, ActivityContext? parent = null)
        => new(operation, logger ?? NullLogger.Instance, parent);

    internal sealed class Operation : IDisposable
    {
        private readonly string _operation;
        private readonly ILogger _logger;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private readonly Activity? _previousActivity;
        private readonly string _traceId;
        private readonly string _spanId;
        private readonly Dictionary<string, object?> _measurements = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private string _outcome = "incomplete";
        private string? _reason;
        private int _disposed;
        private int _droppedMeasurements;
        private int _truncatedValues;

        internal Operation(string operation, ILogger logger, ActivityContext? parent)
        {
            _operation = operation;
            _logger = logger;
            var previous = Activity.Current;
            _previousActivity = previous;
            Activity? activity = null;
            try
            {
                activity = parent.HasValue
                    ? Source.StartActivity(operation, ActivityKind.Internal, parent.Value)
                    : Source.StartActivity(operation, ActivityKind.Internal);
            }
            catch (Exception) { RestoreFailedStart(); }
            if (activity is null)
            {
                try
                {
                    activity = new Activity(operation).SetIdFormat(ActivityIdFormat.W3C);
                    if (parent.HasValue && parent.Value != default)
                        activity.SetParentId(parent.Value.TraceId, parent.Value.SpanId, parent.Value.TraceFlags);
                    activity.Start();
                }
                catch (Exception) { RestoreFailedStart(); activity = null; }
            }
            _activity = activity;
            var inheritedTrace = parent?.TraceId ?? previous?.TraceId ?? default;
            _traceId = (activity?.TraceId is { } trace && trace != default ? trace
                : inheritedTrace != default ? inheritedTrace : ActivityTraceId.CreateRandom()).ToHexString();
            _spanId = (activity?.SpanId is { } span && span != default ? span : ActivitySpanId.CreateRandom()).ToHexString();
            _activity?.SetTag("saaia.schema_version", "operation.v1");
            if (_activity is null) _measurements["diagnostic_activity_unavailable"] = true;

            void RestoreFailedStart()
            {
                Interlocked.Increment(ref _collectionFailures);
                var current = Activity.Current;
                if (!ReferenceEquals(current, previous) && current?.OperationName == operation)
                {
                    try { current.Stop(); }
                    catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                }
                if (!ReferenceEquals(Activity.Current, previous))
                {
                    try { Activity.Current = previous; }
                    catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                }
            }
        }

        internal string TraceId => _traceId;
        internal string SpanId => _spanId;
        internal double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        internal void Set(string name, object? value)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 96)
            { Interlocked.Increment(ref _collectionFailures); return; }
            lock (_gate)
            {
                if (_disposed != 0) return;
                if (_measurements.Count >= 96 && !_measurements.ContainsKey(name))
                { _droppedMeasurements++; return; }
                if (value is string text && text.Length > 512)
                { value = text[..512]; _truncatedValues++; }
                else if (value is Array array && array.Rank == 1 && array.Length > 64)
                { value = array.Cast<object?>().Take(64).ToArray(); _truncatedValues++; }
                _measurements[name] = value;
                _activity?.SetTag("saaia." + name, value);
            }
        }

        internal void Complete(string outcome = "success", string? reason = null)
        {
            lock (_gate)
            {
                if (_disposed != 0) return;
                _outcome = outcome;
                _reason = reason is { Length: > 256 } ? reason[..256] : reason;
                _activity?.SetStatus(outcome is "failed" or "incomplete" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            }
        }

        internal void Fail(Exception exception, string? reason = null)
        {
            Complete(exception is OperationCanceledException ? "cancelled" : "failed", reason ?? exception.GetType().Name);
            Set("exception_type", exception.GetType().Name);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var elapsed = ElapsedMilliseconds;
            Dictionary<string, object?> snapshot;
            string outcome;
            string? reason;
            lock (_gate)
            {
                outcome = _outcome; reason = _reason;
                snapshot = new(_measurements, StringComparer.Ordinal)
                {
                    ["diagnostic_dropped_measurements"] = _droppedMeasurements,
                    ["diagnostic_truncated_values"] = _truncatedValues
                };
            }
            var metricTags = new TagList { { "operation", _operation }, { "outcome", outcome } };
            try
            {
                _activity?.SetTag("saaia.outcome", outcome);
                _activity?.SetTag("saaia.reason_code", reason);
                try { Duration.Record(elapsed, metricTags); }
                catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                try { Completed.Add(1, metricTags); }
                catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                _logger.LogInformation(
                    "Operation completed schema={DiagnosticSchema} operation={Operation} outcome={Outcome} reason={ReasonCode} duration_ms={DurationMs} trace_id={TraceId} span_id={SpanId} measurements={Measurements}",
                    "operation.v1", _operation, outcome, reason, elapsed, TraceId, SpanId,
                    JsonSerializer.Serialize(snapshot));
            }
            catch (Exception)
            {
                // A failed sink/serializer must not replace the business operation's outcome.
                Interlocked.Increment(ref _collectionFailures);
            }
            finally
            {
                try { _activity?.Dispose(); }
                catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                finally
                {
                    if (ReferenceEquals(Activity.Current, _activity) || Activity.Current?.IsStopped == true)
                    {
                        var previous = _previousActivity;
                        while (previous?.IsStopped == true) previous = previous.Parent;
                        try { Activity.Current = previous; }
                        catch (Exception) { Interlocked.Increment(ref _collectionFailures); }
                    }
                }
            }
        }
    }
}
