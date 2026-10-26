using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    [InlineData("upsert")]
    [InlineData("unsearchable")]
    [InlineData("ocr_failure")]
    [InlineData("delete")]
    [InlineData("failed")]
    [InlineData("defer")]
    [InlineData("cancel")]
    [InlineData("superseded")]
    [InlineData("progress")]
    [InlineData("checkpoint")]
    public async Task Ingestion_lease_reassigned_to_another_worker_rejects_old_publication_without_mutating_new_owner(string publication)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        const string path = "Knowledge/Lease-control.pdf";
        await db.SeedRunningJobAsync(tenant, doc, jobId, path, 1, 0,
            action: publication == "delete" ? "delete" : "upsert");
        await using var connection = await ds.OpenConnectionAsync();
        await connection.ExecuteAsync("""
UPDATE ingestion_jobs SET status='queued', locked_by=NULL, locked_at=NULL,
payload=CAST(@payload AS jsonb) WHERE job_id=@job;
""", new { job = jobId, payload = JsonSerializer.Serialize(new { docId = doc, version = 1 }) });
        var first = await JobRepo.TryDequeueAsync(ds, "lease-test-A", CancellationToken.None);
        Assert.NotNull(first);
        await connection.ExecuteAsync("UPDATE ingestion_jobs SET locked_at=now()-interval '1 hour' WHERE job_id=@job", new { job = jobId });
        Assert.Equal(1, await JobRepo.RequeueStaleRunningAsync(ds, TimeSpan.FromMinutes(1), CancellationToken.None));
        var second = await JobRepo.TryDequeueAsync(ds, "lease-test-B", CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(first!.JobId, second!.JobId);

        const string text = "The operator must disconnect the instrument before maintenance and record the control measurement.";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var page = new ExtractedPdfPage(1, text, 17, text.Length, hash);
        var section = new ExtractedDocumentSection(0, "Control", 1, 1, 1, 1, null);
        var unit = new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 17, hash);
        var chunk = new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 17, hash, "unit_exact_v1");
        var quality = PdfExtractionQualitySummary.FromPages([page]);

        // This call belongs to the first dequeued attempt. The RED baseline has no ownership argument.
        var committed = publication switch
        {
            "upsert" => await JobRepo.CompleteUpsertAsync(ds, tenant, first.JobId, path, hash,
                text.Length, DateTime.UtcNow, 1, [page], [section], [unit], [chunk], [], [], CancellationToken.None, leaseOwner: first.LeaseOwner),
            "unsearchable" => await DocumentFoundationRepo.PublishUnsearchableRetrievalDiagnosticsAsync(ds,
                tenant, doc, first.JobId, path, hash, text.Length, DateTime.UtcNow, 1, [page], [unit], [],
                IngestionWorker.BuildRetrievalChunkQualitySummary([]), "pdf_text", false, false, null, null,
                null, quality, quality, "manual_review_no_searchable_chunks", CancellationToken.None, leaseOwner: first.LeaseOwner),
            "ocr_failure" => await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(ds,
                tenant, doc, first.JobId, path, hash, text.Length, DateTime.UtcNow, 1, "pdf_text", true,
                "eng", 10, null, quality, quality, "no_indexable_text", CancellationToken.None, [page], leaseOwner: first.LeaseOwner),
            "failed" => await TransitionAsync(() => JobRepo.MarkFailedAsync(ds, first.JobId, "old-attempt", CancellationToken.None, first.LeaseOwner)),
            "defer" => await TransitionAsync(() => JobRepo.DeferTransientAsync(ds, first.JobId, "old-attempt", TimeSpan.Zero, CancellationToken.None, first.LeaseOwner)),
            "cancel" => await TransitionAsync(() => JobRepo.MarkCanceledAsync(ds, first.JobId, "canceled_by_admin_old", CancellationToken.None,
                first.LeaseOwner, stabilizeDocument: true)),
            "superseded" => await JobRepo.MarkSupersededAndQueueCurrentAsync(ds, tenant, first.JobId, path, 1,
                CancellationToken.None, leaseOwner: first.LeaseOwner),
            "progress" => await RejectedAsync(() => JobRepo.UpdateProgressAsync(ds, first.JobId, "old-attempt", 1, 1,
                CancellationToken.None, first.LeaseOwner)),
            "checkpoint" => await RejectedAsync(() => JobRepo.StoreResumeCheckpointAsync(ds, first.JobId, "old-attempt", 1, 1,
                "model", "raw", CancellationToken.None, "old-attempt", first.LeaseOwner)),
            _ => await JobRepo.CompleteDeleteAsync(ds, tenant, first.JobId, path, 1, CancellationToken.None, leaseOwner: first.LeaseOwner)
        };
        Assert.False(committed);
        var state = await connection.QuerySingleAsync<(string Status, string? Owner, int Attempts)>(
            "SELECT status, locked_by AS Owner, attempts FROM ingestion_jobs WHERE job_id=@job", new { job = jobId });
        Assert.Equal("running", state.Status);
        Assert.Equal(second.LeaseOwner, state.Owner);
        Assert.StartsWith("lease-test-B:", state.Owner);
        Assert.Equal(3, state.Attempts);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc }));
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT auto_ingest_paused FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc }));
        Assert.DoesNotContain("old-attempt", await connection.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM ingestion_jobs WHERE job_id=@job", new { job = jobId }));

        static async Task<bool> TransitionAsync(Func<Task> change) { await change(); return false; }
        static async Task<bool> RejectedAsync(Func<Task> change)
        {
            await Assert.ThrowsAsync<IngestionLeaseLostException>(change);
            return false;
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Ingestion_heartbeat_zero_rows_cancels_and_observes_inflight_operation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        const string path = "Knowledge/Heartbeat-control.pdf";
        await db.SeedRunningJobAsync(tenant, doc, jobId, path, 1, 0);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new IngestionWorker(services, NullLogger<IngestionWorker>.Instance,
            new IngestionBulkheads(Options.Create(new IngestionOptions()), NullLogger<IngestionBulkheads>.Instance),
            new TeiWorkloadGovernor(), new IngestionJobCancellationRegistry());
        var canceled = false;
        var finished = false;
        var error = await Record.ExceptionAsync(() => worker.RunWithJobHeartbeatAsync(ds,
            new IngestionJob(jobId, tenant, "upsert", path, "knowledge", doc, 1, null), "test-worker",
            async operationToken =>
            {
                await using var connection = await ds.OpenConnectionAsync();
                await connection.ExecuteAsync("UPDATE ingestion_jobs SET locked_by='replacement-worker' WHERE job_id=@job", new { job = jobId });
                try { await Task.Delay(TimeSpan.FromMilliseconds(350), operationToken); }
                catch (OperationCanceledException) { canceled = true; throw; }
                finally { finished = true; }
                return true;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(15)));
        Assert.True(canceled, "Loss of the lease must cancel the in-flight operation.");
        Assert.True(finished, "The operation must be observed before the heartbeat wrapper returns.");
        Assert.NotNull(error);
        await using var check = await ds.OpenConnectionAsync();
        Assert.Equal("replacement-worker", await check.ExecuteScalarAsync<string>(
            "SELECT locked_by FROM ingestion_jobs WHERE job_id=@job", new { job = jobId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingestion_delete_or_cancel_never_reuses_an_abandoned_source_generation(bool cancel)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var job = Guid.NewGuid();
        const string path = "Knowledge/Generation-control.pdf";
        await db.SeedRunningJobAsync(tenant, doc, job, path, 7, 6, action: cancel ? "upsert" : "delete");
        await using var connection = await ds.OpenConnectionAsync();
        if (cancel)
        {
            await connection.ExecuteAsync("UPDATE ingestion_jobs SET payload='{" +
                "\"control\":{\"cancelRequested\":true}}'::jsonb WHERE job_id=@job", new { job });
            Assert.False(await JobRepo.CompleteUpsertAsync(ds, tenant, job, path, [1], 1, DateTime.UtcNow,
                7, [], [], [], [], [], [], CancellationToken.None, leaseOwner: "test-worker"));
        }
        else
        {
            Assert.True(await JobRepo.CompleteDeleteAsync(ds, tenant, job, path, 7, CancellationToken.None,
                leaseOwner: "test-worker"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc }));
        }
        Assert.Equal(7, await connection.ExecuteScalarAsync<int>(
            "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc", new { tenant, doc }));
        var next = await IngestionEnqueue.EnqueueUpsertAsync(connection, tenant, path, "knowledge", null, CancellationToken.None);
        Assert.Equal(8, next.Version);
        var acquired = await JobRepo.TryDequeueAsync(ds, "same-consumer", CancellationToken.None);
        Assert.NotNull(acquired);
        await JobRepo.DeferTransientAsync(ds, acquired!.JobId, "fixture", TimeSpan.Zero, CancellationToken.None,
            leaseOwner: acquired.LeaseOwner);
        await connection.ExecuteAsync("UPDATE ingestion_jobs SET available_at=now(), attempts=0 WHERE job_id=@job", new { job = acquired.JobId });
        var reacquired = await JobRepo.TryDequeueAsync(ds, "same-consumer", CancellationToken.None);
        Assert.NotNull(reacquired);
        Assert.NotEqual(acquired.LeaseOwner, reacquired!.LeaseOwner);
        Assert.StartsWith("same-consumer:", reacquired.LeaseOwner);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingestion_renewal_does_not_block_on_a_publication_row_lock_but_rejects_another_owner(bool reassigned)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var job = Guid.NewGuid();
        await db!.SeedRunningJobAsync(tenant, doc, job, "Knowledge/Publication-lock.pdf", 1, 0);
        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString) { CommandTimeout = 1 };
        await using var ds = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var holder = await ds.OpenConnectionAsync();
        if (reassigned)
            await holder.ExecuteAsync("UPDATE ingestion_jobs SET locked_by='successor-worker' WHERE job_id=@job", new { job });
        await using var publication = await holder.BeginTransactionAsync();
        var before = await holder.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT locked_at FROM ingestion_jobs WHERE job_id=@job FOR UPDATE", new { job }, transaction: publication));

        // Keep the publication lock held until renewal has returned. A second blocking UPDATE
        // deadlocks against our own transaction until its command timeout cancels the work.
        IngestionLeaseRenewal? renewal = null;
        var failure = await Record.ExceptionAsync(async () => { renewal = await IngestionLeaseGuard.RenewAsync(ds, job, "test-worker", CancellationToken.None); });
        if (reassigned)
            Assert.IsType<IngestionLeaseLostException>(failure);
        else
        {
            Assert.Null(failure);
            Assert.Equal(IngestionLeaseRenewal.OwnerObservedRenewalDeferred, renewal);
        }
        var after = await holder.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT locked_at FROM ingestion_jobs WHERE job_id=@job", new { job }, transaction: publication));
        Assert.Equal(before, after); // Ownership may be observed; the TTL was not renewed under the held lock.
        await publication.RollbackAsync();
        if (!reassigned)
        {
            Assert.Equal(IngestionLeaseRenewal.Renewed, await IngestionLeaseGuard.RenewAsync(ds, job, "test-worker", CancellationToken.None));
            Assert.True(await holder.ExecuteScalarAsync<DateTime>("SELECT locked_at FROM ingestion_jobs WHERE job_id=@job", new { job }) > before);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Ingestion_missing_or_empty_lease_cannot_mutate_progress_terminal_state_or_publication(string? owner)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var job = Guid.NewGuid();
        const string path = "Knowledge/Lease-admission.pdf";
        await db.SeedRunningJobAsync(tenant, doc, job, path, 1, 0);
        await using var connection = await ds.OpenConnectionAsync();
        const string snapshot = """
SELECT jsonb_build_object('job', to_jsonb(j), 'document', to_jsonb(d))::text
FROM ingestion_jobs j JOIN documents d ON d.tenant_id=j.tenant_id AND d.doc_path=j.doc_path WHERE j.job_id=@job;
""";
        var before = await connection.ExecuteScalarAsync<string>(snapshot, new { job });
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.MarkFailedAsync(ds, job, "unowned", CancellationToken.None, owner));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.DeferTransientAsync(ds, job, "unowned", TimeSpan.Zero, CancellationToken.None, owner));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.MarkCanceledAsync(ds, job, "canceled_by_admin_unowned", CancellationToken.None, owner, stabilizeDocument: true));
        Assert.False(await JobRepo.MarkSupersededAndQueueCurrentAsync(ds, tenant, job, path, 1,
            CancellationToken.None, leaseOwner: owner));
        Assert.False(await JobRepo.CompleteUpsertAsync(ds, tenant, job, path, [1], 1, DateTime.UtcNow, 1,
            [], [], [], [], [], [], CancellationToken.None, leaseOwner: owner));
        Assert.False(await JobRepo.CompleteDeleteAsync(ds, tenant, job, path, 1, CancellationToken.None, owner));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.UpdateProgressAsync(ds, job, "unowned", 1, 1,
            CancellationToken.None, owner));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => JobRepo.StoreResumeCheckpointAsync(ds, job,
            "source", 1, 1, "model", "raw", CancellationToken.None, leaseOwner: owner));
        await Assert.ThrowsAsync<IngestionLeaseLostException>(() => IngestionLeaseGuard.RenewAsync(ds, job, owner, CancellationToken.None));
        Assert.Equal(before, await connection.ExecuteScalarAsync<string>(snapshot, new { job }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revisions WHERE tenant_id=@tenant", new { tenant }));
    }
}
