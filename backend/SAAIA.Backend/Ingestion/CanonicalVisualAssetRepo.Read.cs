using System.Security.Cryptography;
using Dapper;
using Npgsql;

internal sealed record CanonicalVisualAssetDescriptor(string AssetId, int PageNumber, string? FigureId,
    string SourceSha256, string ContentSha256, string MediaType, int PixelWidth, int PixelHeight, long SizeBytes);
internal sealed record CanonicalVisualAssetContent(CanonicalVisualAssetDescriptor Descriptor, byte[] Bytes);
internal sealed record CanonicalVisualAssetInventory(string Status, string InterpretationStatus,
    bool ImageToolAvailable, long AvailableAssetCount, IReadOnlyList<CanonicalVisualAssetDescriptor> Assets,
    bool AssetsOmitted);

internal static partial class CanonicalVisualAssetRepo
{
    private const string ReadScope = """
        FROM document_revision_visual_assets a
        JOIN document_revisions dr ON dr.revision_id=a.revision_id AND dr.tenant_id=a.tenant_id AND dr.source_hash=a.source_hash
        JOIN documents d ON d.doc_id=dr.doc_id AND d.tenant_id=dr.tenant_id
        WHERE a.tenant_id=@tenant AND a.revision_id=@revision AND dr.doc_id=@document
          AND d.indexed_version>0 AND d.status<>'deleted'
        """;
    private const string MetadataColumns = """
        a.asset_id AS "AssetId",a.page_number AS "PageNumber",a.figure_id AS "FigureId",
        encode(a.source_hash,'hex') AS "SourceSha256",encode(a.content_hash,'hex') AS "ContentSha256",
        a.media_type AS "MediaType",a.pixel_width AS "PixelWidth",a.pixel_height AS "PixelHeight",
        octet_length(a.payload)::bigint AS "SizeBytes"
        """;

    internal static async Task<CanonicalVisualAssetInventory> ListAsync(NpgsqlDataSource dataSource,
        Guid tenant, Guid document, Guid revision, int? pageStart, int? pageEnd, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ListAsync(connection, tenant, document, revision, pageStart, pageEnd, ct);
    }

    internal static async Task<CanonicalVisualAssetInventory> ListAsync(NpgsqlConnection connection,
        Guid tenant, Guid document, Guid revision, int? pageStart, int? pageEnd, CancellationToken ct)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty
            || pageStart is <= 0 || pageEnd is <= 0 || pageStart > pageEnd)
            throw new ArgumentException("canonical_visual_asset_scope_invalid");
        var scope = new { tenant, document, revision, first = pageStart, last = pageEnd };
        const string pages = " AND (@first::int IS NULL OR a.page_number>=@first) AND (@last::int IS NULL OR a.page_number<=@last)";
        var rows = (await connection.QueryAsync<AssetRow>(new CommandDefinition(
            "SELECT " + MetadataColumns + ",COUNT(*) OVER() AS \"TotalCount\" " + ReadScope + pages + " ORDER BY a.page_number,a.asset_id LIMIT 16;",
            scope, cancellationToken: ct))).ToArray();
        var total = rows.FirstOrDefault()?.TotalCount ?? 0;
        return new(total > 0 ? "pixels_available" : "pixels_not_available", "not_performed", false, total,
            rows.Select(Descriptor).ToArray(), total > rows.Length);
    }

    internal static async Task<CanonicalVisualAssetContent?> ReadAsync(NpgsqlDataSource dataSource,
        Guid tenant, Guid document, Guid revision, string assetId, CancellationToken ct)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty || !IsAssetId(assetId)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            "SELECT " + MetadataColumns + ",a.payload AS \"Payload\" " + ReadScope + " AND a.asset_id=@asset;",
            new { tenant, document, revision, asset = assetId }, cancellationToken: ct));
        if (row is null) return null;
        var png = CanonicalPngAsset.Validate(row.Payload);
        if (!string.Equals(row.ContentSha256, Convert.ToHexString(SHA256.HashData(row.Payload)), StringComparison.OrdinalIgnoreCase)
            || row.MediaType != "image/png" || row.PixelWidth != png.Width || row.PixelHeight != png.Height)
            throw new InvalidDataException("canonical_visual_asset_stored_integrity_failed");
        return new(Descriptor(row), row.Payload);
    }

    private static CanonicalVisualAssetDescriptor Descriptor(AssetRow row) => new(row.AssetId,row.PageNumber,
        row.FigureId,row.SourceSha256,row.ContentSha256,row.MediaType,row.PixelWidth,row.PixelHeight,row.SizeBytes);

    private static bool IsAssetId(string id) => id.Length is > 0 and <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private sealed class AssetRow
    {
        public string AssetId { get; init; } = "";
        public int PageNumber { get; init; }
        public string? FigureId { get; init; }
        public string SourceSha256 { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public string MediaType { get; init; } = "";
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
        public long SizeBytes { get; init; }
        public byte[] Payload { get; init; } = [];
        public long TotalCount { get; init; }
    }
}
