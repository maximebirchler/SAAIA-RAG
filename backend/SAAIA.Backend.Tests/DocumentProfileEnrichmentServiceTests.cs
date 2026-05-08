using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentProfileEnrichmentServiceTests
{
    [Fact]
    public async Task BuildEnrichedProfileAsync_uses_llm_json_and_preserves_baseline_terms()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"en\",\"summary\":\"LLM profile: IND570 describes PLC exchange controls and operational safety checks.\",\"keywords\":[\"plc exchange\",\"safety checks\"],\"entities\":[\"IND570\"],\"topics\":[\"PLC Integration\"],\"questions\":[\"How does IND570 handle PLC exchange controls?\"],\"limits\":[\"Use page chunks for exact parameters.\"],\"cards\":[{\"title\":\"PLC exchange controls\",\"pageStart\":7,\"pageEnd\":8,\"kind\":\"llm_content_card\",\"signals\":[\"plc exchange\",\"controls\"]}]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            RevisionId: Guid.NewGuid(),
            DocId: Guid.NewGuid(),
            ProfileVersion: "deterministic_v1",
            Language: "en",
            SummaryText: "Deterministic baseline profile.",
            Keywords: ["baseline-keyword"],
            Entities: ["EN 15281"],
            Topics: ["Baseline Topic"],
            HypotheticalQuestions: ["What does the document say about baseline-keyword?"],
            Limits: ["Use page chunks for exact facts."],
            SearchText: "baseline-keyword EN 15281",
            TokenCount: 3,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Baseline card",
                    2,
                    2,
                    "section",
                    ["baseline-keyword"])
            ]);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                DocId: baseline.DocId,
                DocPath: "Programmation/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "programmation",
                PageCount: 42,
                IndexedVersion: 1),
            baseline,
            ["PLC Integration"],
            ["The IND570 exchanges PLC control words and status data."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("llm_backoffice_v1", profile!.ProfileVersion);
        Assert.Equal("en", profile.Language);
        Assert.Contains("LLM profile", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("plc exchange", profile.Keywords);
        Assert.Contains("baseline-keyword", profile.Keywords);
        Assert.Contains("IND570", profile.Entities);
        Assert.Contains("EN 15281", profile.Entities);
        Assert.Contains("How does IND570 handle PLC exchange controls?", profile.HypotheticalQuestions);
        Assert.Contains("Use page chunks for exact parameters.", profile.Limits);
        Assert.Contains("plc exchange", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "PLC exchange controls", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Baseline card", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_ignores_schema_language_placeholder()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"fr|en|es|pt|de|it|und\",\"summary\":\"Profil compact.\",\"keywords\":[\"compact\"],\"entities\":[],\"topics\":[],\"questions\":[],\"limits\":[]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "fr",
            "Profil deterministe.",
            ["base"],
            [],
            [],
            [],
            [],
            "base",
            1);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(baseline.DocId, "Cuisine/Test.pdf", "Test.pdf", "cuisine", 1, 1),
            baseline,
            [],
            [],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("fr", profile!.Language);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_uses_arbitrary_document_profile_language()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"nl\",\"summary\":\"Profiel over onderhoud en veiligheidscontroles.\",\"keywords\":[\"onderhoud\"],\"entities\":[],\"topics\":[\"veiligheidscontroles\"],\"questions\":[\"Welke controles staan in de handleiding?\"],\"limits\":[]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "und",
            "Deterministic baseline profile.",
            ["onderhoud"],
            [],
            [],
            [],
            [],
            "onderhoud veiligheidscontroles",
            2);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Kennisbank/Handleiding.pdf",
                "Handleiding.pdf",
                "kennisbank",
                PageCount: 4,
                IndexedVersion: 1,
                ProfileLanguage: "nl"),
            baseline,
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("nl", profile!.Language);
        Assert.Contains("onderhoud", profile.Keywords);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_keeps_target_language_when_llm_reports_different_language()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "Profile about maintenance and safety checks.",
                  "keywords": ["onderhoud"],
                  "entities": [],
                  "topics": ["veiligheidscontroles"],
                  "questions": [],
                  "limits": []
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "und",
            "Baseline.",
            ["onderhoud"],
            [],
            [],
            [],
            [],
            "onderhoud veiligheidscontroles",
            2);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Kennisbank/Handleiding.pdf",
                "Handleiding.pdf",
                "kennisbank",
                PageCount: 4,
                IndexedVersion: 1,
                ProfileLanguage: "nl"),
            baseline,
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("nl", profile!.Language);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_detects_non_ui_language_when_baseline_is_unknown()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"nl\",\"summary\":\"Profiel over onderhoud en veiligheidscontroles.\",\"keywords\":[\"onderhoud\"],\"entities\":[],\"topics\":[\"veiligheidscontroles\"],\"questions\":[\"Welke controles staan in de handleiding?\"],\"limits\":[]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "und",
            "Baseline.",
            ["onderhoud"],
            [],
            [],
            [],
            [],
            "onderhoud veiligheidscontroles",
            2);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Kennisbank/Handleiding.pdf",
                "Handleiding.pdf",
                "kennisbank",
                PageCount: 4,
                IndexedVersion: 1),
            baseline,
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("nl", profile!.Language);
        Assert.NotNull(capturedRequest);
        Assert.Contains("Document/output language: nl", capturedRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_carries_manual_review_quality_in_prompt_and_limits()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"en\",\"summary\":\"LLM profile for extracted verification controls.\",\"keywords\":[\"verification controls\"],\"entities\":[],\"topics\":[\"quality review\"],\"questions\":[],\"limits\":[]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["verification"],
            [],
            [],
            [],
            [],
            "verification controls quality review",
            4);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "OCR/manual-review.pdf",
                "manual-review.pdf",
                "ocr",
                PageCount: 5,
                IndexedVersion: 2,
                ExtractionQuality: BuildManualReviewLowTextQuality()),
            baseline,
            ["Quality review"],
            ["The available text mentions verification controls but only sparse text was extracted."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.NotNull(capturedRequest);
        Assert.Contains("Source quality status: manual_review_low_text", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("Text status: low_text", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("do not treat them as a complete normal source", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(profile!.Limits, limit => limit.Contains("Source quality note", StringComparison.Ordinal));
        Assert.Contains("OCR/extraction requires review", profile.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_preserves_quality_limit_when_llm_limits_fill_cap()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for verification controls.",
                  "keywords": ["verification controls"],
                  "entities": [],
                  "topics": ["quality review"],
                  "questions": [],
                  "limits": [
                    "Use verification controls chunk 1.",
                    "Use verification controls chunk 2.",
                    "Use verification controls chunk 3.",
                    "Use verification controls chunk 4.",
                    "Use verification controls chunk 5.",
                    "Use verification controls chunk 6.",
                    "Use verification controls chunk 7.",
                    "Use verification controls chunk 8."
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["verification"],
            [],
            [],
            [],
            [],
            "verification controls quality review",
            4);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "OCR/manual-review.pdf",
                "manual-review.pdf",
                "ocr",
                PageCount: 5,
                IndexedVersion: 2,
                ExtractionQuality: BuildManualReviewLowTextQuality()),
            baseline,
            ["Quality review"],
            ["The available text mentions verification controls but only sparse text was extracted."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal(8, profile!.Limits.Count);
        Assert.Contains(profile.Limits, limit => limit.Contains("Source quality note", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.Limits, limit => string.Equals(limit, "Use verification controls chunk 8.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_keeps_only_grounded_llm_content_cards()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"en\",\"summary\":\"LLM profile for grounded content.\",\"keywords\":[\"plc exchange\",\"secret dessert calibration\"],\"entities\":[],\"topics\":[\"status data\",\"invented dessert\"],\"questions\":[\"How does IND570 exchange status data?\",\"How does ZX999 tune dessert mode?\"],\"limits\":[\"Use page chunks for exact parameters.\",\"Do not claim ZX999 certification.\"],\"cards\":[{\"title\":\"PLC exchange controls\",\"pageStart\":99,\"pageEnd\":100,\"kind\":\"llm_content_card\",\"signals\":[\"plc exchange\",\"controls\"]},{\"title\":\"Secret dessert calibration\",\"pageStart\":3,\"pageEnd\":3,\"kind\":\"llm_content_card\",\"signals\":[\"invented\",\"dessert\"]}]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Deterministic baseline profile.",
            ["baseline-keyword"],
            [],
            [],
            [],
            [],
            "baseline-keyword",
            1,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Baseline card",
                    2,
                    2,
                    "section",
                    ["baseline-keyword"])
            ]);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Programmation/IND570.pdf",
                "IND570.pdf",
                "programmation",
                PageCount: 42,
                IndexedVersion: 1),
            baseline,
            ["PLC Integration"],
            ["The IND570 exchanges PLC control words and status data."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Contains(profile!.ContentCards, card =>
            string.Equals(card.Title, "PLC exchange controls", StringComparison.Ordinal)
            && card.PageStart is null
            && card.PageEnd is null);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Baseline card", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Secret dessert calibration", StringComparison.Ordinal));
        Assert.Contains("plc exchange", profile.Keywords);
        Assert.DoesNotContain("secret dessert calibration", profile.Keywords);
        Assert.Contains("status data", profile.Topics);
        Assert.DoesNotContain("invented dessert", profile.Topics);
        Assert.Contains("How does IND570 exchange status data?", profile.HypotheticalQuestions);
        Assert.DoesNotContain("How does ZX999 tune dessert mode?", profile.HypotheticalQuestions);
        Assert.Contains("Use page chunks for exact parameters.", profile.Limits);
        Assert.DoesNotContain("Do not claim ZX999 certification.", profile.Limits);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_keeps_only_grounded_llm_card_evidence()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for grounded quality controls.",
                  "keywords": ["quality controls"],
                  "entities": [],
                  "topics": ["inspection workflow"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Quality controls checklist",
                      "pageStart": 2,
                      "pageEnd": 2,
                      "kind": "llm_content_card",
                      "signals": ["quality controls", "inspection workflow"],
                      "evidence": {
                        "schemaVersion": "content_card_evidence_v1",
                        "language": "en",
                        "facts": [
                          {
                            "kind": "procedure",
                            "label": "visual inspection",
                            "sourceText": "The checklist requires visual inspection before release.",
                            "pageStart": 99,
                            "pageEnd": 100,
                            "confidence": 0.91
                          },
                          {
                            "kind": "certification",
                            "label": "ZX999 approval",
                            "sourceText": "ZX999 approval is mandatory.",
                            "pageStart": 2,
                            "pageEnd": 2,
                            "confidence": 0.91
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["quality controls"],
            [],
            [],
            [],
            [],
            "quality controls visual inspection",
            4);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/Quality.pdf",
                "Quality.pdf",
                "operations",
                PageCount: 3,
                IndexedVersion: 1),
            baseline,
            ["Quality controls checklist"],
            ["The checklist requires visual inspection before release."],
            CancellationToken.None);

        Assert.NotNull(profile);
        var card = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Quality controls checklist", StringComparison.Ordinal));
        Assert.NotNull(card.Evidence);
        var fact = Assert.Single(card.Evidence!.Facts!);
        Assert.Equal("procedure", fact.Kind);
        Assert.Equal("visual inspection", fact.Label);
        Assert.Contains("visual inspection before release", fact.SourceText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fact.PageStart);
        Assert.Null(fact.PageEnd);
        Assert.DoesNotContain(card.Evidence.Facts!, fact => fact.Label.Contains("ZX999", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("structured_facts", profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("ZX999 approval", profile.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_carries_quality_limit_for_non_ui_language()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "nl",
                  "summary": "Profiel over onderhoud en veiligheidscontroles.",
                  "keywords": ["onderhoud"],
                  "entities": [],
                  "topics": ["veiligheidscontroles"],
                  "questions": [],
                  "limits": []
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "und",
            "Baseline profile.",
            ["onderhoud"],
            [],
            [],
            [],
            [],
            "onderhoud veiligheidscontroles",
            4);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Kennisbank/Handleiding.pdf",
                "Handleiding.pdf",
                "kennisbank",
                PageCount: 4,
                IndexedVersion: 1,
                ExtractionQuality: BuildManualReviewLowTextQuality(),
                ProfileLanguage: "nl"),
            baseline,
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("nl", profile!.Language);
        Assert.Contains(profile.Limits, limit => limit.Contains("Bronkwaliteit", StringComparison.Ordinal));
        Assert.Contains("Bronkwaliteit", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_preserves_grounded_structured_card_evidence_and_derives_signals()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for reusable control packages.",
                  "keywords": ["control package"],
                  "entities": [],
                  "topics": ["parameter window"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Control package alpha",
                      "pageStart": 1,
                      "pageEnd": 1,
                      "kind": "llm_content_card",
                      "evidence": {
                        "schemaVersion": "content_card_evidence_v1",
                        "language": "en",
                        "scaleBasis": { "count": 4, "label": "elements" },
                        "quantityFacts": [
                          { "value": 400, "unit": "g", "label": "base material", "sourceText": "400 g base material" },
                          { "value": 5, "unit": "cl", "label": "binder", "sourceText": "5 cl binder" }
                        ]
                      }
                    },
                    {
                      "title": "Safety parameter window",
                      "pageStart": 2,
                      "pageEnd": 2,
                      "kind": "llm_content_card",
                      "evidence": {
                        "schemaVersion": "content_card_evidence_v1",
                        "language": "en",
                        "nonScalableReasons": ["safety_or_parameter_context"]
                      }
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["control"],
            [],
            [],
            [],
            [],
            "control package alpha safety parameter window 400 g base material 5 cl binder pressure speed",
            10);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/ControlPackages.pdf",
                "ControlPackages.pdf",
                "operations",
                PageCount: 3,
                IndexedVersion: 1),
            baseline,
            ["Control package alpha", "Safety parameter window"],
            [
                "Control package alpha is defined for 4 elements with 400 g base material and 5 cl binder.",
                "Safety parameter window lists pressure 2 bar and speed 1500 rpm for verification."
            ],
            CancellationToken.None);

        Assert.NotNull(profile);

        var scalableCard = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Control package alpha", StringComparison.Ordinal));
        Assert.NotNull(scalableCard.Evidence);
        Assert.Equal(4, scalableCard.Evidence!.ScaleBasis!.Count);
        Assert.Contains(scalableCard.Evidence.QuantityFacts, fact => string.Equals(fact.Label, "base material", StringComparison.Ordinal));
        Assert.Contains("scale_basis", scalableCard.Signals);
        Assert.Contains("quantity_list", scalableCard.Signals);
        Assert.Contains("scalable_quantities", scalableCard.Signals);

        var parameterCard = Assert.Single(profile.ContentCards, card => string.Equals(card.Title, "Safety parameter window", StringComparison.Ordinal));
        Assert.NotNull(parameterCard.Evidence);
        Assert.Contains("safety_or_parameter_context", parameterCard.Evidence!.NonScalableReasons);
        Assert.Contains("non_scalable_quantities", parameterCard.Signals);
        Assert.DoesNotContain("scalable_quantities", parameterCard.Signals);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_does_not_keep_structured_signals_when_evidence_source_text_is_ungrounded()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for quality controls.",
                  "keywords": ["quality controls"],
                  "entities": [],
                  "topics": ["inspection workflow"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Quality controls checklist",
                      "pageStart": 2,
                      "pageEnd": 2,
                      "kind": "llm_content_card",
                      "signals": ["quality controls", "structured_facts", "scalable_quantities"],
                      "evidence": {
                        "schemaVersion": "content_card_evidence_v1",
                        "language": "en",
                        "facts": [
                          {
                            "kind": "certification",
                            "label": "ZX999 approval",
                            "sourceText": "ZX999 approval is mandatory.",
                            "pageStart": 2,
                            "pageEnd": 2,
                            "confidence": 0.91
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["quality controls"],
            [],
            [],
            [],
            [],
            "quality controls checklist",
            4);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/Quality.pdf",
                "Quality.pdf",
                "operations",
                PageCount: 3,
                IndexedVersion: 1),
            baseline,
            ["Quality controls checklist"],
            ["The checklist requires visual inspection before release."],
            CancellationToken.None);

        Assert.NotNull(profile);
        var card = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Quality controls checklist", StringComparison.Ordinal));
        Assert.Null(card.Evidence);
        Assert.Contains("quality controls", card.Signals);
        Assert.DoesNotContain("structured_facts", card.Signals);
        Assert.DoesNotContain("scalable_quantities", card.Signals);
        Assert.DoesNotContain("structured_facts", profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("ZX999 approval", profile.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_preserves_baseline_card_evidence_when_llm_reuses_title()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for quality controls.",
                  "keywords": ["quality controls"],
                  "entities": [],
                  "topics": ["inspection workflow"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Quality controls checklist",
                      "pageStart": 2,
                      "pageEnd": 2,
                      "kind": "llm_content_card",
                      "signals": ["quality controls"]
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["quality controls"],
            [],
            [],
            [],
            [],
            "quality controls visual inspection",
            4,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Quality controls checklist",
                    2,
                    2,
                    "unit_lead",
                    ["quality controls", "structured_facts"],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        ScaleBasis: null,
                        QuantityFacts: [],
                        NonScalableReasons: [],
                        Confidence: 0.77,
                        Language: "en",
                        Facts:
                        [
                            new DocumentProfileEvidenceFact(
                                "procedure",
                                "visual inspection",
                                null,
                                null,
                                "The checklist requires visual inspection before release.",
                                2,
                                2,
                                0.77)
                        ]))
            ]);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/Quality.pdf",
                "Quality.pdf",
                "operations",
                PageCount: 3,
                IndexedVersion: 1),
            baseline,
            ["Quality controls checklist"],
            ["The checklist requires visual inspection before release."],
            CancellationToken.None);

        Assert.NotNull(profile);
        var card = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Quality controls checklist", StringComparison.Ordinal));
        Assert.NotNull(card.Evidence);
        Assert.Equal(0.77, card.Evidence!.Confidence);
        Assert.Contains(card.Evidence.Facts!, fact => fact.Label.Contains("visual inspection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_preserves_baseline_card_pages_when_llm_reuses_title_without_pages()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for release checks.",
                  "keywords": ["release checks"],
                  "entities": [],
                  "topics": ["release workflow"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Release validation checklist",
                      "kind": "llm_content_card",
                      "signals": ["release checks"]
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["release checks"],
            [],
            [],
            [],
            [],
            "release validation checklist release checks",
            4,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Release validation checklist",
                    6,
                    7,
                    "unit_lead",
                    ["release checks"])
            ]);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/Release.pdf",
                "Release.pdf",
                "operations",
                PageCount: 12,
                IndexedVersion: 1),
            baseline,
            ["Release validation checklist"],
            ["The release validation checklist describes the release checks."],
            CancellationToken.None);

        Assert.NotNull(profile);
        var card = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Release validation checklist", StringComparison.Ordinal));
        Assert.Equal(6, card.PageStart);
        Assert.Equal(7, card.PageEnd);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_derives_card_pages_from_grounded_evidence_facts()
    {
        var service = CreateService(
            BuildChatResponse(
                """
                {
                  "language": "en",
                  "summary": "LLM profile for release checks.",
                  "keywords": ["release checks"],
                  "entities": [],
                  "topics": ["release workflow"],
                  "questions": [],
                  "limits": [],
                  "cards": [
                    {
                      "title": "Release validation checklist",
                      "kind": "llm_content_card",
                      "signals": ["release checks"],
                      "evidence": {
                        "schemaVersion": "content_card_evidence_v1",
                        "language": "en",
                        "facts": [
                          {
                            "kind": "requirement",
                            "label": "release validation",
                            "sourceText": "Release validation requires supervisor approval before shipment.",
                            "pageStart": 5,
                            "pageEnd": 6,
                            "confidence": 0.86
                          },
                          {
                            "kind": "procedure",
                            "label": "release checklist",
                            "sourceText": "The release checklist is archived after approval.",
                            "pageStart": 7,
                            "confidence": 0.82
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "en",
            "Baseline profile.",
            ["release checks"],
            [],
            [],
            [],
            [],
            "release validation release checklist supervisor approval shipment archived",
            8);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                baseline.DocId,
                "Operations/Release.pdf",
                "Release.pdf",
                "operations",
                PageCount: 12,
                IndexedVersion: 1),
            baseline,
            ["Release validation checklist"],
            [
                "Release validation requires supervisor approval before shipment.",
                "The release checklist is archived after approval."
            ],
            CancellationToken.None);

        Assert.NotNull(profile);
        var card = Assert.Single(profile!.ContentCards, card => string.Equals(card.Title, "Release validation checklist", StringComparison.Ordinal));
        Assert.Equal(5, card.PageStart);
        Assert.Equal(7, card.PageEnd);
        Assert.NotNull(card.Evidence);
        Assert.Contains(card.Evidence!.Facts!, fact => fact.PageStart == 5);
        Assert.Contains(card.Evidence.Facts!, fact => fact.PageStart == 7);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_returns_null_when_runtime_is_unavailable()
    {
        var service = CreateService("""{ "error": "runtime_unavailable" }""", HttpStatusCode.ServiceUnavailable);
        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(Guid.NewGuid(), "A/B.pdf", "B.pdf", "a", 1, 1),
            new DocumentProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), "deterministic_v1", "und", "baseline", [], [], [], [], [], "baseline", 1),
            [],
            [],
            CancellationToken.None);

        Assert.Null(profile);
    }

    private static DocumentProfileEnrichmentService CreateService(
        string body,
        HttpStatusCode statusCode,
        Action<string>? captureRequestBody = null)
        => new(new LocalLlmChatClient(
            new StubHttpClientFactory(body, statusCode, captureRequestBody),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));

    private static string BuildChatResponse(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content
                    }
                }
            }
        });

    private static CapabilityBExtractionQualitySnapshot BuildManualReviewLowTextQuality()
        => new(
            Status: "manual_review_low_text",
            TextStatus: "low_text",
            ExtractionConfidence: 0.35,
            ManualReviewRecommended: true,
            OcrAttempted: false,
            OcrApplied: false,
            OcrRecommended: true,
            OcrFailureReason: null,
            PageCount: 5,
            TextPageCount: 1,
            EmptyPageCount: 2,
            SparsePageCount: 3,
            TotalWordCount: 22,
            TotalCharCount: 160,
            TextPageRatio: 0.2,
            Signals: ["low_text_extraction", "ocr_recommended"],
            Source: "document_processing_run");

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode, Action<string>? captureRequestBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode, captureRequestBody))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body, HttpStatusCode statusCode, Action<string>? captureRequestBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (captureRequestBody is not null && request.Content is not null)
                captureRequestBody(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
