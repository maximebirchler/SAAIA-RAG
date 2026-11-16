using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedAcronymDefinitionProbeTests
{
    [Theory]
    [InlineData("Qu'est-ce qu'une PTS ?", "PTS")]
    [InlineData("Que signifie ABC ?", "ABC")]
    [InlineData("What is HVAC?", "HVAC")]
    public void ExtractsOneAcronymFromADirectDefinitionQuestion(
        string question, string expected)
    {
        Assert.True(SourceBackedAcronymDefinitionProbe.TryExtractAcronym(
            question, out var acronym));
        Assert.Equal(expected, acronym);
    }

    [Theory]
    [InlineData("Pourquoi utiliser PTS ?")]
    [InlineData("Qu'est-ce que PTS dans ISO ?")]
    [InlineData("Qu'est-ce qu'une PTS selon le manuel ?")]
    [InlineData("Qu'est-ce qu'une pompe ?")]
    public void DoesNotInterceptOtherQuestions(string question)
        => Assert.False(SourceBackedAcronymDefinitionProbe.TryExtractAcronym(
            question, out _));

    [Fact]
    public void RejectsDenseOnlyResultsWithoutAnUppercaseLiteralAnchor()
    {
        using var response = JsonDocument.Parse("""
            {
              "items": [
                { "docName": "Manual.pdf", "text": "Unrelated nearby technical paragraph." },
                { "docName": "Other.pdf", "text": "data pts in the chart" }
              ],
              "metrics": {
                "exactMatchReturned": 0,
                "sparseReturned": 0,
                "degradedRetrievers": []
              }
            }
            """);
        Assert.True(SourceBackedAcronymDefinitionProbe.HasNoReliableAcronymAnchor(
            response.RootElement, "PTS"));
    }

    [Theory]
    [InlineData("{\"items\":[{\"docName\":\"Manual.pdf\",\"text\":\"A PTS is a documented safety system.\"}],\"metrics\":{\"exactMatchReturned\":0,\"sparseReturned\":0}}")]
    [InlineData("{\"items\":[{\"docName\":\"Manual.pdf\",\"text\":\"A possible source.\"}],\"metrics\":{\"exactMatchReturned\":0,\"sparseReturned\":1}}")]
    [InlineData("{\"items\":[],\"metrics\":{\"exactMatchReturned\":0,\"sparseReturned\":0,\"degradedRetrievers\":[\"canonical_sparse_fts\"]}}")]
    public void KeepsNormalAgentWhenAnAnchorOrUncertainSearchExists(string json)
    {
        using var response = JsonDocument.Parse(json);
        Assert.False(SourceBackedAcronymDefinitionProbe.HasNoReliableAcronymAnchor(
            response.RootElement, "PTS"));
    }
}
