using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend;

internal static class NativeRawPageProjection
{
    // Cooperative own-work deadline, including serialization/hash/compression. SQL wait is separate.
    internal static NativeRawPagePreparation Prepare(Guid tenant, CanonicalIngestionBundle bundle,
        CanonicalCompressedArtifact artifact, CancellationToken ct, Func<TimeSpan>? elapsed = null)
    {
        var document = bundle.Document;
        var root = new NativeRawPageRootBinding(new(tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256), Convert.ToHexString(artifact.ContentHash).ToLowerInvariant());
        var run = bundle.ManifestEnvelope.Manifest.ProcessingRunId;
        var generation = new NativeRawPageGenerationBinding(GenerationId(root, run, document.ManifestSha256),
            NativeRawPageSchema.Projection, run, document.ManifestSha256);
        var work = new NativeRawPageWork(ct, TimeSpan.FromSeconds(2), elapsed);
        try
        {
            work.Check();
            // Every descriptor necessarily exceeds 64 UTF8 bytes. Avoid sorting/allocating a catalogue that cannot fit.
            if (document.PageCount > NativeRawPageSchema.MaximumCatalogBytes / 64)
                throw new NativeRawPageLimitException("native_raw_catalog_size_limit");
            var pages = new List<NativeRawPagePrepared>();
            long utf8Total = 0, compressedTotal = 0;
            foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
            {
                work.Check();
                if (page.PageNumber != pages.Count + 1)
                    throw new NativeRawPageLimitException("native_raw_page_inventory_noncontiguous");
                Strings(page.QualityFlags, work);
                var blocks = new List<NativeRawBlockSnapshot>();
                foreach (var block in page.Blocks)
                {
                    work.Check();
                    if (!CanonicalObservationSelector.IsNativeObservation(block)) continue;
                    if (!NativeRawPageCodec.IsHash(block.Text.RawSha256))
                        throw new NativeRawPageLimitException("native_raw_page_projection_unrepresentable");
                    // The bounded writer below is authoritative; avoid copying a string already too large to fit.
                    if (blocks.Count >= NativeRawPageSchema.MaximumPageBytes / 64
                        || block.Polygon?.Points.Count > NativeRawPageSchema.MaximumPageBytes / 10)
                        throw new NativeRawPageLimitException("native_raw_page_size_limit");
                    String(block.Text.Raw, work);
                    String(block.BlockId, work); String(block.Provenance.Engine, work);
                    String(block.Provenance.EngineVersion, work); String(block.Provenance.Method, work);
                    String(block.Provenance.StageId, work); Strings(block.QualityFlags, work);
                    blocks.Add(new(block.BlockId, block.BlockType, block.Ordinal, block.ReadingOrder,
                        block.Text.Raw, block.Text.RawSha256, Polygon(block.Polygon), block.QualityFlags.ToArray(),
                        block.Provenance.Engine, block.Provenance.EngineVersion, block.Provenance.Method, block.Provenance.StageId));
                }
                var retained = blocks.OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal)
                    .ThenBy(block => block.BlockId, StringComparer.Ordinal).ToArray();
                var snapshot = new NativeRawPageSnapshot(NativeRawPageSchema.Snapshot, root, generation,
                    page.PageNumber, document.PageCount, Geometry(page.Geometry), page.QualityFlags.ToArray(),
                    retained.Length > 0 ? "observed" : "unavailable",
                    retained.Length > 0 ? null : "no_captured_native_observation", retained.Length > 0 ? retained : null);
                var bytes = NativeRawPageCodec.Serialize(snapshot, NativeRawPageSchema.MaximumPageBytes, work);
                utf8Total += bytes.Length;
                if (utf8Total > NativeRawPageSchema.MaximumDocumentBytes)
                    throw new NativeRawPageLimitException("native_raw_document_size_limit");
                var hash = NativeRawPageCodec.Hash(bytes); work.Check();
                var compressed = NativeRawPageCodec.Compress(bytes, work);
                compressedTotal += compressed.Length;
                if (compressedTotal > NativeRawPageSchema.MaximumDocumentBytes)
                    throw new NativeRawPageLimitException("native_raw_document_size_limit");
                pages.Add(new(new(page.PageNumber, snapshot.Availability, snapshot.Reason,
                    retained.Length > 0 ? retained.Length : null, hash, bytes.Length, compressed.Length), compressed));
            }
            work.Check();
            var catalog = new NativeRawPageCatalog(NativeRawPageSchema.Catalog, root, generation, "raw_pages", null,
                document.PageCount, pages.Select(page => page.Descriptor).ToArray());
            var catalogBytes = NativeRawPageCodec.Serialize(catalog, NativeRawPageSchema.MaximumCatalogBytes, work);
            var catalogHash = System.Security.Cryptography.SHA256.HashData(catalogBytes); work.Check();
            return new(catalog, catalogBytes, catalogHash, pages);
        }
        catch (NativeRawPageLimitException error)
        {
            ct.ThrowIfCancellationRequested();
            // Constant-size negative receipt after refusal; no second scan, gzip or reset of the deadline.
            var catalog = new NativeRawPageCatalog(NativeRawPageSchema.Catalog, root, generation, "inline_fallback",
                error.Message, document.PageCount, []);
            var bytes = NativeRawPageCodec.Serialize(catalog, NativeRawPageSchema.MaximumCatalogBytes);
            return new(catalog, bytes, System.Security.Cryptography.SHA256.HashData(bytes), []);
        }
    }

    internal static string GenerationId(NativeRawPageRootBinding root, Guid run, string manifest)
        => NativeRawPageCodec.Hash(NativeRawPageCodec.Utf8.GetBytes(string.Join("\n", NativeRawPageSchema.Projection,
            root.Identity.TenantId.ToString("D"), root.Identity.DocumentId.ToString("D"), root.Identity.RevisionId.ToString("D"),
            root.Identity.SourceSha256.ToLowerInvariant(), run.ToString("D"), manifest.ToLowerInvariant(), root.CanonicalBundleSha256.ToLowerInvariant())));

    private static CanonicalPolygon? Polygon(CanonicalPolygon? polygon) => polygon is null ? null
        : new() { Points = polygon.Points.Select(point => new CanonicalPoint { X = point.X, Y = point.Y }).ToList() };
    private static CanonicalPageGeometry Geometry(CanonicalPageGeometry geometry) => new()
    { Width = geometry.Width, Height = geometry.Height, Unit = geometry.Unit, RotationDegrees = geometry.RotationDegrees,
        CoordinateSpace = geometry.CoordinateSpace };

    private static void Strings(IReadOnlyList<string>? values, NativeRawPageWork work)
    {
        if (values is null) throw new NativeRawPageLimitException("native_raw_page_projection_unrepresentable");
        // Even empty JSON strings need two bytes. Bound collection copies before allocating them.
        if (values.Count > NativeRawPageSchema.MaximumPageBytes / 2)
            throw new NativeRawPageLimitException("native_raw_page_size_limit");
        foreach (var value in values) { work.Check(); String(value, work); }
    }

    private static void String(string? value, NativeRawPageWork work)
    {
        if (value is null) throw new NativeRawPageLimitException("native_raw_page_projection_unrepresentable");
        // Preflight one JSON string before the framework encoder can rent a buffer for it.
        // Default JSON encoding admits Basic Latin; non-ASCII UTF16 units need six escaped bytes.
        long upperBytes = 2;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4095) == 0) work.Check();
            var character = value[index];
            upperBytes += character is '\\' or '\n' or '\r' or '\t' or '\b' or '\f' ? 2
                : character <= 127 && !System.Text.Encodings.Web.JavaScriptEncoder.Default.WillEncode(character) ? 1 : 6;
            if (upperBytes > NativeRawPageSchema.MaximumPageBytes)
                throw new NativeRawPageLimitException("native_raw_page_size_limit");
        }
    }
}
