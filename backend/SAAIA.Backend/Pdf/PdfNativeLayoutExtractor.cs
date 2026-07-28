using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

internal static class PdfNativeLayoutExtractor
{
    internal const string Algorithm =
        "nearest_neighbour+recursive_xy_cut+unsupervised_reading_order";

    public static PdfNativeLayoutExtraction Extract(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var words = page.GetWords(
            NearestNeighbourWordExtractor.Instance);
        var segmented = RecursiveXYCut.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance
            .Get(segmented)
            .ToArray();
        var blocks = new List<ExtractedPdfLayoutBlock>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index];
            var text = PdfTextSanitizer.ForStorage(source.Text).Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            blocks.Add(new(
                ReadingOrder: blocks.Count,
                Text: text,
                Left: source.BoundingBox.Left,
                Right: source.BoundingBox.Right,
                Top: source.BoundingBox.Top,
                Bottom: source.BoundingBox.Bottom,
                Algorithm: Algorithm));
        }

        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
            builder.Append(block.Text);
        }

        return new(
            Text: builder.ToString(),
            Blocks: blocks,
            Algorithm: Algorithm);
    }
}

internal sealed record PdfNativeLayoutExtraction(
    string Text,
    IReadOnlyList<ExtractedPdfLayoutBlock> Blocks,
    string Algorithm);

internal sealed record ExtractedPdfLayoutBlock(
    int ReadingOrder,
    string Text,
    double Left,
    double Right,
    double Top,
    double Bottom,
    string Algorithm)
{
    public double Width => Math.Max(0d, Right - Left);
    public double Height => Math.Max(0d, Top - Bottom);
}
