using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_bad_manifest_is_durably_quarantined_without_poisoning_other_plans()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-corruption-control.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-corruption-control')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        await JobRepo.MarkCanceledAsync(ds, first.Job.JobId, "fixture_finished", CancellationToken.None, first.Job.LeaseOwner);
        var second = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        await JobRepo.MarkCanceledAsync(ds, second.Job.JobId, "fixture_finished", CancellationToken.None, second.Job.LeaseOwner);
        var current = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'c');
        // Simulate administrative corruption only inside this new fixture. Normal registry writes
        // cannot remove a sealed point; the collector must nevertheless fail closed durably.
        await conn.ExecuteAsync("""
ALTER TABLE ingestion_vector_plan_points DISABLE TRIGGER ingestion_vector_point_immutable;
DELETE FROM ingestion_vector_plan_points WHERE plan_id=@plan;
ALTER TABLE ingestion_vector_plan_points ENABLE TRIGGER ingestion_vector_point_immutable;
""", new { plan = first.PlanId });
        var stored = new HashSet<Guid> { first.Point.PointId, second.Point.PointId, current.Point.PointId };
        var collector = new VectorGarbageCollector(ds, new MemoryVectorGcTransport(stored),
            Options.Create(new VectorGcOptions { Enabled = true }), NullLogger<VectorGarbageCollector>.Instance);
        var round = await collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, round.DeferredPlans);
        Assert.Equal(1, round.VerifiedAbsentPoints);
        Assert.Contains(first.Point.PointId, stored);
        Assert.DoesNotContain(second.Point.PointId, stored);
        Assert.Contains(current.Point.PointId, stored);
        Assert.Equal("manifest_incomplete", await conn.ExecuteScalarAsync<string>(
            "SELECT last_gc_code FROM ingestion_vector_plans WHERE plan_id=@plan", new { plan = first.PlanId }));
        var inventory = await VectorGcInventory.ReadAsync(ds, CancellationToken.None);
        Assert.Equal(1, inventory.QuarantinedPlans);
        Assert.Equal(0, inventory.TransportFailures);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_manifest_rejects_post_registration_point_append()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-manifest-seal')", new { tenant });
        var fixture = await CreateVectorGcPlanAsync(ds, conn, tenant, "Knowledge/GC-seal.pdf", 'a');
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("""
INSERT INTO ingestion_vector_plan_points(plan_id,ordinal,point_id,canonical_chunk_id)
VALUES(@plan,1,@point,@chunk);
""", new { plan = fixture.PlanId, point = Guid.NewGuid(), chunk = Guid.NewGuid() }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ingestion_vector_plan_points WHERE plan_id=@plan", new { plan = fixture.PlanId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_gc_retirement_commits_or_rolls_back_with_real_publication(bool failPublication)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-publication.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-publication')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(ds, first.Job, first.Fingerprint));
        var second = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        if (failPublication)
        {
            await conn.ExecuteAsync("""
CREATE FUNCTION gc_fixture_fail_publication() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'gc_fixture_publish_failure'; END $$;
CREATE TRIGGER gc_fixture_fail_publication BEFORE INSERT ON document_revisions FOR EACH ROW EXECUTE FUNCTION gc_fixture_fail_publication();
""");
            await Assert.ThrowsAsync<PostgresException>(() => PublishVectorGcPlanAsync(ds, second.Job, second.Fingerprint));
        }
        else Assert.True(await PublishVectorGcPlanAsync(ds, second.Job, second.Fingerprint));
        var published = await conn.QuerySingleAsync<(int Version, string Fingerprint)>(
            "SELECT indexed_version AS Version,ingestion_vector_plan_fingerprint AS Fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant, doc = first.Job.DocId });
        Assert.Equal(failPublication ? first.Job.Version : second.Job.Version, published.Version);
        Assert.Equal(failPublication ? first.Fingerprint : second.Fingerprint, published.Fingerprint);
        Assert.Equal(!failPublication, await conn.ExecuteScalarAsync<bool>(
            "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan", new { plan = first.PlanId }));
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan", new { plan = second.PlanId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_gc_preserves_active_older_publication_and_all_current_generation_plans(bool cancel)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-active.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-active')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(ds, first.Job, first.Fingerprint));
        var second = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        var alternate = new string('c', 64);
        var altPoint = new IngestionVectorPoint(IngestionVectorPlan.PointId(second.Point.ChunkId, alternate), second.Point.ChunkId);
        await IngestionVectorRegistry.RegisterAsync(ds, second.Job, second.Job.LeaseOwner!, VectorGcRag(), alternate, [altPoint], CancellationToken.None);
        if (cancel) await JobRepo.MarkCanceledAsync(ds, second.Job.JobId, "fixture_cancel", CancellationToken.None, second.Job.LeaseOwner);
        else await JobRepo.MarkFailedAsync(ds, second.Job.JobId, "fixture_failure", CancellationToken.None, second.Job.LeaseOwner);
        Assert.Equal(0, await IngestionVectorRegistry.RetireEligibleAsync(conn, null, 100, CancellationToken.None));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_vector_plans WHERE retired_at IS NOT NULL"));
        Assert.Equal(first.Fingerprint, await conn.ExecuteScalarAsync<string>(
            "SELECT ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant", new { tenant }));
        // Legacy active publication has no plan proof: protect its whole published generation.
        await conn.ExecuteAsync("UPDATE documents SET ingestion_vector_plan_fingerprint=NULL WHERE tenant_id=@tenant", new { tenant });
        Assert.Equal(0, await IngestionVectorRegistry.RetireEligibleAsync(conn, null, 100, CancellationToken.None));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_tombstone_forbids_reactivation_and_survives_tenant_cascade()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-retirement.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-retirement')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        await JobRepo.MarkCanceledAsync(ds, first.Job.JobId, "fixture_finished", CancellationToken.None, first.Job.LeaseOwner);
        var second = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        Assert.Equal(1, await IngestionVectorRegistry.RetireEligibleAsync(conn, null, 100, CancellationToken.None));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => IngestionVectorRegistry.AssertWritableAsync(ds, first.PlanId, first.Job,
            first.Job.LeaseOwner!, CancellationToken.None));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => IngestionVectorRegistry.RegisterAsync(ds,
            second.Job, second.Job.LeaseOwner!, VectorGcRag(), first.Fingerprint, [first.Point], CancellationToken.None));
        Assert.Equal("vector_plan_retired_or_generation_mismatch", rejected.Message);
        Assert.False(await PublishVectorGcPlanAsync(ds, second.Job, first.Fingerprint));
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE ingestion_vector_plans SET retired_at=NULL WHERE plan_id=@plan", new { plan = first.PlanId }));
        Assert.Equal(1, await conn.ExecuteAsync("DELETE FROM tenants WHERE tenant_id=@tenant", new { tenant }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_vector_plans WHERE tenant_id=@tenant", new { tenant }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_vector_plan_points"));
        Assert.True(await conn.ExecuteScalarAsync<long>(
            "SELECT highest_generation FROM ingestion_generation_watermarks WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant, doc = first.Job.DocId }) >= second.Job.Version);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_rechecks_retired_ids_after_late_write_and_preserves_the_new_plan()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-late-write.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-late-write')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(ds, first.Job, first.Fingerprint));
        var second = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        Assert.True(await PublishVectorGcPlanAsync(ds, second.Job, second.Fingerprint));
        var stored = new HashSet<Guid> { first.Point.PointId, second.Point.PointId };
        var transport = new MemoryVectorGcTransport(stored);
        var collector = new VectorGarbageCollector(ds, transport, Options.Create(new VectorGcOptions
        { Enabled = true, MaxPlansPerRound = 1, RecheckSeconds = 1 }), NullLogger<VectorGarbageCollector>.Instance);
        var initial = await collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, initial.VerifiedAbsentPoints);
        Assert.DoesNotContain(first.Point.PointId, stored);
        Assert.Contains(second.Point.PointId, stored);
        stored.Add(first.Point.PointId); // An already emitted old upsert completes after the sweep.
        await conn.ExecuteAsync("UPDATE ingestion_vector_plans SET available_at=now() WHERE plan_id=@plan", new { plan = first.PlanId });
        var repeated = await collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, repeated.VerifiedAbsentPoints);
        Assert.DoesNotContain(first.Point.PointId, stored);
        Assert.Contains(second.Point.PointId, stored);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(first.PlanId, transport.LastPlan);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_lost_collector_lease_cannot_checkpoint_successor_or_hide_failed_batch()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-lease.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-lease')", new { tenant });
        var first = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        await JobRepo.MarkCanceledAsync(ds, first.Job.JobId, "fixture_finished", CancellationToken.None, first.Job.LeaseOwner);
        await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'b');
        Assert.Equal(1, await IngestionVectorRegistry.RetireEligibleAsync(conn, null, 100, CancellationToken.None));
        var ownerA = Assert.Single(await VectorGcQueue.ClaimAsync(ds, 1, 60, CancellationToken.None));
        Assert.Empty(await VectorGcQueue.ClaimAsync(ds, 1, 60, CancellationToken.None));
        await conn.ExecuteAsync("UPDATE ingestion_vector_plans SET gc_expires_at=now()-interval '1 second' WHERE plan_id=@plan", new { plan = first.PlanId });
        var ownerB = Assert.Single(await VectorGcQueue.ClaimAsync(ds, 1, 60, CancellationToken.None));
        Assert.NotEqual(ownerA.Owner, ownerB.Owner);
        Assert.False(await VectorGcQueue.CheckpointAsync(ds, ownerA, 1, new("absence_observed", true, 1, 1), 1, 1, CancellationToken.None));
        Assert.True(await VectorGcQueue.CheckpointAsync(ds, ownerB, 1, new("operation_timeout", false, 0, 0), 1, 1, CancellationToken.None));
        var state = await conn.QuerySingleAsync<(int Cursor, string Code, DateTime? Completed)>(
            "SELECT gc_cursor AS Cursor,last_gc_code AS Code,last_sweep_completed_at AS Completed FROM ingestion_vector_plans WHERE plan_id=@plan", new { plan = first.PlanId });
        Assert.Equal(0, state.Cursor);
        Assert.Equal("operation_timeout", state.Code);
        Assert.Null(state.Completed);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_manifest_is_idempotent_complete_and_rejects_changed_target_or_points()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-manifest.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-manifest')", new { tenant });
        var fixture = await CreateVectorGcPlanAsync(ds, conn, tenant, path, 'a');
        Assert.Equal(fixture.PlanId, await IngestionVectorRegistry.RegisterAsync(ds, fixture.Job, fixture.Job.LeaseOwner!,
            VectorGcRag(), fixture.Fingerprint, [fixture.Point], CancellationToken.None));
        var changedTarget = VectorGcRag();
        changedTarget.QdrantCollection = "different";
        await Assert.ThrowsAsync<InvalidOperationException>(() => IngestionVectorRegistry.RegisterAsync(ds,
            fixture.Job, fixture.Job.LeaseOwner!, changedTarget, fixture.Fingerprint, [fixture.Point], CancellationToken.None));
        var chunk = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => IngestionVectorRegistry.RegisterAsync(ds,
            fixture.Job, fixture.Job.LeaseOwner!, VectorGcRag(), fixture.Fingerprint,
            [new(IngestionVectorPlan.PointId(chunk, fixture.Fingerprint), chunk)], CancellationToken.None));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => IngestionVectorRegistry.RegisterAsync(ds,
            fixture.Job, "wrong-owner", VectorGcRag(), fixture.Fingerprint, [fixture.Point], CancellationToken.None));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_vector_plans"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_vector_plan_points"));
    }

    private static RagOptions VectorGcRag() => new()
    { QdrantBaseUrl = "http://controlled.invalid", QdrantCollection = "owned", QdrantRuntimeRevision = "qualified-runtime-v1" };

    private static async Task<(IngestionJob Job, Guid PlanId, string Fingerprint, IngestionVectorPoint Point)> CreateVectorGcPlanAsync(
        NpgsqlDataSource ds, NpgsqlConnection conn, Guid tenant, string path, char fingerprintCharacter)
    {
        var queued = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenant, path, "knowledge", null, CancellationToken.None);
        var job = await JobRepo.TryDequeueAsync(ds, "gc-registry-fixture", CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(queued.JobId, job!.JobId);
        var fingerprint = new string(fingerprintCharacter, 64);
        var chunk = DocumentFoundationRepo.BuildStableRetrievalChunkId(job.DocId, job.Version, 0);
        var point = new IngestionVectorPoint(IngestionVectorPlan.PointId(chunk, fingerprint), chunk);
        var plan = await IngestionVectorRegistry.RegisterAsync(ds, job, job.LeaseOwner!, VectorGcRag(), fingerprint, [point], CancellationToken.None);
        return (job, plan, fingerprint, point);
    }

    private static Task<bool> PublishVectorGcPlanAsync(NpgsqlDataSource ds, IngestionJob job, string fingerprint)
    {
        const string text = "The operator must disconnect the instrument before maintenance and record the measurement in the approved register for every inspection.";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var page = new ExtractedPdfPage(1, text, 22, text.Length, hash);
        var section = new ExtractedDocumentSection(0, "Control", 1, 1, 1, 1, null);
        var unit = new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 22, hash);
        var chunk = new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 22, hash, "unit_exact_v1");
        return JobRepo.CompleteUpsertAsync(ds, job.TenantId, job.JobId, job.DocPath, hash, text.Length, DateTime.UtcNow,
            job.Version, [page], [section], [unit], [chunk], [], [], CancellationToken.None,
            ingestionVectorPlanFingerprint: fingerprint, leaseOwner: job.LeaseOwner);
    }

    private sealed class MemoryVectorGcTransport(HashSet<Guid> stored) : IVectorGcTransport
    {
        public int Calls { get; private set; }
        public Guid LastPlan { get; private set; }
        public Task<VectorGcBatchResult> CollectAsync(VectorGcPlan plan, IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct)
        {
            Calls++;
            LastPlan = plan.PlanId;
            foreach (var point in points) stored.Remove(point.PointId);
            return Task.FromResult(new VectorGcBatchResult("absence_observed", true, points.Count, points.Count));
        }
    }
}
