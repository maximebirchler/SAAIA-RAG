using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalNativeLayoutReconcilerTests
{
    [Fact]
    public void Apply_PreservesAnAlternativeWhenBagMatchesButOrderDisagrees()
    {
        const string rowWise = """
LEFT COLUMN RIGHT COLUMN
LEFT ALPHA DETAIL RIGHT ECHO DETAIL
LEFT BRAVO DETAIL RIGHT FOXTROT DETAIL
""";
        var document = Document(rowWise);
        var nativePage = NativePage(
            rowWise,
            [
                Layout(0, "LEFT COLUMN HEADING", 40, 700),
                Layout(1, "LEFT ALPHA DETAIL", 40, 670),
                Layout(2, "LEFT BRAVO DETAIL", 40, 640),
                Layout(3, "RIGHT COLUMN HEADING", 330, 700),
                Layout(4, "RIGHT ECHO DETAIL", 330, 670),
                Layout(5, "RIGHT FOXTROT DETAIL", 330, 640)
            ]);
        document.Pages[0].Blocks[0].Text =
            Text(rowWise.Replace(
                "LEFT COLUMN",
                "LEFT COLUMN HEADING",
                StringComparison.Ordinal).Replace(
                "RIGHT COLUMN",
                "RIGHT COLUMN HEADING",
                StringComparison.Ordinal));

        var result = CanonicalNativeLayoutReconciler.Apply(
            document,
            document.Pages[0],
            nativePage,
            navigationHint: false,
            engineVersion: "test");

        Assert.True(result.Applied);
        Assert.Equal(6, result.RecoveredBlockCount);
        Assert.True(result.BagAgreement >= 0.85);
        Assert.True(
            result.OrderedAgreement
            < CanonicalNativeLayoutReconciler
                .MaximumOrderedAgreement);
        var recovered = document.Pages[0].Blocks
            .Where(static block =>
                block.BlockType
                == CanonicalNativeLayoutReconciler
                    .RecoveryBlockType)
            .OrderBy(static block => block.ReadingOrder)
            .ToArray();
        Assert.Equal(6, recovered.Length);
        Assert.Equal("LEFT COLUMN HEADING", recovered[0].Text.Retrieval);
        Assert.Equal("RIGHT COLUMN HEADING", recovered[3].Text.Retrieval);
        Assert.All(
            recovered,
            static block => Assert.Equal(
                "llm_client",
                block.Provenance.Attributes[
                    "semanticDecisionOwner"]));
    }

    [Fact]
    public void Apply_DoesNotRecoverWhenCanonicalOrderAlreadyAgrees()
    {
        const string text =
            "Metric State Row Alpha Ready Row Beta Closed";
        var document = Document(text);
        document.Pages[0].Tables.Add(new()
        {
            TableId = "table_test",
            RowCount = 2,
            ColumnCount = 2,
            Polygon = Box(0.05, 0.05, 0.95, 0.95)
        });
        var nativePage = NativePage(
            text,
            [
                Layout(0, "Metric State", 60, 700),
                Layout(1, "Row Alpha Ready", 60, 670),
                Layout(2, "Row Beta Closed", 60, 640)
            ]);

        var result = CanonicalNativeLayoutReconciler.Apply(
            document,
            document.Pages[0],
            nativePage,
            navigationHint: false,
            engineVersion: "test");

        Assert.False(result.Applied);
        Assert.Equal(3, result.CandidateBlockCount);
        Assert.DoesNotContain(
            document.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeLayoutReconciler
                    .RecoveryBlockType);
    }

    [Fact]
    public void Apply_RejectsDominantUndersegmentedBlock()
    {
        var dominantTokens = Enumerable.Range(0, 600)
            .Select(static index => $"main{index}")
            .ToArray();
        var secondaryTokens = Enumerable.Range(0, 100)
            .Select(static index => $"side{index}")
            .ToArray();
        var rowWise = string.Join(
            " ",
            Enumerable.Range(0, dominantTokens.Length)
                .SelectMany(index =>
                    index < secondaryTokens.Length
                        ? new[]
                        {
                            dominantTokens[index],
                            secondaryTokens[index]
                        }
                        : [dominantTokens[index]]));
        var document = Document(rowWise);
        var nativePage = NativePage(
            rowWise,
            [
                Layout(
                    0,
                    string.Join(" ", dominantTokens),
                    40,
                    700),
                Layout(
                    1,
                    string.Join(" ", secondaryTokens),
                    330,
                    700)
            ]);

        var result = CanonicalNativeLayoutReconciler.Apply(
            document,
            document.Pages[0],
            nativePage,
            navigationHint: false,
            engineVersion: "test");

        Assert.False(result.Applied);
        Assert.Equal(
            CanonicalNativeLayoutReconciler
                .UndersegmentedDecisionReason,
            result.DecisionReason);
        Assert.Equal(2, result.CandidateBlockCount);
        Assert.DoesNotContain(
            document.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeLayoutReconciler
                    .RecoveryBlockType);
    }

    private static CanonicalDocument Document(string text)
        => new()
        {
            DocumentId =
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId =
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Source = new()
            {
                Sha256 = new string('a', 64),
                MediaType = "application/pdf",
                SizeBytes = text.Length,
                DisplayName = "layout-test.pdf"
            },
            PageCount = 1,
            ManifestSha256 = new string('b', 64),
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
                    },
                    Blocks =
                    [
                        new()
                        {
                            BlockId = "docling_row_wise",
                            BlockType = "text",
                            Ordinal = 0,
                            ReadingOrder = 0,
                            Polygon = Box(
                                0.05,
                                0.05,
                                0.95,
                                0.95),
                            Text = Text(text),
                            Provenance = new()
                            {
                                StageId = "canonical_projection",
                                Method = "docling",
                                Engine = "docling",
                                EngineVersion = "test"
                            }
                        }
                    ]
                }
            ]
        };

    private static ExtractedPdfPage NativePage(
        string text,
        IReadOnlyList<ExtractedPdfLayoutBlock> blocks)
        => new(
            PageNumber: 1,
            Text: text,
            WordCount: text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).Length,
            CharCount: text.Length,
            Checksum: SHA256.HashData(
                Encoding.UTF8.GetBytes(text)),
            Quality: PdfPageExtractionQuality.FromText(
                text,
                text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).Length,
                text.Length),
            WidthPoints: 600,
            HeightPoints: 800,
            NativeLayoutText: string.Join(
                Environment.NewLine,
                blocks.Select(static block => block.Text)),
            NativeLayoutBlocks: blocks,
            NativeLayoutAlgorithm:
                PdfNativeLayoutExtractor.Algorithm);

    private static ExtractedPdfLayoutBlock Layout(
        int order,
        string text,
        double left,
        double top)
        => new(
            order,
            text,
            left,
            left + 180,
            top,
            top - 20,
            PdfNativeLayoutExtractor.Algorithm);

    private static CanonicalTextVariants Text(string text)
        => new()
        {
            Raw = text,
            Canonical = text,
            Normalized = text.ToLowerInvariant(),
            Retrieval = text,
            Display = text,
            RawSha256 = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant()
        };

    private static CanonicalPolygon Box(
        double left,
        double top,
        double right,
        double bottom)
        => new()
        {
            Points =
            [
                new() { X = left, Y = top },
                new() { X = right, Y = top },
                new() { X = right, Y = bottom },
                new() { X = left, Y = bottom }
            ]
        };
}
