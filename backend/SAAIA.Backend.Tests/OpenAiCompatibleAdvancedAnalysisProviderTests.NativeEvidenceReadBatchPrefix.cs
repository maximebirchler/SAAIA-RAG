using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Legacy_evidence_read_parser_keeps_its_three_argument_signature_and_eight_window_execution_limit()
    {
        using var payload = JsonDocument.Parse("{\"evidenceReads\":[{},{},{},{},{},{},{},{},{}]}");
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadRequests", payload.RootElement, null, null));
        Assert.Equal("advanced_native_evidence_read_invalid", error.ErrorCode);
    }

    [Theory]
    [InlineData("chat-completions", 12, false)]
    [InlineData("responses", 12, false)]
    [InlineData("chat-completions", 15, false)]
    [InlineData("responses", 15, false)]
    [InlineData("chat-completions", 12, true)]
    [InlineData("responses", 12, true)]
    [InlineData("chat-completions", 15, true)]
    [InlineData("responses", 15, true)]
    public async Task Native_evidence_read_batch_executes_eight_then_exact_tail_without_documentary_io(
        string protocol, int count, bool documentaryQuotaExhausted)
    {
        var gateway = new EvidencePrefixGateway(count, documentaryQuotaExhausted);
        var calls = EvidencePrefixCalls(gateway.Bodies);
        using var factory = new QueuedHttpClientFactory(
            Completion(GuidancePlan(1)),
            EvidencePrefixReply(protocol, calls), EvidencePrefixReply(protocol, calls.Skip(8).ToArray()),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 4);
        options.MaximumEvidencePromptCharacters = 98_304;
        options.NativeResearchMaximumHistoryCharacters = 98_304;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Single(gateway.Requests);
        Assert.Equal(4, factory.Requests.Count);
        var afterPrefix = WorkspaceUser(factory.Requests[2].Body);
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(count, outputs.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(protocol == "responses" ? "call-budget-" + i : "call_" + i, outputs[i].CallId);
            Assert.Equal(i < 8 ? "projected" : "non_executed_batch_limit", outputs[i].Body.GetProperty("status").GetString());
            AssertEvidencePrefixWindow(afterPrefix, gateway.Bodies[i], i < 8 ? 3000 : 2400);
            if (i < 8) continue;
            Assert.Equal(8, outputs[i].Body.GetProperty("batchCallLimit").GetInt32());
            Assert.False(outputs[i].Body.GetProperty("stateChanged").GetBoolean());
            Assert.Empty(outputs[i].Body.GetProperty("visibleEvidenceIds").EnumerateArray());
            Assert.False(outputs[i].Body.GetProperty("resumptionRequiresResearchAllowed").GetBoolean());
            Assert.True(outputs[i].Body.GetProperty("resumptionRequiresEvidenceReadingAllowed").GetBoolean());
            Assert.Contains("evidenceReadingAllowed", outputs[i].Body.GetProperty("instruction").GetString());
        }
        Assert.True(afterPrefix.GetProperty("researchTools").GetProperty("evidenceReadingAllowed").GetBoolean());
        if (documentaryQuotaExhausted)
            Assert.False(afterPrefix.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        var afterTail = WorkspaceUser(factory.Requests[3].Body);
        Assert.All(gateway.Bodies, body => AssertEvidencePrefixWindow(afterTail, body, 3000));
        Assert.Equal(count - 8, NativeCorrelationOutputs(factory.Requests[3].Body, protocol).Length);
        Assert.All(NativeCorrelationOutputs(factory.Requests[3].Body, protocol),
            output => Assert.Equal("projected", output.Body.GetProperty("status").GetString()));
        foreach (var message in factory.Requests.Skip(1))
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(message.Body), 1, 98_304);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
    }

    private static (string Name, string Arguments)[] EvidencePrefixCalls(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies)
        => bodies.Select(body => ("read_evidence", JsonSerializer.Serialize(new
        {
            evidenceId = body.Reference.EvidenceId, offset = 0, length = 3000
        }))).ToArray();

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_evidence_read_conflicting_window_in_deferred_tail_recovers_before_any_window_changes(string protocol)
    {
        var gateway = new EvidencePrefixGateway(12, false);
        var calls = EvidencePrefixCalls(gateway.Bodies);
        calls[^1] = ("read_evidence", calls[0].Arguments.Replace("3000", "2800", StringComparison.Ordinal));
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), EvidencePrefixReply(protocol, calls),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 4);
        options.MaximumEvidencePromptCharacters = 98_304;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                    gateway, CancellationToken.None);
        Assert.Single(gateway.Requests);
        Assert.All(NativeCorrelationOutputs(factory.Requests[2].Body, protocol), output =>
        {
            Assert.Equal("batch_rejected_before_execution", output.Body.GetProperty("status").GetString());
            Assert.Equal("read_evidence_conflicting_windows", output.Body.GetProperty("argumentFeedback").GetProperty("reasonCode").GetString());
        });
        Assert.All(gateway.Checkpoints, checkpoint => Assert.True(checkpoint.EvidenceReadWindows is null or { Count: 0 }));
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Native_evidence_read_invalid_tail_rejects_all_windows_with_typed_feedback(
        string protocol, bool splitUnicode)
    {
        var gateway = new EvidencePrefixGateway(12, false, unicodeTail: splitUnicode);
        var calls = EvidencePrefixCalls(gateway.Bodies);
        calls[^1] = ("read_evidence", JsonSerializer.Serialize(new
        {
            evidenceId = splitUnicode ? gateway.Bodies[^1].Reference.EvidenceId : "UNPUBLISHED-ID",
            offset = splitUnicode ? gateway.Bodies[^1].Content.IndexOf("\U0001F642", StringComparison.Ordinal) + 1 : 0,
            length = 512
        }));
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), EvidencePrefixReply(protocol, calls),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 4);
        options.MaximumEvidencePromptCharacters = 98_304;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);
        Assert.Single(gateway.Requests);
        var wire = factory.Requests[2].Body;
        Assert.All(NativeCorrelationOutputs(wire, protocol), output =>
        {
            Assert.Equal("batch_rejected_before_execution", output.Body.GetProperty("status").GetString());
            Assert.Equal(splitUnicode ? "read_evidence_offset_splits_unicode_character" : "advanced_native_evidence_read_invalid",
                output.Body.GetProperty("argumentFeedback").GetProperty("reasonCode").GetString());
        });
        Assert.All(gateway.Bodies, body => AssertEvidencePrefixWindow(WorkspaceUser(wire), body, 2400));
        Assert.All(gateway.Checkpoints, checkpoint => Assert.True(checkpoint.EvidenceReadWindows is null or { Count: 0 }));
    }

    private static (string Name, string Arguments)[] TitlePrefixCalls()
        => Enumerable.Range(0, 5).Select(offset => ("list_source_titles", JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", offset, limit = 20, titleContains = (string?)null,
            pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = false
        }))).ToArray();

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_title_operation_limit_preserves_four_page_prefix_and_defers_entire_tail_in_order(string protocol)
    {
        var calls = TitlePrefixCalls().Append(("read_source",
            """{"sourceKey":"internal-source-1","pageStart":7,"pageCount":1,"topK":20}""")).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), EvidencePrefixReply(protocol, calls),
            EvidencePrefixReply(protocol, calls.Skip(4).ToArray()), EarlyReply(protocol, GuidanceInsufficiency));
        var gateway = new TitlePrefixGateway();
        var options = PrefixOptions(protocol, 4);
        options.NativeSourceTitleNavigationEnabled = true;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 5), gateway.Requests.Where(r => r.Operation == "list_source_titles").Select(r => r.Offset));
        Assert.Single(gateway.Requests, r => r.Operation == "read_source");
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(6, outputs.Length);
        Assert.All(outputs.Take(4), output => Assert.Equal("executed", output.Body.GetProperty("status").GetString()));
        Assert.All(outputs.Skip(4), output =>
        {
            Assert.Equal("non_executed_batch_limit", output.Body.GetProperty("status").GetString());
            Assert.Equal("list_source_titles", output.Body.GetProperty("limitingOperation").GetString());
            Assert.Equal(4, output.Body.GetProperty("operationCallLimit").GetInt32());
            Assert.Equal(8, output.Body.GetProperty("batchCallLimit").GetInt32());
            Assert.False(output.Body.GetProperty("stateChanged").GetBoolean());
        });
        Assert.DoesNotContain("READ-7", WorkspaceUser(factory.Requests[2].Body).GetProperty("evidence").GetRawText());
        Assert.All(NativeCorrelationOutputs(factory.Requests[3].Body, protocol),
            output => Assert.Equal("executed", output.Body.GetProperty("status").GetString()));
        Assert.Contains("READ-7", WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").GetRawText());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_title_invalid_deferred_fifth_page_rejects_before_any_prefix_documentary_effect(string protocol)
    {
        var calls = TitlePrefixCalls();
        calls[^1] = ("list_source_titles", calls[^1].Arguments.Replace("\"offset\":4", "\"offset\":10001", StringComparison.Ordinal));
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), EvidencePrefixReply(protocol, calls));
        var gateway = new TitlePrefixGateway();
        var options = PrefixOptions(protocol, 4);
        options.NativeSourceTitleNavigationEnabled = true;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                    gateway, CancellationToken.None));
        Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Requests);
        Assert.DoesNotContain(gateway.Requests, r => r.Operation == "list_source_titles");
    }

    private static HttpResponseMessage EvidencePrefixReply(string protocol, (string Name, string Arguments)[] calls)
        => protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);

    private static void AssertEvidencePrefixWindow(JsonElement user, AdvancedAnalysisResolvedEvidence body, int length)
    {
        var item = Assert.Single(user.GetProperty("evidence").EnumerateArray(),
            row => row.GetProperty("evidenceId").GetString() == body.Reference.EvidenceId);
        Assert.Equal(0, item.GetProperty("contentOffset").GetInt32());
        Assert.Equal(body.Content[..length], item.GetProperty("content").GetString());
    }

    private sealed class EvidencePrefixGateway(int count, bool exhausted, bool unicodeTail = false) : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Bodies { get; } = Enumerable.Range(1, count)
            .Select(i => NavigationCard("CACHE-" + i, "Procedure " + i, 1, i, card: false) with
            {
                Content = "Procedure " + i + "\nIsolate the documented component before inspection.\n"
                    + new string('x', 3200) + (unicodeTail && i == count ? "\U0001F642" : "")
                    + "\nRecord the observed condition after the intervention."
            }).ToArray();
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public List<AdvancedAnalysisResearchCheckpoint> Checkpoints { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => Requests.Count == 0 ? [] : Bodies;
        public AdvancedAnalysisToolBudget Budget => new(exhausted ? 1 : 64, Requests.Count, 300_000, 0, 128, Evidence.Count);

        public Task SaveResearchCheckpointAsync(AdvancedAnalysisResearchCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Assert.Empty(Requests);
            Requests.Add(request);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, Bodies, [], 0, 1));
        }
    }

    private sealed class TitlePrefixGateway : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> evidence = [];
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => evidence;

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(request.Offset, request.Offset + 1,
                        [new("Documented checkpoint " + request.Offset, "title_anchor", null, 7, 7)], false)));
            var item = NavigationCard(request.Operation == "read_source" ? "READ-7" : "TITLE-BOOTSTRAP",
                "Documented checkpoint", 1, request.Operation == "read_source" ? 7 : 1, card: false);
            evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [item], [], 0, Requests.Count));
        }
    }
}
