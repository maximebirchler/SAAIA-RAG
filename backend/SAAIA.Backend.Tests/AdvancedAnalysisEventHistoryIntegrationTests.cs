using Dapper;
using Npgsql;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class AdvancedAnalysisEventHistoryIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Job_state_history_preserves_retry_recovery_lease_correlation_and_previous_errors()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var jobId = await SeedJobAsync(database);
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var first = await store.TryClaimAsync("worker-a", "customer-server", "model", 120, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", first.ParentTraceparent);
        Assert.Equal("turn-17", first.TurnId);
        Assert.True(first.QueueAgeMilliseconds >= 0);
        Assert.True(await store.TryScheduleRetryAsync(jobId, "worker-a", "provider_unavailable", 0, 3, CancellationToken.None));
        Assert.NotNull(await store.TryClaimAsync("worker-b", "customer-server", "model", 120, CancellationToken.None));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var beforeHeartbeat = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM advanced_analysis_job_events WHERE job_id=@job", new { job = jobId });
        Assert.Equal(AdvancedAnalysisLeaseState.Active, await store.RenewAndReadStateAsync(jobId, "worker-b", 120, CancellationToken.None));
        Assert.Equal(beforeHeartbeat, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM advanced_analysis_job_events WHERE job_id=@job", new { job = jobId }));
        await connection.ExecuteAsync("UPDATE advanced_analysis_jobs SET lease_expires_at=now()-interval '1 second' WHERE job_id=@job", new { job = jobId });
        Assert.Equal(1, await store.RecoverExpiredAsync(3, 0, CancellationToken.None));
        Assert.NotNull(await store.TryClaimAsync("worker-c", "customer-server", "model", 120, CancellationToken.None));
        Assert.True(await store.TryMarkFailedAsync(jobId, "worker-c", "test_terminal_failure", CancellationToken.None));
        var events = (await connection.QueryAsync<EventRow>("SELECT event_name AS \"Name\", previous_error_code AS \"PreviousError\", error_code AS \"Error\", turn_id AS \"Turn\", queue_wait_milliseconds AS \"QueueMilliseconds\" FROM advanced_analysis_job_events WHERE job_id=@job ORDER BY event_id", new { job = jobId })).ToArray();
        Assert.Equal(new[] { "queued", "lease_acquired", "retry_scheduled", "lease_acquired", "lease_recovered", "lease_acquired", "failed" }, events.Select(item => item.Name));
        Assert.Equal("provider_unavailable", events[3].PreviousError);
        Assert.Equal("lease_expired_retry", events[4].Error);
        Assert.All(events, item => Assert.Equal("turn-17", item.Turn));
        Assert.All(events.Where(item => item.Name == "lease_acquired"), item => Assert.True(item.QueueMilliseconds >= 0));
        var mutation = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("UPDATE advanced_analysis_job_events SET error_code='rewritten' WHERE job_id=@job", new { job = jobId }));
        Assert.Equal("P0001", mutation.SqlState);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Job_retention_removes_its_content_free_event_history_with_the_job()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var jobId = await SeedJobAsync(database);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE advanced_analysis_jobs SET status='failed',created_at=now()-interval '3 days',expires_at=now()-interval '1 day' WHERE job_id=@job", new { job = jobId });
        Assert.True(await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM advanced_analysis_job_events WHERE job_id=@job", new { job = jobId }) > 0);
        Assert.Equal(1, await new AdvancedAnalysisJobStore(database.DataSource).PurgeExpiredAsync(100, CancellationToken.None));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM advanced_analysis_job_events WHERE job_id=@job", new { job = jobId }));
    }

    [ProfileTheory("IntegrationPg")]
    [InlineData(null, false)]
    [InlineData("not-a-session-guid", true)]
    [InlineData("matching_compact_guid", false)]
    public async Task Queued_history_uses_the_real_session_even_when_client_header_is_missing_or_wrong(string? claimedHeader, bool expectedMismatch)
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var jobId = await SeedJobAsync(database, claimedHeader);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var session = await connection.ExecuteScalarAsync<Guid>("SELECT session_id FROM advanced_analysis_jobs WHERE job_id=@jobId", new { jobId });
        var events = (await connection.QueryAsync<CorrelationRow>(
            "SELECT session_id AS \"Session\", client_session_id AS \"ClientSession\", client_session_id_mismatch AS \"Mismatch\" FROM advanced_analysis_job_events WHERE job_id=@jobId", new { jobId })).ToArray();
        var created = Assert.Single(events);
        Assert.Equal(session.ToString("D"), created.Session);
        Assert.Equal(claimedHeader == "matching_compact_guid" ? session.ToString("N") : claimedHeader, created.ClientSession);
        Assert.Equal(expectedMismatch, created.Mismatch);
    }

    private static async Task<Guid> SeedJobAsync(LocalPostgresDiagnosticFixture database, string? clientSessionHeader = "session-17")
    {
        var tenant = Guid.NewGuid(); var session = Guid.NewGuid(); var job = Guid.NewGuid();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name,is_active) VALUES(@tenant,'synthetic-diagnostic-fixture',true);
            INSERT INTO chat_sessions(tenant_id,user_id,session_id,title,created_at,updated_at)
              VALUES(@tenant,'synthetic-user',@session,'fixture',now(),now());
            INSERT INTO advanced_analysis_jobs(job_id,tenant_id,user_id,session_id,handoff_id,schema_version,handoff,expires_at,
              parent_traceparent,diagnostic_request_id,diagnostic_turn_id,diagnostic_session_id)
              VALUES(@job,@tenant,'synthetic-user',@session,@handoff,'saaia.advanced-analysis-handoff.v1','{}'::jsonb,
                now()+interval '1 day','00-0123456789abcdef0123456789abcdef-0123456789abcdef-01','request-17','turn-17',@client_session);
            """, new { tenant, session, job, handoff = Guid.NewGuid(),
                client_session = clientSessionHeader == "matching_compact_guid" ? session.ToString("N") : clientSessionHeader });
        return job;
    }

    private sealed class EventRow
    {
        public string Name { get; init; } = string.Empty;
        public string? PreviousError { get; init; }
        public string? Error { get; init; }
        public string? Turn { get; init; }
        public double? QueueMilliseconds { get; init; }
    }

    private sealed class CorrelationRow
    {
        public string Session { get; init; } = string.Empty;
        public string? ClientSession { get; init; }
        public bool Mismatch { get; init; }
    }
}
