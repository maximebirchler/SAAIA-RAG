using System.Linq;
using System.Reflection;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagMatchedContentCardsTests
{
    [Fact]
    public void BuildMatchedContentCards_returns_structured_profile_cards_in_query_order()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Alpha safety checklist",
              "pageStart": 2,
              "pageEnd": 3,
              "kind": "unit_lead",
              "signals": ["alpha", "safety", "checklist"]
            },
            {
              "title": "Beta maintenance appendix",
              "pageStart": 8,
              "pageEnd": 9,
              "kind": "exact_lead",
              "signals": ["beta", "maintenance"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "alpha safety", limit: 1);

        var card = Assert.Single(cards);
        Assert.Equal("Alpha safety checklist", card.Title);
        Assert.Equal(2, card.PageStart);
        Assert.Equal(3, card.PageEnd);
        Assert.Equal("unit_lead", card.Kind);
        Assert.Contains("checklist", card.Signals ?? []);
    }

    [Fact]
    public void BuildMatchedContentCards_keeps_numeric_technical_identifier_titles()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "ISO 13849-1",
              "pageStart": 12,
              "pageEnd": 12,
              "kind": "standard_ref",
              "signals": ["ISO 13849-1", "control safety"]
            },
            {
              "title": "Weekly maintenance overview",
              "pageStart": 3,
              "pageEnd": 4,
              "kind": "section",
              "signals": ["maintenance"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "control safety ISO 13849-1", limit: 2);

        var card = Assert.Single(cards);
        Assert.Equal("ISO 13849-1", card.Title);
        Assert.Equal(12, card.PageStart);
        Assert.Equal("standard_ref", card.Kind);
    }

    [Fact]
    public void BuildMatchedContentCards_returns_empty_for_missing_profile_metadata()
    {
        var cards = RagEndpoints.BuildMatchedContentCards(null, "alpha safety");

        Assert.Empty(cards);
    }

    [Fact]
    public void BuildMatchedContentCards_does_not_fill_limit_with_unrelated_cards()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Alpha safety checklist",
              "pageStart": 2,
              "pageEnd": 3,
              "kind": "unit_lead",
              "signals": ["alpha", "safety", "checklist"]
            },
            {
              "title": "Weekly dessert plan",
              "pageStart": 12,
              "pageEnd": 13,
              "kind": "unit_lead",
              "signals": ["dessert", "menu"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "alpha safety", limit: 2);

        var card = Assert.Single(cards);
        Assert.Equal("Alpha safety checklist", card.Title);
    }

    [Fact]
    public void BuildMatchedContentCards_returns_empty_when_query_has_no_overlap()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Weekly dessert plan",
              "pageStart": 12,
              "pageEnd": 13,
              "kind": "unit_lead",
              "signals": ["dessert", "menu"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "alpha safety", limit: 2);

        Assert.Empty(cards);
    }

    [Fact]
    public void BuildMatchedContentCards_prefers_direct_title_match_over_signal_only_overlap()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Operations appendix",
              "pageStart": 40,
              "pageEnd": 41,
              "kind": "section",
              "signals": ["alpha", "safety", "calibration", "handover"]
            },
            {
              "title": "Alpha safety",
              "pageStart": 7,
              "pageEnd": 8,
              "kind": "unit_lead",
              "signals": ["checklist"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "alpha safety", limit: 2);

        Assert.Equal(["Alpha safety", "Operations appendix"], cards.Select(static card => card.Title));
    }

    [Fact]
    public void BuildMatchedContentCards_can_select_card_from_structured_evidence_only()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Operational appendix",
              "contentCardId": "card-evidence-1",
              "pageStart": 5,
              "pageEnd": 6,
              "kind": "section",
              "signals": ["operations"],
              "evidence": {
                "schemaVersion": "structured_evidence_v1",
                "scaleBasis": { "count": 3, "label": "validation basis" },
                "quantityFacts": [
                  { "value": 9, "unit": "checks", "label": "control points", "sourceText": "9 checks across control points" }
                ],
                "confidence": 0.91
              }
            },
            {
              "title": "Reference glossary",
              "pageStart": 20,
              "pageEnd": 21,
              "kind": "section",
              "signals": ["glossary"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "control points checks validation basis", limit: 2);

        var card = Assert.Single(cards);
        Assert.Equal("Operational appendix", card.Title);
        Assert.Equal("card-evidence-1", card.ContentCardId);
        Assert.True(card.Evidence.HasValue);
        Assert.Equal("structured_evidence_v1", card.Evidence!.Value.GetProperty("schemaVersion").GetString());
        Assert.Equal("control points", card.Evidence.Value.GetProperty("quantityFacts")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void BuildMatchedContentCards_accepts_loose_generic_evidence_shapes()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Inspection checklist",
              "pageStart": 4,
              "pageEnd": 4,
              "kind": "llm_content_card",
              "signals": ["inspection"],
              "evidence": {
                "schemaVersion": "content_card_evidence_v1",
                "language": "en",
                "scaleBasis": { "value": "3", "unit": "checks", "label": "inspection checks" },
                "facts": [
                  {
                    "type": "requirement",
                    "name": "release gate",
                    "value": 3,
                    "unit": "checks",
                    "quote": "Release requires 3 inspection checks.",
                    "page": 4,
                    "confidence": "0.88"
                  }
                ]
              }
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(metadataJson, "release gate inspection checks", limit: 2);

        var card = Assert.Single(cards);
        Assert.True(card.Evidence.HasValue);
        var evidence = card.Evidence!.Value;
        Assert.Equal("en", evidence.GetProperty("language").GetString());
        Assert.Equal(3, evidence.GetProperty("scaleBasis").GetProperty("count").GetInt32());
        Assert.Equal("inspection_checks", evidence.GetProperty("scaleBasis").GetProperty("label").GetString());
        Assert.Equal("requirement", evidence.GetProperty("facts")[0].GetProperty("kind").GetString());
        Assert.Equal("3", evidence.GetProperty("facts")[0].GetProperty("value").GetString());
        Assert.Equal(4, evidence.GetProperty("facts")[0].GetProperty("pageStart").GetInt32());
    }

    [Theory]
    [InlineData("fr", "Repères de contenu")]
    [InlineData("pt", "Pistas de conteúdo")]
    [InlineData("en-US", "Content cues")]
    [InlineData("nl", "Content cues")]
    [InlineData("ar", "Content cues")]
    public void BuildContentCuesLabel_uses_neutral_fallback_for_non_ui_document_languages(
        string language,
        string expected)
    {
        var method = typeof(RagEndpoints).GetMethod(
            "BuildContentCuesLabel",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var label = Assert.IsType<string>(method!.Invoke(null, [language]));

        Assert.Equal(expected, label);
        if (language is "nl" or "ar")
            Assert.DoesNotContain('_', label);
    }

    [Theory]
    [InlineData("fr", "Profil documentaire")]
    [InlineData("en-US", "Profile summary")]
    [InlineData("nl", "Profile summary")]
    public void BuildDocumentProfileSectionTitle_keeps_profile_matches_out_of_raw_internal_labels(
        string language,
        string expected)
    {
        var method = typeof(RagEndpoints).GetMethod(
            "BuildDocumentProfileSectionTitle",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var label = Assert.IsType<string>(method!.Invoke(null, [language]));

        Assert.Equal(expected, label);
        Assert.DoesNotContain("Document profile", label, StringComparison.Ordinal);
        Assert.DoesNotContain('_', label);
    }

    [Fact]
    public void BuildMatchedContentCards_filters_cards_without_page_range_from_page_scoped_hits()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Release gate",
              "pageStart": 2,
              "pageEnd": 2,
              "kind": "llm_content_card",
              "signals": ["release", "gate"],
              "evidence": {
                "schemaVersion": "content_card_evidence_v1",
                "facts": [
                  { "kind": "requirement", "label": "release gate", "sourceText": "Release gate is on page 2.", "pageStart": 2 }
                ]
              }
            },
            {
              "title": "Release gate follow-up",
              "pageStart": 20,
              "pageEnd": 21,
              "kind": "llm_content_card",
              "signals": ["release", "gate", "follow-up"]
            },
            {
              "title": "Document-wide release glossary",
              "kind": "llm_content_card",
              "signals": ["release", "glossary"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(
            metadataJson,
            "release gate follow-up",
            limit: 4,
            pageStart: 20,
            pageEnd: 20);

        Assert.DoesNotContain(cards, card => card.Title == "Release gate");
        Assert.Contains(cards, card => card.Title == "Release gate follow-up");
        Assert.DoesNotContain(cards, card => card.Title == "Document-wide release glossary");
    }

    [Fact]
    public void BuildMatchedContentCards_uses_evidence_derived_page_range_for_page_scoped_hits()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Release validation checklist",
              "kind": "llm_content_card",
              "signals": ["release", "validation"],
              "evidence": {
                "schemaVersion": "content_card_evidence_v1",
                "facts": [
                  { "kind": "requirement", "label": "release validation", "sourceText": "Release validation requires approval.", "pageStart": 12, "pageEnd": 13 }
                ]
              }
            },
            {
              "title": "Document-wide release glossary",
              "kind": "llm_content_card",
              "signals": ["release", "glossary"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(
            metadataJson,
            "release validation approval",
            limit: 4,
            pageStart: 12,
            pageEnd: 12);

        var card = Assert.Single(cards);
        Assert.Equal("Release validation checklist", card.Title);
        Assert.Equal(12, card.PageStart);
        Assert.Equal(13, card.PageEnd);
    }

    [Fact]
    public void BuildMatchedContentCards_keeps_document_level_cards_for_document_profile_hits()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "Document-wide release glossary",
              "kind": "llm_content_card",
              "signals": ["release", "glossary"]
            }
          ]
        }
        """;

        var cards = RagEndpoints.BuildMatchedContentCards(
            metadataJson,
            "release glossary",
            limit: 4);

        var card = Assert.Single(cards);
        Assert.Equal("Document-wide release glossary", card.Title);
        Assert.Null(card.PageStart);
    }
}
