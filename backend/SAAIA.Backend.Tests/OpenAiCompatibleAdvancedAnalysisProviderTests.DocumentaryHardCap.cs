using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task DocumentaryHardCap_large_structured_selection_never_raises_configured_primary_budget(
        string protocol, bool nativeRead)
    {
        const int configured = 8_000;
        var queries = Enumerable.Range(1, 8).Select(index => new { query = "documented method " + index, topK = 20 }).ToArray();
        using var factory = new QueuedHttpClientFactory(
            Completion(JsonSerializer.Serialize(new { selectionMode = "repeatable_named_items", queries })),
            EarlyReply(protocol, EarlyInsufficient));
        var options = WorkspaceOptions(protocol);
        options.NativeObservationReadEnabled = nativeRead;
        options.MaximumEvidencePromptCharacters = configured;
        options.ExternalMaximumCallsPerJob = 2;
        // Forty excerpt bodies exercise projection and continuation windows.
        // Reuse four canonical documents so the distinct-source catalogue itself
        // is not the separately tested incompressible-metadata counterexample.
        var documents = Enumerable.Range(1, 4).Select(_ => (Doc: Guid.NewGuid().ToString("D"),
            Revision: Guid.NewGuid().ToString("D"))).ToArray();
        var sources = Enumerable.Range(1, 40).Select(index => BuildEvidence("E" + index,
            "METHOD " + index + ": preserve ±0.125 mm, \"scope\" and the required prerequisite.\n" + new string('x', 4_000),
            fileName: $"methods-{(index - 1) % documents.Length}.pdf",
            docId: documents[(index - 1) % documents.Length].Doc,
            revisionId: documents[(index - 1) % documents.Length].Revision,
            pageStart: index, exactTitle: "METHOD " + index)).ToArray();
        var gateway = new RecordingToolGateway(sources);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(configured, options.MaximumEvidencePromptCharacters);
        var body = factory.Requests[1].Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, configured);
        var prompt = WorkspaceUser(body);
        Assert.False(prompt.TryGetProperty("alternativeTableReadings", out _));
        Assert.False(prompt.TryGetProperty("alternativeNativeReadings", out _));
        var visible = prompt.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.NotEmpty(visible);
        Assert.True(visible.Length < sources.Length);
        AssertWireProjection(prompt, sources.Length, visible.Length);
        foreach (var item in visible)
        {
            var original = sources.Single(source => source.Reference.EvidenceId == item.GetProperty("evidenceId").GetString());
            var text = item.GetProperty("content").GetString()!;
            Assert.Equal(original.Content[..text.Length], text);
            Assert.Equal(original.Content.Length, item.GetProperty("originalContentLength").GetInt32());
            Assert.Equal(0, item.GetProperty("contentOffset").GetInt32());
            Assert.True(item.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(text.Length, item.GetProperty("nextContentOffset").GetInt32());
        }
        Assert.Equal(40, gateway.Evidence.Count);
    }
}
