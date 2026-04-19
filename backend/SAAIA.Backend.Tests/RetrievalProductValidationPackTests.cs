using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalProductValidationPackTests
{
    [Fact]
    public void Retrieval_product_validation_v1_declares_expected_version_and_scale()
    {
        var pack = RetrievalQuestionBankFixture.LoadProductValidationV1();

        Assert.Equal("v1", pack.Version);
        Assert.Equal(20, pack.ValidationCases.Count);
    }

    [Fact]
    public void Retrieval_product_validation_v1_covers_key_product_families()
    {
        var pack = RetrievalQuestionBankFixture.LoadProductValidationV1();
        var families = pack.ValidationCases.Select(static c => c.Family).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Contains("qualified_customer_answer", families);
        Assert.Contains("short_explanation", families);
        Assert.Contains("comparison", families);
        Assert.Contains("locate_passage", families);
        Assert.Contains("clarification", families);
        Assert.Contains("document_selection", families);
    }

    [Fact]
    public void Retrieval_product_validation_v1_cases_are_grounded_in_runtime_ready_v5_cases()
    {
        var pack = RetrievalQuestionBankFixture.LoadProductValidationV1();
        var v5 = RetrievalQuestionBankFixture.LoadV5();
        var runtimeReadyNames = v5.QuestionCases
            .Where(static q => q.RuntimeReady)
            .Select(static q => q.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(pack.ValidationCases, validationCase =>
        {
            Assert.Contains(validationCase.Name, runtimeReadyNames);
            Assert.False(string.IsNullOrWhiteSpace(validationCase.Query));
            Assert.False(string.IsNullOrWhiteSpace(validationCase.ExpectedBehavior));
            Assert.False(string.IsNullOrWhiteSpace(validationCase.ExpectedResponseShape));
            Assert.False(string.IsNullOrWhiteSpace(validationCase.ExpectedPrimaryDocHint));
            Assert.NotEmpty(validationCase.ManualChecks ?? Array.Empty<string>());
        });
    }
}
