using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentsTreeCacheContractTests
{
    [Fact]
    public async Task BuildTreeResponse_returns_304_when_if_none_match_matches_tree_etag()
    {
        var root = BuildTreeFromDocPaths(
        [
            "ATEX/CEN TR 15281.pdf",
            "Programmation/Mettler/MettlerToledo_IND570.pdf"
        ]);

        var firstContext = CreateHttpContext();
        var firstResult = BuildTreeResponse(firstContext, format: "json", maxDepth: null, path: "", source: "documents", root);

        await firstResult.ExecuteAsync(firstContext);

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);

        var firstBody = ReadResponseBody(firstContext);
        Assert.Contains("\"source\":\"documents\"", firstBody, StringComparison.Ordinal);

        var secondContext = CreateHttpContext();
        secondContext.Request.Headers.IfNoneMatch = etag;

        var secondResult = BuildTreeResponse(secondContext, format: "json", maxDepth: null, path: "", source: "documents", root);

        await secondResult.ExecuteAsync(secondContext);

        Assert.Equal(etag, secondContext.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext));
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static object BuildTreeFromDocPaths(IEnumerable<string> docPaths)
    {
        var method = typeof(DocumentsEndpoints).GetMethod(
            "BuildTreeFromDocPaths",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return method!.Invoke(null, [docPaths])!;
    }

    private static IResult BuildTreeResponse(
        HttpContext context,
        string format,
        int? maxDepth,
        string path,
        string source,
        object root)
    {
        var method = typeof(DocumentsEndpoints).GetMethod(
            "BuildTreeResponse",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (IResult)method!.Invoke(null, [context, format, maxDepth, path, source, root])!;
    }
}
