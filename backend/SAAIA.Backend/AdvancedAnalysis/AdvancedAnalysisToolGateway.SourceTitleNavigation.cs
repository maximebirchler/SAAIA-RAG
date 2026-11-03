using System.Text.Json;
using Dapper;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisSourceTitle(string Title, string Kind, int? SourcePage,
    int? PhysicalPageStart, int? PhysicalPageEnd);

internal sealed record AdvancedAnalysisSourceTitlePage(int Offset, int? NextOffset,
    IReadOnlyList<AdvancedAnalysisSourceTitle> Items, bool OffsetLimitReached, int OmittedOversizedLocators = 0)
{
    internal const string Operation = "list_source_titles";
    internal const string Query = "Canonical contents and title anchors";
    internal const int MaximumOffset = 10_000;
    internal const int MaximumLimit = 20;
    internal const int MaximumPagesPerBatch = 4;
    internal const int MaximumTitleFilterCharacters = 128;
    internal const int MaximumPageCharacters = 2_048;
    private static readonly JsonSerializerOptions BudgetJson = new(JsonSerializerDefaults.Web);

    internal static bool IsValidWindow(int offset, int limit)
        => offset is >= 0 and <= MaximumOffset && limit is >= 1 and <= MaximumLimit;

    internal static AdvancedAnalysisSourceTitlePage Fit(int offset, int limit, IReadOnlyList<AdvancedAnalysisSourceTitle> fetched)
    {
        for (var count = Math.Min(limit, fetched.Count); count >= 0; count--)
        {
            var next = offset + count;
            var more = fetched.Count > count;
            var page = new AdvancedAnalysisSourceTitlePage(offset,
                more && next <= MaximumOffset ? next : null, fetched.Take(count).ToArray(), more && next > MaximumOffset);
            // Budget the complete serialized page under conservative double JSON
            // escaping. A cursor only advances over whole returned locators.
            if ((count > 0 || !more)
                && JsonSerializer.Serialize(JsonSerializer.Serialize(page, BudgetJson), BudgetJson).Length <= MaximumPageCharacters)
                return page;
        }
        // A single exceptionally long row must not trap browsing at one offset.
        // Omit only the blocking whole row and explicitly report the omission.
        var afterOmission = offset + 1;
        return new(offset, fetched.Count > 1 && afterOmission <= MaximumOffset ? afterOmission : null,
            [], fetched.Count > 1 && afterOmission > MaximumOffset, OmittedOversizedLocators: 1);
    }
}

internal sealed partial class AdvancedAnalysisToolGateway
{
    private async Task<AdvancedAnalysisSourceTitlePage> ListCanonicalSourceTitlesAsync(
        AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
    {
        var observed = _evidence.First(item =>
            string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase));
        var scope = new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!), revision_id = Guid.Parse(request.RevisionId!) };
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var identity = await connection.QuerySingleOrDefaultAsync<CanonicalReadSourceIdentity>(
            new CommandDefinition("""
                SELECT encode(dr.source_hash, 'hex') AS "SourceHash", d.doc_path AS "DocPath"
                FROM document_revisions dr
                JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
                WHERE dr.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
                  AND d.status='indexed' AND d.indexed_version>0 AND dr.indexed_version=d.indexed_version;
                """, scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (identity is null || string.IsNullOrWhiteSpace(identity.SourceHash)
            || !string.Equals(identity.SourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizePath(identity.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal))
            throw new AdvancedAnalysisToolException("canonical_title_navigation_identity_changed");

        // Same index data as /documents/navigation. Do not project body text,
        // interpretation, internal row IDs, citation handles or unresolved pages
        // as target coordinates. The revision check and rows share a snapshot.
        const string sql = """
            WITH locators AS (
                SELECT label AS title, 'contents' AS kind, source_page,
                       target_page_start AS page_start, target_page_end AS page_end,
                       entry_index AS ordinal, navigation_entry_id AS id
                FROM document_navigation_entries
                WHERE tenant_id=@tenant AND doc_id=@doc_id AND revision_id=@revision_id
                  AND NULLIF(BTRIM(label), '') IS NOT NULL
                UNION ALL
                SELECT title, 'title_anchor', NULL::int, page_start, page_end,
                       anchor_index, title_anchor_id
                FROM document_title_anchors
                WHERE tenant_id=@tenant AND doc_id=@doc_id AND revision_id=@revision_id
                  AND NULLIF(BTRIM(title), '') IS NOT NULL
            ), distinct_locators AS (
                SELECT DISTINCT ON (lower(btrim(title)), page_start, page_end,
                    CASE WHEN page_start IS NULL THEN source_page END) *
                FROM locators l
                WHERE (@title_filter IS NULL OR strpos(lower(title), lower(@title_filter)) > 0)
                  AND (@page_first IS NULL OR COALESCE(page_end, page_start, source_page) >= @page_first)
                  AND (@page_last IS NULL OR COALESCE(page_start, source_page) <= @page_last)
                  AND (NOT @card_only OR EXISTS (
                      SELECT 1 FROM document_profile_content_cards c
                      WHERE c.tenant_id=@tenant AND c.doc_id=@doc_id AND c.revision_id=@revision_id
                        AND lower(btrim(c.title)) = lower(btrim(l.title))
                        AND c.page_start <= COALESCE(l.page_end, l.page_start)
                        AND COALESCE(c.page_end, c.page_start) >= l.page_start
                        AND saaia_is_safe_profile_content_card(c.kind, c.title, c.page_start, c.page_end, c.metadata)
                  ))
                ORDER BY lower(btrim(title)), page_start, page_end,
                    CASE WHEN page_start IS NULL THEN source_page END, kind DESC, ordinal, id
            )
            SELECT title AS "Title", kind AS "Kind", source_page AS "SourcePage",
                   page_start AS "PhysicalPageStart", page_end AS "PhysicalPageEnd"
            FROM distinct_locators
            ORDER BY COALESCE(page_start, source_page, 2147483647), kind, ordinal, id
            LIMIT @row_limit OFFSET @row_offset;
            """;
        var rows = (await connection.QueryAsync<AdvancedAnalysisSourceTitle>(new CommandDefinition(sql,
            new { scope.tenant, scope.doc_id, scope.revision_id, row_limit = request.TopK + 1, row_offset = request.Offset,
                title_filter = request.TitleContains, page_first = request.PageStart, page_last = request.PageEnd,
                card_only = request.CardBackedTitlesOnly },
            transaction, cancellationToken: cancellationToken))).ToArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AdvancedAnalysisSourceTitlePage.Fit(request.Offset, request.TopK, rows);
    }
}
