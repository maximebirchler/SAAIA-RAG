using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Source_title_navigation_three_parallel_pages_are_charged_before_fitting_current_evidence(string protocol)
    {
        const int maximum = 16_000;
        var evidence = Enumerable.Range(1, 15).Select(index =>
            NavigationCard($"SOURCE-{index}", null, source: (index - 1) % 3 + 1, page: index, card: false) with
            {
                Content = $"Inspection record {index}: " + string.Concat(
                    Enumerable.Repeat("Check that the assembly is isolated before adjusting its limits. ", 15))
            }).ToArray();
        var calls = Enumerable.Range(1, 3).Select(index => ("list_source_titles", JsonSerializer.Serialize(new
        {
            sourceKey = $"internal-source-{index}", offset = 0, limit = 20, titleContains = (string?)null,
            pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = true
        }))).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, """{"outcome":"insufficient_documentation","answerText":"Additional bodies still need review.","claims":[]}"""));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.MaximumEvidencePromptCharacters = maximum;
        options.SemanticCriticEnabled = false;
        options.NativeResearchWorkspaceEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new SourceTitleNavigationBudgetGateway(evidence);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Empty(result.Claims);
        Assert.Equal(3, gateway.Pages.Count);
        Assert.Equal(3, factory.Requests.Count);
        var writer = factory.Requests[1].Body;
        var followup = factory.Requests[2].Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(followup), 1, maximum);
        var outputs = NativeCorrelationOutputs(followup, protocol);
        Assert.Equal(3, outputs.Length);
        foreach (var output in outputs)
        {
            var value = output.Body;
            Assert.Equal("executed", value.GetProperty("status").GetString());
            Assert.True(value.GetProperty("navigationOnly").GetBoolean());
            Assert.False(value.TryGetProperty("visibleEvidenceIds", out _));
            var page = gateway.Pages[value.GetProperty("sourceKey").GetString()!];
            Assert.Equal(page.NextOffset, value.GetProperty("page").GetProperty("nextOffset").GetInt32());
            Assert.Equal(page.Items.Select(item => item.Title), value.GetProperty("page").GetProperty("items")
                .EnumerateArray().Select(item => item.GetProperty("title").GetString()));
        }
        Assert.True(WorkspaceUser(followup).GetProperty("evidence").GetArrayLength()
            < WorkspaceUser(writer).GetProperty("evidence").GetArrayLength());
    }

    private sealed class SourceTitleNavigationBudgetGateway(IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
        : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => evidence;
        public Dictionary<string, AdvancedAnalysisSourceTitlePage> Pages { get; } = new();

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            if (request.Operation != "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, evidence, [], 0, 1));
            var source = evidence.First(item => item.Reference.DocId == request.DocId).Reference.EvidenceId;
            var locators = Enumerable.Range(1, 21).Select(i => new AdvancedAnalysisSourceTitle(
                $"Inspection checkpoint {source}-{i}: verify the isolator position and record the identifier before adjustment",
                "title_anchor", null, i, i)).ToArray();
            var page = AdvancedAnalysisSourceTitlePage.Fit(request.Offset, request.TopK, locators);
            Pages.Add($"internal-source-{Pages.Count + 1}", page);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Pages.Count + 1, SourceTitlePage: page));
        }
    }
}
