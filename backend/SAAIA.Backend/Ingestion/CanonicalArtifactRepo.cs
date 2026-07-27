using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record CanonicalCompressedArtifact(
    string ArtifactType,
    string SchemaVersion,
    byte[] ContentHash,
    long ByteSize,
    byte[] Payload);

internal static class CanonicalArtifactRepo
{
    internal const string BundleArtifactType = "canonical_ingestion_bundle";
    internal const string Compression = "gzip";

    public static CanonicalCompressedArtifact BuildBundleArtifact(CanonicalIngestionBundle bundle)
    {
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        var json = CanonicalContractJson.SerializeCanonical(bundle);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new(
            BundleArtifactType,
            bundle.SchemaVersion,
            SHA256.HashData(bytes),
            bytes.LongLength,
            Compress(bytes));
    }

    public static CanonicalIngestionBundle ReadBundleArtifact(
        CanonicalCompressedArtifact artifact,
        long maxUncompressedBytes = 512L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!string.Equals(artifact.ArtifactType, BundleArtifactType, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected canonical artifact type '{artifact.ArtifactType}'.");

        var bytes = Decompress(artifact.Payload, maxUncompressedBytes);
        if (bytes.LongLength != artifact.ByteSize)
            throw new InvalidDataException("Canonical artifact byte size does not match.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), artifact.ContentHash))
            throw new InvalidDataException("Canonical artifact content hash does not match.");

        var bundle = CanonicalContractJson.Deserialize<CanonicalIngestionBundle>(
            Encoding.UTF8.GetString(bytes));
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        return bundle;
    }

    public static async Task UpsertBundleAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        CanonicalIngestionBundle bundle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        var artifact = BuildBundleArtifact(bundle);
        if (bundle.Document.RevisionId != revisionId)
            throw new InvalidOperationException("Canonical bundle revision does not match the publication revision.");

        const string artifactSql = """
INSERT INTO document_revision_binary_artifacts(
    tenant_id,
    revision_id,
    artifact_type,
    schema_version,
    content_hash,
    compression,
    byte_size,
    stored_size,
    payload)
VALUES(
    @tenant_id,
    @revision_id,
    @artifact_type,
    @schema_version,
    @content_hash,
    @compression,
    @byte_size,
    @stored_size,
    @payload)
ON CONFLICT (revision_id, artifact_type) DO UPDATE
SET schema_version=EXCLUDED.schema_version,
    content_hash=EXCLUDED.content_hash,
    compression=EXCLUDED.compression,
    byte_size=EXCLUDED.byte_size,
    stored_size=EXCLUDED.stored_size,
    payload=EXCLUDED.payload,
    created_at=now();
""";
        await conn.ExecuteAsync(new CommandDefinition(
            artifactSql,
            new
            {
                tenant_id = tenantId,
                revision_id = revisionId,
                artifact_type = artifact.ArtifactType,
                schema_version = artifact.SchemaVersion,
                content_hash = artifact.ContentHash,
                compression = Compression,
                byte_size = artifact.ByteSize,
                stored_size = artifact.Payload.LongLength,
                payload = artifact.Payload
            },
            transaction: tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM document_source_anchors WHERE revision_id=@revision_id;",
            new { revision_id = revisionId },
            transaction: tx,
            cancellationToken: ct));

        const string anchorSql = """
INSERT INTO document_source_anchors(
    tenant_id,
    revision_id,
    anchor_id,
    projection_type,
    projection_id,
    precision,
    page_start,
    page_end,
    content_hash,
    manifest_hash,
    payload)
VALUES(
    @tenant_id,
    @revision_id,
    @anchor_id,
    @projection_type,
    @projection_id,
    @precision,
    @page_start,
    @page_end,
    @content_hash,
    @manifest_hash,
    CAST(@payload AS jsonb));
""";
        foreach (var anchor in bundle.SourceAnchors)
        {
            var json = CanonicalContractJson.SerializeCanonical(anchor);
            await conn.ExecuteAsync(new CommandDefinition(
                anchorSql,
                new
                {
                    tenant_id = tenantId,
                    revision_id = revisionId,
                    anchor_id = anchor.AnchorId,
                    projection_type = anchor.ProjectionType,
                    projection_id = anchor.ProjectionId,
                    precision = anchor.Precision,
                    page_start = anchor.Regions.Min(static region => region.PageNumber),
                    page_end = anchor.Regions.Max(static region => region.PageNumber),
                    content_hash = SHA256.HashData(Encoding.UTF8.GetBytes(json)),
                    manifest_hash = Convert.FromHexString(anchor.ManifestSha256),
                    payload = json
                },
                transaction: tx,
                cancellationToken: ct));
        }
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] payload, long maxUncompressedBytes)
    {
        if (maxUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes));

        using var input = new MemoryStream(payload, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (output.Length + read > maxUncompressedBytes)
                throw new InvalidDataException("Canonical artifact exceeds the configured decompression limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
