using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static class SourcesEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/sources/resolve", ResolveSourceAsync);
    }

    private static async Task<IResult> ResolveSourceAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        SourceResolveRequest request)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        var raw = (request.Ref ?? request.PdfRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Results.Ok(new SourceResolveResponse
            {
                Error = "missing_source_ref",
                RequestedRef = raw
            });
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        var source = TryParseDocumentRef(raw, out var docId)
            ? await ResolvedSourceProjection.ResolveByDocIdAsync(conn, tenantId, docId, ct)
            : await ResolvedSourceProjection.ResolveByRefAsync(conn, tenantId, raw, ct);
        if (source is null)
        {
            return Results.Ok(new SourceResolveResponse
            {
                Error = "source_not_found",
                RequestedRef = raw
            });
        }

        return Results.Ok(new SourceResolveResponse
        {
            RequestedRef = raw,
            Source = source
        });
    }

    private static bool TryParseDocumentRef(string raw, out Guid docId)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith("doc_", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        return Guid.TryParse(value, out docId);
    }
}
