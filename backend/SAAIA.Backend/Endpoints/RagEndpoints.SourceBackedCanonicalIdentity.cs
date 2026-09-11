using System.Text.Json;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{
    internal static async Task<IReadOnlyDictionary<string, RagDocumentSourceIdentity>> LoadRagCanonicalDocumentSourceIdentitiesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var chunkIds = matches.Select(static match => Guid.TryParse(match.ChunkId, out var id) ? id : (Guid?)null)
            .Where(static id => id.HasValue).Select(static id => id!.Value).Distinct().ToArray();
        var identities = new Dictionary<string, RagDocumentSourceIdentity>(StringComparer.OrdinalIgnoreCase);
        if (chunkIds.Length == 0)
            return identities;

        // Resolve the actual current chunk, not just the latest document header.
        // Publication can change between retrieval and response construction.
        const string sql = """
SELECT d.doc_id AS "DocId", d.doc_path AS "DocPath",
       c.retrieval_chunk_id AS "ChunkId", r.revision_id::text AS "RevisionId",
       encode(r.source_hash, 'hex') AS "SourceHash"
FROM retrieval_chunks c
JOIN document_revisions r ON r.tenant_id=c.tenant_id AND r.revision_id=c.revision_id
JOIN documents d ON d.tenant_id=r.tenant_id AND d.doc_id=r.doc_id AND d.indexed_version=r.indexed_version
WHERE c.tenant_id=@tenant AND c.retrieval_chunk_id=ANY(@chunkIds)
  AND d.status='indexed' AND d.indexed_version>0;
""";
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CanonicalChunkIdentityRow>(new CommandDefinition(sql,
            new { tenant = tenantId, chunkIds }, cancellationToken: ct));
        foreach (var row in rows)
        {
            var key = BuildCanonicalChunkIdentityKey(row.DocId.ToString(), row.DocPath, row.ChunkId.ToString());
            if (key is not null && !string.IsNullOrWhiteSpace(row.RevisionId) && !string.IsNullOrWhiteSpace(row.SourceHash))
                identities[key] = new RagDocumentSourceIdentity(row.RevisionId, row.SourceHash);
        }
        return identities;
    }

    private static string? BuildCanonicalChunkIdentityKey(string? docId, string? docPath, string? chunkId)
    {
        var path = NormalizeDocumentSourceHashPathKey(docPath);
        if (!Guid.TryParse(docId, out var document) || !Guid.TryParse(chunkId, out var chunk) || string.IsNullOrWhiteSpace(path))
            return null;
        return "canonical-chunk:" + JsonSerializer.Serialize(new[] { document.ToString("N"), path, chunk.ToString("N") });
    }

    private static RagSearchResponse RetainCurrentCanonicalSourceIdentities(
        RagSearchResponse response, IReadOnlyDictionary<string, RagDocumentSourceIdentity> identities)
    {
        var current = response.Matches.Where(match => ResolveDocumentSourceIdentity(match, identities) is not null).ToList();
        if (current.Count == response.Matches.Count)
            return response;
        var errors = response.DegradedRetrieverErrors?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        errors["source_identity"] = "canonical_chunk_not_current";
        return response with
        {
            Matches = current,
            DegradedRetrievers = (response.DegradedRetrievers ?? Array.Empty<string>()).Append("source_identity")
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            DegradedRetrieverErrors = errors
        };
    }

    private sealed class CanonicalChunkIdentityRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public Guid ChunkId { get; set; }
        public string RevisionId { get; set; } = "";
        public string SourceHash { get; set; } = "";
    }
}
