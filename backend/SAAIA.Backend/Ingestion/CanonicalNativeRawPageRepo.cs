using Dapper;
using Npgsql;

namespace SAAIA.Backend;

internal static partial class CanonicalNativeRawPageRepo
{
    internal static async Task<NativeRawPageLoadResult> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, Guid tenantId, Guid documentId, Guid revisionId, string sourceSha256,
        int physicalPage, NativeRawPageExpectedBinding? expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || documentId == Guid.Empty || revisionId == Guid.Empty || physicalPage <= 0
            || !NativeRawPageCodec.IsHash(sourceSha256)) throw NativeRawPageCodec.Invalid();
        // Presence is independent of validity. An invalid/oversize row must never look like legacy absence.
        // Header and page use ONE SQL statement/snapshot; the canonical payload is never materialized.
        var row = await connection.QuerySingleOrDefaultAsync<ReadRow>(new CommandDefinition("""
            SELECT d.doc_name AS "FileName",d.doc_path AS "DocPath",a.content_hash AS "RootHash",
             a.schema_version AS "RootSchema",a.compression AS "RootCompression",a.byte_size AS "RootBytes",
             (c.revision_id IS NOT NULL) AS "CatalogPresent",c.doc_id AS "CatalogDocument",c.source_hash AS "CatalogSource",
             c.generation_id AS "Generation",c.projection_version AS "Version",c.processing_run_id AS "Run",
             c.canonical_bundle_hash AS "CatalogRoot",c.manifest_hash AS "Manifest",c.mode AS "Mode",c.reason AS "Reason",
             c.page_count AS "PageCount",c.content_hash AS "CatalogHash",octet_length(c.payload) AS "CatalogLength",
             CASE WHEN octet_length(c.payload) BETWEEN 2 AND 1048576 THEN c.payload END AS "CatalogPayload",
             (p.page_number IS NOT NULL) AS "PagePresent",p.page_number AS "PageNumber",p.availability AS "Availability",
             p.reason AS "PageReason",p.block_count AS "BlockCount",p.content_hash AS "PageHash",p.compression AS "Compression",
             p.byte_size AS "ByteSize",p.stored_size AS "StoredSize",octet_length(p.payload) AS "PayloadLength",
             CASE WHEN octet_length(p.payload) BETWEEN 1 AND 4194304 THEN p.payload END AS "PagePayload",
             EXISTS(SELECT 1 FROM document_revision_native_raw_pages q WHERE q.tenant_id=c.tenant_id
              AND q.revision_id=c.revision_id AND q.generation_id=c.generation_id) AS "AnyPage"
            FROM document_revisions r JOIN documents d ON d.tenant_id=r.tenant_id AND d.doc_id=r.doc_id
            JOIN document_revision_binary_artifacts a ON a.tenant_id=r.tenant_id AND a.revision_id=r.revision_id
            LEFT JOIN document_revision_native_raw_catalog c ON c.tenant_id=r.tenant_id AND c.revision_id=r.revision_id
            LEFT JOIN document_revision_native_raw_pages p ON p.tenant_id=c.tenant_id AND p.revision_id=c.revision_id
             AND p.generation_id=c.generation_id AND p.page_number=@physicalPage
            WHERE r.tenant_id=@tenantId AND r.doc_id=@documentId AND r.revision_id=@revisionId
             AND r.source_hash=@source AND d.indexed_version>0 AND d.status<>'deleted'
             AND a.artifact_type='canonical_ingestion_bundle';
            """, new { tenantId, documentId, revisionId, physicalPage, source = Convert.FromHexString(sourceSha256) },
            transaction, cancellationToken: ct));
        if (row is null) return new(NativeRawPageReadKind.ScopeMissing, null, null, null,
            "native_observation_source_unavailable", null, null, null, null);
        if (row.RootHash is not { Length: 32 } || row.RootSchema != "canonical_ingestion_bundle_v1"
            || row.RootCompression != "gzip" || row.RootBytes <= 0) throw NativeRawPageCodec.Invalid();
        var root = new NativeRawPageRootBinding(new(tenantId, documentId, revisionId, sourceSha256), Hex(row.RootHash));
        if (!row.CatalogPresent)
        {
            Expected(expected, root, NativeRawPageReadKind.LegacyAbsent, null);
            return new(NativeRawPageReadKind.LegacyAbsent, root, null, null, "legacy_catalog_absent", row.FileName, row.DocPath, null, null);
        }
        if (row.CatalogPayload is null || row.CatalogLength != row.CatalogPayload.Length || row.CatalogHash is not { Length: 32 }
            || !System.Security.Cryptography.SHA256.HashData(row.CatalogPayload).AsSpan().SequenceEqual(row.CatalogHash))
            throw NativeRawPageCodec.Invalid();
        var catalog = NativeRawPageCodec.Deserialize<NativeRawPageCatalog>(row.CatalogPayload, NativeRawPageSchema.MaximumCatalogBytes);
        NativeRawPageValidation.Catalog(catalog, root);
        var generation = catalog.Generation;
        if (row.CatalogDocument != documentId || !SameBytes(row.CatalogSource, Convert.FromHexString(sourceSha256))
            || !SameBytes(row.Generation, Convert.FromHexString(generation.GenerationId)) || row.Version != generation.ProjectionVersion
            || row.Run != generation.ProcessingRunId || !SameBytes(row.CatalogRoot, row.RootHash)
            || !SameBytes(row.Manifest, Convert.FromHexString(generation.ManifestSha256)) || row.Mode != catalog.Mode
            || row.Reason != catalog.Reason || row.PageCount != catalog.PageCount
            || catalog.Mode == "raw_pages" && physicalPage > catalog.PageCount)
            throw NativeRawPageCodec.Invalid();
        var kind = catalog.Mode == "inline_fallback" ? NativeRawPageReadKind.ExplicitInline : NativeRawPageReadKind.RawPage;
        Expected(expected, root, kind, generation);
        if (kind == NativeRawPageReadKind.ExplicitInline)
        {
            if (row.AnyPage || row.PagePresent) throw NativeRawPageCodec.Invalid();
            return new(kind, root, generation, null, catalog.Reason, row.FileName, row.DocPath, null, null);
        }
        var descriptor = catalog.Pages[physicalPage - 1];
        if (!row.PagePresent || row.PagePayload is null || row.PageNumber != physicalPage || row.Compression != "gzip"
            || row.PayloadLength != descriptor.CompressedBytes || row.StoredSize != descriptor.CompressedBytes
            || row.ByteSize != descriptor.Utf8Bytes || row.Availability != descriptor.Availability || row.PageReason != descriptor.Reason
            || row.BlockCount != descriptor.BlockCount || !SameBytes(row.PageHash, Convert.FromHexString(descriptor.Sha256)))
            throw NativeRawPageCodec.Invalid();
        var page = DecodePage(row.PagePayload, catalog, descriptor, ct);
        return new(kind, root, generation, page, page.Reason, row.FileName, row.DocPath, row.PagePayload.Length, descriptor.Utf8Bytes);
    }

    internal static NativeRawPageSnapshot DecodePage(byte[] payload, NativeRawPageCatalog catalog,
        NativeRawPageDescriptor descriptor, CancellationToken ct)
    {
        using var detail = NativeReadDiagnostics.Start("native.page_decode");
        try
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            using (var phase = detail?.Phase("gzip_inflate_copy"))
            {
                phase?.Set("payload_bytes_materialized", payload.LongLength);
                phase?.Set("declared_uncompressed_bytes", descriptor.Utf8Bytes);
                bytes = NativeRawPageCodec.Inflate(payload, descriptor.Utf8Bytes);
                phase?.Set("inflated_bytes_produced", bytes.LongLength); phase?.Complete();
            }
            using (var phase = detail?.Phase("hash_validate"))
            {
                if (!NativeRawPageCodec.HashEquals(NativeRawPageCodec.Hash(bytes), descriptor.Sha256)) throw NativeRawPageCodec.Invalid();
                phase?.Set("sha256_bytes_processed", bytes.LongLength); phase?.Complete();
            }
            string json;
            using (var phase = detail?.Phase("utf8_decode"))
            {
                phase?.Set("utf8_input_bytes", bytes.LongLength);
                try { json = NativeRawPageCodec.Utf8.GetString(bytes); }
                catch (System.Text.DecoderFallbackException) { throw NativeRawPageCodec.Invalid(); }
                phase?.Set("decoded_utf16_characters", json.Length); phase?.Complete();
            }
            NativeRawPageSnapshot page;
            using (var phase = detail?.Phase("json_deserialize"))
            { page = NativeRawPageCodec.DeserializePage(json); phase?.Complete(); }
            using (var phase = detail?.Phase("native_page_validate"))
            { NativeRawPageValidation.Page(page, catalog, descriptor); ct.ThrowIfCancellationRequested(); phase?.Complete(); }
            detail?.Complete();
            return page;
        }
        catch (Exception error) { detail?.Fail(error); throw; }
    }

    private static void Expected(NativeRawPageExpectedBinding? expected, NativeRawPageRootBinding root,
        NativeRawPageReadKind kind, NativeRawPageGenerationBinding? generation)
    {
        if (expected is not null && (!NativeRawPageCodec.HashEquals(expected.CanonicalBundleSha256, root.CanonicalBundleSha256)
            || expected.Kind != kind || expected.GenerationId != generation?.GenerationId
            || expected.ProjectionVersion != generation?.ProjectionVersion))
            throw new InvalidDataException("native_observation_generation_changed");
    }
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static bool SameBytes(byte[]? a, byte[] b) => a is not null && a.AsSpan().SequenceEqual(b);
    private sealed class ReadRow
    {
        public string FileName { get; set; } = "";
        public string DocPath { get; set; } = "";
        public byte[] RootHash { get; set; } = [];
        public string RootSchema { get; set; } = "";
        public string RootCompression { get; set; } = "";
        public long RootBytes { get; set; }
        public bool CatalogPresent { get; set; }
        public Guid? CatalogDocument { get; set; }
        public byte[]? CatalogSource { get; set; }
        public byte[]? Generation { get; set; }
        public string? Version { get; set; }
        public Guid? Run { get; set; }
        public byte[]? CatalogRoot { get; set; }
        public byte[]? Manifest { get; set; }
        public string? Mode { get; set; }
        public string? Reason { get; set; }
        public int? PageCount { get; set; }
        public byte[]? CatalogHash { get; set; }
        public int? CatalogLength { get; set; }
        public byte[]? CatalogPayload { get; set; }
        public bool PagePresent { get; set; }
        public int? PageNumber { get; set; }
        public string? Availability { get; set; }
        public string? PageReason { get; set; }
        public int? BlockCount { get; set; }
        public byte[]? PageHash { get; set; }
        public string? Compression { get; set; }
        public int? ByteSize { get; set; }
        public int? StoredSize { get; set; }
        public int? PayloadLength { get; set; }
        public byte[]? PagePayload { get; set; }
        public bool AnyPage { get; set; }
    }
}
