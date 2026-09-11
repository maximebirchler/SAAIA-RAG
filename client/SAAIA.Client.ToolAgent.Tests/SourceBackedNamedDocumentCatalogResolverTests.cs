using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedNamedDocumentCatalogResolverTests
{
    [Fact]
    public async Task Exact_file_name_is_resolved_from_complete_current_catalog()
    {
        var catalog = new DelegateCatalogClient((_, limit, offset, _) =>
            Task.FromResult(Page(
                offset,
                limit,
                total: 2,
                endOfList: true,
                Candidate("doc-neighbor", "Operations/HX-42 Guide.pdf"),
                Candidate("doc-target", "Operations/Service Bulletin HX-42.pdf"))));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("service bulletin hx-42.PDF", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Equal("doc-target", Assert.Single(result.Candidates).DocId);
        Assert.Equal("resolved_exact_current_catalog", result.ReasonCode);
    }

    [Fact]
    public async Task Extensionless_exact_name_is_resolved_without_fuzzy_matching()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-target", "Operations/Service Bulletin HX-42.pdf"),
            Candidate("doc-fuzzy", "Operations/Service Bulletins HX-420.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-target", Assert.Single(result.Candidates).DocId);
    }

    [Fact]
    public async Task Unique_extensionless_identity_prefix_is_resolved()
    {
        var catalog = SinglePageCatalog(
            Candidate(
                "doc-target",
                "Standards/FD CEN TR 15281 2023 Potentially explosive atmospheres.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("FD CEN TR 15281 2023", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-target", Assert.Single(result.Candidates).DocId);
        Assert.Equal(
            "resolved_unique_identity_prefix_current_catalog",
            result.ReasonCode);
    }

    [Fact]
    public async Task Exact_identity_has_priority_over_longer_identity_prefixes()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-exact", "Standards/FD CEN TR 15281 2023.pdf"),
            Candidate(
                "doc-longer",
                "Standards/FD CEN TR 15281 2023 Potentially explosive atmospheres.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("FD CEN TR 15281 2023", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-exact", Assert.Single(result.Candidates).DocId);
        Assert.Equal("resolved_exact_current_catalog", result.ReasonCode);
    }

    [Fact]
    public async Task Multiple_extensionless_identity_prefixes_are_ambiguous()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-a", "Standards/ISO 9001 2015.pdf"),
            Candidate("doc-b", "Standards/ISO 9001 2026.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("ISO 9001", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(
            "multiple_identity_prefix_current_catalog_matches",
            result.ReasonCode);
    }

    [Fact]
    public async Task Identity_prefix_requires_a_lexical_boundary()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-neighbor", "Standards/ISO 90010 Guide.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("ISO 9001", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task File_name_with_extension_does_not_use_identity_prefix()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-longer", "Standards/ISO 9001 Annotated.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("ISO 9001.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Unique_formal_numeric_designator_resolves_an_extensionless_reference()
    {
        var catalog = SinglePageCatalog(
            Candidate(
                "doc-target",
                "Standards/FD CEN TR 15281 2023 Inerting guidance.pdf"),
            Candidate("doc-other", "Standards/ISO 13850 2015.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("norme EN 15281", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-target", Assert.Single(result.Candidates).DocId);
        Assert.Equal(
            "resolved_unique_formal_designator_current_catalog",
            result.ReasonCode);
    }

    [Fact]
    public async Task Formal_designator_is_sent_as_a_catalog_query()
    {
        var seenQueries = new List<string>();
        var catalog = new DelegateCatalogClient((query, limit, offset, _) =>
        {
            seenQueries.Add(query);
            var candidates = query == "15281"
                ? new[]
                {
                    Candidate(
                        "doc-target",
                        "Standards/CEN TR 15281 Guide.pdf")
                }
                : Array.Empty<SourceBackedDocumentResolutionCandidate>();
            return Task.FromResult(Page(
                offset,
                limit,
                candidates.Length,
                endOfList: true,
                candidates));
        });

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("norme EN 15281", CancellationToken.None);

        Assert.Contains("15281", seenQueries);
        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-target", Assert.Single(result.Candidates).DocId);
    }

    [Fact]
    public async Task Multiple_documents_with_same_formal_designator_are_ambiguous()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-a", "Standards/CEN TR 15281 Guide.pdf"),
            Candidate("doc-b", "Archive/CEN TR 15281 Commentary.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("norme EN 15281", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(
            "multiple_formal_designator_current_catalog_matches",
            result.ReasonCode);
    }

    [Theory]
    [InlineData("norme EN 2023", "Standards/EN 2023 Commentary.pdf")]
    [InlineData("15281", "Standards/CEN TR 15281 Guide.pdf")]
    [InlineData("norme EN 15281.pdf", "Standards/CEN TR 15281 Guide.pdf")]
    [InlineData("norme EN 15281", "Standards/CEN TR 152810 Guide.pdf")]
    [InlineData("norme EN 15281", "Standards/CEN TR 15281-1 Guide.pdf")]
    public async Task Formal_designator_guard_rejects_unsafe_matches(
        string requested,
        string candidatePath)
    {
        var result = await new SourceBackedNamedDocumentResolver(
                SinglePageCatalog(Candidate("doc-neighbor", candidatePath)))
            .ResolveAsync(requested, CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Complete_catalog_without_exact_match_is_not_found()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-neighbor", "Operations/Service Bulletins HX-420.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound, result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Exact_homonyms_in_distinct_paths_are_ambiguous()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-a", "Plant-A/Service Bulletin HX-42.pdf"),
            Candidate("doc-b", "Plant-B/Service Bulletin HX-42.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous, result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task Full_path_disambiguates_exact_homonyms()
    {
        var catalog = SinglePageCatalog(
            Candidate("doc-a", "Plant-A/Service Bulletin HX-42.pdf"),
            Candidate("doc-b", "Plant-B/Service Bulletin HX-42.pdf"));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync(
                "Plant-B\\Service Bulletin HX-42.pdf",
                CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Equal("doc-b", Assert.Single(result.Candidates).DocId);
    }

    [Fact]
    public async Task Timeout_is_inconclusive_and_never_not_found()
    {
        var catalog = new DelegateCatalogClient((_, _, _, _) =>
            Task.FromException<SourceBackedDocumentCatalogPage>(
                new TaskCanceledException("synthetic timeout")));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Inconclusive, result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_observation_failed", result.ReasonCode);
        Assert.Equal(nameof(TaskCanceledException), result.TechnicalError);
    }

    [Fact]
    public async Task Non_advancing_pagination_is_inconclusive()
    {
        var catalog = new DelegateCatalogClient((_, limit, offset, _) =>
            Task.FromResult(Page(offset, limit, total: null, endOfList: false)));

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Inconclusive, result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_pagination_did_not_advance", result.ReasonCode);
    }

    [Fact]
    public async Task Duplicate_rows_for_same_doc_id_do_not_create_false_ambiguity()
    {
        var duplicate = Candidate(
            "doc-target",
            "Operations/Service Bulletin HX-42.pdf");
        var catalog = SinglePageCatalog(duplicate, duplicate);

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task Ambiguous_candidate_payload_is_bounded_without_losing_exact_count()
    {
        var candidates = Enumerable.Range(1, 25)
            .Select(index => Candidate(
                "doc-" + index,
                "Plant-" + index + "/Service Bulletin HX-42.pdf"))
            .ToArray();
        var catalog = SinglePageCatalog(candidates);

        var result = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync("Service Bulletin HX-42.pdf", CancellationToken.None);

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(25, result.ExactMatchCount);
        Assert.Equal(20, result.Candidates.Count);
    }

    private static ISourceBackedNamedDocumentCatalogClient SinglePageCatalog(
        params SourceBackedDocumentResolutionCandidate[] candidates)
        => new DelegateCatalogClient((_, limit, offset, _) =>
            Task.FromResult(Page(
                offset,
                limit,
                candidates.Length,
                endOfList: true,
                candidates)));

    private static SourceBackedDocumentCatalogPage Page(
        int offset,
        int limit,
        int? total,
        bool endOfList,
        params SourceBackedDocumentResolutionCandidate[] candidates)
        => new(candidates, offset, limit, total, endOfList);

    private static SourceBackedDocumentResolutionCandidate Candidate(
        string docId,
        string docPath)
        => new(
            docId,
            docPath,
            docPath.Replace('\\', '/').Split('/').Last(),
            RevisionId: "rev-current",
            SourceHash: new string('b', 64));

    private sealed class DelegateCatalogClient(
        Func<string, int, int, CancellationToken,
            Task<SourceBackedDocumentCatalogPage>> handler)
        : ISourceBackedNamedDocumentCatalogClient
    {
        public Task<SourceBackedDocumentCatalogPage> SearchAsync(
            string query,
            int limit,
            int offset,
            CancellationToken ct)
            => handler(query, limit, offset, ct);
    }
}
