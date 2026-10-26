using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", "read_source")]
    [InlineData("chat-completions", "find_source_text")]
    [InlineData("responses", "read_source")]
    [InlineData("responses", "find_source_text")]
    public async Task Text_source_quality_reaches_actual_writer_http_before_and_after_native_document_tools(
        string protocol, string operation)
    {
        const string content = "Le seuil est 17 bar. Inspect the documented assembly before applying this value.";
        var evidence = TextQualityFixture(BuildEvidence("E1", content), "ok", false,
            ["regional_text_repair_inconclusive", "figure_semantics_not_extracted"]);
        var arguments = operation == "read_source"
            ? """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20}"""
            : """{"sourceKey":"internal-source-1","query":"17 bar","offset":0,"topK":20}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction(operation, arguments) : NativeCompletion((operation, arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(evidence);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(operation, gateway.Searches[1].Operation);
        foreach (var index in new[] { 1, 2 })
        {
            var visible = Assert.Single(WorkspaceUser(factory.Requests[index].Body).GetProperty("evidence").EnumerateArray());
            Assert.Equal(content, visible.GetProperty("content").GetString());
            var quality = visible.GetProperty("sourceQuality");
            Assert.Equal("ok", quality.GetProperty("textStatus").GetString());
            Assert.False(quality.GetProperty("ocrCandidate").GetBoolean());
            Assert.Equal("not_verified", quality.GetProperty("semanticAccuracy").GetString());
            Assert.Equal(new[] { "regional_text_repair_inconclusive", "figure_semantics_not_extracted" },
                quality.GetProperty("signals").EnumerateArray().Select(item => item.GetString()).ToArray());
            using var wire = JsonDocument.Parse(factory.Requests[index].Body);
            var system = wire.RootElement.GetProperty(protocol == "responses" ? "input" : "messages")
                .EnumerateArray().First(item => item.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString()!;
            Assert.Contains("sourceQuality", system, StringComparison.Ordinal);
            Assert.Contains("not_verified", system, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Text_source_quality_preserves_actual_corruption_status_without_rewriting_the_evidence()
    {
        const string content = "Le seuil est 17 bar. An adjacent observed field contains \uFFFD.";
        var evidence = TextQualityFixture(BuildEvidence("E1", content), "low_text", true,
            ["replacement_chars_remaining"]);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(content, visible.GetProperty("content").GetString());
        Assert.Equal("low_text", visible.GetProperty("sourceQuality").GetProperty("textStatus").GetString());
        Assert.True(visible.GetProperty("sourceQuality").GetProperty("ocrCandidate").GetBoolean());
    }

    [Fact]
    public async Task Text_without_observed_quality_does_not_gain_a_verified_quality_claim()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.True(!visible.TryGetProperty("sourceQuality", out var quality) || quality.ValueKind == JsonValueKind.Null);
    }

    // The JSON fixture compiles against the old optional contract, so a missing
    // transport fails at the real captured HTTP boundary rather than at build time.
    private static AdvancedAnalysisResolvedEvidence TextQualityFixture(AdvancedAnalysisResolvedEvidence evidence,
        string textStatus, bool ocrCandidate, string[] signals)
    {
        var node = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        node["SourceQuality"] = JsonSerializer.SerializeToNode(new
        {
            textStatus, ocrCandidate, signals, semanticAccuracy = "not_verified"
        });
        return node.Deserialize<AdvancedAnalysisResolvedEvidence>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }
}
