using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalObservationCoverageTypographyTests
{
    [Theory]
    [InlineData("native_text_recovery")]
    [InlineData("native_layout_recovery")]
    public void Coverage_NarrativeDashPresentationDoesNotRepublishACompletePrimaryObservation(string channel)
    {
        const string primary = "2042-Clause (b). Reference 102-300 remains applicable.";
        const string native = "2042—Clause (b). Reference 102–300 remains applicable.";
        var document = Document(primary);
        document.Pages[0].Blocks.Add(Block("native", channel, native, 1));

        Assert.Equal(primary, Assert.Single(Units(document)).Text);
        Assert.Equal(2, document.Pages[0].Blocks.Count);
        Assert.Equal(native, document.Pages[0].Blocks[1].Text.Raw);
    }

    [Fact]
    public void Reconciliation_NarrativeDashPresentationDoesNotCreateAVerifiedGapForTheSameSource()
    {
        const string primary = "2042-Clause (b). Reference 102-300 remains applicable.";
        var document = Document(primary);
        Reconcile(document, "2042—Clause (b). Reference 102–300 remains applicable.");

        Assert.Equal(primary, Assert.Single(Units(document)).Text);
        Assert.DoesNotContain(document.Pages[0].Blocks,
            block => block.BlockType == CanonicalNativeTextCoverageReconciler.RecoveryBlockType);
        Assert.Equal(primary, document.Pages[0].Blocks[0].Text.Raw);
    }

    [Fact]
    public void Reconciliation_TwoExplicitContinuationsOnParallelPhysicalLinesDoNotRepublishBothColumns()
    {
        var primary = new[] { "The manufacturer recorded the limits before approval.",
            "The inspection remained effective throughout the release." };
        var document = Document(primary);
        Reconcile(document, "The manufac- The inspection remained ef-\n"
            + "turer recorded the limits before approval. fective throughout the release.");

        Assert.Equal(primary, Units(document).Select(unit => unit.Text));
        Assert.Equal(primary, document.Pages[0].Blocks.Take(2).Select(block => block.Text.Raw));
    }

    [Theory]
    [InlineData("The valve may open at -5 °C.", "The valve may not open at -5 °C.")]
    [InlineData("The valve may open at 5 °C.", "The valve may open at -5 °C.")]
    [InlineData("The valve may open at -5 °C.", "The valve may open at +5 °C.")]
    [InlineData("The limit remains 1,5 bar.", "The limit remains 15 bar.")]
    public void Reconciliation_DashVariantNeverHidesAUniqueNegationSignOrDecimal(string primaryBody, string nativeBody)
    {
        var primary = "2042-Clause (b). " + primaryBody;
        var native = "2042—Clause (b). " + nativeBody;
        var document = Document(primary);
        Reconcile(document, native);

        var units = Units(document);
        Assert.Contains(units, unit => unit.Text == primary);
        Assert.Contains(units, unit => unit.Text == native);
        Assert.Equal(primary, document.Pages[0].Blocks[0].Text.Raw);
    }

    [Fact]
    public void Reconciliation_AmbiguousContinuationSuffixesRemainLiteralSourceObservations()
    {
        var document = Document("The manufacturer remains liable.", "The manufacture remains audited.");
        const string native = "The manufac- remains liable.\nturer ture";
        Reconcile(document, native);

        var recovered = document.Pages[0].Blocks.Where(block =>
            block.BlockType == CanonicalNativeTextCoverageReconciler.RecoveryBlockType).ToArray();
        Assert.Contains(recovered, block => block.Text.Raw.Contains("manufac-", StringComparison.Ordinal));
        Assert.Contains(recovered, block => block.Text.Raw.Contains("turer ture", StringComparison.Ordinal));
    }

    [Fact]
    public void Reconciliation_UniqueNegationOnContinuedPrefixKeepsItsLiteralSuffixLine()
    {
        const string primary = "The manufacturer may open.";
        const string native = "The manufac- may not open.\nturer";
        var document = Document(primary);
        Reconcile(document, native);

        Assert.Contains(Units(document), unit => unit.Text.Replace("\r\n", "\n", StringComparison.Ordinal) == native);
        Assert.Equal(primary, document.Pages[0].Blocks[0].Text.Raw);
    }

    private static IReadOnlyList<ExtractedDocumentUnit> Units(CanonicalDocument document)
        => CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument());

    private static void Reconcile(CanonicalDocument document, string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var page = new ExtractedPdfPage(1, text, words, text.Length, SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            PdfPageExtractionQuality.FromText(text, words, text.Length));
        CanonicalNativeTextCoverageReconciler.Apply(document,
            new PdfExtractionResult([], [page], PdfExtractionQualitySummary.FromPages([page])));
    }

    private static CanonicalDocument Document(params string[] texts) => new()
    {
        DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Source = new() { Sha256 = new string('a', 64), MediaType = "application/pdf" },
        ManifestSha256 = new string('b', 64), PageCount = 1,
        Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" },
            Blocks = texts.Select((text, index) => Block("primary_" + index, "text", text, index)).ToList() }]
    };

    private static CanonicalBlock Block(string id, string type, string text, int order) => new()
    {
        BlockId = id, BlockType = type, Ordinal = order, ReadingOrder = order,
        Text = new() { Raw = text, Canonical = text, Display = text, Retrieval = text,
            Normalized = text.ToLowerInvariant(), RawSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() },
        Provenance = new() { Engine = type.StartsWith("native_", StringComparison.Ordinal) ? "PdfPig" : "docling" }
    };
}
