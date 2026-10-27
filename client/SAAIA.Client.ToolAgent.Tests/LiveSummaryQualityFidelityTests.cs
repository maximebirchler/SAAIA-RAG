using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

[Trait("Profile", "Unit")]
public sealed class LiveSummaryQualityFidelityTests
{
    private readonly ITestOutputHelper _output;
    public LiveSummaryQualityFidelityTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 6)]
    public async Task Live_summary_wire_distinguishes_local_warning_omissions_from_complete_transport(int sourceCount, int limit)
    {
        var signals = Enumerable.Range(0, 10).Select(index => index == 7
            ? "structured_transcription_not_verified" : "observed_warning_" + index).ToList();
        var source = new ToolMemory.SourceRef
        {
            DocPath = "synthetic/quality.pdf", DocName = "quality.pdf", PageStart = 5, PageEnd = 5,
            QualitySignals = signals, QualitySignalsMetadata = new("complete", 0, false, false),
            ChunkQualitySignals = ["native_reading_order_uncertain"],
            ChunkQualitySignalsMetadata = new("complete", 0, false, false)
        };
        var sources = Enumerable.Repeat(source, sourceCount).ToArray();
        var visible = await SendMetadata(sources);
        var metadata = Metadata(visible, "qualitySignalsMetadata");
        Assert.Equal("source", metadata.GetProperty("scope").GetString());
        Assert.Equal(limit, metadata.GetProperty("localVisibleCount").GetInt32());
        Assert.Equal(10 - limit, metadata.GetProperty("localOmittedCount").GetInt32());
        Assert.Equal("complete", metadata.GetProperty("transportMetadata").GetProperty("status").GetString());
        Assert.Equal(0, metadata.GetProperty("transportMetadata").GetProperty("omittedCount").GetInt32());
        var chunk = Metadata(visible, "chunkQualitySignalsMetadata");
        Assert.Equal("chunk", chunk.GetProperty("scope").GetString());
        Assert.Equal(1, chunk.GetProperty("localVisibleCount").GetInt32());
        Assert.Equal(0, chunk.GetProperty("localOmittedCount").GetInt32());
        Assert.Contains("native_reading_order_uncertain", visible);
        if (limit > 7) Assert.Contains("structured_transcription_not_verified", visible);
        else Assert.DoesNotContain("structured_transcription_not_verified", visible);
        Assert.Contains("omitted or unavailable diagnostics do not establish clean or complete source quality", visible);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("partial")]
    [InlineData("malformed")]
    public async Task Live_summary_wire_keeps_upstream_unknown_or_malformed_warning_metadata(string state)
    {
        var source = new ToolMemory.SourceRef
        {
            DocPath = "synthetic/quality.pdf", PageStart = 1, PageEnd = 1,
            QualitySignals = ["known_warning"],
            QualitySignalsMetadata = state switch
            {
                "partial" => new("partial", null, true, false),
                "malformed" => new("invalid_status"),
                _ => null
            }
        };
        var visible = await SendMetadata([source]);
        var sourceMetadata = Metadata(visible, "qualitySignalsMetadata");
        Assert.Equal(0, sourceMetadata.GetProperty("localOmittedCount").GetInt32());
        var transport = sourceMetadata.GetProperty("transportMetadata");
        Assert.Equal(state, transport.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, transport.GetProperty("omittedCount").ValueKind);
        var chunk = Metadata(visible, "chunkQualitySignalsMetadata").GetProperty("transportMetadata");
        Assert.Equal("unavailable", chunk.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, chunk.GetProperty("omittedCount").ValueKind);
        Assert.Contains("known_warning", visible);
    }

    private async Task<string> SendMetadata(IReadOnlyList<ToolMemory.SourceRef> sources)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(item => item.Name == "AppendLiveSummarySourceMetadataPrompt"
                && item.GetParameters()[1].ParameterType == typeof(IReadOnlyList<ToolMemory.SourceRef>));
        var prompt = new StringBuilder();
        method.Invoke(null, [prompt, sources]);
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        await new OpenAiCompatLlmClient(http, "http://127.0.0.1:1", "synthetic-model")
            .CompleteAsync([("user", prompt.ToString())], false, CancellationToken.None);
        var bytes = Assert.Single(handler.Requests);
        using var wire = JsonDocument.Parse(bytes);
        var visible = wire.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        _output.WriteLine($"actual_metadata_prompt_characters={visible.Length}; actual_http_body_bytes={bytes.Length}; source_count={sources.Count}");
        return visible;
    }

    private static JsonElement Metadata(string prompt, string name)
    {
        var position = prompt.IndexOf(name + "=", StringComparison.Ordinal);
        var separatorLength = name.Length + 1;
        if (position < 0) position = prompt.IndexOf(name + ":", StringComparison.Ordinal);
        Assert.True(position >= 0, "Actual summary prompt lacks " + name);
        var suffix = prompt[(position + separatorLength)..].TrimStart();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(suffix));
        using var json = JsonDocument.ParseValue(ref reader);
        return json.RootElement.Clone();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<byte[]> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
