using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Working_set_jsonb_history_restores_committed_rotation_or_legacy_without_document_bodies(bool rotation)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its isolated PostgreSQL fixture.");
        var literal = "PRIVATE_WORKING_SET_DOCUMENT_BODY_SENTINEL";
        var seed = await database.SeedEvidenceAsync("working-set-persistence", literal + " first.");
        var chunkB = Guid.NewGuid();
        var chunkC = Guid.NewGuid();
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("""
                INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,
                  page_start,page_end,text_content,token_count)
                VALUES(@b,@tenant,@revision,1,3,3,@second,4),
                      (@c,@tenant,@revision,2,4,4,@third,4);
                """, new { b = chunkB, c = chunkC, tenant = seed.TenantId, revision = seed.RevisionId,
                    second = literal + " second.", third = literal + " third." });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(BuildAdvancedOptions()));
        var resolved = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString("D") },
             new AdvancedAnalysisEvidenceReference { ChunkId = chunkB.ToString("D") },
             new AdvancedAnalysisEvidenceReference { ChunkId = chunkC.ToString("D") }], CancellationToken.None);
        Assert.True(resolved.IsValid);
        Assert.Equal(3, resolved.Evidence.Count);
        var a = resolved.Evidence[0].Reference;
        var b = resolved.Evidence[1].Reference;
        var c = resolved.Evidence[2].Reference;
        var jobId = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        const string firstWorker = "working-set-before-retry";
        var lease = await store.TryClaimAsync(firstWorker, "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);

        async Task Append(string status, AdvancedAnalysisResultEvidence reference,
            AdvancedAnalysisEvidenceWorkingSet? snapshot)
        {
            Assert.NotNull(await store.TryAppendToolEventAsync(seed.TenantId, jobId, firstWorker,
                lease!.AttemptCount, 8, status,
                new AdvancedAnalysisSearchRequest("canonical lookup", EvidenceWorkingSet: snapshot),
                [reference], [], 1, status == "failed" ? "synthetic_refusal" : null, CancellationToken.None));
        }

        // The first event is deliberately legacy to qualify transition as well
        // as a fully legacy history. Audit results stay separate from snapshots.
        await Append("succeeded", a, null);
        await Append("succeeded", b, rotation ? WorkingSetSnapshot(2, a, b) : null);
        await Append("succeeded", c, rotation ? WorkingSetSnapshot(2, c, b) : null);
        await Append("failed", a, rotation ? WorkingSetSnapshot(2, a) : null);
        var history = await store.LoadToolHistoryAsync(seed.TenantId, jobId, CancellationToken.None);
        Assert.Equal(4, history.Count);
        Assert.Equal(b.EvidenceId, Assert.Single(history[1].Evidence).EvidenceId);
        Assert.Equal(c.EvidenceId, Assert.Single(history[2].Evidence).EvidenceId);

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            var requests = (await connection.QueryAsync<string>("""
                SELECT request::text FROM advanced_analysis_tool_events
                WHERE tenant_id=@tenant AND job_id=@job ORDER BY event_sequence;
                """, new { tenant = seed.TenantId, job = jobId })).ToArray();
            var evidenceRows = (await connection.QueryAsync<string>("""
                SELECT evidence_references::text FROM advanced_analysis_tool_events
                WHERE tenant_id=@tenant AND job_id=@job ORDER BY event_sequence;
                """, new { tenant = seed.TenantId, job = jobId })).ToArray();
            Assert.Equal(4, requests.Length);
            foreach (var value in requests.Concat(evidenceRows))
                Assert.DoesNotContain(literal, value, StringComparison.Ordinal);
            using var latestCommitted = JsonDocument.Parse(requests[2]);
            if (rotation)
            {
                var snapshot = latestCommitted.RootElement.GetProperty("evidenceWorkingSet");
                Assert.Equal(AdvancedAnalysisEvidenceWorkingSet.CurrentSchemaVersion,
                    snapshot.GetProperty("schemaVersion").GetString());
                Assert.Equal(2, snapshot.GetProperty("evidence").GetArrayLength());
            }
            else
                Assert.False(latestCommitted.RootElement.TryGetProperty("evidenceWorkingSet", out _));
        }

        await database.ExpireLeaseAsync(jobId, attemptCount: 1);
        Assert.Equal(1, await store.RecoverExpiredAsync(2, 0, CancellationToken.None));
        var resumedLease = await store.TryClaimAsync("working-set-after-retry", "fake", 30, CancellationToken.None);
        Assert.NotNull(resumedLease);
        Assert.Equal(2, resumedLease!.AttemptCount);
        var durableHistory = await store.LoadToolHistoryAsync(seed.TenantId, jobId, CancellationToken.None);
        var references = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([], durableHistory, rotation ? 2 : 3);
        Assert.Equal(rotation ? new[] { c.EvidenceId, b.EvidenceId } : new[] { a.EvidenceId, b.EvidenceId, c.EvidenceId },
            references.Select(item => item.EvidenceId));
        var revalidated = await resolver.ResolveAsync(seed.TenantId, references, CancellationToken.None);
        Assert.True(revalidated.IsValid);
        Assert.Equal(rotation ? 2 : 3, revalidated.Evidence.Count);
        if (rotation)
            Assert.DoesNotContain(revalidated.Evidence, item => item.Reference.EvidenceId == a.EvidenceId);
    }
}
