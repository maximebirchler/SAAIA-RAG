using System.Collections;
using System.Net;
using System.Reflection;
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
    public async Task Current_primary_page_read_preserves_titles_and_procedures_before_old_query_hits(string protocol)
    {
        var old = Enumerable.Range(0, 24).Select(index => PrimaryReadBody("OLD-" + index,
            "Previous lexical result " + index + ": " + new string('x', 1_600))).ToArray();
        var current = new[]
        {
            PrimaryReadBody("PAGE-LABEL", "Page 7", physicalContext: true),
            PrimaryReadBody("UNASSIGNED-HEADING", "Module C inspection", physicalContext: true),
            PrimaryReadBody("HEADING", "Seal inspection\nInspection conditions and required isolation."),
            PrimaryReadBody("TABLE", "Component | Limit\nSeal A | 17 bar\nSeal B | 12 bar"),
            PrimaryReadBody("PROCEDURE", "1. Isolate the assembly.\n2. Inspect each seal.\n3. Record the observed pressure. " + new string('a', 650)),
            PrimaryReadBody("CONTINUED", "4. Replace a damaged component.\n5. Restore the assembly only after verification. " + new string('b', 400))
        };
        var gateway = new PrimaryReadProjectionGateway(old, current, includeInventory: true);
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 16_000;
        options.NativeObservationReadEnabled = true;
        using var factory = PrimaryReadProjectionFactory(protocol);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_http_401", error.ErrorCode);
        var wire = factory.Requests[2].Body;
        var rows = WorkspaceUser(wire).GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Equal(new[] { "HEADING", "TABLE", "PROCEDURE", "CONTINUED" },
            rows.Take(4).Select(item => item.GetProperty("evidenceId").GetString()));
        foreach (var body in current)
        {
            var row = Assert.Single(rows, item => item.GetProperty("evidenceId").GetString() == body.Reference.EvidenceId);
            Assert.Equal(body.Content, row.GetProperty("content").GetString());
            Assert.False(row.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(body.Reference.PageStart, row.GetProperty("physicalPageStart").GetInt32());
        }
        Assert.True(rows.Length < gateway.Evidence.Count);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 16_000);
        var output = Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body;
        Assert.Equal(6, output.GetProperty("visibleEvidenceIdCount").GetInt32());
        Assert.Equal(0, output.GetProperty("unprojectedEvidenceIdCount").GetInt32());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Primary_read_inventory_does_not_hide_omitted_bodies_or_confuse_aliases_with_id_list_limits(string protocol)
    {
        var bodies = Enumerable.Range(0, 12).Select(index => PrimaryReadBody("READ-" + index,
            "Documented inspection " + index + ": " + new string('x', 2_000))).ToArray();
        var gateway = new PrimaryReadProjectionGateway([PrimaryReadBody("SEED", "Source overview.")],
            bodies.Append(bodies[0]).ToArray(), includeInventory: true);
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 10_000;
        options.NativeObservationReadEnabled = true;
        var logger = new NativeCorrelationLogger();
        using var factory = PrimaryReadProjectionFactory(protocol);
        await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        var wire = factory.Requests[2].Body;
        var output = Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body;
        Assert.Equal(13, output.GetProperty("returnedEvidenceCount").GetInt32());
        var feedback = output.GetProperty("projectionFeedback");
        Assert.Equal("some_returned_evidence_omitted", feedback.GetProperty("status").GetString());
        var count = output.GetProperty("visibleEvidenceIdCount").GetInt32();
        Assert.InRange(count, 1, 11);
        Assert.Equal(12 - count, output.GetProperty("unprojectedEvidenceIdCount").GetInt32());
        Assert.Equal(Math.Max(0, count - 8), output.GetProperty("visibleEvidenceIdsOmitted").GetInt32());
        Assert.Equal(12 - count, feedback.GetProperty("omittedEvidenceIdCount").GetInt32());
        Assert.All(feedback.GetProperty("omittedPhysicalLocators").EnumerateArray(), locator =>
        {
            Assert.Equal("internal-source-1", locator.GetProperty("sourceKey").GetString());
            Assert.Equal(7, locator.GetProperty("pageStart").GetInt32());
            Assert.Equal(7, locator.GetProperty("pageEnd").GetInt32());
        });
        Assert.True(output.TryGetProperty("secondaryInventory", out _));
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 10_000);
        var probe = Assert.Single(logger.Entries, entry => entry.Contains("operation=advanced.native_tool_results ", StringComparison.Ordinal));
        Assert.Contains("\"primary_read_returned_evidence_unique_count\":12", probe);
        Assert.Contains("\"primary_read_omitted_evidence_unique_count\":" + (12 - count), probe);
        Assert.DoesNotContain("Documented inspection", probe);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Primary_read_shortened_body_has_an_exact_visible_continuation_even_without_id_omissions(string protocol)
    {
        var body = PrimaryReadBody("LONG", "Inspection procedure: " + new string('z', 6_000));
        var gateway = new PrimaryReadProjectionGateway([PrimaryReadBody("SEED", "Source overview.")], [body], true);
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 16_000;
        options.NativeObservationReadEnabled = true;
        using var factory = PrimaryReadProjectionFactory(protocol);
        await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        var wire = factory.Requests[2].Body;
        var output = Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body;
        var feedback = output.GetProperty("projectionFeedback");
        Assert.Equal("some_returned_evidence_shortened", feedback.GetProperty("status").GetString());
        Assert.Equal(0, output.GetProperty("unprojectedEvidenceIdCount").GetInt32());
        var continuation = Assert.Single(feedback.GetProperty("visibleBodyContinuations").EnumerateArray());
        var row = Assert.Single(WorkspaceUser(wire).GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == "LONG");
        Assert.Equal("LONG", continuation.GetProperty("evidenceId").GetString());
        Assert.Equal(row.GetProperty("nextContentOffset").GetInt32(), continuation.GetProperty("nextContentOffset").GetInt32());
        Assert.Equal(body.Content.Length, continuation.GetProperty("originalContentLength").GetInt32());
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 16_000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Primary_read_diagnostics_accept_unidentified_empty_or_null_content_without_throwing(bool nullContent)
    {
        var body = PrimaryReadBody("READ", "Canonical inspection procedure.");
        var unidentified = PrimaryReadBody("UNIDENTIFIED", "") with
        {
            Reference = SourceCatalogueReference(body.Reference, null!, body.Reference.SourceHash),
            Content = nullContent ? null! : ""
        };
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 16_000;
        options.NativeObservationReadEnabled = true;
        var logger = new NativeCorrelationLogger();
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        var request = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, [body, unidentified], body.Reference);
        turn.GetType().GetProperty("CallsJson")!.SetValue(turn,
            """[{"id":"read","type":"function","function":{"name":"read_source","arguments":"{}"}}]""");
        var active = new[] { body, unidentified };
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            active, new Dictionary<string, HashSet<string>>(), active, new Dictionary<string, string>(), null, turn)!;
        var prompt = JsonDocument.Parse(SourceCataloguePrompt(provider, projection));
        Assert.Contains(prompt.RootElement.GetProperty("evidence").EnumerateArray(),
            item => !item.TryGetProperty("evidenceId", out var id) || id.ValueKind == JsonValueKind.Null);
        Assert.NotNull(InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, false));
        var probe = Assert.Single(logger.Entries, entry => entry.Contains("operation=advanced.native_tool_results ", StringComparison.Ordinal));
        Assert.Contains("\"primary_read_returned_evidence_unique_count\":1", probe);
    }

    [Fact]
    public void Primary_read_continuation_starts_at_the_first_gap_between_disjoint_visible_windows()
    {
        var body = PrimaryReadBody("READ", new string('a', 6_000));
        var request = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, [body], body.Reference);
        var turnType = turn.GetType();
        turnType.GetProperty("CallsJson")!.SetValue(turn,
            """[{"id":"read","type":"function","function":{"name":"read_source","arguments":"{}"}}]""");
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var windows = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        windows.Add("READ", Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [4_000, 200, null], null)!);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 30_000;
        options.NativeObservationReadEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            new[] { body }, new Dictionary<string, HashSet<string>>(), new[] { body }, new Dictionary<string, string>(), windows, turn)!;
        var messages = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, false)!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(messages));
        using var output = JsonDocument.Parse(json.RootElement[1].GetProperty("content").GetString()!);
        var continuation = Assert.Single(output.RootElement.GetProperty("projectionFeedback").GetProperty("visibleBodyContinuations").EnumerateArray());
        var prompt = JsonDocument.Parse(SourceCataloguePrompt(provider, projection));
        var row = Assert.Single(prompt.RootElement.GetProperty("evidence").EnumerateArray());
        var prefix = Assert.Single(row.GetProperty("supportingContentWindows").EnumerateArray());
        Assert.Equal(0, prefix.GetProperty("contentOffset").GetInt32());
        Assert.Equal(prefix.GetProperty("content").GetString()!.Length, continuation.GetProperty("nextContentOffset").GetInt32());
        Assert.InRange(continuation.GetProperty("nextContentOffset").GetInt32(), 1, 3_999);
        Assert.Equal(4_200, row.GetProperty("nextContentOffset").GetInt32());
    }

    [Theory]
    [InlineData("scope_hash")]
    [InlineData("reference_chunk")]
    [InlineData("reference_page")]
    [InlineData("retired")]
    [InlineData("deferred")]
    [InlineData("resource_limit")]
    [InlineData("find_source_text")]
    [InlineData("mixed_scope")]
    public void Current_primary_read_focus_cannot_reintroduce_or_promote_unbound_evidence(string change)
    {
        var old = PrimaryReadBody("OLD", "Existing visible body.");
        var body = PrimaryReadBody("READ", "New canonical procedure.");
        var observed = change is "reference_chunk" or "reference_page"
            ? body with { Reference = new AdvancedAnalysisResultEvidence
            {
                EvidenceId = body.Reference.EvidenceId, DocId = body.Reference.DocId, RevisionId = body.Reference.RevisionId,
                SourceHash = body.Reference.SourceHash, DocPath = body.Reference.DocPath,
                ChunkId = change == "reference_chunk" ? Guid.NewGuid().ToString("D") : body.Reference.ChunkId,
                PageStart = change == "reference_page" ? 8 : 7, PageEnd = change == "reference_page" ? 8 : 7
            }} : body;
        var scope = change == "scope_hash" ? SourceCatalogueReference(old.Reference, "OLD", "different-hash") : old.Reference;
        var request = new AdvancedAnalysisSearchRequest("", DocId: old.Reference.DocId, RevisionId: old.Reference.RevisionId,
            DocPath: old.Reference.DocPath, PageStart: 7, PageEnd: 7, Operation: change == "find_source_text" ? change : "read_source");
        var bodies = change == "mixed_scope"
            ? new[] { body, body with { Reference = SourceCatalogueReference(body.Reference, "OTHER", "different-hash") } }
            : new[] { observed };
        var turn = PrimaryReadProjectionTurn(request, bodies, scope,
            deferred: change == "deferred", limited: change == "resource_limit");
        var active = change == "retired" ? new[] { old } : new[] { old, body };
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", active, active, turn, null)!;
        Assert.Equal(active, result);
        Assert.All(result, item => Assert.Contains(active, current => ReferenceEquals(current, item)));
    }

    [Fact]
    public void Current_primary_read_focus_keeps_explicit_evidence_windows_first_and_rebinds_current_bodies()
    {
        var old = PrimaryReadBody("WINDOW", "Previously requested canonical window.");
        var body = PrimaryReadBody("READ", "Current canonical procedure.");
        var request = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, [body], body.Reference);
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var windows = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        windows.Add("WINDOW", Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [0, 256, null], null)!);
        var rebound = body with { Content = "Revalidated current canonical procedure." };
        var active = new[] { old, rebound };
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", active, active, turn, windows)!;
        Assert.Equal(new[] { "WINDOW", "READ" }, result.Select(item => item.Reference.EvidenceId));
        Assert.Same(rebound, result[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_primary_read_continuity_requires_an_existing_hint_and_does_not_prioritize_old_same_source_hits(bool allowed)
    {
        var oldSame = PrimaryReadBody("OLD-SAME", "Previous condition from the source currently being read.");
        var oldOther = BuildEvidence("OLD-OTHER", "Previously visible procedure from a different source.", "other.pdf");
        var current = new[] { PrimaryReadBody("FIRST", "Current inspection heading."), PrimaryReadBody("TAIL", "Current inspection procedure.") };
        var request = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, current, current[0].Reference);
        turn.GetType().GetProperty("ContinuityEvidenceIds")!.SetValue(turn,
            allowed ? new[] { "OLD-SAME", "OLD-OTHER", "RETIRED" } : Array.Empty<string>());
        var active = new[] { oldSame, oldOther }.Concat(current).ToArray();
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", active, active, turn, null)!;
        Assert.Equal(allowed ? new[] { "FIRST", "OLD-OTHER", "TAIL", "OLD-SAME" }
            : new[] { "FIRST", "TAIL", "OLD-SAME", "OLD-OTHER" }, result.Select(item => item.Reference.EvidenceId));
        Assert.All(result, item => Assert.Contains(active, original => ReferenceEquals(item, original)));
    }

    [Fact]
    public void Current_primary_read_continuity_is_bounded_to_eight_other_sources()
    {
        var prior = Enumerable.Range(0, 9).Select(index => BuildEvidence("PRIOR-" + index,
            "Previously visible substantive procedure.", "other-" + index + ".pdf")).ToArray();
        var current = new[] { PrimaryReadBody("FIRST", "Current inspection heading."), PrimaryReadBody("TAIL", "Current inspection procedure.") };
        var turn = PrimaryReadProjectionTurn(new("", PageStart: 7, PageEnd: 7, Operation: "read_source"), current, current[0].Reference);
        turn.GetType().GetProperty("ContinuityEvidenceIds")!.SetValue(turn, prior.Select(item => item.Reference.EvidenceId!).ToArray());
        var active = prior.Concat(current).ToArray();
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", active, active, turn, null)!;
        Assert.Equal("FIRST", result[0].Reference.EvidenceId);
        Assert.Equal(prior.Take(8), result.Skip(1).Take(8));
        Assert.Equal("TAIL", result[9].Reference.EvidenceId);
        Assert.Same(prior[8], result[10]);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("inventory")]
    public void Current_primary_read_continuity_cannot_promote_an_old_rejected_source(string kind)
    {
        var rejected = BuildEvidence("REJECTED", "Previously visible rejected candidate.", "rejected.pdf");
        var kept = BuildEvidence("KEPT", "Previously visible procedure retained for source coverage.", "kept.pdf");
        var current = new[] { PrimaryReadBody("FIRST", "Current inspection heading."), PrimaryReadBody("TAIL", "Current inspection procedure.") };
        var active = new[] { rejected, kept }.Concat(current).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, [rejected, kept]);
        var request = AccumulatedRemember(context, "read_source", current);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, PrimaryReadProjectionTurn(request, current, current[0].Reference));
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType(
            kind == "workspace" ? "ResearchWorkspaceItem" : "CandidateInventoryItem", BindingFlags.NonPublic)!;
        object?[] arguments = kind == "workspace"
            ? ["rejected", "Rejected entry", "other research", "rejected", "Explicit decision", new[] { "REJECTED" }]
            : ["rejected", "Rejected entry", "internal-source-1", Array.Empty<string>(), Array.Empty<string>(), "rejected",
                "Explicit decision", new[] { "REJECTED" }, new[] { "REJECTED" }];
        var item = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, arguments, null)!;
        var items = Array.CreateInstance(type, 1); items.SetValue(item, 0);
        context.GetType().GetProperty(kind == "workspace" ? "Workspace" : "CandidateInventory")!.SetValue(context, items);
        var previousOrder = PreviousProjectionPriority(context, active, active);
        var turn = context.GetType().GetProperty("NativeTurn")!.GetValue(context)!;
        Assert.DoesNotContain("REJECTED", (IReadOnlyList<string>)turn.GetType().GetProperty("ContinuityEvidenceIds")!.GetValue(turn)!);
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", previousOrder, active, turn, null)!;
        Assert.Equal(new[] { "FIRST", "KEPT", "TAIL", "REJECTED" }, result.Select(value => value.Reference.EvidenceId));
    }

    private static AdvancedAnalysisResolvedEvidence PrimaryReadBody(string id, string content, bool physicalContext = false)
        => BuildEvidence(id, content, "inspection.pdf", "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222", 7) with
        {
            SourceStructure = new("Inspection", "Inspection", null, null)
            { ChunkType = physicalContext ? "physical_page_context_v1" : "paragraph_v1" }
        };

    private static QueuedHttpClientFactory PrimaryReadProjectionFactory(string protocol)
        => NativeCorrelationFactory(protocol,
            [("read_source", """{"sourceKey":"internal-source-1","pageStart":7,"pageCount":1,"topK":20}""")], true);

    private static object PrimaryReadProjectionTurn(AdvancedAnalysisSearchRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies, AdvancedAnalysisResultEvidence scope,
        bool deferred = false, bool limited = false)
    {
        var results = new Dictionary<string, AdvancedAnalysisSearchObservation>
        {
            [(string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!] = new("", bodies, [], 1, 1,
                ResourceLimit: limited ? new("synthetic_limit", null, null, null) : null)
        };
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = type.GetConstructors().Single(ctor => ctor.GetParameters().Length == 8)
            .Invoke(["[]", new[] { request }, results, null, null, false, false, false]);
        type.GetProperty("CallRequests")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisSearchRequest> { ["read"] = request });
        type.GetProperty("CallSourceScopes")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisResultEvidence> { ["read"] = scope });
        if (deferred) type.GetProperty("DeferredCallIds")!.SetValue(turn, new HashSet<string> { "read" });
        return turn;
    }

    private sealed class PrimaryReadProjectionGateway(IReadOnlyList<AdvancedAnalysisResolvedEvidence> initial,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> read, bool includeInventory) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _active = [];
        private int _calls;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            var bodies = _calls++ == 0 ? initial : read;
            foreach (var item in bodies)
                if (_active.All(existing => existing.Reference.EvidenceId != item.Reference.EvidenceId)) _active.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, bodies, [], 1, _calls,
                AlternativeTableReadings: request.Operation == "read_source" && includeInventory
                    ? new(new(initial[0].Reference.SourceHash, "observed", 0, 1, [], []), null) : null));
        }
    }
}
