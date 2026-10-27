using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Private immutable diagnostic artifact, committed with the canonical bundle.</summary>
internal static class DoclingInputGeometryArtifactRepo
{
    internal static IngestionArtifactManifest Describe(DoclingInputGeometryArtifact artifact) => new()
    {
        ArtifactId = CanonicalStableId.Create("artifact", artifact.RevisionId.ToString("D"), DoclingInputGeometryArtifact.ArtifactType, artifact.Sha256),
        ArtifactType = DoclingInputGeometryArtifact.ArtifactType,
        SchemaVersion = DoclingInputGeometryArtifact.Schema,
        Sha256 = artifact.Sha256,
        SizeBytes = artifact.ByteSize,
        StorageKey = artifact.StorageKey
    };
    internal static void ValidateIdentity(DoclingInputGeometryArtifact artifact, Guid tenant, Guid document, Guid revision, Guid run, string source)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty || run == Guid.Empty || artifact.TenantId != tenant || artifact.DocumentId != document || artifact.RevisionId != revision || artifact.ProcessingRunId != run || !string.Equals(artifact.SourceSha256, source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("input_geometry_publication_identity_invalid");
    }

    internal static async Task StoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenant, CanonicalIngestionBundle bundle, DoclingInputGeometryArtifact artifact, CancellationToken ct)
    {
        var document = bundle.Document;
        ValidateIdentity(artifact, tenant, document.DocumentId, document.RevisionId, bundle.ManifestEnvelope.Manifest.ProcessingRunId, document.Source.Sha256);
        using var payload = Read(artifact);
        if (!CanonicalContractJson.HasValidHash(bundle.ManifestEnvelope) || payload.RootElement.GetProperty("backendCodeRevision").GetString() != bundle.ManifestEnvelope.Manifest.CodeRevision || payload.RootElement.GetProperty("serializedPdfBytes").GetInt64() != document.Source.SizeBytes)
            throw new InvalidDataException("input_geometry_manifest_identity_invalid");
        var descriptors = bundle.ManifestEnvelope.Manifest.Artifacts.Where(a => a.ArtifactType == DoclingInputGeometryArtifact.ArtifactType).ToArray();
        var expected = Describe(artifact);
        if (descriptors.Length != 1 || descriptors[0].Sha256 != expected.Sha256 || descriptors[0].SchemaVersion != expected.SchemaVersion || descriptors[0].SizeBytes != expected.SizeBytes || descriptors[0].StorageKey != expected.StorageKey || descriptors[0].ArtifactId != expected.ArtifactId)
            throw new InvalidDataException("input_geometry_manifest_descriptor_invalid");
        var owned = await conn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT 1 FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@document
            AND revision_id=@revision AND source_hash=@source FOR KEY SHARE;
            """, new { tenant, document = document.DocumentId, revision = document.RevisionId, source = Convert.FromHexString(document.Source.Sha256) }, tx, cancellationToken: ct));
        if (owned != 1)
            throw new InvalidDataException("input_geometry_publication_revision_invalid");
        var args = new
        {
            tenant,
            revision = document.RevisionId,
            type = DoclingInputGeometryArtifact.ArtifactType,
            schema = DoclingInputGeometryArtifact.Schema,
            hash = Convert.FromHexString(artifact.Sha256),
            size = artifact.ByteSize,
            stored = artifact.StoredSize,
            payload = artifact.CopyGzip()
        };
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO document_revision_binary_artifacts(tenant_id,revision_id,artifact_type,schema_version,content_hash,compression,byte_size,stored_size,payload)
            VALUES(@tenant,@revision,@type,@schema,@hash,'gzip',@size,@stored,@payload)
            ON CONFLICT(revision_id,artifact_type) DO NOTHING;
            """, args, tx, cancellationToken: ct));
        var same = await conn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT 1 FROM document_revision_binary_artifacts WHERE tenant_id=@tenant AND revision_id=@revision
            AND artifact_type=@type AND schema_version=@schema AND content_hash=@hash AND compression='gzip'
            AND byte_size=@size AND stored_size=@stored AND payload=@payload;
            """, args, tx, cancellationToken: ct));
        if (same != 1)
            throw new InvalidDataException("input_geometry_immutable_conflict");
    }

    // Separate defensive storage/read work: byte-bounded, not claimed inside the observer's five seconds.
    internal static JsonDocument Read(DoclingInputGeometryArtifact artifact)
    {
        if (artifact.ByteSize > DoclingInputGeometryCapture.MaximumJsonBytes || artifact.StoredSize > DoclingInputGeometryCapture.MaximumGzipBytes)
            throw new InvalidDataException("input_geometry_read_limit");
        using var input = new MemoryStream(artifact.CopyGzip(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > DoclingInputGeometryCapture.MaximumJsonBytes)
                throw new InvalidDataException("input_geometry_read_limit");
            output.Write(buffer, 0, count);
        }

        var bytes = output.ToArray();
        if (bytes.LongLength != artifact.ByteSize || DoclingInputGeometryCapture.Sha(bytes) != artifact.Sha256 || !bytes.AsSpan().SequenceEqual(artifact.CopyJson()))
            throw new InvalidDataException("input_geometry_content_hash_invalid");
        // Reject fields outside this private contract, including nested token/link fields.
        try
        {
            _ = JsonSerializer.Deserialize<DoclingInputGeometrySnapshot>(bytes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("input_geometry_payload_contract_invalid");
        }

        var result = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        try
        {
            var r = result.RootElement;
            if (r.GetProperty("schemaVersion").GetString() != DoclingInputGeometryArtifact.Schema || r.GetProperty("availability").GetString() != "observed" || !r.GetProperty("sourceInventoryComplete").GetBoolean() || r.GetProperty("tenantId").GetGuid() != artifact.TenantId || r.GetProperty("documentId").GetGuid() != artifact.DocumentId || r.GetProperty("revisionId").GetGuid() != artifact.RevisionId || r.GetProperty("processingRunId").GetGuid() != artifact.ProcessingRunId || !string.Equals(r.GetProperty("expectedSourceSha256").GetString(), artifact.SourceSha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(r.GetProperty("serializedPdfSha256").GetString(), artifact.SourceSha256, StringComparison.OrdinalIgnoreCase) || r.GetProperty("capturePoint").GetString() != "final_http_entity_after_httpclient_decompression_before_backend_numeric_conversion" || r.GetProperty("responseSha256").GetString()is not { Length: 64 } || r.GetProperty("serializations").GetInt32() != 1)
                throw new InvalidDataException("input_geometry_payload_identity_invalid");
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
