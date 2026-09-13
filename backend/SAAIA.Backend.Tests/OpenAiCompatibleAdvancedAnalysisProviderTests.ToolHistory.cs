using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Large_native_read_batch_preserves_all_outputs_opaque_state_and_current_proof()
        => await AssertHistoryBatchCompletes(8_000, 16_384);

    [Theory]
    [InlineData(20_000, 32_768)]
    [InlineData(40_000, 65_536)]
    public async Task Expanded_native_history_budget_preserves_all_outputs_and_opaque_state(
        int opaqueCharacters, int historyCharacters)
        => await AssertHistoryBatchCompletes(opaqueCharacters, historyCharacters);

    private static async Task AssertHistoryBatchCompletes(int opaqueCharacters, int historyCharacters)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"content_claims","queries":[{"query":"overview","topK":12}]}"""),
            HistoryNativeReadBatch(opaqueCharacters), NativeResponseAnswer(NativeAnswered));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var options = WorkspaceOptions("responses");
        options.NativeResearchWorkspaceEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;
        options.NativeResearchMaximumHistoryCharacters = historyCharacters;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(atomicEvidenceMode: "content_claim",
                selectionPolicy: "structured_layout", atomicEvidenceType: "documented_fact"),
                gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Equal(9, gateway.Searches.Count);
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        var items = request.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var state = Assert.Single(items, i => i.TryGetProperty("type", out var type)
            && type.GetString() == "reasoning");
        Assert.Equal(new string('s', opaqueCharacters), state.GetProperty("encrypted_content").GetString());
        var outputs = items.Where(i => i.TryGetProperty("type", out var type)
            && type.GetString() == "function_call_output").ToArray();
        Assert.Equal(8, outputs.Length);
        for (var i = 0; i < outputs.Length; i++)
        {
            Assert.Equal("history-call-" + i, outputs[i].GetProperty("call_id").GetString());
            using var output = JsonDocument.Parse(outputs[i].GetProperty("output").GetString()!);
            Assert.Equal("executed", output.RootElement.GetProperty("status").GetString());
            Assert.Equal("read_source", output.RootElement.GetProperty("operation").GetString());
            Assert.True(output.RootElement.TryGetProperty("resolvedSource", out _));
            Assert.Equal(1, output.RootElement.GetProperty("returnedEvidenceCount").GetInt32());
            Assert.Equal(0, output.RootElement.GetProperty("visibleEvidenceIdsOmitted").GetInt32());
            Assert.Equal(i == 0, output.RootElement.TryGetProperty("instruction", out _));
            Assert.False(output.RootElement.TryGetProperty("argumentFeedback", out _));
            Assert.Equal("E1", Assert.Single(output.RootElement.GetProperty("visibleEvidenceIds")
                .EnumerateArray()).GetString());
        }
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds[0]);
    }

    [Theory]
    [InlineData(20_000, 16_384)]
    [InlineData(40_000, 32_768)]
    public async Task Oversized_native_state_is_rejected_without_truncation_or_another_model_call(
        int opaqueCharacters, int historyCharacters)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"content_claims","queries":[{"query":"overview","topK":12}]}"""),
            HistoryNativeReadBatch(opaqueCharacters));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var options = WorkspaceOptions("responses");
        options.NativeResearchWorkspaceEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;
        options.NativeResearchMaximumHistoryCharacters = historyCharacters;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
                BuildRequest(atomicEvidenceMode: "content_claim", selectionPolicy: "structured_layout",
                    atomicEvidenceType: "documented_fact"), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_history_limit_exceeded", error.Message);
        Assert.Equal(historyCharacters, error.Data["maximumHistoryCharacters"]);
        Assert.True((int)error.Data["historyCharacters"]! > historyCharacters);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(9, gateway.Searches.Count);
    }

    [Theory]
    [InlineData(16_383)]
    [InlineData(65_537)]
    public async Task Invalid_native_history_budget_is_rejected_before_model_or_corpus_io(int characters)
    {
        using var factory = new QueuedHttpClientFactory();
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var options = WorkspaceOptions("responses");
        options.NativeResearchMaximumHistoryCharacters = characters;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_history_budget_invalid", error.Message);
        Assert.Empty(factory.Requests);
        Assert.Empty(gateway.Searches);
    }

    private static HttpResponseMessage HistoryNativeReadBatch(int opaqueCharacters)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                status = "completed",
                output = new object[]
                {
                    new { id = "simulated-history-state", type = "reasoning",
                        summary = Array.Empty<object>(), encrypted_content = new string('s', opaqueCharacters) }
                }.Concat(Enumerable.Range(0, 8).Select(i => (object)new
                {
                    id = "history-function-" + i, type = "function_call", call_id = "history-call-" + i,
                    name = "read_source", status = "completed",
                    arguments = JsonSerializer.Serialize(new
                    {
                        sourceKey = "internal-source-1", pageStart = i * 4 + 1,
                        pageEnd = i * 4 + 4, topK = 12
                    })
                })),
                usage = new { input_tokens = 100, output_tokens = 50 }
            })
        };
}
