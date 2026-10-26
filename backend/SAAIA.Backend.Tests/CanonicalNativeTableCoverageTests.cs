using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalNativeTableCoverageTests
{
    internal const string Unresolved = "canonical_table_native_region_coverage_unresolved";

    [Fact]
    public void Declared_complete_grid_warns_when_actual_native_words_exist_below_every_cell_inside_its_polygon()
    {
        using var source = new NativeTableCoverageFixture(lowerWords: true);
        var structure = source.Project();
        var page = Assert.Single(structure.CanonicalDocument!.Pages);
        var table = Assert.Single(page.Tables);

        Assert.Equal(2, table.RowCount); Assert.Equal(1, table.ColumnCount);
        Assert.Equal(new[] { "HEADER", "UPPER" }, table.Cells.Select(cell => cell.Text.Raw));
        Assert.Contains(Unresolved, page.QualityFlags);
        Assert.Equal("available", table.Provenance.Attributes["nativeTableCoverageAvailability"]);
        Assert.True(int.Parse(table.Provenance.Attributes["nativeTableCoverageUnrepresentedWordCount"], CultureInfo.InvariantCulture) >= 3);
        Assert.Equal("unrepresented_native_words_observed", table.Provenance.Attributes["nativeTableCoverageOutcome"]);
        Assert.Contains(structure.Units, unit => (unit.ExtractionQualitySignals ?? []).Contains(Unresolved, StringComparer.Ordinal));
        Assert.DoesNotContain(table.Cells, cell => cell.Text.Canonical.Contains("LOWER", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blank_area_or_words_outside_the_table_are_not_reported_as_missing_table_cells(bool outsideWords)
    {
        using var source = new NativeTableCoverageFixture(lowerWords: false, outsideWords: outsideWords);
        var structure = source.Project();

        Assert.DoesNotContain(Unresolved, structure.CanonicalDocument!.Pages[0].QualityFlags);
        Assert.Equal(2, structure.CanonicalDocument.Pages[0].Tables[0].Cells.Count);
    }

    [Fact]
    public void Native_word_observations_are_bound_to_the_original_pdf_bytes_without_publishing_extra_cells()
    {
        using var source = new NativeTableCoverageFixture(lowerWords: true);
        var value = JsonSerializer.SerializeToElement(source.Extraction.Pages[0], NativeTableCoverageFixture.JsonOptions);

        Assert.True(value.TryGetProperty("nativeWordObservation", out var observed));
        Assert.Equal(source.SourceSha, observed.GetProperty("sourceSha256").GetString());
        Assert.Equal("available", observed.GetProperty("availability").GetString());
        Assert.Contains(observed.GetProperty("words").EnumerateArray(), word => word.GetProperty("text").GetString() == "LOWER");
        Assert.Equal(source.SourceSha, Convert.ToHexString(SHA256.HashData(source.PdfBytes)).ToLowerInvariant());
    }

    [Fact]
    public void Missing_native_observation_is_unavailable_and_never_a_zero_missing_word_success()
    {
        using var source = new NativeTableCoverageFixture(lowerWords: true);
        var original = source.Extraction.Pages[0];
        var native = source.Extraction with { Pages = [new(original.PageNumber, original.Text, original.WordCount,
            original.CharCount, original.Checksum, original.Quality, WidthPoints: 600, HeightPoints: 800)] };

        var structure = source.Project(native);
        var table = structure.CanonicalDocument!.Pages[0].Tables[0];

        Assert.Equal("unavailable", table.Provenance.Attributes["nativeTableCoverageAvailability"]);
        Assert.False(table.Provenance.Attributes.ContainsKey("nativeTableCoverageUnrepresentedWordCount"));
    }
}

internal sealed class NativeTableCoverageFixture : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"saaia-table-word-coverage-{Guid.NewGuid():N}.pdf");
    internal byte[] PdfBytes { get; }
    internal string SourceSha { get; }
    internal PdfExtractionResult Extraction { get; }

    internal NativeTableCoverageFixture(bool lowerWords, bool outsideWords = false, string lowerText = "LOWER -5 NOT")
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("HEADER", 10, new PdfPoint(60, 700), font);
        page.AddText("UPPER", 10, new PdfPoint(60, 600), font);
        if (lowerWords) page.AddText(lowerText, 10, new PdfPoint(60, 400), font);
        if (outsideWords) page.AddText("OUTSIDE FOOTNOTE", 10, new PdfPoint(60, 100), font);
        PdfBytes = builder.Build();
        SourceSha = Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant();
        File.WriteAllBytes(path, PdfBytes);
        Extraction = PdfExtractor.Extract(path);
    }

    internal IngestionStructureResult Project(PdfExtractionResult? extraction = null)
    {
        var response = Response();
        return IngestionProjectionStage.ProjectStructure(new(Guid.NewGuid(), Guid.NewGuid(), 1,
            SourceSha, PdfBytes.Length, "native-table-fixture.pdf",
            new() { NativeTextCoverageReconciliationEnabled = false, NativePdfImageInventoryEnabled = false },
            response, extraction ?? Extraction, DoclingLegacyExtractionAdapter.Project(response)), NullLogger.Instance);
    }

    internal CanonicalDocument Canonical()
        => DoclingCanonicalDocumentAdapter.Project(new(Guid.NewGuid(), Guid.NewGuid(), SourceSha,
            PdfBytes.Length, "native-table-fixture.pdf", SourceSha, "canonical_projection", "fixture", "fixture"), Response().Document.JsonContent!);

    private static DoclingConvertResponse Response() => new()
    {
        Document = new() { JsonContent = new()
        {
            SchemaName = "DoclingDocument", Version = "1.11.0",
            Pages = new(StringComparer.Ordinal) { ["1"] = new() { PageNumber = 1, Size = new() { Width = 600, Height = 800 } } },
            Body = new() { Children = [new() { Ref = "#/tables/0" }] },
            Tables = [new() { SelfRef = "#/tables/0", Label = "table", ContentLayer = "body",
                Provenance = [new() { PageNumber = 1, BoundingBox = Box(50, 720, 550, 240) }],
                Data = new() { RowCount = 2, ColumnCount = 1, Cells =
                [
                    new() { RowIndex = 0, ColumnIndex = 0, IsColumnHeader = true, Text = "HEADER", BoundingBox = Box(50, 720, 550, 680) },
                    new() { RowIndex = 1, ColumnIndex = 0, Text = "UPPER", BoundingBox = Box(50, 640, 550, 560) }
                ] } }]
        } }
    };

    private static DoclingBoundingBox Box(double left, double top, double right, double bottom)
        => new() { Left = left, Top = top, Right = right, Bottom = bottom, CoordinateOrigin = "BOTTOMLEFT" };

    public void Dispose() { if (File.Exists(path)) File.Delete(path); }
}
