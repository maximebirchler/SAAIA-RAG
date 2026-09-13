using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
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
