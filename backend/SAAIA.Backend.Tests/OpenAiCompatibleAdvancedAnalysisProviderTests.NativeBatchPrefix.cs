using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static (string Name, string Arguments)[] PrefixReadCalls(int first, int count)
        => Enumerable.Range(first, count).Select(page => ("read_source", JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = page, pageCount = 1, topK = 20
        }))).ToArray();

    private static AdvancedAnalysisOptions PrefixOptions(string protocol, int modelCalls, bool workspace = false)
    {
        var options = EarlyOptions(protocol, modelCalls);
        options.SemanticCriticEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        options.NativeResearchWorkspaceEnabled = workspace;
        options.MaximumPlanQueries = 8;
        return options;
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_batch_prefix_executes_eight_reads_and_defers_two_for_a_later_turn(string protocol)
    {
        var allCalls = PrefixReadCalls(1, 10);
        var firstReply = protocol == "responses" ? BudgetNativeResponseFunctions(allCalls) : NativeCompletion(allCalls);
        if (protocol == "responses")
        {
            var envelope = JsonNode.Parse(await firstReply.Content.ReadAsStringAsync())!.AsObject();
            envelope["output"]!.AsArray().Insert(0, new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "reason-prefix", ["summary"] = new JsonArray(),
                ["encrypted_content"] = "opaque-prefix-continuity"
            });
            firstReply.Content = new StringContent(envelope.ToJsonString());
        }
        var remaining = PrefixReadCalls(9, 2);
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), firstReply,
            protocol == "responses" ? BudgetNativeResponseFunctions(remaining) : NativeCompletion(remaining),
            EarlyReply(protocol, GuidanceInsufficiency));
        var gateway = new PrefixToolGateway();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, PrefixOptions(protocol, 4), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.Equal(Enumerable.Range(1, 10), reads.Select(request => request.PageStart!.Value));
        Assert.All(reads, request =>
        {
            Assert.Equal(request.PageStart, request.PageEnd);
            Assert.Equal(20, request.TopK);
            Assert.Equal(gateway.Initial.Reference.DocId, request.DocId);
            Assert.Equal(gateway.Initial.Reference.RevisionId, request.RevisionId);
        });
        var firstFollowup = factory.Requests[2].Body;
        var outputs = NativeCorrelationOutputs(firstFollowup, protocol);
        Assert.Equal(10, outputs.Length);
        for (var i = 0; i < outputs.Length; i++)
        {
            Assert.Equal(protocol == "responses" ? "call-budget-" + i : "call_" + i, outputs[i].CallId);
            Assert.Equal(i < 8 ? "executed" : "non_executed_batch_limit", outputs[i].Body.GetProperty("status").GetString());
            if (i < 8) continue;
            Assert.Equal(8, outputs[i].Body.GetProperty("batchCallLimit").GetInt32());
            Assert.Empty(outputs[i].Body.GetProperty("visibleEvidenceIds").EnumerateArray());
            Assert.False(outputs[i].Body.GetProperty("stateChanged").GetBoolean());
        }
        Assert.DoesNotContain("READ-9", WorkspaceUser(firstFollowup).GetProperty("evidence").GetRawText());
        Assert.DoesNotContain("READ-10", WorkspaceUser(firstFollowup).GetProperty("evidence").GetRawText());
        Assert.Equal(8, WorkspaceUser(firstFollowup).GetProperty("researchTools").GetProperty("maximumQueries").GetInt32());
        Assert.All(NativeCorrelationOutputs(factory.Requests[3].Body, protocol),
            output => Assert.Equal("executed", output.Body.GetProperty("status").GetString()));
        Assert.Contains("READ-10", WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").GetRawText());
        if (protocol == "responses")
            Assert.Contains("opaque-prefix-continuity", firstFollowup);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Native_batch_prefix_invalid_tail_refuses_every_prefix_operation(string protocol, bool invalidScope)
    {
        var calls = PrefixReadCalls(1, 10);
        calls[9] = ("read_source", invalidScope
            ? """{"sourceKey":"unobserved-source","pageStart":10,"pageCount":1,"topK":20}"""
            : """{"sourceKey":"internal-source-1","pageStart":10,"pageCount":5,"topK":20}""");
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, GuidanceInsufficiency));
        var gateway = new PrefixToolGateway();
        var run = new OpenAiCompatibleAdvancedAnalysisProvider(factory, PrefixOptions(protocol, 4), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);
        if (invalidScope)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => run);
            Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
        }
        else
        {
            await run;
            var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
            Assert.Equal(10, outputs.Length);
            Assert.All(outputs, output => Assert.Equal("batch_rejected_before_execution",
                output.Body.GetProperty("status").GetString()));
        }
        Assert.Single(gateway.Requests);
        Assert.Single(gateway.Evidence);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_batch_prefix_defers_memory_candidate_and_window_mutations_in_original_order(string protocol)
    {
        var request = BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option");
        var update = CandidateUpdate("deferred-update", "CHECKPOINT", "internal-source-1",
            request.Handoff.Load.Columns[0], "body_verified", [], ["E1"]);
        var calls = PrefixReadCalls(1, 8).Concat(new[]
        {
            ("save_research_state", WorkspaceOne),
            ("save_candidate_inventory", update),
            ("read_evidence", """{"evidenceId":"E1","offset":1024,"length":512}""")
        }).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, GuidanceInsufficiency));
        var gateway = new PrefixToolGateway();
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, PrefixOptions(protocol, 3, workspace: true), null)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal(8, gateway.Requests.Count(item => item.Operation == "read_source"));
        var followup = WorkspaceUser(factory.Requests[2].Body);
        Assert.Empty(followup.GetProperty("researchWorkspace").GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(followup.GetProperty("candidateInventory").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("status").GetString() == "body_verified");
        var initial = followup.GetProperty("evidence").EnumerateArray().Single(item => item.GetProperty("evidenceId").GetString() == "E1");
        Assert.Equal(0, initial.GetProperty("contentOffset").GetInt32());
        Assert.All(NativeCorrelationOutputs(factory.Requests[2].Body, protocol).Skip(8),
            output => Assert.Equal("non_executed_batch_limit", output.Body.GetProperty("status").GetString()));
        Assert.True(gateway.Checkpoint?.EvidenceReadWindows is null or { Count: 0 });
    }

    [Fact]
    public async Task Native_batch_prefix_does_not_reserve_documentary_budget_for_its_tail()
    {
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            BudgetNativeResponseFunctions(PrefixReadCalls(1, 10)), NativeResponseAnswer(GuidanceInsufficiency));
        var gateway = new PrefixToolGateway(maximumCalls: 9); // One bootstrap and eight remaining.
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, PrefixOptions("responses", 3), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);
        Assert.Equal(9, gateway.Requests.Count);
        Assert.Equal(0, gateway.Budget.RemainingCalls);
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, "responses");
        Assert.Equal(8, outputs.Count(output => output.Body.GetProperty("status").GetString() == "executed"));
        Assert.Equal(2, outputs.Count(output => output.Body.GetProperty("status").GetString() == "non_executed_batch_limit"));
        var followup = WorkspaceUser(factory.Requests[2].Body);
        Assert.False(followup.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.False(followup.TryGetProperty("researchResourceLimit", out _));
        Assert.All(outputs.Skip(8), output =>
        {
            Assert.True(output.Body.GetProperty("resumptionRequiresResearchAllowed").GetBoolean());
            Assert.Contains("researchTools.researchAllowed", output.Body.GetProperty("instruction").GetString());
            Assert.False(output.Body.TryGetProperty("retryAllowed", out _));
        });
    }

    [Fact]
    public async Task Native_batch_prefix_validates_deferred_candidate_merge_before_any_documentary_effect()
    {
        var request = BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option");
        var initialKey = (string)InvokeSourceCatalogue(null, "BuildCandidateInventoryKey", "internal-source-1", "CHECKPOINT")!;
        // Individually valid update, but it would replace a source-1 key with source-2 evidence.
        var update = CandidateUpdate(initialKey, "OTHER CHECKPOINT", "internal-source-2",
            request.Handoff.Load.Columns[0], "body_verified", [], ["E2"]);
        var calls = PrefixReadCalls(1, 8).Append(("save_candidate_inventory", update)).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)), BudgetNativeResponseFunctions(calls));
        var gateway = new PrefixToolGateway(additionalSource: true);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, PrefixOptions("responses", 3, workspace: true), null)
                .ExecuteAsync(request, gateway, CancellationToken.None));
        Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Requests);
        Assert.Equal(2, gateway.Evidence.Count);
        Assert.NotNull(gateway.Checkpoint);
        Assert.Contains(gateway.Checkpoint.Candidates, item => item.Key == initialKey && item.SourceKey == "internal-source-1");
        Assert.DoesNotContain(gateway.Checkpoint.Candidates, item => item.Status == "body_verified");
    }

    private sealed class PrefixToolGateway(int maximumCalls = 64, bool additionalSource = false) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public AdvancedAnalysisResolvedEvidence Initial { get; } =
            NavigationCard("E1", "CHECKPOINT", 1, 1, card: false) with
            { Content = "CHECKPOINT\n" + new string('x', 3000) + "\nIsolate the assembly before adjustment." };
        private AdvancedAnalysisResolvedEvidence? Additional { get; } = additionalSource
            ? NavigationCard("E2", "OTHER CHECKPOINT", 2, 1, card: false) with
                { Content = "OTHER CHECKPOINT\nInspect the second assembly before adjustment." }
            : null;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public AdvancedAnalysisResearchCheckpoint? Checkpoint { get; private set; }
        public AdvancedAnalysisToolBudget Budget => new(maximumCalls, Requests.Count, 300_000, 0, 128, _evidence.Count);

        public Task SaveResearchCheckpointAsync(AdvancedAnalysisResearchCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Checkpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Operation == "read_source"
                ? NavigationCard("READ-" + request.PageStart, "Checkpoint " + request.PageStart,
                    1, request.PageStart!.Value, card: false)
                : Initial;
            var found = request.Operation != "read_source" && Additional is not null
                ? new[] { body, Additional } : [body];
            foreach (var item in found)
                if (!_evidence.Contains(item)) _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 0, Requests.Count));
        }
    }
}
