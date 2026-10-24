using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Backend.Middleware;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class BackendDiagnosticsTests
{
    [Theory]
    [InlineData("search_corpus", "source_backed_canonical_search")]
    [InlineData("read_source", "source_backed_canonical_read")]
    [InlineData("find_source_text", "source_backed_canonical_find")]
    public void Tool_history_names_describe_the_operation_actually_executed(string operation, string expected)
        => Assert.Equal(expected, SAAIA.Backend.AdvancedAnalysis.AdvancedAnalysisJobStore.ResolveToolName(operation));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Operation_emits_exactly_one_correlated_terminal_record_on_success_or_failure(bool fail)
    {
        var logger = new DiagnosticRecordingLogger<BackendDiagnosticsTests>();
        var parent = ActivityContext.Parse("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", null);
        var operation = BackendDiagnostics.Start("test.operation", logger, parent);
        operation.Set("candidate_count", 12);
        if (fail) operation.Fail(new IOException("sensitive-error-message"), "test_failed");
        else operation.Complete();
        operation.Dispose(); operation.Dispose();
        var entry = Assert.Single(logger.Events);
        Assert.Equal("0123456789abcdef0123456789abcdef", entry.Values["TraceId"]);
        Assert.Equal(fail ? "failed" : "success", entry.Values["Outcome"]);
        Assert.True((double)entry.Values["DurationMs"]! >= 0);
        Assert.DoesNotContain("sensitive-error-message", entry.Text);
        using var fields = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        Assert.Equal(12, fields.RootElement.GetProperty("candidate_count").GetInt32());
    }

    [Fact]
    public void An_unfinished_operation_never_claims_success()
    {
        var logger = new DiagnosticRecordingLogger<BackendDiagnosticsTests>();
        using (BackendDiagnostics.Start("test.unfinished", logger)) { }
        Assert.Equal("incomplete", Assert.Single(logger.Events).Values["Outcome"]);
    }

    [Fact]
    public async Task Http_exception_has_final_status_correlation_and_completion_diagnostic()
    {
        var logger = new DiagnosticRecordingLogger<RequestIdMiddleware>();
        var errorMiddleware = new ErrorHandlingMiddleware(_ => throw new IOException("private marker"), NullLogger<ErrorHandlingMiddleware>.Instance);
        var middleware = new RequestIdMiddleware(errorMiddleware.InvokeAsync, logger);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Headers["X-Request-Id"] = "request-17";
        context.Request.Headers["X-SAAIA-Turn-Id"] = "turn-17";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("request-17", context.Response.Headers["X-Request-Id"]);
        var operation = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var value) && Equals(value, "http.request"));
        Assert.Equal("failed", operation.Values["Outcome"]);
        using var fields = JsonDocument.Parse((string)operation.Values["Measurements"]!);
        Assert.Equal("turn-17", fields.RootElement.GetProperty("turn_id").GetString());
        Assert.Equal(500, fields.RootElement.GetProperty("http_status").GetInt32());
    }

    [Fact]
    public async Task Http_cancellation_has_a_terminal_cancelled_diagnostic()
    {
        var logger = new DiagnosticRecordingLogger<RequestIdMiddleware>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var middleware = new RequestIdMiddleware(_ => throw new OperationCanceledException(cancellation.Token), logger);
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
        var operation = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var value) && Equals(value, "http.request"));
        Assert.Equal("cancelled", operation.Values["Outcome"]);
    }

    [Theory]
    [InlineData("request-17", "request-17")]
    [InlineData("line\nbreak", null)]
    [InlineData("one,two", null)]
    public void Untrusted_correlation_headers_cannot_inject_multiline_logs(string supplied, string? expected)
        => Assert.Equal(expected, RequestIdMiddleware.ReadSafeIdentifier(supplied));
}
