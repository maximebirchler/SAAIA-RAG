using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CuisineLlmQuestionBankTests
{
    [Fact]
    public void Cuisine_question_bank_declares_expected_version_and_scale()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();

        Assert.Equal("cuisine-v1", corpus.Version);
        Assert.Equal("2026-04-30", corpus.GeneratedAt);
        Assert.Equal("jeu_tests_questions_llm_cuisine.csv", corpus.SourceFile);
        Assert.True(corpus.ValidationCases.Count >= 290, "The cuisine validation pack should keep the full customer-style question set.");
    }

    [Fact]
    public void Cuisine_question_bank_covers_core_llm_rag_behaviors()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();
        var axes = corpus.ValidationCases
            .Select(static c => c.Axis)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("Inventaire corpus", axes);
        Assert.Contains("Extraction mono-recette", axes);
        Assert.Contains("Sourcing / citations / traçabilité", axes);
        Assert.Contains("Robustesse / refus / hallucination", axes);
        Assert.Contains("Multi-tour / mémoire de session", axes);
        Assert.Contains("Raisonnement multi-doc complexe", axes);
        Assert.Contains("Composition de menu / fusion contrôlée", axes);
        Assert.Contains("Contraintes / substitutions / adaptation", axes);
    }

    [Fact]
    public void Cuisine_question_bank_keeps_document_and_multi_pdf_balance()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();

        var multiPdfCases = corpus.ValidationCases.Count(static c => string.Equals(c.CorpusTarget, "Multi-PDF", StringComparison.Ordinal));
        var allCorpusCases = corpus.ValidationCases.Count(static c => string.Equals(c.CorpusTarget, "Tous", StringComparison.Ordinal));
        var singleDocumentTargets = corpus.ValidationCases
            .Where(static c => c.CorpusTarget.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(static c => c.CorpusTarget)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(multiPdfCases >= 140, "The pack should heavily exercise multi-document reasoning.");
        Assert.True(allCorpusCases >= 10, "The pack should include corpus inventory questions.");
        Assert.True(singleDocumentTargets.Length >= 9, "The pack should cover the cuisine PDFs broadly.");
    }

    [Fact]
    public void Cuisine_question_bank_keeps_difficult_and_anti_hallucination_cases()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();

        var difficultCases = corpus.ValidationCases.Count(static c =>
            string.Equals(c.Difficulty, "Difficile", StringComparison.Ordinal) ||
            string.Equals(c.Difficulty, "Très difficile", StringComparison.Ordinal));
        var antiHallucinationCases = corpus.ValidationCases.Count(static c =>
            string.Equals(c.Axis, "Robustesse / refus / hallucination", StringComparison.Ordinal));

        Assert.True(difficultCases >= 100, "The pack should keep enough hard cases to expose weak reasoning.");
        Assert.True(antiHallucinationCases >= 20, "The pack should explicitly test refusal and anti-hallucination behavior.");
    }

    [Fact]
    public void Cuisine_question_bank_exercises_weekly_meal_planning_and_menu_assistance()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();

        var planningCases = corpus.ValidationCases.Count(static c =>
            c.Question.Contains("semaine", StringComparison.OrdinalIgnoreCase) ||
            c.Question.Contains("7 repas", StringComparison.OrdinalIgnoreCase) ||
            c.Question.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
            c.Question.Contains("menu", StringComparison.OrdinalIgnoreCase));
        var compositionCases = corpus.ValidationCases.Count(static c =>
            string.Equals(c.Axis, "Composition de menu / fusion contrôlée", StringComparison.Ordinal));

        Assert.True(planningCases >= 20, "The pack should stress realistic weekly/menu planning prompts.");
        Assert.True(compositionCases >= 30, "The pack should include enough menu composition cases to test useful assistant behavior.");
    }

    [Fact]
    public void Cuisine_question_bank_cases_are_well_formed()
    {
        var corpus = RetrievalQuestionBankFixture.LoadCuisineValidationV1();

        Assert.All(corpus.ValidationCases, testCase =>
        {
            Assert.Matches(@"^Q\d{3}$", testCase.Id);
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
