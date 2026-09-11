using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task SemanticAnswerTransactionV3Scope_DeclaresPresentedPoolAsTransactionDocumentScope()
    {
        var system = await EvidenceScopeSystemPromptAsync();

        Assert.Contains(
            "pool canonique presente est le perimetre documentaire de cette transaction",
            system,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3Scope_ForbidsHypotheticalMissingAlternativesAsSoleGap()
    {
        var system = await EvidenceScopeSystemPromptAsync();

        Assert.Contains(
            "possibilite hypothetique absente du pool",
            system,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3Scope_DoesNotTurnPoolAbsenceIntoWorldNonExistence()
    {
        var system = await EvidenceScopeSystemPromptAsync();

        Assert.Contains(
            "absence dans le pool n'est jamais une preuve de non-existence hors du pool",
            system,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3Scope_PreservesResearchBeyondExplicitScope()
    {
        var system = await EvidenceScopeSystemPromptAsync();

        Assert.Contains(
            "demande exige explicitement une conclusion qui depasse ce perimetre",
            system,
            StringComparison.Ordinal);
        Assert.Contains(
            "si un fait necessaire au livrable manque dans les preuves visibles",
            system,
            StringComparison.Ordinal);
    }

    private static async Task<string> EvidenceScopeSystemPromptAsync()
    {
        var (_, llm) = await ExecuteResolutionV3ReviewAsync(
            DirectAnswerV3Json());
        return Assert.Single(llm.Requests)[0].Content ?? string.Empty;
    }
}
