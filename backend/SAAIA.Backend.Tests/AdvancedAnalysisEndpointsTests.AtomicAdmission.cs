using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisEndpointsTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_mutation_and_audit_roll_back_together_when_audit_is_rejected(bool cancel)
    {
        await using var database = await PostgresIntegrationDb.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its isolated PostgreSQL fixture.");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var tenant = Guid.NewGuid();
        var request = BuildRequest(sessionId: Guid.NewGuid(), handoffId: Guid.NewGuid());
        await SeedSessionAsync(dataSource, tenant, request.SessionId, request.UserId);
        var license = Options.Create(new LicenseOptions { AdvancedAnalysisEnabled = true });
        var options = Options.Create(new AdvancedAnalysisOptions());
        Guid? jobId = null;
        if (cancel)
        {
            var context = BuildContext(tenant);
            var result = await AdvancedAnalysisEndpoints.CreateAsync(context, dataSource, license, options,
                NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance, request);
            using var payload = await ExecuteJsonAsync(result, context);
            jobId = payload.RootElement.GetProperty("jobId").GetGuid();
        }
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            CREATE FUNCTION reject_advanced_audit_for_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.action IN ('advanced_analysis.job.create', 'advanced_analysis.job.cancel') THEN
                RAISE EXCEPTION 'controlled advanced audit failure' USING ERRCODE='P0001';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER reject_advanced_audit_for_test BEFORE INSERT ON audit_events
              FOR EACH ROW EXECUTE FUNCTION reject_advanced_audit_for_test();
            """);

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            if (cancel)
                await AdvancedAnalysisEndpoints.CancelAsync(BuildContext(tenant), dataSource,
                    NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance, jobId!.Value, request.UserId);
            else
                await AdvancedAnalysisEndpoints.CreateAsync(BuildContext(tenant), dataSource, license, options,
                    NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance, request);
        });
        Assert.Equal("P0001", error.SqlState);
        var rows = (await connection.QueryAsync<(string Status, long Revision, bool CancelRequested)>("""
            SELECT status, revision::bigint, cancel_requested_at IS NOT NULL
            FROM advanced_analysis_jobs WHERE tenant_id=@tenant AND user_id=@user;
            """, new { tenant, user = request.UserId })).ToArray();
        if (cancel)
        {
            var row = Assert.Single(rows);
            Assert.Equal("queued", row.Status);
            Assert.Equal(1, row.Revision);
            Assert.False(row.CancelRequested);
        }
        else Assert.Empty(rows);
        Assert.Equal(cancel ? 1 : 0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM audit_events WHERE tenant_id=@tenant;", new { tenant }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Concurrent_distinct_handoffs_share_user_quota_but_do_not_block_other_owners()
    {
        await using var database = await PostgresIntegrationDb.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its isolated PostgreSQL fixture.");
        var application = "advanced-quota-" + Guid.NewGuid().ToString("N");
        await using var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { ApplicationName = application }.ConnectionString);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var first = BuildRequest(userId: "quota-owner", sessionId: Guid.NewGuid(), handoffId: Guid.NewGuid());
        var second = BuildRequest(userId: "quota-owner", sessionId: Guid.NewGuid(), handoffId: Guid.NewGuid());
        var otherUser = BuildRequest(userId: "other-owner", sessionId: Guid.NewGuid(), handoffId: Guid.NewGuid());
        var crossTenant = BuildRequest(userId: "quota-owner", sessionId: Guid.NewGuid(), handoffId: Guid.NewGuid());
        foreach (var request in new[] { first, second, otherUser })
            await SeedSessionAsync(dataSource, tenant, request.SessionId, request.UserId);
        await SeedSessionAsync(dataSource, otherTenant, crossTenant.SessionId, crossTenant.UserId);
        var license = Options.Create(new LicenseOptions { AdvancedAnalysisEnabled = true });
        var options = Options.Create(new AdvancedAnalysisOptions { MaximumQueuedJobsPerUser = 1 });
        await using var monitor = new NpgsqlConnection(database.ConnectionString);
        await monitor.OpenAsync();
        // A literal fixture UUID scopes the insertion barrier. Other users and
        // tenants must be able to pass admission while this user's insert waits.
        await monitor.ExecuteAsync($"""
            CREATE FUNCTION hold_quota_insert_for_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.tenant_id = '{tenant:D}'::uuid AND NEW.user_id='quota-owner' THEN
                PERFORM pg_advisory_xact_lock(731094215);
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER hold_quota_insert_for_test BEFORE INSERT ON advanced_analysis_jobs
              FOR EACH ROW EXECUTE FUNCTION hold_quota_insert_for_test();
            SELECT pg_advisory_lock(731094215);
            """);
        async Task<int> CreateStatusAsync(Guid owner, SAAIA.Contracts.AdvancedAnalysisJobCreateRequest request)
        {
            var context = BuildContext(owner);
            var result = await AdvancedAnalysisEndpoints.CreateAsync(context, dataSource, license, options,
                NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance, request);
            return await ExecuteStatusCodeAsync(result, context);
        }
        var processing = new[] { CreateStatusAsync(tenant, first), CreateStatusAsync(tenant, second) };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await monitor.ExecuteScalarAsync<int>(new CommandDefinition("""
                       SELECT count(*)::int FROM pg_stat_activity
                       WHERE application_name=@application AND datname=current_database()
                         AND state='active' AND wait_event_type='Lock';
                       """, new { application }, cancellationToken: timeout.Token)) < 2)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(StatusCodes.Status202Accepted,
                await CreateStatusAsync(tenant, otherUser).WaitAsync(timeout.Token));
            Assert.Equal(StatusCodes.Status202Accepted,
                await CreateStatusAsync(otherTenant, crossTenant).WaitAsync(timeout.Token));
        }
        finally
        {
            await monitor.ExecuteAsync("SELECT pg_advisory_unlock(731094215);");
            await Task.WhenAll(processing).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(new[] { StatusCodes.Status202Accepted, StatusCodes.Status429TooManyRequests },
            processing.Select(task => task.Result).OrderBy(value => value));
        Assert.Equal(1, await monitor.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM advanced_analysis_jobs
            WHERE tenant_id=@tenant AND user_id='quota-owner' AND status IN ('queued','running');
            """, new { tenant }));
    }
}
