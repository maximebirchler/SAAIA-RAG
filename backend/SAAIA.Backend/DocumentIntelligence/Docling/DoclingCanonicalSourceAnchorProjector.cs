using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Backend;

internal sealed record DoclingCanonicalAnchorSelection(
    string ProjectionId, string ProjectionType, int PageStart, int PageEnd,
    IReadOnlyList<string>? BlockIds, IReadOnlyList<string>? SpanIds, IReadOnlyList<string>? TableCellIds);

internal static class DoclingCanonicalSourceAnchorProjector
{
    // Existing single-projection entry points remain fully validating.
    public static SourceAnchor ProjectPageRange(CanonicalDocument document, string projectionId,
        string projectionType, int pageStart, int pageEnd)
        => ProjectPageRangeCore(document, projectionId, projectionType, pageStart, pageEnd, validateAnchor: true);

    public static SourceAnchor ProjectSelection(CanonicalDocument document, string projectionId,
        string projectionType, int pageStart, int pageEnd, IReadOnlyList<string>? blockIds,
        IReadOnlyList<string>? spanIds, IReadOnlyList<string>? tableCellIds)
        => ProjectSelectionCore(document, projectionId, projectionType, pageStart, pageEnd,
            blockIds, spanIds, tableCellIds, validateAnchor: true);

    public static IReadOnlyList<SourceAnchor> ProjectSelections(CanonicalDocument document,
        IReadOnlyList<DoclingCanonicalAnchorSelection> selections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selections);
        var materialized = selections.ToArray();
        using var probe = BackendDiagnostics.Start("ingestion.canonical_anchor_batch");
        probe.Set("requested_anchor_count", materialized.Length);
        probe.Set("document_validation_scope", "projection_batch_excludes_final_bundle");
        probe.Set("document_validation_attempts", 0);
        probe.Set("anchor_validation_completed_count", null);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Prevalidate before constructing any anchor or resolving references.
            probe.Set("document_validation_attempts", 1);
            CanonicalContractValidator.ValidateOrThrow(document);
            var anchors = new List<SourceAnchor>(materialized.Length);
            try
            {
                foreach (var selection in materialized)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(selection);
                    anchors.Add(ProjectSelectionCore(document, selection.ProjectionId, selection.ProjectionType,
                        selection.PageStart, selection.PageEnd, selection.BlockIds, selection.SpanIds,
                        selection.TableCellIds, validateAnchor: false));
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                // The old loop validated each earlier anchor immediately. Do not
                // hide an invalid prefix behind a later construction/cancellation.
                probe.Set("document_validation_attempts", 2);
                CanonicalContractValidator.ValidateAnchorsOrThrow(document, anchors);
                throw;
            }
            // A self-validating Contracts API, no reusable certificate or public skip.
            probe.Set("document_validation_attempts", 2);
            CanonicalContractValidator.ValidateAnchorsOrThrow(document, anchors);
            probe.Set("anchor_validation_completed_count", anchors.Count);
            cancellationToken.ThrowIfCancellationRequested();
            probe.Complete();
            return anchors;
        }
        catch (Exception error)
        {
            probe.Fail(error);
            throw;
        }
    }

    private static SourceAnchor ProjectPageRangeCore(
        CanonicalDocument document,
        string projectionId,
        string projectionType,
        int pageStart,
        int pageEnd,
        bool validateAnchor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        if (pageStart <= 0 || pageEnd < pageStart)
            throw new ArgumentOutOfRangeException(nameof(pageStart), "Page range is invalid.");

        var regions = document.Pages
            .Where(page => page.PageNumber >= pageStart && page.PageNumber <= pageEnd)
            .OrderBy(static page => page.PageNumber)
            .Select(ProjectRegion)
            .ToList();
        var precision = regions.Any(static region =>
            region.TableCellIds.Count > 0)
            ? "page_block_and_table_cell"
            : "page_block";
        return CreateAnchor(
            document,
            projectionId,
            projectionType,
            precision,
            regions,
            validateAnchor);
    }

    private static SourceAnchor ProjectSelectionCore(
        CanonicalDocument document,
        string projectionId,
        string projectionType,
        int pageStart,
        int pageEnd,
        IReadOnlyList<string>? blockIds,
        IReadOnlyList<string>? spanIds,
        IReadOnlyList<string>? tableCellIds,
        bool validateAnchor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        if (pageStart <= 0 || pageEnd < pageStart)
            throw new ArgumentOutOfRangeException(
                nameof(pageStart),
                "Page range is invalid.");

        var requestedBlocks = (blockIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var requestedSpans = (spanIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var requestedCells = (tableCellIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (requestedBlocks.Count == 0
            && requestedSpans.Count == 0
            && requestedCells.Count == 0)
        {
            return ProjectPageRangeCore(
                document,
                projectionId,
                projectionType,
                pageStart,
                pageEnd,
                validateAnchor);
        }

        var regions = new List<SourceAnchorRegion>();
        var foundBlocks = new HashSet<string>(StringComparer.Ordinal);
        var foundSpans = new HashSet<string>(StringComparer.Ordinal);
        var foundCells = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in document.Pages
                     .Where(page =>
                         page.PageNumber >= pageStart
                         && page.PageNumber <= pageEnd)
                     .OrderBy(static page => page.PageNumber))
        {
            var blocks = page.Blocks
                .Where(block =>
                    requestedBlocks.Contains(block.BlockId)
                    || block.Spans.Any(span =>
                        requestedSpans.Contains(span.SpanId)))
                .OrderBy(static block => block.ReadingOrder)
                .ThenBy(static block => block.Ordinal)
                .ToArray();
            var cells = page.Tables
                .OrderBy(static table => table.Ordinal)
                .SelectMany(static table => table.Cells
                    .OrderBy(static cell => cell.RowIndex)
                    .ThenBy(static cell => cell.ColumnIndex))
                .Where(cell => requestedCells.Contains(cell.CellId))
                .ToArray();
            if (blocks.Length == 0 && cells.Length == 0)
                continue;

            foreach (var block in blocks)
            {
                if (requestedBlocks.Contains(block.BlockId))
                    foundBlocks.Add(block.BlockId);
                foreach (var span in block.Spans.Where(span =>
                             requestedSpans.Contains(span.SpanId)
                             || requestedBlocks.Contains(block.BlockId)))
                {
                    foundSpans.Add(span.SpanId);
                }
            }
            foreach (var cell in cells)
                foundCells.Add(cell.CellId);

            regions.Add(ProjectRegion(
                page,
                blocks,
                cells,
                requestedSpans));
        }

        ThrowIfReferencesMissing(
            "block",
            requestedBlocks,
            foundBlocks);
        ThrowIfReferencesMissing(
            "span",
            requestedSpans,
            foundSpans);
        ThrowIfReferencesMissing(
            "table cell",
            requestedCells,
            foundCells);

        var precision = requestedCells.Count > 0
            ? requestedBlocks.Count > 0 || requestedSpans.Count > 0
                ? "block_and_table_cell"
                : "table_cell"
            : requestedSpans.Count > 0
                ? "block_span"
                : "block";
        return CreateAnchor(
            document,
            projectionId,
            projectionType,
            precision,
            regions,
            validateAnchor);
    }

    private static SourceAnchor CreateAnchor(
        CanonicalDocument document,
        string projectionId,
        string projectionType,
        string precision,
        List<SourceAnchorRegion> regions,
        bool validateAnchor)
    {
        var anchor = new SourceAnchor
        {
            AnchorId = CanonicalStableId.Create(
                "anchor",
                document.Source.Sha256,
                document.RevisionId.ToString("D"),
                projectionType,
                projectionId),
            DocumentId = document.DocumentId,
            RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256,
            ManifestSha256 = document.ManifestSha256,
            ProjectionId = projectionId,
            ProjectionType = projectionType,
            Precision = precision,
            Regions = regions
        };

        if (validateAnchor)
            CanonicalContractValidator.ValidateOrThrow(anchor, document);
        return anchor;
    }

    private static SourceAnchorRegion ProjectRegion(CanonicalPage page)
    {
        var blocks = page.Blocks
            .OrderBy(static block => block.ReadingOrder)
            .ThenBy(static block => block.Ordinal)
            .ToArray();
        var cells = page.Tables
            .OrderBy(static table => table.Ordinal)
            .SelectMany(static table => table.Cells
                .OrderBy(static cell => cell.RowIndex)
                .ThenBy(static cell => cell.ColumnIndex))
            .ToArray();
        return ProjectRegion(
            page,
            blocks,
            cells,
            requestedSpans: null);
    }

    private static SourceAnchorRegion ProjectRegion(
        CanonicalPage page,
        IReadOnlyList<CanonicalBlock> blocks,
        IReadOnlyList<CanonicalTableCell> cells,
        IReadOnlySet<string>? requestedSpans)
    {
        var rawText = Join(
            blocks.Select(static block => block.Text.Raw)
                .Concat(cells.Select(static cell => cell.Text.Raw)));
        return new()
        {
            PageNumber = page.PageNumber,
            BlockIds = blocks.Select(static block => block.BlockId).ToList(),
            SpanIds = blocks
                .SelectMany(static block => block.Spans)
                .Where(span =>
                    requestedSpans is null
                    || requestedSpans.Count == 0
                    || requestedSpans.Contains(span.SpanId))
                .Select(static span => span.SpanId)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            TableCellIds = cells.Select(static cell => cell.CellId).ToList(),
            Polygon = MergePolygons(
                blocks.Select(static block => block.Polygon)
                    .Concat(cells.Select(static cell => cell.Polygon))),
            RawTextSha256 = Sha256(rawText)
        };
    }

    private static void ThrowIfReferencesMissing(
        string referenceType,
        IReadOnlySet<string> requested,
        IReadOnlySet<string> found)
    {
        var missing = requested
            .Where(value => !found.Contains(value))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Canonical {referenceType} references cannot be resolved: "
                + string.Join(", ", missing));
        }
    }

    private static CanonicalPolygon? MergePolygons(
        IEnumerable<CanonicalPolygon?> polygons)
    {
        var points = polygons
            .Where(static polygon => polygon is not null)
            .SelectMany(static polygon => polygon!.Points)
            .ToArray();
        if (points.Length == 0)
            return null;

        var left = points.Min(static point => point.X);
        var right = points.Max(static point => point.X);
        var top = points.Min(static point => point.Y);
        var bottom = points.Max(static point => point.Y);
        return new()
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

    private static string Join(IEnumerable<string> values)
        => string.Join(
            "\n",
            values.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
