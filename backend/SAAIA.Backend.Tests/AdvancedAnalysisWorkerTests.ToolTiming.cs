using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Failed_admitted_tool_time_is_charged_once_and_survives_resume()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its isolated PostgreSQL fixture.");
        var seed = await database.SeedEvidenceAsync("failed-tool-time", "Inspection is required.");
        var jobId = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var settings = BuildAdvancedOptions();
        var advanced = Options.Create(settings);
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, advanced);
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new SAAIA.Contracts.AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        var rag = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var lease = await store.TryClaimAsync("failed-tool-time-worker", "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);
        var factory = new AdvancedAnalysisToolGatewayFactory(database.DataSource, rag, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, store, advanced);
        var gateway = factory.Create(jobId, seed.TenantId, "failed-tool-time-worker", lease!.AttemptCount, initial.Evidence);
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await blocker.ExecuteAsync("UPDATE document_revisions SET source_hash=@hash WHERE revision_id=@revision;",
            new { hash = new byte[32], revision = seed.RevisionId });
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("LOCK TABLE documents IN ACCESS EXCLUSIVE MODE;", transaction: transaction);
        var read = new AdvancedAnalysisSearchRequest("physical pages", TopK: 5,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, PageStart: 2, PageEnd: 2,
            Operation: "read_source", RevisionId: seed.RevisionId.ToString());
        var pending = gateway.SearchAsync(read, CancellationToken.None);
        try
        {
            await using var monitor = await database.DataSource.OpenConnectionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await monitor.ExecuteScalarAsync<int>(new CommandDefinition("""
                       SELECT count(*)::int FROM pg_stat_activity WHERE datname=current_database()
                         AND state='active' AND wait_event_type='Lock' AND pid<>pg_backend_pid();
                       """, cancellationToken: timeout.Token)) < 1)
                await Task.Delay(10, timeout.Token);
            // The barrier proves that this admitted call spent measurable time
            // waiting in its canonical read before its identity check fails.
            await Task.Delay(25, timeout.Token);
        }
        finally { await transaction.RollbackAsync(); }
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => pending);
        Assert.Equal("canonical_read_source_identity_changed", error.ErrorCode);
        var history = await store.LoadToolHistoryAsync(seed.TenantId, jobId, CancellationToken.None);
        var failed = Assert.Single(history);
        Assert.Equal("failed", failed.Status);
        Assert.True(failed.ElapsedMilliseconds > 0);
        Assert.Equal(failed.ElapsedMilliseconds, gateway.Budget!.ConsumedElapsedMilliseconds);
        Assert.Equal(1, gateway.Budget.ConsumedCalls);
        var resumed = factory.Create(jobId, seed.TenantId, "failed-tool-time-worker", lease.AttemptCount,
            initial.Evidence, history);
        Assert.Equal(gateway.Budget.ConsumedElapsedMilliseconds, resumed.Budget!.ConsumedElapsedMilliseconds);
        Assert.Equal(gateway.Budget.ConsumedCalls, resumed.Budget.ConsumedCalls);
    }
}
