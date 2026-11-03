using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Endpoints;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Source_title_navigation_pages_the_observed_revision_and_never_admits_proof()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg selected without an isolated database.");
        var seed = await database.SeedEvidenceAsync("navigation-source", "Private source body.");
        var other = await database.SeedEvidenceAsync("navigation-other-tenant", "Other tenant body.");
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO document_title_anchors(title_anchor_id, tenant_id, revision_id, doc_id, anchor_index,
                source_kind, title, normalized_title, page_start, page_end)
            VALUES (@first, @tenant, @rev, @doc, 0, 'section', 'Safety check', 'safety check', 3, 3),
                   (@second, @tenant, @rev, @doc, 1, 'section', 'Replacement check', 'replacement check', 12, 13),
                   (@foreign, @otherTenant, @otherRev, @otherDoc, 0, 'section', 'Other tenant', 'other tenant', 1, 1);
            INSERT INTO document_navigation_entries(navigation_entry_id, tenant_id, revision_id, doc_id, entry_index,
                source_page, label, normalized_label, target_page_start, target_page_end)
            VALUES (@toc, @tenant, @rev, @doc, 0, 1, 'Safety check', 'safety check', 3, 3),
                   (@unresolved, @tenant, @rev, @doc, 1, 1, 'Unresolved heading', 'unresolved heading', NULL, NULL);
            INSERT INTO document_profiles(document_profile_id, tenant_id, revision_id, doc_id,
                summary_text, search_text, checksum)
            VALUES (@profile, @tenant, @rev, @doc, '', '', decode(repeat('a0', 32), 'hex'));
            INSERT INTO document_profile_content_cards(content_card_id, tenant_id, document_profile_id, revision_id, doc_id,
                profile_version, card_index, title, normalized_title, page_start, page_end, kind, search_text, checksum)
            VALUES (@card, @tenant, @profile, @rev, @doc, 'deterministic_v1', 0, 'Safety check', 'safety check', 3, 3,
                'section', 'PRIVATE CARD BODY', decode(repeat('a1', 32), 'hex')),
                   (@nonmatchingCard, @tenant, @profile, @rev, @doc, 'deterministic_v1', 1,
                    'Replacement check', 'replacement check', 14, 14, 'section', 'PRIVATE OTHER CARD BODY',
                    decode(repeat('a2', 32), 'hex'));
            """, new { first = Guid.NewGuid(), second = Guid.NewGuid(), foreign = Guid.NewGuid(),
            toc = Guid.NewGuid(), unresolved = Guid.NewGuid(), profile = Guid.NewGuid(), card = Guid.NewGuid(),
            nonmatchingCard = Guid.NewGuid(), tenant = seed.TenantId, rev = seed.RevisionId,
            doc = seed.DocId, otherTenant = other.TenantId, otherRev = other.RevisionId, otherDoc = other.DocId });
        var options = BuildAdvancedOptions();
        options.NativeResearchToolsEnabled = true;
        options.NativeSourceTitleNavigationEnabled = true;
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(options));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { DocId = seed.DocId.ToString(), ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var job = await database.InsertJobAsync(seed, BuildEmptyHandoff());
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        const string worker = "navigation-fixture";
        var lease = await store.TryClaimAsync(worker, "fake", 30, CancellationToken.None);
        Assert.NotNull(lease);
        var factory = new AdvancedAnalysisToolGatewayFactory(database.DataSource, rag, http, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, store, Options.Create(options));
        var gateway = factory.Create(job, seed.TenantId, worker, lease!.AttemptCount, initial.Evidence);
        var request = new AdvancedAnalysisSearchRequest(AdvancedAnalysisSourceTitlePage.Query, TopK: 2,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(),
            Operation: AdvancedAnalysisSourceTitlePage.Operation);
        var first = await gateway.SearchAsync(request, CancellationToken.None);
        Assert.Empty(first.Evidence);
        Assert.Equal(2, first.SourceTitlePage!.NextOffset);
        Assert.Collection(first.SourceTitlePage.Items,
            row => { Assert.Equal("Unresolved heading", row.Title); Assert.Equal(1, row.SourcePage); Assert.Null(row.PhysicalPageStart); },
            row => { Assert.Equal("Safety check", row.Title); Assert.Equal(3, row.PhysicalPageStart); Assert.Equal("title_anchor", row.Kind); });
        var second = await gateway.SearchAsync(request with { Offset = 2 }, CancellationToken.None);
        Assert.Empty(second.Evidence);
        Assert.Equal("Replacement check", Assert.Single(second.SourceTitlePage!.Items).Title);
        Assert.Null(second.SourceTitlePage.NextOffset);
        var backed = await gateway.SearchAsync(request with { CardBackedTitlesOnly = true }, CancellationToken.None);
        Assert.Equal("Safety check", Assert.Single(backed.SourceTitlePage!.Items).Title);
        var filtered = await gateway.SearchAsync(request with { TitleContains = "CHECK", PageStart = 10, PageEnd = 12 }, CancellationToken.None);
        Assert.Equal("Replacement check", Assert.Single(filtered.SourceTitlePage!.Items).Title);
        var absent = await gateway.SearchAsync(request with { TitleContains = "check%" }, CancellationToken.None);
        Assert.Empty(absent.SourceTitlePage!.Items);
        Assert.Single(gateway.Evidence);
        var durable = await connection.QueryAsync<(string ToolName, string Status, string Evidence)>("""
            SELECT tool_name, status, evidence_references::text
            FROM advanced_analysis_tool_events WHERE job_id=@job ORDER BY event_sequence
            """, new { job });
        Assert.Equal(5, durable.Count());
        Assert.All(durable, item =>
        {
            Assert.Equal("source_backed_canonical_navigation", item.ToolName);
            Assert.Equal("succeeded", item.Status);
            Assert.Equal("[]", item.Evidence);
        });
        Assert.Equal("canonical_title_navigation_scope_or_window_invalid",
            (await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
                request with { TopK = 21 }, CancellationToken.None))).ErrorCode);
        Assert.Equal("canonical_read_source_not_observed",
            (await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
                request with { DocId = other.DocId.ToString(), RevisionId = other.RevisionId.ToString(), DocPath = other.DocPath },
                CancellationToken.None))).ErrorCode);
        await connection.ExecuteAsync("UPDATE documents SET indexed_version=indexed_version+1 WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant = seed.TenantId, doc = seed.DocId });
        Assert.Equal("canonical_title_navigation_identity_changed",
            (await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
                request with { Offset = 10_000 }, CancellationToken.None))).ErrorCode);
    }
}
