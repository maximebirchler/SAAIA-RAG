using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", "dns")]
    [InlineData("responses", "dns")]
    [InlineData("chat-completions", "socket")]
    [InlineData("responses", "socket")]
    [InlineData("chat-completions", "tls")]
    [InlineData("responses", "tls")]
    [InlineData("chat-completions", "body")]
    [InlineData("responses", "body")]
    public async Task Transport_failure_preserves_safe_cause_without_retry_or_private_details(
        string protocol, string failure)
    {
        const string privateDetail = "server-secret private-corpus https://private-endpoint.invalid";
        var attempts = 0;
        Exception cause = failure switch
        {
            "dns" => new HttpRequestException(HttpRequestError.NameResolutionError, privateDetail,
                new SocketException((int)SocketError.HostNotFound)),
            "socket" => new HttpRequestException(HttpRequestError.ConnectionError, privateDetail,
                new IOException(privateDetail, new SocketException((int)SocketError.ConnectionReset))),
            "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, privateDetail,
                new AuthenticationException(privateDetail)),
            _ => new IOException(privateDetail, new SocketException((int)SocketError.ConnectionReset))
        };
        using var factory = new OmittedHandleHttpFactory(_ =>
        {
            attempts++;
            return failure == "body"
                ? Task.FromResult(CompletionStream(cause))
                : Task.FromException<HttpResponseMessage>(cause);
        });
        var options = WorkspaceOptions(protocol);
        options.LlmMaximumHttpAttempts = 3;
        var logger = new WireBudgetLogger();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "server-secret", logger)
                .ExecuteAsync(BuildRequest(), new RecordingToolGateway(), CancellationToken.None));

        Assert.Equal("advanced_llm_transport_error", error.ErrorCode);
        Assert.Equal(1, attempts);
        var log = Assert.Single(logger.Lines, line => line.Contains("operation=llm.call", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(log[(log.IndexOf("measurements=", StringComparison.Ordinal) + 13)..]);
        var values = json.RootElement;
        Assert.Equal(failure == "body" ? "response_processing" : "awaiting_response_headers",
            values.GetProperty("transport_phase").GetString());
        Assert.Equal(failure == "body" ? "IOException" : "HttpRequestException",
            values.GetProperty("transport_exception_type").GetString());
        Assert.Equal(1, values.GetProperty("http_attempt_count").GetInt32());
        Assert.Equal(0, values.GetProperty("diagnostic_dropped_measurements").GetInt32());
        if (failure == "tls")
        {
            Assert.Equal("SecureConnectionError", values.GetProperty("transport_http_error").GetString());
            Assert.True(values.GetProperty("transport_tls_failure").GetBoolean());
            Assert.False(values.TryGetProperty("transport_socket_error", out _));
        }
        else
        {
            Assert.Equal(failure == "dns" ? "HostNotFound" : "ConnectionReset",
                values.GetProperty("transport_socket_error").GetString());
            if (failure != "body")
                Assert.Equal(failure == "dns" ? "NameResolutionError" : "ConnectionError",
                    values.GetProperty("transport_http_error").GetString());
        }
        foreach (var secret in new[] { "server-secret", "private-corpus", "private-endpoint.invalid" })
        {
            Assert.DoesNotContain(secret, string.Join("\n", logger.Lines), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
    }
}
