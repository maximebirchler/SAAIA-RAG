using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

/// <summary>
/// Regression tests for composite reference matching.
/// Bug: "EN 15281" returned no results while "15281" worked.
/// Root cause: query term "en 15281" didn't match document entry "cen tr 15281".
/// Fix: expand both query terms and document entries with sub-variants (prefix stripping + bare numeric).
/// </summary>
public sealed class ExactMatchBugDiagnosticTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("EN 15281", "en 15281")]
    [InlineData("EN 15281", "15281")]
    [InlineData("ISO 13849", "iso 13849")]
    [InlineData("ISO 13849", "13849")]
    [InlineData("CEN TR 15281", "cen tr 15281")]
    [InlineData("CEN TR 15281", "tr 15281")]
    [InlineData("CEN TR 15281", "15281")]
    [InlineData("IEC 61508", "iec 61508")]
    [InlineData("IEC 61508", "61508")]
    public void ExtractLookupTerms_produces_expected_variants(string query, string expectedTerm)
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms(query);

        output.WriteLine($"ExtractLookupTerms('{query}') = [{string.Join(", ", terms.Select(t => $"'{t}'"))}]");

        Assert.Contains(expectedTerm, terms, StringComparer.Ordinal);
    }

    [Fact]
    public void ExtractLookupTerms_produces_numeric_core_for_natural_query()
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms("Que dit la norme EN 15281 ?");

        output.WriteLine($"Terms: [{string.Join(", ", terms.Select(t => $"'{t}'"))}]");

        Assert.Contains("en 15281", terms);
        Assert.Contains("15281", terms);
    }

    [Fact]
    public void Extract_produces_variant_entries_for_composite_references()
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0, 0, 1, 1,
                "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.",
                70, 11, [1])
        };

        var entries = ExactMatchEntryExtractor.Extract(units);

        output.WriteLine("Entries:");
        foreach (var entry in entries)
            output.WriteLine($"  normalizedText='{entry.NormalizedText}' kind={entry.Kind}");

        // Should have the full reference AND sub-variants
        Assert.Contains(entries, e => e.NormalizedText == "cen tr 15281");
        Assert.Contains(entries, e => e.NormalizedText == "tr 15281");
        Assert.Contains(entries, e => e.NormalizedText == "15281");
    }

    [Fact]
    public void EN_15281_query_matches_CEN_TR_15281_document_entry()
    {
        // Ingestion side: extract entries from document
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0, 0, 1, 1,
                "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.",
                70, 11, [1])
        };
        var entries = ExactMatchEntryExtractor.Extract(units);
        var entryTexts = entries.Select(e => e.NormalizedText).ToHashSet(StringComparer.Ordinal);

        // Query side: extract lookup terms from user query
        var queryTerms = ExactMatchEntryExtractor.ExtractLookupTerms("EN 15281");

        output.WriteLine("=== Document entries ===");
        foreach (var e in entryTexts) output.WriteLine($"  '{e}'");
        output.WriteLine("=== Query terms ===");
        foreach (var t in queryTerms) output.WriteLine($"  '{t}'");

        // THE FIX: at least one query term must match at least one document entry
        var matched = queryTerms.Any(term => entryTexts.Contains(term));
        Assert.True(matched, "EN 15281 query should match CEN TR 15281 document via shared sub-variant '15281'");
    }

    [Theory]
    [InlineData("EN 15281")]
    [InlineData("15281")]
    [InlineData("CEN TR 15281")]
    [InlineData("TR 15281")]
    public void All_reference_forms_match_CEN_TR_15281_document(string query)
    {
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0, 0, 1, 1,
                "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.",
                70, 11, [1])
        };
        var entries = ExactMatchEntryExtractor.Extract(units);
        var entryTexts = entries.Select(e => e.NormalizedText).ToHashSet(StringComparer.Ordinal);

        var queryTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query);

        var matched = queryTerms.Any(term => entryTexts.Contains(term));
        Assert.True(matched, $"Query '{query}' should match CEN TR 15281 document");
    }

    [Fact]
    public void ExpandReferenceVariants_strips_prefixes_progressively()
    {
        var variants = ExactMatchEntryExtractor.ExpandReferenceVariants("CEN TR 15281").ToList();

        output.WriteLine($"Variants of 'CEN TR 15281': [{string.Join(", ", variants.Select(v => $"'{v}'"))}]");

        Assert.Contains("TR 15281", variants, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("15281", variants, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandReferenceVariants_handles_simple_two_part_reference()
    {
        var variants = ExactMatchEntryExtractor.ExpandReferenceVariants("EN 15281").ToList();

        output.WriteLine($"Variants of 'EN 15281': [{string.Join(", ", variants.Select(v => $"'{v}'"))}]");

        Assert.Contains("15281", variants, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandReferenceVariants_returns_empty_for_single_token()
    {
        var variants = ExactMatchEntryExtractor.ExpandReferenceVariants("IND570").ToList();
        Assert.Empty(variants);
    }
}
