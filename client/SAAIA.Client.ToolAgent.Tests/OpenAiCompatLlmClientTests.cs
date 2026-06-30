using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class OpenAiCompatLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_sends_conservative_sampling_and_answer_budget()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "You are SAAIA."),
            ("user", "Hello")
        }, forceJson: false, CancellationToken.None);

        Assert.Equal("ok", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;

        Assert.Equal(1000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.Equal(0.85, root.GetProperty("top_p").GetDouble(), precision: 3);
        Assert.Equal(0.2, root.GetProperty("frequency_penalty").GetDouble(), precision: 3);
        Assert.Equal(0.05, root.GetProperty("presence_penalty").GetDouble(), precision: 3);
        Assert.True(root.TryGetProperty("stop", out var stop));
        Assert.Contains(stop.EnumerateArray(), item => item.GetString() == "\nTOOL_RESULTS");
    }

    [Fact]
    public async Task CompleteAsync_keeps_json_response_format_and_larger_budget_for_structured_calls()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"{\"mode\":\"auto\"}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "Return JSON."),
            ("user", "Route this.")
        }, forceJson: true, CancellationToken.None);

        Assert.Equal("{\"mode\":\"auto\"}", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;

        Assert.Equal(1600, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.TryGetProperty("response_format", out var format));
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("stop", out _));
    }

    [Fact]
    public async Task CompleteAsync_uses_larger_budget_for_broad_source_backed_writer_prompts()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "Write a clean source-backed answer. Do not dump raw excerpts."),
            ("user", """
PRIVATE_SOURCE_COVERAGE_NOTE:
The initial evidence is partial.

PRIVATE_SOURCE_WRITING_BRIEF:
Rewrite the useful evidence into a clean answer.

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
Several distinct candidates across several pages.
""")
        }, forceJson: false, CancellationToken.None);

        Assert.Equal("ok", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        Assert.Equal(4096, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CaptureHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
