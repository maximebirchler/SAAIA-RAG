using System.Diagnostics;

namespace SAAIA.Backend.Retrieval;

/// <summary>One timing/outcome per executed channel, on success, failure and cancellation.</summary>
internal sealed class RetrievalPhaseDiagnostics(ILogger? logger)
{
    internal Task<(T Result, long DurationMs)> MeasureOptionalAsync<T>(string phaseName, string? retriever,
        bool enabled, string skipReason, Func<Task<T>> action, Func<T> whenSkipped,
        Func<T, int>? getReturnedCount = null)
    {
        if (enabled) return MeasureAsync(phaseName, retriever, action, getReturnedCount);
        using var activityScope = RetrievalTelemetry.StartPhaseScope(phaseName);
        var activity = activityScope.Activity;
        using var operation = BackendDiagnostics.Start(phaseName, logger);
        activity?.SetTag("saaia.phase.outcome", "skipped");
        activity?.SetTag("saaia.phase.reason", skipReason);
        operation.Set("retriever", retriever);
        operation.Set("executed", false);
        operation.Complete("skipped", skipReason);
        RetrievalTelemetry.CompletePhase(activity, 0, 0, retriever);
        return Task.FromResult((whenSkipped(), 0L));
    }

    internal async Task<(T Result, long DurationMs)> MeasureAsync<T>(string phaseName, string? retriever,
        Func<Task<T>> action, Func<T, int>? getReturnedCount = null)
    {
        using var activityScope = RetrievalTelemetry.StartPhaseScope(phaseName);
        var activity = activityScope.Activity;
        using var operation = BackendDiagnostics.Start(phaseName, logger);
        var started = Stopwatch.GetTimestamp();
        operation.Set("retriever", retriever);
        operation.Set("executed", true);
        try
        {
            var result = await action().ConfigureAwait(false);
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var returned = getReturnedCount?.Invoke(result) ?? 0;
            RetrievalTelemetry.CompletePhase(activity, returned, elapsed, retriever);
            operation.Set("returned", returned);
            operation.Complete();
            activity?.SetTag("saaia.phase.outcome", "success");
            return (result, elapsed);
        }
        catch (Exception error)
        {
            RetrievalTelemetry.MarkPhaseError(activity, error);
            operation.Fail(error);
            activity?.SetTag("saaia.phase.outcome", error is OperationCanceledException ? "cancelled" : "failed");
            throw;
        }
    }
}

internal sealed class RetrievalDegradationTracker
{
    private readonly HashSet<string> _retrievers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    internal IReadOnlyCollection<string> Retrievers => _retrievers;
    internal IReadOnlyDictionary<string, string> Errors => _errors;

    internal void Mark(string retriever, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(retriever)) return;
        var normalized = retriever.Trim().ToLowerInvariant();
        lock (_gate)
        {
            _retrievers.Add(normalized);
            if (!string.IsNullOrWhiteSpace(reason)) _errors[normalized] = reason;
        }
    }
}
