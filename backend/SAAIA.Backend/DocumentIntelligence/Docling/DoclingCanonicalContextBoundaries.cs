using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Physical and navigation boundaries constrain every inferred section view.</summary>
internal static class DoclingCanonicalContextBoundaries
{
    internal static HashSet<int> Find(IEnumerable<CanonicalPage> pages,
        IReadOnlyDictionary<string, DoclingContentItem> sourceItems, CancellationToken cancellationToken)
    {
        using var probe = BackendDiagnostics.Start("ingestion.canonical_section_boundaries");
        var boundaries = new HashSet<int>();
        int? previousPage = null;
        var previousNavigationOnly = false;
        var gaps = 0;
        var navigationExits = 0;
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observations = CanonicalObservationSelector.SelectPage(page);
            var blocks = observations.Blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text.Retrieval)
                && !CanonicalSourceAssociationPolicy.IsProvenPageCounter(block, page.PageNumber)).ToArray();
            var tables = observations.Tables.Where(table => table.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell.Text.Retrieval))).ToArray();
            if (blocks.Length == 0 && tables.Length == 0) continue;
            var content = blocks.Where(block => block.BlockType is not ("section_header" or "title"))
                .Select(block => block.Provenance).Concat(tables.Select(table => table.Provenance)).ToArray();
            var navigationOnly = content.Length > 0 && content.All(IsNavigation);
            var gap = previousPage.HasValue && page.PageNumber > previousPage.Value + 1;
            var leavingNavigation = previousNavigationOnly && !navigationOnly;
            if (gap || leavingNavigation) boundaries.Add(page.PageNumber);
            if (gap) gaps++;
            if (leavingNavigation) navigationExits++;
            previousPage = page.PageNumber;
            previousNavigationOnly = navigationOnly;
        }
        probe.Set("physical_gap_count", gaps);
        probe.Set("navigation_exit_count", navigationExits);
        probe.Set("section_boundary_count", boundaries.Count);
        probe.Complete();
        return boundaries;

        bool IsNavigation(CanonicalExtractionProvenance provenance)
            => provenance.Attributes.TryGetValue("doclingSelfRef", out var reference)
               && sourceItems.TryGetValue(reference, out var item)
               && DoclingDocumentTraversal.IsNavigationItem(item, sourceItems);
    }
}
