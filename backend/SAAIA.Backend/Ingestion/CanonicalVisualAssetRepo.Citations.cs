using Dapper;
using Npgsql;

internal sealed record CanonicalVisualAssetPage(
    bool ScopeExists,
    long? TotalCount,
    IReadOnlyList<CanonicalVisualAssetDescriptor>? Assets);

internal static partial class CanonicalVisualAssetRepo
{
    // Metadata only. No PNG payload is selected or passed to a provider.
    internal static async Task<CanonicalVisualAssetPage> ListCitationsAsync(
        NpgsqlConnection connection,
        Guid tenant,
        Guid document,
        Guid revision,
        string sourceSha,
        int first,
        int last,
        int offset,
        int limit,
        CancellationToken ct)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty
            || sourceSha.Length != 64 || !sourceSha.All(char.IsAsciiHexDigit)
            || first <= 0 || last < first || last - first >= 4
            || offset is < 0 or > MaximumDocumentAssets || limit is < 1 or > 64)
            throw new ArgumentException("canonical_visual_citation_scope_invalid");

        var scope = new
        {
            tenant, document, revision, source = Convert.FromHexString(sourceSha), first, last, offset, limit
        };
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
                SELECT 1 FROM document_revisions dr
                JOIN documents d ON d.doc_id=dr.doc_id AND d.tenant_id=dr.tenant_id
                WHERE dr.tenant_id=@tenant AND dr.doc_id=@document AND dr.revision_id=@revision
                    AND dr.source_hash=@source AND d.indexed_version>0 AND d.status<>'deleted');
            """, scope, cancellationToken: ct));
        if (!exists)
            return new CanonicalVisualAssetPage(false, null, null);

        const string pages = " AND dr.source_hash=@source AND a.page_number>=@first AND a.page_number<=@last";
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) " + ReadScope + pages, scope, cancellationToken: ct));
        if (total > MaximumDocumentAssets)
            throw new InvalidDataException("canonical_visual_asset_document_budget_exceeded");

        var rows = await connection.QueryAsync<AssetRow>(new CommandDefinition(
            "SELECT " + MetadataColumns + " " + ReadScope + pages
                + " ORDER BY a.page_number,a.asset_id LIMIT @limit OFFSET @offset;",
            scope, cancellationToken: ct));
        return new CanonicalVisualAssetPage(true, total, rows.Select(Descriptor).ToArray());
    }
}
