using Dapper;
using Npgsql;

namespace SAAIA.Backend.Retrieval;

internal sealed record CanonicalIndexPresence(
    string Status, Guid DocId, Guid? RevisionId, int IndexedVersion,
    int? RequestedPhysicalPage, Guid? RequestedChunkId,
    long RevisionChunkCount, long MatchingChunkCount, int? FirstPhysicalPage, int? LastPhysicalPage,
    string Scope = "canonical-postgres", bool VectorPresenceChecked = false);

/// <summary>
/// Targeted canonical-presence check, separate from ranking. An empty search result
/// never implies absence from the index. This check does not claim vector parity.
/// </summary>
internal static class CanonicalIndexProbe
{
    internal static async Task<CanonicalIndexPresence> CheckAsync(RetrievalExecutionContext context,
        NpgsqlDataSource dataSource, Guid docId, Guid? revisionId, int? physicalPage, Guid? chunkId)
    {
        if (physicalPage is <= 0) throw new ArgumentOutOfRangeException(nameof(physicalPage));
        using var diagnostic = BackendDiagnostics.Start("retrieval.index_presence", context.Logger);
        diagnostic.Set("doc_id", docId);
        diagnostic.Set("revision_id", revisionId);
        diagnostic.Set("physical_page", physicalPage);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<PresenceRow>(new CommandDefinition("""
                SELECT d.status AS "DocumentStatus", d.indexed_version AS "IndexedVersion",
                       dr.revision_id AS "RevisionId", dr.indexed_version AS "RevisionVersion",
                       count(rc.retrieval_chunk_id) AS "RevisionChunkCount",
                       count(rc.retrieval_chunk_id) FILTER (WHERE
                         (@physical_page IS NULL OR (rc.page_start<=@physical_page AND rc.page_end>=@physical_page))
                         AND (@chunk_id IS NULL OR rc.retrieval_chunk_id=@chunk_id)) AS "MatchingChunkCount",
                       min(rc.page_start) AS "FirstPhysicalPage", max(rc.page_end) AS "LastPhysicalPage"
                FROM documents d
                LEFT JOIN document_revisions dr ON dr.tenant_id=d.tenant_id AND dr.doc_id=d.doc_id
                  AND ((@revision_id IS NULL AND dr.indexed_version=d.indexed_version) OR dr.revision_id=@revision_id)
                LEFT JOIN retrieval_chunks rc ON rc.tenant_id=dr.tenant_id AND rc.revision_id=dr.revision_id
                WHERE d.tenant_id=@tenant_id AND d.doc_id=@doc_id
                GROUP BY d.status, d.indexed_version, dr.revision_id, dr.indexed_version;
                """, new
            {
                tenant_id = context.TenantId,
                doc_id = docId,
                revision_id = revisionId,
                physical_page = physicalPage,
                chunk_id = chunkId
            }, cancellationToken: context.CancellationToken))
                .ConfigureAwait(false);
            var status = Classify(row?.DocumentStatus, row?.IndexedVersion ?? 0, row?.RevisionId,
                row?.RevisionVersion, row?.RevisionChunkCount ?? 0, row?.MatchingChunkCount ?? 0);
            diagnostic.Set("status", status);
            diagnostic.Set("revision_chunk_count", row?.RevisionChunkCount ?? 0);
            diagnostic.Set("matching_chunk_count", row?.MatchingChunkCount ?? 0);
            diagnostic.Complete();
            return new(status, docId, row?.RevisionId, row?.IndexedVersion ?? 0, physicalPage, chunkId,
                row?.RevisionChunkCount ?? 0, row?.MatchingChunkCount ?? 0, row?.FirstPhysicalPage, row?.LastPhysicalPage);
        }
        catch (Exception error) { diagnostic.Fail(error); throw; }
    }

    internal static string Classify(string? documentStatus, int indexedVersion, Guid? revisionId,
        int? revisionVersion, long revisionChunkCount, long matchingChunkCount)
        => documentStatus is null ? "document_not_found"
            : documentStatus != "indexed" || indexedVersion <= 0 ? "index_not_published"
            : revisionId is null ? "revision_not_found"
            : revisionVersion != indexedVersion ? "revision_not_current"
            : revisionChunkCount == 0 ? "no_canonical_chunks"
            : matchingChunkCount == 0 ? "requested_locator_not_present"
            : "canonical_present";

    private sealed record PresenceRow(string DocumentStatus, int IndexedVersion, Guid? RevisionId,
        int? RevisionVersion, long RevisionChunkCount, long MatchingChunkCount,
        int? FirstPhysicalPage, int? LastPhysicalPage);
}
