using System.Reflection;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Scanner_conditional_delete_rechecks_source_at_worker_and_preserves_returned_publication(
        bool requiresSourceAbsence, bool sourceReturns)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        await using var connection = await ds.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        const string path = "Knowledge/Conditional-source.pdf";
        await connection.ExecuteAsync("INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'scanner-conditional-delete')", new { tenant });
        var published = await CreateVectorGcPlanAsync(ds, connection, tenant, path, 'a');
        Assert.True(await PublishVectorGcPlanAsync(ds, published.Job, published.Fingerprint));
        var revisionBefore = await connection.ExecuteScalarAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@doc AND indexed_version=@version;",
            new { tenant, doc = published.Job.DocId, version = published.Job.Version });
        var queuedDelete = await IngestionEnqueue.EnqueueDeleteAsync(connection, tenant, path, CancellationToken.None);
        if (requiresSourceAbsence)
        {
            // Model the scanner admission's persisted contract before the worker guard exists.
            await connection.ExecuteAsync("""
UPDATE ingestion_jobs SET payload=payload || '{"requiresSourceAbsence":true,"source":"scanner_missing"}'::jsonb
WHERE job_id=@job;
""", new { job = queuedDelete.JobId });
        }
        var root = Path.Combine(Path.GetTempPath(), $"saaia-conditional-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Knowledge"));
        try
        {
            if (sourceReturns)
                await File.WriteAllTextAsync(Path.Combine(root, path), "%PDF-1.4\nA distinct returned synthetic source needs fresh ingestion.\n");
            var delete = await JobRepo.TryDequeueAsync(ds, "conditional-delete-worker", CancellationToken.None);
            Assert.NotNull(delete);
            Assert.Equal(queuedDelete.JobId, delete!.JobId);
            Assert.Equal(requiresSourceAbsence, delete.RequiresSourceAbsence);
            using var services = new ServiceCollection().BuildServiceProvider();
            using var worker = new IngestionWorker(services, NullLogger<IngestionWorker>.Instance,
                new IngestionBulkheads(Options.Create(new IngestionOptions()), NullLogger<IngestionBulkheads>.Instance),
                new TeiWorkloadGovernor(), new IngestionJobCancellationRegistry());
            var process = typeof(IngestionWorker).GetMethod("ProcessDeleteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(process);
            await (Task<bool>)process!.Invoke(worker, new object[]
            {
                ds, new IngestionOptions { DocumentsRoot = root }, delete, delete.LeaseOwner!, CancellationToken.None
            })!;

            var state = await connection.QuerySingleAsync<(string Status, int Indexed, int Ingestion, string? Plan)>(
                "SELECT status,indexed_version,ingestion_version,ingestion_vector_plan_fingerprint FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant, doc = published.Job.DocId });
            var retired = await connection.ExecuteScalarAsync<bool>(
                "SELECT retired_at IS NOT NULL FROM ingestion_vector_plans WHERE plan_id=@plan;", new { plan = published.PlanId });
            if (requiresSourceAbsence && sourceReturns)
            {
                Assert.Equal("indexed", state.Status);
                Assert.Equal(published.Job.Version, state.Indexed);
                Assert.Equal(published.Fingerprint, state.Plan);
                Assert.False(retired);
                Assert.True(state.Ingestion > queuedDelete.Version, "A fresh generation must be scheduled for the returned source.");
                Assert.Equal("canceled", await connection.ExecuteScalarAsync<string>(
                    "SELECT status FROM ingestion_jobs WHERE job_id=@job;", new { job = delete.JobId }));
                var fresh = await connection.QuerySingleAsync<(string Status, int Version)>("""
SELECT status,(payload->>'version')::int FROM ingestion_jobs
WHERE tenant_id=@tenant AND doc_path=@path AND action='upsert' AND status='queued';
""", new { tenant, path });
                Assert.Equal(state.Ingestion, fresh.Version);
                Assert.Equal("queued", fresh.Status);
                Assert.Equal(revisionBefore, await connection.ExecuteScalarAsync<Guid>(
                    "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@doc AND indexed_version=@version;",
                    new { tenant, doc = published.Job.DocId, version = published.Job.Version }));
            }
            else
            {
                Assert.Equal("deleted", state.Status);
                Assert.Equal(0, state.Indexed);
                Assert.Null(state.Plan);
                Assert.True(retired);
                Assert.Equal("done", await connection.ExecuteScalarAsync<string>(
                    "SELECT status FROM ingestion_jobs WHERE job_id=@job;", new { job = delete.JobId }));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
