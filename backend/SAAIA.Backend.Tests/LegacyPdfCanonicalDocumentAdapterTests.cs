using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LegacyPdfCanonicalDocumentAdapterTests
{
    [Fact]
    public void Adapter_preserves_raw_and_canonical_text_without_inventing_spatial_blocks()
    {
        var extraction = CreateExtraction();
        var context = CreateContext();

        var document = LegacyPdfCanonicalDocumentAdapter.Project(
            context,
            extraction,
            [new(0, "Introduction", 1, 1, 2, 0, 2)]);

        var firstPage = document.Pages[0];
        var block = Assert.Single(firstPage.Blocks);
        Assert.Equal("Raw  page   text", block.Text.Raw);
        Assert.Equal("Canonical page text", block.Text.Canonical);
        Assert.Equal("canonical page text", block.Text.Normalized);
        Assert.Equal(595, firstPage.Geometry.Width);
        Assert.Equal(842, firstPage.Geometry.Height);
        Assert.Null(block.Polygon);
        Assert.Contains("spatial_blocks_unavailable", block.QualityFlags);
        Assert.Equal(2, Assert.Single(document.SectionTree).ContentBlockIds.Count);
        Assert.Empty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Page_range_anchor_is_reconstructible_and_declares_coarse_precision()
    {
        var document = LegacyPdfCanonicalDocumentAdapter.Project(
            CreateContext(),
            CreateExtraction(),
            [new(0, "Introduction", 1, 1, 2, 0, 2)]);

        var anchor = LegacyCanonicalSourceAnchorProjector.ProjectPageRange(
            document,
            "chunk_0",
            "retrieval_chunk",
            1,
            2);
        var evidence = CanonicalEvidenceResolver.Resolve(anchor, document);

        Assert.Equal("page_block", anchor.Precision);
        Assert.Equal(2, evidence.Count);
        Assert.Equal("Raw  page   text", evidence[0].RawText);
        Assert.Equal("Second raw text", evidence[1].RawText);
    }

    private static PdfExtractionResult CreateExtraction()
    {
        var firstText = "Canonical page text";
        var secondText = "Second canonical text";
        var pages = new List<ExtractedPdfPage>
        {
            new(
                1,
                firstText,
                3,
                firstText.Length,
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(firstText)),
                PdfPageExtractionQuality.FromText(firstText, 3, firstText.Length),
                ImageCount: 1,
                RawText: "Raw  page   text",
                WidthPoints: 595,
                HeightPoints: 842),
            new(
                2,
                secondText,
                3,
                secondText.Length,
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secondText)),
                PdfPageExtractionQuality.FromText(secondText, 3, secondText.Length),
                RawText: "Second raw text")
        };

        return new(
            [],
            pages,
            PdfExtractionQualitySummary.FromPages(pages),
            Source: "pdf_text");
    }

    private static LegacyCanonicalProjectionContext CreateContext()
        => new(
            DocumentId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SourceSha256: new string('a', 64),
            SourceSizeBytes: 123,
            SourceDisplayName: "sample.pdf",
            ManifestSha256: new string('b', 64),
            StageId: "native_parser",
            Method: "native_text",
            Engine: "PdfPig",
            EngineVersion: "0.1.13",
            Language: "fr");
}
