using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Contracts.Tests;

internal static class CanonicalContractFixture
{
    internal const string BlockId = "block_intro";
    internal const string SpanId = "span_intro";
    internal const string CellId = "cell_duration";

    internal static CanonicalDocument CreateDocument()
    {
        var blockText = Text("Petit-déjeuner équilibré");
        var spanText = Text("Petit-déjeuner");
        var cellText = Text("20 min");

        return new()
        {
            DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Source = new()
            {
                Sha256 = new string('a', 64),
                MediaType = "application/pdf",
                SizeBytes = 42,
                DisplayName = "sample.pdf"
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
                        Width = 595,
                        Height = 842,
                        Unit = "pt"
                    },
                    ImageCoverage = 0.15,
                    Blocks =
                    [
                        new()
                        {
                            BlockId = BlockId,
                            BlockType = "heading",
                            ReadingOrder = 1,
                            Text = blockText,
                            Polygon = Box(0.1, 0.1, 0.8, 0.2),
                            Spans =
                            [
                                new()
                                {
                                    SpanId = SpanId,
                                    Text = spanText,
                                    Polygon = Box(0.1, 0.1, 0.4, 0.2),
                                    Words =
                                    [
                                        new()
                                        {
                                            WordId = "word_breakfast",
                                            Text = "Petit-déjeuner",
                                            Polygon = Box(0.1, 0.1, 0.4, 0.2),
                                            Confidence = 0.99
                                        }
                                    ],
                                    Provenance = Provenance()
                                }
                            ],
                            Provenance = Provenance()
                        }
                    ],
                    Tables =
                    [
                        new()
                        {
                            TableId = "table_recipe",
                            RowCount = 1,
                            ColumnCount = 1,
                            Polygon = Box(0.1, 0.3, 0.8, 0.5),
                            CaptionBlockIds = [BlockId],
                            Cells =
                            [
                                new()
                                {
                                    CellId = CellId,
                                    Text = cellText,
                                    Polygon = Box(0.1, 0.3, 0.8, 0.5),
                                    BlockIds = [BlockId],
                                    SpanIds = [SpanId]
                                }
                            ],
                            Provenance = Provenance()
                        }
                    ],
                    Figures =
                    [
                        new()
                        {
                            FigureId = "figure_breakfast",
                            FigureType = "photo",
                            Polygon = Box(0.1, 0.55, 0.8, 0.9),
                            CaptionBlockIds = [BlockId],
                            AssetIds = ["asset_breakfast"],
                            Provenance = Provenance()
                        }
                    ]
                }
            ],
            SectionTree =
            [
                new()
                {
                    SectionId = "section_breakfast",
                    Level = 1,
                    PageStart = 1,
                    PageEnd = 1,
                    TitleBlockIds = [BlockId],
                    ContentBlockIds = [BlockId]
                }
            ],
            Assets =
            [
                new()
                {
                    AssetId = "asset_breakfast",
                    MediaType = "image/png",
                    Sha256 = new string('c', 64),
                    StorageKey = "sha256/cc/example.png",
                    SizeBytes = 10
                }
            ],
            Relations =
            [
                new()
                {
                    RelationId = "relation_caption",
                    RelationType = "caption_of",
                    SourceId = BlockId,
                    TargetId = "figure_breakfast",
                    Confidence = 0.95,
                    Provenance = Provenance()
                }
            ]
        };
    }

    internal static SourceAnchor CreateAnchor(CanonicalDocument document)
    {
        var rawText = document.Pages[0].Blocks[0].Spans[0].Text.Raw;
        return new()
        {
            AnchorId = "anchor_breakfast",
            DocumentId = document.DocumentId,
            RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256,
            ManifestSha256 = document.ManifestSha256,
            ProjectionId = "chunk_breakfast",
            ProjectionType = "retrieval_chunk",
            Precision = "span",
            Regions =
            [
                new()
                {
                    PageNumber = 1,
                    SpanIds = [SpanId],
                    Polygon = Box(0.1, 0.1, 0.4, 0.2),
                    RawTextSha256 = Sha256(rawText)
                }
            ]
        };
    }

    internal static CanonicalTextVariants Text(string value)
        => new()
        {
            Raw = value,
            Canonical = value,
            Normalized = value.ToLowerInvariant(),
            Retrieval = value,
            Display = value,
            RawSha256 = Sha256(value)
        };

    internal static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CanonicalExtractionProvenance Provenance()
        => new()
        {
            StageId = "native_parser",
            Method = "native_text",
            Engine = "fixture",
            EngineVersion = "1",
            Confidence = 0.98
        };

    private static CanonicalPolygon Box(double left, double top, double right, double bottom)
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
