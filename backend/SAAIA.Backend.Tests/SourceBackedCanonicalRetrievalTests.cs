using System.Text.RegularExpressions;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class SourceBackedCanonicalRetrievalTests
{
    [Fact]
    public void Canonical_sparse_channel_keeps_lexical_provenance_and_rrf_scores()
    {
        var lexical = Match(0.4, "Knowledge/guide.pdf", 1, "shared-chunk")
            with
        { EmbeddingBasis = "source_backed_sparse_fts_v1" };
        var dense = lexical with { EmbeddingBasis = "contextual_text_v1", Score = 0.9 };
        var other = Match(0.8, "Knowledge/other.pdf", 2, "other-chunk")
            with
        { EmbeddingBasis = "contextual_text_v1" };
        var legacyLexical = lexical with { EmbeddingBasis = "sparse_bm25_v1" };
        var expected = RetrievalSelection.FuseWithRrf([], [legacyLexical], [dense, other]);
        var actual = RetrievalSelection.FuseWithRrf([], [lexical], [dense, other]);
        Assert.Equal(expected.Select(match => (match.ChunkId, match.Score)), actual.Select(match => (match.ChunkId, match.Score)));
        Assert.Equal("sparse_bm25", RetrievalNavigation.ResolveRetriever(actual[0]));
        Assert.Equal("source_backed_sparse_fts_v1", actual[0].EmbeddingBasis);
    }

    [Fact]
    public void SelectCanonicalMatches_preserves_rank_and_applies_only_explicit_identity_and_quotas()
    {
        var navigation = Match(
            score: 0.95,
            docPath: "Knowledge/guide.pdf",
            page: 1,
            chunkId: "nav-1",
            chunkType: "navigation");
        var firstContent = Match(
            score: 0.90,
            docPath: "Knowledge/guide.pdf",
            page: 2,
            chunkId: "content-1");
        var duplicateIdentity = firstContent with { Score = 0.89 };
        var samePage = Match(
            score: 0.88,
            docPath: "Knowledge/guide.pdf",
            page: 2,
            chunkId: "content-2");
        var otherDocument = Match(
            score: 0.87,
            docPath: "Knowledge/other.pdf",
            page: 4,
            chunkId: "other-1");

        var selected = CanonicalRetrieval.SelectCanonicalMatches(
            [navigation, firstContent, duplicateIdentity, samePage, otherDocument],
            topK: 4,
            minScore: 0,
            maxPerDoc: 3,
            maxPerPage: 1);

        Assert.Equal(
            ["nav-1", "content-1", "other-1"],
            selected.Select(static match => match.ChunkId!).ToArray());
    }

    [Fact]
    public void SelectCanonicalMatches_does_not_infer_semantic_relevance_from_text_or_chunk_type()
    {
        var candidates = new[]
        {
            Match(
                score: 0.8,
                docPath: "Knowledge/a.pdf",
                page: 1,
                chunkId: "generic",
                text: "Table des matières",
                chunkType: "navigation"),
            Match(
                score: 0.7,
                docPath: "Knowledge/b.pdf",
                page: 3,
                chunkId: "specific",
                text: "A concrete procedure",
                chunkType: "content")
        };

        var selected = CanonicalRetrieval.SelectCanonicalMatches(
            candidates,
            topK: 2,
            minScore: 0,
            maxPerDoc: 2,
            maxPerPage: 2);

        Assert.Equal(
            ["generic", "specific"],
            selected.Select(static item => item.ChunkId!).ToArray());
    }

    [Fact]
    public void Canonical_retrieval_source_contains_no_legacy_semantic_route_predicates()
    {
        var calls = CompiledArchitectureContract.Calls(typeof(CanonicalRetrieval));
        Assert.DoesNotContain(calls, static call => call.Name.StartsWith("Should", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, static call => call.Name is "BuildAnswerGuidance" or "BuildSelectionHints"
            or "SearchSparseMatchesAsync");
        Assert.Contains(calls, static call => call.Name == "SearchSourceBackedCanonicalSparseMatchesAsync");
        var sparseSql = CompiledArchitectureContract.StringsForMethod(
            typeof(CanonicalRetrieval), "SearchSourceBackedCanonicalSparseMatchesAsync");
        // IncludeResearchSurfaces intentionally enriches selected results after
        // canonical ranking; the sparse channel itself must not expand cards.
        Assert.DoesNotContain(CompiledArchitectureContract.CallsForMethod(
            typeof(CanonicalRetrieval), "SearchSourceBackedCanonicalSparseMatchesAsync"),
            static call => call.Name is "AttachDocumentProfileContentCardsAsync" or "SearchSparseMatchesAsync");
        Assert.Contains(sparseSql, static text => Regex.IsMatch(text, @"cte\.search_tsv\s*@@\s*sparse_query\.q"));
        Assert.DoesNotContain(sparseSql, static text => text.Contains("document_profile_content_cards", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CompiledArchitectureContract.Calls(typeof(RetrievalEngine)),
            static call => call.Name == "SearchSourceBackedCanonicalCoreAsync");
    }

    private static RagMatch Match(
        double score,
        string docPath,
        int page,
        string chunkId,
        string text = "Evidence",
        string chunkType = "content")
        => new(
            Score: score,
            DocId: Guid.NewGuid().ToString(),
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: page,
            PageEnd: page,
            ChunkId: chunkId,
            ChunkIndex: page,
            Text: text,
            IngestionVersion: 1,
            HashDoc: null,
            EmbedText: text,
            EmbeddingBasis: "test",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

}
