using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalEvaluationCorpusTests
{
    [Theory]
    [InlineData("retrieval_eval_corpus.v1.json", "v1")]
    [InlineData("retrieval_eval_corpus.v2.json", "v2")]
    [InlineData("retrieval_eval_corpus.v3.json", "v3")]
    [InlineData("retrieval_eval_corpus.v4.json", "v4")]
    [InlineData("retrieval_eval_corpus.v5.json", "v5")]
    [InlineData("retrieval_eval_corpus.v6.json", "v6")]
    public void Retrieval_eval_corpus_declares_expected_version(string fileName, string expectedVersion)
    {
        using var document = LoadFixture(fileName);
        var version = document.RootElement.GetProperty("version").GetString();

        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void Retrieval_eval_corpus_v3_contains_richer_business_expectations_than_v2()
    {
        using var v2 = LoadFixture("retrieval_eval_corpus.v2.json");
        using var v3 = LoadFixture("retrieval_eval_corpus.v3.json");

        var v2DominantCases = v2.RootElement.GetProperty("dominantRetrieverCases").GetArrayLength();
        var v3DominantCases = v3.RootElement.GetProperty("dominantRetrieverCases").GetArrayLength();
        var v3DecisionCases = v3.RootElement.GetProperty("decisionCases").GetArrayLength();
        var v3RankingCases = v3.RootElement.GetProperty("rankingExpectationCases").GetArrayLength();
        var v3CalibrationCases = v3.RootElement.GetProperty("calibrationCases").GetArrayLength();

        Assert.True(v3DominantCases >= v2DominantCases);
        Assert.True(v3DecisionCases > 0);
        Assert.True(v3RankingCases > 0);
        Assert.True(v3CalibrationCases > 0);
    }

    [Fact]
    public void Retrieval_eval_corpus_v4_contains_richer_conversational_expectations_than_v3()
    {
        using var v3 = LoadFixture("retrieval_eval_corpus.v3.json");
        using var v4 = LoadFixture("retrieval_eval_corpus.v4.json");

        var v3DecisionCases = v3.RootElement.GetProperty("decisionCases").GetArrayLength();
        var v4DecisionCases = v4.RootElement.GetProperty("decisionCases").GetArrayLength();
        var v4DominantCases = v4.RootElement.GetProperty("dominantRetrieverCases").GetArrayLength();
        var v4CalibrationCases = v4.RootElement.GetProperty("calibrationCases").GetArrayLength();
        var v4ExactQueries = v4.RootElement.GetProperty("exactPositiveCases").EnumerateArray()
            .Select(item => item.GetProperty("query").GetString() ?? string.Empty)
            .ToArray();

        Assert.True(v4DecisionCases >= 8);
        Assert.True(v4DominantCases >= 8);
        Assert.True(v4CalibrationCases >= 4);
        Assert.True(v4DecisionCases >= v3DecisionCases);
        Assert.Contains(v4ExactQueries, query => query.Contains("Tu peux me retrouver", StringComparison.Ordinal));
        Assert.Contains(v4ExactQueries, query => query.Contains("J ai besoin du doc", StringComparison.Ordinal));
        Assert.Contains(v4ExactQueries, query => query.Contains("Est-ce que tu as", StringComparison.Ordinal));
    }

    [Fact]
    public void Retrieval_eval_corpora_keep_negative_queries_for_exact_noise_checks()
    {
        foreach (var fileName in new[] { "retrieval_eval_corpus.v1.json", "retrieval_eval_corpus.v2.json", "retrieval_eval_corpus.v3.json", "retrieval_eval_corpus.v4.json" })
        {
            using var document = LoadFixture(fileName);
            var negativeQueries = document.RootElement.GetProperty("exactNegativeQueries").GetArrayLength();
            Assert.True(negativeQueries > 0, $"{fileName} should keep exactNegativeQueries coverage.");
        }
    }

    [Fact]
    public void Retrieval_eval_corpus_v6_covers_procedure_synthesis_and_comparison_families()
    {
        using var v6 = LoadFixture("retrieval_eval_corpus.v6.json");

        var v6DominantCases = v6.RootElement.GetProperty("dominantRetrieverCases").GetArrayLength();
        var v6DecisionCases = v6.RootElement.GetProperty("decisionCases").GetArrayLength();
        var v6CalibrationCases = v6.RootElement.GetProperty("calibrationCases").GetArrayLength();
        var v6LinkedCases = v6.RootElement.GetProperty("linkedContextCases").GetArrayLength();

        Assert.True(v6DominantCases >= 8);
        Assert.True(v6DecisionCases >= 4);
        Assert.True(v6CalibrationCases >= 4);
        Assert.True(v6LinkedCases >= 4);

        var hasExactProcedureRef = v6.RootElement.GetProperty("dominantRetrieverCases")
            .EnumerateArray()
            .Any(item => item.GetProperty("expectedDominantRetriever").GetString() == "exact_match");
        var hasSparseKeyword = v6.RootElement.GetProperty("dominantRetrieverCases")
            .EnumerateArray()
            .Any(item => item.GetProperty("expectedDominantRetriever").GetString() == "sparse_bm25");
        var hasDenseSynthesis = v6.RootElement.GetProperty("dominantRetrieverCases")
            .EnumerateArray()
            .Any(item => item.GetProperty("expectedDominantRetriever").GetString() == "dense_qdrant");

        Assert.True(hasExactProcedureRef, "v6 should cover exact-match retrieval for standard refs embedded in procedure queries.");
        Assert.True(hasSparseKeyword, "v6 should cover sparse BM25 for keyword-heavy procedure queries.");
        Assert.True(hasDenseSynthesis, "v6 should cover dense retrieval for synthesis and multi-doc comparison queries.");
    }

    private static JsonDocument LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
