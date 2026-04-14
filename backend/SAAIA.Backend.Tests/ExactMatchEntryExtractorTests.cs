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
                [1])
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(0, entry.UnitOrdinal));
        Assert.All(entries, entry => Assert.Equal(2, entry.SectionOrdinal));
        Assert.All(entries, entry => Assert.Equal(3, entry.PageStart));
        Assert.All(entries, entry => Assert.Equal("verbatim_excerpt", entry.Kind));
    }

    [Fact]
    public void Extract_falls_back_to_whole_unit_when_short_or_unsplittable()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(0, null, 1, 1, "Court paragraphe sans vraie segmentation", 38, 5, [1])
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        var entry = Assert.Single(entries);
        Assert.Equal("court paragraphe sans vraie segmentation", entry.NormalizedText);
        Assert.Equal("verbatim_excerpt", entry.Kind);
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
                [1])
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        Assert.Contains(entries, entry => entry.Text == "EN 15281" && entry.Kind == "standard_ref");
        Assert.Contains(entries, entry => entry.Text == "IND570" && entry.Kind == "code_ref");
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
        Assert.Contains("ind570", terms);
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
