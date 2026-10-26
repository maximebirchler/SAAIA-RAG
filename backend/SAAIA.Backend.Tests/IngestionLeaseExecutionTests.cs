using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionLeaseExecutionTests
{
    [Fact]
    public async Task Renewal_failure_cancels_and_observes_the_operation_without_exposing_the_database_message()
    {
        var renewals = 0;
        var canceled = false;
        var observed = false;
        var error = await Assert.ThrowsAsync<IngestionLeaseLostException>(() => IngestionLeaseExecution.RunAsync(
            _ => Interlocked.Increment(ref renewals) == 1 ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("private database response")),
            async token =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch (OperationCanceledException) { canceled = true; throw; }
                finally { observed = true; }
                return true;
            }, TimeSpan.FromMilliseconds(5), CancellationToken.None));
        Assert.True(canceled);
        Assert.True(observed);
        Assert.Equal("ingestion_heartbeat_failed", error.Message);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task A_renewal_that_fails_while_success_is_unwinding_cannot_be_returned_as_success()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewals = 0;
        var operationReturned = false;
        var renewalObserved = false;
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => IngestionLeaseExecution.RunAsync(
            async token =>
            {
                if (Interlocked.Increment(ref renewals) == 1) return;
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { /* Completion begins while renewal is in flight. */ }
                finally { renewalObserved = true; }
                throw new InvalidOperationException("late renewal failure");
            },
            async _ => { await started.Task; operationReturned = true; return true; },
            TimeSpan.FromMilliseconds(5), CancellationToken.None));
        Assert.True(operationReturned);
        Assert.True(renewalObserved);
    }

    [Fact]
    public async Task External_cancellation_is_respected_even_if_the_operation_ignores_its_token()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IngestionLeaseExecution.RunAsync(
            _ => Task.CompletedTask,
            _ => { cancellation.Cancel(); return Task.FromResult(true); },
            TimeSpan.FromSeconds(1), cancellation.Token));
    }
}
