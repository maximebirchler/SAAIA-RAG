using Dapper;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Heartbeat_database_failure_cancels_and_observes_provider_before_worker_returns()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its isolated PostgreSQL fixture.");
        var seed = await database.SeedEvidenceAsync("heartbeat-failure", "Inspection is required.");
        var jobId = await database.InsertJobAsync(seed, BuildHandoff(seed, "heartbeat-evidence"));
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("""
                CREATE TABLE lease_failure_control(enabled boolean NOT NULL);
                INSERT INTO lease_failure_control VALUES(false);
                CREATE FUNCTION reject_lease_renewal_for_test() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF OLD.status='running' AND NEW.status='running'
                    AND NEW.revision=OLD.revision AND NEW.lease_expires_at>OLD.lease_expires_at
                    AND (SELECT enabled FROM lease_failure_control) THEN
                    RAISE EXCEPTION 'controlled lease renewal failure' USING ERRCODE='P0001';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER reject_lease_renewal_for_test BEFORE UPDATE ON advanced_analysis_jobs
                  FOR EACH ROW EXECUTE FUNCTION reject_lease_renewal_for_test();
                """);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasedForCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        var provider = new DelegateProvider(async (request, token) =>
        {
            await using (var connection = await database.DataSource.OpenConnectionAsync(token))
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE lease_failure_control SET enabled=true;", cancellationToken: token));
            started.TrySetResult();
            try
            {
                await releasedForCleanup.Task.WaitAsync(token);
                return SuccessfulResult("heartbeat-evidence");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled = true;
                throw;
            }
            finally { exited.TrySetResult(); }
        });
        var worker = CreateWorker(database.DataSource, provider, heartbeatMilliseconds: 25);
        var processing = worker.ProcessOnceAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(canceled, "A failed heartbeat must cancel the in-flight provider before returning.");
            Assert.True(exited.Task.IsCompletedSuccessfully,
                "The worker must observe provider completion, not leave an unowned task running.");
            var row = await database.ReadJobAsync(jobId);
            Assert.Equal("failed", row.Status);
            Assert.Null(row.ResultJson);
            Assert.Single(provider.Requests);
        }
        finally
        {
            releasedForCleanup.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
