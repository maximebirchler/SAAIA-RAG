using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

internal static class VisualAssetEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/documents/{docId:guid}/revisions/{revisionId:guid}/assets", ListAsync);
        app.MapGet("/documents/{docId:guid}/revisions/{revisionId:guid}/assets/{assetId}", ReadAsync);
    }

    internal static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource dataSource,
        Guid docId, Guid revisionId, int? pageStart, int? pageEnd, ILoggerFactory logs)
    {
        using var probe = BackendDiagnostics.Start("documents.visual_assets.list", logs.CreateLogger("VisualAssets"));
        if (docId == Guid.Empty || revisionId == Guid.Empty || pageStart is <= 0 || pageEnd is <= 0 || pageStart > pageEnd)
        {
            probe.Complete("rejected", "invalid_visual_scope");
            return Results.BadRequest(new { error = "invalid_visual_scope" });
        }
        try
        {
        var assets = await CanonicalVisualAssetRepo.ListAsync(dataSource, context.GetTenantId(), docId, revisionId,
            pageStart, pageEnd, context.RequestAborted);
        probe.Set("available_assets", assets.AvailableAssetCount);
        probe.Set("returned_assets", assets.Assets.Count);
        probe.Set("assets_omitted", assets.AssetsOmitted);
        probe.Complete();
        return Results.Ok(assets);
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }

    internal static async Task<IResult> ReadAsync(HttpContext context, NpgsqlDataSource dataSource,
        Guid docId, Guid revisionId, string assetId, ILoggerFactory logs)
    {
        using var probe = BackendDiagnostics.Start("documents.visual_assets.read", logs.CreateLogger("VisualAssets"));
        try
        {
        var asset = await CanonicalVisualAssetRepo.ReadAsync(dataSource, context.GetTenantId(), docId, revisionId,
            assetId, context.RequestAborted);
        if (asset is null) { probe.Complete("not_found"); return Results.NotFound(); }
        probe.Set("asset_bytes", asset.Bytes.Length);
        probe.Set("pixel_width", asset.Descriptor.PixelWidth);
        probe.Set("pixel_height", asset.Descriptor.PixelHeight);
        context.Response.Headers.ETag = '"' + asset.Descriptor.ContentSha256 + '"';
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        probe.Complete();
        return Results.Bytes(asset.Bytes, "image/png");
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }
}
