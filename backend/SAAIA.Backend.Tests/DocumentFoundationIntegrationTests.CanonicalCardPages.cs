using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_card_publication_and_resolution_preserve_context_pages_and_legacy_cards_stay_locators()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var tenant = Guid.NewGuid(); var docId = Guid.NewGuid(); var job = Guid.NewGuid();
        const string path = "Synthetic/Inspection.pdf";
        const string title = "Inspection requirements";
        const string text = "Record the measured pressure before opening the isolation valve.";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var pages = Enumerable.Range(1, 8).Select(page => new ExtractedPdfPage(page,
            page == 6 ? text : page == 2 ? title : string.Empty, 11, text.Length, hash)).ToArray();
        ExtractedDocumentSection[] sections = [new(0, title, 1, 2, 8, null, null)];
        ExtractedDocumentUnit[] units = [new(7, 0, 6, 6, text, text.Length, 11, hash)];
        ProjectedRetrievalChunk[] chunks = [new(7, 0, 7, 6, 6, text, 11, hash, "unit_exact_v1")];
        await db!.SeedRunningJobAsync(tenant, docId, job, path, 1, 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenant, job, path, hash, 500, DateTime.UtcNow,
            1, pages, sections, units, chunks, [], [], CancellationToken.None,
            structuredProfileUnits: units, leaseOwner: "test-worker"));
        await using var connection = await ds.OpenConnectionAsync();
        var card = await connection.QuerySingleAsync<(Guid Id, Guid Revision, int Start, int End)>("""
            SELECT content_card_id,revision_id,page_start,page_end FROM document_profile_content_cards
            WHERE tenant_id=@tenant AND doc_id=@docId;
            """, new { tenant, docId });
        Assert.Equal(2, card.Start);
        Assert.Equal(6, card.End);
        var resolver = new AdvancedAnalysisEvidenceResolver(ds, Options.Create(new AdvancedAnalysisOptions()));
        AdvancedAnalysisEvidenceReference Reference(int end) => new()
        {
            EvidenceId = "card-inspection", DocId = docId.ToString(), RevisionId = card.Revision.ToString(),
            ContentCardId = card.Id.ToString(), SourceHash = Convert.ToHexString(hash).ToLowerInvariant(),
            PageStart = 2, PageEnd = end
        };
        var result = await resolver.ResolveAsync(tenant, [Reference(6)], CancellationToken.None);
        Assert.True(result.IsValid);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(text, evidence.Content);
        Assert.Equal(2, evidence.Reference.PageStart);
        Assert.Equal(6, evidence.Reference.PageEnd);
        Assert.False((await resolver.ResolveAsync(Guid.NewGuid(), [Reference(6)], CancellationToken.None)).IsValid);

        // Reproduce an old published card: its metadata points to the real
        // page-six chunk, but its declared citation still ends on page two.
        await connection.ExecuteAsync("""
            UPDATE document_profile_content_cards SET page_end=2
            WHERE tenant_id=@tenant AND content_card_id=@Id;
            """, new { tenant, card.Id });
        var legacy = await resolver.ResolveAsync(tenant, [Reference(2)], CancellationToken.None);
        Assert.True(legacy.IsValid);
        var locator = Assert.Single(legacy.Evidence);
        Assert.Equal(title, locator.Content);
        Assert.Equal("navigation", RetrievalContentClassifier.AnalyzeEvidenceContent(locator.Content, title).ContentRole);
        Assert.False((await resolver.ResolveAsync(tenant, [Reference(6)], CancellationToken.None)).IsValid);
    }
}
