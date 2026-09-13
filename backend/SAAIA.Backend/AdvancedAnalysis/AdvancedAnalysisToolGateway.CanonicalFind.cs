using Dapper;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class AdvancedAnalysisToolGateway
{
    private async Task<(IReadOnlyList<AdvancedAnalysisEvidenceReference> References,
        AdvancedAnalysisFindDiagnostic Diagnostic)> FindCanonicalTextAsync(
        AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
    {
        var observed = _evidence.First(item =>
            string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY", transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var scope = new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!), revision_id = Guid.Parse(request.RevisionId!) };
        // Validate identity even when the literal has no match; an empty result
        // must not conceal a removed, changed or cross-tenant source.
        const string identitySql = """
            SELECT encode(dr.source_hash, 'hex') AS "SourceHash", d.doc_path AS "DocPath"
            FROM document_revisions dr
            JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
            WHERE dr.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
              AND d.status='indexed' AND d.indexed_version>0;
            """;
        var identity = await connection.QuerySingleOrDefaultAsync<CanonicalFindSourceIdentity>(new CommandDefinition(
            identitySql, scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var sourceHash = identity?.SourceHash;
        if (!string.Equals(sourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(sourceHash)
            || !string.Equals(NormalizePath(identity?.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal))
            throw new AdvancedAnalysisToolException("canonical_find_source_identity_changed");

        // strpos treats percent/underscore/quotes as text, never as SQL patterns.
        // Whitespace normalization permits a title split by an extracted newline.
        const string sql = """
            SELECT d.doc_id::text AS "DocId", dr.revision_id::text AS "RevisionId",
                   d.doc_name AS "FileName", d.doc_path AS "DocPath",
                   encode(dr.source_hash, 'hex') AS "SourceHash",
                   rc.page_start AS "PageStart", rc.page_end AS "PageEnd",
                   rc.retrieval_chunk_id::text AS "ChunkId"
            FROM retrieval_chunks rc
            JOIN document_revisions dr ON dr.tenant_id=rc.tenant_id AND dr.revision_id=rc.revision_id
            JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
            WHERE rc.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
              AND d.status='indexed' AND d.indexed_version>0
              AND strpos(lower(regexp_replace(rc.text_content, '[[:space:]]+', ' ', 'g')),
                         lower(regexp_replace(@literal, '[[:space:]]+', ' ', 'g'))) > 0
            ORDER BY rc.page_start, rc.page_end, rc.chunk_index, rc.retrieval_chunk_id
            LIMIT @row_limit OFFSET @row_offset;
            """;
        var references = (await connection.QueryAsync<AdvancedAnalysisEvidenceReference>(new CommandDefinition(sql,
            new { scope.tenant, scope.doc_id, scope.revision_id, literal = request.Query.Trim(),
                row_limit = request.TopK + 1, row_offset = request.Offset },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        if (references.Any(reference =>
                !string.Equals(NormalizePath(reference.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal)
                || !string.Equals(reference.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase)))
            throw new AdvancedAnalysisToolException("canonical_find_source_identity_changed");
        var more = references.Length > request.TopK;
        var returned = references.Take(request.TopK).ToArray();
        var nextOffset = request.Offset + request.TopK;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (returned, new AdvancedAnalysisFindDiagnostic(
            more ? nextOffset <= 10_000 ? "canonical_text_matches_more_available" : "canonical_text_matches_pagination_limit_reached"
                : returned.Length == 0 ? "no_literal_match_at_offset_in_observed_revision" : "canonical_text_matches_returned",
            returned.Length, more && nextOffset <= 10_000 ? nextOffset : null));
    }

    private sealed record CanonicalFindSourceIdentity(string SourceHash, string DocPath);
}
