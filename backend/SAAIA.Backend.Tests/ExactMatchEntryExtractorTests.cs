using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ExactMatchEntryExtractorTests
{
    [Fact]
    public void Extract_returns_sentence_level_entries_for_long_units()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                2,
                3,
                3,
                "Premiere phrase avec assez de contenu pour etre conservee dans l index d exact match et rester utile lors des recherches precises. Deuxieme phrase elle aussi suffisamment longue pour l exact match et pour verifier une vraie segmentation deterministe.",
                250,
                34,
                [1],
                0,
                250)
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(0, entry.UnitOrdinal));
        Assert.All(entries, entry => Assert.Equal(2, entry.SectionOrdinal));
        Assert.All(entries, entry => Assert.Equal(3, entry.PageStart));
        Assert.All(entries, entry => Assert.Equal("verbatim_excerpt", entry.Kind));
        Assert.All(entries, entry => Assert.NotNull(entry.OffsetStart));
        Assert.All(entries, entry => Assert.True(entry.OffsetEnd > entry.OffsetStart));
    }

    [Fact]
    public void Extract_falls_back_to_whole_unit_when_short_or_unsplittable()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(0, null, 1, 1, "Court paragraphe sans vraie segmentation", 38, 5, [1], 0, 38)
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        var entry = Assert.Single(entries);
        Assert.Equal("court paragraphe sans vraie segmentation", entry.NormalizedText);
        Assert.Equal("verbatim_excerpt", entry.Kind);
        Assert.Equal(0, entry.OffsetStart);
        Assert.Equal(entry.Text.Length, entry.OffsetEnd);
    }

    [Fact]
    public void Extract_restricts_sparse_low_quality_units_to_targeted_references()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                1,
                12,
                12,
                "EN 15281 short sparse page with a weak sentence that should not become a verbatim excerpt.",
                84,
                13,
                [1],
                0,
                84,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.Contains(entries, entry => entry.Text == "EN 15281" && entry.Kind == "standard_ref");
        Assert.DoesNotContain(entries, entry => entry.Text.Contains("weak sentence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_adds_targeted_references_for_standard_and_code_tokens()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                1,
                2,
                2,
                "La norme EN 15281 doit etre appliquee avec le code IND570 pendant le controle.",
                76,
                12,
                [1],
                0,
                76)
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.Contains(entries, entry => entry.Text == "EN 15281" && entry.Kind == "standard_ref");
        Assert.Contains(entries, entry => entry.Text == "IND570" && entry.Kind == "code_ref");
        Assert.Contains(entries, entry => entry.Text == "EN 15281" && entry.OffsetStart is not null);
        Assert.DoesNotContain(entries, entry => entry.Kind == "standard_ref" && entry.Text.StartsWith("La norme", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_skips_mixed_navigation_catalog_units()
    {
        var mixedNavigation = """
Controls overview 3
Maintenance plan 18
Alarm reset 22
Lockout checklist 27
Appendix 31
Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.
""";
        var content = "CONTROL HANDOVER PLAN requires operators to check status, record notes, and validate the final handover.";
        Assert.Equal(
            RetrievalContentClassifier.MixedNavigationContentRole,
            RetrievalContentClassifier.AnalyzeChunk(mixedNavigation).ContentRole);

        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, mixedNavigation, mixedNavigation.Length, 26, [1], 0, mixedNavigation.Length),
            new ExtractedDocumentUnit(1, 1, 2, 2, content, content.Length, 13, [2], 0, content.Length)
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.DoesNotContain(entries, entry => entry.UnitOrdinal == 0);
        Assert.Contains(entries, entry => entry.UnitOrdinal == 1);
    }

    [Fact]
    public void Extract_does_not_promote_lowercase_language_preposition_with_short_number_to_standard_ref()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                1,
                12,
                12,
                "Le controle fonctionne en 12 modes et la notice le repete en 68 exemples pratiques.",
                84,
                14,
                [1],
                0,
                84)
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.DoesNotContain(entries, entry => entry.Kind == "standard_ref");
    }

    [Fact]
    public void NormalizeForLookup_compacts_case_and_punctuation_for_exact_search()
    {
        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(" EN-15281 : 2006 ");

        Assert.Equal("en 15281 2006", normalized);
    }

    [Fact]
    public void ExtractLookupTerms_pulls_targeted_references_from_natural_query()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Que dit la norme EN 15281 sur le code IND570 ?");

        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
        Assert.Contains("ind570", terms);
    }

    [Fact]
    public void ExpandReferenceVariants_bridges_composite_standard_references_to_numeric_core()
    {
        var variants = ExactMatchEntryExtractor.ExpandReferenceVariants("CEN TR 15281").ToArray();

        Assert.Contains("TR 15281", variants);
        Assert.Contains("15281", variants);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_en_reference_to_numeric_variant_for_doc_metadata_matching()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on EN 15281 ?");

        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_keeps_standalone_numeric_reference_keys_for_real_queries()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on 15281 ?");

        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_compact_standard_reference_without_space()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on EN15281 ?");

        Assert.Contains("en15281", terms);
        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_hyphenated_standard_reference()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on EN-15281 ?");

        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_cen_tr_reference_with_slash_separator()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on CEN/TR 15281 ?");

        Assert.Contains("cen tr 15281", terms);
        Assert.Contains("tr 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_nist_sp_reference_to_document_name_tokens()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Compare NIST 800-53A avec SP 800-53r5.");

        Assert.Contains("nist 800 53a", terms);
        Assert.Contains("800 53a", terms);
        Assert.Contains("sp 800 53r5", terms);
    }

    [Fact]
    public void ExtractLookupTerms_bridges_split_code_reference_variants_to_compact_form()
    {
        var spaced = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on IND 570 ?");
        var hyphenated = ExactMatchEntryExtractor.ExtractLookupTerms("Ou trouve-t-on IND-570 ?");

        Assert.Contains("ind 570", spaced);
        Assert.Contains("ind570", spaced);
        Assert.Contains("ind 570", hyphenated);
        Assert.Contains("ind570", hyphenated);
    }

    [Fact]
    public void ExtractLookupTerms_keeps_product_code_when_vendor_name_is_present()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Montre le manuel Mettler Toledo IND-570.");

        Assert.Contains("ind 570", terms);
        Assert.Contains("ind570", terms);
    }

    [Fact]
    public void ExtractLookupTerms_handles_noisy_user_query_for_cen_document()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("stp je cherche le pdf inerting EN-15281");

        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_does_not_keep_pdf_prefixed_numeric_noise()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Ouvre-moi le pdf 15281 inerting stp.");

        Assert.Contains("15281", terms);
        Assert.DoesNotContain("pdf 15281", terms);
        Assert.DoesNotContain("pdf15281", terms);
    }

    [Fact]
    public void ExtractLookupTerms_handles_noisy_user_query_for_ind570_manual()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("need the Mettler Toledo IND570 manual pdf please");

        Assert.Contains("ind570", terms);
    }

    [Fact]
    public void ExtractLookupTerms_does_not_keep_terminal_prefixed_numeric_noise()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Est-ce que tu as le manuel du terminal IND570 ?");

        Assert.Contains("ind570", terms);
        Assert.DoesNotContain("terminal ind570", terms);
    }

    [Fact]
    public void ExtractReferenceKeys_pulls_numeric_reference_keys_for_metadata_matching()
    {
        var keys = ExactMatchEntryExtractor.ExtractReferenceKeys("CEN TR 15281 2006 Guidance on inerting and IEC 61508");

        Assert.Contains("15281", keys);
        Assert.Contains("61508", keys);
    }

    [Fact]
    public void Stable_exact_match_entry_id_is_deterministic_for_same_revision_and_index()
    {
        var revisionId = Guid.Parse("abababab-abab-abab-abab-abababababab");

        var left = DocumentFoundationRepo.BuildStableExactMatchEntryId(revisionId, 9);
        var right = DocumentFoundationRepo.BuildStableExactMatchEntryId(revisionId, 9);

        Assert.Equal(left, right);
    }
}
