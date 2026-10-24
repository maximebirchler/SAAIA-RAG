using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SAAIA.Backend.Middleware;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RequestIdCollectorFailureTests
{
    [Fact]
    public async Task Header_session_identity_is_labelled_as_client_claim_in_logs_scopes_and_activity()
    {
        using var parent = new System.Diagnostics.Activity("http-parent").Start();
        var logger = new DiagnosticRecordingLogger<RequestIdMiddleware>();
        var request = new DefaultHttpContext();
        request.Request.Headers["X-SAAIA-Session-Id"] = "client-claimed-session";
        await new RequestIdMiddleware(_ => Task.CompletedTask, logger).InvokeAsync(request);
        Assert.Equal("client-claimed-session", parent.GetTagItem("saaia.client_session_id"));
        Assert.Null(parent.GetTagItem("saaia.session_id"));
        Assert.Contains(logger.Scopes, scope => Equals(scope["client_session_id"], "client-claimed-session")
            && !scope.ContainsKey("session_id"));
        var entry = Assert.Single(logger.Events, item => item.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "http.request"));
        using var measurements = System.Text.Json.JsonDocument.Parse((string)entry.Values["Measurements"]!);
        Assert.Equal("client-claimed-session", measurements.RootElement.GetProperty("client_session_id").GetString());
        Assert.False(measurements.RootElement.TryGetProperty("session_id", out _));
    }

    [Theory]
    [InlineData("begin", false)]
    [InlineData("log", false)]
    [InlineData("dispose", false)]
    [InlineData("begin", true)]
    [InlineData("log", true)]
    [InlineData("dispose", true)]
    public async Task Logger_failures_cannot_block_the_request_or_replace_its_business_exception(string failure, bool businessFails)
    {
        var before = BackendDiagnostics.CollectionFailures;
        var expected = new InvalidOperationException("business_failure_must_survive");
        var calls = 0;
        var middleware = new RequestIdMiddleware(_ =>
        {
            calls++;
            if (businessFails) throw expected;
            return Task.CompletedTask;
        }, new FailingLogger(failure));
        var request = new DefaultHttpContext();
        if (businessFails)
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(request)));
        else
            await middleware.InvokeAsync(request);
        Assert.Equal(1, calls);
        Assert.True(BackendDiagnostics.CollectionFailures > before);
    }

    private sealed class FailingLogger(string failure) : ILogger<RequestIdMiddleware>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => failure == "begin" ? throw new IOException("collector_scope_begin") : new Scope(failure == "dispose");
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (failure == "log") throw new IOException("collector_log_write");
        }
        private sealed class Scope(bool fail) : IDisposable
        {
            public void Dispose() { if (fail) throw new IOException("collector_scope_dispose"); }
        }
    }
}
