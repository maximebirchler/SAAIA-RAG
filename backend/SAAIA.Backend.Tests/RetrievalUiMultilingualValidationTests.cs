using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalUiMultilingualValidationTests
{
    private static readonly string[] ExpectedLanguages = ["fr", "en", "es", "pt", "de", "it"];

    [Fact]
    public void Ui_multilingual_validation_declares_expected_version_and_languages()
    {
        var corpus = RetrievalQuestionBankFixture.LoadUiMultilingualValidationV1();

        Assert.Equal("ui-multilingual-v1", corpus.Version);
        Assert.Equal("2026-05-09", corpus.GeneratedAt);
        Assert.Equal(ExpectedLanguages, corpus.Languages);
    }

    [Fact]
    public void Ui_multilingual_validation_keeps_each_case_translated_in_all_client_languages()
    {
        var corpus = RetrievalQuestionBankFixture.LoadUiMultilingualValidationV1();

        Assert.Equal(120, corpus.ValidationCases.Count);
        Assert.Equal(20, corpus.ValidationCases.Select(static c => c.BaseId).Distinct(StringComparer.Ordinal).Count());

        foreach (var group in corpus.ValidationCases.GroupBy(static c => c.BaseId, StringComparer.Ordinal))
        {
            Assert.Equal(6, group.Count());
            Assert.Equal(ExpectedLanguages, group.Select(static c => c.Language).OrderBy(static l => Array.IndexOf(ExpectedLanguages, l)).ToArray());
            Assert.Single(group.Select(static c => c.Axis).Distinct(StringComparer.Ordinal));
            Assert.Single(group.Select(static c => c.CorpusTarget).Distinct(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Ui_multilingual_validation_cases_are_well_formed_and_unique()
    {
        var corpus = RetrievalQuestionBankFixture.LoadUiMultilingualValidationV1();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        Assert.All(corpus.ValidationCases, testCase =>
        {
            Assert.True(ids.Add(testCase.Id), $"Duplicate multilingual validation id: {testCase.Id}");
            Assert.Matches(@"^ML\d{3}-(fr|en|es|pt|de|it)$", testCase.Id);
            Assert.Matches(@"^ML\d{3}$", testCase.BaseId);
            Assert.Contains(testCase.Language, ExpectedLanguages);
            Assert.False(string.IsNullOrWhiteSpace(testCase.Axis));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Difficulty));
            Assert.False(string.IsNullOrWhiteSpace(testCase.CorpusTarget));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Theme));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Question));
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedAnswerKind));
            Assert.False(string.IsNullOrWhiteSpace(testCase.ValidationPoints));
        });
    }
}
