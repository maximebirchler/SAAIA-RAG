using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class RemainingModelPromptUnicodeTests
{
    [Theory]
    [InlineData("observation")]
    [InlineData("display-value")]
    [InlineData("memory-prompt")]
    [InlineData("memory-context")]
    [InlineData("direct-card-proof")]
    [InlineData("fact-card-proof")]
    [InlineData("cell-feedback")]
    [InlineData("overview")]
    [InlineData("live-summary")]
    [InlineData("retrieval-term")]
    [InlineData("profile-signal")]
    public async Task Remaining_production_prompt_cuts_retain_whole_unicode_on_the_serialized_client_request(string surface)
    {
        var text = new string('a', 119) + "🧪" + new string('b', 600);
        var expected = new string('a', 119) + "…";
        string projected;
        switch (surface)
        {
            case "observation":
                projected = InvokeText(typeof(SourceBackedAgentObservationCompactor), "Truncate", text, 120);
                break;
            case "display-value":
                projected = InvokeText(typeof(SourceBackedAgentV2Runner), "TrimDisplayValue", text, 120);
                break;
            case "memory-prompt":
                const string marker = "\n- MEMORY_CONTEXT_TRUNCATED: details remain available in persistent memory.";
                var contentLimit = 200 - marker.Length;
                text = new string('a', contentLimit - 1) + "🧪" + new string('b', 600);
                expected = new string('a', contentLimit - 1) + marker;
                projected = InvokeText(typeof(SourceBackedAgentV2Runner), "BoundMemoryPrompt", text, 4096, (int?)200);
                break;
            case "memory-context":
                expected = new string('a', 119) + "...";
                projected = InvokeText(typeof(ToolAgentOrchestrator), "CompactSourceBackedMemoryText", text, 120);
                break;
            case "overview":
                expected = new string('a', 119) + "...";
                projected = InvokeText(typeof(ToolAgentOrchestrator), "CompactEvidenceOverviewText", text, 120);
                break;
            case "live-summary":
                text = new string('a', 76) + "🧪" + new string('b', 600);
                expected = new string('a', 76) + "...";
                projected = InvokeText(typeof(ToolAgentOrchestrator), "ShortenLiveSummaryEvidenceValue", text);
                break;
            case "retrieval-term":
                text = new string('a', 116) + "🧪" + new string('b', 600);
                expected = new string('a', 116) + "...";
                projected = InvokeText(typeof(ToolAgentOrchestrator), "NormalizeLiveSummaryRetrievalTerm", text);
                break;
            case "profile-signal":
                text = new string('a', 159) + "🧪" + new string('b', 600);
                expected = new string('a', 159);
                projected = InvokeText(typeof(ToolAgentOrchestrator), "CompactClientProfileSignalValue", text);
                break;
            case "direct-card-proof":
            case "fact-card-proof":
                text = new string('a', 519) + "🧪" + new string('b', 600);
                var evidence = surface == "direct-card-proof"
                    ? JsonSerializer.SerializeToElement(new { sourceText = text })
                    : JsonSerializer.SerializeToElement(new { facts = new[] { new { text } } });
                projected = InvokeText(typeof(SourceBackedCanonicalContentCardInventory), "ReadProof",
                    JsonSerializer.SerializeToElement(new { }), (JsonElement?)evidence);
                expected = new string('a', 519) + "...";
                break;
            default:
                text = new string('a', 76) + "🧪" + new string('b', 600);
                projected = InvokeText(typeof(SourceContractVerifier), "TrimCellForMessage", text);
                expected = new string('a', 76) + "...";
                break;
        }

        Assert.Equal(expected, await SendThroughProductionSerialization(projected));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public async Task Focused_technical_window_does_not_introduce_a_partial_character_before_readable_formatting(string boundary)
    {
        var source = boundary == "start"
            ? new string('a', 59) + "🧪" + new string('b', 38) + " temperature " + new string('c', 500)
            : new string('a', 99) + " temperature " + new string('b', 227) + "🧪" + new string('c', 500);
        var projected = InvokeText(typeof(ToolAgentOrchestrator), "FocusShortTechnicalEvidenceText",
            Hit(source), "temperature", 160);
        var expected = "... " + (boundary == "start" ? source[61..340] : source[60..339]);
        Assert.Equal(expected, await SendThroughProductionSerialization(projected));
    }

    [Theory]
    [InlineData("GetFocusedExactItemEvidenceText")]
    [InlineData("GetFocusedExactItemStructuredEvidenceText")]
    public async Task Focused_exact_item_windows_keep_complete_characters_from_the_source(string method)
    {
        var source = string.Concat(Enumerable.Repeat("a🧪", 1000)) + " banane "
            + string.Concat(Enumerable.Repeat("b🧪", 1000));
        var projected = InvokeText(typeof(ToolAgentOrchestrator), method, "banane", Hit(source));
        Assert.NotEmpty(projected);
        var visible = await SendThroughProductionSerialization(projected);
        Assert.Contains("banane", visible);
        Assert.Contains(visible, source, StringComparison.Ordinal);
    }

    private static string InvokeText(Type owner, string name, params object?[] arguments)
    {
        var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, arguments));
    }

    private static object Hit(string text)
    {
        var type = typeof(ToolAgentOrchestrator).GetNestedType("RagHitSummary", BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(item => item.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "DocPath" => (object)"synthetic/unicode.pdf",
            "DocName" => "unicode.pdf",
            "PageStart" or "PageEnd" => 1,
            "Excerpt" => "",
            "FullText" => text,
            _ => parameter.HasDefaultValue ? parameter.DefaultValue : null
        }).ToArray();
        return constructor.Invoke(arguments);
    }

    private static async Task<string> SendThroughProductionSerialization(string content)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new OpenAiCompatLlmClient(http, "http://127.0.0.1:1", "synthetic-unicode-model");
        await client.CompleteAsync([("user", content)], false, CancellationToken.None);
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests));
        var visible = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("\uFFFD", visible);
        _ = new UTF8Encoding(false, true).GetBytes(visible);
        return visible;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<byte[]> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // No socket is opened: this complements the root's loopback suite
            // while testing the exact bytes from the same production serializer.
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Requests.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
