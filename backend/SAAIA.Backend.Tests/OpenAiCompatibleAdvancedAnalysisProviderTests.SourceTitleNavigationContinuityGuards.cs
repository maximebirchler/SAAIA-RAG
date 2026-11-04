using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("retired")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("in-place-scope")]
    public void Source_title_navigation_cursor_expires_with_its_exact_source_scope(string change)
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberNavigationCursor(provider, context, body.Reference);
        Assert.Single(NavigationCursors(context));
        AdvancedAnalysisResolvedEvidence[] active = [body];
        if (change == "retired") active = [];
        else
        {
            var changed = CopyAdvancedResultEvidence(body.Reference);
            if (change == "revision") typeof(AdvancedAnalysisResultEvidence).GetProperty("RevisionId")!.SetValue(changed, "99999999-2222-4222-8222-222222222222");
            if (change == "hash") typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(changed, new string('f', 64));
            if (change == "path") typeof(AdvancedAnalysisResultEvidence).GetProperty("DocPath")!.SetValue(changed, "another.pdf");
            if (change == "in-place-scope")
            {
                typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(body.Reference, new string('f', 64));
                changed = body.Reference;
            }
            active = [body with { Reference = changed }];
        }
        InvokeSourceCatalogue(provider, "PruneSourceTitleCursors", BuildDirectRequest(), context, active);
        Assert.Empty(NavigationCursors(context));
        InvokeSourceCatalogue(provider, "PruneSourceTitleCursors", BuildDirectRequest(), context, new[] { body });
        Assert.Empty(NavigationCursors(context));
    }

    [Fact]
    public void Source_title_navigation_frontiers_keep_exact_filters_and_furthest_observed_offset_and_are_job_local()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var gateway = new AccumulatedReadGateway([body]); var context = AccumulatedReadContext(gateway);
        RememberNavigationCursor(provider, context, body.Reference, offset: 10, next: 19, filter: "Étalonnage 🧪", pageStart: 2, pageEnd: 70, cardOnly: true);
        RememberNavigationCursor(provider, context, body.Reference, offset: 0, next: 9, filter: "Étalonnage 🧪", pageStart: 2, pageEnd: 70, cardOnly: true);
        RememberNavigationCursor(provider, context, body.Reference, offset: 15, next: 16, filter: "Étalonnage 🧪", pageStart: 2, pageEnd: 70, cardOnly: true, limit: 1);
        RememberNavigationCursor(provider, context, body.Reference, offset: 0, next: 9, filter: "other", cardOnly: false);
        Assert.Equal(2, NavigationCursors(context).Length);
        Assert.Empty(NavigationCursors(AccumulatedReadContext(gateway)));
        var navigation = NavigationCursorPayload(provider, context, [body]);
        var specific = Assert.Single(navigation.GetProperty("items").EnumerateArray(), row => row.GetProperty("titleContains").GetString() == "Étalonnage 🧪");
        Assert.Equal(10, specific.GetProperty("offset").GetInt32()); Assert.Equal(19, specific.GetProperty("nextOffset").GetInt32());
        Assert.Equal(20, specific.GetProperty("limit").GetInt32());
        Assert.Equal(2, specific.GetProperty("pageStart").GetInt32()); Assert.Equal(70, specific.GetProperty("pageEnd").GetInt32());
        Assert.True(specific.GetProperty("cardBackedOnly").GetBoolean());
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("deferred")]
    [InlineData("failed")]
    [InlineData("changed-scope")]
    [InlineData("invented-next")]
    public void Source_title_navigation_only_retains_correlated_successful_pages(string kind)
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberNavigationCursor(provider, context, body.Reference, kind: kind);
        Assert.Empty(NavigationCursors(context));
    }

    [Fact]
    public void Source_title_navigation_is_bounded_and_its_identical_current_payload_is_charged_without_mutation()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        for (var i = 0; i < 40; i++) RememberNavigationCursor(provider, context, body.Reference, filter: $"Filter {i} \"漢字🧪");
        Assert.Equal(32, NavigationCursors(context).Length);
        var before = context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context);
        var projection = NavigationCursorProjection(provider, context, [body]);
        var catalogue = projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection)!;
        var budget = (string)InvokeSourceCatalogue(provider, "BuildDocumentaryProjectionBudgetPrompt", projection, catalogue, null, null, null, null)!;
        var actual = (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", SourceCataloguePrompt(provider, projection), projection)!;
        Assert.Same(before, context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(budget)!["sourceCatalogue"], JsonNode.Parse(actual)!["sourceCatalogue"]));
        var navigation = JsonNode.Parse(actual)!["sourceCatalogue"]!["titleNavigation"]!;
        var listed = navigation["listedCursorCount"]!.GetValue<int>();
        Assert.InRange(listed, 1, 31);
        Assert.Equal(32 - listed, navigation["omittedCursorCount"]!.GetValue<int>());
        var measured = (int)InvokeSourceCatalogue(provider, "MeasureDocumentaryProjectionWithSourcesWireCharacters", projection, null, catalogue)!;
        var without = JsonNode.Parse(budget)!.AsObject(); without["sourceCatalogue"]!.AsObject().Remove("titleNavigation");
        int Count(string prompt) => OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(JsonSerializer.Serialize(new
        { messages = new[] { new { role = "system", content = "" }, new { role = "user", content = prompt } } }));
        Assert.Equal(Count(budget), measured);
        Assert.True(Count(budget) > Count(without.ToJsonString()));
    }

    private static object[] NavigationCursors(object context)
        => ((IEnumerable)context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context)!).Cast<object>().ToArray();

    [Fact]
    public void Source_title_navigation_survives_carrier_replacement_but_never_an_ambiguous_alias_or_unfittable_header()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberNavigationCursor(provider, context, body.Reference);
        var replacement = NavigationContinuityBody("REPLACEMENT", 1, 2);
        InvokeSourceCatalogue(provider, "PruneSourceTitleCursors", BuildDirectRequest(), context, new[] { replacement });
        Assert.Single(NavigationCursors(context));
        Assert.Equal(19, Assert.Single(NavigationCursorPayload(provider, context, [replacement])
            .GetProperty("items").EnumerateArray()).GetProperty("nextOffset").GetInt32());
        var projection = SourceCatalogueProjection(provider, [replacement]);
        var sources = projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection)!;
        var retained = context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context)!;
        Assert.Same(sources, InvokeSourceCatalogue(null, "BuildSourceTitleNavigationCatalogue", sources, retained, 1));
        var handles = SourceCatalogueHandles(projection);
        var duplicate = Array.CreateInstance(handles[0].GetType(), 2);
        duplicate.SetValue(handles[0], 0); duplicate.SetValue(handles[0], 1);
        Assert.Same(duplicate, InvokeSourceCatalogue(null, "BuildSourceTitleNavigationCatalogue", duplicate, retained, 98_304));
    }

    private static void RememberNavigationCursor(OpenAiCompatibleAdvancedAnalysisProvider provider, object context,
        AdvancedAnalysisResultEvidence scope, int offset = 10, int next = 19, string? filter = null,
        int? pageStart = null, int? pageEnd = null, bool cardOnly = true, string? kind = null, int limit = 20)
    {
        var query = new AdvancedAnalysisSearchRequest(AdvancedAnalysisSourceTitlePage.Query, TopK: limit,
            DocId: scope.DocId, RevisionId: scope.RevisionId, DocPath: scope.DocPath,
            Operation: AdvancedAnalysisSourceTitlePage.Operation, Offset: offset, TitleContains: filter,
            PageStart: pageStart, PageEnd: pageEnd, CardBackedTitlesOnly: cardOnly);
        var page = new AdvancedAnalysisSourceTitlePage(offset, kind == "invented-next" ? next + 1 : next,
            Enumerable.Range(0, next - offset).Select(i => new AdvancedAnalysisSourceTitle($"Operation {i}", "title_anchor", null, 10 + i, 10 + i)).ToArray(), false);
        var results = new Dictionary<string, AdvancedAnalysisSearchObservation>();
        if (kind != "missing-result") results[(string)InvokeSourceCatalogue(null, "BuildSearchIdentity", query)!] = new(query.Query, [], [], 0, 1,
            NotExecutedAfterResourceLimit: kind == "failed", SourceTitlePage: page);
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 8).Invoke(["[]", new[] { query }, results, null, null, false, false, false]);
        type.GetProperty("CallRequests")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisSearchRequest> { ["navigation"] = query });
        var bound = CopyAdvancedResultEvidence(scope);
        if (kind == "changed-scope") typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(bound, new string('f', 64));
        type.GetProperty("CallSourceScopes")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisResultEvidence> { ["navigation"] = bound });
        if (kind == "deferred") type.GetProperty("DeferredCallIds")!.SetValue(turn, new HashSet<string> { "navigation" });
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        InvokeSourceCatalogue(provider, "RememberSourceTitleCursors", BuildDirectRequest(), context);
    }

    private static AdvancedAnalysisResultEvidence CopyAdvancedResultEvidence(AdvancedAnalysisResultEvidence source)
        => new() { EvidenceId = source.EvidenceId, DocId = source.DocId, RevisionId = source.RevisionId,
            SourceHash = source.SourceHash, DocPath = source.DocPath, FileName = source.FileName,
            ChunkId = source.ChunkId, PageStart = source.PageStart, PageEnd = source.PageEnd };

    private static object NavigationCursorProjection(OpenAiCompatibleAdvancedAnalysisProvider provider, object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
        => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithNavigationRegistry", BuildDirectRequest(), active,
            new Dictionary<string, HashSet<string>>(), active, context.GetType().GetProperty("PromptSourceKeys")!.GetValue(context),
            null, null, context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context),
            context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context))!;

    private static JsonElement NavigationCursorPayload(OpenAiCompatibleAdvancedAnalysisProvider provider, object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
    {
        var projection = NavigationCursorProjection(provider, context, active);
        var prompt = (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", SourceCataloguePrompt(provider, projection), projection)!;
        using var document = JsonDocument.Parse(prompt);
        return document.RootElement.GetProperty("sourceCatalogue").GetProperty("titleNavigation").Clone();
    }
}
