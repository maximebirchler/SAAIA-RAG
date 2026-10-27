using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DocumentTitleNavigationProjector
{
    private const int MaxCanonicalNavigationTargetBlocks = 20_000;
    private sealed record CanonicalNavigationTarget(string BlockId, int Page, string Title);

    private static Dictionary<string, List<CanonicalNavigationTarget>>? ReadCanonicalNavigationTargets(CanonicalDocument document,
        CancellationToken ct, CanonicalNavigationAudit audit)
    {
        var targets = new Dictionary<string, List<CanonicalNavigationTarget>>(StringComparer.Ordinal);
        var scanned = 0;
        var cellsScanned = 0;
        var tablesScanned = 0;
        foreach (var page in document.Pages)
        {
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var navigationPage = false;
            foreach (var table in page.Tables)
            {
                ct.ThrowIfCancellationRequested();
                if (++tablesScanned > MaxCanonicalNavigationTables || table.Cells.Count > MaxCanonicalNavigationCells - cellsScanned)
                { audit.Note("target_table_or_cell_limit"); audit.Limit = true; return null; }
                cellsScanned += table.Cells.Count;
                navigationPage |= table.Provenance.Attributes.GetValueOrDefault("doclingLabel") == "document_index";
                foreach (var cell in table.Cells) sourceIds.UnionWith(cell.BlockIds);
                sourceIds.UnionWith(table.CaptionBlockIds);
            }
            foreach (var block in page.Blocks)
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > MaxCanonicalNavigationTargetBlocks)
                { audit.Note("target_block_limit"); audit.Limit = true; return null; }
                if (navigationPage || block.IsRepeatedFurniture || sourceIds.Contains(block.BlockId)
                    || CanonicalObservationSelector.IsNativeObservation(block)
                    || block.BlockType != "heading" && block.BlockType != "section_header") continue;
                if (block.Provenance.Attributes.TryGetValue("doclingLabel", out var label)
                    && (label is "page_header" or "page_footer" or "document_index")) continue;
                if (block.Text.Raw.Length > MaxCanonicalRowCharacters)
                { audit.Note("target_title_character_limit"); audit.Limit = true; return null; }
                var title = CanonicalNavigationWhitespace(block.Text.Raw);
                if (title.Length == 0) continue;
                if (!targets.TryGetValue(title, out var candidates)) targets.Add(title, candidates = []);
                // Two witnesses suffice to prove ambiguity, but the scan continues so an
                // exhausted inventory cannot certify any apparently unique earlier title.
                if (candidates.Count < 2) candidates.Add(new(block.BlockId, page.PageNumber, title));
            }
        }
        return targets;
    }
}
