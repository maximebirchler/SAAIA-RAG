using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Page and unit views of the same reconciled observations; no second semantic section inference.</summary>
internal static class CanonicalExtractionViewProjector
{
    internal static PdfExtractionResult Project(CanonicalDocument document, IReadOnlyList<ExtractedPdfPage> sourcePages, DoclingDocument source)
    {
        var sourceByPage = sourcePages.ToDictionary(page => page.PageNumber);
        var order = DoclingDocumentTraversal.BuildReadingOrder(source);
        var pages = document.Pages.OrderBy(page => page.PageNumber).Select(page =>
        {
            var observations = TextObservations(page, order).ToArray();
            var text = string.Join(Environment.NewLine + Environment.NewLine, observations.Select(item => item.Text));
            var raw = string.Join(Environment.NewLine + Environment.NewLine, observations.Select(item => item.Raw));
            var words = Count(text);
            var quality = PdfPageExtractionQuality.FromText(text, words, text.Length);
            quality = quality with { Signals = quality.Signals.Concat(page.QualityFlags)
                .Concat(CanonicalObservationSelector.SelectPage(page).Blocks.SelectMany(block => block.QualityFlags))
                .Append("canonical_observation_page_view").Distinct(StringComparer.Ordinal).ToArray() };
            var original = sourceByPage.TryGetValue(page.PageNumber, out var existing) ? existing
                : new ExtractedPdfPage(page.PageNumber, "", 0, 0, SHA256.HashData(Array.Empty<byte>()));
            return original with { Text = text, RawText = raw, WordCount = words, CharCount = text.Length,
                Checksum = SHA256.HashData(Encoding.UTF8.GetBytes(raw)), Quality = quality,
                ImageCount = Math.Max(original.ImageCount, page.Figures.Count),
                WidthPoints = page.Geometry.Width, HeightPoints = page.Geometry.Height };
        }).ToList();
        var tokens = pages.SelectMany(page => page.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => new WordToken(token, page.PageNumber))).ToList();
        return new(tokens, pages, PdfExtractionQualitySummary.FromPages(pages), Source: "docling_canonical_observations");
    }

    internal static IReadOnlyList<ExtractedDocumentUnit> ProjectUnits(CanonicalDocument document, DoclingDocument source)
    {
        var order = DoclingDocumentTraversal.BuildReadingOrder(source);
        var unassignedBlocks = document.Pages.SelectMany(page => page.Blocks)
            .Where(CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext)
            .Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        var sectionByBlock = document.SectionTree.SelectMany(section => section.ContentBlockIds.Concat(section.TitleBlockIds)
            .Select(block => (Block: block, Section: section.Ordinal)))
            .Where(item => !unassignedBlocks.Contains(item.Block))
            .GroupBy(item => item.Block, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Section).Distinct().ToArray(), StringComparer.Ordinal);
        var units = new List<ExtractedDocumentUnit>();
        foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
        {
            var blockSignals = page.Blocks.ToDictionary(block => block.BlockId,
                block => block.QualityFlags, StringComparer.Ordinal);
            foreach (var observation in TextObservations(page, order))
            {
                int? section = observation.BlockId is not null && sectionByBlock.TryGetValue(observation.BlockId, out var known)
                    && known.Length == 1 ? known[0] : null;
                var text = observation.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var quality = PdfPageExtractionQuality.FromText(text, Count(text), text.Length);
                units.Add(new(units.Count, section, page.PageNumber, page.PageNumber, text, text.Length, Count(text),
                    SHA256.HashData(Encoding.UTF8.GetBytes(text)), ExtractionTextStatus: quality.TextStatus,
                    ExtractionTextSparse: quality.TextSparse, ExtractionOcrCandidate: quality.OcrCandidate,
                    ExtractionQualitySignals: quality.Signals.Concat(page.QualityFlags)
                        .Concat(observation.BlockId is not null && blockSignals.TryGetValue(observation.BlockId, out var signals)
                            ? signals : [])
                        .Append(section.HasValue ? "canonical_section_link" : "section_association_not_asserted")
                        .Distinct(StringComparer.Ordinal).ToArray()));
            }
        }
        return units;
    }

    private static IEnumerable<(string Text, string Raw, string? BlockId)> TextObservations(CanonicalPage page,
        IReadOnlyDictionary<string, int> order)
    {
        var observations = new List<(int Order, int SubOrder, string Text, string Raw, string? BlockId)>();
        var selection = CanonicalObservationSelector.SelectPage(page);
        foreach (var block in selection.Blocks)
            if (!string.IsNullOrWhiteSpace(block.Text.Canonical))
                observations.Add((selection.ResolveOrder(block.BlockId, Order(block.Provenance, block.ReadingOrder)), block.Ordinal,
                    block.Text.Canonical, block.Text.Raw, block.BlockId));
        foreach (var table in selection.Tables)
            foreach (var row in table.Cells.GroupBy(cell => cell.RowIndex).OrderBy(row => row.Key))
            {
                var cells = row.OrderBy(cell => cell.ColumnIndex).ThenBy(cell => cell.CellId, StringComparer.Ordinal).ToArray();
                observations.Add((selection.ResolveOrder(table.TableId, Order(table.Provenance, table.Ordinal)), row.Key,
                    string.Join("\t", cells.Select(cell => cell.Text.Canonical)),
                    string.Join("\t", cells.Select(cell => cell.Text.Raw)), null));
            }
        return observations.OrderBy(item => item.Order).ThenBy(item => item.SubOrder)
            .Select(item => (item.Text, item.Raw, item.BlockId));

        int Order(CanonicalExtractionProvenance provenance, int fallback)
        {
            if (!provenance.Attributes.TryGetValue("doclingSelfRef", out var sourceRef))
                return int.MaxValue / 2 + fallback; // Unlocated supplements remain explicitly unassigned.
            var index = provenance.Attributes.TryGetValue("doclingProvenanceIndex", out var raw)
                && int.TryParse(raw, out var parsed) ? parsed : 0;
            return DoclingDocumentTraversal.ResolveOrder(order, sourceRef, index);
        }
    }

    private static int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
