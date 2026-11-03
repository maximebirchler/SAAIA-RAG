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
    public async Task Explicit_batch_shares_the_existing_wire_budget_across_six_queries_before_extra_hits(
        string protocol, bool escaped)
    {
        var groups = BalancedExplicitBodies(escaped);
        var active = groups.SelectMany(group => group).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        RememberBalancedExplicitBatch(context, groups);

        // These are the pre-existing projection APIs, so the test also compiles
        // against the grouped-by-query implementation for a causal RED run.
        var focused = AccumulatedReadPriority(context, active, active);
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 40_000;
        options.NativeObservationReadEnabled = false;
        options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var projection = BalancedExplicitProjection(provider, context, active, focused);
        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(),
            "writer", "Return JSON.", SourceCataloguePrompt(provider, projection), 128,
            CancellationToken.None, true, null, projection));

        var body = Assert.Single(factory.Requests).Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 40_000);
        var visible = WorkspaceUser(body).GetProperty("evidence").EnumerateArray().ToArray();
        Assert.True(visible.Length < active.Length, "This fixture must actually saturate the unchanged documentary cap.");
        foreach (var group in groups)
        {
            var first = Assert.Single(visible, item => item.GetProperty("evidenceId").GetString() == group[0].Reference.EvidenceId);
            Assert.Equal(group[0].Content, first.GetProperty("content").GetString());
            Assert.False(first.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(1, first.GetProperty("physicalPageStart").GetInt32());
            Assert.Equal(1, first.GetProperty("physicalPageEnd").GetInt32());
            Assert.Equal("not_verified", first.GetProperty("sourceQuality").GetProperty("semanticAccuracy").GetString());
            Assert.Contains("isolate the assembly before applying this value", first.GetProperty("content").GetString());
        }
        Assert.Equal(groups.Select(group => group[0].Reference.EvidenceId),
            focused.Take(groups.Length).Select(item => item.Reference.EvidenceId));
        Assert.Equal(groups.Select(group => group[1].Reference.EvidenceId),
            focused.Skip(groups.Length).Take(groups.Length).Select(item => item.Reference.EvidenceId));
    }

    [Fact]
    public void Explicit_batch_keeps_newest_batch_first_old_active_ids_and_no_hit_results_without_hoarding_query_hits()
    {
        var groups = BalancedExplicitBodies(false);
        var older = AccumulatedReadBodies("OLD1", "OLD2");
        var active = older.Concat(groups.SelectMany(group => group)).ToArray();
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        AccumulatedRemember(context, "read_source", older);
        RememberBalancedExplicitBatch(context, groups);
        Assert.Equal(groups.Select(group => group[0].Reference.EvidenceId), AccumulatedReadIds(context).Take(6));
        Assert.Equal(new[] { "OLD1", "Q1H2", "OLD2", "Q2H2" }, AccumulatedReadIds(context).Skip(6).Take(4));
        var beforeEmpty = AccumulatedReadIds(context).ToArray();
        AccumulatedRemember(context, "find_source_text", []);
        Assert.Equal(beforeEmpty, AccumulatedReadIds(context));

        // Overlapping results consume one canonical slot, then the next unique
        // body of that request gets its turn ahead of another hit in group 1.
        var overlap = AccumulatedReadContext(gateway);
        RememberBalancedExplicitBatch(overlap, [groups[0], [groups[0][0], groups[1][0], groups[1][1]]]);
        Assert.Equal(new[] { "Q1H1", "Q2H1", "Q1H2", "Q2H2", "Q1H3" }, AccumulatedReadIds(overlap));
    }

    [Fact]
    public void Explicit_priority_never_resurrects_expired_or_stale_focused_bodies_even_without_remembered_ids()
    {
        var old = AccumulatedReadBodies("EXPIRED", "CURRENT");
        var current = old[1] with { Content = "Revalidated current content; isolate the assembly before applying this value." };
        var gateway = new AccumulatedReadGateway([current]);
        var context = AccumulatedReadContext(gateway);
        var unknown = AccumulatedReadBodies("UNKNOWN")[0];
        var focused = AccumulatedReadPriority(context, gateway.Active, [old[0], old[1], unknown]);
        Assert.Same(current, Assert.Single(focused));

        RememberBalancedExplicitBatch(context, [[old[0], old[1], unknown]]);
        Assert.Equal(new[] { "CURRENT" }, AccumulatedReadIds(context));
        gateway.Active = [];
        AccumulatedRemember(context, "find_source_text", []);
        Assert.Empty(AccumulatedReadIds(context));
        Assert.Empty(AccumulatedReadPriority(context, gateway.Active, focused));
    }

    [Fact]
    public void Targeted_read_window_still_promotes_its_current_body_with_exact_unicode_cursor_and_no_expired_copy()
    {
        var groups = BalancedExplicitBodies(false, longTail: true);
        var active = groups.SelectMany(group => group).ToArray();
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        RememberBalancedExplicitBatch(context, groups);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 40_000;
        options.NativeObservationReadEnabled = false;
        options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var focused = AccumulatedReadPriority(context, active, active);
        var projection = BalancedExplicitProjection(provider, context, active, focused);
        var target = groups[^1][0];
        var offset = target.Content.IndexOf("🧪", StringComparison.Ordinal);
        Assert.True(offset > 2_400);

        using var split = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidenceReads = new[] { new { evidenceId = target.Reference.EvidenceId, offset = offset + 1, length = 512 } }
        }));
        var invalid = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadRequests", split.RootElement, projection, context));
        Assert.Equal("advanced_native_evidence_read_invalid", invalid.ErrorCode);

        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidenceReads = new[] { new { evidenceId = target.Reference.EvidenceId, offset, length = 512 } }
        }));
        var updates = InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", read.RootElement, projection, context)!;
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        focused = AccumulatedReadPriority(context, active, active);
        Assert.Same(target, focused[0]);
        projection = BalancedExplicitProjection(provider, context, active, focused);
        using var prompt = JsonDocument.Parse(SourceCataloguePrompt(provider, projection));
        var visible = Assert.Single(prompt.RootElement.GetProperty("evidence").EnumerateArray(),
            item => item.GetProperty("evidenceId").GetString() == target.Reference.EvidenceId);
        Assert.Equal(offset, visible.GetProperty("contentOffset").GetInt32());
        Assert.Equal(target.Content.Substring(offset, 512), visible.GetProperty("content").GetString());
        Assert.Equal(offset + 512, visible.GetProperty("nextContentOffset").GetInt32());
        Assert.Equal(0, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);

        gateway.Active = active.Where(item => item.Reference.EvidenceId != target.Reference.EvidenceId).ToArray();
        AccumulatedRemember(context, "find_source_text", []);
        var afterExpiry = AccumulatedReadPriority(context, gateway.Active, [target]);
        Assert.DoesNotContain(afterExpiry, item => item.Reference.EvidenceId == target.Reference.EvidenceId);
    }

    private static AdvancedAnalysisResolvedEvidence[][] BalancedExplicitBodies(bool escaped, bool longTail = false)
        => Enumerable.Range(1, 6).Select(query => Enumerable.Range(1, 3).Select(hit =>
        {
            var id = $"Q{query}H{hit}";
            var condition = $"Observed canonical body {id}: isolate the assembly before applying this value. ";
            if (escaped) condition += "Quoted \"scope\", slash \\, 漢字 🧪\n";
            var content = condition + new string('x', longTail ? 3_000 : 2_100);
            if (longTail) content += "🧪 Exact later source condition. " + new string('y', 1_024);
            return TextQualityFixture(AccumulatedReadBody(id, content), "ok", false, ["source_conditions_retained"]);
        }).ToArray()).ToArray();

    private static void RememberBalancedExplicitBatch(object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence>[] groups)
    {
        var requests = groups.Select((group, index) => new AdvancedAnalysisSearchRequest(
            "Observed literal query " + index, TopK: 3, PageStart: 1, PageEnd: 1,
            Operation: "find_source_text")).ToArray();
        var results = requests.Select((request, index) => new
        {
            Key = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!,
            Value = new AdvancedAnalysisSearchObservation(request.Query, groups[index], [], 1, groups[index].Count)
        }).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        InvokeSourceCatalogue(null, "RememberLatestExplicitRead", context, requests, results);
    }

    private static object BalancedExplicitProjection(OpenAiCompatibleAdvancedAnalysisProvider provider, object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
        => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(), active,
            new Dictionary<string, HashSet<string>>(), focused, new Dictionary<string, string>(),
            context.GetType().GetProperty("EvidenceReadWindows")!.GetValue(context), null)!;
}
