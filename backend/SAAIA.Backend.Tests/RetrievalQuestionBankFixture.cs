using System.Text.Json;

namespace SAAIA.Backend.Tests;

internal static class RetrievalQuestionBankFixture
{
    public static RetrievalQuestionBankCorpus LoadV5()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval_eval_corpus.v5.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RetrievalQuestionBankCorpus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Failed to load retrieval evaluation v5 question bank.");
    }

    public static RetrievalProductValidationCorpus LoadProductValidationV1()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval_product_validation.v1.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RetrievalProductValidationCorpus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Failed to load retrieval product validation v1 pack.");
    }

    public static RetrievalCuisineValidationCorpus LoadCuisineValidationV1()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval_cuisine_validation.v1.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RetrievalCuisineValidationCorpus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Failed to load cuisine retrieval validation v1 pack.");
    }

    internal sealed record RetrievalQuestionBankCorpus(
        string Version,
        IReadOnlyList<QuestionCase> QuestionCases);

    internal sealed record RetrievalProductValidationCorpus(
        string Version,
        IReadOnlyList<ProductValidationCase> ValidationCases);

    internal sealed record RetrievalCuisineValidationCorpus(
        string Version,
        string GeneratedAt,
        string SourceFile,
        string Description,
        IReadOnlyList<CuisineValidationCase> ValidationCases);

    internal sealed record QuestionCase(
        string Name,
        string Query,
        string Intent,
        string ExpectedBehavior,
        IReadOnlyList<string> SourceDocHints,
        IReadOnlyList<string> ExpectedDocHints,
        string? Notes = null,
        bool RuntimeReady = false,
        string? ExpectedPrimaryDocHint = null,
        string? ExpectedPrimarySectionHint = null,
        string? ExpectedResponseShape = null,
        IReadOnlyList<string>? ExpectedQualificationTokens = null,
        IReadOnlyList<string>? ExpectedClarificationTokens = null);

    internal sealed record ProductValidationCase(
        string Name,
        string Query,
        string Family,
        string ExpectedBehavior,
        string ExpectedResponseShape,
        string ExpectedPrimaryDocHint,
        string? ExpectedPrimarySectionHint = null,
        IReadOnlyList<string>? ManualChecks = null,
        string? Notes = null);

    internal sealed record CuisineValidationCase(
        string Id,
        string Axis,
        string Difficulty,
        string CorpusTarget,
        string Theme,
        string Question,
        string ExpectedAnswerKind,
        string ValidationPoints);
}
