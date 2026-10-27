using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend;

internal static class NativeRawPageValidation
{
    internal static void Catalog(NativeRawPageCatalog catalog, NativeRawPageRootBinding root)
    {
        if (catalog is null || catalog.Root is null || catalog.Generation is null || catalog.Pages is null
            || catalog.SchemaVersion != NativeRawPageSchema.Catalog || !SameRoot(catalog.Root, root)
            || catalog.PageCount < 0 || catalog.Generation.ProjectionVersion != NativeRawPageSchema.Projection
            || catalog.Generation.ProcessingRunId == Guid.Empty || !NativeRawPageCodec.IsHash(catalog.Generation.ManifestSha256)
            || catalog.Generation.GenerationId != NativeRawPageProjection.GenerationId(root,
                catalog.Generation.ProcessingRunId, catalog.Generation.ManifestSha256)) throw Invalid();
        if (catalog.Mode == "inline_fallback")
        {
            if (catalog.Pages.Count != 0 || catalog.Reason is not ("native_raw_page_size_limit" or "native_raw_document_size_limit"
                or "native_raw_catalog_size_limit" or "native_raw_page_time_limit" or "native_raw_page_inventory_noncontiguous"
                or "native_raw_page_projection_unrepresentable")) throw Invalid();
            return;
        }
        if (catalog.Mode != "raw_pages" || catalog.Reason is not null || catalog.Pages.Count != catalog.PageCount) throw Invalid();
        long rawTotal = 0, storedTotal = 0;
        for (var index = 0; index < catalog.Pages.Count; index++)
        {
            var page = catalog.Pages[index];
            if (page is null || page.PhysicalPage != index + 1 || !NativeRawPageCodec.IsHash(page.Sha256)
                || page.Utf8Bytes is < 2 or > NativeRawPageSchema.MaximumPageBytes
                || page.CompressedBytes is < 1 or > NativeRawPageSchema.MaximumPageBytes
                || !Availability(page.Availability, page.Reason, page.BlockCount)) throw Invalid();
            rawTotal += page.Utf8Bytes; storedTotal += page.CompressedBytes;
        }
        if (rawTotal > NativeRawPageSchema.MaximumDocumentBytes || storedTotal > NativeRawPageSchema.MaximumDocumentBytes) throw Invalid();
    }

    internal static void Page(NativeRawPageSnapshot page, NativeRawPageCatalog catalog, NativeRawPageDescriptor descriptor)
    {
        if (page is null || page.SchemaVersion != NativeRawPageSchema.Snapshot || page.Root is null || page.Generation is null
            || !SameRoot(page.Root, catalog.Root) || page.Generation != catalog.Generation
            || page.PhysicalPage != descriptor.PhysicalPage || page.PageCount != catalog.PageCount
            || page.Availability != descriptor.Availability || page.Reason != descriptor.Reason
            || !Availability(page.Availability, page.Reason, page.Blocks?.Count)
            || page.PageQualityFlags is null || page.PageQualityFlags.Any(value => value is null)
            || page.Blocks?.Count != descriptor.BlockCount) throw Invalid();
        var geometry = page.Geometry;
        if (geometry is null || geometry.CoordinateSpace != CanonicalSchema.NormalizedTopLeftCoordinateSpace
            || geometry.Width.HasValue != geometry.Height.HasValue
            || geometry.Width.HasValue && string.IsNullOrWhiteSpace(geometry.Unit)
            || geometry.Width is { } width && (!double.IsFinite(width) || width <= 0)
            || geometry.Height is { } height && (!double.IsFinite(height) || height <= 0)
            || geometry.RotationDegrees is not (0 or 90 or 180 or 270)) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        NativeRawBlockSnapshot? previous = null;
        foreach (var block in page.Blocks ?? [])
        {
            if (block is null || string.IsNullOrEmpty(block.BlockId) || !ids.Add(block.BlockId)
                || !CanonicalObservationSelector.IsNativeObservation(block.BlockType)
                || block.Raw is null
                || !NativeRawPageCodec.HashEquals(block.RawSha256, RawHash(block.Raw))
                || block.QualityFlags is null || block.QualityFlags.Any(value => value is null)
                || block.Engine is null || block.EngineVersion is null || block.Method is null || block.StageId is null) throw Invalid();
            if (previous is not null && (block.ReadingOrder < previous.ReadingOrder
                || block.ReadingOrder == previous.ReadingOrder && block.Ordinal < previous.Ordinal
                || block.ReadingOrder == previous.ReadingOrder && block.Ordinal == previous.Ordinal
                    && StringComparer.Ordinal.Compare(block.BlockId, previous.BlockId) < 0)) throw Invalid();
            if (block.ObservedPolygon is { } polygon && (polygon.Points is null || polygon.Points.Count < 3
                || polygon.Points.Any(point => point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1))) throw Invalid();
            previous = block;
        }
    }

    internal static bool SameRoot(NativeRawPageRootBinding a, NativeRawPageRootBinding b)
        => a.Identity is not null && b.Identity is not null && a.Identity.TenantId == b.Identity.TenantId
            && a.Identity.DocumentId == b.Identity.DocumentId && a.Identity.RevisionId == b.Identity.RevisionId
            && NativeRawPageCodec.HashEquals(a.Identity.SourceSha256, b.Identity.SourceSha256)
            && NativeRawPageCodec.HashEquals(a.CanonicalBundleSha256, b.CanonicalBundleSha256);
    private static bool Availability(string availability, string? reason, int? count)
        => availability == "observed" && count > 0 && reason is null
            || availability == "unavailable" && count is null && reason == "no_captured_native_observation";
    private static string RawHash(string raw)
    {
        try { return NativeRawPageCodec.Hash(NativeRawPageCodec.Utf8.GetBytes(raw)); }
        catch (System.Text.EncoderFallbackException) { throw Invalid(); }
    }
    private static InvalidDataException Invalid() => NativeRawPageCodec.Invalid();
}
