using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record DoclingCanonicalProjectionContext(
    Guid DocumentId,
    Guid RevisionId,
    string SourceSha256,
    long SourceSizeBytes,
    string SourceDisplayName,
    string ManifestSha256,
    string StageId,
    string EngineVersion,
    string DeploymentRevision,
    double? Confidence = null);

internal static class DoclingCanonicalDocumentAdapter
{
    private sealed record BlockProjection(
        string SourceRef,
        string Label,
        int Level,
        int PageNumber,
        int GlobalOrder,
        CanonicalBlock Block);

    public static CanonicalDocument Project(
        DoclingCanonicalProjectionContext context,
        DoclingDocument source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ValidateContext(context);

        var sourceItems = DoclingDocumentTraversal.BuildItemMap(source);
        var orderByRef = DoclingDocumentTraversal.BuildReadingOrder(source, sourceItems);
        var pages = BuildPages(source);
        var blocks = ProjectBlocks(context, source, pages, orderByRef);
        var blockIdsBySourceRef = blocks
            .GroupBy(static item => item.SourceRef, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(item => item.Block.BlockId).ToArray(),
                StringComparer.Ordinal);

        foreach (var pageGroup in blocks.GroupBy(static item => item.PageNumber))
        {
            var page = RequirePage(pages, pageGroup.Key);
            var ordinal = 0;
            foreach (var projection in pageGroup
                         .OrderBy(static item => item.GlobalOrder)
                         .ThenBy(static item => item.Block.BlockId, StringComparer.Ordinal))
            {
                projection.Block.Ordinal = ordinal;
                projection.Block.ReadingOrder = ordinal;
                page.Blocks.Add(projection.Block);
                ordinal++;
            }
        }

        CanonicalRepeatedPageFurnitureClassifier.Apply(pages.Values);

        var relations = new List<CanonicalRelation>();
        ProjectTables(context, source, pages, blockIdsBySourceRef, relations);
        ProjectFigures(context, source, pages, blockIdsBySourceRef, relations);
        var sections = BuildSections(context.SourceSha256, blocks);

        var document = new CanonicalDocument
        {
            DocumentId = context.DocumentId,
            RevisionId = context.RevisionId,
            Source = new()
            {
                Sha256 = context.SourceSha256,
                MediaType = "application/pdf",
                SizeBytes = context.SourceSizeBytes,
                DisplayName = context.SourceDisplayName
            },
            PageCount = pages.Count,
            Pages = pages.Values.OrderBy(static page => page.PageNumber).ToList(),
            SectionTree = sections,
            Relations = relations,
            ManifestSha256 = context.ManifestSha256
        };

        CanonicalContractValidator.ValidateOrThrow(document);
        return document;
    }

    private static SortedDictionary<int, CanonicalPage> BuildPages(DoclingDocument source)
    {
        var pages = new SortedDictionary<int, CanonicalPage>();
        foreach (var pair in source.Pages)
        {
            var pageNumber = pair.Value.PageNumber;
            if (pageNumber <= 0 && !int.TryParse(pair.Key, out pageNumber))
                throw new InvalidDataException($"Docling page key '{pair.Key}' is invalid.");
            if (pageNumber <= 0)
                throw new InvalidDataException("Docling page numbers must be positive.");
            if (pages.ContainsKey(pageNumber))
                throw new InvalidDataException($"Docling page {pageNumber} is duplicated.");
            if (!double.IsFinite(pair.Value.Size.Width)
                || !double.IsFinite(pair.Value.Size.Height)
                || pair.Value.Size.Width <= 0
                || pair.Value.Size.Height <= 0)
            {
                throw new InvalidDataException($"Docling page {pageNumber} has invalid geometry.");
            }

            pages.Add(pageNumber, new CanonicalPage
            {
                PageNumber = pageNumber,
                Geometry = new()
                {
                    Width = pair.Value.Size.Width,
                    Height = pair.Value.Size.Height,
                    Unit = "pt"
                },
                QualityFlags = ["document_intelligence_docling"]
            });
        }

        if (pages.Count == 0)
            throw new InvalidDataException("Docling returned no pages.");
        return pages;
    }

    private static List<BlockProjection> ProjectBlocks(
        DoclingCanonicalProjectionContext context,
        DoclingDocument source,
        IReadOnlyDictionary<int, CanonicalPage> pages,
        IReadOnlyDictionary<string, int> orderByRef)
    {
        var projections = new List<BlockProjection>();
        foreach (var item in source.Texts)
        {
            for (var provenanceIndex = 0; provenanceIndex < item.Provenance.Count; provenanceIndex++)
            {
                var sourceProvenance = item.Provenance[provenanceIndex];
                var page = RequirePage(pages, sourceProvenance.PageNumber);
                var (rawText, canonicalText) = ProjectTextRegion(
                    item,
                    sourceProvenance,
                    provenanceIndex);
                var blockId = CanonicalStableId.Create(
                    "block",
                    context.SourceSha256,
                    item.SelfRef,
                    sourceProvenance.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    provenanceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Sha256(rawText));
                var qualityFlags = new List<string>();
                var polygon = ToCanonicalPolygon(sourceProvenance.BoundingBox, page.Geometry);
                if (polygon is null)
                    qualityFlags.Add("spatial_anchor_unavailable");
                if (item.Provenance.Count > 1)
                    qualityFlags.Add("multi_region_source_item");

                var block = new CanonicalBlock
                {
                    BlockId = blockId,
                    BlockType = string.IsNullOrWhiteSpace(item.Label) ? "text" : item.Label,
                    Polygon = polygon,
                    Text = BuildTextVariants(rawText, canonicalText),
                    IsRepeatedFurniture =
                        string.Equals(item.ContentLayer, "furniture", StringComparison.OrdinalIgnoreCase),
                    QualityFlags = qualityFlags,
                    Provenance = BuildProvenance(
                        context,
                        item.SelfRef,
                        item.Label,
                        item.ContentLayer,
                        sourceProvenance.BoundingBox,
                        provenanceIndex,
                        sourceProvenance.CharacterSpan)
                };
                projections.Add(new(
                    item.SelfRef,
                    item.Label,
                    Math.Clamp(item.Level ?? 1, 1, 16),
                    sourceProvenance.PageNumber,
                    DoclingDocumentTraversal.ResolveOrder(
                        orderByRef,
                        item.SelfRef,
                        provenanceIndex),
                    block));
            }
        }

        return projections;
    }

    private static void ProjectTables(
        DoclingCanonicalProjectionContext context,
        DoclingDocument source,
        IReadOnlyDictionary<int, CanonicalPage> pages,
        IReadOnlyDictionary<string, string[]> blockIdsBySourceRef,
        List<CanonicalRelation> relations)
    {
        var ordinalByPage = new Dictionary<int, int>();
        foreach (var item in source.Tables)
        {
            if (item.Data.RowCount <= 0 || item.Data.ColumnCount <= 0)
                throw new InvalidDataException($"Docling table '{item.SelfRef}' has invalid dimensions.");
            ValidateTableRepair(item);

            for (var provenanceIndex = 0; provenanceIndex < item.Provenance.Count; provenanceIndex++)
            {
                var sourceProvenance = item.Provenance[provenanceIndex];
                var page = RequirePage(pages, sourceProvenance.PageNumber);
                var ordinal = ordinalByPage.TryGetValue(page.PageNumber, out var current) ? current : 0;
                ordinalByPage[page.PageNumber] = ordinal + 1;
                var tableId = CanonicalStableId.Create(
                    "table",
                    context.SourceSha256,
                    item.SelfRef,
                    page.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    provenanceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var tableProvenance = BuildProvenance(
                    context,
                    item.SelfRef,
                    item.Label,
                    item.ContentLayer,
                    sourceProvenance.BoundingBox,
                    provenanceIndex,
                    sourceProvenance.CharacterSpan);
                AddTableRepairProvenance(tableProvenance, item.TableRepair);
                var table = new CanonicalTable
                {
                    TableId = tableId,
                    Ordinal = ordinal,
                    Polygon = ToCanonicalPolygon(sourceProvenance.BoundingBox, page.Geometry),
                    RowCount = item.Data.RowCount,
                    ColumnCount = item.Data.ColumnCount,
                    CaptionBlockIds = ResolveBlockIds(item.Captions, blockIdsBySourceRef).ToList(),
                    Provenance = tableProvenance
                };

                for (var cellIndex = 0; cellIndex < item.Data.Cells.Count; cellIndex++)
                {
                    var cell = item.Data.Cells[cellIndex];
                    ValidateCell(item, cell, cellIndex);
                    var rawText = cell.TextRepair is null
                        ? cell.Text
                        : string.IsNullOrEmpty(cell.OriginalText)
                            ? cell.Text
                            : cell.OriginalText;
                    var cellId = CanonicalStableId.Create(
                        "cell",
                        context.SourceSha256,
                        item.SelfRef,
                        cell.RowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        cell.ColumnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        cellIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Sha256(rawText),
                        Sha256(cell.Text));
                    table.Cells.Add(new()
                    {
                        CellId = cellId,
                        RowIndex = cell.RowIndex,
                        ColumnIndex = cell.ColumnIndex,
                        RowSpan = cell.RowSpan,
                        ColumnSpan = cell.ColumnSpan,
                        CellRole = ResolveCellRole(cell),
                        Polygon = ToCanonicalPolygon(cell.BoundingBox, page.Geometry),
                        Text = BuildTextVariants(rawText, cell.Text)
                    });
                }

                if (item.TableRepair is not null)
                    AddQualityFlag(page.QualityFlags, "canonical_table_regional_ocr_repaired");
                AddTableGridQualityFlags(page, table);
                page.Tables.Add(table);
                AddCaptionRelations(
                    context.SourceSha256,
                    table.CaptionBlockIds,
                    table.TableId,
                    relations);
            }
        }
    }

    private static void ProjectFigures(
        DoclingCanonicalProjectionContext context,
        DoclingDocument source,
        IReadOnlyDictionary<int, CanonicalPage> pages,
        IReadOnlyDictionary<string, string[]> blockIdsBySourceRef,
        List<CanonicalRelation> relations)
    {
        var ordinalByPage = new Dictionary<int, int>();
        foreach (var item in source.Pictures)
        {
            for (var provenanceIndex = 0; provenanceIndex < item.Provenance.Count; provenanceIndex++)
            {
                var sourceProvenance = item.Provenance[provenanceIndex];
                var page = RequirePage(pages, sourceProvenance.PageNumber);
                var ordinal = ordinalByPage.TryGetValue(page.PageNumber, out var current) ? current : 0;
                ordinalByPage[page.PageNumber] = ordinal + 1;
                var figureId = CanonicalStableId.Create(
                    "figure",
                    context.SourceSha256,
                    item.SelfRef,
                    page.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    provenanceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var captionIds = ResolveBlockIds(item.Captions, blockIdsBySourceRef);
                page.Figures.Add(new()
                {
                    FigureId = figureId,
                    FigureType = string.IsNullOrWhiteSpace(item.Label) ? "picture" : item.Label,
                    Ordinal = ordinal,
                    Polygon = ToCanonicalPolygon(sourceProvenance.BoundingBox, page.Geometry),
                    CaptionBlockIds = captionIds.ToList(),
                    Provenance = BuildProvenance(
                        context,
                        item.SelfRef,
                        item.Label,
                        item.ContentLayer,
                        sourceProvenance.BoundingBox,
                        provenanceIndex,
                        sourceProvenance.CharacterSpan)
                });
                AddCaptionRelations(context.SourceSha256, captionIds, figureId, relations);
            }
        }
    }

    private static List<CanonicalSectionNode> BuildSections(
        string sourceSha256,
        IReadOnlyList<BlockProjection> blocks)
    {
        var states = new List<(CanonicalSectionNode Node, int Level)>();
        var active = new Stack<(CanonicalSectionNode Node, int Level)>();
        foreach (var projection in blocks
                     .OrderBy(static item => item.PageNumber)
                     .ThenBy(static item => item.GlobalOrder)
                     .ThenBy(static item => item.Block.ReadingOrder))
        {
            if (string.Equals(projection.Label, "section_header", StringComparison.Ordinal))
            {
                while (active.Count > 0 && active.Peek().Level >= projection.Level)
                    active.Pop();

                var section = new CanonicalSectionNode
                {
                    SectionId = CanonicalStableId.Create(
                        "section",
                        sourceSha256,
                        projection.Block.BlockId),
                    ParentSectionId = active.Count == 0 ? null : active.Peek().Node.SectionId,
                    Ordinal = states.Count,
                    Level = projection.Level,
                    TitleBlockIds = [projection.Block.BlockId],
                    PageStart = projection.PageNumber,
                    PageEnd = projection.PageNumber
                };
                var state = (section, projection.Level);
                states.Add(state);
                active.Push(state);
                continue;
            }

            if (projection.Block.IsRepeatedFurniture)
                continue;
            if (active.Count == 0)
                continue;
            var current = active.Peek().Node;
            current.ContentBlockIds.Add(projection.Block.BlockId);
            current.PageEnd = Math.Max(current.PageEnd, projection.PageNumber);
        }

        return states.Select(static state => state.Node).ToList();
    }

    private static CanonicalExtractionProvenance BuildProvenance(
        DoclingCanonicalProjectionContext context,
        string sourceRef,
        string label,
        string contentLayer,
        DoclingBoundingBox? boundingBox,
        int provenanceIndex,
        IReadOnlyList<int>? characterSpan = null)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doclingSelfRef"] = sourceRef,
            ["doclingLabel"] = label ?? "",
            ["contentLayer"] = contentLayer ?? "",
            ["doclingProvenanceIndex"] = provenanceIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["deploymentRevision"] = context.DeploymentRevision
        };
        if (!string.IsNullOrWhiteSpace(boundingBox?.CoordinateOrigin))
            attributes["sourceCoordinateOrigin"] = boundingBox.CoordinateOrigin;
        if (characterSpan is { Count: 2 })
        {
            attributes["doclingCharSpanStart"] = characterSpan[0].ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            attributes["doclingCharSpanEndExclusive"] = characterSpan[1].ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return new()
        {
            StageId = context.StageId,
            Method = "document_intelligence",
            Engine = "docling",
            EngineVersion = context.EngineVersion,
            Confidence = NormalizeConfidence(context.Confidence),
            Attributes = attributes
        };
    }

    private static (string Raw, string Canonical) ProjectTextRegion(
        DoclingTextItem item,
        DoclingProvenance provenance,
        int provenanceIndex)
    {
        if (item.Provenance.Count <= 1)
        {
            var singleRegionRaw = string.IsNullOrEmpty(item.OriginalText)
                ? item.Text ?? ""
                : item.OriginalText;
            return (singleRegionRaw, item.Text ?? "");
        }

        if (provenance.CharacterSpan is not { Length: 2 })
        {
            throw new InvalidDataException(
                $"Docling text '{item.SelfRef}' provenance {provenanceIndex} "
                + "does not contain an exact two-bound character span.");
        }

        var start = provenance.CharacterSpan[0];
        var endExclusive = provenance.CharacterSpan[1];
        var fragment = SliceByUnicodeScalarRange(
            item.OriginalText ?? "",
            start,
            endExclusive,
            item.SelfRef,
            provenanceIndex);

        // Docling defines multi-provenance charspans against `orig`, not `text`.
        // Its own serializer therefore uses the exact `orig` slice as both values.
        return (fragment, fragment);
    }

    private static string SliceByUnicodeScalarRange(
        string value,
        int start,
        int endExclusive,
        string sourceRef,
        int provenanceIndex)
    {
        if (start < 0 || endExclusive < start)
        {
            throw InvalidCharacterSpan(
                sourceRef,
                provenanceIndex,
                start,
                endExclusive,
                value.EnumerateRunes().Count());
        }

        var scalarIndex = 0;
        var utf16Index = 0;
        int? utf16Start = start == 0 ? 0 : null;
        int? utf16End = endExclusive == 0 ? 0 : null;
        foreach (var rune in value.EnumerateRunes())
        {
            if (scalarIndex == start)
                utf16Start = utf16Index;
            if (scalarIndex == endExclusive)
            {
                utf16End = utf16Index;
                break;
            }

            utf16Index += rune.Utf16SequenceLength;
            scalarIndex++;
        }

        if (scalarIndex == start)
            utf16Start ??= utf16Index;
        if (scalarIndex == endExclusive)
            utf16End ??= utf16Index;
        if (!utf16Start.HasValue || !utf16End.HasValue)
        {
            throw InvalidCharacterSpan(
                sourceRef,
                provenanceIndex,
                start,
                endExclusive,
                scalarIndex);
        }

        return value.Substring(
            utf16Start.Value,
            utf16End.Value - utf16Start.Value);
    }

    private static InvalidDataException InvalidCharacterSpan(
        string sourceRef,
        int provenanceIndex,
        int start,
        int endExclusive,
        int scalarLength)
        => new(
            $"Docling text '{sourceRef}' provenance {provenanceIndex} "
            + $"contains invalid character span [{start}, {endExclusive}) "
            + $"for original text length {scalarLength}.");

    private static CanonicalTextVariants BuildTextVariants(string? rawText, string? canonicalText)
    {
        var raw = rawText ?? "";
        var canonical = canonicalText ?? "";
        return new()
        {
            Raw = raw,
            Canonical = canonical,
            Normalized = Normalize(canonical),
            Retrieval = canonical,
            Display = canonical,
            RawSha256 = Sha256(raw)
        };
    }

    private static CanonicalPolygon? ToCanonicalPolygon(
        DoclingBoundingBox? box,
        CanonicalPageGeometry geometry)
    {
        if (box is null || geometry.Width is not > 0 || geometry.Height is not > 0)
            return null;
        if (!double.IsFinite(box.Left)
            || !double.IsFinite(box.Right)
            || !double.IsFinite(box.Top)
            || !double.IsFinite(box.Bottom))
        {
            return null;
        }

        var left = Math.Min(box.Left, box.Right);
        var right = Math.Max(box.Left, box.Right);
        var sourceTop = Math.Min(box.Top, box.Bottom);
        var sourceBottom = Math.Max(box.Top, box.Bottom);
        double top;
        double bottom;
        if (string.Equals(box.CoordinateOrigin, "BOTTOMLEFT", StringComparison.OrdinalIgnoreCase))
        {
            top = geometry.Height.Value - sourceBottom;
            bottom = geometry.Height.Value - sourceTop;
        }
        else if (string.Equals(box.CoordinateOrigin, "TOPLEFT", StringComparison.OrdinalIgnoreCase))
        {
            top = sourceTop;
            bottom = sourceBottom;
        }
        else
        {
            return null;
        }

        var x1 = Clamp01(left / geometry.Width.Value);
        var x2 = Clamp01(right / geometry.Width.Value);
        var y1 = Clamp01(top / geometry.Height.Value);
        var y2 = Clamp01(bottom / geometry.Height.Value);
        return new()
        {
            Points =
            [
                new() { X = x1, Y = y1 },
                new() { X = x2, Y = y1 },
                new() { X = x2, Y = y2 },
                new() { X = x1, Y = y2 }
            ]
        };
    }

    private static CanonicalPage RequirePage(
        IReadOnlyDictionary<int, CanonicalPage> pages,
        int pageNumber)
        => pages.TryGetValue(pageNumber, out var page)
            ? page
            : throw new InvalidDataException(
                $"Docling provenance references missing page {pageNumber}.");

    private static string[] ResolveBlockIds(
        IEnumerable<DoclingReference> references,
        IReadOnlyDictionary<string, string[]> blockIdsBySourceRef)
        => references
            .SelectMany(reference =>
                blockIdsBySourceRef.TryGetValue(reference.Ref, out var blockIds)
                    ? blockIds
                    : throw new InvalidDataException(
                        $"Docling caption reference '{reference.Ref}' cannot be resolved."))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void AddCaptionRelations(
        string sourceSha256,
        IEnumerable<string> captionIds,
        string targetId,
        ICollection<CanonicalRelation> relations)
    {
        foreach (var captionId in captionIds)
        {
            relations.Add(new()
            {
                RelationId = CanonicalStableId.Create(
                    "relation",
                    sourceSha256,
                    "caption_for",
                    captionId,
                    targetId),
                RelationType = "caption_for",
                SourceId = captionId,
                TargetId = targetId,
                Provenance = new()
                {
                    Method = "document_intelligence",
                    Engine = "docling"
                }
            });
        }
    }

    private static void ValidateCell(
        DoclingTableItem table,
        DoclingTableCell cell,
        int cellIndex)
    {
        if (cell.RowIndex < 0
            || cell.ColumnIndex < 0
            || cell.RowSpan <= 0
            || cell.ColumnSpan <= 0
            || cell.RowIndex + cell.RowSpan > table.Data.RowCount
            || cell.ColumnIndex + cell.ColumnSpan > table.Data.ColumnCount)
        {
            throw new InvalidDataException(
                $"Docling table '{table.SelfRef}' cell {cellIndex} exceeds its declared dimensions.");
        }
    }

    private static void ValidateTableRepair(DoclingTableItem table)
    {
        var repair = table.TableRepair;
        if (repair is null)
            return;

        if (string.IsNullOrWhiteSpace(repair.SchemaVersion)
            || string.IsNullOrWhiteSpace(repair.Engine)
            || string.IsNullOrWhiteSpace(repair.EngineVersion)
            || string.IsNullOrWhiteSpace(repair.ModelId)
            || string.IsNullOrWhiteSpace(repair.Reason)
            || repair.OriginalCellCount <= 0
            || repair.FinalCellCount != table.Data.Cells.Count
            || repair.RepairedCellCount < 0
            || repair.AddedCellCount <= 0
            || repair.OcrLineCount <= 0
            || repair.DurationMs < 0
            || NormalizeConfidence(repair.MeanConfidence) is null)
        {
            throw new InvalidDataException(
                $"Docling table '{table.SelfRef}' contains invalid regional OCR repair metadata.");
        }

        var repairedCells = table.Data.Cells
            .Where(static cell => cell.TextRepair is not null)
            .ToArray();
        var addedCells = repairedCells.Count(static cell =>
            string.Equals(
                cell.TextRepair!.Action,
                "add_missing_cell",
                StringComparison.Ordinal));
        var splitCells = repairedCells.Count(static cell =>
            string.Equals(
                cell.TextRepair!.Action,
                "split_absorbed_text",
                StringComparison.Ordinal));
        if (repairedCells.Length != repair.RepairedCellCount + repair.AddedCellCount
            || addedCells != repair.AddedCellCount
            || splitCells != repair.RepairedCellCount
            || repairedCells.Any(static cell =>
                cell.OriginalText is null
                || string.IsNullOrWhiteSpace(cell.TextRepair!.SchemaVersion)
                || string.IsNullOrWhiteSpace(cell.TextRepair.Engine)
                || string.IsNullOrWhiteSpace(cell.TextRepair.EngineVersion)
                || string.IsNullOrWhiteSpace(cell.TextRepair.ModelId)
                || NormalizeConfidence(cell.TextRepair.Confidence) is null))
        {
            throw new InvalidDataException(
                $"Docling table '{table.SelfRef}' contains inconsistent repaired-cell provenance.");
        }
    }

    private static void AddTableGridQualityFlags(
        CanonicalPage page,
        CanonicalTable table)
    {
        var occupancy = new int[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
        {
            for (var row = cell.RowIndex; row < cell.RowIndex + cell.RowSpan; row++)
            {
                for (var column = cell.ColumnIndex;
                     column < cell.ColumnIndex + cell.ColumnSpan;
                     column++)
                {
                    occupancy[row, column]++;
                }
            }
        }

        var sparse = false;
        var overlap = false;
        for (var row = 0; row < table.RowCount; row++)
        {
            for (var column = 0; column < table.ColumnCount; column++)
            {
                sparse |= occupancy[row, column] == 0;
                overlap |= occupancy[row, column] > 1;
            }
        }

        if (sparse)
            AddQualityFlag(page.QualityFlags, "canonical_table_sparse_grid");
        if (overlap)
            AddQualityFlag(page.QualityFlags, "canonical_table_overlapping_grid");
    }

    private static void AddTableRepairProvenance(
        CanonicalExtractionProvenance provenance,
        DoclingTableRepair? repair)
    {
        if (repair is null)
            return;

        provenance.Attributes["regionalTableRepairSchema"] = repair.SchemaVersion;
        provenance.Attributes["regionalTableRepairEngine"] = repair.Engine;
        provenance.Attributes["regionalTableRepairEngineVersion"] = repair.EngineVersion;
        provenance.Attributes["regionalTableRepairModelId"] = repair.ModelId;
        provenance.Attributes["regionalTableRepairReason"] = repair.Reason;
        provenance.Attributes["regionalTableRepairOriginalCellCount"] =
            repair.OriginalCellCount.ToString(CultureInfo.InvariantCulture);
        provenance.Attributes["regionalTableRepairFinalCellCount"] =
            repair.FinalCellCount.ToString(CultureInfo.InvariantCulture);
        provenance.Attributes["regionalTableRepairRepairedCellCount"] =
            repair.RepairedCellCount.ToString(CultureInfo.InvariantCulture);
        provenance.Attributes["regionalTableRepairAddedCellCount"] =
            repair.AddedCellCount.ToString(CultureInfo.InvariantCulture);
        provenance.Attributes["regionalTableRepairOcrLineCount"] =
            repair.OcrLineCount.ToString(CultureInfo.InvariantCulture);
        provenance.Attributes["regionalTableRepairDurationMs"] =
            repair.DurationMs.ToString(CultureInfo.InvariantCulture);
        if (NormalizeConfidence(repair.MeanConfidence) is { } confidence)
        {
            provenance.Attributes["regionalTableRepairMeanConfidence"] =
                confidence.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    private static void AddQualityFlag(
        ICollection<string> qualityFlags,
        string value)
    {
        if (!qualityFlags.Contains(value, StringComparer.Ordinal))
            qualityFlags.Add(value);
    }

    private static string ResolveCellRole(DoclingTableCell cell)
    {
        if (cell.IsColumnHeader && cell.IsRowHeader)
            return "column_and_row_header";
        if (cell.IsColumnHeader)
            return "column_header";
        if (cell.IsRowHeader)
            return "row_header";
        if (cell.IsRowSection)
            return "row_section";
        return "data";
    }

    private static double Clamp01(double value)
        => Math.Clamp(value, 0d, 1d);

    private static double? NormalizeConfidence(double? value)
        => value.HasValue && double.IsFinite(value.Value)
            ? Math.Clamp(value.Value, 0d, 1d)
            : null;

    private static string Normalize(string value)
        => string.Join(
                ' ',
                value
                    .Normalize(NormalizationForm.FormKC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateContext(DoclingCanonicalProjectionContext context)
    {
        if (context.DocumentId == Guid.Empty || context.RevisionId == Guid.Empty)
            throw new ArgumentException("Canonical document and revision identifiers are required.");
        if (context.SourceSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source size cannot be negative.");
        if (context.SourceSha256.Length != 64 || !context.SourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Source SHA-256 is invalid.", nameof(context));
        if (context.ManifestSha256.Length != 64 || !context.ManifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Manifest SHA-256 is invalid.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.StageId)
            || string.IsNullOrWhiteSpace(context.EngineVersion)
            || string.IsNullOrWhiteSpace(context.DeploymentRevision))
        {
            throw new ArgumentException("Docling provenance is incomplete.", nameof(context));
        }
    }
}
