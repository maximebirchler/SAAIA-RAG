using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SemanticResolutionV5OrderExperimentTests
{
    [Fact]
    public void DecisionFirstArm_ReturnsTheFrozenContractUnchanged()
    {
        var source = BuildResolutionContract("E1", "E3");

        var observed = SemanticResolutionV5OrderExperiment.Apply(
            source,
            SemanticResolutionV5OrderArm.DecisionFirst);

        Assert.Same(source, observed);
        Assert.Equal(
            new[] { "decision", "evidenceIds", "reason" },
            PropertyOrder(observed));
        Assert.Equal(
            new[] { "decision", "evidenceIds", "reason" },
            RequiredOrder(observed));
    }

    [Fact]
    public void ReasonFirstArm_ChangesOnlyPropertiesAndRequiredOrder()
    {
        var source = BuildResolutionContract("E1", "E3");

        var observed = SemanticResolutionV5OrderExperiment.Apply(
            source,
            SemanticResolutionV5OrderArm.ReasonFirst);

        Assert.NotSame(source, observed);
        Assert.Equal(source.Name, observed.Name);
        Assert.Equal(
            new[] { "type", "properties", "required", "additionalProperties" },
            RootOrder(observed));
        Assert.Equal(
            new[] { "reason", "evidenceIds", "decision" },
            PropertyOrder(observed));
        Assert.Equal(
            new[] { "reason", "evidenceIds", "decision" },
            RequiredOrder(observed));
        Assert.Equal(
            source.Schema.GetProperty("type").GetRawText(),
            observed.Schema.GetProperty("type").GetRawText());
        Assert.Equal(
            source.Schema.GetProperty("additionalProperties").GetRawText(),
            observed.Schema.GetProperty("additionalProperties").GetRawText());
        foreach (var propertyName in new[] { "decision", "evidenceIds", "reason" })
        {
            Assert.Equal(
                source.Schema.GetProperty("properties")
                    .GetProperty(propertyName).GetRawText(),
                observed.Schema.GetProperty("properties")
                    .GetProperty(propertyName).GetRawText());
        }
        Assert.Equal(
            RequiredOrder(source).OrderBy(static item => item, StringComparer.Ordinal),
            RequiredOrder(observed).OrderBy(static item => item, StringComparer.Ordinal));
    }

    [Fact]
    public void ReasonFirstArm_PreservesTheExactAllowedEvidencePool()
    {
        var source = BuildResolutionContract("E1", "E3", "E8");

        var observed = SemanticResolutionV5OrderExperiment.Apply(
            source,
            SemanticResolutionV5OrderArm.ReasonFirst);

        Assert.Equal(
            new[] { "E1", "E3", "E8" },
            observed.Schema.GetProperty("properties")
                .GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Theory]
    [InlineData("source_backed_flat_writer_v1")]
    [InlineData("source_backed_semantic_review_v1")]
    [InlineData("unrelated_structured_contract")]
    public void ReasonFirstArm_DoesNotTouchAnyOtherStructuredContract(string name)
    {
        var contract = new LlmStructuredOutputContract(
            name,
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { value = new { type = "string" } },
                required = new[] { "value" },
                additionalProperties = false
            }));

        var observed = SemanticResolutionV5OrderExperiment.Apply(
            contract,
            SemanticResolutionV5OrderArm.ReasonFirst);

        Assert.Same(contract, observed);
    }

    [Fact]
    public void ReasonFirstArm_FailsClosedWhenTheFrozenV5ShapeDrifts()
    {
        var drifted = new LlmStructuredOutputContract(
            SemanticResolutionV5OrderExperiment.ContractName,
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    decision = new { type = "string" },
                    evidenceIds = new { type = "array" },
                    reason = new { type = "string" },
                    presentation = new { type = "string" }
                },
                required = new[]
                {
                    "decision", "evidenceIds", "reason", "presentation"
                },
                additionalProperties = false
            }));

        var error = Assert.Throws<InvalidOperationException>(() =>
            SemanticResolutionV5OrderExperiment.Apply(
                drifted,
                SemanticResolutionV5OrderArm.ReasonFirst));

        Assert.Contains("exact frozen source schema", error.Message);
    }

    private static LlmStructuredOutputContract BuildResolutionContract(
        params string[] allowedEvidenceIds)
        => SemanticResolutionV5OrderExperiment.BuildFrozenContract(
            allowedEvidenceIds);

    private static string[] RootOrder(LlmStructuredOutputContract contract)
        => contract.Schema.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

    private static string[] PropertyOrder(LlmStructuredOutputContract contract)
        => contract.Schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

    private static string[] RequiredOrder(LlmStructuredOutputContract contract)
        => contract.Schema.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();
}
