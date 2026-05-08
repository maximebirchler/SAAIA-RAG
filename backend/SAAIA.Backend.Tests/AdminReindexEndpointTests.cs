using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminReindexEndpointTests
{
    [Fact]
    public async Task ReindexAsync_rejects_unscoped_full_catalog_scan_without_force_flag()
    {
        var ctx = BuildAdminContext();

        var result = await AdminEndpoints.ReindexAsync(
            ctx,
            CreateUnusedDataSource(),
            Options.Create(new IngestionOptions { DocumentsRoot = "C:/definitely/missing/saaia-documents-root" }),
            new ReindexRequest());

        var payload = await ExecuteResultAsync<JsonElement>(result, ctx);
        Assert.Equal("whole_catalog_reindex_requires_force", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReindexAsync_allows_explicit_full_catalog_force_before_root_validation()
    {
        var ctx = BuildAdminContext();

        var result = await AdminEndpoints.ReindexAsync(
            ctx,
            CreateUnusedDataSource(),
            Options.Create(new IngestionOptions { DocumentsRoot = "C:/definitely/missing/saaia-documents-root" }),
            new ReindexRequest(ForceWholeCatalog: true));

        var payload = await ExecuteResultAsync<JsonElement>(result, ctx);
        Assert.Equal("DocumentsRoot not found", payload.GetProperty("error").GetString());
    }

    private static DefaultHttpContext BuildAdminContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return ctx;
    }

    private static NpgsqlDataSource CreateUnusedDataSource()
        => NpgsqlDataSource.Create("Host=localhost;Port=1;Username=unused;Password=unused;Database=unused;Timeout=1;Command Timeout=1");

    private static async Task<T> ExecuteResultAsync<T>(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<T>(ctx.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }
}
