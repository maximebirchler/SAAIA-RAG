using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Fact]
    public async Task Research_checkpoint_survives_expired_lease_retry_and_remains_tenant_scoped()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync(
            "checkpoint-tenant",
            "PORRIDGE INGREDIENTS PREPARATION");
        var jobId = await database.InsertJobAsync(
            seed,
            BuildHandoff(seed, "porridge"));
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        Assert.NotNull(await store.TryClaimAsync(
            "checkpoint-worker-a",
            "fake",
            30,
            CancellationToken.None));
        var checkpoint = new AdvancedAnalysisResearchCheckpoint(
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion,
            [
                new AdvancedAnalysisCandidateCheckpoint(
                    "porridge",
                    "Porridge",
                    "internal-source-1",
                    ["petit-déjeuner"],
                    [],
                    "body_verified",
                    "Verified body retained.",
                    [],
                    ["E1"])
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{seed.DocId:D}|{seed.RevisionId:D}|sha256:test"] =
                    "internal-source-1"
            });

        Assert.True(await store.TrySaveResearchCheckpointAsync(
            seed.TenantId,
            jobId,
            "checkpoint-worker-a",
            checkpoint,
            CancellationToken.None));
        var first = await store.LoadResearchCheckpointAsync(
            seed.TenantId,
            jobId,
            "checkpoint-worker-a",
            CancellationToken.None);
        Assert.Equal("Porridge", Assert.Single(first!.Candidates).ExactTitle);
        Assert.Null(await store.LoadResearchCheckpointAsync(
            Guid.NewGuid(),
            jobId,
            "checkpoint-worker-a",
            CancellationToken.None));

        await database.ExpireLeaseAsync(jobId, attemptCount: 1);
        Assert.Equal(
            1,
            await store.RecoverExpiredAsync(2, 0, CancellationToken.None));
        Assert.NotNull(await store.TryClaimAsync(
            "checkpoint-worker-b",
            "fake",
            30,
            CancellationToken.None));
        var resumed = await store.LoadResearchCheckpointAsync(
            seed.TenantId,
            jobId,
            "checkpoint-worker-b",
            CancellationToken.None);

        Assert.Equal(
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion,
            resumed!.SchemaVersion);
        Assert.Equal("E1", Assert.Single(resumed.Candidates).BodyEvidenceIds[0]);
        Assert.Equal(
            "internal-source-1",
            Assert.Single(resumed.PromptSourceKeys).Value);
    }
}
