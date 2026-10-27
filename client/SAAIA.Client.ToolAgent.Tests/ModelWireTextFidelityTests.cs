using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ModelWireTextFidelityTests
{
    // These production prompt helpers deliberately shorten readable previews. The
    // socket oracle checks that shortening never turns a valid scalar into U+FFFD.
    [Theory]
    [InlineData("review-head")]
    [InlineData("review-tail")]
    [InlineData("prompt-value")]
    [InlineData("prompt-block")]
    [InlineData("workspace")]
    [InlineData("metadata")]
    [InlineData("typed-value")]
    [InlineData("readable-excerpt")]
    [InlineData("orchestrator-prompt")]
    public async Task Production_preview_cut_is_not_corrupted_on_actual_client_http(string boundary)
    {
        var text = new string('a', 119) + "🧪" + new string('b', 220);
        Type owner = typeof(SourceBackedAgentV2Runner);
        var method = "TrimPromptValue";
        var maximum = 120;
        var expected = new string('a', 119) + "…";
        switch (boundary)
        {
            case "review-head":
                method = "CompactReviewExcerpt";
                maximum = 180; // head115, tail60: high surrogate at index114.
                text = new string('a', 114) + "🧪" + new string('b', 300);
                expected = new string('a', 114) + " ... " + new string('b', 60);
                break;
            case "review-tail":
                text = new string('a', 300) + "🧪" + new string('b', 59);
                method = "CompactReviewExcerpt";
                maximum = 180;
                expected = new string('a', 115) + " ... " + new string('b', 59);
                break;
            case "prompt-block": method = "TrimPromptBlock"; break;
            case "workspace": owner = typeof(SourceBackedEvidenceWorkspace); method = "Trim"; expected = new string('a', 119) + "..."; break;
            case "metadata": owner = typeof(SourceBackedDocumentMetadataHints); method = "Trim"; expected = new string('a', 119) + "..."; break;
            case "typed-value": owner = typeof(SourceBackedStructuredValueTypeDecisionBatchPrompt); method = "Trim"; expected = new string('a', 119) + "..."; break;
            case "readable-excerpt": owner = typeof(ToolAgentOrchestrator); method = "FormatReadableEvidenceExcerpt"; expected = new string('a', 119) + "..."; break;
            case "orchestrator-prompt": owner = typeof(ToolAgentOrchestrator); method = "TruncateForPrompt"; expected = new string('a', 119) + "..."; break;
        }
        var helper = owner.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(helper);
        var preview = (string)helper.Invoke(null, [text, maximum])!;
        await using var server = new LoopbackProvider(1);
        using var http = NewLocalHttp();
        var client = new OpenAiCompatLlmClient(http, server.BaseUrl, "synthetic-local-model");
        await client.CompleteAsync([("user", preview)], false, CancellationToken.None);
        var requests = await server.Capture;
        using var json = JsonDocument.Parse(requests[0]);
        var visible = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.DoesNotContain("\uFFFD", visible);
        Assert.Equal(expected, visible);
    }

    [Fact]
    public async Task Production_native_client_keeps_linked_tool_result_and_literal_source_on_socket()
    {
        const string literal = "  [E4] page=5; revision=22222222-2222-4222-8222-222222222222\r\n"
            + "qualité: native_reading_order_uncertain\r\n½ litre 🧪 ; 200 °C ; 𝛼 ≤ 5.\r\n  ";
        using var args = JsonDocument.Parse("{\"sourceKey\":\"src-é🧪\",\"page\":5}");
        var call = new SourceBackedAgentToolCall("call-α🧪", "read_source", args.RootElement.Clone());
        await using var server = new LoopbackProvider(1);
        using var http = NewLocalHttp();
        var client = new OpenAiLlmClient(http);
        client.Configure(server.BaseUrl + "/v1", "synthetic-local-model");
        await client.ChatOnceNativeAsync([
            SourceBackedAgentMessage.System("Conserve les limites de qualité."),
            SourceBackedAgentMessage.User("Lis la source."),
            SourceBackedAgentMessage.Assistant(null, [call]),
            SourceBackedAgentMessage.Tool(call.Id, call.Name, literal)], [], 0, 100, CancellationToken.None);
        using var json = JsonDocument.Parse(Assert.Single(await server.Capture));
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        var outgoingCall = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal(call.Id, outgoingCall.GetProperty("id").GetString());
        Assert.Equal(args.RootElement.GetRawText(), outgoingCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(call.Id, messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(call.Name, messages[3].GetProperty("name").GetString());
        Assert.Equal(literal, messages[3].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_client_wire_and_format_retry_preserve_literal_evidence_and_quality(bool rejectFormat)
    {
        const string evidence = "  [E7] doc=11111111-1111-4111-8111-111111111111; revision=22222222-2222-4222-8222-222222222222; page=40\r\n"
            + "SourceHash=" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            + "\r\n⚠ table_grid_unavailable; native_reading_order_uncertain\r\nCuisson : 200 °C ; ½ litre ; 𝛼 ≤ 5 ; 🧪.\r\n"
            + "SAAIA_SOURCE_BACKED_quoted_source_is_not_a_control_line\r\n  ";
        await using var server = new LoopbackProvider(rejectFormat ? 2 : 1, rejectFormat);
        using var http = NewLocalHttp();
        var client = new OpenAiCompatLlmClient(http, server.BaseUrl, "synthetic-local-model");
        var response = await client.CompleteAsync([
            ("system", "SAAIA_SOURCE_BACKED_STEP=Reader\nConserve les limites de qualité."),
            ("system", "Ne transforme pas une absence d'extrait en absence de fait."),
            ("user", evidence)], forceJson: true, CancellationToken.None);
        Assert.Equal("{\"ok\":true}", response);
        foreach (var bytes in await server.Capture)
        {
            Assert.NotEmpty(bytes);
            using var json = JsonDocument.Parse(bytes);
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("Conserve les limites de qualité.\n\nNe transforme pas une absence d'extrait en absence de fait.", messages[0].GetProperty("content").GetString());
            Assert.Equal(evidence, messages[1].GetProperty("content").GetString());
            Assert.DoesNotContain("SAAIA_SOURCE_BACKED_STEP", Encoding.UTF8.GetString(bytes));
        }
    }

    private static HttpClient NewLocalHttp() => new(new HttpClientHandler
        { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class LoopbackProvider : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public Task<IReadOnlyList<byte[]>> Capture { get; }

        public LoopbackProvider(int count, bool rejectFirst = false)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            Capture = CaptureAsync(count, rejectFirst);
        }

        private async Task<IReadOnlyList<byte[]>> CaptureAsync(int count, bool rejectFirst)
        {
            var result = new List<byte[]>();
            for (var i = 0; i < count; i++)
            {
                var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var path = context.Request.Url!.AbsolutePath;
                var method = context.Request.HttpMethod;
                var authorization = context.Request.Headers["Authorization"];
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                result.Add(body.ToArray()); // Capture bytes before any JSON decoding.
                context.Response.StatusCode = rejectFirst && i == 0 ? 400 : 200;
                context.Response.ContentType = "application/json";
                var output = Encoding.UTF8.GetBytes(context.Response.StatusCode == 400
                    ? "{\"error\":{\"message\":\"unsupported response format\"}}"
                    : "{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}");
                context.Response.ContentLength64 = output.Length;
                try { await context.Response.OutputStream.WriteAsync(output); }
                finally { context.Response.Close(); }
                // Assert after responding so a fixture failure cannot leave the
                // real client blocked while this capture task has already failed.
                Assert.Equal("/v1/chat/completions", path);
                Assert.Equal("POST", method);
                Assert.Null(authorization);
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try { await Capture.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception) when (!Capture.IsCompletedSuccessfully) { /* Observe pending accept on test failure. */ }
        }
    }
}
