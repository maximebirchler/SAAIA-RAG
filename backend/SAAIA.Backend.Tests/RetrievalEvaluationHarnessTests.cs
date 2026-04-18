using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalEvaluationHarnessTests
{
    [Fact]
    public void Retrieval_v3_exact_lookup_harness_reports_recall_and_low_noise()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v1.json");
        var exactMetrics = EvaluateExactLookup(corpus.ExactPositiveCases, corpus.ExactNegativeQueries);

        Assert.True(exactMetrics.Recall >= 1.0, $"exact recall too low: {exactMetrics.Recall:P}");
        Assert.True(exactMetrics.NoiseRate <= 0.0, $"exact lookup noise too high: {exactMetrics.NoiseRate:P}");
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_dominant_retrievers_on_corpus_v2()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v2.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<DominantRetrieverCase>>(corpus.DominantRetrieverCases);

        foreach (var testCase in cases)
        {
            var extracted = ExactMatchEntryExtractor.ExtractLookupTerms(testCase.Query);
            var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(testCase.Query);
            var targetedTerms = extracted
                .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
                .ToArray();

            if (string.Equals(testCase.ExpectedDominantRetriever, "exact_match", StringComparison.Ordinal))
            {
                Assert.NotEmpty(targetedTerms);
                foreach (var expected in testCase.ExpectedExactTerms)
                    Assert.Contains(expected, targetedTerms, StringComparer.Ordinal);
                if (!testCase.AllowExactNoise)
                    Assert.Equal(testCase.ExpectedExactTerms.Count, targetedTerms.Length);
            }
            else if (string.Equals(testCase.ExpectedDominantRetriever, "dense_qdrant", StringComparison.Ordinal))
            {
                Assert.Empty(targetedTerms);
                Assert.False(testCase.AllowExactNoise);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported expectedDominantRetriever '{testCase.ExpectedDominantRetriever}'.");
            }
        }
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_linked_context_behavior_on_corpus_v2()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v2.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<LinkedContextCase>>(corpus.LinkedContextCases);

        foreach (var testCase in cases)
        {
            var linkedScore = RagEndpoints.ComputeLinkedMatchScore(
                testCase.AnchorScore,
                testCase.LinkType,
                testCase.AnchorRetriever);

            Assert.True(linkedScore < testCase.AnchorScore, $"{testCase.Name}: linked score should stay below anchor score.");

            if (testCase.ExpectedShouldHelp)
            {
                Assert.True(linkedScore >= 0.75, $"{testCase.Name}: linked score should remain strong enough to help.");
            }
            else
            {
                Assert.True(linkedScore < 0.60, $"{testCase.Name}: linked score should remain secondary.");
            }
        }
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_dominant_retrievers_on_corpus_v3()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v3.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<DominantRetrieverCase>>(corpus.DominantRetrieverCases);

        Assert.True(cases.Count >= 7, "corpus v3 should carry a richer dominant-retriever coverage.");

        foreach (var testCase in cases)
            AssertDominantRetrieverCase(testCase);
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_linked_context_behavior_on_corpus_v3()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v3.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<LinkedContextCase>>(corpus.LinkedContextCases);

        Assert.True(cases.Count >= 4, "corpus v3 should carry richer linked-context coverage.");

        foreach (var testCase in cases)
            AssertLinkedContextCase(testCase);
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_channel_ordering_on_corpus_v3()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v3.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<RankingExpectationCase>>(corpus.RankingExpectationCases);

        Assert.NotEmpty(cases);

        foreach (var testCase in cases)
        {
            var exactScore = RagEndpoints.ComputeExactMatchScore(testCase.ExactKind, testCase.MatchedTerm, testCase.ExactText);
            var denseRanked = RagEndpoints.RerankDenseMatches([
                new RagMatch(testCase.DenseLegacyScore, "doc", "ATEX/CEN.pdf", "CEN.pdf", 2, 2, "dense-legacy", 1, "legacy", 1, "hash", "legacy", "chunk_text", 1, 1, "Safety", null, "legacy_word_window_v1", null, null, null),
                new RagMatch(testCase.DensePreciseScore, "doc", "ATEX/CEN.pdf", "CEN.pdf", 2, 2, "dense-precise", 2, "precise", 1, "hash", "context", "contextual_text_v1", 1, 2, "Safety", "Chapter 1 > Safety", "unit_exact_v1", null, null, null)
            ]);
            var linkedScore = RagEndpoints.ComputeLinkedMatchScore(denseRanked[0].Score, testCase.LinkedType, "dense_qdrant");

            var orderedChannels = new[]
            {
                ("exact_match", exactScore),
                ("dense_qdrant", denseRanked[0].Score),
                ("linked_context", linkedScore)
            }
            .OrderByDescending(item => item.Item2)
            .Select(item => item.Item1)
            .ToArray();

            Assert.Equal(testCase.ExpectedOrder, orderedChannels);
        }
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_business_decisions_on_corpus_v3()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v3.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<DecisionCase>>(corpus.DecisionCases);

        Assert.True(cases.Count >= 3, "corpus v3 should include business-oriented decision cases.");

        foreach (var testCase in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedDocHint));
            AssertDominantRetrieverCase(new DominantRetrieverCase(
                testCase.Query,
                testCase.ExpectedDominantRetriever,
                testCase.ExpectedExactTerms,
                testCase.AllowExactNoise,
                testCase.ExpectedLexicalTerms));

            var linkedScore = RagEndpoints.ComputeLinkedMatchScore(0.82, "same_section", "dense_qdrant");
            var linkedFromLinkedScore = RagEndpoints.ComputeLinkedMatchScore(0.82, "same_section", "linked_context");

            if (string.Equals(testCase.LinkedExpectation, "helpful", StringComparison.Ordinal))
            {
                Assert.True(linkedScore >= 0.75);
            }
            else if (string.Equals(testCase.LinkedExpectation, "secondary", StringComparison.Ordinal))
            {
                Assert.True(linkedFromLinkedScore < linkedScore);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported linkedExpectation '{testCase.LinkedExpectation}'.");
            }
        }
    }

    [Fact]
    public void Retrieval_v3_decision_harness_reports_expected_fusion_calibration_on_corpus_v3()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v3.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<CalibrationCase>>(corpus.CalibrationCases);

        Assert.True(cases.Count >= 3, "corpus v3 should include calibration cases for sparse and rerank behavior.");

        foreach (var testCase in cases)
        {
            var candidates = testCase.Candidates
                .Select(candidate => new RagMatch(
                    Score: candidate.BaseScore,
                    DocId: candidate.DocId,
                    DocPath: candidate.DocPath,
                    DocName: candidate.DocName,
                    PageStart: candidate.PageStart,
                    PageEnd: candidate.PageEnd,
                    ChunkId: candidate.ChunkId,
                    ChunkIndex: candidate.ChunkIndex,
                    Text: candidate.Text,
                    IngestionVersion: 1,
                    HashDoc: candidate.HashDoc,
                    EmbedText: candidate.EmbedText,
                    EmbeddingBasis: candidate.EmbeddingBasis,
                    SectionOrdinal: candidate.SectionOrdinal,
                    UnitOrdinal: candidate.UnitOrdinal,
                    SectionTitle: candidate.SectionTitle,
                    HeadingPath: candidate.HeadingPath,
                    ChunkType: candidate.ChunkType,
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null))
                .ToArray();

            var calibrated = RagEndpoints.CalibrateFusedMatches(testCase.Query, candidates);
            var final = testCase.RerankItems is { Count: > 0 }
                ? RagEndpoints.ApplyRerankScores(
                    calibrated,
                    testCase.RerankItems.Select(item => new TeiClient.RerankItem(item.Index, item.Score)).ToArray(),
                    Math.Min(testCase.RerankPrefixCount ?? calibrated.Count, calibrated.Count))
                : calibrated;

            var top = final[0];
            Assert.Equal(testCase.ExpectedTopChunkId, top.ChunkId);
            Assert.Equal(testCase.ExpectedTopRetriever, ResolveRetriever(top));
            if (!string.IsNullOrWhiteSpace(testCase.ExpectedDocHint))
            {
                var docText = $"{top.DocName} {top.DocPath}";
                Assert.Contains(testCase.ExpectedDocHint, docText, StringComparison.OrdinalIgnoreCase);
            }

            if (testCase.ExpectRerankPromotion)
                Assert.True(top.RerankScore.HasValue, $"{testCase.Name}: expected rerank to materially promote the top candidate.");
        }
    }

    [Fact]
    public void Retrieval_v4_exact_lookup_harness_reports_recall_and_low_noise()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v4.json");
        var exactMetrics = EvaluateExactLookup(corpus.ExactPositiveCases, corpus.ExactNegativeQueries);

        Assert.True(exactMetrics.Recall >= 1.0, $"exact recall too low: {exactMetrics.Recall:P}");
        Assert.True(exactMetrics.NoiseRate <= 0.0, $"exact lookup noise too high: {exactMetrics.NoiseRate:P}");
    }

    [Fact]
    public void Retrieval_v4_decision_harness_reports_expected_dominant_retrievers()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v4.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<DominantRetrieverCase>>(corpus.DominantRetrieverCases);

        Assert.True(cases.Count >= 8, "corpus v4 should carry richer conversational dominant-retriever coverage.");

        foreach (var testCase in cases)
            AssertDominantRetrieverCase(testCase);
    }

    [Fact]
    public void Retrieval_v4_decision_harness_reports_expected_business_decisions()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v4.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<DecisionCase>>(corpus.DecisionCases);

        Assert.True(cases.Count >= 8, "corpus v4 should include richer conversational decision coverage.");

        foreach (var testCase in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedDocHint));
            AssertDominantRetrieverCase(new DominantRetrieverCase(
                testCase.Query,
                testCase.ExpectedDominantRetriever,
                testCase.ExpectedExactTerms,
                testCase.AllowExactNoise,
                testCase.ExpectedLexicalTerms));

            var linkedScore = RagEndpoints.ComputeLinkedMatchScore(0.82, "same_section", "dense_qdrant");
            var linkedFromLinkedScore = RagEndpoints.ComputeLinkedMatchScore(0.82, "same_section", "linked_context");

            if (string.Equals(testCase.LinkedExpectation, "helpful", StringComparison.Ordinal))
            {
                Assert.True(linkedScore >= 0.75);
            }
            else if (string.Equals(testCase.LinkedExpectation, "secondary", StringComparison.Ordinal))
            {
                Assert.True(linkedFromLinkedScore < linkedScore);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported linkedExpectation '{testCase.LinkedExpectation}'.");
            }
        }
    }

    [Fact]
    public void Retrieval_v4_decision_harness_reports_expected_fusion_calibration()
    {
        var corpus = LoadCorpus("retrieval_eval_corpus.v4.json");
        var cases = Assert.IsAssignableFrom<IReadOnlyList<CalibrationCase>>(corpus.CalibrationCases);

        Assert.True(cases.Count >= 4, "corpus v4 should include conversational calibration cases.");

        foreach (var testCase in cases)
        {
            var candidates = testCase.Candidates
                .Select(candidate => new RagMatch(
                    Score: candidate.BaseScore,
                    DocId: candidate.DocId,
                    DocPath: candidate.DocPath,
                    DocName: candidate.DocName,
                    PageStart: candidate.PageStart,
                    PageEnd: candidate.PageEnd,
                    ChunkId: candidate.ChunkId,
                    ChunkIndex: candidate.ChunkIndex,
                    Text: candidate.Text,
                    IngestionVersion: 1,
                    HashDoc: candidate.HashDoc,
                    EmbedText: candidate.EmbedText,
                    EmbeddingBasis: candidate.EmbeddingBasis,
                    SectionOrdinal: candidate.SectionOrdinal,
                    UnitOrdinal: candidate.UnitOrdinal,
                    SectionTitle: candidate.SectionTitle,
                    HeadingPath: candidate.HeadingPath,
                    ChunkType: candidate.ChunkType,
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null))
                .ToArray();

            var calibrated = RagEndpoints.CalibrateFusedMatches(testCase.Query, candidates);
            var final = testCase.RerankItems is { Count: > 0 }
                ? RagEndpoints.ApplyRerankScores(
                    calibrated,
                    testCase.RerankItems.Select(item => new TeiClient.RerankItem(item.Index, item.Score)).ToArray(),
                    Math.Min(testCase.RerankPrefixCount ?? calibrated.Count, calibrated.Count))
                : calibrated;

            var top = final[0];
            Assert.Equal(testCase.ExpectedTopChunkId, top.ChunkId);
            Assert.Equal(testCase.ExpectedTopRetriever, ResolveRetriever(top));
            if (!string.IsNullOrWhiteSpace(testCase.ExpectedDocHint))
            {
                var docText = $"{top.DocName} {top.DocPath}";
                Assert.Contains(testCase.ExpectedDocHint, docText, StringComparison.OrdinalIgnoreCase);
            }

            if (testCase.ExpectRerankPromotion)
                Assert.True(top.RerankScore.HasValue, $"{testCase.Name}: expected rerank to materially promote the top candidate.");
        }
    }

    [Fact]
    public void Retrieval_v3_structure_harness_preserves_sections_and_contextual_neighbors()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Chapter 1", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Introduction", 2, 1, 1, 2, null),
            new ExtractedDocumentSection(2, "Safety", 2, 2, 2, 1, null)
        };

        var units = new[]
        {
            new ExtractedDocumentUnit(0, 1, 1, 1, "alpha beta gamma", 16, 3, [1]),
            new ExtractedDocumentUnit(1, 1, 1, 1, "delta epsilon zeta", 18, 3, [2]),
            new ExtractedDocumentUnit(2, 2, 2, 2, "theta iota kappa", 16, 3, [3]),
            new ExtractedDocumentUnit(3, 2, 2, 2, "lambda mu nu", 12, 3, [4])
        };

        var projectedChunks = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 5,
            overlapWords: 1,
            minWords: 1);

        var crossingSectionChunks = projectedChunks.Count(chunk =>
        {
            var coveredUnits = units
                .Where(unit => unit.PageStart <= chunk.PageEnd && unit.PageEnd >= chunk.PageStart)
                .Where(unit => chunk.Text.Contains(unit.Text, StringComparison.Ordinal))
                .Select(unit => unit.SectionOrdinal)
                .Distinct()
                .ToArray();
            return coveredUnits.Length > 1;
        });

        var contextualEntries = ContextualTextProjector.Project("ATEX/CEN.pdf", sections, units, projectedChunks);
        var metrics = EvaluateContextualCoverage(projectedChunks, contextualEntries);

        Assert.Equal(0, crossingSectionChunks);
        Assert.True(metrics.HeadingCoverage >= 1.0, $"heading-path contextual coverage too low: {metrics.HeadingCoverage:P}");
        Assert.True(metrics.NeighborCoverage >= 0.5, $"neighbor contextual coverage too low: {metrics.NeighborCoverage:P}");
    }

    [Fact]
    public void Retrieval_v3_channel_harness_prefers_exact_then_precise_dense_then_linked()
    {
        var exact = new RagMatch(
            Score: RagEndpoints.ComputeExactMatchScore("standard_ref", "en 15281", "EN 15281"),
            DocId: "doc",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "exact",
            ChunkIndex: 0,
            Text: "EN 15281",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "EN 15281",
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Safety",
            HeadingPath: "Chapter 1 > Safety",
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var denseCandidates = RagEndpoints.RerankDenseMatches([
            new RagMatch(0.50, "doc", "ATEX/CEN.pdf", "CEN.pdf", 2, 2, "dense-legacy", 1, "legacy", 1, "hash", "legacy", "chunk_text", 1, 1, "Safety", null, "legacy_word_window_v1", null, null, null),
            new RagMatch(0.49, "doc", "ATEX/CEN.pdf", "CEN.pdf", 2, 2, "dense-precise", 2, "precise", 1, "hash", "context", "contextual_text_v1", 1, 2, "Safety", "Chapter 1 > Safety", "unit_exact_v1", null, null, null)
        ]);

        var linked = new RagMatch(
            Score: RagEndpoints.ComputeLinkedMatchScore(denseCandidates[0].Score, "same_section"),
            DocId: "doc",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "linked",
            ChunkIndex: 3,
            Text: "linked context",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "linked context",
            EmbeddingBasis: "linked_context_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 3,
            SectionTitle: "Safety",
            HeadingPath: "Chapter 1 > Safety",
            ChunkType: "section_window_v1",
            PrevChunkId: "dense-precise",
            NextChunkId: null,
            SameSectionChunkId: "dense-precise");

        Assert.Equal("dense-precise", denseCandidates[0].ChunkId);
        Assert.True(exact.Score > denseCandidates[0].Score);
        Assert.True(denseCandidates[0].Score > linked.Score);
    }

    private static ExactLookupMetrics EvaluateExactLookup(
        IReadOnlyList<ExactQueryCase> positiveCases,
        IReadOnlyList<string> negativeQueries)
    {
        var exactHits = 0;
        var expectedExactTerms = 0;

        foreach (var testCase in positiveCases)
        {
            var extracted = ExactMatchEntryExtractor.ExtractLookupTerms(testCase.Query);
            foreach (var expected in testCase.ExpectedTerms)
            {
                expectedExactTerms++;
                if (extracted.Contains(expected, StringComparer.Ordinal))
                    exactHits++;
            }
        }

        var noisyNegativeQueries = negativeQueries.Count(query =>
        {
            var extracted = ExactMatchEntryExtractor.ExtractLookupTerms(query);
            var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(query);
            return extracted.Any(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal));
        });

        return new ExactLookupMetrics(
            Recall: expectedExactTerms == 0 ? 1.0 : (double)exactHits / expectedExactTerms,
            NoiseRate: negativeQueries.Count == 0 ? 0.0 : (double)noisyNegativeQueries / negativeQueries.Count);
    }

    private static RetrievalEvalCorpus LoadCorpus(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RetrievalEvalCorpus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Failed to load retrieval evaluation corpus.");
    }

    private static ContextualCoverageMetrics EvaluateContextualCoverage(
        IReadOnlyList<ProjectedRetrievalChunk> projectedChunks,
        IReadOnlyList<ProjectedContextualTextEntry> contextualEntries)
    {
        var contextualWithHeadingPath = contextualEntries.Count(entry => entry.Text.Contains("HeadingPath:", StringComparison.Ordinal));
        var contextualWithNeighborContext = contextualEntries.Count(entry =>
            entry.Text.Contains("PreviousContext:", StringComparison.Ordinal) || entry.Text.Contains("NextContext:", StringComparison.Ordinal));

        return new ContextualCoverageMetrics(
            HeadingCoverage: projectedChunks.Count == 0 ? 1.0 : (double)contextualWithHeadingPath / projectedChunks.Count,
            NeighborCoverage: projectedChunks.Count == 0 ? 1.0 : (double)contextualWithNeighborContext / projectedChunks.Count);
    }

    private static void AssertDominantRetrieverCase(DominantRetrieverCase testCase)
    {
        var extracted = ExactMatchEntryExtractor.ExtractLookupTerms(testCase.Query);
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(testCase.Query);
        var targetedTerms = extracted
            .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
            .ToArray();
        var lexicalTokens = RagEndpoints.ExtractLexicalQueryTokens(testCase.Query);

        if (string.Equals(testCase.ExpectedDominantRetriever, "exact_match", StringComparison.Ordinal))
        {
            Assert.NotEmpty(targetedTerms);
            foreach (var expected in testCase.ExpectedExactTerms)
                Assert.Contains(expected, targetedTerms, StringComparer.Ordinal);
            if (!testCase.AllowExactNoise)
            {
                var expectedFamilies = testCase.ExpectedExactTerms
                    .Select(CollapseReferenceVariant)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.All(targetedTerms, term =>
                    Assert.Contains(CollapseReferenceVariant(term), expectedFamilies));
            }
        }
        else if (string.Equals(testCase.ExpectedDominantRetriever, "sparse_bm25", StringComparison.Ordinal))
        {
            Assert.Empty(targetedTerms);
            var expectedLexicalTerms = Assert.IsAssignableFrom<IReadOnlyList<string>>(testCase.ExpectedLexicalTerms);
            Assert.NotEmpty(expectedLexicalTerms);
            foreach (var expected in expectedLexicalTerms)
                Assert.Contains(expected, lexicalTokens, StringComparer.Ordinal);
            Assert.False(testCase.AllowExactNoise);
        }
        else if (string.Equals(testCase.ExpectedDominantRetriever, "dense_qdrant", StringComparison.Ordinal))
        {
            Assert.Empty(targetedTerms);
            Assert.False(testCase.AllowExactNoise);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported expectedDominantRetriever '{testCase.ExpectedDominantRetriever}'.");
        }
    }

    private static void AssertLinkedContextCase(LinkedContextCase testCase)
    {
        var linkedScore = RagEndpoints.ComputeLinkedMatchScore(
            testCase.AnchorScore,
            testCase.LinkType,
            testCase.AnchorRetriever);

        Assert.True(linkedScore < testCase.AnchorScore, $"{testCase.Name}: linked score should stay below anchor score.");

        if (testCase.ExpectedShouldHelp)
        {
            Assert.True(linkedScore >= 0.75, $"{testCase.Name}: linked score should remain strong enough to help.");
        }
        else
        {
            Assert.True(linkedScore < 0.60, $"{testCase.Name}: linked score should remain secondary.");
        }
    }

    private sealed record ExactQueryCase(string Query, IReadOnlyList<string> ExpectedTerms);
    private sealed record ExactLookupMetrics(double Recall, double NoiseRate);
    private sealed record ContextualCoverageMetrics(double HeadingCoverage, double NeighborCoverage);
    private sealed record RetrievalEvalCorpus(
        string Version,
        IReadOnlyList<ExactQueryCase> ExactPositiveCases,
        IReadOnlyList<string> ExactNegativeQueries,
        IReadOnlyList<DominantRetrieverCase>? DominantRetrieverCases = null,
        IReadOnlyList<LinkedContextCase>? LinkedContextCases = null,
        IReadOnlyList<RankingExpectationCase>? RankingExpectationCases = null,
        IReadOnlyList<DecisionCase>? DecisionCases = null,
        IReadOnlyList<CalibrationCase>? CalibrationCases = null);
    private sealed record DominantRetrieverCase(
        string Query,
        string ExpectedDominantRetriever,
        IReadOnlyList<string> ExpectedExactTerms,
        bool AllowExactNoise = false,
        IReadOnlyList<string>? ExpectedLexicalTerms = null);
    private sealed record LinkedContextCase(
        string Name,
        string AnchorRetriever,
        double AnchorScore,
        string LinkType,
        bool ExpectedShouldHelp);
    private sealed record RankingExpectationCase(
        string Name,
        string ExactKind,
        string MatchedTerm,
        string ExactText,
        double DenseLegacyScore,
        double DensePreciseScore,
        double LinkedAnchorScore,
        string LinkedType,
        IReadOnlyList<string> ExpectedOrder);
    private sealed record DecisionCase(
        string Name,
        string Query,
        string ExpectedDominantRetriever,
        IReadOnlyList<string> ExpectedExactTerms,
        bool AllowExactNoise,
        string LinkedExpectation,
        string ExpectedDocHint,
        IReadOnlyList<string>? ExpectedLexicalTerms = null);
    private sealed record CalibrationCase(
        string Name,
        string Query,
        IReadOnlyList<CalibrationCandidate> Candidates,
        string ExpectedTopChunkId,
        string ExpectedTopRetriever,
        string? ExpectedDocHint = null,
        IReadOnlyList<CalibrationRerankItem>? RerankItems = null,
        int? RerankPrefixCount = null,
        bool ExpectRerankPromotion = false);
    private sealed record CalibrationCandidate(
        string ChunkId,
        double BaseScore,
        string EmbeddingBasis,
        string Text,
        string? EmbedText,
        string ChunkType,
        string DocId = "doc-1",
        string DocPath = "ATEX/CEN.pdf",
        string DocName = "CEN.pdf",
        int? PageStart = 1,
        int? PageEnd = 1,
        int? ChunkIndex = 0,
        string HashDoc = "hash",
        int? SectionOrdinal = 1,
        int? UnitOrdinal = 1,
        string? SectionTitle = "Safety",
        string? HeadingPath = "Chapter 1 > Safety");
    private sealed record CalibrationRerankItem(int Index, double Score);

    private static string ResolveRetriever(RagMatch match)
        => match.EmbeddingBasis switch
        {
            "exact_match_v1" => "exact_match",
            "sparse_bm25_v1" => "sparse_bm25",
            "linked_context_v1" => "linked_context",
            _ => "dense_qdrant"
        };

    private static string CollapseReferenceVariant(string value)
        => Regex.Replace(value ?? string.Empty, @"[\s._/\-]+", string.Empty);
}
