using Dapper;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_generation_exhaustion_refuses_admission_without_wrapping_or_mutating_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/GC-exhaustion.pdf";
        await conn.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-exhaustion')", new { tenant });
        var first = await EnqueueVectorGcGenerationAsync(conn, tenant, path, "upsert");
        await conn.ExecuteAsync("UPDATE ingestion_generation_watermarks SET highest_generation=@maximum WHERE tenant_id=@tenant AND doc_id=@doc",
            new { maximum = (long)int.MaxValue, tenant, doc = first.DocId });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EnqueueVectorGcGenerationAsync(conn, tenant, path, "delete"));
        Assert.Equal("ingestion_generation_exhausted", error.Message);
        Assert.Equal(first.Version, await conn.ExecuteScalarAsync<int>(
            "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc = first.DocId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_jobs"));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_admission_resolves_replaced_document_identity_before_reusing_its_watermark()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var delayedConnection = await ds.OpenConnectionAsync();
        await using var maintenance = await ds.OpenConnectionAsync();
        await using var barrier = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var formerDoc = Guid.NewGuid();
        const string path = "Knowledge/GC-identity-replacement.pdf";
        var replacementDoc = IdUtil.DeterministicGuid($"{tenant}:{path}");
        var gate = Random.Shared.NextInt64(1, long.MaxValue);
        await maintenance.ExecuteAsync("""
INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-identity-replacement');
INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
VALUES(@tenant,@former,@path,'control.pdf','knowledge','pending',10,0);
INSERT INTO ingestion_generation_watermarks(tenant_id,doc_id,highest_generation) VALUES(@tenant,@replacement,50);
""", new { tenant, former = formerDoc, replacement = replacementDoc, path });
        await maintenance.ExecuteAsync($"""
CREATE FUNCTION gc_identity_admission_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF current_setting('saaia.fixture_admission',true)='delayed' THEN PERFORM pg_advisory_xact_lock({gate}); END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gc_identity_admission_barrier BEFORE INSERT ON documents FOR EACH ROW EXECUTE FUNCTION gc_identity_admission_barrier();
""");
        await delayedConnection.ExecuteAsync("SET saaia.fixture_admission='delayed'");
        var pid = await delayedConnection.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
        await barrier.ExecuteAsync("SELECT pg_advisory_lock(@gate)", new { gate });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<(Guid DocId, int Version, Guid JobId)>? delayed = null;
        try
        {
            delayed = IngestionEnqueue.EnqueueUpsertAsync(delayedConnection, tenant, path, "knowledge", null, deadline.Token);
            while (!await maintenance.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event='advisory')", new { pid }, cancellationToken: deadline.Token)))
                await Task.Delay(10, deadline.Token);
            // The schema permits legacy/import IDs. This is an isolated maintenance witness,
            // not a claim that a current WinUI flow rewrites document IDs.
            await maintenance.ExecuteAsync("""
DELETE FROM documents WHERE tenant_id=@tenant AND doc_id=@former;
INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
VALUES(@tenant,@replacement,@path,'control.pdf','knowledge','pending',1,0);
""", new { tenant, former = formerDoc, replacement = replacementDoc, path });
            await barrier.ExecuteAsync("SELECT pg_advisory_unlock(@gate)", new { gate });
            var admitted = await delayed;
            Assert.Equal(replacementDoc, admitted.DocId);
            Assert.True(admitted.Version > 50,
                $"A reservation of the former ID must not advance a replacement document below its own historical floor. Actual={admitted.Version}; replacementFloor=50.");
            Assert.True(await maintenance.ExecuteScalarAsync<long>(
                "SELECT highest_generation FROM ingestion_generation_watermarks WHERE tenant_id=@tenant AND doc_id=@replacement",
                new { tenant, replacement = replacementDoc }) >= admitted.Version);
        }
        finally
        {
            await barrier.ExecuteAsync("SELECT pg_advisory_unlock(@gate)", new { gate });
            if (delayed is not null) { try { await delayed; } catch (OperationCanceledException) { } }
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("upsert", "delete")]
    [InlineData("delete", "upsert")]
    public async Task Vector_gc_out_of_order_admissions_never_share_generation_between_upsert_and_delete(string delayedAction, string earlierAction)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var delayedConnection = await ds.OpenConnectionAsync();
        await using var earlierConnection = await ds.OpenConnectionAsync();
        await using var barrierConnection = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var gate = Random.Shared.NextInt64(1, long.MaxValue);
        const string path = "Knowledge/GC-admission-order.pdf";
        await earlierConnection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'gc-admission-order')", new { tenant });
        await earlierConnection.ExecuteAsync($"""
CREATE FUNCTION gc_admission_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF current_setting('saaia.fixture_admission',true)='delayed' THEN PERFORM pg_advisory_xact_lock({gate}); END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gc_admission_barrier BEFORE INSERT ON documents FOR EACH ROW EXECUTE FUNCTION gc_admission_barrier();
""");
        await delayedConnection.ExecuteAsync("SET saaia.fixture_admission='delayed'");
        var delayedPid = await delayedConnection.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
        await barrierConnection.ExecuteAsync("SELECT pg_advisory_lock(@gate)", new { gate });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<(Guid DocId, int Version, Guid JobId)>? delayed = null;
        try
        {
            delayed = delayedAction == "upsert"
                ? IngestionEnqueue.EnqueueUpsertAsync(delayedConnection, tenant, path, "knowledge", null, deadline.Token)
                : IngestionEnqueue.EnqueueDeleteAsync(delayedConnection, tenant, path, deadline.Token);
            while (!await earlierConnection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event='advisory')",
                new { pid = delayedPid }, cancellationToken: deadline.Token)))
                await Task.Delay(10, deadline.Token);
            var earlier = await EnqueueVectorGcGenerationAsync(earlierConnection, tenant, path, earlierAction);
            var acquiredEarlier = await JobRepo.TryDequeueAsync(ds, "gc-admission-earlier", deadline.Token);
            Assert.NotNull(acquiredEarlier);
            Assert.Equal(earlier.JobId, acquiredEarlier!.JobId);
            await barrierConnection.ExecuteAsync("SELECT pg_advisory_unlock(@gate)", new { gate });
            var admittedLater = await delayed;
            Assert.True(admittedLater.Version > earlier.Version,
                $"Distinct {earlierAction}/{delayedAction} admissions must not share a generation when the lower reservation reaches documents last. " +
                $"Earlier={earlier.Version}; later={admittedLater.Version}.");
            var earlierCanPublish = earlierAction == "delete"
                ? await JobRepo.CompleteDeleteAsync(ds, tenant, earlier.JobId, path, earlier.Version, deadline.Token, acquiredEarlier.LeaseOwner)
                : await PublishVectorGcPlanAsync(ds, acquiredEarlier, new string('a',64));
            Assert.False(earlierCanPublish);
            Assert.Equal(admittedLater.Version, await earlierConnection.ExecuteScalarAsync<int>(
                "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc = admittedLater.DocId }));
        }
        finally
        {
            await barrierConnection.ExecuteAsync("SELECT pg_advisory_unlock(@gate)", new { gate });
            if (delayed is not null) { try { await delayed; } catch (OperationCanceledException) { } }
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("upsert", "upsert", false)]
    [InlineData("upsert", "delete", false)]
    [InlineData("delete", "upsert", false)]
    [InlineData("delete", "delete", false)]
    [InlineData("upsert", "upsert", true)]
    [InlineData("upsert", "delete", true)]
    [InlineData("delete", "upsert", true)]
    [InlineData("delete", "delete", true)]
    public async Task Vector_gc_recreation_never_reuses_a_generation_or_point_identity_after_physical_removal(
        string firstAction, string nextAction, bool removeTenant)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Vector-generation-control.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'vector-generation-control')", new { tenant });

        var first = await EnqueueVectorGcGenerationAsync(connection, tenant, path, firstAction);
        var acquired = await JobRepo.TryDequeueAsync(ds, "vector-gc-fixture", CancellationToken.None);
        Assert.NotNull(acquired);
        Assert.Equal(first.JobId, acquired!.JobId);
        Assert.Equal(first.DocId, acquired.DocId);
        Assert.Equal(first.Version, acquired.Version);
        await JobRepo.MarkCanceledAsync(ds, acquired.JobId, "fixture_generation_finished", CancellationToken.None,
            leaseOwner: acquired.LeaseOwner);
        Assert.Equal("canceled", await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM ingestion_jobs WHERE job_id=@job", new { job = first.JobId }));

        // Only this fresh isolated fixture row is removed. Production deletion currently keeps
        // documents; a future maintenance purge must not erase the storage-generation barrier.
        if (removeTenant)
        {
            Assert.Equal(1, await connection.ExecuteAsync("DELETE FROM tenants WHERE tenant_id=@tenant", new { tenant }));
            await connection.ExecuteAsync("INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'vector-generation-control')", new { tenant });
        }
        else
        {
            Assert.Equal(1, await connection.ExecuteAsync("DELETE FROM documents WHERE tenant_id=@tenant AND doc_id=@doc",
                new { tenant, doc = first.DocId }));
        }

        var next = await EnqueueVectorGcGenerationAsync(connection, tenant, path, nextAction);
        Assert.Equal(first.DocId, next.DocId);
        var firstChunk = DocumentFoundationRepo.BuildStableRetrievalChunkId(first.DocId, first.Version, 0);
        var nextChunk = DocumentFoundationRepo.BuildStableRetrievalChunkId(next.DocId, next.Version, 0);
        var firstPlan = VectorGcFixturePlan(tenant, first.DocId, path, first.Version);
        var nextPlan = VectorGcFixturePlan(tenant, next.DocId, path, next.Version);
        var firstPoint = IngestionVectorPlan.PointId(firstChunk, firstPlan);
        var nextPoint = IngestionVectorPlan.PointId(nextChunk, nextPlan);
        Assert.True(next.Version > first.Version && firstChunk != nextChunk && firstPlan != nextPlan && firstPoint != nextPoint,
            $"A physical {(removeTenant ? "tenant" : "document")} removal must preserve the generation floor. " +
            $"Previous={first.Version}; next={next.Version}; chunkReused={firstChunk == nextChunk}; " +
            $"planReused={firstPlan == nextPlan}; pointReused={firstPoint == nextPoint}. " +
            "A delayed delete of a retired plan must never address the recreated publication.");
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_current_generation_retry_preserves_generation_but_renews_lease()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Vector-retry-control.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'vector-retry-control')", new { tenant });
        var enqueued = await EnqueueVectorGcGenerationAsync(connection, tenant, path, "upsert");
        var first = await JobRepo.TryDequeueAsync(ds, "vector-gc-retry", CancellationToken.None);
        Assert.NotNull(first);
        await JobRepo.DeferTransientAsync(ds, first!.JobId, "fixture_transient", TimeSpan.FromSeconds(1), CancellationToken.None,
            leaseOwner: first.LeaseOwner);
        await connection.ExecuteAsync("UPDATE ingestion_jobs SET available_at=now() WHERE job_id=@job", new { job = first.JobId });
        var second = await JobRepo.TryDequeueAsync(ds, "vector-gc-retry", CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(enqueued.JobId, second!.JobId);
        Assert.Equal(enqueued.DocId, second.DocId);
        Assert.Equal(enqueued.Version, second.Version);
        Assert.NotEqual(first.LeaseOwner, second.LeaseOwner);
        Assert.Equal(enqueued.Version, await connection.ExecuteScalarAsync<int>(
            "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant, doc = enqueued.DocId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Vector_gc_generation_floor_isolated_by_tenant_and_document_identity()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await ds.OpenConnectionAsync();
        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();
        const string path = "Knowledge/Vector-tenant-control.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id, name) VALUES(@firstTenant, 'vector-first'),(@secondTenant, 'vector-second')",
            new { firstTenant, secondTenant });
        var first = await EnqueueVectorGcGenerationAsync(connection, firstTenant, path, "upsert");
        var second = await EnqueueVectorGcGenerationAsync(connection, secondTenant, path, "upsert");
        Assert.Equal(first.Version, second.Version);
        Assert.NotEqual(first.DocId, second.DocId);
        Assert.NotEqual(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(first.DocId, first.Version, 0),
            DocumentFoundationRepo.BuildStableRetrievalChunkId(second.DocId, second.Version, 0));
    }

    private static Task<(Guid DocId, int Version, Guid JobId)> EnqueueVectorGcGenerationAsync(
        NpgsqlConnection connection, Guid tenant, string path, string action)
        => action == "upsert"
            ? IngestionEnqueue.EnqueueUpsertAsync(connection, tenant, path, "knowledge", null, CancellationToken.None)
            : IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None);

    private static string VectorGcFixturePlan(Guid tenant, Guid document, string path, int version)
        => IngestionVectorPlan.Fingerprint(new string('a', 64), new RagOptions(), 3, "raw",
            new object[] { new { sourceFact = "An unchanged controlled source fact." } },
            new IngestionVectorIdentity(tenant, document, path, "knowledge", version));
}
