using System.Net;
using System.Text;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LocalLlmChatClientTests
{
    [Fact]
    public async Task TryCompleteWithTelemetryAsync_reads_text_content_blocks_from_array_payload()
    {
        var client = CreateClient(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": [
                      { "type": "text", "text": "Line 1" },
                      { "type": "reasoning", "text": "ignore me" },
                      { "type": "text", "text": "Line 2" }
                    ]
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Equal("Line 1" + Environment.NewLine + "Line 2", result.Content);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.Null(result.Error);
        Assert.True(result.BytesRead > 0);
        Assert.NotNull(result.ResponseHeadersMs);
        Assert.NotNull(result.FirstByteMs);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_choices_missing_when_success_payload_has_no_choices()
    {
        var client = CreateClient("""{ "id": "chatcmpl_test" }""", HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("choices_missing", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.BytesRead > 0);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_content_missing_when_choice_message_has_no_content()
    {
        var client = CreateClient(
            """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("content_missing", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.BytesRead > 0);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_empty_body_when_success_response_has_no_payload()
    {
        var client = CreateClient(string.Empty, HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("empty_body", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_exception_when_success_payload_is_not_valid_json()
    {
        var client = CreateClient("not-json-at-all", HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("exception", result.Error);
        Assert.Null(result.StatusCode);
    }

    private static LocalLlmChatClient CreateClient(string body, HttpStatusCode statusCode)
        => new(
            new StubHttpClientFactory(body, statusCode),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            });

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
