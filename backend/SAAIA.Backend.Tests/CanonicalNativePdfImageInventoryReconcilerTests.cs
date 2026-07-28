using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed class CanonicalNativePdfImageInventoryReconcilerTests
{
    [Fact]
    public void Apply_AddsPageAnchoredMechanicalFiguresWithoutSemanticClaims()
    {
        var document = BuildCanonicalDocument();
        var nativeExtraction = BuildNativeExtraction();

        var summary = CanonicalNativePdfImageInventoryReconciler.Apply(
            document,
            nativeExtraction);

        Assert.True(summary.Enabled);
        Assert.Equal(2, summary.CandidateImageCount);
        Assert.Equal(2, summary.AddedFigureCount);
        Assert.Equal(1, summary.EnrichedPageCount);
        var page = Assert.Single(document.Pages);
        Assert.Contains(
            CanonicalNativePdfImageInventoryReconciler.QualityFlag,
            page.QualityFlags);
        var first = page.Figures[0];
        Assert.Equal(
            CanonicalNativePdfImageInventoryReconciler.FigureType,
            first.FigureType);
        Assert.Null(first.AlternativeText);
        Assert.Empty(first.CaptionBlockIds);
        Assert.Empty(first.AssetIds);
        Assert.Equal(
            "llm_client",
            first.Provenance.Attributes["semanticDecisionOwner"]);
        Assert.Equal(
            "native_pdf_embedded_image",
            first.Provenance.Attributes["sourceType"]);
        var polygon = Assert.IsType<CanonicalPolygon>(first.Polygon);
        Assert.Equal(0.1, polygon.Points[0].X, 6);
        Assert.Equal(0.1, polygon.Points[0].Y, 6);
        Assert.Equal(0.5, polygon.Points[2].X, 6);
        Assert.Equal(0.5, polygon.Points[2].Y, 6);
        Assert.Empty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Apply_IsIdempotentForTheSameNativeImagePlacements()
    {
        var document = BuildCanonicalDocument();
        var nativeExtraction = BuildNativeExtraction();

        var first = CanonicalNativePdfImageInventoryReconciler.Apply(
            document,
            nativeExtraction);
        var second = CanonicalNativePdfImageInventoryReconciler.Apply(
            document,
            nativeExtraction);

        Assert.Equal(2, first.AddedFigureCount);
        Assert.Equal(0, second.AddedFigureCount);
        Assert.Equal(2, Assert.Single(document.Pages).Figures.Count);
        Assert.Empty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Apply_DoesNotMutateTheDocumentWhenDisabled()
    {
        var document = BuildCanonicalDocument();

        var summary = CanonicalNativePdfImageInventoryReconciler.Apply(
            document,
            BuildNativeExtraction(),
            enabled: false);

        Assert.False(summary.Enabled);
        Assert.Empty(Assert.Single(document.Pages).Figures);
    }

    private static CanonicalDocument BuildCanonicalDocument()
        => new()
        {
            DocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            RevisionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Source = new()
            {
                Sha256 = new string('a', 64),
                MediaType = "application/pdf",
                SizeBytes = 100,
                DisplayName = "mixed.pdf"
            },
            PageCount = 1,
            Pages =
            [
                new()
                {
                    PageNumber = 1,
                    Geometry = new()
                    {
                        Width = 600,
                        Height = 800,
                        Unit = "pt"
                    }
                }
            ],
            ManifestSha256 = new string('b', 64)
        };

    private static PdfExtractionResult BuildNativeExtraction()
    {
        var text = "Native text";
        var page = new ExtractedPdfPage(
            PageNumber: 1,
            Text: text,
            WordCount: 2,
            CharCount: text.Length,
            Checksum: System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)),
            WidthPoints: 600,
            HeightPoints: 800,
            NativeImageRegions:
            [
                new(
                    SourceIndex: 0,
                    Left: 60,
                    Right: 300,
                    Top: 720,
                    Bottom: 400,
                    WidthInSamples: 880,
                    HeightInSamples: 640),
                new(
                    SourceIndex: 1,
                    Left: 360,
                    Right: 540,
                    Top: 300,
                    Bottom: 100,
                    WidthInSamples: 400,
                    HeightInSamples: 300)
            ]);
        return new(
            [],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));
    }
}
