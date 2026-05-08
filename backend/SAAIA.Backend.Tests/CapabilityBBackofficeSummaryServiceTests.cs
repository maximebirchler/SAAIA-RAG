using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CapabilityBBackofficeSummaryServiceTests
{
    [Fact]
    public async Task BuildSummaryAsync_uses_llm_summary_when_runtime_returns_text()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Operational summary for IND570: Scope and Purpose frames the control perimeter. Operators should review the PLC exchange guardrails before deployment."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose", "PLC Integration"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("Operational summary for IND570", payload.SummaryText, StringComparison.Ordinal);
        Assert.False(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_document_foundation", payload.Meta.GetProperty("strategy").GetString());
        Assert.True(payload.Meta.GetProperty("llmDurationMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmResponseHeadersMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmFirstResponseMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmBytesRead").GetInt64() > 0);
        Assert.InRange(payload.Meta.GetProperty("qualityScore").GetDouble(), 0.0, 1.0);
        var qualitySignals = payload.Meta.GetProperty("qualitySignals");
        Assert.True(qualitySignals.GetProperty("lineCount").GetInt32() >= 1);
        Assert.True(qualitySignals.GetProperty("sectionCoverageScore").GetDouble() >= 0.0);
        Assert.True(qualitySignals.GetProperty("keywordCoverageScore").GetDouble() >= 0.0);
    }

    [Fact]
    public async Task BuildSummaryAsync_prompt_keeps_excerpt_samples_independent_and_hides_storage_path()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Ce document couvre des procedures et des conseils pratiques, avec des exemples separes issus des extraits fournis."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Knowledge/procedures.pdf",
                DocName: "procedures.pdf",
                Category: "knowledge",
                PageCount: 24,
                IndexedVersion: 3),
            ["Procedures", "Organisation"],
            [
                "Verifier une procedure avant validation.",
                "Controler les composants avant utilisation.",
                "Archiver les resultats apres execution."
            ],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.NotNull(capturedRequest);
        Assert.DoesNotContain("Document path:", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Indexed version:", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Independent source excerpts", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("Keep separate examples separate", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("do not merge two separate bullets into one invented procedure", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cite at most 3 clear examples", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skip names or phrases that look garbled", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not group examples into inferred categories", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Important: redige uniquement en francais.", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("do not use parenthetical examples", capturedRequest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildSummaryAsync_carries_extraction_quality_caveat_and_metadata_for_ocr_failed_low_text()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Ce document decrit les controles de securite et les procedures disponibles dans les extraits."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "OCR/manual-review.pdf",
                DocName: "manual-review.pdf",
                Category: "ocr",
                PageCount: 6,
                IndexedVersion: 2,
                ExtractionQuality: BuildProblemExtractionQuality()),
            ["Controle qualite"],
            ["Les controles extraits mentionnent une verification avant validation."],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.NotNull(capturedRequest);
        Assert.Contains("Source quality status: ocr_failed_or_insufficient", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("Text status: low_text", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("Manual review recommended: true", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("OCR failure reason: ocr_failed", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("do not treat them as a complete normal source", capturedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reserve source", payload.SummaryText, StringComparison.Ordinal);

        var extractionQuality = payload.Meta.GetProperty("extractionQuality");
        Assert.Equal("ocr_failed_or_insufficient", extractionQuality.GetProperty("status").GetString());
        Assert.Equal("low_text", extractionQuality.GetProperty("textStatus").GetString());
        Assert.True(extractionQuality.GetProperty("requiresCaution").GetBoolean());
        Assert.True(extractionQuality.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.Equal("ocr_failed", extractionQuality.GetProperty("ocrFailureReason").GetString());
        Assert.Contains(
            extractionQuality.GetProperty("signals").EnumerateArray(),
            item => string.Equals(item.GetString(), "ocr_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSummaryAsync_removes_duplicate_sentences_from_llm_summary()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Ce document propose des recettes simples. Ce document propose des recettes simples. Il decrit des portions copieuses et copieuses. Il donne aussi des conseils de preparation."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Cuisine/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "cuisine",
                PageCount: 12,
                IndexedVersion: 1),
            ["Recettes"],
            ["Le document propose des recettes simples et des conseils de preparation."],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.Equal(
            1,
            CountOccurrences(payload.SummaryText, "Ce document propose des recettes simples.", StringComparison.Ordinal));
        Assert.DoesNotContain("copieuses et copieuses", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("portions copieuses.", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Il donne aussi des conseils de preparation.", payload.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSummaryAsync_falls_back_when_llm_uses_wrong_language()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "This document is a French cookbook containing recipes and practical examples for home cooking."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Cuisine/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "cuisine",
                PageCount: 12,
                IndexedVersion: 1),
            ["Recettes"],
            ["Le document propose des recettes simples et des conseils de preparation."],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.True(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_language_mismatch", payload.Meta.GetProperty("fallbackReason").GetString());
        Assert.Contains("Guide.pdf est un document cuisine", payload.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "Guide.pdf ist ein Dokument")]
    [InlineData("nl-BE", "Onderhoud")]
    public async Task BuildSummaryAsync_falls_back_when_llm_answers_english_for_non_english_document_language(
        string preferredLanguage,
        string expectedFallbackFragment)
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "This document contains maintenance requirements and includes examples that should be reviewed before operation."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Knowledge/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "knowledge",
                PageCount: 12,
                IndexedVersion: 1,
                ProfileLanguage: preferredLanguage),
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None,
            preferredLanguage: preferredLanguage);

        Assert.True(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_language_mismatch", payload.Meta.GetProperty("fallbackReason").GetString());
        Assert.Contains(expectedFallbackFragment, payload.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSummaryAsync_does_not_force_language_mismatch_fallback_for_unknown_guard_language()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "This document contains maintenance checks and describes available safety requirements."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Kunskap/Manual.pdf",
                DocName: "Manual.pdf",
                Category: "kunskap",
                PageCount: 12,
                IndexedVersion: 1,
                ProfileLanguage: "sv"),
            ["Underhall"],
            ["Manualen beskriver underhall och sakerhetskontroller."],
            CancellationToken.None,
            preferredLanguage: "sv");

        Assert.False(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("sv", payload.DocLanguage);
        Assert.Contains("maintenance checks", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildSummaryAsync_falls_back_to_deterministic_summary_when_runtime_is_unavailable()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("IND570.pdf is a document in atex category", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("indexed version", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.True(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_runtime_unavailable", payload.Meta.GetProperty("fallbackReason").GetString());
        Assert.Equal("http_503", payload.Meta.GetProperty("llmError").GetString());
        Assert.Equal("llm_runtime_unavailable", payload.Meta.GetProperty("llmFailureKind").GetString());
        Assert.Equal("http", payload.Meta.GetProperty("llmFailureCategory").GetString());
        Assert.Equal(503, payload.Meta.GetProperty("llmStatusCode").GetInt32());
        Assert.InRange(payload.Meta.GetProperty("qualityScore").GetDouble(), 0.0, 1.0);
        Assert.True(payload.Meta.GetProperty("qualitySignals").GetProperty("matchedSectionCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task BuildSummaryAsync_fallback_respects_preferred_document_language()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Cuisine/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "cuisine",
                PageCount: 12,
                IndexedVersion: 1),
            ["Batch cooking"],
            ["Le batch cooking consiste a preparer plusieurs repas de la semaine a l'avance."],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.Contains("Guide.pdf est un document cuisine", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Sections cles", payload.SummaryText, StringComparison.Ordinal);
        Assert.Equal("fr", payload.Meta.GetProperty("outputLanguage").GetString());
    }

    [Theory]
    [InlineData("fr-ch", "est un document", "Sections cles")]
    [InlineData("pt-br", "e um documento", "Secoes principais")]
    [InlineData("es", "es un documento", "Secciones clave")]
    [InlineData("pt", "e um documento", "Secoes principais")]
    [InlineData("de", "ist ein Dokument", "Wichtige Abschnitte")]
    [InlineData("it", "e un documento", "Sezioni chiave")]
    public void BuildDeterministicSummary_localizes_supported_fallback_languages(
        string language,
        string overviewFragment,
        string sectionsLabel)
    {
        var payload = CapabilityBBackofficeSummaryService.BuildDeterministicSummary(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Knowledge/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "knowledge",
                PageCount: 3,
                IndexedVersion: 1),
            ["Operations"],
            ["Operators use the indexed metadata when no runtime is available."],
            fallbackReason: "test",
            preferredLanguage: language);

        Assert.Contains(overviewFragment, payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains(sectionsLabel, payload.SummaryText, StringComparison.Ordinal);
        Assert.Equal(language, payload.Meta.GetProperty("outputLanguage").GetString());
    }

    [Fact]
    public void BuildDeterministicSummary_detects_document_language_when_profile_language_is_unknown()
    {
        var payload = CapabilityBBackofficeSummaryService.BuildDeterministicSummary(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Cuisine/Guide.pdf",
                DocName: "Guide.pdf",
                Category: "cuisine",
                PageCount: 3,
                IndexedVersion: 1),
            ["Recettes de la semaine"],
            ["Vous pouvez preparer les legumes avec une sauce simple pour les repas de la semaine."],
            fallbackReason: "test",
            preferredLanguage: "und");

        Assert.Contains("est un document", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Sections cles", payload.SummaryText, StringComparison.Ordinal);
        Assert.Equal("fr", payload.Meta.GetProperty("outputLanguage").GetString());
    }

    [Fact]
    public void BuildDeterministicSummary_accepts_arbitrary_profile_language_tags()
    {
        var payload = CapabilityBBackofficeSummaryService.BuildDeterministicSummary(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Kennisbank/Handleiding.pdf",
                DocName: "Handleiding.pdf",
                Category: "kennisbank",
                PageCount: 3,
                IndexedVersion: 1,
                ProfileLanguage: "nl"),
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            fallbackReason: "test",
            preferredLanguage: "und");

        Assert.Equal("nl", payload.DocLanguage);
        Assert.Equal("nl", payload.Meta.GetProperty("outputLanguage").GetString());
        Assert.DoesNotContain(" is a document", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Key sections", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Onderhoud", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("De handleiding beschrijft onderhoud", payload.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Onderhoud", "De handleiding beschrijft onderhoud en veiligheidscontroles.", "nl")]
    [InlineData("السلامة", "يشرح هذا المستند اجراءات السلامة والصيانة للمعدات.", "ar")]
    [InlineData("维护", "本文件介绍安全联锁状态和维护要求。", "zh")]
    public void BuildDeterministicSummary_detects_non_ui_document_language_without_client_language(
        string section,
        string excerpt,
        string expectedLanguage)
    {
        var payload = CapabilityBBackofficeSummaryService.BuildDeterministicSummary(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Knowledge/Universal.pdf",
                DocName: "Universal.pdf",
                Category: "knowledge",
                PageCount: 3,
                IndexedVersion: 1),
            [section],
            [excerpt],
            fallbackReason: "test",
            preferredLanguage: "und");

        Assert.Equal(expectedLanguage, payload.DocLanguage);
        Assert.Equal(expectedLanguage, payload.Meta.GetProperty("outputLanguage").GetString());
        Assert.Contains(excerpt, payload.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(" is a document", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Key sections", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("nl", "Bronkwaliteit")]
    [InlineData("ar", "source_quality_requires_review")]
    [InlineData("zh", "source_quality_requires_review")]
    public void BuildDeterministicSummary_preserves_quality_caveat_for_non_ui_languages(
        string language,
        string expectedCaveatFragment)
    {
        var payload = CapabilityBBackofficeSummaryService.BuildDeterministicSummary(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Knowledge/Universal.pdf",
                DocName: "Universal.pdf",
                Category: "knowledge",
                PageCount: 3,
                IndexedVersion: 1,
                ExtractionQuality: BuildProblemExtractionQuality(),
                ProfileLanguage: language),
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            fallbackReason: "test",
            preferredLanguage: "und");

        Assert.Equal(language, payload.DocLanguage);
        Assert.Contains(expectedCaveatFragment, payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains(expectedCaveatFragment, payload.Meta.GetProperty("extractionQuality").GetProperty("caveat").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSummaryAsync_uses_arbitrary_profile_language_in_prompt()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Deze handleiding beschrijft onderhoud en veiligheidscontroles."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Kennisbank/Handleiding.pdf",
                DocName: "Handleiding.pdf",
                Category: "kennisbank",
                PageCount: 3,
                IndexedVersion: 1,
                ProfileLanguage: "nl"),
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None,
            preferredLanguage: "und");

        Assert.Equal("nl", payload.DocLanguage);
        Assert.NotNull(capturedRequest);
        Assert.Contains("Requested output language: nl", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("BCP-47 language tag: nl", capturedRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSummaryAsync_uses_llm_summary_when_runtime_returns_text_content_blocks()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": [
                      { "type": "text", "text": "Operational summary for IND570: Scope and Purpose frames the control perimeter." },
                      { "type": "text", "text": "Operators should review PLC exchange guardrails before deployment." }
                    ]
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose", "PLC Integration"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("Operational summary for IND570", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Operators should review PLC exchange guardrails", payload.SummaryText, StringComparison.Ordinal);
        Assert.False(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_document_foundation", payload.Meta.GetProperty("strategy").GetString());
    }

    [Fact]
    public async Task BuildSummaryAsync_falls_back_to_deterministic_summary_when_runtime_returns_invalid_json()
    {
        var service = CreateService("not-json-at-all", HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("IND570.pdf is a document in atex category", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("indexed version", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.True(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_exception", payload.Meta.GetProperty("fallbackReason").GetString());
        Assert.Equal("exception", payload.Meta.GetProperty("llmError").GetString());
        Assert.Equal("llm_exception", payload.Meta.GetProperty("llmFailureKind").GetString());
        Assert.Equal("exception", payload.Meta.GetProperty("llmFailureCategory").GetString());
    }

    private static CapabilityBBackofficeSummaryService CreateService(
        string body,
        HttpStatusCode statusCode,
        Action<string>? captureRequestBody = null)
        => new(
            new LocalLlmChatClient(
                new StubHttpClientFactory(body, statusCode, captureRequestBody),
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "local"
                }),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            });

    private static CapabilityBExtractionQualitySnapshot BuildProblemExtractionQuality()
        => new(
            Status: "ocr_failed_or_insufficient",
            TextStatus: "low_text",
            ExtractionConfidence: 0.25,
            ManualReviewRecommended: true,
            OcrAttempted: true,
            OcrApplied: false,
            OcrRecommended: true,
            OcrFailureReason: "ocr_failed",
            PageCount: 6,
            TextPageCount: 1,
            EmptyPageCount: 2,
            SparsePageCount: 3,
            TotalWordCount: 18,
            TotalCharCount: 120,
            TextPageRatio: 0.1667,
            Signals: ["low_average_words_per_page", "ocr_recommended", "ocr_failed"],
            Source: "pdf_text");

    private static int CountOccurrences(string value, string needle, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, comparison)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

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
