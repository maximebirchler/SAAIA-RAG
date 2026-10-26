using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalObservationContinuationTests
{
    [Fact]
    public void Reconciliation_DoesNotPublishNativeWordFragmentsWhenAPhysicalLineIncludesTheOtherColumn()
    {
        var primary = new[] { "The manufacturer shall record results before release.", "Independent inspection is required." };
        var document = Document(primary);
        Reconcile(document, "The manufac- Independent inspection\nturer shall record results before release. is required.");

        Assert.Equal(primary, CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument()).Select(unit => unit.Text));
    }

    [Fact]
    public void Reconciliation_TypographicalDashInsideAnIdentifierIsNotANewNegativeValue()
    {
        var document = Document(["Reference 102-300 remains applicable."]);
        Reconcile(document, "Reference 102–300 remains applicable.");

        Assert.Equal("Reference 102-300 remains applicable.",
            Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument())).Text);
    }

    private static void Reconcile(CanonicalDocument document, string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var page = new ExtractedPdfPage(1, text, words, text.Length, SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            PdfPageExtractionQuality.FromText(text, words, text.Length));
        CanonicalNativeTextCoverageReconciler.Apply(document,
            new PdfExtractionResult([], [page], PdfExtractionQualitySummary.FromPages([page])));
    }

    private static CanonicalDocument Document(string[] texts) => new()
    {
        DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Source = new() { Sha256 = new string('a', 64), MediaType = "application/pdf" },
        ManifestSha256 = new string('b', 64), PageCount = 1,
        Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" },
            Blocks = texts.Select((text, index) => new CanonicalBlock
            {
                BlockId = "block_" + index, BlockType = "text", Ordinal = index, ReadingOrder = index,
                Text = new() { Raw = text, Canonical = text, Display = text, Retrieval = text,
                    Normalized = text.ToLowerInvariant(), RawSha256 = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() }
            }).ToList() }]
    };
}
