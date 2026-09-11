using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SingleUnitCitationContractTests
{
    [Theory]
    [InlineData(false, "Le module Atlas est portable [E1]. Son indicateur clignote deux fois [E1].", true)]
    [InlineData(true, "Le module Atlas est portable [E1]. Son indicateur clignote deux fois [E1]. Le module Boreal clignote trois fois [E2].", false)]
    [InlineData(false, "Le module Boreal clignote trois fois [E2].", false)]
    [InlineData(false, "Le module Atlas est portable.", false)]
    public void Verification_distinguishes_multiple_claims_for_one_unit_from_multiple_selected_units(
        bool selectTwoUnits, string answer, bool expectedValid)
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                hits = new[]
            {
                new { docId = "atlas", docPath = "Lab/Atlas.pdf", revisionId = "rev-atlas", sourceHash = new string('a', 64),
                    chunkId = "atlas-chunk", pageStart = 1, pageEnd = 1, excerpt = "Le module Atlas est portable. Son indicateur clignote deux fois." },
                new { docId = "boreal", docPath = "Lab/Boreal.pdf", revisionId = "rev-boreal", sourceHash = new string('b', 64),
                    chunkId = "boreal-chunk", pageStart = 2, pageEnd = 2, excerpt = "Le module Boreal clignote trois fois." }
            }
            })
        });
        var intake = new SourceBackedIntake(selectTwoUnits ? "Présente deux modules." : "Présente un module.", "rag.answer", [], [], false, "fr");
        var bundle = EvidenceBundleBuilder.FromToolResults(results, intake.UserQuestion);
        Assert.Equal(2, bundle.Items.Count);
        var selected = selectTwoUnits ? new[] { "E1", "E2" } : new[] { "E1" };
        var draft = new WriterDraft(answer, SourceContractVerifier.ExtractEvidenceIds(answer).Distinct().ToArray());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("VerifySelectedWriterDraft", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = method.Invoke(null, new object[] { draft, bundle, intake, selected, "named_item", false })!;
        var verification = (SourceVerificationResult)result.GetType().GetProperty("Verification")!.GetValue(result)!;
        Assert.Equal(expectedValid, verification.IsValid);
        Assert.Equal(selectTwoUnits ? 2 : 1, result.GetType().GetProperty("RequiredEvidenceGroupCount")!.GetValue(result));
        if (selectTwoUnits)
            Assert.Contains(verification.Errors, error => error.Code == "semantic_selection_citation_repeated");
        if (expectedValid)
            Assert.DoesNotContain(verification.Errors, error => error.Code == "semantic_selection_citation_repeated");
    }
}
