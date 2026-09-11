using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedEvidenceSemanticAnnotationTests
{
    [Fact]
    public void Bundle_rebuild_preserves_llm_semantic_annotations_only_for_same_immutable_evidence()
    {
        var previous = new EvidenceBundle(
            "previous",
            "question",
            new[]
            {
                Item(
                    "E1",
                    "revision-a",
                    "Titre approuve par le LLM",
                    "[\"Option A\"]",
                    "false"),
                Item("E2", "revision-a", "Ancien titre")
            },
            Array.Empty<SourceBackedTraceEvent>());
        var rebuilt = new EvidenceBundle(
            "rebuilt",
            "question",
            new[]
            {
                Item("E1", "revision-a", semanticDisplayValue: null),
                Item("E2", "revision-b", semanticDisplayValue: null)
            },
            Array.Empty<SourceBackedTraceEvent>());

        var result = SourceBackedAgentV2Runner.PreserveAgentSemanticAnnotations(
            previous,
            rebuilt,
            out var preservedCount);

        Assert.Equal(1, preservedCount);
        Assert.Equal(
            "Titre approuve par le LLM",
            result.ById["E1"].SelectionHints["semanticDisplayValue"]);
        Assert.Equal(
            "[\"Option A\"]",
            result.ById["E1"].SelectionHints[
                "semanticCompatibleColumnLabels"]);
        Assert.Equal(
            "false",
            result.ById["E1"].SelectionHints[
                "semanticNavigationAnchorEligible"]);
        Assert.DoesNotContain(
            "semanticDisplayValue",
            result.ById["E2"].SelectionHints.Keys);
        Assert.DoesNotContain(
            "semanticCompatibleColumnLabels",
            result.ById["E2"].SelectionHints.Keys);
        Assert.DoesNotContain(
            "semanticNavigationAnchorEligible",
            result.ById["E2"].SelectionHints.Keys);
    }

    private static EvidenceItem Item(
        string evidenceId,
        string revisionId,
        string? semanticDisplayValue,
        string? semanticCompatibleColumnLabels = null,
        string? semanticNavigationAnchorEligible = null)
    {
        var hints = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["sourceAnchorLabel"] = "Fragment OCR"
        };
        if (!string.IsNullOrWhiteSpace(semanticDisplayValue))
            hints["semanticDisplayValue"] = semanticDisplayValue;
        if (!string.IsNullOrWhiteSpace(semanticCompatibleColumnLabels))
        {
            hints["semanticCompatibleColumnLabels"] =
                semanticCompatibleColumnLabels;
        }
        if (!string.IsNullOrWhiteSpace(semanticNavigationAnchorEligible))
        {
            hints["semanticNavigationAnchorEligible"] =
                semanticNavigationAnchorEligible;
        }

        return new EvidenceItem(
            evidenceId,
            "canonical_content_card",
            "documents.content_cards",
            "",
            "doc-1",
            "document.pdf",
            "Categorie/document.pdf",
            "source-hash",
            revisionId,
            7,
            7,
            "card-1",
            "Fragment OCR",
            "fragment ocr",
            1,
            1,
            "Categorie",
            "fr",
            "fr",
            "good",
            (JsonElement?)null,
            hints,
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
