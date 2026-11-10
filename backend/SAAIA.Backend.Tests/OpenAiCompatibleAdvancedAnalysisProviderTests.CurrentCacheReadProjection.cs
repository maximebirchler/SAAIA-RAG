using System.Collections;
using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Repeated_current_cache_prefix_refreshes_all_eight_windows_before_historical_windows_under_actual_wire_cap(string protocol)
    {
        var gateway = new CurrentCacheProjectionGateway();
        var first = gateway.Bodies.Take(12).ToArray();
        var second = new[] { 0, 1, 12, 2, 13, 3, 4, 5, 14, 6, 15, 7, 16, 17, 18 }
            .Select(index => gateway.Bodies[index]).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            EvidencePrefixReply(protocol, [("read_source", """{"sourceKey":"internal-source-1","pageStart":7,"pageCount":1,"topK":20}""")]),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(first)),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(first.Skip(8).ToArray())),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(second)),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(second.Skip(8).ToArray())),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 7);
        options.MaximumEvidencePromptCharacters = 98_304;
        options.NativeResearchMaximumHistoryCharacters = 98_304;
        var logger = new NativeCorrelationLogger();
        AdvancedAnalysisProviderResult result;
        try { result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None); }
        catch (Exception e) { throw new InvalidOperationException("HTTP count=" + factory.Requests.Count + " " + string.Join(" / ", logger.Entries.Where(entry => entry.Contains("research_argument_rejection"))), e); }
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(7, factory.Requests.Count);
        foreach (var message in factory.Requests.Skip(1))
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(message.Body), 1, 98_304);
        var prefix = WorkspaceUser(factory.Requests[5].Body);
        var outputs = NativeCorrelationOutputs(factory.Requests[5].Body, protocol);
        Assert.Equal(15, outputs.Length);
        for (var index = 0; index < 8; index++)
        {
            Assert.Equal("projected", outputs[index].Body.GetProperty("status").GetString());
            AssertCurrentCacheProjectionWindow(prefix, second[index]);
        }
        Assert.All(outputs.Skip(8), output =>
            Assert.Equal("non_executed_batch_limit", output.Body.GetProperty("status").GetString()));
        var afterTail = WorkspaceUser(factory.Requests[6].Body);
        foreach (var body in second.Skip(8)) AssertCurrentCacheProjectionWindow(afterTail, body);
        foreach (var message in factory.Requests.Skip(1))
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(message.Body), 1, 98_304);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    private static void AssertCurrentCacheProjectionWindow(JsonElement user, AdvancedAnalysisResolvedEvidence body)
    {
        var rows = user.GetProperty("evidence").EnumerateArray().Where(row => row.GetProperty("evidenceId").GetString() == body.Reference.EvidenceId).ToArray();
        Assert.True(rows.Length == 1, "The admitted current window must be visible under its canonical ID: " + body.Reference.EvidenceId);
        var item = rows[0];
        Assert.Equal(0, item.GetProperty("contentOffset").GetInt32());
        Assert.Equal(body.Content[..Math.Min(3000, body.Content.Length)], item.GetProperty("content").GetString());
    }

    [Fact]
    public void An_unchanged_cache_window_refreshes_priority_without_reporting_new_evidence_progress()
    {
        var bodies = AccumulatedReadBodies("EARLIER", "LATER");
        var context = AccumulatedReadContext(new AccumulatedReadGateway(bodies));
        object Windows(string id)
        {
            var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
            var map = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), type))!;
            map.Add(id, Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [0, 512, null], null)!);
            return map;
        }
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", Windows("EARLIER"), context)!);
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", Windows("LATER"), context)!);
        Assert.Equal(0, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", Windows("EARLIER"), context)!);
        Assert.Equal("EARLIER", PreviousProjectionPriority(context, bodies, bodies)[0].Reference.EvidenceId);
    }

    [Theory]
    [InlineData("chat-completions", "absent", false)]
    [InlineData("responses", "absent", false)]
    [InlineData("chat-completions", "gap", false)]
    [InlineData("responses", "gap", false)]
    [InlineData("chat-completions", "partial_support", false)]
    [InlineData("responses", "partial_support", false)]
    [InlineData("chat-completions", "support", true)]
    [InlineData("responses", "support", true)]
    public void Cache_window_tool_feedback_reports_the_requested_window_per_item_including_supporting_windows(
        string protocol, string shape, bool visible)
    {
        var body = PrimaryReadBody("CACHED", new string('a', 6_000));
        var offset = shape == "gap" ? 3_000 : shape == "partial_support" ? 2_200 : 1_000;
        var arguments = JsonSerializer.Serialize(new { evidenceId = "CACHED", offset, length = 512 });
        var calls = JsonSerializer.Serialize(new[] { new { id = "cache", type = "function",
            function = new { name = "read_evidence", arguments } } });
        var turnType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = turnType.GetConstructors().Single(ctor => ctor.GetParameters().Length == 8)
            .Invoke([calls, null, new Dictionary<string, AdvancedAnalysisSearchObservation>(), null, null, false, false, true]);
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var windows = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        windows.Add("CACHED", Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [4_000, 200, null], null)!);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 30_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var active = shape == "absent" ? Array.Empty<AdvancedAnalysisResolvedEvidence>() : [body];
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            active, new Dictionary<string, HashSet<string>>(), active, new Dictionary<string, string>(), windows, turn)!;
        var messages = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, protocol == "responses")!;
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(messages));
        var row = Assert.Single(envelope.RootElement.EnumerateArray(), item =>
            protocol == "responses" ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var output = JsonDocument.Parse(row.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal(visible ? "projected" : "not_projected", output.RootElement.GetProperty("status").GetString());
        Assert.Equal(offset, output.RootElement.GetProperty("requestedWindow").GetProperty("offset").GetInt32());
        if (!visible) Assert.Contains("not fully visible", output.RootElement.GetProperty("instruction").GetString());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task An_already_stored_but_omitted_cache_window_is_visible_through_actual_no_progress_recovery(string protocol)
    {
        var gateway = new CurrentCacheProjectionGateway();
        var first = gateway.Bodies.Take(12).ToArray();
        var second = new[] { 0, 1, 12, 2, 13, 3, 4, 5, 14, 6, 15, 7, 16, 17, 18 }
            .Select(index => gateway.Bodies[index]).ToArray();
        var replies = new[] { Completion(GuidancePlan(1)),
            EvidencePrefixReply(protocol, [("read_source", """{"sourceKey":"internal-source-1","pageStart":7,"pageCount":1,"topK":20}""")]),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(first)),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(first.Skip(8).ToArray())),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(second)),
            EvidencePrefixReply(protocol, EvidencePrefixCalls(second.Skip(8).ToArray())) };
        var wires = new List<string>();
        AdvancedAnalysisResolvedEvidence? repeated = null;
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync();
            wires.Add(wire);
            if (wires.Count <= replies.Length)
                return await AdaptProtocolAgnosticFixtureAsync(request, replies[wires.Count - 1], CancellationToken.None);
            var user = WorkspaceUser(wire);
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 98_304);
            if (wires.Count == 7)
            {
                var visible = user.GetProperty("evidence").EnumerateArray()
                    .Select(item => item.GetProperty("evidenceId").GetString()).ToHashSet(StringComparer.Ordinal);
                repeated = first.FirstOrDefault(body => !visible.Contains(body.Reference.EvidenceId));
                Assert.NotNull(repeated);
                Assert.Contains(user.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray(),
                    item => item.GetProperty("evidenceId").GetString() == repeated.Reference.EvidenceId);
                return EvidencePrefixReply(protocol, EvidencePrefixCalls([repeated]));
            }
            Assert.Equal(8, wires.Count);
            AssertCurrentCacheProjectionWindow(user, repeated!);
            Assert.Equal(repeated!.Reference.EvidenceId,
                user.GetProperty("evidence")[0].GetProperty("evidenceId").GetString());
            Assert.Equal("evidence_window_already_available", user.GetProperty("researchFeedback").GetProperty("reasonCode").GetString());
            Assert.Equal("not_changed", Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body.GetProperty("status").GetString());
            Assert.Equal(2, gateway.Requests.Count);
            return await AdaptProtocolAgnosticFixtureAsync(request, EarlyReply(protocol, GuidanceInsufficiency), CancellationToken.None);
        });
        var options = PrefixOptions(protocol, 8);
        options.MaximumEvidencePromptCharacters = options.NativeResearchMaximumHistoryCharacters = 98_304;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Equal(8, result.ProviderCallCount);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    private sealed class CurrentCacheProjectionGateway : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Bodies { get; } = Enumerable.Range(0, 19)
            .Select(i => NavigationCard("CURRENT-CACHE-" + i, "Inspection procedure " + i, 1, i + 1, card: false) with
            {
                Content = "Procedure " + i + ": isolate the documented assembly before inspection.\n"
                    + new string('<', 1_000)
            }).ToArray();
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        private readonly List<AdvancedAnalysisResolvedEvidence> active = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => active;
        public AdvancedAnalysisToolBudget Budget => new(64, Requests.Count, 300_000, 0, 128, Evidence.Count);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                var older = Enumerable.Range(0, 109).Select(index => NavigationCard("OLDER-CACHE-" + index, "Earlier inspection " + index, 1, index + 1, card: false) with { Content = "Earlier observed body " + index + ": " + new string('x', 1_000) }).ToArray();
                active.AddRange(older);
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, older, [], 0, 1));
            }
            Assert.Equal("read_source", request.Operation);
            Assert.Equal(2, Requests.Count);
            active.AddRange(Bodies);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, Bodies, [], 0, 2));
        }
    }
}
