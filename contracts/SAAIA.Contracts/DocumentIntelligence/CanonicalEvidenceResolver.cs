namespace SAAIA.Contracts.DocumentIntelligence;

public static class CanonicalEvidenceResolver
{
    public static IReadOnlyList<ResolvedSourceEvidence> Resolve(
        SourceAnchor anchor,
        CanonicalDocument document)
    {
        CanonicalContractValidator.ValidateOrThrow(anchor, document);

        return anchor.Regions
            .Select(region =>
            {
                var page = document.Pages.Single(item => item.PageNumber == region.PageNumber);
                return ResolveRegion(region, page);
            })
            .ToArray();
    }

    internal static ResolvedSourceEvidence ResolveRegion(
        SourceAnchorRegion region,
        CanonicalPage page)
    {
        if (region.TableRegionObservationId is not null || region.TableRegionObservationLineIds is not null)
        {
            var observations = page.TableRegionObservations?.Observations?
                .Where(item => item.ObservationId == region.TableRegionObservationId).ToArray() ?? [];
            var requested = (region.TableRegionObservationLineIds ?? []).ToHashSet(StringComparer.Ordinal);
            // Validation reports broken/mixed references. Never substitute primary
            // blocks or another observation for a missing secondary reference.
            var lines = observations.Length == 1
                ? observations[0].Lines.Where(line => requested.Contains(line.LineId)).OrderBy(line => line.RawOrdinal).ToArray()
                : [];
            var literal = string.Join("\n", lines.Select(line => line.Text));
            return new() { PageNumber = region.PageNumber, RawText = literal, CanonicalText = literal, DisplayText = literal };
        }
        var requestedBlocks = region.BlockIds.ToHashSet(StringComparer.Ordinal);
        var requestedSpans = region.SpanIds.ToHashSet(StringComparer.Ordinal);
        var requestedCells = region.TableCellIds.ToHashSet(StringComparer.Ordinal);
        var texts = new List<CanonicalTextVariants>();

        foreach (var block in page.Blocks.OrderBy(item => item.ReadingOrder).ThenBy(item => item.Ordinal))
        {
            if (requestedBlocks.Contains(block.BlockId))
                texts.Add(block.Text);

            foreach (var span in block.Spans.OrderBy(item => item.Ordinal))
            {
                if (requestedSpans.Contains(span.SpanId))
                    texts.Add(span.Text);
            }
        }

        foreach (var table in page.Tables.OrderBy(item => item.Ordinal))
        {
            foreach (var cell in table.Cells.OrderBy(item => item.RowIndex).ThenBy(item => item.ColumnIndex))
            {
                if (requestedCells.Contains(cell.CellId))
                    texts.Add(cell.Text);
            }
        }

        return new()
        {
            PageNumber = region.PageNumber,
            RawText = Join(texts.Select(item => item.Raw)),
            CanonicalText = Join(texts.Select(item => item.Canonical)),
            DisplayText = Join(texts.Select(item => item.Display))
        };
    }

    private static string Join(IEnumerable<string> values)
        => string.Join(
            "\n",
            values.Where(value => !string.IsNullOrWhiteSpace(value)));
}
