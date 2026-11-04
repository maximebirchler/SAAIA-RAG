using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Previous_projection_continuity_is_a_fair_ordering_hint_and_never_an_extra_pin()
    {
        var initial = new[] { PreviousProjectionBody("A1"), PreviousProjectionBody("A2"),
            PreviousProjectionBody("A3"), PreviousProjectionBody("C1", source: 2) };
        var current = new[] { AccumulatedReadBodies("B1", "B2", "B3"), AccumulatedReadBodies("D1", "D2", "D3") };
        var active = initial.Concat(current.SelectMany(group => group)).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionQueries(context)["A1"] = ["first intent"];
        PreviousProjectionQueries(context)["A2"] = ["first intent"];
        PreviousProjectionQueries(context)["A3"] = ["first intent"];
        PreviousProjectionQueries(context)["C1"] = ["other intent"];
        PreviousProjectionRemember(context, initial);
        RememberBalancedExplicitBatch(context, current, "read_source");
        var priority = PreviousProjectionPriority(context, active, active);
        Assert.Equal(new[] { "B1", "D1", "A1", "B2", "C1", "D2", "A2", "B3", "A3", "D3" },
            priority.Select(item => item.Reference.EvidenceId));
        Assert.DoesNotContain("A1", AccumulatedReadIds(context));
        Assert.DoesNotContain("C1", AccumulatedReadIds(context));
        // An empty next projection stops protecting the prior projection. Only
        // independently explicit reads remain pinned by the existing policy.
        var pins = PreviousProjectionRemember(context, []);
        Assert.DoesNotContain("A1", pins); Assert.DoesNotContain("C1", pins);
        Assert.Equal(AccumulatedReadIds(context).Order(StringComparer.Ordinal), pins.Order(StringComparer.Ordinal));
        Assert.Empty(PreviousProjectionIds(context));
    }

    [Fact]
    public void Previous_projection_continuity_rebinds_active_ids_and_forgets_retired_ids()
    {
        var initial = new[] { PreviousProjectionBody("A1"), PreviousProjectionBody("A2") };
        var current = AccumulatedReadBodies("B1", "B2");
        var gateway = new AccumulatedReadGateway(initial.Concat(current).ToArray());
        var context = AccumulatedReadContext(gateway);
        PreviousProjectionRemember(context, initial); AccumulatedRemember(context, "read_source", current);
        var freshA2 = initial[1] with { Content = "Current canonical condition, never the earlier object." };
        gateway.Active = [freshA2, .. current];
        var priority = PreviousProjectionPriority(context, gateway.Active, initial.Concat(current).ToArray());
        Assert.Same(freshA2, Assert.Single(priority, item => item.Reference.EvidenceId == "A2"));
        Assert.DoesNotContain(priority, item => item.Reference.EvidenceId == "A1");
        Assert.Equal(new[] { "A2" }, PreviousProjectionIds(context));
        gateway.Active = current;
        PreviousProjectionPriority(context, gateway.Active, initial);
        Assert.Empty(PreviousProjectionIds(context));
    }

    [Fact]
    public void Previous_projection_continuity_preserves_the_current_six_primary_reads_and_targeted_window()
    {
        var initial = new[] { PreviousProjectionBody("A1"), PreviousProjectionBody("C1", source: 2) };
        var groups = BalancedExplicitBodies(false);
        var active = initial.Concat(groups.SelectMany(group => group)).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, initial); RememberBalancedExplicitBatch(context, groups, "read_source");
        var priority = PreviousProjectionPriority(context, active, active);
        Assert.Equal(groups.Select(group => group[0].Reference.EvidenceId), priority.Take(6).Select(item => item.Reference.EvidenceId));
        var beforeEmpty = priority.Select(item => item.Reference.EvidenceId).ToArray();
        AccumulatedRemember(context, "find_source_text", []);
        Assert.Equal(beforeEmpty, PreviousProjectionPriority(context, active, active).Select(item => item.Reference.EvidenceId));
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [0, 512, null], null)!;
        var updates = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        updates.Add("C1", window);
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        Assert.Equal("C1", PreviousProjectionPriority(context, active, active)[0].Reference.EvidenceId);
    }

    [Fact]
    public void Previous_projection_continuity_does_not_change_focus_before_any_explicit_read()
    {
        var initial = new[] { PreviousProjectionBody("A1"), PreviousProjectionBody("C1", source: 2) };
        var context = AccumulatedReadContext(new AccumulatedReadGateway(initial));
        PreviousProjectionRemember(context, initial);
        Assert.Equal(initial.Reverse(), PreviousProjectionPriority(context, initial, initial.Reverse().ToArray()));
        Assert.Empty(AccumulatedReadIds(context));
        Assert.Equal(new[] { "A1", "C1" }, PreviousProjectionIds(context));
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Previous_projection_initial_card_coexists_with_four_explicit_page_reads_on_actual_wire(
        string protocol, bool escaped)
    {
        const int cap = 65_536;
        var special = escaped ? " <tag q=\"v\"> & \\path\\ 漢字 🧪 e\u0301" : string.Empty;
        var initial = new[]
        {
            PreviousProjectionBody("INITIAL-CARD", "Documented initial procedure: isolate before applying the value. " + special + new string('a', 600), card: true),
            PreviousProjectionBody("SOURCE-CARRIER", source: 2)
        };
        var groups = Enumerable.Range(1, 4).Select(page => Enumerable.Range(1, 8).Select(hit =>
        {
            var id = $"PAGE{page}H{hit}";
            var text = hit == 1 ? page == 1 ? "Le seuil est 17 bar." : page == 4 ? "Exact page title" : "Short title"
                : "Canonical page body " + id + ": isolate the assembly before applying this value. " + special
                    + string.Concat(Enumerable.Repeat("\"scope\\path\" ", 140));
            return TextQualityFixture(PreviousProjectionBody(id, text, source: 2, page: page), "ok", false,
                ["source_conditions_retained"]);
        }).ToArray()).ToArray();
        var calls = Enumerable.Range(1, 4).Select(page => ("read_source", JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-2", pageStart = page, pageEnd = page, topK = 10
        }))).ToArray();
        var answered = NativeAnswered.Replace("E1", "PAGE1H1", StringComparison.Ordinal);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"threshold","topK":20}]}"""),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            protocol == "responses" ? NativeResponseAnswer(answered) : Completion(answered));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = cap;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        options.NativeResearchWorkspaceEnabled = false; options.NativeResearchActiveProposalEnabled = false;
        var gateway = new SequencedToolGateway(initial, groups[0], groups[1], groups[2], groups[3]);
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var result = await provider.ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(5, gateway.Searches.Count);
        Assert.All(gateway.Searches.Skip(1), read =>
        {
            Assert.Equal("read_source", read.Operation); Assert.Equal(10, read.TopK);
            Assert.Equal(initial[1].Reference.DocId, read.DocId); Assert.Equal(read.PageStart, read.PageEnd);
        });
        var first = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray(),
            item => item.GetProperty("evidenceId").GetString() == "INITIAL-CARD");
        Assert.Equal(initial[0].Content, first.GetProperty("content").GetString());
        var sent = factory.Requests[2].Body;
        var visible = WorkspaceUser(sent).GetProperty("evidence").EnumerateArray().ToArray();

        // Build the independent full/control oracle from raw canonical items,
        // not from an already budget-trimmed native projection.
        options.MaximumEvidencePromptCharacters = 1_000_000;
        var full = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            gateway.Evidence, new Dictionary<string, HashSet<string>>(), gateway.Evidence,
            new Dictionary<string, string>(), null, null)!;
        var inner = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        var records = JsonSerializer.SerializeToNode(full, inner)!.AsArray().ToDictionary(
            item => item!["evidenceId"]!.GetValue<string>(), item => item!, StringComparer.Ordinal);
        var needed = groups.Select(group => group[0].Reference.EvidenceId!).Concat(new[] { "INITIAL-CARD", "PAGE1H2" }).ToArray();
        var control = PreviousProjectionOracleWire(sent, records, needed, inner);
        // Both fitting and the final gate now use compact outer HTTP encoding;
        // preserve the legacy inner prompt JSON in this independent oracle.
        Assert.InRange(CrossBatchDocumentaryDifference(control, compact: true), 1, cap);
        Assert.True(CrossBatchDocumentaryDifference(PreviousProjectionOracleWire(sent, records,
            groups.SelectMany(group => group).Select(item => item.Reference.EvidenceId!), inner), compact: true) > cap);
        foreach (var id in needed)
        {
            // All calls above use existing APIs, so OLD fails this documentary
            // coexistence assertion rather than a missing new helper/member.
            var item = Assert.Single(visible, item => item.GetProperty("evidenceId").GetString() == id);
            var canonical = Assert.Single(gateway.Evidence, item => item.Reference.EvidenceId == id);
            Assert.Equal(canonical.Content, item.GetProperty("content").GetString());
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(canonical.Reference.DocId == initial[0].Reference.DocId ? "internal-source-1" : "internal-source-2",
                item.GetProperty("sourceKey").GetString());
            Assert.Equal(canonical.Reference.PageStart, item.GetProperty("physicalPageStart").GetInt32());
        }
        Assert.Equal(groups.Select(group => group[0].Reference.EvidenceId), visible.Take(4).Select(item => item.GetProperty("evidenceId").GetString()));
        Assert.Equal("content_card", Assert.Single(visible, item => item.GetProperty("evidenceId").GetString() == "INITIAL-CARD")
            .GetProperty("evidenceKind").GetString());
        // Navigation handles are documentary overhead too, even though their
        // omitted bodies remain non-citable until a subsequent explicit read.
        var handles = WorkspaceUser(sent).GetProperty("omittedEvidenceReadHandles");
        Assert.True(handles.GetProperty("navigationOnly").GetBoolean());
        Assert.False(handles.GetProperty("citable").GetBoolean());
        Assert.NotEmpty(handles.GetProperty("items").EnumerateArray());
        Assert.All(handles.GetProperty("items").EnumerateArray(), handle =>
            Assert.DoesNotContain(visible, item => item.GetProperty("evidenceId").GetString()
                == handle.GetProperty("evidenceId").GetString()));
        var actualWire = CrossBatchDocumentaryDifference(sent, compact: true);
        Assert.InRange(actualWire, 1, cap);
        Assert.Equal(actualWire, OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(sent));
        Assert.True(visible.Length < gateway.Evidence.Count);
        Assert.Equal(4, NativeCorrelationOutputs(sent, protocol).Length);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("inventory")]
    public void Previous_projection_rejected_memory_does_not_promote_or_remove_canonical_ids(string kind)
    {
        var rejected = PreviousProjectionBody("REJECTED");
        var kept = PreviousProjectionBody("KEPT", source: 2);
        var current = AccumulatedReadBodies("B1", "B2");
        var active = new[] { rejected, kept }.Concat(current).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, [rejected, kept]);
        AccumulatedRemember(context, "read_source", current);
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType(
            kind == "workspace" ? "ResearchWorkspaceItem" : "CandidateInventoryItem", BindingFlags.NonPublic)!;
        object?[] arguments = kind == "workspace"
            ? ["rejected", "Rejected entry", "other research", "rejected", "Explicit model decision", new[] { "REJECTED" }]
            : ["rejected", "Rejected entry", "internal-source-1", Array.Empty<string>(), Array.Empty<string>(), "rejected",
                "Explicit model decision", new[] { "REJECTED" }, new[] { "REJECTED" }];
        var item = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, arguments, null)!;
        var items = Array.CreateInstance(type, 1); items.SetValue(item, 0);
        context.GetType().GetProperty(kind == "workspace" ? "Workspace" : "CandidateInventory")!.SetValue(context, items);
        var priority = PreviousProjectionPriority(context, active, current.Concat(new[] { rejected, kept }).ToArray());
        Assert.Equal(new[] { "B1", "KEPT", "B2", "REJECTED" }, priority.Select(value => value.Reference.EvidenceId));
        Assert.Same(rejected, Assert.Single(priority, value => value.Reference.EvidenceId == "REJECTED"));
        Assert.DoesNotContain("REJECTED", AccumulatedReadIds(context));
        Assert.Equal(active.Length, priority.Count);
        // Explicitly requesting a window still leads. The rejection only
        // suppresses this new soft hint, not canonical access or explicit reads.
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [0, 256, null], null)!;
        var updates = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        updates.Add("REJECTED", window);
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        Assert.Equal("REJECTED", PreviousProjectionPriority(context, active, active)[0].Reference.EvidenceId);
    }

    private static AdvancedAnalysisResolvedEvidence PreviousProjectionBody(string id, string? text = null,
        int source = 1, int page = 1, bool card = false)
    {
        var body = BuildEvidence(id, text ?? "Canonical initial condition " + id + ": isolate the assembly.",
            fileName: "source-" + source + ".pdf", docId: source == 1 ? "11111111-1111-4111-8111-111111111111" : "33333333-3333-4333-8333-333333333333",
            revisionId: source == 1 ? "22222222-2222-4222-8222-222222222222" : "44444444-4444-4444-8444-444444444444", pageStart: page);
        if (!card) return body;
        return body with { Reference = new AdvancedAnalysisResultEvidence
        {
            EvidenceId = id, DocId = body.Reference.DocId, RevisionId = body.Reference.RevisionId,
            SourceHash = body.Reference.SourceHash, DocPath = body.Reference.DocPath, FileName = body.Reference.FileName,
            PageStart = page, PageEnd = page, ContentCardId = "55555555-5555-4555-8555-555555555555"
        }};
    }

    private static Dictionary<string, HashSet<string>> PreviousProjectionQueries(object context)
        => (Dictionary<string, HashSet<string>>)context.GetType().GetProperty("RetrievalQueriesByEvidenceId")!.GetValue(context)!;

    private static IReadOnlyList<string> PreviousProjectionIds(object context)
        => (IReadOnlyList<string>)context.GetType().GetProperty("PreviousProjectionEvidenceIds")!.GetValue(context)!;

    private static IReadOnlyCollection<string> PreviousProjectionRemember(object context, IReadOnlyList<AdvancedAnalysisResolvedEvidence> visible)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            visible, new Dictionary<string, HashSet<string>>(), visible, new Dictionary<string, string>(), null, null)!;
        return (IReadOnlyCollection<string>)InvokeSourceCatalogue(null, "RetainedResearchEvidenceIds", context, projection)!;
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PreviousProjectionPriority(object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused,
        AdvancedAnalysisLoadDescriptor? load = null)
        => (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizePreviousProjectionEvidence", context, load ?? BuildDirectRequest().Handoff.Load, active, focused)!;

    private static string PreviousProjectionOracleWire(string captured, IReadOnlyDictionary<string, JsonNode> records,
        IEnumerable<string> ids, JsonSerializerOptions inner)
    {
        var root = JsonNode.Parse(captured)!.AsObject();
        var messages = root[root.ContainsKey("input") ? "input" : "messages"]!.AsArray();
        var row = messages.OfType<JsonObject>().Single(item => item["role"]?.GetValue<string>() == "user");
        var user = JsonNode.Parse(row["content"]!.GetValue<string>())!.AsObject();
        user["evidence"] = new JsonArray(ids.Select(id => records[id].DeepClone()).ToArray());
        row["content"] = user.ToJsonString(inner);
        return root.ToJsonString(inner);
    }
}
