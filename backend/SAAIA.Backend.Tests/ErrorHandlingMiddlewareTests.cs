using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ErrorHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_preserves_existing_json_error_body()
    {
        var middleware = new ErrorHandlingMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"capability_not_authorized","capabilityKey":"capability_b.backoffice_generation"}""");
            },
            NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = BuildContext();

        await middleware.InvokeAsync(ctx);

        var body = await ReadBodyAsync(ctx);
        Assert.Contains("capability_not_authorized", body, StringComparison.Ordinal);
        Assert.Contains("capability_b.backoffice_generation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bad request", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_formats_bare_error_status()
    {
        var middleware = new ErrorHandlingMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = BuildContext();

        await middleware.InvokeAsync(ctx);

        var body = await ReadBodyAsync(ctx);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Contains("Not found", body, StringComparison.Ordinal);
        Assert.Contains("requestId", body, StringComparison.Ordinal);
    }

    private static DefaultHttpContext BuildContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
