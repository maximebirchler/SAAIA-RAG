using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed class AdvancedAnalysisEvidenceResolver
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly AdvancedAnalysisOptions _options;

    public AdvancedAnalysisEvidenceResolver(
        NpgsqlDataSource dataSource,
        IOptions<AdvancedAnalysisOptions> options)
    {
        _dataSource = dataSource;
        _options = options.Value;
    }

    public async Task<AdvancedAnalysisEvidenceResolution> ResolveAsync(
        Guid tenantId,
        IReadOnlyList<AdvancedAnalysisEvidenceReference> references,
        CancellationToken cancellationToken)
    {
        var maximumPerItem = Math.Clamp(
            _options.MaximumEvidenceCharactersPerItem,
            1_000,
            200_000);
        var maximumTotal = Math.Clamp(
            _options.MaximumEvidenceCharactersTotal,
            maximumPerItem,
            2_000_000);
        var resolved = new List<AdvancedAnalysisResolvedEvidence>(references.Count);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var reference in references)
        {
            var row = await ResolveOneAsync(
                connection,
                tenantId,
                reference,
                cancellationToken).ConfigureAwait(false);
            if (row is null || !MatchesExpectedIdentity(reference, row))
            {
                return AdvancedAnalysisEvidenceResolution.Invalid(
                    "evidence_revalidation_failed");
            }

            if (string.IsNullOrWhiteSpace(row.Content)
                || row.Content.Length > maximumPerItem)
            {
                return AdvancedAnalysisEvidenceResolution.Invalid(
                    "evidence_content_limit_exceeded");
            }

            totalCharacters += row.Content.Length;
            if (totalCharacters > maximumTotal)
            {
                return AdvancedAnalysisEvidenceResolution.Invalid(
                    "evidence_total_limit_exceeded");
            }

            var evidenceId = string.IsNullOrWhiteSpace(reference.EvidenceId)
                ? BuildEvidenceId(row)
                : reference.EvidenceId.Trim();
            if (evidenceId.Length > 200 || !evidenceIds.Add(evidenceId))
            {
                return AdvancedAnalysisEvidenceResolution.Invalid(
                    "evidence_identity_duplicate");
            }

            resolved.Add(new AdvancedAnalysisResolvedEvidence(
                new AdvancedAnalysisResultEvidence
                {
                    EvidenceId = evidenceId,
                    DocId = row.DocId.ToString("D"),
                    RevisionId = row.RevisionId.ToString("D"),
                    FileName = row.FileName,
                    DocPath = row.DocPath,
                    SourceHash = row.SourceHash,
                    PageStart = row.PageStart,
                    PageEnd = row.PageEnd,
                    ChunkId = NullIfBlank(row.ChunkId),
                    AnchorId = NullIfBlank(row.AnchorId),
                    ContentCardId = NullIfBlank(row.ContentCardId)
                },
                row.Content));
        }

        return AdvancedAnalysisEvidenceResolution.Valid(resolved);
    }

    private static async Task<ResolvedEvidenceRow?> ResolveOneAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        AdvancedAnalysisEvidenceReference reference,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference.ChunkId, out var chunkId))
            return await ResolveChunkAsync(connection, tenantId, chunkId, cancellationToken);
        if (Guid.TryParse(reference.ContentCardId, out var contentCardId))
            return await ResolveContentCardAsync(
                connection,
                tenantId,
                contentCardId,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(reference.AnchorId))
            return await ResolveAnchorAsync(
                connection,
                tenantId,
                reference.AnchorId.Trim(),
                cancellationToken);
        if (Guid.TryParse(reference.RevisionId, out var revisionId)
            && reference.PageStart > 0
            && reference.PageEnd >= reference.PageStart)
        {
            return await ResolveRevisionPagesAsync(
                connection,
                tenantId,
                revisionId,
                reference.PageStart,
                reference.PageEnd,
                cancellationToken);
        }

        return null;
    }

    private static Task<ResolvedEvidenceRow?> ResolveChunkAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid chunkId,
        CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<ResolvedEvidenceRow>(
            new CommandDefinition(
                """
                SELECT
                  d.doc_id AS "DocId",
                  dr.revision_id AS "RevisionId",
                  d.doc_name AS "FileName",
                  d.doc_path AS "DocPath",
                  encode(dr.source_hash, 'hex') AS "SourceHash",
                  rc.page_start AS "PageStart",
                  rc.page_end AS "PageEnd",
                  rc.retrieval_chunk_id::text AS "ChunkId",
                  sa.anchor_id AS "AnchorId",
                  NULL::text AS "ContentCardId",
                  rc.text_content AS "Content"
                FROM retrieval_chunks rc
                JOIN document_revisions dr
                  ON dr.tenant_id=rc.tenant_id
                 AND dr.revision_id=rc.revision_id
                JOIN documents d
                  ON d.tenant_id=dr.tenant_id
                 AND d.doc_id=dr.doc_id
                LEFT JOIN document_source_anchors sa
                  ON sa.tenant_id=rc.tenant_id
                 AND sa.revision_id=rc.revision_id
                 AND sa.projection_type='retrieval_chunk'
                 AND sa.projection_id=rc.retrieval_chunk_id::text
                WHERE rc.tenant_id=@tenant
                  AND rc.retrieval_chunk_id=@chunk_id
                LIMIT 1;
                """,
                new { tenant = tenantId, chunk_id = chunkId },
                cancellationToken: cancellationToken));

    private static Task<ResolvedEvidenceRow?> ResolveContentCardAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid contentCardId,
        CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<ResolvedEvidenceRow>(
            new CommandDefinition(
                """
                SELECT
                  d.doc_id AS "DocId",
                  dr.revision_id AS "RevisionId",
                  d.doc_name AS "FileName",
                  d.doc_path AS "DocPath",
                  encode(dr.source_hash, 'hex') AS "SourceHash",
                  COALESCE(cc.page_start, 1) AS "PageStart",
                  COALESCE(cc.page_end, cc.page_start, 1) AS "PageEnd",
                  NULL::text AS "ChunkId",
                  NULL::text AS "AnchorId",
                  cc.content_card_id::text AS "ContentCardId",
                  cc.search_text AS "Content"
                FROM document_profile_content_cards cc
                JOIN document_revisions dr
                  ON dr.tenant_id=cc.tenant_id
                 AND dr.revision_id=cc.revision_id
                JOIN documents d
                  ON d.tenant_id=dr.tenant_id
                 AND d.doc_id=dr.doc_id
                WHERE cc.tenant_id=@tenant
                  AND cc.content_card_id=@content_card_id
                LIMIT 1;
                """,
                new { tenant = tenantId, content_card_id = contentCardId },
                cancellationToken: cancellationToken));

    private static async Task<ResolvedEvidenceRow?> ResolveAnchorAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string anchorId,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<ResolvedEvidenceRow>(
            new CommandDefinition(
                """
                SELECT
                  d.doc_id AS "DocId",
                  dr.revision_id AS "RevisionId",
                  d.doc_name AS "FileName",
                  d.doc_path AS "DocPath",
                  encode(dr.source_hash, 'hex') AS "SourceHash",
                  sa.page_start AS "PageStart",
                  sa.page_end AS "PageEnd",
                  rc.retrieval_chunk_id::text AS "ChunkId",
                  sa.anchor_id AS "AnchorId",
                  cc.content_card_id::text AS "ContentCardId",
                  COALESCE(rc.text_content, cc.search_text) AS "Content"
                FROM document_source_anchors sa
                JOIN document_revisions dr
                  ON dr.tenant_id=sa.tenant_id
                 AND dr.revision_id=sa.revision_id
                JOIN documents d
                  ON d.tenant_id=dr.tenant_id
                 AND d.doc_id=dr.doc_id
                LEFT JOIN retrieval_chunks rc
                  ON sa.projection_type='retrieval_chunk'
                 AND rc.tenant_id=sa.tenant_id
                 AND rc.revision_id=sa.revision_id
                 AND rc.retrieval_chunk_id::text=sa.projection_id
                LEFT JOIN document_profile_content_cards cc
                  ON sa.projection_type='content_card'
                 AND cc.tenant_id=sa.tenant_id
                 AND cc.revision_id=sa.revision_id
                 AND cc.content_card_id::text=sa.projection_id
                WHERE sa.tenant_id=@tenant
                  AND sa.anchor_id=@anchor_id
                LIMIT 2;
                """,
                new { tenant = tenantId, anchor_id = anchorId },
                cancellationToken: cancellationToken))).AsList();
        return rows.Count == 1 ? rows[0] : null;
    }

    private static async Task<ResolvedEvidenceRow?> ResolveRevisionPagesAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid revisionId,
        int pageStart,
        int pageEnd,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<ResolvedEvidenceRow>(
            new CommandDefinition(
                """
                SELECT
                  d.doc_id AS "DocId",
                  dr.revision_id AS "RevisionId",
                  d.doc_name AS "FileName",
                  d.doc_path AS "DocPath",
                  encode(dr.source_hash, 'hex') AS "SourceHash",
                  rc.page_start AS "PageStart",
                  rc.page_end AS "PageEnd",
                  rc.retrieval_chunk_id::text AS "ChunkId",
                  sa.anchor_id AS "AnchorId",
                  NULL::text AS "ContentCardId",
                  rc.text_content AS "Content"
                FROM document_revisions dr
                JOIN documents d
                  ON d.tenant_id=dr.tenant_id
                 AND d.doc_id=dr.doc_id
                JOIN retrieval_chunks rc
                  ON rc.tenant_id=dr.tenant_id
                 AND rc.revision_id=dr.revision_id
                LEFT JOIN document_source_anchors sa
                  ON sa.tenant_id=rc.tenant_id
                 AND sa.revision_id=rc.revision_id
                 AND sa.projection_type='retrieval_chunk'
                 AND sa.projection_id=rc.retrieval_chunk_id::text
                WHERE dr.tenant_id=@tenant
                  AND dr.revision_id=@revision_id
                  AND rc.page_end >= @page_start
                  AND rc.page_start <= @page_end
                ORDER BY rc.chunk_index;
                """,
                new
                {
                    tenant = tenantId,
                    revision_id = revisionId,
                    page_start = pageStart,
                    page_end = pageEnd
                },
                cancellationToken: cancellationToken))).AsList();
        if (rows.Count == 0)
            return null;

        var first = rows[0];
        return first with
        {
            PageStart = pageStart,
            PageEnd = pageEnd,
            ChunkId = null,
            AnchorId = null,
            Content = string.Join(
                "\n\n",
                rows.Select(static row => row.Content)
                    .Where(static content => !string.IsNullOrWhiteSpace(content)))
        };
    }

    private static bool MatchesExpectedIdentity(
        AdvancedAnalysisEvidenceReference expected,
        ResolvedEvidenceRow actual)
    {
        if (!MatchesGuid(expected.DocId, actual.DocId)
            || !MatchesGuid(expected.RevisionId, actual.RevisionId)
            || !MatchesText(expected.FileName, actual.FileName)
            || !MatchesText(expected.DocPath, actual.DocPath)
            || !MatchesHash(expected.SourceHash, actual.SourceHash)
            || !MatchesText(expected.ChunkId, actual.ChunkId)
            || !MatchesText(expected.AnchorId, actual.AnchorId)
            || !MatchesText(expected.ContentCardId, actual.ContentCardId))
        {
            return false;
        }

        return (expected.PageStart <= 0 || expected.PageStart == actual.PageStart)
               && (expected.PageEnd <= 0 || expected.PageEnd == actual.PageEnd);
    }

    private static bool MatchesGuid(string? expected, Guid actual)
        => string.IsNullOrWhiteSpace(expected)
           || (Guid.TryParse(expected, out var parsed) && parsed == actual);

    private static bool MatchesText(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected)
           || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesHash(string? expected, string actual)
        => string.IsNullOrWhiteSpace(expected)
           || string.Equals(
               NormalizeHash(expected),
               NormalizeHash(actual),
               StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string value)
        => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    private static string BuildEvidenceId(ResolvedEvidenceRow row)
    {
        var identity = string.Join(
            "|",
            row.DocId.ToString("D"),
            row.RevisionId.ToString("D"),
            row.ChunkId ?? string.Empty,
            row.AnchorId ?? string.Empty,
            row.ContentCardId ?? string.Empty,
            row.PageStart,
            row.PageEnd,
            row.SourceHash);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"advanced-evidence-{hash[..32]}";
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ResolvedEvidenceRow
    {
        public Guid DocId { get; init; }
        public Guid RevisionId { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string DocPath { get; init; } = string.Empty;
        public string SourceHash { get; init; } = string.Empty;
        public int PageStart { get; init; }
        public int PageEnd { get; init; }
        public string? ChunkId { get; init; }
        public string? AnchorId { get; init; }
        public string? ContentCardId { get; init; }
        public string Content { get; init; } = string.Empty;
    }
}

internal sealed record AdvancedAnalysisEvidenceResolution(
    bool IsValid,
    string? ErrorCode,
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence)
{
    public static AdvancedAnalysisEvidenceResolution Valid(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
        => new(true, null, evidence);

    public static AdvancedAnalysisEvidenceResolution Invalid(string errorCode)
        => new(false, errorCode, []);
}
