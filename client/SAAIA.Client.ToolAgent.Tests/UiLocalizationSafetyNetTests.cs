using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Controls;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class UiLocalizationSafetyNetTests
{
    [Theory]
    [InlineData("fr", "Sources", "Ouvrir")]
    [InlineData("en", "Sources", "Open")]
    [InlineData("es", "Fuentes", "Abrir")]
    [InlineData("pt", "Fontes", "Abrir")]
    [InlineData("de", "Quellen", "Oeffnen")]
    [InlineData("it", "Fonti", "Apri")]
    public void Sources_cards_labels_are_localized(string language, string expectedHeader, string expectedButton)
    {
        Assert.Equal(expectedHeader, SourcesCardsControl.GetSourcesHeaderText(language));
        Assert.Equal(expectedButton, SourcesCardsControl.GetOpenButtonText(language));
    }

    [Theory]
    [InlineData("fr", "p. 2-4", "score 0.876", "langue document allemand", "langue profil anglais", "qualité texte faible, revue requise 86%", "OCR recommandé", "revue recommandée", "rév. abcdef1234", "cartes Controle source")]
    [InlineData("en", "p. 2-4", "score 0.876", "document language German", "profile language English", "quality low text, review required 86%", "OCR recommended", "review recommended", "rev abcdef1234", "cards Controle source")]
    [InlineData("es", "p. 2-4", "score 0.876", "idioma del documento alemán", "idioma del perfil inglés", "calidad texto insuficiente, revisión requerida 86%", "OCR recomendado", "revisión recomendada", "rev. abcdef1234", "tarjetas Controle source")]
    [InlineData("pt", "p. 2-4", "score 0.876", "idioma do documento alemão", "idioma do perfil inglês", "qualidade texto insuficiente, revisão necessária 86%", "OCR recomendado", "revisão recomendada", "rev. abcdef1234", "cartões Controle source")]
    [InlineData("de", "S. 2-4", "Score 0.876", "Dokumentsprache Deutsch", "Profilsprache Englisch", "Qualität wenig Text, Prüfung nötig 86%", "OCR empfohlen", "Prüfung empfohlen", "Rev. abcdef1234", "Karten Controle source")]
    [InlineData("it", "p. 2-4", "score 0.876", "lingua documento tedesco", "lingua profilo inglese", "qualità testo scarso, revisione richiesta 86%", "OCR consigliato", "revisione consigliata", "rev. abcdef1234", "schede Controle source")]
    public void Sources_cards_metadata_badges_are_localized(
        string language,
        string expectedPages,
        string expectedScore,
        string expectedDocumentLanguage,
        string expectedProfileLanguage,
        string expectedQuality,
        string expectedOcr,
        string expectedReview,
        string expectedHash,
        string expectedCards)
    {
        var source = new SourceCard
        {
            PageStart = 2,
            PageEnd = 4,
            Score = 0.876,
            DocLanguage = "de",
            ProfileLanguage = "en",
            PageQualityStatus = "manual_review_low_text",
            ExtractionConfidence = 0.86,
            ManualReviewRecommended = true,
            OcrRecommended = true,
            SourceHash = "abcdef1234567890",
            MatchedContentCards = new()
            {
                new SourceContentCard { Title = "Controle source" }
            }
        };

        Assert.Equal(expectedPages, SourcesCardsControl.GetPagesLabel(source, language));
        Assert.Equal(expectedScore, SourcesCardsControl.GetScoreLabel(source, language));
        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);
        Assert.Contains(expectedDocumentLanguage, metadata);
        Assert.Contains(expectedProfileLanguage, metadata);
        Assert.Contains(expectedQuality, metadata);
        Assert.Contains(expectedOcr, metadata);
        Assert.Contains(expectedReview, metadata);
        Assert.Contains(expectedHash, metadata);
        Assert.Contains(expectedCards, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "cartes ID", "preuve cartes 88%")]
    [InlineData("en", "card IDs", "card evidence 88%")]
    [InlineData("es", "ID tarjetas", "evidencia tarjetas 88%")]
    [InlineData("pt", "IDs cartões", "evidência cartões 88%")]
    [InlineData("de", "Karten-IDs", "Kartenbeleg 88%")]
    [InlineData("it", "ID schede", "evidenza schede 88%")]
    public void Sources_cards_metadata_shows_content_card_ids_and_evidence(
        string language,
        string expectedIdsLabel,
        string expectedEvidence)
    {
        using var evidence = JsonDocument.Parse("""{"schemaVersion":"debug_card_v1","confidence":0.88}""");
        var source = new SourceCard
        {
            MatchedContentCards = new()
            {
                new SourceContentCard
                {
                    Title = "Controle source",
                    ContentCardId = "card-abcdef123456",
                    Evidence = evidence.RootElement.Clone()
                }
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedIdsLabel, metadata);
        Assert.Contains("card-abcde", metadata);
        Assert.Contains(expectedEvidence, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "preuve cartes faits 2, 91%")]
    [InlineData("en", "card evidence facts 2, 91%")]
    [InlineData("es", "evidencia tarjetas hechos 2, 91%")]
    [InlineData("pt", "evidência cartões factos 2, 91%")]
    [InlineData("de", "Kartenbeleg Fakten 2, 91%")]
    [InlineData("it", "evidenza schede fatti 2, 91%")]
    public void Sources_cards_metadata_localizes_generic_content_card_facts(
        string language,
        string expectedEvidence)
    {
        using var evidence = JsonDocument.Parse(
            """
            {
              "schemaVersion": "content_card_evidence_v1",
              "confidence": 0.91,
              "facts": [
                { "kind": "procedure", "label": "controle visuel" },
                { "kind": "requirement", "label": "validation" }
              ]
            }
            """);
        var source = new SourceCard
        {
            MatchedContentCards = new()
            {
                new SourceContentCard
                {
                    Title = "Controle source",
                    Evidence = evidence.RootElement.Clone()
                }
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedEvidence, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "preuve cartes faits 2, 91%, base 4 éléments, non adaptable 2, langue anglais")]
    [InlineData("en", "card evidence facts 2, 91%, basis 4 elements, non-scalable 2, language English")]
    [InlineData("es", "evidencia tarjetas hechos 2, 91%, base 4 elementos, no adaptable 2, idioma ingl\u00e9s")]
    [InlineData("pt", "evid\u00eancia cart\u00f5es factos 2, 91%, base 4 elementos, n\u00e3o adapt\u00e1vel 2, idioma ingl\u00eas")]
    [InlineData("de", "Kartenbeleg Fakten 2, 91%, Basis 4 Elemente, nicht skalierbar 2, Sprache Englisch")]
    [InlineData("it", "evidenza schede fatti 2, 91%, base 4 elementi, non scalabile 2, lingua inglese")]
    public void Sources_cards_metadata_summarizes_rich_content_card_evidence_without_raw_source_text(
        string language,
        string expectedEvidence)
    {
        using var evidence = JsonDocument.Parse(
            """
            {
              "schemaVersion": "content_card_evidence_v1",
              "language": "en",
              "confidence": 0.91,
              "scaleBasis": { "count": 4, "label": "elements" },
              "quantityFacts": [
                { "kind": "quantity", "label": "control", "value": "3", "sourceText": "Do not display this raw evidence." },
                { "kind": "quantity", "label": "review", "value": "2" }
              ],
              "nonScalableReasons": [ "temperature", "time" ]
            }
            """);
        var source = new SourceCard
        {
            DocLanguage = "fr",
            MatchedContentCards = new()
            {
                new SourceContentCard
                {
                    Title = "Controle source",
                    Evidence = evidence.RootElement.Clone()
                }
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedEvidence, metadata);
        Assert.DoesNotContain("Do not display this raw evidence", metadata, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "valeurs control 3; review 2", "raisons contexte s\u00e9curit\u00e9 ou param\u00e8tre; contrainte de temps")]
    [InlineData("en", "values control 3; review 2", "reasons safety or parameter context; time constraint")]
    [InlineData("es", "valores control 3; review 2", "razones contexto de seguridad o par\u00e1metro; restricci\u00f3n de tiempo")]
    [InlineData("pt", "valores control 3; review 2", "raz\u00f5es contexto de seguran\u00e7a ou par\u00e2metro; restri\u00e7\u00e3o de tempo")]
    [InlineData("de", "Werte control 3; review 2", "Gr\u00fcnde Sicherheits- oder Parameterkontext; Zeitvorgabe")]
    [InlineData("it", "valori control 3; review 2", "ragioni contesto di sicurezza o parametro; vincolo di tempo")]
    public void Sources_cards_metadata_shows_safe_fact_values_and_localized_reasons(
        string language,
        string expectedValues,
        string expectedReasons)
    {
        using var evidence = JsonDocument.Parse(
            """
            {
              "schemaVersion": "content_card_evidence_v1",
              "confidence": 0.91,
              "quantityFacts": [
                { "kind": "quantity", "label": "control", "value": "3", "sourceText": "Do not display this raw evidence." },
                { "kind": "quantity", "label": "review", "value": 2 }
              ],
              "nonScalableReasons": [ "technical_parameter_context", "time_limit" ]
            }
            """);
        var source = new SourceCard
        {
            MatchedContentCards = new()
            {
                new SourceContentCard
                {
                    Title = "Controle source",
                    Evidence = evidence.RootElement.Clone()
                }
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedValues, metadata);
        Assert.Contains(expectedReasons, metadata);
        Assert.DoesNotContain("Do not display this raw evidence", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("technical parameter context", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("time limit", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("technical_parameter_context", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("time_limit", metadata, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "raisons contrainte documentee")]
    [InlineData("en", "reasons documented constraint")]
    [InlineData("es", "razones restriccion documentada")]
    [InlineData("pt", "raz\u00f5es restricao documentada")]
    [InlineData("de", "Gr\u00fcnde dokumentierte Vorgabe")]
    [InlineData("it", "ragioni vincolo documentato")]
    public void Sources_cards_metadata_hides_unknown_backend_reason_codes(
        string language,
        string expectedReasons)
    {
        using var evidence = JsonDocument.Parse(
            """
            {
              "schemaVersion": "content_card_evidence_v1",
              "nonScalableReasons": [ "future_backend_reason" ]
            }
            """);
        var source = new SourceCard
        {
            MatchedContentCards = new()
            {
                new SourceContentCard
                {
                    Title = "Controle source",
                    Evidence = evidence.RootElement.Clone()
                }
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedReasons, metadata);
        Assert.DoesNotContain("future_backend_reason", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future backend reason", metadata, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "OCR tent\u00e9", "OCR appliqu\u00e9", "OCR recommand\u00e9")]
    [InlineData("en", "OCR attempted", "OCR applied", "OCR recommended")]
    [InlineData("es", "OCR intentado", "OCR aplicado", "OCR recomendado")]
    [InlineData("pt", "OCR tentado", "OCR aplicado", "OCR recomendado")]
    [InlineData("de", "OCR versucht", "OCR angewendet", "OCR empfohlen")]
    [InlineData("it", "OCR tentato", "OCR applicato", "OCR consigliato")]
    public void Sources_cards_metadata_shows_attempted_applied_and_recommended_ocr_together(
        string language,
        string expectedAttempted,
        string expectedApplied,
        string expectedRecommended)
    {
        var source = new SourceCard
        {
            OcrAttempted = true,
            OcrApplied = true,
            OcrRecommended = true
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedAttempted, metadata);
        Assert.Contains(expectedApplied, metadata);
        Assert.Contains(expectedRecommended, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "langue allemand")]
    [InlineData("en", "language German")]
    [InlineData("es", "idioma alemán")]
    [InlineData("pt", "idioma alemão")]
    [InlineData("de", "Sprache Deutsch")]
    [InlineData("it", "lingua tedesco")]
    public void Sources_cards_metadata_uses_single_language_label_when_document_and_profile_match(
        string language,
        string expectedLanguage)
    {
        var source = new SourceCard
        {
            DocLanguage = "de",
            ProfileLanguage = "de",
            PageQualityStatus = "page_ok_with_images",
            OcrApplied = true
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedLanguage, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Sources_cards_metadata_does_not_expose_raw_quality_or_ocr_snake_case(string language)
    {
        var source = new SourceCard
        {
            DocLanguage = "it",
            ProfileLanguage = "en",
            DocumentQualityStatus = "ocr_applied_ok",
            PageQualityStatus = "manual_review_low_text",
            TextStatus = "low_text",
            OcrRecommended = true,
            ManualReviewRecommended = true
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("extraction_ok")]
    [InlineData("extraction_ok_with_page_review")]
    [InlineData("ocr_failed_or_insufficient")]
    [InlineData("text_extraction_ok_with_images")]
    [InlineData("manual_review_probable_ocr_noise")]
    [InlineData("page_ok_indexed_by_context")]
    [InlineData("page_ok_empty_text")]
    [InlineData("page_ok_low_value_text")]
    [InlineData("empty_text")]
    [InlineData("manual_review_text_not_indexed")]
    public void Sources_cards_metadata_localizes_backend_quality_statuses_without_raw_identifiers(string status)
    {
        foreach (var language in new[] { "fr", "en", "es", "pt", "de", "it" })
        {
            var source = new SourceCard
            {
                PageQualityStatus = status,
                OcrApplied = true,
                OcrRecommended = true
            };

            var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

            AssertNoRawSourceCardMetadata(metadata);
            Assert.Contains(SourcesCardsControl.GetMetadataLabel(new SourceCard { OcrApplied = true }, language), metadata);
            Assert.Contains(SourcesCardsControl.GetMetadataLabel(new SourceCard { OcrRecommended = true }, language), metadata);
        }
    }

    [Fact]
    public void Sources_cards_metadata_humanizes_unknown_quality_identifiers()
    {
        var source = new SourceCard
        {
            DocLanguage = "nl",
            QualityStatus = "needs_layout_review"
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, "en");

        Assert.Contains("Needs layout review", metadata);
        Assert.DoesNotContain("needs_layout_review", metadata);
        Assert.DoesNotMatch("[a-z]+_[a-z_]+", metadata);
    }

    [Fact]
    public void Sources_cards_metadata_localizes_camel_case_quality_identifiers_without_raw_labels()
    {
        foreach (var language in new[] { "fr", "en", "es", "pt", "de", "it" })
        {
            var camelCase = SourcesCardsControl.GetMetadataLabel(
                new SourceCard { QualityStatus = "manualReviewLowText" },
                language);
            var snakeCase = SourcesCardsControl.GetMetadataLabel(
                new SourceCard { QualityStatus = "manual_review_low_text" },
                language);

            Assert.Equal(snakeCase, camelCase);
            AssertNoRawSourceCardMetadata(camelCase);
            Assert.DoesNotContain("manualReviewLowText", camelCase, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sources_cards_metadata_keeps_category_with_other_badges()
    {
        var source = new SourceCard
        {
            DocLanguage = "en",
            PageQualityStatus = "page_ok",
            SourceHash = "abc123456789",
            CategoryPath = "Knowledge/Manuals",
            SelectionHintEvidenceRole = "supporting_context",
            SelectionHintSupportScore = 84,
            SelectionHintQualityPenalty = 1
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, "en");

        Assert.Contains("language English", metadata);
        Assert.Contains("quality page OK", metadata);
        Assert.Contains("evidence context", metadata);
        Assert.Contains("selection 83", metadata);
        Assert.Contains("rev abc1234567", metadata);
        Assert.Contains("category Knowledge/Manuals", metadata);
        Assert.DoesNotContain("supporting_context", metadata);
    }

    [Fact]
    public void Sources_cards_metadata_shows_distinct_page_and_document_quality()
    {
        var source = new SourceCard
        {
            PageQualityStatus = "manual_review_low_text",
            PageExtractionConfidence = 0.35,
            DocumentQualityStatus = "extraction_ok",
            DocumentExtractionConfidence = 0.91
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, "en");

        Assert.Contains("page quality low text, review required 35%", metadata);
        Assert.Contains("document quality extraction OK 91%", metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Fact]
    public void Sources_cards_metadata_uses_category_ref_when_category_path_is_missing()
    {
        var metadata = SourcesCardsControl.GetMetadataLabel(
            new SourceCard
            {
                CategoryRef = "cat_042",
                SelectionHintActionabilityScore = 77
            },
            "en");

        Assert.Contains("category cat_042", metadata);
        Assert.Contains("selection 77", metadata);
    }

    [Theory]
    [InlineData("fragment", 64, 7, "selection 57")]
    [InlineData("navigation", 51, 9, "selection 42")]
    public void Sources_cards_metadata_uses_fragment_and_navigation_scores(string role, int score, int penalty, string expected)
    {
        var source = new SourceCard
        {
            SelectionHintEvidenceRole = role,
            SelectionHintFragmentScore = role == "fragment" ? score : null,
            SelectionHintNavigationScore = role == "navigation" ? score : null,
            SelectionHintQualityPenalty = penalty
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, "en");

        Assert.Contains(expected, metadata);
    }

    [Theory]
    [InlineData("fr", "preuve conseil")]
    [InlineData("en", "evidence advisory")]
    [InlineData("es", "evidencia asesoría")]
    [InlineData("pt", "evidencia aconselhamento")]
    [InlineData("de", "Beleg Hinweis")]
    [InlineData("it", "evidenza consiglio")]
    public void Sources_cards_metadata_localizes_advisory_selection_role(string language, string expected)
    {
        var metadata = SourcesCardsControl.GetMetadataLabel(
            new SourceCard { SelectionHintEvidenceRole = "advisory" },
            language);

        Assert.Contains(expected, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Fact]
    public void Source_card_parser_reads_quality_ocr_language_hash_and_cards()
    {
        const string json = """
        {
          "sources": [
            {
              "docPath": "Knowledge/manual.pdf",
              "docName": "manual.pdf",
              "pageStart": 3,
              "pageEnd": 4,
              "sourceHash": "hash-abcdef",
              "docLanguage": "it",
              "profileLanguage": "en",
              "categoryPath": "Knowledge/Manuals",
              "extractionQuality": {
                "extractionSource": "pdf_text_plus_image_ocr",
                "documentQualityStatus": "ocr_applied_ok",
                "pageQualityStatus": "manual_review_low_text",
                "textStatus": "low_text",
                "extractionConfidence": 0.73,
                "manualReviewRecommended": true,
                "ocrAttempted": true,
                "ocrApplied": true,
                "ocrRecommended": true,
                "signals": [ "low_text", "image_text" ]
              },
              "matchedContentCards": [
                { "title": "Controle source", "kind": "procedure", "pageStart": 3, "signals": [ "title_match" ] }
              ],
              "contentSignals": {
                "contentRole": "mixed_navigation_content",
                "navigationReason": "inline_page_number_list",
                "navigationScore": 0.42,
                "contentDensityScore": 0.76
              },
              "selectionHints": {
                "evidenceRole": "actionable_item",
                "actionabilityScore": 9,
                "supportScore": 4,
                "fragmentScore": 1,
                "navigationScore": 0,
                "qualityPenalty": 2
              }
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("hash-abcdef", card.SourceHash);
        Assert.Equal("it", card.DocLanguage);
        Assert.Equal("en", card.ProfileLanguage);
        Assert.Equal("Knowledge/Manuals", card.CategoryPath);
        Assert.Equal("pdf_text_plus_image_ocr", card.ExtractionSource);
        Assert.Equal("ocr_applied_ok", card.DocumentQualityStatus);
        Assert.Equal("manual_review_low_text", card.PageQualityStatus);
        Assert.Equal("low_text", card.TextStatus);
        Assert.Equal(0.73, card.ExtractionConfidence);
        Assert.True(card.ManualReviewRecommended);
        Assert.True(card.OcrAttempted);
        Assert.True(card.OcrApplied);
        Assert.True(card.OcrRecommended);
        Assert.Contains("image_text", card.QualitySignals);
        Assert.Equal("Controle source", Assert.Single(card.MatchedContentCards).Title);
        Assert.Equal("actionable_item", card.SelectionHintEvidenceRole);
        Assert.Equal(9, card.SelectionHintActionabilityScore);
        Assert.Equal(2, card.SelectionHintQualityPenalty);
        Assert.Equal("mixed_navigation_content", card.ContentRole);
        Assert.Equal("inline_page_number_list", card.NavigationReason);
        Assert.Equal(0.42, card.RetrievalNavigationScore);
        Assert.Equal(0.76, card.ContentDensityScore);
    }

    [Fact]
    public void Source_card_parser_keeps_richest_duplicate_source_card()
    {
        const string json = """
        {
          "sources": [
            {
              "docPath": "Knowledge/manual.pdf",
              "docName": "manual.pdf",
              "pageStart": 3,
              "pageEnd": 4,
              "snippet": "same evidence"
            },
            {
              "docPath": "Knowledge/manual.pdf",
              "docName": "manual.pdf",
              "pageStart": 3,
              "pageEnd": 4,
              "snippet": "same evidence",
              "sourceHash": "hash-rich",
              "docLanguage": "en",
              "profileLanguage": "fr",
              "extractionQuality": {
                "extractionSource": "pdf_text_plus_image_ocr",
                "pageQualityStatus": "page_ok",
                "ocrApplied": true
              },
              "matchedContentCards": [
                { "title": "Rich card", "kind": "section" }
              ],
              "selectionHints": {
                "evidenceRole": "supporting_context",
                "supportScore": 8
              }
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("hash-rich", card.SourceHash);
        Assert.Equal("en", card.DocLanguage);
        Assert.Equal("fr", card.ProfileLanguage);
        Assert.Equal("pdf_text_plus_image_ocr", card.ExtractionSource);
        Assert.True(card.OcrApplied);
        Assert.Equal("Rich card", Assert.Single(card.MatchedContentCards).Title);
        Assert.Equal("supporting_context", card.SelectionHintEvidenceRole);
        Assert.Equal(8, card.SelectionHintSupportScore);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("items")]
    [InlineData("merged")]
    [InlineData("hits")]
    [InlineData("matches")]
    [InlineData("rootArray")]
    [InlineData("searches")]
    public void Source_card_parser_reads_supported_payload_wrappers(string wrapper)
    {
        var card = Assert.Single(SourceCardParser.Parse(BuildWrappedSourcePayload(wrapper)));

        Assert.Equal("Knowledge/manual.pdf", card.DocPath);
        Assert.Equal("hash-abcdef", card.SourceHash);
        Assert.Equal("en", card.DocLanguage);
        Assert.Equal("ocr_applied_ok", card.DocumentQualityStatus);
        Assert.Equal("Control source", Assert.Single(card.MatchedContentCards).Title);
    }

    [Fact]
    public void Source_card_parser_uses_source_label_as_display_name()
    {
        const string json = """
        {
          "sources": [
            {
              "docPath": "Knowledge/manual.pdf",
              "label": "Resolved display label",
              "sourceHash": "hash-label"
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("Knowledge/manual.pdf", card.DocPath);
        Assert.Equal("Resolved display label", card.DocName);
        Assert.Equal("hash-label", card.SourceHash);
    }

    [Fact]
    public void Source_card_parser_reads_raw_source_resolve_payload()
    {
        const string json = """
        {
          "requestedRef": "reference-item",
          "source": {
            "docId": "doc-alpha",
            "docPath": "workspace/reference-item.txt",
            "docName": "reference-item.txt",
            "pageStart": 7,
            "pageEnd": 8,
            "chunkId": "segment-alpha",
            "sourceHash": "hash-alpha",
            "docLanguage": "en",
            "profileLanguage": "fr",
            "categoryRef": "cat_007",
            "categoryPath": "workspace",
            "extractionQuality": {
              "extractionSource": "text_extraction",
              "documentQualityStatus": "document_ok",
              "pageQualityStatus": "page_ok",
              "textStatus": "ok",
              "documentExtractionConfidence": 0.82,
              "pageExtractionConfidence": 0.91,
              "documentManualReviewRecommended": false,
              "pageManualReviewRecommended": true,
              "ocrAttempted": true,
              "ocrApplied": false,
              "ocrRecommended": false,
              "signals": [ "structured_text", "signal_2", "signal_3", "signal_4", "signal_5", "signal_6" ]
            },
            "matchedContentCards": [
              {
                "title": "Section alpha",
                "pageStart": 7,
                "pageEnd": 8,
                "kind": "section",
                "signals": [ "heading_match" ]
              }
            ],
            "selectionHints": {
              "evidenceRole": "supporting_context",
              "actionabilityScore": 5,
              "supportScore": 8,
              "fragmentScore": 1,
              "navigationScore": 0,
              "qualityPenalty": 3
            }
          }
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("doc-alpha", card.DocId);
        Assert.Equal("workspace/reference-item.txt", card.DocPath);
        Assert.Equal("reference-item.txt", card.DocName);
        Assert.Equal(7, card.PageStart);
        Assert.Equal(8, card.PageEnd);
        Assert.Equal("segment-alpha", card.ChunkId);
        Assert.Equal("hash-alpha", card.SourceHash);
        Assert.Equal("en", card.DocLanguage);
        Assert.Equal("fr", card.ProfileLanguage);
        Assert.Equal("cat_007", card.CategoryRef);
        Assert.Equal("workspace", card.CategoryPath);
        Assert.Equal("text_extraction", card.ExtractionSource);
        Assert.Equal("document_ok", card.DocumentQualityStatus);
        Assert.Equal("page_ok", card.PageQualityStatus);
        Assert.Equal("ok", card.TextStatus);
        Assert.Equal("page_ok", card.QualityStatus);
        Assert.Equal(0.91, card.ExtractionConfidence);
        Assert.Equal(0.82, card.DocumentExtractionConfidence);
        Assert.Equal(0.91, card.PageExtractionConfidence);
        Assert.True(card.ManualReviewRecommended);
        Assert.False(card.DocumentManualReviewRecommended);
        Assert.True(card.PageManualReviewRecommended);
        Assert.True(card.OcrAttempted);
        Assert.False(card.OcrApplied);
        Assert.False(card.OcrRecommended);
        Assert.Contains("structured_text", card.QualitySignals);
        Assert.Contains("signal_6", card.QualitySignals);
        var contentCard = Assert.Single(card.MatchedContentCards);
        Assert.Equal("Section alpha", contentCard.Title);
        Assert.Equal("section", contentCard.Kind);
        Assert.Contains("heading_match", contentCard.Signals);
        Assert.Equal("supporting_context", card.SelectionHintEvidenceRole);
        Assert.Equal(5, card.SelectionHintActionabilityScore);
        Assert.Equal(8, card.SelectionHintSupportScore);
        Assert.Equal(3, card.SelectionHintQualityPenalty);
    }

    [Fact]
    public void Source_card_parser_reads_snake_case_quality_and_cards()
    {
        const string json = """
        {
          "Sources": [
            {
              "doc_path": "Knowledge/manual.pdf",
              "doc_name": "manual.pdf",
              "page_start": "5",
              "page_end": "6",
              "source_hash": "hash-snake",
              "doc_language": "de",
              "profile_language": "de",
              "category_ref": "cat_001",
              "category_path": "Knowledge/Manuals",
              "chunk_id": "chunk-snake",
              "extraction_quality": {
                "extraction_source": "pdf_text_plus_image_ocr",
                "document_quality_status": "ocr_applied_ok",
                "page_quality_status": "page_ok_with_images",
                "text_status": "ok",
                "page_extraction_confidence": "0.91",
                "page_manual_review_recommended": "false",
                "ocr_attempted": "true",
                "ocr_applied": "true",
                "ocr_recommended": "false",
                "signals": [ "page_contains_images" ]
              },
              "matched_content_cards": [
                { "title": "Snake card", "kind": "procedure", "page_start": 5, "signals": [ "structured_item" ] }
              ]
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("hash-snake", card.SourceHash);
        Assert.Equal("de", card.DocLanguage);
        Assert.Equal("de", card.ProfileLanguage);
        Assert.Equal("cat_001", card.CategoryRef);
        Assert.Equal("chunk-snake", card.ChunkId);
        Assert.Equal("pdf_text_plus_image_ocr", card.ExtractionSource);
        Assert.Equal("ocr_applied_ok", card.DocumentQualityStatus);
        Assert.Equal("page_ok_with_images", card.PageQualityStatus);
        Assert.Equal("ok", card.TextStatus);
        Assert.Equal(0.91, card.ExtractionConfidence);
        Assert.True(card.OcrAttempted);
        Assert.True(card.OcrApplied);
        Assert.Contains("page_contains_images", card.QualitySignals);
        Assert.Equal("Snake card", Assert.Single(card.MatchedContentCards).Title);
    }

    [Fact]
    public void Source_card_parser_reads_content_cards_alias()
    {
        const string json = """
        {
          "sources": [
            {
              "docPath": "Knowledge/manual.pdf",
              "docName": "manual.pdf",
              "sourceHash": "hash-content-cards",
              "contentCards": [
                { "title": "Alias content card", "kind": "section", "pageStart": 2, "signals": [ "profile_card" ] }
              ]
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));
        var contentCard = Assert.Single(card.MatchedContentCards);

        Assert.Equal("hash-content-cards", card.SourceHash);
        Assert.Equal("Alias content card", contentCard.Title);
        Assert.Equal("section", contentCard.Kind);
        Assert.Contains("profile_card", contentCard.Signals);
    }

    [Fact]
    public void Source_card_parser_reads_pascal_case_quality_confidence_and_review_fields()
    {
        const string json = """
        {
          "sources": [
            {
              "docPath": "Knowledge/manual.pdf",
              "docName": "manual.pdf",
              "extractionQuality": {
                "DocumentQualityStatus": "ocr_failed_or_insufficient",
                "PageQualityStatus": "manual_review_low_text",
                "DocumentExtractionConfidence": 0.42,
                "DocumentManualReviewRecommended": true,
                "OcrAttempted": true,
                "OcrApplied": false,
                "OcrRecommended": true
              }
            }
          ]
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("ocr_failed_or_insufficient", card.DocumentQualityStatus);
        Assert.Equal("manual_review_low_text", card.PageQualityStatus);
        Assert.Equal(0.42, card.ExtractionConfidence);
        Assert.True(card.ManualReviewRecommended);
        Assert.True(card.OcrAttempted);
        Assert.False(card.OcrApplied);
        Assert.True(card.OcrRecommended);
    }

    [Fact]
    public void Source_card_parser_reads_extraction_diagnostic_summary()
    {
        const string json = """
        {
          "source": {
            "docPath": "Knowledge/manual.pdf",
            "docName": "manual.pdf",
            "extractionQuality": {
              "documentQualityStatus": "ocr_failed_or_insufficient",
              "diagnosticSummary": {
                "nativeTextStatus": "empty_text",
                "nativeOcrRecommended": true,
                "ocrMode": "image_page",
                "ocrLanguages": "fra+eng",
                "ocrDurationMs": 1234,
                "ocrFailureReason": "ocr_failed",
                "ocrAppliedReason": "image_ocr_no_novel_text",
                "ocrTimedOut": true,
                "ocrAttemptedPageCount": 3,
                "ocrSkippedPageCount": 2,
                "ocrPagesWithNovelTextCount": 1,
                "pageCount": 5,
                "textPageCount": 1,
                "emptyPageCount": 2,
                "sparsePageCount": 2,
                "imagePageCount": 4,
                "pageWarningCount": 1,
                "pageReviewRecommendedCount": 2
              }
            }
          }
        }
        """;

        var card = Assert.Single(SourceCardParser.Parse(json));

        Assert.Equal("ocr_failed_or_insufficient", card.DocumentQualityStatus);
        Assert.NotNull(card.ExtractionDiagnosticSummary);
        var diagnostics = card.ExtractionDiagnosticSummary!;
        Assert.Equal("empty_text", diagnostics.NativeTextStatus);
        Assert.True(diagnostics.NativeOcrRecommended);
        Assert.Equal("image_page", diagnostics.OcrMode);
        Assert.Equal("fra+eng", diagnostics.OcrLanguages);
        Assert.Equal(1234, diagnostics.OcrDurationMs);
        Assert.Equal("ocr_failed", diagnostics.OcrFailureReason);
        Assert.Equal("image_ocr_no_novel_text", diagnostics.OcrAppliedReason);
        Assert.True(diagnostics.OcrTimedOut);
        Assert.Equal(3, diagnostics.OcrAttemptedPageCount);
        Assert.Equal(2, diagnostics.OcrSkippedPageCount);
        Assert.Equal(1, diagnostics.OcrPagesWithNovelTextCount);
        Assert.Equal(5, diagnostics.PageCount);
        Assert.Equal(1, diagnostics.TextPageCount);
        Assert.Equal(2, diagnostics.EmptyPageCount);
        Assert.Equal(2, diagnostics.SparsePageCount);
        Assert.Equal(4, diagnostics.ImagePageCount);
        Assert.Equal(1, diagnostics.PageWarningCount);
        Assert.Equal(2, diagnostics.PageReviewRecommendedCount);
    }

    [Theory]
    [InlineData("fr", "incident OCR OCR échoué", "raison OCR aucun texte image utile", "pages OCR 3+2", "pages OCR utiles 1", "texte natif texte vide", "pages à revoir 2", "pages avec alerte 1", "pages image 4")]
    [InlineData("en", "OCR issue OCR failed", "OCR reason no useful image text", "OCR pages 3+2", "useful OCR pages 1", "native text empty text", "pages to review 2", "warning pages 1", "image pages 4")]
    [InlineData("es", "incidencia OCR OCR fallido", "motivo OCR sin texto útil en imagen", "páginas OCR 3+2", "páginas OCR útiles 1", "texto nativo texto vacío", "páginas a revisar 2", "páginas con aviso 1", "páginas con imagen 4")]
    [InlineData("pt", "incidente OCR OCR falhou", "motivo OCR sem texto útil na imagem", "páginas OCR 3+2", "páginas OCR úteis 1", "texto nativo texto vazio", "páginas a rever 2", "páginas com aviso 1", "páginas com imagem 4")]
    [InlineData("de", "OCR-Hinweis OCR fehlgeschlagen", "OCR-Grund kein nützlicher Bildtext", "OCR-Seiten 3+2", "nützliche OCR-Seiten 1", "nativer Text leerer Text", "Seiten zur Prüfung 2", "Warnseiten 1", "Bildseiten 4")]
    [InlineData("it", "problema OCR OCR non riuscito", "motivo OCR nessun testo immagine utile", "pagine OCR 3+2", "pagine OCR utili 1", "testo nativo testo vuoto", "pagine da rivedere 2", "pagine con avviso 1", "pagine immagine 4")]
    public void Sources_cards_metadata_shows_localized_extraction_diagnostics(
        string language,
        string expectedFailure,
        string expectedReason,
        string expectedOcrPages,
        string expectedNovelPages,
        string expectedNativeText,
        string expectedReviewPages,
        string expectedWarningPages,
        string expectedImagePages)
    {
        var source = new SourceCard
        {
            ExtractionDiagnosticSummary = new SourceExtractionDiagnosticSummary
            {
                NativeTextStatus = "empty_text",
                OcrFailureReason = "ocr_failed",
                OcrAppliedReason = "image_ocr_no_novel_text",
                OcrAttemptedPageCount = 3,
                OcrSkippedPageCount = 2,
                OcrPagesWithNovelTextCount = 1,
                ImagePageCount = 4,
                PageWarningCount = 1,
                PageReviewRecommendedCount = 2
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedFailure, metadata);
        Assert.Contains(expectedReason, metadata);
        Assert.Contains(expectedOcrPages, metadata);
        Assert.Contains(expectedNovelPages, metadata);
        Assert.Contains(expectedNativeText, metadata);
        Assert.Contains(expectedReviewPages, metadata);
        Assert.Contains(expectedWarningPages, metadata);
        Assert.Contains(expectedImagePages, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr", "OCR natif recommandé", "mode OCR OCR complet + images", "langues OCR fra+eng", "durée OCR 1.2s", "pages 5", "pages texte 1", "pages vides 2", "pages pauvres 2")]
    [InlineData("en", "native OCR recommended", "OCR mode full OCR + images", "OCR languages fra+eng", "OCR duration 1.2s", "pages 5", "text pages 1", "empty pages 2", "sparse pages 2")]
    [InlineData("es", "OCR nativo recomendado", "modo OCR OCR completo + imágenes", "idiomas OCR fra+eng", "duración OCR 1.2s", "páginas 5", "páginas con texto 1", "páginas vacías 2", "páginas escasas 2")]
    [InlineData("pt", "OCR nativo recomendado", "modo OCR OCR completo + imagens", "idiomas OCR fra+eng", "duração OCR 1.2s", "páginas 5", "páginas com texto 1", "páginas vazias 2", "páginas escassas 2")]
    [InlineData("de", "native OCR empfohlen", "OCR-Modus vollständige OCR + Bilder", "OCR-Sprachen fra+eng", "OCR-Dauer 1.2s", "Seiten 5", "Textseiten 1", "leere Seiten 2", "seiten mit wenig Text 2")]
    [InlineData("it", "OCR nativo consigliato", "modalità OCR OCR completo + immagini", "lingue OCR fra+eng", "durata OCR 1.2s", "pagine 5", "pagine testo 1", "pagine vuote 2", "pagine scarne 2")]
    public void Sources_cards_metadata_shows_localized_ocr_operational_diagnostics(
        string language,
        string expectedNativeRecommendation,
        string expectedMode,
        string expectedLanguages,
        string expectedDuration,
        string expectedPages,
        string expectedTextPages,
        string expectedEmptyPages,
        string expectedSparsePages)
    {
        var source = new SourceCard
        {
            ExtractionDiagnosticSummary = new SourceExtractionDiagnosticSummary
            {
                NativeOcrRecommended = true,
                OcrMode = "full_document_plus_image_page",
                OcrLanguages = "fra+eng",
                OcrDurationMs = 1234,
                PageCount = 5,
                TextPageCount = 1,
                EmptyPageCount = 2,
                SparsePageCount = 2
            }
        };

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        Assert.Contains(expectedNativeRecommendation, metadata);
        Assert.Contains(expectedMode, metadata);
        Assert.Contains(expectedLanguages, metadata);
        Assert.Contains(expectedDuration, metadata);
        Assert.Contains(expectedPages, metadata);
        Assert.Contains(expectedTextPages, metadata);
        Assert.Contains(expectedEmptyPages, metadata);
        Assert.Contains(expectedSparsePages, metadata);
        AssertNoRawSourceCardMetadata(metadata);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Source_card_labels_used_by_metadata_exist_for_all_ui_languages(string language)
    {
        var keys = new[]
        {
            "language",
            "document_language",
            "profile_language",
            "quality",
            "page_quality",
            "document_quality",
            "ocr_attempted",
            "ocr_applied",
            "ocr_recommended",
            "ocr_failure",
            "ocr_reason",
            "ocr_pages",
            "ocr_novel_pages",
            "native_text",
            "native_ocr_recommended",
            "ocr_mode",
            "ocr_languages",
            "ocr_duration",
            "document_pages",
            "text_pages",
            "empty_pages",
            "sparse_pages",
            "page_review_count",
            "page_warning_count",
            "image_pages",
            "review_recommended",
            "evidence_role",
            "selection_score",
            "content_role",
            "content_density",
            "retrieval_navigation",
            "navigation_reason",
            "content_role.content",
            "content_role.navigation",
            "content_role.mixed_navigation_content",
            "hash",
            "content_cards",
            "content_card_ids",
            "content_card_evidence",
            "content_card_facts",
            "content_card_scale_basis",
            "content_card_non_scalable",
            "content_card_values",
            "content_card_reasons",
            "category"
        };

        foreach (var key in keys)
        {
            var value = LocalizedStrings.SourceCardLabel(key, language);

            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.NotEqual($"source_card.{key}", value);
        }
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Source_ocr_exception_reason_is_localized(string language)
    {
        var value = LocalizedStrings.LocalizedSourceOcrReason("exception", language);

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.DoesNotContain("source_card", value, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Exception", value);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Source_ocr_disabled_diagnostics_are_localized(string language)
    {
        var source = new SourceCard
        {
            DocumentQualityStatus = "ocr_required_but_disabled",
            QualityStatus = "no_indexable_text",
            ExtractionDiagnosticSummary = new SourceExtractionDiagnosticSummary
            {
                NativeTextStatus = "no_indexable_text",
                OcrFailureReason = "ocr_required_but_disabled",
                OcrAppliedReason = "ocr_disabled",
                OcrMode = "ocr_disabled"
            }
        };

        var values = new[]
        {
            LocalizedStrings.LocalizedSourceQualityStatus("ocr_required_but_disabled", language),
            LocalizedStrings.LocalizedSourceQualityStatus("no_indexable_text", language),
            LocalizedStrings.LocalizedSourceQualityStatus("scanned_pdf_not_indexable", language),
            LocalizedStrings.LocalizedSourceQualityStatus("document_not_indexable", language),
            LocalizedStrings.LocalizedSourceOcrReason("ocr_required_but_disabled", language),
            LocalizedStrings.LocalizedSourceOcrReason("ocr_disabled", language),
            LocalizedStrings.LocalizedSourceOcrReason("ocr_output_missing", language),
            LocalizedStrings.LocalizedSourceOcrReason("scanned_pdf_not_indexable", language),
            LocalizedStrings.LocalizedSourceOcrReason("no_indexable_text", language),
            LocalizedStrings.LocalizedSourceOcrReason("document_not_indexable", language),
            LocalizedStrings.LocalizedSourceOcrMode("ocr_disabled", language)
        };

        foreach (var value in values)
        {
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.DoesNotContain("source_card", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_", value, StringComparison.Ordinal);
        }

        var metadata = SourcesCardsControl.GetMetadataLabel(source, language);

        AssertNoRawSourceCardMetadata(metadata);
        Assert.DoesNotContain("ocr_required_but_disabled", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("no_indexable_text", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("ocr_disabled", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_card_parser_reads_camel_and_pascal_quality_aliases()
    {
        const string json = """
        {
          "Sources": [
            {
              "DocPath": "Knowledge/pascal.pdf",
              "DocName": "pascal.pdf",
              "ExtractionQuality": {
                "QualityStatus": "ocr_failed_or_insufficient",
                "Confidence": "0.42",
                "ManualReview": true,
                "OCRAttempted": true,
                "OCRApplied": false,
                "OCRRecommended": true
              }
            },
            {
              "docPath": "Knowledge/camel.pdf",
              "docName": "camel.pdf",
              "extractionQuality": {
                "quality": "manual_review_low_text",
                "confidence": 0.67,
                "manualReview": "true",
                "ocrAttempted": true
              }
            }
          ]
        }
        """;

        var cards = SourceCardParser.Parse(json).OrderBy(static card => card.DocName).ToArray();

        var camel = Assert.Single(cards, static card => card.DocName == "camel.pdf");
        Assert.Equal("manual_review_low_text", camel.QualityStatus);
        Assert.Equal(0.67, camel.ExtractionConfidence);
        Assert.True(camel.ManualReviewRecommended);
        Assert.True(camel.OcrAttempted);

        var pascal = Assert.Single(cards, static card => card.DocName == "pascal.pdf");
        Assert.Equal("ocr_failed_or_insufficient", pascal.QualityStatus);
        Assert.Equal(0.42, pascal.ExtractionConfidence);
        Assert.True(pascal.ManualReviewRecommended);
        Assert.True(pascal.OcrAttempted);
        Assert.False(pascal.OcrApplied);
        Assert.True(pascal.OcrRecommended);
    }

    [Fact]
    public void Tool_agent_runtime_avoids_corpus_specific_retrieval_terms()
    {
        var repoRoot = FindRepoRoot();
        var toolAgentDir = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "ToolAgent");
        var backendRagEndpoint = Path.Combine(repoRoot, "backend", "SAAIA.Backend", "Endpoints", "RagEndpoints.cs");
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(toolAgentDir, "*.cs", SearchOption.AllDirectories)
                .Concat([backendRagEndpoint])
                .Select(File.ReadAllText));

        var forbidden = new[]
        {
            "PDF34",
            "Ce qui vient des PDF",
            "LooksLikeColdAssembly",
            "ingredient",
            "ingredients",
            "recette",
            "recipe",
            "entree",
            "plat",
            "dessert",
            "cuisine",
            "tasse",
            "tasses",
            "cup",
            "cups",
            "cuillere",
            "cuilleres",
            "ustensile",
            "ustensiles"
        };

        foreach (var term in forbidden)
        {
            if (term.Contains(' ', StringComparison.Ordinal))
            {
                Assert.DoesNotContain(term, source, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            Assert.False(
                Regex.IsMatch(source, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                $"Forbidden corpus-specific runtime token found: {term}");
        }
    }

    [Fact]
    public void Tool_agent_runtime_avoids_serving_portion_specific_scaling_terms()
    {
        var repoRoot = FindRepoRoot();
        var stateFile = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "ToolAgent", "ToolAgentOrchestrator.State.cs");
        var source = File.ReadAllText(stateFile);

        foreach (var term in new[] { "serving", "servings", "portion", "portions" })
        {
            Assert.False(
                Regex.IsMatch(source, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                $"Forbidden corpus-specific scaling token found: {term}");
        }
    }

    [Fact]
    public void Visible_source_page_labels_use_localized_prefix_helpers()
    {
        var repoRoot = FindRepoRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "ToolAgent"), "*.cs", SearchOption.AllDirectories)
            .Concat([
                Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Services", "RagChatAgent.cs")
            ]);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(@"Append\("" p\.""\)", source);
            Assert.DoesNotMatch(@"\$""[^""]*(?:\(p\.| p\.)", source);
        }
    }

    [Fact]
    public void MainWindow_apply_ui_language_updates_sources_cards_control()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "HelpAndLocalization.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("SourcesCards.ApplyUiLanguage(lang)", source);
    }

    [Fact]
    public void Legacy_user_settings_dialog_reference_uses_client_ui_text_for_visible_labels()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "UserSettingsDialog.xaml"));
        var code = File.ReadAllText(Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "UserSettingsDialog.xaml.cs"));
        var combined = xaml + "\n" + code;

        foreach (var mojibake in new[] { "ParamÃ", "rÃ", "sÃ", "lâ", "Ã‰", "diagnosticâ" })
            Assert.DoesNotContain(mojibake, combined, StringComparison.Ordinal);

        Assert.Contains("Title = T(\"settings.title\")", code);
        Assert.Contains("PrimaryButtonText = T(\"settings.apply\")", code);
        Assert.Contains("CloseButtonText = ClientUiText.Get(\"dialog.close\", _uiLanguage)", code);
        Assert.Contains("SafeSettingsNoteText.Text = T(\"settings.safe_note\")", code);
        Assert.Contains("AssistantEnabledToggle.Header = T(\"settings.toggle.assistant\")", code);
        Assert.Contains("StrictModeToggle.Header = T(\"settings.toggle.strict\")", code);
        Assert.Contains("RagQualityLabelText.Text = T(\"settings.rag_quality\")", code);
        Assert.Contains("StyleLabelText.Text = T(\"settings.style\")", code);
        Assert.Contains("AnswerLengthLabelText.Text = T(\"settings.length\")", code);
        Assert.Contains("AssistantRepairNoteText.Text = T(\"settings.repair.note\")", code);
        Assert.Contains("OpenSupportFolderButton.Content = T(\"settings.support.open_folder\")", code);
    }

    [Fact]
    public void Sources_cards_control_keeps_the_explicit_active_language()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "SourcesCardsControl.xaml.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("_uiLanguage = ClientUiText.NormalizeLanguage(uiLanguage ?? AppSettings.Load().UiLanguage)", source);
        Assert.Contains("GetOpenButtonText(_uiLanguage)", source);
        Assert.Contains("ClientUiText.Get(\"dialog.close\", _uiLanguage)", source);
    }

    [Fact]
    public void MainWindow_apply_ui_language_covers_core_visible_labels()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "HelpAndLocalization.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("ChatsHeaderText.Text = ClientUiText.Get(\"panel.chats\", lang)", source);
        Assert.Contains("NewChatButton.Content = ClientUiText.Get(\"button.new\", lang)", source);
        Assert.Contains("JumpBottomButton.Content = ClientUiText.Get(\"button.jump_bottom\", lang)", source);
        Assert.Contains("TypingText.Text = ClientUiText.Get(\"typing\", lang)", source);
        Assert.Contains("InputBox.PlaceholderText = ClientUiText.Get(\"input.placeholder\", lang)", source);
        Assert.Contains("ConnectButton.Content = ClientUiText.Get(\"button.connect\", lang)", source);
        Assert.Contains("UserSettingsButton.Content = ClientUiText.Get(\"header.settings\", lang)", source);
        Assert.Contains("SourcesToggleButton.Content = ClientUiText.Get(\"panel.sources\", lang)", source);
        Assert.Contains("SourcesPanelTitleText.Text = ClientUiText.Get(\"panel.sources\", lang)", source);
        Assert.Contains("LocalLlmRuntimeDiagnosticsButton.Content = ClientUiText.Get(\"button.runtime_diagnostics\", lang)", source);
    }

    [Fact]
    public void Session_menu_labels_are_localized_on_open()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "Sessions.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("rename.Text = ClientUiText.Get(\"session.menu.rename\", _appSettings.UiLanguage)", source);
        Assert.Contains("delete.Text = ClientUiText.Get(\"session.menu.delete\", _appSettings.UiLanguage)", source);
    }

    [Fact]
    public void Setup_wizard_applies_localized_placeholders_from_code()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "SetupWizardDialog.xaml.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("ApiKeyBox.PlaceholderText = \"saaia_…\"", source);
        Assert.Contains("LlamaExeBox.PlaceholderText = SZ(", source);
        Assert.Contains("ModelPathBox.PlaceholderText = SZ(", source);
        Assert.Contains("HostBox.PlaceholderText = SZ(", source);
        Assert.Contains("PortBox.PlaceholderText = SZ(", source);
        Assert.Contains("ModelIdBox.PlaceholderText = SZ(", source);
    }

    [Fact]
    public void Setup_wizard_applies_localized_title_buttons_and_advanced_labels_from_code()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "SetupWizardDialog.xaml.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("HeroTitleText.Text = SZ(", source);
        Assert.Contains("WizardApplyButton.Content = SZ(", source);
        Assert.Contains("WizardCancelButton.Content = SZ(", source);
        Assert.Contains("TestReadyButton.Content = SZ(", source);
        Assert.Contains("TestApiKeyButton.Content = SZ(", source);
        Assert.Contains("UseLocalLlmCheck.Content = SZ(", source);
        Assert.Contains("AutoStartCheck.Content = SZ(", source);
        Assert.Contains("TestModelsButton.Content = SZ(", source);
        Assert.Contains("StartLocalLlmButton.Content = SZ(", source);
        Assert.Contains("StopLocalLlmButton.Content = SZ(", source);
    }

    [Fact]
    public void Admin_runtime_overlay_uses_localized_runtime_summary_labels()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "AdminRuntimeOpsPanel.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("LocalRuntimeText(\"Evenements runtime recents :\"", source);
        Assert.Contains("LocalRuntimeText(\"Evenements runtime recents : aucun\"", source);
        Assert.Contains("ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)", source);
        Assert.Contains("ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)", source);
        Assert.Contains("ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)", source);
        Assert.Contains("ReasonCounts: ReadIntDictionary(itemSummaryElement, \"reasonCounts\")", source);
        Assert.Contains("LatestCampaignStatus: TryGetString(itemSummaryElement, \"latestCampaignStatus\")", source);
        Assert.Contains("LatestCampaignOccurredAt: TryGetDateTimeOffset(itemSummaryElement, \"latestCampaignOccurredAt\")", source);
        Assert.Contains("BuildCapabilityBCampaignOperationalText(item.Summary, lang)", source);
        Assert.Contains("BuildLatestCampaignText(item.Summary, lang)", source);
        Assert.Contains("BuildCapabilityBReasonCountsText(item.Summary.ReasonCounts, lang)", source);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "TODO.md")))
                return current;

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root not found from test output directory.");
    }

    private static void AssertNoRawSourceCardMetadata(string metadata)
    {
        foreach (var raw in new[]
        {
            "manual_review_low_text",
            "page_ok_with_images",
            "page_ok_indexed_by_context",
            "page_ok_empty_text",
            "page_ok_low_value_text",
            "manual_review_text_not_indexed",
            "ocr_applied_ok",
            "ocr_applied_ok_with_page_warnings",
            "low_text",
            "empty_text",
            "manualReviewLowText",
            "ocr_attempted",
            "image_page",
            "full_document_plus_image_page",
            "ocr_required_but_disabled",
            "no_indexable_text",
            "ocr_disabled",
            "ocr_output_missing",
            "OCR?"
        })
        {
            Assert.DoesNotContain(raw, metadata, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch("[a-z]+_[a-z_]+", metadata);
    }

    private static string BuildWrappedSourcePayload(string wrapper)
    {
        const string source = """
        {
          "docPath": "Knowledge/manual.pdf",
          "docName": "manual.pdf",
          "sourceHash": "hash-abcdef",
          "docLanguage": "en",
          "extractionQuality": {
            "documentQualityStatus": "ocr_applied_ok"
          },
          "matchedContentCards": [
            { "title": "Control source" }
          ]
        }
        """;

        return wrapper switch
        {
            "rootArray" => $"[{source}]",
            "searches" => $$"""{ "searches": [ { "items": [ {{source}} ] } ] }""",
            _ => $$"""{ "{{wrapper}}": [ {{source}} ] }"""
        };
    }
}
