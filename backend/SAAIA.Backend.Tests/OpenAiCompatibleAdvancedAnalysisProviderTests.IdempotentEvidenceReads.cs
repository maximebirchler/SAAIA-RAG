using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Identical_cached_read_calls_share_one_window_and_keep_every_call_result(string protocol)
    {
        var gateway = new EvidencePrefixGateway(6, false);
        var calls = EvidencePrefixCalls(gateway.Bodies).ToList();
        calls.Add(calls[3]);
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            EvidencePrefixReply(protocol, calls.ToArray()), EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 3);
        options.MaximumEvidencePromptCharacters = 98_304;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(gateway.Requests);
        Assert.Equal(3, factory.Requests.Count);
        var wire = factory.Requests[2].Body;
        var outputs = NativeCorrelationOutputs(wire, protocol);
        Assert.Equal(7, outputs.Length);
        Assert.Equal(7, outputs.Select(output => output.CallId).Distinct().Count());
        Assert.All(outputs, output => Assert.Equal("projected", output.Body.GetProperty("status").GetString()));
        Assert.All(gateway.Bodies, body => AssertEvidencePrefixWindow(WorkspaceUser(wire), body, 3000));
        Assert.Equal(outputs[3].Body.GetProperty("requestedWindow").GetRawText(),
            outputs[6].Body.GetProperty("requestedWindow").GetRawText());
        Assert.Contains(gateway.Checkpoints, checkpoint => checkpoint.EvidenceReadWindows?.Count == 6);
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("Operation")?.ToString() == "llm.evidence_read_batch"
            && e.Values.GetValueOrDefault("ReasonCode")?.ToString() == "identical_windows_coalesced");
    }
}
