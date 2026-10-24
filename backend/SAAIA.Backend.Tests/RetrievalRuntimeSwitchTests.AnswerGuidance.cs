using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{

    [Theory]
    [InlineData("VX-12 calibration limits")]
    [InlineData("controle interverrouillage VX-12")]
    [InlineData("Pump maintenance threshold")]
    public void BuildAnswerGuidance_marks_empty_retrieval_as_no_source_match_for_generic_queries(string query)
    {
        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(query, Array.Empty<RagMatch>());

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("no_relevant_source_found", guidance.Reason);
        Assert.Equal("no_source_match", guidance.ResponseShape);
        Assert.False(string.IsNullOrWhiteSpace(guidance.QualificationNote));
        Assert.DoesNotContain("recette", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ingredient", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuisine", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnswerGuidance_asks_for_placeholder_standard_even_when_retrieval_is_empty()
    {
        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(
            "Le projet respecte-t-il la norme xxx ?",
            Array.Empty<RagMatch>());

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.Equal("missing_standard_identifier", guidance.Reason);
        Assert.Equal("clarify", guidance.ResponseShape);
        Assert.False(string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion));
        Assert.Empty(guidance.MatchedDocHints ?? []);
    }

    [Theory]
    [InlineData("Un controle standard.", "Quel")]
    [InlineData("A standard control.", "Which")]
    [InlineData("Un control estandar.", "Que")]
    [InlineData("Um controle padrao.", "Qual")]
    [InlineData("Eine Standardkontrolle.", "Welches")]
    [InlineData("Un controllo standard.", "Quale")]
    public void BuildAnswerGuidance_asks_localized_clarification_for_ambiguous_generic_fragment(
        string query,
        string expectedMarker)
    {
        var matches = new[]
        {
            TestMatch(
                text: "Standard control procedure: verify sensor, record result, approve deviation.",
                docPath: "Knowledge/control-procedure.pdf")
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(query, matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.Equal("ambiguous_bare_fragment_requires_scope", guidance.Reason);
        Assert.Equal("clarify", guidance.ResponseShape);
        Assert.Contains(expectedMarker, guidance.ClarifyingQuestion!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnswerGuidance_asks_clarification_for_audience_constrained_generic_fragment()
    {
        var matches = new[]
        {
            TestMatch(
                text: "Document profile with several options designed for children and beginner groups.",
                docPath: "Knowledge/group-activities.pdf",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile")
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance("Une procedure enfant.", matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.Equal("ambiguous_bare_fragment_requires_scope", guidance.Reason);
        Assert.Equal("clarify", guidance.ResponseShape);
    }

    [Fact]
    public void BuildAnswerGuidance_adds_caveat_when_selected_sources_have_low_extraction_quality()
    {
        var match = TestMatch(
            text: "Procedure: verify the scanned label before using the recorded pressure value.",
            docPath: "Scans/Archive.pdf",
            page: 3);
        var quality = new RagItemExtractionQualityDto(
            PageQualityStatus: "manual_review_low_text",
            PageExtractionConfidence: 0.32,
            PageManualReviewRecommended: true,
            TextStatus: "low_confidence");
        var qualityByMatch = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase)
        {
            [RetrievalResultProjector.BuildExtractionQualityMatchKey(match)] = quality
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(
            "Que dit ce document sur la procedure ?",
            [match],
            qualityByMatch);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("source_quality_requires_manual_review_caveat", guidance.Reason);
        Assert.NotNull(guidance.QualificationNote);
        Assert.Contains("OCR", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractMatchedDocHints_derives_generic_reference_and_alpha_hints()
    {
        var matches = new[]
        {
            new RagMatch(0.9, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 1, "chunk-1", 0, "functional safety handbook", 1, "hash-1", "functional safety handbook", "exact_match_v1", 1, 1, "Safety", "Safety", "document_metadata_ref", null, null, null),
            new RagMatch(0.9, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 1, 1, "chunk-2", 0, "fieldbus integration guide", 1, "hash-2", "fieldbus integration guide", "exact_match_v1", 1, 1, "PLC", "PLC", "document_metadata_ref", null, null, null),
            new RagMatch(0.7, "doc-3", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 1, 1, "chunk-3", 0, "agreement document", 1, "hash-3", "agreement document", "contextual_text_v1", 1, 1, "General", "General", "unit_exact_v1", null, null, null),
            new RagMatch(0.7, "doc-4", "Safety/Prevention.pdf", "Prevention.pdf", 1, 1, "chunk-4", 0, "prevention document", 1, "hash-4", "prevention document", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null)
        };

        var hints = RetrievalAnswerGuidance.ExtractMatchedDocHints(matches);

        Assert.Contains("61511", hints);
        Assert.Contains("XR200", hints);
        Assert.Contains("ACCORD", hints);
        Assert.Contains("PREVENTION", hints);
        Assert.DoesNotContain("GUIDE", hints);
    }

    [Fact]
    public void BuildAnswerGuidance_uses_generic_domain_context_for_qualification()
    {
        var matches = new[]
        {
            new RagMatch(0.92, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 2, "chunk-1", 0, "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", 1, "hash-1", "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", "contextual_text_v1", 1, 1, "Process safety", "Process safety", "unit_exact_v1", null, null, null),
            new RagMatch(0.89, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 3, 4, "chunk-2", 1, "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", 1, "hash-2", "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", "contextual_text_v1", 2, 2, "PLC integration", "PLC integration", "unit_exact_v1", null, null, null)
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(
            "J'ai une discussion avec un client qui me demande si notre projet est conforme a cette norme, tu peux m'aider a repondre ?",
            matches);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.NotNull(guidance.QualificationNote);
        Assert.Contains("61511", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XR200", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("perimetre", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contexte projet", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnswerGuidance_keeps_high_impact_caveats_generic_without_corpus_terms()
    {
        var matches = new[]
        {
            TestMatch(
                text: "Privacy policy retention notes mention customer data handling and audit history.",
                docPath: "Policies/Privacy policy.pdf")
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(
            "Un client demande si on peut garantir legalement que cette option est conforme, tu repondrais quoi ?",
            matches);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("high_impact_or_safety_answer_requires_qualification", guidance.Reason);
    }

    [Fact]
    public void BuildAnswerGuidance_does_not_treat_domain_incident_words_as_high_impact_by_themselves()
    {
        var matches = new[]
        {
            TestMatch(
                text: "The historical incident report mentions an explosion during an old production event.",
                docPath: "Archive/Incident history.pdf")
        };

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(
            "Le document mentionne une explosion dans l'historique ?",
            matches);

        Assert.Equal("answer", guidance.Behavior);
        Assert.Equal("documented_question_with_relevant_sources", guidance.Reason);
    }
}
