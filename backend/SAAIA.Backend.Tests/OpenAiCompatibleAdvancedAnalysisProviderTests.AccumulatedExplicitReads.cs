using System.Collections;
using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Explicit_reads_accumulate_active_ids_with_newest_batch_first_and_stable_exact_deduplication()
    {
        var active = AccumulatedReadBodies("A1", "A2", "B1", "B2");
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", active[..2]);
        AccumulatedRemember(context, "find_source_text", active[2..]);
        Assert.Equal(new[] { "B1", "B2", "A1", "A2" }, AccumulatedReadIds(context));

        AccumulatedRemember(context, "read_source", [active[1], active[3], active[1]]);
        Assert.Equal(new[] { "A2", "B2", "B1", "A1" }, AccumulatedReadIds(context));
        // An empty result neither erases earlier reads nor invents a body.
        AccumulatedRemember(context, "find_source_text", []);
        Assert.Equal(new[] { "A2", "B2", "B1", "A1" }, AccumulatedReadIds(context));
    }

    [Fact]
    public void Explicit_read_window_preserves_other_active_reads_and_unchanged_window_is_a_noop()
    {
        var active = AccumulatedReadBodies("A", "B", "C");
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", [active[0]]);
        AccumulatedRemember(context, "find_source_text", [active[1]]);
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType(
            "EvidenceReadWindow", BindingFlags.NonPublic)!;
        var window = Activator.CreateInstance(windowType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [0, 512, null], null)!;
        var updates = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        updates.Add("C", window);

        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        Assert.Equal(new[] { "C", "B", "A" }, AccumulatedReadIds(context));
        Assert.Equal(0, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        Assert.Equal(new[] { "C", "B", "A" }, AccumulatedReadIds(context));
    }

    [Fact]
    public void Explicit_read_retention_filters_retired_and_unknown_ids_and_rebinds_only_current_objects()
    {
        var active = AccumulatedReadBodies("A", "B", "C");
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        AccumulatedRemember(context, "read_source", [active[0], active[1]]);
        gateway.Active = [active[1], active[2]];

        var retention = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod(
            "RetainedResearchEvidenceIds", SourceCatalogueFlags)!;
        var itemType = retention.GetParameters()[1].ParameterType.GenericTypeArguments[0];
        var emptyVisible = Array.CreateInstance(itemType, 0);
        var retained = (IReadOnlyCollection<string>)InvokeSourceCatalogue(null,
            "RetainedResearchEvidenceIds", context, emptyVisible)!;
        Assert.Equal(new[] { "B" }, retained);
        Assert.Equal(new[] { "B" }, AccumulatedReadIds(context));

        var unknown = AccumulatedReadBodies("NOT-ACTIVE")[0];
        AccumulatedRemember(context, "find_source_text", [active[2], unknown, active[0]]);
        Assert.Equal(new[] { "C", "B" }, AccumulatedReadIds(context));
        var currentB = active[1] with { Content = "Current revalidated body, never the cached old object." };
        gateway.Active = [currentB, active[2]];
        var focused = AccumulatedReadPriority(context, gateway.Active, [active[2]]);
        Assert.DoesNotContain(focused, item => item.Reference.EvidenceId == "A");
        Assert.DoesNotContain(focused, item => item.Reference.EvidenceId == "NOT-ACTIVE");
        Assert.Same(currentB, Assert.Single(focused, item => item.Reference.EvidenceId == "B"));
        Assert.Empty(AccumulatedReadIds(AccumulatedReadContext(gateway)));
    }

    [Theory]
    [InlineData("chat-completions", 8_000)]
    [InlineData("responses", 8_000)]
    [InlineData("chat-completions", 40_000)]
    [InlineData("responses", 40_000)]
    public async Task Explicit_read_batches_keep_small_prior_bodies_ahead_of_noise_under_exact_wire_cap(
        string protocol, int cap)
    {
        var read = AccumulatedReadBodies("A1", "A2", "B1", "B2");
        var noise = Enumerable.Range(0, 24).Select(number => AccumulatedReadBody(
            "N" + number, "General source context " + number + new string('x', 2_400))).ToArray();
        var active = noise.Concat(read).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", read[..2]);
        var latestRequest = AccumulatedRemember(context, "find_source_text", read[2..]);
        var latestResults = new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase)
        {
            [(string)InvokeSourceCatalogue(null, "BuildSearchIdentity", latestRequest)!] =
                new(latestRequest.Query, read[2..], [], 1, 2)
        };
        var calls = JsonSerializer.Serialize(new[] { new { id = "latest-call", type = "function",
            function = new { name = "find_source_text", arguments = "{}" } } });
        var responseItems = JsonSerializer.Serialize(new[] { new { type = "function_call", id = "fc_latest",
            call_id = "latest-call", name = "find_source_text", arguments = "{}" } });
        var turnType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType(
            "NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = turnType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 8)
            .Invoke([calls, new[] { latestRequest }, latestResults, null, responseItems, false, false, false]);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);

        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = cap;
        options.NativeObservationReadEnabled = false;
        options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        // The previous implementation placed only B before this noise. A stayed
        // active but was hidden; the fix must preserve both small read batches.
        var focused = AccumulatedReadPriority(context, active, noise);
        Assert.Equal(new[] { "B1", "B2", "A1", "A2" }, focused.Take(4).Select(item => item.Reference.EvidenceId));
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys",
            BuildDirectRequest(), active, new Dictionary<string, HashSet<string>>(), focused,
            new Dictionary<string, string>(), null, turn)!;
        var messages = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, protocol == "responses")!;
        var history = JsonSerializer.Serialize(messages);
        Assert.Contains("latest-call", history);
        Assert.DoesNotContain("read_source", history);
        Assert.InRange(history.Length, 1, options.NativeResearchMaximumHistoryCharacters);
        Assert.Same(turn, context.GetType().GetProperty("NativeTurn")!.GetValue(context));

        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(),
            "writer", "Return JSON.", SourceCataloguePrompt(provider, projection), 128,
            CancellationToken.None, true, messages, projection));
        var body = Assert.Single(factory.Requests).Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, cap);
        var visible = WorkspaceUser(body).GetProperty("evidence").EnumerateArray().ToArray();
        foreach (var expected in read)
        {
            var item = Assert.Single(visible, value => value.GetProperty("evidenceId").GetString() == expected.Reference.EvidenceId);
            Assert.Equal(expected.Content, item.GetProperty("content").GetString());
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
        }
        // A tight cap may still omit other active bodies; absence remains explicit.
        Assert.True(visible.Length < active.Length);
    }

    private static AdvancedAnalysisResolvedEvidence AccumulatedReadBody(string id, string content)
        => BuildEvidence(id, content, fileName: "operations.pdf",
            docId: "11111111-1111-4111-8111-111111111111",
            revisionId: "22222222-2222-4222-8222-222222222222");

    private static AdvancedAnalysisResolvedEvidence[] AccumulatedReadBodies(params string[] ids)
        => ids.Select(id => AccumulatedReadBody(id, "Exact canonical body " + id + ": isolate the seal before operation.")).ToArray();

    private static object AccumulatedReadContext(AccumulatedReadGateway gateway)
    {
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("SynthesisResearchContext", BindingFlags.NonPublic)!;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 6)
            .Invoke([gateway, Array.Empty<string>(), new List<AdvancedAnalysisSearchRequest>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new List<IReadOnlyList<AdvancedAnalysisResolvedEvidence>>(), new Dictionary<string, HashSet<string>>()]);
    }

    private static IReadOnlyList<string> AccumulatedReadIds(object context)
        => (IReadOnlyList<string>)context.GetType().GetProperty("LatestExplicitReadEvidenceIds")!.GetValue(context)!;

    private static AdvancedAnalysisSearchRequest AccumulatedRemember(object context, string operation,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var request = new AdvancedAnalysisSearchRequest("Observed literal " + operation, TopK: 4,
            PageStart: 1, PageEnd: 1, Operation: operation);
        var identity = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!;
        var results = new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase)
        {
            [identity] = new(request.Query, evidence, [], 1, 1)
        };
        InvokeSourceCatalogue(null, "RememberLatestExplicitRead", context, new[] { request }, results);
        return request;
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> AccumulatedReadPriority(object context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
        => (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeLatestExplicitRead", active, focused, AccumulatedReadIds(context))!;

    private sealed class AccumulatedReadGateway(IReadOnlyList<AdvancedAnalysisResolvedEvidence> active) : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Active { get; set; } = active;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => Active;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No documentary call belongs to a projection test.");
    }
}
