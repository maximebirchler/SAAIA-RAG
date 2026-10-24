using System.Diagnostics;

namespace SAAIA.Client.Core.Execution;

public sealed record AgentIterationStep<TState, TIteration, TResult>(string Name,
    Func<TState, TIteration, CancellationToken, Task<AgentIterationOutcome<TResult>>> Execute) where TResult : class;

public sealed record AgentIterationObservation(string Phase, string Outcome, string Reason, long ElapsedMilliseconds);

public sealed class AgentIterationObserverHealth
{
    private long _observerFailures;
    public long ObserverFailures => Interlocked.Read(ref _observerFailures);
    internal void RecordFailure() => Interlocked.Increment(ref _observerFailures);
}

/// <summary>Runs ordered phases once and stops at the first explicit control outcome. A phase owns cancellation policy.</summary>
public static class AgentIterationPipeline
{
    public static async Task<AgentIterationOutcome<TResult>> RunAsync<TState, TIteration, TResult>(
        TState state, TIteration iteration, IReadOnlyList<AgentIterationStep<TState, TIteration, TResult>> steps,
        CancellationToken ct, AgentIterationObserverHealth observerHealth,
        Action<AgentIterationObservation>? observe = null) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(observerHealth);
        foreach (var step in steps)
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = "failed";
            var reason = "phase_failed";
            try
            {
                var result = await step.Execute(state, iteration, ct).ConfigureAwait(false);
                outcome = "success";
                reason = result.Disposition.ToString().ToLowerInvariant();
                if (result.Disposition != AgentIterationDisposition.Advance) return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = "cancelled";
                reason = "cancellation_requested";
                throw;
            }
            catch (Exception exception)
            {
                reason = exception.GetType().Name;
                throw;
            }
            finally
            {
                // Diagnostic observers cannot replace an answer or an exception.
                try { observe?.Invoke(new(step.Name, outcome, reason, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)); }
                catch { observerHealth.RecordFailure(); }
            }
        }
        return AgentIterationOutcome<TResult>.Advance;
    }
}
