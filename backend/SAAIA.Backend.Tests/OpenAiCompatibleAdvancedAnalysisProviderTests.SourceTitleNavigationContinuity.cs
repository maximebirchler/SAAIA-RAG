using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Source_title_navigation_observed_cursors_survive_two_later_body_reads(string protocol)
    {
        var listings = Enumerable.Range(1, 3).Select(source => ("list_source_titles", JsonSerializer.Serialize(new
        {
            sourceKey = $"internal-source-{source}", offset = source == 1 ? 10 : 9, limit = 20,
            titleContains = source == 2 ? "inspection" : (string?)null,
            pageStart = source == 2 ? 2 : (int?)null, pageEnd = source == 2 ? 60 : (int?)null,
            cardBackedOnly = source != 3
        }))).ToArray();
        var firstRead = ("read_source", """{"sourceKey":"internal-source-1","pageStart":30,"pageCount":1,"topK":20}""");
        var secondRead = ("read_source", """{"sourceKey":"internal-source-2","pageStart":40,"pageCount":1,"topK":20}""");
        HttpResponseMessage Native(params (string Name, string Arguments)[] calls)
            => protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            Native(listings), Native(firstRead), Native(secondRead), EarlyReply(protocol,
                """{"outcome":"insufficient_documentation","answerText":"Further documented choices are needed.","claims":[]}"""));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchWorkspaceEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new NavigationContinuityGateway();
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Empty(result.Claims);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal(6, gateway.Requests.Count);
        foreach (var index in new[] { 3, 4 })
        {
            var user = WorkspaceUser(factory.Requests[index].Body);
            Assert.True(user.GetProperty("sourceCatalogue").TryGetProperty("titleNavigation", out var navigation),
                "Observed pagination must survive replacement of the current native turn by body reads.");
            Assert.True(navigation.GetProperty("navigationOnly").GetBoolean());
            Assert.False(navigation.GetProperty("citable").GetBoolean());
            var cursors = navigation.GetProperty("items").EnumerateArray().OrderBy(row => row.GetProperty("sourceKey").GetString()).ToArray();
            Assert.Equal(new[] { 19, 18, 18 }, cursors.Select(row => row.GetProperty("nextOffset").GetInt32()));
            Assert.Equal(new[] { 10, 9, 9 }, cursors.Select(row => row.GetProperty("offset").GetInt32()));
            var filtered = cursors[1];
            Assert.Equal("inspection", filtered.GetProperty("titleContains").GetString());
            Assert.Equal(2, filtered.GetProperty("pageStart").GetInt32());
            Assert.Equal(60, filtered.GetProperty("pageEnd").GetInt32());
            Assert.True(filtered.GetProperty("cardBackedOnly").GetBoolean());
            Assert.False(cursors[2].GetProperty("cardBackedOnly").GetBoolean());
            Assert.DoesNotContain("evidenceId", navigation.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("content", navigation.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(factory.Requests[index].Body),
                1, options.MaximumEvidencePromptCharacters);
        }
        var probes = logger.Events.Where(entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "llm.call")
            .Select(entry => JsonDocument.Parse((string)entry.Values["Measurements"]!).RootElement.Clone()).ToArray();
        Assert.Equal(factory.Requests.Count, probes.Length);
        foreach (var request in factory.Requests)
        {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Body)));
            var probe = Assert.Single(probes, value => value.GetProperty("request_sha256").GetString()!.Equals(digest, StringComparison.OrdinalIgnoreCase));
            var user = WorkspaceUser(request.Body);
            JsonElement navigation = default;
            var hasNavigation = user.TryGetProperty("sourceCatalogue", out var catalogue) && catalogue.TryGetProperty("titleNavigation", out navigation);
            Assert.Equal(hasNavigation ? navigation.GetProperty("retainedCursorCount").GetInt32() : 0, probe.GetProperty("source_title_cursor_retained_count").GetInt32());
            Assert.Equal(hasNavigation ? navigation.GetProperty("listedCursorCount").GetInt32() : 0, probe.GetProperty("source_title_cursor_listed_count").GetInt32());
            Assert.Equal(hasNavigation ? navigation.GetProperty("omittedCursorCount").GetInt32() : 0, probe.GetProperty("source_title_cursor_omitted_count").GetInt32());
            Assert.Equal(0, probe.GetProperty("diagnostic_dropped_measurements").GetInt32());
        }
    }

    private sealed class NavigationContinuityGateway : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
            {
                var titles = Enumerable.Range(0, 9).Select(index => new AdvancedAnalysisSourceTitle(
                    $"Operation {index}", "title_anchor", null, 10 + index, 10 + index)).ToArray();
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(request.Offset, request.Offset + titles.Length, titles, false)));
            }
            AdvancedAnalysisResolvedEvidence[] additions = request.Operation == "search_corpus"
                ? Enumerable.Range(1, 3).Select(source => NavigationContinuityBody($"NAV-{source}", source, 1)).ToArray()
                : [NavigationContinuityBody($"READ-{Requests.Count}", request.DocPath!.Contains("2", StringComparison.Ordinal) ? 2 : 1,
                    request.PageStart ?? 1)];
            _evidence.AddRange(additions);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, additions, [], 0, Requests.Count));
        }
    }

    private static AdvancedAnalysisResolvedEvidence NavigationContinuityBody(string id, int source, int page)
        => BuildEvidence(id, $"Canonical operating guidance for source {source}, page {page}.",
            fileName: $"source-{source}.pdf", docId: $"{source:D8}-1111-4111-8111-111111111111",
            revisionId: $"{source:D8}-2222-4222-8222-222222222222", pageStart: page);
}
