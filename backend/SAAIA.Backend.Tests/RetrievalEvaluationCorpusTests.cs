using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalEvaluationCorpusTests
{
    [Theory]
    [InlineData("retrieval_eval_corpus.v1.json", "v1")]
    [InlineData("retrieval_eval_corpus.v2.json", "v2")]
    [InlineData("retrieval_eval_corpus.v3.json", "v3")]
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

        Assert.True(v3DominantCases >= v2DominantCases);
        Assert.True(v3DecisionCases > 0);
        Assert.True(v3RankingCases > 0);
    }

    [Fact]
    public void Retrieval_eval_corpora_keep_negative_queries_for_exact_noise_checks()
    {
        foreach (var fileName in new[] { "retrieval_eval_corpus.v1.json", "retrieval_eval_corpus.v2.json", "retrieval_eval_corpus.v3.json" })
        {
            using var document = LoadFixture(fileName);
            var negativeQueries = document.RootElement.GetProperty("exactNegativeQueries").GetArrayLength();
            Assert.True(negativeQueries > 0, $"{fileName} should keep exactNegativeQueries coverage.");
        }
    }

    private static JsonDocument LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
