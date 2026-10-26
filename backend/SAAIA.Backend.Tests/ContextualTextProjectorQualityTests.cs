using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ContextualTextProjectorQualityTests
{
    private const string RegionWarning = "canonical_table_native_region_coverage_unresolved";

    [Fact]
    public void Context_quality_unions_chunk_and_only_text_contributors_without_changing_source_membership()
    {
        var units = new[]
        {
            Unit(0, "Previous", ["previous_observation"]),
            Unit(1, "Current", ["current_observation"]),
            Unit(2, "Next", ["next_observation"])
        };
        var chunk = Chunk([RegionWarning]);
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], units, [chunk]));
        Assert.True(entry.IncludesCurrentUnitContext);
        Assert.True(entry.IncludesPreviousContext);
        Assert.True(entry.IncludesNextContext);
        Assert.Equal(new[] { 1 }, entry.SourceUnitOrdinals);
        Assert.Equal(1, entry.PageStart);
        Assert.Equal(1, entry.PageEnd);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Text)), entry.Checksum);
        var actual = Json(entry);
        Assert.Equal(new[] { 0, 1, 2 }, Integers(actual, "contributorUnitOrdinals"));
        Assert.Equal(new[] { RegionWarning, "current_observation", "next_observation", "previous_observation" },
            Strings(actual, "extractionQualitySignals"));
        Assert.Equal("complete", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.Equal("ok", actual.GetProperty("extractionTextStatus").GetString());
        Assert.False(actual.GetProperty("extractionOcrCandidate").GetBoolean());
        Assert.False(actual.GetProperty("extractionTextSparse").GetBoolean());
    }

    [Fact]
    public void Context_quality_does_not_import_warnings_from_rejected_or_duplicate_unit_context()
    {
        var chunk = Chunk([RegionWarning]);
        var units = new[]
        {
            Unit(0, "Rejected", ["replacement_chars_remaining"]) with
            {
                ExtractionTextStatus = "low_text", ExtractionTextSparse = true, ExtractionOcrCandidate = true
            },
            Unit(1, "Same", ["not_appended_current_warning"]) with { Text = chunk.Text },
            Unit(2, "Other section", ["not_appended_next_warning"]) with { SectionOrdinal = 8 }
        };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], units, [chunk]));
        Assert.False(entry.IncludesCurrentUnitContext);
        Assert.False(entry.IncludesPreviousContext);
        Assert.False(entry.IncludesNextContext);
        Assert.DoesNotContain("previous_context:", entry.Text);
        var actual = Json(entry);
        Assert.Empty(Integers(actual, "contributorUnitOrdinals"));
        Assert.Equal(new[] { RegionWarning }, Strings(actual, "extractionQualitySignals"));
        Assert.False(actual.GetProperty("extractionOcrCandidate").GetBoolean());
    }

    [Fact]
    public void Context_quality_unknown_legacy_inputs_do_not_invent_clean_status_or_false_ocr()
    {
        var chunk = Chunk(null) with { ExtractionTextStatus = null };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], [], [chunk]));
        AssertUnknown(Json(entry));
    }

    [Fact]
    public void Context_quality_mixed_unknown_is_explicit_and_cannot_assert_false_ocr_or_sparse()
    {
        var unknown = Unit(2, "Unknown", null) with { ExtractionTextStatus = null };
        var current = Unit(1, "Current", []) with { Text = Chunk([]).Text };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], [current, unknown], [Chunk([RegionWarning])]));
        Assert.True(entry.IncludesNextContext);
        var actual = Json(entry);
        Assert.Equal("mixed_unknown", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.Equal("mixed_unknown", actual.GetProperty("extractionTextStatus").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionOcrCandidate").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionTextSparse").ValueKind);
        Assert.Equal(new[] { RegionWarning }, Strings(actual, "extractionQualitySignals"));
        Assert.Equal(new[] { 2 }, Integers(actual, "contributorUnitOrdinals"));
    }

    [Fact]
    public void Context_quality_retains_positive_ocr_and_mixed_status_from_added_neighbor()
    {
        var current = Unit(1, "Current", []) with { Text = Chunk([]).Text };
        var next = Unit(2, "Next", ["region_ocr_observed"]) with
        {
            ExtractionTextStatus = "review_required", ExtractionOcrCandidate = true
        };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], [current, next], [Chunk([RegionWarning])]));
        var actual = Json(entry);
        Assert.Equal("complete", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.Equal("mixed", actual.GetProperty("extractionTextStatus").GetString());
        Assert.True(actual.GetProperty("extractionOcrCandidate").GetBoolean());
        Assert.Equal(new[] { RegionWarning, "region_ocr_observed" }, Strings(actual, "extractionQualitySignals"));
    }

    [Fact]
    public void Context_quality_unprojected_legacy_entry_defaults_are_nullable_unavailable()
    {
        var entry = new ProjectedContextualTextEntry(0, null, null, 0, 1, 1, "legacy", 6, 1, [1]);
        AssertUnknown(Json(entry));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Context_quality_partial_diagnostics_do_not_establish_negative_ocr_or_sparse(bool hasStatus)
    {
        var chunk = Chunk(hasStatus ? null : []) with { ExtractionTextStatus = hasStatus ? "ok" : null };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], [], [chunk]));
        var actual = Json(entry);
        Assert.Equal("mixed_unknown", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionOcrCandidate").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionTextSparse").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Context_quality_partial_diagnostics_preserve_observed_positive_ocr_and_sparse(bool hasStatus)
    {
        var chunk = Chunk(hasStatus ? null : []) with
        {
            ExtractionTextStatus = hasStatus ? "review_required" : null,
            ExtractionOcrCandidate = true,
            ExtractionTextSparse = true
        };
        var entry = Assert.Single(ContextualTextProjector.Project("Manual.pdf", [], [], [chunk]));
        var actual = Json(entry);
        Assert.Equal("mixed_unknown", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.True(actual.GetProperty("extractionOcrCandidate").GetBoolean());
        Assert.True(actual.GetProperty("extractionTextSparse").GetBoolean());
    }

    internal static ExtractedDocumentUnit Unit(int ordinal, string label, IReadOnlyList<string>? signals)
    {
        var text = $"{label} observation describes the inspection procedure and records the measured condition before the operator signs the report and confirms the required calibration checks for this instrument.";
        return new ExtractedDocumentUnit(ordinal, 0, 1, 1, text, text.Length, 28,
            SHA256.HashData(Encoding.UTF8.GetBytes(text)), ExtractionTextStatus: "ok", ExtractionQualitySignals: signals);
    }

    internal static ProjectedRetrievalChunk Chunk(IReadOnlyList<string>? signals)
    {
        const string text = "The instrument reading must be verified against the reference record before the inspection can be completed and signed by the responsible operator.";
        return new ProjectedRetrievalChunk(0, 0, 1, 1, 1, text, 24,
            SHA256.HashData(Encoding.UTF8.GetBytes(text)), "section_window_v1",
            ExtractionTextStatus: "ok", ExtractionQualitySignals: signals,
            SourceUnitOrdinals: [1], SourceUnitStartOrdinal: 1, SourceUnitEndOrdinal: 1,
            SourceUnitCount: 1, ChunkComposition: "single_unit");
    }

    private static JsonElement Json(ProjectedContextualTextEntry entry)
        => JsonSerializer.SerializeToElement(entry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static string[] Strings(JsonElement json, string key)
        => json.GetProperty(key).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static int[] Integers(JsonElement json, string key)
        => json.GetProperty(key).EnumerateArray().Select(item => item.GetInt32()).ToArray();

    private static void AssertUnknown(JsonElement actual)
    {
        Assert.Equal("unavailable", actual.GetProperty("extractionQualityAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionTextStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionOcrCandidate").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionTextSparse").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("extractionQualitySignals").ValueKind);
    }
}
