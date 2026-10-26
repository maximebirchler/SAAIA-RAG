using Dapper;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conditional_delete_commit_rechecks_under_document_lock_and_refuses_present_or_unknown_source(bool present)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var source = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Conditional-commit-gate.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'conditional-commit-gate')", new { tenant });
        var published = await CreateVectorGcPlanAsync(source, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(source, published.Job, published.Fingerprint));
        var deletion = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: true, enqueueSource: "scanner_missing");
        var job = await JobRepo.TryDequeueAsync(source, "conditional-commit-gate-worker", CancellationToken.None);
        Assert.NotNull(job);
        var probed = false;
        IngestionSourcePresence ObserveUnderLock()
        {
            using var observer = source.OpenConnection();
            var lockError = Assert.Throws<PostgresException>(() => observer.ExecuteScalar<int>(
                "SELECT 1 FROM documents WHERE tenant_id=@tenant AND doc_id=@doc FOR UPDATE NOWAIT;",
                new { tenant, doc = published.Job.DocId }));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, lockError.SqlState);
            probed = true;
            return present ? IngestionSourcePresence.Present : IngestionSourcePresence.Unknown;
        }
        Func<Task> complete = () => JobRepo.CompleteDeleteAsync(source, tenant, job!.JobId, path, deletion.Version,
            CancellationToken.None, leaseOwner: job!.LeaseOwner, conditionalSourcePresence: ObserveUnderLock);
        if (present) await Assert.ThrowsAsync<IngestionSourceReturnedBeforeCommitException>(complete);
        else await Assert.ThrowsAsync<IngestionSourcePresenceUnknownException>(complete);

        Assert.True(probed);
        Assert.Equal((deletion.Version, published.Job.Version, published.Fingerprint),
            await connection.QuerySingleAsync<(int Ingestion, int Indexed, string? Plan)>(
                "SELECT ingestion_version,indexed_version,ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant, doc = published.Job.DocId }));
        Assert.Equal("running", await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM ingestion_jobs WHERE job_id=@job;", new { job = job!.JobId }));
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan;", new { plan = published.PlanId }));
        if (present)
            Assert.Equal(ReturnedScannerSourceOutcome.FreshGenerationQueued,
                await JobRepo.PreserveReturnedScannerSourceAsync(source, job!, job!.LeaseOwner!, CancellationToken.None));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Returned_source_reports_superseded_without_claiming_another_fresh_generation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var source = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Conditional-source-superseded.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'conditional-superseded-outcome')", new { tenant });
        var published = await CreateVectorGcPlanAsync(source, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(source, published.Job, published.Fingerprint));
        await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: true, enqueueSource: "scanner_missing");
        var job = await JobRepo.TryDequeueAsync(source, "conditional-superseded-outcome", CancellationToken.None);
        Assert.NotNull(job);
        var replacement = await IngestionEnqueue.EnqueueUpsertAsync(connection, tenant, path, "Knowledge", null,
            CancellationToken.None, enqueueSource: "admin");

        Assert.Equal(ReturnedScannerSourceOutcome.Superseded,
            await JobRepo.PreserveReturnedScannerSourceAsync(source, job!, job!.LeaseOwner!, CancellationToken.None));

        Assert.Equal((replacement.Version, published.Job.Version, published.Fingerprint),
            await connection.QuerySingleAsync<(int Ingestion, int Indexed, string? Plan)>(
                "SELECT ingestion_version,indexed_version,ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant, doc = published.Job.DocId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND action='upsert' AND status='queued';", new { tenant }));
        Assert.Equal(replacement.Version, await connection.ExecuteScalarAsync<int>(
            "SELECT (payload->>'version')::int FROM ingestion_jobs WHERE job_id=@job AND status='queued';",
            new { job = replacement.JobId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Superseded_running_delete_preserves_latest_versioned_conditional_intent(
        bool firstConditional, bool latestConditional)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var source = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Latest-delete-intent.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'conditional-intent')", new { tenant });
        var published = await CreateVectorGcPlanAsync(source, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(source, published.Job, published.Fingerprint));
        var first = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: firstConditional, enqueueSource: firstConditional ? "scanner_missing" : "admin");
        var running = await JobRepo.TryDequeueAsync(source, "delete-intent-first", CancellationToken.None);
        Assert.NotNull(running);
        Assert.Equal(first.JobId, running!.JobId);
        Assert.Equal(firstConditional, running.RequiresSourceAbsence);
        var latest = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: latestConditional, enqueueSource: latestConditional ? "scanner_missing" : "admin");
        Assert.Equal(first.JobId, latest.JobId);
        Assert.True(latest.Version > first.Version);

        Assert.False(await JobRepo.CompleteDeleteAsync(source, tenant, running.JobId, path, running.Version,
            CancellationToken.None, leaseOwner: running.LeaseOwner));
        var next = await JobRepo.TryDequeueAsync(source, "delete-intent-latest", CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal("delete", next!.Action);
        Assert.Equal(latest.Version, next.Version);
        Assert.Equal(latestConditional, next.RequiresSourceAbsence);
        Assert.NotEqual(first.JobId, next.JobId);
        Assert.Equal(published.Job.Version, await connection.ExecuteScalarAsync<int>(
            "SELECT indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
            new { tenant, doc = published.Job.DocId }));
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan;", new { plan = published.PlanId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Returned_source_transition_rolls_back_document_and_delete_if_fresh_upsert_insert_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var source = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Returned-source-rollback.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'conditional-rollback')", new { tenant });
        var published = await CreateVectorGcPlanAsync(source, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(source, published.Job, published.Fingerprint));
        var deletion = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: true, enqueueSource: "scanner_missing");
        var job = await JobRepo.TryDequeueAsync(source, "conditional-rollback-worker", CancellationToken.None);
        Assert.NotNull(job);
        await connection.ExecuteAsync("""
CREATE FUNCTION fail_returned_source_insert() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'synthetic_fresh_upsert_insert_failure'; END $$;
CREATE TRIGGER fail_returned_source_insert BEFORE INSERT ON ingestion_jobs
FOR EACH ROW WHEN (NEW.action='upsert' AND NEW.payload->>'source'='scanner_source_returned')
EXECUTE FUNCTION fail_returned_source_insert();
""");

        await Assert.ThrowsAsync<PostgresException>(() => JobRepo.PreserveReturnedScannerSourceAsync(
            source, job!, job!.LeaseOwner!, CancellationToken.None));

        var document = await connection.QuerySingleAsync<(int Ingestion, int Indexed, string? Plan)>(
            "SELECT ingestion_version,indexed_version,ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
            new { tenant, doc = published.Job.DocId });
        Assert.Equal(deletion.Version, document.Ingestion);
        Assert.Equal(published.Job.Version, document.Indexed);
        Assert.Equal(published.Fingerprint, document.Plan);
        var state = await connection.QuerySingleAsync<(string Status, string? Owner)>(
            "SELECT status,locked_by FROM ingestion_jobs WHERE job_id=@job;", new { job = job!.JobId });
        Assert.Equal("running", state.Status);
        Assert.Equal(job.LeaseOwner, state.Owner);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND action='upsert' AND status='queued';", new { tenant }));
        Assert.True(await connection.ExecuteScalarAsync<long>(
            "SELECT highest_generation FROM ingestion_generation_watermarks WHERE tenant_id=@tenant AND doc_id=@doc;",
            new { tenant, doc = published.Job.DocId }) > deletion.Version);
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan;", new { plan = published.PlanId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Returned_source_transition_rejects_stale_owner_without_mutating_successor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var source = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Returned-source-lease.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'conditional-lease')", new { tenant });
        var published = await CreateVectorGcPlanAsync(source, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(source, published.Job, published.Fingerprint));
        var deletion = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None,
            requiresSourceAbsence: true, enqueueSource: "scanner_missing");
        var previous = await JobRepo.TryDequeueAsync(source, "conditional-owner-A", CancellationToken.None);
        Assert.NotNull(previous);
        await connection.ExecuteAsync("UPDATE ingestion_jobs SET locked_at=now()-interval '2 hours' WHERE job_id=@job;",
            new { job = previous!.JobId });
        Assert.Equal(1, await JobRepo.RequeueStaleRunningAsync(source, TimeSpan.FromMinutes(5), CancellationToken.None));
        var successor = await JobRepo.TryDequeueAsync(source, "conditional-owner-B", CancellationToken.None);
        Assert.NotNull(successor);
        Assert.NotEqual(previous.LeaseOwner, successor!.LeaseOwner);

        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.PreserveReturnedScannerSourceAsync(
            source, previous, previous.LeaseOwner!, CancellationToken.None));

        Assert.Equal(("running", successor.LeaseOwner), await connection.QuerySingleAsync<(string Status, string? Owner)>(
            "SELECT status,locked_by FROM ingestion_jobs WHERE job_id=@job;", new { job = successor.JobId }));
        Assert.Equal((deletion.Version, published.Job.Version, published.Fingerprint),
            await connection.QuerySingleAsync<(int Ingestion, int Indexed, string? Plan)>(
                "SELECT ingestion_version,indexed_version,ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant, doc = published.Job.DocId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND action='upsert' AND status='queued';", new { tenant }));
    }
}
