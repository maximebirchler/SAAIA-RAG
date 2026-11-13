using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", "·")]
    [InlineData("chat-completions", "·")]
    [InlineData("responses", " x ")]
    [InlineData("chat-completions", "x\ny")]
    public async Task Source_title_filter_rejection_is_atomic_and_model_can_correct_it(string protocol, string invalid)
    {
        var validCall = TitleFilterCall("seal");
        var invalidCall = TitleFilterCall(invalid);
        HttpResponseMessage Calls(params (string, string)[] calls) => protocol == "responses"
            ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Calls(validCall, invalidCall), Calls(TitleFilterCall(null)), EarlyReply(protocol, NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.ExternalMaximumCallsPerJob = 6;
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new SourceTitleNavigationGateway(evidence, evidence, "Inspection");

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal(new[] { "search_corpus", "list_source_titles" }, gateway.Requests.Select(x => x.Operation));
        Assert.Null(gateway.Requests[1].TitleContains);
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol).ToArray();
        Assert.Equal(2, outputs.Length);
        foreach (var output in outputs)
        {
            Assert.Equal("batch_rejected_before_execution", output.Body.GetProperty("status").GetString());
            Assert.Equal("list_source_titles_filter_invalid", output.Body.GetProperty("argumentFeedback")
                .GetProperty("reasonCode").GetString());
            Assert.Contains("titleContains", output.Body.GetProperty("instruction").GetString());
            Assert.Contains("null", output.Body.GetProperty("instruction").GetString());
        }
        using var wire = JsonDocument.Parse(factory.Requests[1].Body);
        var tool = Assert.Single(wire.RootElement.GetProperty("tools").EnumerateArray()
            .Select(x => protocol == "responses" ? x : x.GetProperty("function")),
            x => x.GetProperty("name").GetString() == "list_source_titles");
        var filter = tool.GetProperty("parameters").GetProperty("properties").GetProperty("titleContains");
        Assert.Equal(2, filter.GetProperty("minLength").GetInt32());
        Assert.Equal(128, filter.GetProperty("maxLength").GetInt32());
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task Source_title_filter_recovery_respects_existing_call_and_retry_limits(string protocol, bool noBudget)
    {
        HttpResponseMessage Invalid() => protocol == "responses"
            ? BudgetNativeResponseFunctions([TitleFilterCall("·")]) : NativeCompletion(TitleFilterCall("·"));
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Invalid(), Invalid());
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.ExternalMaximumCallsPerJob = noBudget ? 3 : 6;
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new SourceTitleNavigationGateway(evidence, evidence, "Inspection");
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));

        Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
        Assert.Equal(noBudget ? 2 : 3, factory.Requests.Count);
        Assert.Single(gateway.Requests);
    }

    private static (string, string) TitleFilterCall(string? filter) => ("list_source_titles",
        JsonSerializer.Serialize(new { sourceKey = "internal-source-1", offset = 0, limit = 20,
            titleContains = filter, pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = false }));
}
