using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagAdminDiagnosticsEndpointTests
{
    [Fact]
    public void Map_exposes_admin_test_retrieval_endpoint_with_admin_guard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddHttpClient();
        builder.Services.Configure<RagOptions>(_ => { });
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Username=saaia;Password=saaia;Database=saaia"));
        builder.Services.AddSingleton<RagSearchBulkhead>();
        builder.Services.AddSingleton<TeiWorkloadGovernor>();
        var app = builder.Build();

        RagEndpoints.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        var endpoint = Assert.Single(
            endpoints,
            route => string.Equals(route.RoutePattern.RawText, "/admin/rag/test-retrieval", StringComparison.Ordinal));

        Assert.Contains(endpoint.Metadata, metadata => metadata is RequireAdminKeyMetadata);
    }
}
