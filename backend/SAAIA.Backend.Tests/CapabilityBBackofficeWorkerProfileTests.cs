using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CapabilityBBackofficeWorkerProfileTests
{
    [Fact]
    public void BuildBackofficeProfileFromSummary_filters_unsafe_baseline_cards()
    {
        var docId = Guid.NewGuid();
        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            docId,
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["quality"],
            [],
            [],
            [],
            [],
            "quality",
            1,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Unsafe LLM title only card",
                    3,
                    3,
                    "llm_content_card",
                    ["unsafe"]),
                new DocumentProfileContentCard(
                    "Deterministic section card",
                    2,
                    2,
                    "section",
                    ["section"]),
                new DocumentProfileContentCard(
                    "Quality controls checklist",
                    null,
                    null,
                    "llm_content_card",
                    ["grounded"],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        ScaleBasis: null,
                        QuantityFacts: [],
                        NonScalableReasons: [],
                        Confidence: 0.8,
                        Language: "en",
                        Facts:
                        [
                            new DocumentProfileEvidenceFact(
                                "procedure",
                                "visual inspection",
                                null,
                                null,
                                "The checklist requires visual inspection before release.",
                                4,
                                4,
                                0.8)
                        ])),
                new DocumentProfileContentCard(
                    "ISO 13849-1",
                    null,
                    null,
                    "llm_content_card",
                    ["standard"])
            ]);

        var profile = CapabilityBBackofficeWorker.BuildBackofficeProfileFromSummary(
            new CapabilityBDocumentRow(
                docId,
                "Operations/Quality.pdf",
                "Quality.pdf",
                "operations",
                PageCount: 5,
                IndexedVersion: 1,
                ProfileLanguage: "en"),
            baseline,
            new CapabilityBGeneratedSummaryPayload(
                "Backoffice summary.",
                JsonDocument.Parse("{}").RootElement.Clone(),
                new CapabilityBSummaryQualityEvaluation(1, 1, 1, 1, 1, 1, 1, 1, 1, 1),
                "en"),
            "en");

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Unsafe LLM title only card", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Deterministic section card", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Quality controls checklist", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ISO 13849-1", StringComparison.Ordinal));
    }
}
