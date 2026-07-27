using System.Security.Cryptography;
using System.Text;

namespace SAAIA.Contracts.DocumentIntelligence;

public sealed record CanonicalValidationIssue(string Path, string Code, string Message);

public sealed class CanonicalContractException(IReadOnlyList<CanonicalValidationIssue> issues)
    : Exception(string.Join(
        Environment.NewLine,
        issues.Select(issue => $"{issue.Path} [{issue.Code}] {issue.Message}")))
{
    public IReadOnlyList<CanonicalValidationIssue> Issues { get; } = issues;
}

public static class CanonicalContractValidator
{
    public static IReadOnlyList<CanonicalValidationIssue> Validate(CanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new List<CanonicalValidationIssue>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        var spanIds = new HashSet<string>(StringComparer.Ordinal);
        var cellIds = new HashSet<string>(StringComparer.Ordinal);
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        var allReferenceIds = new HashSet<string>(StringComparer.Ordinal);

        Require(document.SchemaVersion == CanonicalSchema.DocumentVersion, "$.schemaVersion", "schema_version", "Unsupported canonical document schema.", issues);
        Require(document.DocumentId != Guid.Empty, "$.documentId", "required", "Document ID is required.", issues);
        Require(document.RevisionId != Guid.Empty, "$.revisionId", "required", "Revision ID is required.", issues);
        RequireSha256(document.Source.Sha256, "$.source.sha256", issues);
        Require(document.Source.SizeBytes >= 0, "$.source.sizeBytes", "range", "Source size cannot be negative.", issues);
        RequireSha256(document.ManifestSha256, "$.manifestSha256", issues);
        Require(document.PageCount == document.Pages.Count, "$.pageCount", "count_mismatch", "Page count must match the pages collection.", issues);

        var pageNumbers = new HashSet<int>();
        for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            var path = $"$.pages[{pageIndex}]";
            Require(page.PageNumber > 0, $"{path}.pageNumber", "range", "Page number must be positive.", issues);
            Require(pageNumbers.Add(page.PageNumber), $"{path}.pageNumber", "duplicate", "Page number must be unique.", issues);
            ValidatePageGeometry(page.Geometry, $"{path}.geometry", issues);
            ValidateUnitInterval(page.ImageCoverage, $"{path}.imageCoverage", issues);

            foreach (var block in page.Blocks)
            {
                AddId(block.BlockId, $"{path}.blocks", "blockId", ids, blockIds, allReferenceIds, issues);
                ValidatePolygon(block.Polygon, $"{path}.blocks[{block.BlockId}].polygon", issues);
                ValidateText(block.Text, $"{path}.blocks[{block.BlockId}].text", issues);
                ValidateConfidence(block.Provenance.Confidence, $"{path}.blocks[{block.BlockId}].provenance.confidence", issues);

                foreach (var span in block.Spans)
                {
                    AddId(span.SpanId, $"{path}.blocks[{block.BlockId}].spans", "spanId", ids, spanIds, allReferenceIds, issues);
                    ValidatePolygon(span.Polygon, $"{path}.spans[{span.SpanId}].polygon", issues);
                    ValidateText(span.Text, $"{path}.spans[{span.SpanId}].text", issues);
                    ValidateConfidence(span.Provenance.Confidence, $"{path}.spans[{span.SpanId}].provenance.confidence", issues);

                    foreach (var word in span.Words)
                    {
                        AddId(word.WordId, $"{path}.spans[{span.SpanId}].words", "wordId", ids, null, allReferenceIds, issues);
                        ValidatePolygon(word.Polygon, $"{path}.words[{word.WordId}].polygon", issues);
                        ValidateConfidence(word.Confidence, $"{path}.words[{word.WordId}].confidence", issues);
                    }
                }
            }

            foreach (var table in page.Tables)
            {
                AddId(table.TableId, $"{path}.tables", "tableId", ids, null, allReferenceIds, issues);
                ValidatePolygon(table.Polygon, $"{path}.tables[{table.TableId}].polygon", issues);
                Require(table.RowCount > 0, $"{path}.tables[{table.TableId}].rowCount", "range", "A table must have at least one row.", issues);
                Require(table.ColumnCount > 0, $"{path}.tables[{table.TableId}].columnCount", "range", "A table must have at least one column.", issues);
                ValidateConfidence(table.Provenance.Confidence, $"{path}.tables[{table.TableId}].provenance.confidence", issues);

                foreach (var cell in table.Cells)
                {
                    AddId(cell.CellId, $"{path}.tables[{table.TableId}].cells", "cellId", ids, cellIds, allReferenceIds, issues);
                    ValidatePolygon(cell.Polygon, $"{path}.cells[{cell.CellId}].polygon", issues);
                    ValidateText(cell.Text, $"{path}.cells[{cell.CellId}].text", issues);
                    Require(cell.RowIndex >= 0 && cell.RowSpan > 0 && cell.RowIndex + cell.RowSpan <= table.RowCount, $"{path}.cells[{cell.CellId}].rowIndex", "table_bounds", "Cell row range exceeds the table.", issues);
                    Require(cell.ColumnIndex >= 0 && cell.ColumnSpan > 0 && cell.ColumnIndex + cell.ColumnSpan <= table.ColumnCount, $"{path}.cells[{cell.CellId}].columnIndex", "table_bounds", "Cell column range exceeds the table.", issues);
                }
            }

            foreach (var figure in page.Figures)
            {
                AddId(figure.FigureId, $"{path}.figures", "figureId", ids, null, allReferenceIds, issues);
                ValidatePolygon(figure.Polygon, $"{path}.figures[{figure.FigureId}].polygon", issues);
                ValidateConfidence(figure.Provenance.Confidence, $"{path}.figures[{figure.FigureId}].provenance.confidence", issues);
            }
        }

        foreach (var asset in document.Assets)
        {
            AddId(asset.AssetId, "$.assets", "assetId", ids, null, allReferenceIds, issues);
            RequireSha256(asset.Sha256, $"$.assets[{asset.AssetId}].sha256", issues);
            Require(asset.SizeBytes >= 0, $"$.assets[{asset.AssetId}].sizeBytes", "range", "Asset size cannot be negative.", issues);
        }

        foreach (var relation in document.Relations)
            AddId(relation.RelationId, "$.relations", "relationId", ids, null, allReferenceIds, issues);

        foreach (var section in document.SectionTree)
        {
            AddId(section.SectionId, "$.sectionTree", "sectionId", ids, sectionIds, allReferenceIds, issues);
            Require(section.PageStart > 0 && section.PageEnd >= section.PageStart, $"$.sectionTree[{section.SectionId}]", "page_range", "Section page range is invalid.", issues);
        }

        ValidateReferences(document, allReferenceIds, blockIds, spanIds, sectionIds, issues);
        return issues;
    }

    public static IReadOnlyList<CanonicalValidationIssue> Validate(SourceAnchor anchor, CanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(document);
        var issues = Validate(document).ToList();

        Require(anchor.SchemaVersion == CanonicalSchema.SourceAnchorVersion, "$.schemaVersion", "schema_version", "Unsupported source anchor schema.", issues);
        Require(!string.IsNullOrWhiteSpace(anchor.AnchorId), "$.anchorId", "required", "Anchor ID is required.", issues);
        Require(anchor.DocumentId == document.DocumentId, "$.documentId", "identity_mismatch", "Anchor document ID does not match.", issues);
        Require(anchor.RevisionId == document.RevisionId, "$.revisionId", "identity_mismatch", "Anchor revision ID does not match.", issues);
        Require(string.Equals(anchor.SourceSha256, document.Source.Sha256, StringComparison.OrdinalIgnoreCase), "$.sourceSha256", "identity_mismatch", "Anchor source hash does not match.", issues);
        Require(string.Equals(anchor.ManifestSha256, document.ManifestSha256, StringComparison.OrdinalIgnoreCase), "$.manifestSha256", "identity_mismatch", "Anchor manifest hash does not match.", issues);
        Require(!string.IsNullOrWhiteSpace(anchor.ProjectionId), "$.projectionId", "required", "Projection ID is required.", issues);
        Require(!string.IsNullOrWhiteSpace(anchor.ProjectionType), "$.projectionType", "required", "Projection type is required.", issues);
        Require(!string.IsNullOrWhiteSpace(anchor.Precision), "$.precision", "required", "Anchor precision is required.", issues);
        Require(anchor.Regions.Count > 0, "$.regions", "required", "At least one source region is required.", issues);

        for (var index = 0; index < anchor.Regions.Count; index++)
        {
            var region = anchor.Regions[index];
            var path = $"$.regions[{index}]";
            var page = document.Pages.SingleOrDefault(item => item.PageNumber == region.PageNumber);
            Require(page is not null, $"{path}.pageNumber", "broken_reference", "Anchor page does not exist.", issues);
            ValidatePolygon(region.Polygon, $"{path}.polygon", issues);
            RequireSha256(region.RawTextSha256, $"{path}.rawTextSha256", issues);

            if (page is null)
                continue;

            ValidateRegionReferences(region.BlockIds, page.Blocks.Select(item => item.BlockId), $"{path}.blockIds", issues);
            ValidateRegionReferences(region.SpanIds, page.Blocks.SelectMany(item => item.Spans).Select(item => item.SpanId), $"{path}.spanIds", issues);
            ValidateRegionReferences(region.TableCellIds, page.Tables.SelectMany(item => item.Cells).Select(item => item.CellId), $"{path}.tableCellIds", issues);
            var rawText = CanonicalEvidenceResolver.ResolveRegion(region, page).RawText;
            var rawSha256 = ComputeSha256(rawText);
            Require(string.Equals(region.RawTextSha256, rawSha256, StringComparison.OrdinalIgnoreCase), $"{path}.rawTextSha256", "hash_mismatch", "Region raw-text hash does not match the referenced evidence.", issues);
        }

        return issues;
    }

    public static void ValidateOrThrow(CanonicalDocument document)
        => ThrowIfAny(Validate(document));

    public static void ValidateOrThrow(SourceAnchor anchor, CanonicalDocument document)
        => ThrowIfAny(Validate(anchor, document));

    private static void ValidateReferences(
        CanonicalDocument document,
        HashSet<string> allIds,
        HashSet<string> blockIds,
        HashSet<string> spanIds,
        HashSet<string> sectionIds,
        List<CanonicalValidationIssue> issues)
    {
        foreach (var page in document.Pages)
        {
            foreach (var block in page.Blocks)
            {
                ValidateOptionalReference(block.ParentBlockId, blockIds, $"$.blocks[{block.BlockId}].parentBlockId", issues);
                ValidateReferences(block.ChildBlockIds, blockIds, $"$.blocks[{block.BlockId}].childBlockIds", issues);
            }

            foreach (var table in page.Tables)
            {
                ValidateReferences(table.CaptionBlockIds, blockIds, $"$.tables[{table.TableId}].captionBlockIds", issues);
                foreach (var cell in table.Cells)
                {
                    ValidateReferences(cell.BlockIds, blockIds, $"$.cells[{cell.CellId}].blockIds", issues);
                    ValidateReferences(cell.SpanIds, spanIds, $"$.cells[{cell.CellId}].spanIds", issues);
                }
            }

            foreach (var figure in page.Figures)
            {
                ValidateReferences(figure.CaptionBlockIds, blockIds, $"$.figures[{figure.FigureId}].captionBlockIds", issues);
                ValidateReferences(figure.AssetIds, allIds, $"$.figures[{figure.FigureId}].assetIds", issues);
            }
        }

        foreach (var section in document.SectionTree)
        {
            ValidateOptionalReference(section.ParentSectionId, sectionIds, $"$.sectionTree[{section.SectionId}].parentSectionId", issues);
            ValidateReferences(section.TitleBlockIds, blockIds, $"$.sectionTree[{section.SectionId}].titleBlockIds", issues);
            ValidateReferences(section.ContentBlockIds, blockIds, $"$.sectionTree[{section.SectionId}].contentBlockIds", issues);
        }

        foreach (var relation in document.Relations)
        {
            AddRequiredReference(relation.SourceId, allIds, $"$.relations[{relation.RelationId}].sourceId", issues);
            AddRequiredReference(relation.TargetId, allIds, $"$.relations[{relation.RelationId}].targetId", issues);
            ValidateConfidence(relation.Confidence, $"$.relations[{relation.RelationId}].confidence", issues);
        }
    }

    private static void ValidateText(CanonicalTextVariants text, string path, List<CanonicalValidationIssue> issues)
    {
        if (string.IsNullOrEmpty(text.Raw))
        {
            Require(string.IsNullOrEmpty(text.RawSha256) || string.Equals(text.RawSha256, ComputeSha256(""), StringComparison.OrdinalIgnoreCase), $"{path}.rawSha256", "hash_mismatch", "Empty raw text has an invalid hash.", issues);
            return;
        }

        RequireSha256(text.RawSha256, $"{path}.rawSha256", issues);
        Require(string.Equals(text.RawSha256, ComputeSha256(text.Raw), StringComparison.OrdinalIgnoreCase), $"{path}.rawSha256", "hash_mismatch", "Raw-text hash does not match.", issues);
    }

    private static void ValidatePageGeometry(CanonicalPageGeometry geometry, string path, List<CanonicalValidationIssue> issues)
    {
        Require(geometry.Width.HasValue == geometry.Height.HasValue, path, "geometry_pair", "Page width and height must be supplied together.", issues);
        if (geometry.Width.HasValue)
            Require(double.IsFinite(geometry.Width.Value) && geometry.Width.Value > 0, $"{path}.width", "range", "Page width must be positive and finite.", issues);
        if (geometry.Height.HasValue)
            Require(double.IsFinite(geometry.Height.Value) && geometry.Height.Value > 0, $"{path}.height", "range", "Page height must be positive and finite.", issues);
        Require(!geometry.Width.HasValue || !string.IsNullOrWhiteSpace(geometry.Unit), $"{path}.unit", "required", "A unit is required when physical page dimensions are supplied.", issues);
        Require(geometry.RotationDegrees is 0 or 90 or 180 or 270, $"{path}.rotationDegrees", "range", "Rotation must be 0, 90, 180 or 270 degrees.", issues);
        Require(geometry.CoordinateSpace == CanonicalSchema.NormalizedTopLeftCoordinateSpace, $"{path}.coordinateSpace", "coordinate_space", "Unsupported coordinate space.", issues);
    }

    private static void ValidatePolygon(CanonicalPolygon? polygon, string path, List<CanonicalValidationIssue> issues)
    {
        if (polygon is null)
            return;

        Require(polygon.Points.Count >= 3, path, "polygon", "A polygon must contain at least three points.", issues);
        for (var index = 0; index < polygon.Points.Count; index++)
        {
            var point = polygon.Points[index];
            Require(double.IsFinite(point.X) && point.X is >= 0 and <= 1, $"{path}.points[{index}].x", "normalized_coordinate", "X must be finite and in [0, 1].", issues);
            Require(double.IsFinite(point.Y) && point.Y is >= 0 and <= 1, $"{path}.points[{index}].y", "normalized_coordinate", "Y must be finite and in [0, 1].", issues);
        }
    }

    private static void AddId(
        string value,
        string path,
        string field,
        Dictionary<string, string> ids,
        HashSet<string>? typedIds,
        HashSet<string> allReferenceIds,
        List<CanonicalValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new($"{path}.{field}", "required", $"{field} is required."));
            return;
        }

        if (!ids.TryAdd(value, $"{path}.{field}"))
            issues.Add(new($"{path}.{field}", "duplicate_id", $"ID '{value}' already exists at {ids[value]}."));
        typedIds?.Add(value);
        allReferenceIds.Add(value);
    }

    private static void ValidateReferences(IEnumerable<string> values, HashSet<string> known, string path, List<CanonicalValidationIssue> issues)
    {
        foreach (var value in values)
            AddRequiredReference(value, known, path, issues);
    }

    private static void ValidateRegionReferences(IEnumerable<string> values, IEnumerable<string> known, string path, List<CanonicalValidationIssue> issues)
    {
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        foreach (var value in values)
            AddRequiredReference(value, knownSet, path, issues);
    }

    private static void ValidateOptionalReference(string? value, HashSet<string> known, string path, List<CanonicalValidationIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AddRequiredReference(value, known, path, issues);
    }

    private static void AddRequiredReference(string value, HashSet<string> known, string path, List<CanonicalValidationIssue> issues)
    {
        Require(!string.IsNullOrWhiteSpace(value) && known.Contains(value), path, "broken_reference", $"Referenced ID '{value}' does not exist.", issues);
    }

    private static void RequireSha256(string value, string path, List<CanonicalValidationIssue> issues)
        => Require(value.Length == 64 && value.All(Uri.IsHexDigit), path, "sha256", "A lowercase or uppercase 64-character SHA-256 value is required.", issues);

    private static void ValidateConfidence(double? value, string path, List<CanonicalValidationIssue> issues)
        => ValidateUnitInterval(value, path, issues);

    private static void ValidateUnitInterval(double? value, string path, List<CanonicalValidationIssue> issues)
    {
        if (value.HasValue)
            Require(double.IsFinite(value.Value) && value.Value is >= 0 and <= 1, path, "range", "Value must be finite and in [0, 1].", issues);
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ThrowIfAny(IReadOnlyList<CanonicalValidationIssue> issues)
    {
        if (issues.Count > 0)
            throw new CanonicalContractException(issues);
    }

    private static void Require(bool condition, string path, string code, string message, List<CanonicalValidationIssue> issues)
    {
        if (!condition)
            issues.Add(new(path, code, message));
    }
}
