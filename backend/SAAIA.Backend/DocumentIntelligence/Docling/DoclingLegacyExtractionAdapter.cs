using System.Security.Cryptography;
using System.Text;

internal static class DoclingLegacyExtractionAdapter
{
    private sealed record PageEntry(
        int GlobalOrder,
        string RawText,
        string CanonicalText);

    public static PdfExtractionResult Project(DoclingConvertResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var source = response.Document.JsonContent
            ?? throw new InvalidDataException("Docling JSON content is required.");
        var itemMap = DoclingDocumentTraversal.BuildItemMap(source);
        var orderByRef = DoclingDocumentTraversal.BuildReadingOrder(source, itemMap);
        var entriesByPage = source.Pages.Values
            .ToDictionary(
                static page => page.PageNumber,
                static _ => new List<PageEntry>());

        foreach (var item in source.Texts)
        {
            for (var index = 0; index < item.Provenance.Count; index++)
            {
                var provenance = item.Provenance[index];
                var entries = RequirePage(entriesByPage, provenance.PageNumber);
                entries.Add(new(
                    DoclingDocumentTraversal.ResolveOrder(orderByRef, item.SelfRef, index),
                    string.IsNullOrEmpty(item.OriginalText) ? item.Text ?? "" : item.OriginalText,
                    item.Text ?? ""));
            }
        }

        foreach (var table in source.Tables)
        {
            var tableText = RenderTable(table.Data);
            for (var index = 0; index < table.Provenance.Count; index++)
            {
                var provenance = table.Provenance[index];
                var entries = RequirePage(entriesByPage, provenance.PageNumber);
                entries.Add(new(
                    DoclingDocumentTraversal.ResolveOrder(orderByRef, table.SelfRef, index),
                    tableText,
                    tableText));
            }
        }

        var picturesByPage = source.Pictures
            .SelectMany(static picture => picture.Provenance)
            .GroupBy(static provenance => provenance.PageNumber)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var pages = new List<ExtractedPdfPage>(source.Pages.Count);
        foreach (var sourcePage in source.Pages.Values.OrderBy(static page => page.PageNumber))
        {
            var entries = RequirePage(entriesByPage, sourcePage.PageNumber)
                .OrderBy(static entry => entry.GlobalOrder)
                .ToArray();
            var rawText = Join(entries.Select(static entry => entry.RawText));
            var canonicalText = Join(entries.Select(static entry => entry.CanonicalText));
            var wordCount = CountTokens(canonicalText);
            var quality = PdfPageExtractionQuality.FromText(
                canonicalText,
                wordCount,
                canonicalText.Length);
            quality = quality with
            {
                Signals = quality.Signals
                    .Concat([
                        "document_intelligence_docling",
                        "spatial_blocks_available"
                    ])
                    .Concat(source.Tables.Any(table =>
                        table.Provenance.Any(provenance =>
                            provenance.PageNumber == sourcePage.PageNumber))
                        ? ["table_structure_available"]
                        : [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            pages.Add(new(
                sourcePage.PageNumber,
                canonicalText,
                wordCount,
                canonicalText.Length,
                SHA256.HashData(Encoding.UTF8.GetBytes(rawText)),
                quality,
                picturesByPage.TryGetValue(sourcePage.PageNumber, out var imageCount)
                    ? imageCount
                    : 0,
                rawText,
                sourcePage.Size.Width,
                sourcePage.Size.Height));
        }

        var tokens = pages
            .SelectMany(page => SplitTokens(page.Text)
                .Select(token => new WordToken(token, page.PageNumber)))
            .ToList();
        return new(
            tokens,
            pages,
            PdfExtractionQualitySummary.FromPages(pages),
            Source: "docling");
    }

    private static List<PageEntry> RequirePage(
        IReadOnlyDictionary<int, List<PageEntry>> entriesByPage,
        int pageNumber)
        => entriesByPage.TryGetValue(pageNumber, out var entries)
            ? entries
            : throw new InvalidDataException(
                $"Docling provenance references missing page {pageNumber}.");

    private static string RenderTable(DoclingTableData table)
    {
        if (table.RowCount <= 0 || table.ColumnCount <= 0)
            return "";

        var rows = new List<string>(table.RowCount);
        for (var row = 0; row < table.RowCount; row++)
        {
            var values = table.Cells
                .Where(cell => cell.RowIndex == row)
                .OrderBy(static cell => cell.ColumnIndex)
                .Select(static cell => cell.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            if (values.Length > 0)
                rows.Add(string.Join(" | ", values));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static string Join(IEnumerable<string> values)
        => string.Join(
            Environment.NewLine + Environment.NewLine,
            values.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static IEnumerable<string> SplitTokens(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int CountTokens(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
