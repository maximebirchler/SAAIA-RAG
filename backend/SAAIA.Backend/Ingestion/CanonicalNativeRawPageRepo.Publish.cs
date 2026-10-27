using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend;

internal static partial class CanonicalNativeRawPageRepo
{
    internal static async Task PublishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId,
        CanonicalIngestionBundle bundle, CanonicalCompressedArtifact canonicalArtifact, CancellationToken ct)
    {
        var prepared = NativeRawPageProjection.Prepare(tenantId, bundle, canonicalArtifact, ct);
        await StoreAsync(connection, transaction, prepared, ct);
    }

    // The caller already holds the canonical artifact upsert row lock. Generation rotation and pages
    // use that SAME transaction, including concurrent publishers of the same revision.
    internal static async Task StoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        NativeRawPagePreparation prepared, CancellationToken ct)
    {
        var catalog = prepared.Catalog;
        NativeRawPageValidation.Catalog(catalog, catalog.Root);
        var identity = catalog.Root.Identity;
        var args = new
        {
            tenant = identity.TenantId, document = identity.DocumentId, revision = identity.RevisionId,
            source = Convert.FromHexString(identity.SourceSha256), generation = Convert.FromHexString(catalog.Generation.GenerationId),
            version = catalog.Generation.ProjectionVersion, run = catalog.Generation.ProcessingRunId,
            rootHash = Convert.FromHexString(catalog.Root.CanonicalBundleSha256), manifest = Convert.FromHexString(catalog.Generation.ManifestSha256),
            mode = catalog.Mode, reason = catalog.Reason, count = catalog.PageCount,
            hash = prepared.CatalogHash, payload = prepared.CatalogBytes
        };
        // Do not trust a preparation handed across an internal seam without checking its small receipt.
        if (!prepared.CatalogHash.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(prepared.CatalogBytes))
            || !NativeRawPageCodec.Serialize(catalog, NativeRawPageSchema.MaximumCatalogBytes).AsSpan().SequenceEqual(prepared.CatalogBytes)
            || prepared.Pages.Count != catalog.Pages.Count) throw NativeRawPageCodec.Invalid();
        var rootMatches = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM document_revision_binary_artifacts a JOIN document_revisions r
              ON r.tenant_id=a.tenant_id AND r.revision_id=a.revision_id
              WHERE a.tenant_id=@tenant AND r.doc_id=@document AND a.revision_id=@revision AND r.source_hash=@source
                AND a.artifact_type='canonical_ingestion_bundle' AND a.content_hash=@rootHash);
            """, args, transaction, cancellationToken: ct));
        if (!rootMatches) throw NativeRawPageCodec.Invalid();
        var sameGeneration = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM document_revision_native_raw_catalog
                WHERE tenant_id=@tenant AND revision_id=@revision AND generation_id=@generation);
            """, args, transaction, cancellationToken: ct));
        if (sameGeneration)
        {
            var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM document_revision_native_raw_catalog
                 WHERE tenant_id=@tenant AND doc_id=@document AND revision_id=@revision AND source_hash=@source
                  AND generation_id=@generation AND projection_version=@version AND processing_run_id=@run
                  AND canonical_bundle_hash=@rootHash AND manifest_hash=@manifest AND mode=@mode
                  AND reason IS NOT DISTINCT FROM @reason AND page_count=@count AND content_hash=@hash AND payload=@payload);
                """, args, transaction, cancellationToken: ct));
            if (!equal) throw new InvalidDataException("native_observation_generation_conflict");
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM document_revision_native_raw_catalog WHERE tenant_id=@tenant AND revision_id=@revision;
                INSERT INTO document_revision_native_raw_catalog(tenant_id,doc_id,revision_id,source_hash,generation_id,
                  projection_version,processing_run_id,canonical_bundle_hash,manifest_hash,mode,reason,page_count,content_hash,payload)
                VALUES(@tenant,@document,@revision,@source,@generation,@version,@run,@rootHash,@manifest,@mode,@reason,@count,@hash,@payload);
                """, args, transaction, cancellationToken: ct));
        }
        foreach (var page in prepared.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = page.Descriptor;
            if (descriptor.PhysicalPage <= 0 || descriptor.PhysicalPage > catalog.Pages.Count
                || descriptor != catalog.Pages[descriptor.PhysicalPage - 1] || page.Payload.Length != descriptor.CompressedBytes)
                throw NativeRawPageCodec.Invalid();
            var pageArgs = new
            {
                args.tenant, args.document, args.revision, args.source, args.generation,
                page = descriptor.PhysicalPage, availability = descriptor.Availability, reason = descriptor.Reason,
                blocks = descriptor.BlockCount, hash = Convert.FromHexString(descriptor.Sha256),
                bytes = descriptor.Utf8Bytes, stored = descriptor.CompressedBytes, payload = page.Payload
            };
            if (!sameGeneration)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO document_revision_native_raw_pages(tenant_id,doc_id,revision_id,source_hash,generation_id,
                     page_number,availability,reason,block_count,content_hash,compression,byte_size,stored_size,payload)
                    VALUES(@tenant,@document,@revision,@source,@generation,@page,@availability,@reason,@blocks,@hash,'gzip',@bytes,@stored,@payload);
                    """, pageArgs, transaction, cancellationToken: ct));
            else
            {
                var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT EXISTS(SELECT 1 FROM document_revision_native_raw_pages WHERE tenant_id=@tenant AND doc_id=@document
                     AND revision_id=@revision AND source_hash=@source AND generation_id=@generation AND page_number=@page
                     AND availability=@availability AND reason IS NOT DISTINCT FROM @reason AND block_count IS NOT DISTINCT FROM @blocks
                     AND content_hash=@hash AND compression='gzip' AND byte_size=@bytes AND stored_size=@stored AND payload=@payload);
                    """, pageArgs, transaction, cancellationToken: ct));
                if (!equal) throw new InvalidDataException("native_observation_generation_conflict");
            }
        }
        var actualCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM document_revision_native_raw_pages WHERE tenant_id=@tenant AND revision_id=@revision AND generation_id=@generation;
            """, args, transaction, cancellationToken: ct));
        if (actualCount != prepared.Pages.Count) throw NativeRawPageCodec.Invalid();
    }
}
