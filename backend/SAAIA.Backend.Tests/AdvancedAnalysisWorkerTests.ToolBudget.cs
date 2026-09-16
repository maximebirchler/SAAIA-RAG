using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Fact]
    public async Task Canonical_read_limit_is_atomic_correctable_and_does_not_hide_changed_identity()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("window-kept", "windowkept first documented section.");
        const string secondContent = "windowkept second documented section.";
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("""
                INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,
                  page_start,page_end,text_content,token_count)
                VALUES(@chunk,@tenant,@revision,1,3,3,@content,4);
                INSERT INTO contextual_text_entries(contextual_text_entry_id,tenant_id,revision_id,
                  retrieval_chunk_id,entry_index,page_start,page_end,text_content,char_count,token_count,checksum)
                VALUES(@entry,@tenant,@revision,@chunk,1,3,3,@content,length(@content),4,@checksum);
                """, new { chunk = Guid.NewGuid(), tenant = seed.TenantId, revision = seed.RevisionId,
                    content = secondContent, entry = Guid.NewGuid(),
                    checksum = SHA256.HashData(Encoding.UTF8.GetBytes(secondContent)) });

        var jobId = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var settings = BuildAdvancedOptions();
        var advanced = Options.Create(settings);
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, advanced);
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var lease = await store.TryClaimAsync("read-window-worker", "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);
        var factory = new AdvancedAnalysisToolGatewayFactory(database.DataSource, rag, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, store, advanced);
        var gateway = factory.Create(jobId, seed.TenantId, "read-window-worker", lease!.AttemptCount, initial.Evidence);
        var request = new AdvancedAnalysisSearchRequest("windowkept", TopK: 1,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(),
            PageStart: 2, PageEnd: 3, Operation: "read_source");

        var refused = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => gateway.SearchAsync(request, CancellationToken.None));
        Assert.Equal("canonical_read_window_result_limit_exceeded", refused.ErrorCode);
        Assert.NotNull(refused.ResourceLimit);
        Assert.Equal(1, refused.ResourceLimit!.RequestedTopK);
        Assert.Equal(2, refused.ResourceLimit.ObservedAtLeastChunkCount);
        Assert.False(refused.ResourceLimit.StopsResearch);
        Assert.Single(gateway.Evidence);
        var events = await database.ReadToolEventsAsync(jobId);
        Assert.Single(events);
        Assert.Equal("failed", events[0].Status);
        Assert.Equal("[]", events[0].EvidenceJson);

        var corrected = await gateway.SearchAsync(request with { TopK = 2 }, CancellationToken.None);
        Assert.Equal(2, corrected.Evidence.Count);
        Assert.Equal(2, gateway.Evidence.Count);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE document_revisions SET source_hash=@hash WHERE revision_id=@revision;",
                new { hash = new byte[32], revision = seed.RevisionId });
        var changed = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => gateway.SearchAsync(request, CancellationToken.None));
        Assert.Equal("canonical_read_source_identity_changed", changed.ErrorCode);
        Assert.Null(changed.ResourceLimit);
        Assert.Equal(2, gateway.Evidence.Count);
        events = await database.ReadToolEventsAsync(jobId);
        Assert.Equal(new[] { "failed", "succeeded", "failed" }, events.Select(e => e.Status));
        Assert.Equal("[]", events[2].EvidenceJson);
    }

    [Fact]
    public async Task Evidence_capacity_rejection_preserves_admitted_corpus_and_failed_durable_trace()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("capacity-kept", "capacityretained inspection is documented.");
        var extra = await database.SeedAdditionalEvidenceAsync(seed, "capacity-extra", "capacitynew replacement is documented.");
        var jobId = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var settings = BuildAdvancedOptions();
        settings.MaximumAccumulatedEvidenceItems = 1;
        var advanced = Options.Create(settings);
        var rag = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var lease = await store.TryClaimAsync("capacity-worker", "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);
        var factory = new AdvancedAnalysisToolGatewayFactory(database.DataSource, rag, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance),
            new AdvancedAnalysisEvidenceResolver(database.DataSource, advanced), store, advanced);
        var gateway = factory.Create(jobId, seed.TenantId, "capacity-worker", lease!.AttemptCount, []);
        var kept = await gateway.SearchAsync(new("capacityretained", TopK: 5, DocId: seed.DocId.ToString()), CancellationToken.None);
        Assert.Single(kept.Evidence);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
            new("capacitynew", TopK: 5, DocId: extra.DocId.ToString()), CancellationToken.None));
        Assert.Equal("accumulated_evidence_limit_exceeded", error.ErrorCode);
        Assert.NotNull(error.ResourceLimit);
        Assert.Equal(1, error.ResourceLimit!.MaximumEvidenceItems);
        Assert.Equal(1, error.ResourceLimit.ConsumedEvidenceItems);
        Assert.Equal(1, error.ResourceLimit.RequestedNewEvidenceItems);
        Assert.Equal(1, gateway.Budget!.MaximumEvidenceItems);
        Assert.Equal(1, gateway.Budget.ConsumedEvidenceItems);
        Assert.Equal(0, gateway.Budget.RemainingEvidenceItems);
        Assert.Equal(kept.Evidence[0].Reference.EvidenceId, Assert.Single(gateway.Evidence).Reference.EvidenceId);
        var events = await database.ReadToolEventsAsync(jobId);
        Assert.Equal(2, events.Count);
        Assert.Equal("succeeded", events[0].Status);
        Assert.Equal("failed", events[1].Status);
        Assert.Equal("[]", events[1].EvidenceJson);
    }

    [Fact]
    public async Task Unadmitted_tool_call_preserves_limit_error_and_existing_durable_events()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("quota-frontier", "frontierquota inspection is documented.");
        var jobId = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var settings = BuildAdvancedOptions();
        settings.MaximumToolCalls = 2;
        var advanced = Options.Create(settings);
        var rag = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var lease = await store.TryClaimAsync("quota-worker", "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);
        var factory = new AdvancedAnalysisToolGatewayFactory(
            database.DataSource, rag, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance),
            new AdvancedAnalysisEvidenceResolver(database.DataSource, advanced), store, advanced);
        var gateway = factory.Create(jobId, seed.TenantId, "quota-worker", lease!.AttemptCount, []);
        Assert.Equal(2, gateway.Budget!.RemainingCalls);
        var query = new AdvancedAnalysisSearchRequest("frontierquota", TopK: 5);
        Assert.NotEmpty((await gateway.SearchAsync(query, CancellationToken.None)).Evidence);
        Assert.Equal(1, gateway.Budget!.RemainingCalls);
        Assert.NotEmpty((await gateway.SearchAsync(query, CancellationToken.None)).Evidence);
        Assert.Equal(0, gateway.Budget!.RemainingCalls);
        var exhausted = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => gateway.SearchAsync(query, CancellationToken.None));
        Assert.Equal("tool_call_limit_exceeded", exhausted.ErrorCode);
        Assert.Equal(2, await database.CountToolEventsAsync(jobId));

        var history = await store.LoadToolHistoryAsync(seed.TenantId, jobId, CancellationToken.None);
        await database.ExpireLeaseAsync(jobId, attemptCount: 1);
        Assert.Equal(1, await store.RecoverExpiredAsync(2, 0, CancellationToken.None));
        var nextLease = await store.TryClaimAsync("quota-retry", "fake", 30, CancellationToken.None);
        Assert.NotNull(nextLease);
        var resumed = factory.Create(jobId, seed.TenantId, "quota-retry", nextLease!.AttemptCount,
            gateway.Evidence, history);
        Assert.Equal(2, resumed.Budget!.ConsumedCalls);
        Assert.Equal(0, resumed.Budget.RemainingCalls);
        Assert.Equal(history.Sum(e => e.ElapsedMilliseconds), resumed.Budget.ConsumedElapsedMilliseconds);
        var resumedLimit = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => resumed.SearchAsync(query, CancellationToken.None));
        Assert.Equal("tool_call_limit_exceeded", resumedLimit.ErrorCode);
        Assert.Equal(2, await database.CountToolEventsAsync(jobId));
    }
}
