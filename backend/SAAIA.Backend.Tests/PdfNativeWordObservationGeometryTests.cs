using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeWordObservationGeometryTests
{
    [Theory]
    [InlineData(90, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    public void Native_observation_records_the_actual_pdf_rotation_and_crop_origin(int rotation, bool shiftedCrop, bool inheritedBoxes)
    {
        var bytes = BuildPdf(rotation, shiftedCrop, inheritedBoxes);
        using var parsed = PdfDocument.Open(bytes);
        var actualPage = parsed.GetPage(1);
        Assert.Equal(rotation, actualPage.Rotation.Value);
        Assert.NotEmpty(actualPage.Text);
        if (shiftedCrop)
            Assert.Contains("/CropBox [20 30 580 770]", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        if (inheritedBoxes)
            Assert.False(actualPage.Dictionary.TryGet(NameToken.CropBox, out _));
        using var stream = new MemoryStream(bytes);

        var extraction = PdfExtractor.Extract(stream, default);
        var observation = Assert.Single(extraction.Pages).NativeWordObservation!;
        var json = JsonSerializer.SerializeToElement(observation, NativeTableCoverageFixture.JsonOptions);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), observation.SourceSha256);
        Assert.NotEmpty(Assert.Single(extraction.Pages).RawText!);
        Assert.True(json.TryGetProperty("pageRotationDegrees", out var recordedRotation),
            "The native observation must record the source PDF rotation, independently of canonical defaults.");
        Assert.Equal(actualPage.Rotation.Value, recordedRotation.GetInt32());
        Assert.True(json.TryGetProperty("cropBoxLeft", out var recordedLeft),
            "The native observation must record the source PDF crop origin.");
        Assert.True(json.TryGetProperty("cropBoxBottom", out var recordedBottom));
        // PdfPig normalizes Page.CropBox to the display origin. These values
        // must instead retain the original PDF dictionary, including inheritance.
        Assert.Equal(shiftedCrop ? 20d : 0d, recordedLeft.GetDouble());
        Assert.Equal(shiftedCrop ? 30d : 0d, recordedBottom.GetDouble());
    }

    [Theory]
    [InlineData(90, false, false, "unsupported_native_rotation")]
    [InlineData(0, true, false, "unsupported_native_crop_origin")]
    [InlineData(0, true, true, "unsupported_native_crop_origin")]
    public void Unproven_native_coordinate_frames_are_unavailable_even_when_canonical_rotation_defaults_to_zero(
        int rotation, bool shiftedCrop, bool inheritedBoxes, string reason)
    {
        var bytes = BuildPdf(rotation, shiftedCrop, inheritedBoxes);
        using var stream = new MemoryStream(bytes);
        var extraction = PdfExtractor.Extract(stream, default);
        var native = Assert.Single(extraction.Pages);
        var document = Canonical(extraction);
        var table = Assert.Single(document.Pages[0].Tables);
        Assert.Equal(0, document.Pages[0].Geometry.RotationDegrees);
        var sourceBefore = JsonSerializer.Serialize(table.Cells, NativeTableCoverageFixture.JsonOptions);
        var polygonBefore = JsonSerializer.Serialize(table.Polygon, NativeTableCoverageFixture.JsonOptions);
        var nativeBefore = JsonSerializer.Serialize(native.NativeWordObservation, NativeTableCoverageFixture.JsonOptions);

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, extraction);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Equal(1, result.UnavailableTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        Assert.Equal("unavailable", table.Provenance.Attributes["nativeTableCoverageAvailability"]);
        Assert.Equal(reason, table.Provenance.Attributes["nativeTableCoverageOutcome"]);
        Assert.False(table.Provenance.Attributes.ContainsKey("nativeTableCoverageUnrepresentedWordCount"));
        Assert.False(table.Provenance.Attributes.ContainsKey("nativeTableCoverageEvidenceSha256"));
        Assert.Equal(sourceBefore, JsonSerializer.Serialize(table.Cells, NativeTableCoverageFixture.JsonOptions));
        Assert.Equal(polygonBefore, JsonSerializer.Serialize(table.Polygon, NativeTableCoverageFixture.JsonOptions));
        Assert.Equal(nativeBefore, JsonSerializer.Serialize(native.NativeWordObservation, NativeTableCoverageFixture.JsonOptions));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), document.Source.Sha256);
    }

    [Fact]
    public void Legacy_observations_without_proven_native_frame_are_not_assumed_to_have_zero_origin()
    {
        using var stream = new MemoryStream(BuildPdf(0, false));
        var extraction = PdfExtractor.Extract(stream, default);
        var page = Assert.Single(extraction.Pages);
        var observation = page.NativeWordObservation!;
        extraction = extraction with { Pages = [page with { NativeWordObservation = new(
            observation.SourceSha256, observation.Availability, observation.Words,
            observation.ObservedWordCount, observation.Reason, observation.InspectedWordCount) }] };
        var document = Canonical(extraction);

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, extraction);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        Assert.Equal("native_coordinate_frame_unavailable", document.Pages[0].Tables[0].Provenance.Attributes["nativeTableCoverageOutcome"]);
    }

    [Fact]
    public void An_actual_unrotated_zero_origin_pdf_keeps_native_geometric_audit_available()
    {
        using var stream = new MemoryStream(BuildPdf(0, false));
        var extraction = PdfExtractor.Extract(stream, default);
        var document = Canonical(extraction);

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, extraction);

        Assert.Equal(1, result.AuditedTableCount);
        Assert.Equal(0, result.UnavailableTableCount);
        Assert.Equal(0, result.UnrepresentedWordCount);
        Assert.Equal("available", document.Pages[0].Tables[0].Provenance.Attributes["nativeTableCoverageAvailability"]);
    }

    private static CanonicalDocument Canonical(PdfExtractionResult extraction)
    {
        var native = Assert.Single(extraction.Pages);
        return new()
        {
            DocumentId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), PageCount = 1,
            Source = new() { Sha256 = native.NativeWordObservation!.SourceSha256, MediaType = "application/pdf" },
            Pages = [new()
            {
                PageNumber = 1,
                // This is the current Docling adapter contract: no observed PDF rotation is supplied.
                Geometry = new() { Width = native.WidthPoints, Height = native.HeightPoints, Unit = "pt" },
                Tables = [new()
                {
                    TableId = "frame-table", Ordinal = 1, RowCount = 1, ColumnCount = 1,
                    Polygon = WholePage(),
                    Cells = [new()
                    {
                        CellId = "frame-cell", RowIndex = 0, ColumnIndex = 0, Polygon = WholePage(),
                        Text = new() { Raw = "HEADER UPPER", Canonical = "HEADER UPPER" }
                    }]
                }]
            }]
        };
    }

    private static CanonicalPolygon WholePage() => new()
    {
        Points = [new() { X = 0, Y = 0 }, new() { X = 1, Y = 0 },
            new() { X = 1, Y = 1 }, new() { X = 0, Y = 1 }]
    };

    private static byte[] BuildPdf(int rotation, bool shiftedCrop, bool inheritedBoxes = false)
    {
        const string content = "BT\n/F1 10 Tf\n60 600 Td\n(HEADER UPPER) Tj\nET\n";
        var crop = shiftedCrop ? " /CropBox [20 30 580 770]" : "";
        var boxes = " /MediaBox [0 0 600 800]" + crop;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1{(inheritedBoxes ? boxes : "")} >>",
            $"<< /Type /Page /Parent 2 0 R{(inheritedBoxes ? "" : boxes)} /Rotate {rotation} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        ];
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
