using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    private static Task<IResult> CatalogStatsAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, string? path, string? categoryRef)
        => StatsAsync(ctx, ds, ingestOpt, path, categoryRef);

    private static string BuildCategoryRef(int displayOrder)
        => $"cat_{displayOrder:000}";

    private static string BuildDocumentRef(Guid docId)
        => $"doc_{docId:N}";

    private static string BuildSnapshotId(DateTimeOffset computedAtUtc, long totalDocs)
        => $"snap_{computedAtUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}_{Math.Abs(HashCode.Combine(computedAtUtc.UtcTicks, totalDocs)):x8}";

    private static string BuildSnapshotEtag(DateTimeOffset computedAtUtc, long totalDocs, int categoryCount)
        => $"\"cat-{computedAtUtc.UtcTicks:x}-{totalDocs:x}-{categoryCount:x}\"";

    private static string BuildAbsoluteNextLink(HttpContext ctx, string path, IReadOnlyDictionary<string, string?> query)
    {
        var request = ctx.Request;
        var builder = new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1, path);
        var parts = new List<string>();
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeCatalogOrderBy(string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
            return "name_asc";

        return orderBy.Trim().ToLowerInvariant() switch
        {
            "name_desc" => "name_desc",
            "updatedat_desc" => "updatedAt_desc",
            "updatedat_asc" => "updatedAt_asc",
            _ => "name_asc"
        };
    }

    private static bool CursorMatches(string? cursorValue, string? requestValue)
        => string.IsNullOrWhiteSpace(requestValue) || string.Equals(cursorValue ?? string.Empty, requestValue ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool CursorMatches(int cursorValue, int requestValue)
        => requestValue <= 0 || cursorValue == requestValue;

    private static JsonElement ParseJsonOrEmptyObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.Clone();
        }
        catch { }

        using (var doc = JsonDocument.Parse("{}"))
            return doc.RootElement.Clone();
    }

    private static JsonElement ParseJsonOrEmptyArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse("[]");
            return doc.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.Clone();
        }
        catch { }

        using (var doc = JsonDocument.Parse("[]"))
            return doc.RootElement.Clone();
    }
}
