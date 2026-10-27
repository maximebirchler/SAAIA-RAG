using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>One transactional stream, including when an observation has thousands of line anchors.</summary>
internal static class CanonicalSourceAnchorStorage
{
    internal static async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, Guid revisionId,
        IReadOnlyList<SourceAnchor> anchors, CancellationToken ct)
    {
        using var probe = BackendDiagnostics.Start("ingestion.source_anchors.copy");
        probe.Set("anchor_count", anchors.Count);
        try
        {
            if (!ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("canonical_source_anchor_transaction_mismatch");
            if (anchors.Count == 0) { probe.Complete("skipped", "no_anchors"); return; }
            await using var writer = await connection.BeginBinaryImportAsync("""
                COPY document_source_anchors(tenant_id,revision_id,anchor_id,projection_type,projection_id,
                    precision,page_start,page_end,content_hash,manifest_hash,payload)
                FROM STDIN (FORMAT BINARY)
                """, ct);
            foreach (var anchor in anchors)
            {
                ct.ThrowIfCancellationRequested();
                var json = CanonicalContractJson.SerializeCanonical(anchor);
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(tenantId, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(revisionId, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(anchor.AnchorId, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(anchor.ProjectionType, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(anchor.ProjectionId, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(anchor.Precision, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(anchor.Regions.Min(region => region.PageNumber), NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(anchor.Regions.Max(region => region.PageNumber), NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(SHA256.HashData(Encoding.UTF8.GetBytes(json)), NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(Convert.FromHexString(anchor.ManifestSha256), NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(json, NpgsqlDbType.Jsonb, ct);
            }
            var written = await writer.CompleteAsync(ct);
            if (written != (ulong)anchors.Count)
                throw new InvalidDataException("canonical_source_anchor_copy_count_mismatch");
            probe.Set("copied_anchor_count", written);
            probe.Complete();
        }
        catch (Exception error) { probe.Fail(error); throw; }
    }
}
