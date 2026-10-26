using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeGlyphGeometryTests
{
    private const string MissingFontName = "SAAIAGlyphMetricFixtureNeverInstalled7C84A120";

    [Fact]
    public void Nonembedded_unresolved_truetype_keeps_text_but_does_not_claim_observed_glyph_geometry()
    {
        var bytes = BuildPdf();
        using var stream = new MemoryStream(bytes);
        var native = PdfExtractor.Extract(stream, default);
        var page = Assert.Single(native.Pages);
        Assert.Contains("SAFE", page.RawText!, StringComparison.Ordinal);
        Assert.NotEmpty(page.NativeWordObservation!.Words);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), page.NativeWordObservation.SourceSha256);
        var observed = JsonSerializer.SerializeToElement(page.NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("unavailable", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.Equal("native_glyph_metrics_unresolved", observed.GetProperty("glyphGeometryReason").GetString());
        Assert.Equal("available", observed.GetProperty("availability").GetString());
        Assert.True(observed.GetProperty("glyphGeometryDescriptorFallbackFontCount").GetInt32() > 0);
    }

    [Fact]
    public void Descriptor_font_bbox_is_not_admissible_as_positive_table_geometry_evidence()
    {
        using var stream = new MemoryStream(BuildPdf());
        var extraction = PdfExtractor.Extract(stream, default);
        var document = Canonical(extraction);
        var sourceBefore = JsonSerializer.Serialize(document.Pages[0].Tables[0].Cells, NativeTableCoverageFixture.JsonOptions);
        var nativeBefore = JsonSerializer.Serialize(extraction, NativeTableCoverageFixture.JsonOptions);

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, extraction);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Equal(1, result.UnavailableTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        var attributes = document.Pages[0].Tables[0].Provenance.Attributes;
        Assert.Equal("native_glyph_metrics_unresolved", attributes["nativeTableCoverageOutcome"]);
        Assert.False(attributes.ContainsKey("nativeTableCoverageEvidenceSha256"));
        Assert.False(attributes.ContainsKey("nativeTableCoverageUnrepresentedWordCount"));
        Assert.Contains("canonical_table_native_region_coverage_unavailable", document.Pages[0].QualityFlags);
        Assert.Equal(sourceBefore, JsonSerializer.Serialize(document.Pages[0].Tables[0].Cells, NativeTableCoverageFixture.JsonOptions));
        Assert.Equal(nativeBefore, JsonSerializer.Serialize(extraction, NativeTableCoverageFixture.JsonOptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Standard14_afm_metrics_are_a_distinct_supported_method_including_inherited_resources(bool inherited)
    {
        using var stream = new MemoryStream(BuildPdf(standard14: true, inherited: inherited));
        var extraction = PdfExtractor.Extract(stream, default);
        var observed = JsonSerializer.SerializeToElement(extraction.Pages[0].NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("available", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.True(observed.GetProperty("glyphGeometryStandard14FontCount").GetInt32() > 0);
        Assert.Equal(0, observed.GetProperty("glyphGeometryDescriptorFallbackFontCount").GetInt32());
        var result = CanonicalNativeTableCoverageAuditor.Apply(Canonical(extraction), extraction);
        Assert.Equal(1, result.AuditedTableCount);
    }

    [Fact]
    public void An_unused_unresolved_font_does_not_poison_observed_standard14_text()
    {
        using var stream = new MemoryStream(BuildPdf(standard14: true, extraUnused: true));
        var extraction = PdfExtractor.Extract(stream, default);
        var observed = JsonSerializer.SerializeToElement(extraction.Pages[0].NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("available", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.Equal(0, observed.GetProperty("glyphGeometryDescriptorFallbackFontCount").GetInt32());
        Assert.Equal(1, CanonicalNativeTableCoverageAuditor.Apply(Canonical(extraction), extraction).AuditedTableCount);
    }

    [Theory]
    [InlineData("FirstChar")]
    [InlineData("Widths")]
    public void An_incomplete_truetype_dictionary_is_not_mislabelled_as_descriptor_bbox_geometry(string missing)
    {
        using var stream = new MemoryStream(BuildPdf(incompleteTrueType: missing));
        var extraction = PdfExtractor.Extract(stream, default);
        Assert.Contains("SAFE", extraction.Pages[0].RawText!, StringComparison.Ordinal);
        var observed = JsonSerializer.SerializeToElement(extraction.Pages[0].NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("unknown", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.Equal("native_glyph_font_metrics_branch_unverified", observed.GetProperty("glyphGeometryReason").GetString());
        Assert.Equal(0, observed.GetProperty("glyphGeometryDescriptorFallbackFontCount").GetInt32());
        Assert.Equal(0, CanonicalNativeTableCoverageAuditor.Apply(Canonical(extraction), extraction).AuditedTableCount);
    }

    [Fact]
    public void An_unresolved_font_used_by_a_form_xobject_is_observed_instead_of_assuming_page_resources_complete()
    {
        using var stream = new MemoryStream(BuildPdf(inForm: true));
        var extraction = PdfExtractor.Extract(stream, default);
        Assert.Contains("SAFE", extraction.Pages[0].RawText!, StringComparison.Ordinal);
        var observed = JsonSerializer.SerializeToElement(extraction.Pages[0].NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("unavailable", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.Equal("native_glyph_metrics_unresolved", observed.GetProperty("glyphGeometryReason").GetString());
        Assert.Equal(0, CanonicalNativeTableCoverageAuditor.Apply(Canonical(extraction), extraction).AuditedTableCount);
    }

    [Fact]
    public void Distinct_resources_with_the_same_used_font_name_are_not_arbitrarily_matched()
    {
        using var stream = new MemoryStream(BuildPdf(duplicateName: true));
        var extraction = PdfExtractor.Extract(stream, default);
        var observed = JsonSerializer.SerializeToElement(extraction.Pages[0].NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal("unknown", observed.GetProperty("glyphGeometryAvailability").GetString());
        Assert.Equal("native_glyph_font_identity_ambiguous", observed.GetProperty("glyphGeometryReason").GetString());
        Assert.Equal(0, CanonicalNativeTableCoverageAuditor.Apply(Canonical(extraction), extraction).AuditedTableCount);
    }

    [Fact]
    public void Legacy_word_observations_without_glyph_provenance_do_not_become_a_false_complete_audit()
    {
        using var source = new NativeTableCoverageFixture(false);
        var page = source.Extraction.Pages[0]; var old = page.NativeWordObservation!;
        var legacy = new PdfNativeWordObservation(old.SourceSha256, old.Availability, old.Words,
            old.ObservedWordCount, old.Reason, old.InspectedWordCount)
        {
            CoordinateFrameAvailability = old.CoordinateFrameAvailability, PageRotationDegrees = old.PageRotationDegrees,
            CropBoxLeft = old.CropBoxLeft, CropBoxBottom = old.CropBoxBottom,
            MediaBoxLeft = old.MediaBoxLeft, MediaBoxBottom = old.MediaBoxBottom
        };
        var extraction = source.Extraction with { Pages = [page with { NativeWordObservation = legacy }] };
        var document = source.Canonical();

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, extraction);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        Assert.Equal("native_glyph_geometry_unavailable", document.Pages[0].Tables[0].Provenance.Attributes["nativeTableCoverageOutcome"]);
    }

    private static CanonicalDocument Canonical(PdfExtractionResult extraction) => new()
    {
        DocumentId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), PageCount = 1,
        Source = new() { Sha256 = extraction.Pages[0].NativeWordObservation!.SourceSha256, MediaType = "application/pdf" },
        Pages = [new() { PageNumber = 1, Geometry = new() { Unit = "pt", Width = 600, Height = 800 },
            Tables = [new() { TableId = "glyph-table", RowCount = 1, ColumnCount = 1, Polygon = WholePage(),
                Cells = [new() { CellId = "glyph-cell", RowIndex = 0, ColumnIndex = 0, Polygon = WholePage(),
                    Text = new() { Raw = "SAFE 12 -5", Canonical = "SAFE 12 -5" } }] }] }]
    };
    private static CanonicalPolygon WholePage() => new() { Points = [new() { X = 0, Y = 0 }, new() { X = 1, Y = 0 }, new() { X = 1, Y = 1 }, new() { X = 0, Y = 1 }] };

    private static byte[] BuildPdf(bool standard14 = false, bool inherited = false, bool inForm = false,
        bool extraUnused = false, bool duplicateName = false, string? incompleteTrueType = null)
    {
        const string content = "BT\n/F1 10 Tf\n60 600 Td\n(SAFE 12 -5) Tj\nET\n";
        var widths = string.Join(" ", Enumerable.Repeat("500", 95));
        var fontName = incompleteTrueType is null ? MissingFontName : "Helvetica";
        string TrueType(int descriptor) => $"<< /Type /Font /Subtype /TrueType /BaseFont /{fontName} /Encoding /WinAnsiEncoding{(incompleteTrueType == "FirstChar" ? "" : " /FirstChar 32")} /LastChar 126{(incompleteTrueType == "Widths" ? "" : " /Widths [" + widths + "]")} /FontDescriptor {descriptor} 0 R >>";
        string Descriptor(int right) => $"<< /Type /FontDescriptor /FontName /{fontName} /Flags 32 /FontBBox [-500 -200 {right} 1000] /Ascent 800 /Descent -200 /CapHeight 700 /ItalicAngle 0 /StemV 80 >>";
        var resources = inForm ? "<< /XObject << /Fm1 7 0 R >> >>" : $"<< /Font << /F1 4 0 R{(extraUnused || duplicateName ? " /F2 7 0 R" : "")} >> >>";
        var pageContent = inForm ? "q /Fm1 Do Q\n" : content;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1{(inherited ? " /Resources " + resources : "")} >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800]{(inherited ? "" : " /Resources " + resources)} /Contents 5 0 R >>",
            standard14 ? "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" : TrueType(6),
            $"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}endstream",
            Descriptor(2000)
        };
        if (inForm) objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        if (extraUnused || duplicateName) { objects.Add(TrueType(8)); objects.Add(Descriptor(1000)); }
        using var stream = new MemoryStream(); void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n"); var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++) { offsets.Add(stream.Position); Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = stream.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
