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
    public void New_global_search_omissions_remain_reopenable_after_eight_older_cached_windows(string protocol)
    {
        var old = SearchHandleBodies("OLDER", 8, 1);
        var found = SearchHandleBodies("NEW-A", 6, 1).Concat(SearchHandleBodies("NEW-B", 5, 2)).ToArray();
        var active = old.Concat(found).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var oldTurn = OmittedHandleTurn(old);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, oldTurn);
        var initial = OmittedHandleProjection(provider, old, oldTurn, context);
        using var initialPrompt = JsonDocument.Parse(OmittedHandlePrompt(provider, initial, context));
        using var reads = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidenceReads = old.Select(item => new { evidenceId = item.Reference.EvidenceId, offset = 0, length = 512 })
        }));
        var windows = InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", reads.RootElement, initial, context)!;
        InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", windows, context);

        var turn = SearchHandleTurn(found);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        options.MaximumEvidencePromptCharacters = 28_000;
        var projection = OmittedHandleProjection(provider, active, turn, context);
        using var user = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var visible = user.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetProperty("evidenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var omitted = found.Where(item => !visible.Contains(item.Reference.EvidenceId!)).Take(8).ToArray();
        Assert.NotEmpty(omitted);
        Assert.True(user.RootElement.TryGetProperty("omittedEvidenceReadHandles", out var offered),
            "Executed global search omitted canonical bodies but disclosed no current reopening handles.");
        Assert.True(offered.GetProperty("navigationOnly").GetBoolean());
        Assert.False(offered.GetProperty("citable").GetBoolean());
        var handles = offered.GetProperty("items").EnumerateArray().ToArray();
        Assert.InRange(handles.Length, 1, 64);
        Assert.All(omitted, item => Assert.Contains(handles,
            handle => handle.GetProperty("evidenceId").GetString() == item.Reference.EvidenceId));
        var readable = (string[])InvokeSourceCatalogue(null, "ReadableEvidenceIdsForNativeTools", user.RootElement)!;
        var chosen = omitted[0];
        Assert.Contains(chosen.Reference.EvidenceId!, readable);
        using var reopen = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidenceReads = new[] { new { evidenceId = chosen.Reference.EvidenceId, offset = 0, length = 512 } }
        }));
        var update = InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", reopen.RootElement, projection, context)!;
        InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", update, context);
        var prioritized = PreviousProjectionPriority(context, active, active);
        var next = OmittedHandleProjection(provider, prioritized, turn, context);
        using var nextUser = JsonDocument.Parse(OmittedHandlePrompt(provider, next, context));
        var body = Assert.Single(nextUser.RootElement.GetProperty("evidence").EnumerateArray(),
            item => item.GetProperty("evidenceId").GetString() == chosen.Reference.EvidenceId);
        Assert.Equal(chosen.Content[..512], body.GetProperty("content").GetString());
        var wire = protocol == "responses"
            ? JsonSerializer.Serialize(new { input = new[] { new { role = "user", content = nextUser.RootElement.GetRawText() } } })
            : JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = nextUser.RootElement.GetRawText() } } });
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 28_000);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("rejected")]
    [InlineData("no_result")]
    [InlineData("resource_limit")]
    [InlineData("call_lookalike")]
    [InlineData("call_type_lookalike")]
    [InlineData("operation_mismatch")]
    [InlineData("scoped_without_binding")]
    [InlineData("forged_scope")]
    [InlineData("retired")]
    [InlineData("body_changed")]
    [InlineData("revision_changed")]
    [InlineData("source_hash_changed")]
    [InlineData("path_changed")]
    [InlineData("page_changed")]
    [InlineData("ambiguous_body")]
    [InlineData("table_observation")]
    public void Search_reopening_never_grants_a_deferred_rejected_stale_or_secondary_body(string change)
    {
        var found = SearchHandleBodies("TARGET", 1, 1);
        var anchor = SearchHandleBodies("ANCHOR", 1, 1)[0];
        var active = found.Append(anchor).ToList();
        var turn = SearchHandleTurn(found);
        var type = turn.GetType();
        if (change == "deferred") type.GetProperty("DeferredCallIds")!.SetValue(turn, new HashSet<string> { "read" });
        if (change == "rejected") type.GetProperty("Rejection")!.SetValue(turn,
            new AdvancedAnalysisResearchArgumentFeedback("synthetic_rejection", "search_corpus", "", null, null, 4));
        if (change == "scoped_without_binding")
        {
            turn = SearchHandleTurn(found, scoped: true); type = turn.GetType();
            type.GetProperty("CallSourceScopes")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisResultEvidence>());
        }
        if (change == "forged_scope") type.GetProperty("CallSourceScopes")!.SetValue(turn,
            new Dictionary<string, AdvancedAnalysisResultEvidence> { ["read"] = SearchHandleBodies("OTHER", 1, 2)[0].Reference });
        if (change == "no_result") type.GetProperty("Results")!.SetValue(turn,
            new Dictionary<string, AdvancedAnalysisSearchObservation>());
        if (change == "resource_limit")
        {
            var request = ((IReadOnlyDictionary<string, AdvancedAnalysisSearchRequest>)type.GetProperty("CallRequests")!.GetValue(turn)!).Single().Value;
            var identity = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!;
            type.GetProperty("Results")!.SetValue(turn, new Dictionary<string, AdvancedAnalysisSearchObservation>
            {
                [identity] = new(request.Query, found, [], 1, 1, ResourceLimit: new("synthetic_limit", null, null, null))
            });
        }
        if (change is "call_lookalike" or "call_type_lookalike" or "operation_mismatch")
            type.GetProperty("CallsJson")!.SetValue(turn, JsonSerializer.Serialize(new[]
            {
                new { id = "read", type = change == "call_type_lookalike" ? "function_extra" : "function", function = new
                {
                    name = change == "call_lookalike" ? "search_corpus_extra" : "read_source", arguments = "{}"
                } }
            }));
        if (change == "retired") active.RemoveAt(0);
        if (change == "body_changed") active[0] = active[0] with { Content = "A different canonical body." };
        if (change is "revision_changed" or "page_changed" or "source_hash_changed" or "path_changed")
        {
            var reference = found[0].Reference;
            active[0] = active[0] with { Reference = new()
            {
                EvidenceId = reference.EvidenceId, DocId = reference.DocId,
                DocPath = change == "path_changed" ? "changed/path.pdf" : reference.DocPath,
                SourceHash = change == "source_hash_changed" ? new string('f', 64) : reference.SourceHash,
                ChunkId = reference.ChunkId, FileName = reference.FileName,
                RevisionId = change == "revision_changed" ? "changed-revision" : reference.RevisionId,
                PageStart = change == "page_changed" ? 15 : reference.PageStart,
                PageEnd = change == "page_changed" ? 15 : reference.PageEnd
            } };
        }
        if (change == "ambiguous_body") active.Add(found[0] with { Content = "Ambiguous canonical duplicate." });
        if (change == "table_observation")
        {
            found[0] = found[0] with { SourceObservation = new("observation", "table", "line", 0,
                "text-sha", "raw-sha", "raw", 0, null, 2, 0, 1, 1, ["secondary_raw"]) };
            active[0] = found[0];
        }
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        if (change == "path_changed")
        {
            var conflict = Assert.Throws<AdvancedAnalysisProviderException>(() => OmittedHandleProjection(provider, active, turn, context));
            Assert.Equal("advanced_source_catalogue_identity_conflict", conflict.ErrorCode);
            return;
        }
        var full = OmittedHandleProjection(provider, active, turn, context);
        var empty = SearchHandleEmptyProjection(full, active.Count);
        var handles = InvokeSourceCatalogue(null, "BuildOmittedEvidenceReadHandles", turn, empty, active)!;
        using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(handles, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.DoesNotContain(encoded.RootElement.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == "TARGET0");
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void A_scoped_search_discloses_only_its_exact_bound_primary_result()
    {
        var found = SearchHandleBodies("SCOPED", 11, 1);
        var turn = SearchHandleTurn(found, scoped: true);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway(found));
        var full = OmittedHandleProjection(provider, found, turn, context);
        var empty = SearchHandleEmptyProjection(full, found.Length);
        var handles = InvokeSourceCatalogue(null, "BuildOmittedEvidenceReadHandles", turn, empty, found)!;
        using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(handles, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(found.Select(item => item.Reference.EvidenceId),
            encoded.RootElement.EnumerateArray().Select(item => item.GetProperty("evidenceId").GetString()));
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void A_new_search_preserves_the_sixty_four_bound_and_keeps_current_disclosed_omissions()
    {
        var old = SearchHandleBodies("RETAINED", 64, 1);
        var found = SearchHandleBodies("NEW", 11, 2);
        var active = old.Concat(found).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var oldTurn = OmittedHandleTurn(old);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, oldTurn);
        using var initial = JsonDocument.Parse(OmittedHandlePrompt(provider,
            OmittedHandleProjection(provider, old, oldTurn, context), context));
        var turn = SearchHandleTurn(found);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var full = OmittedHandleProjection(provider, active, turn, context);
        var empty = SearchHandleEmptyProjection(full, active.Length);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, empty, context));
        var handles = prompt.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(64, handles.Length);
        Assert.All(found, item => Assert.Contains(handles, h => h.GetProperty("evidenceId").GetString() == item.Reference.EvidenceId));
        Assert.Equal(old.Skip(11).Concat(found).Select(item => item.Reference.EvidenceId),
            handles.Select(item => item.GetProperty("evidenceId").GetString()));
        Assert.Empty(factory.Requests);
    }

    private static object SearchHandleEmptyProjection(object full, int inputCount)
    {
        var type = full.GetType();
        var itemType = type.BaseType!.GetGenericArguments()[0];
        var items = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        var empty = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 2).Invoke([items, inputCount]);
        type.GetProperty("SourceCatalogue")!.SetValue(empty, type.GetProperty("SourceCatalogue")!.GetValue(full));
        type.GetProperty("SourceCatalogueEnabled")!.SetValue(empty, true);
        return empty;
    }

    private static AdvancedAnalysisResolvedEvidence[] SearchHandleBodies(string prefix, int count, int source)
        => Enumerable.Range(0, count).Select(index => PrimaryReadBody(prefix + index,
            "Observed procedure " + prefix + index + ": " + new string('x', 6_000)) with
        {
            Reference = new()
            {
                EvidenceId = prefix + index, DocId = $"{source:00000000}-1111-4111-8111-111111111111",
                RevisionId = $"{source:00000000}-2222-4222-8222-222222222222",
                SourceHash = new string((char)('a' + source), 64), DocPath = $"Documents/source-{source}.pdf",
                FileName = $"source-{source}.pdf", ChunkId = prefix + "-chunk-" + index,
                PageStart = 14, PageEnd = 14
            }
        }).ToArray();

    private static object SearchHandleTurn(IReadOnlyList<AdvancedAnalysisResolvedEvidence> found, bool scoped = false)
    {
        var request = new AdvancedAnalysisSearchRequest("a newly observed inspection condition", TopK: 20,
            DocId: scoped ? found[0].Reference.DocId : null,
            RevisionId: scoped ? found[0].Reference.RevisionId : null,
            DocPath: scoped ? found[0].Reference.DocPath : null,
            Operation: "search_corpus");
        var turn = PrimaryReadProjectionTurn(request, found, found[0].Reference);
        if (!scoped) turn.GetType().GetProperty("CallSourceScopes")!.SetValue(turn,
            new Dictionary<string, AdvancedAnalysisResultEvidence>());
        turn.GetType().GetProperty("CallsJson")!.SetValue(turn, JsonSerializer.Serialize(new[]
        {
            new { id = "read", type = "function", function = new { name = "search_corpus",
                arguments = "{\"query\":\"a newly observed inspection condition\",\"sourceKey\":\"\",\"category\":\"\",\"documentHint\":\"\",\"topK\":20}" } }
        }));
        return turn;
    }
}
