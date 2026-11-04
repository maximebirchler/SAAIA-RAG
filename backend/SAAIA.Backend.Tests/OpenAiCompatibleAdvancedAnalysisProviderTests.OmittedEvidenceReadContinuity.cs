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
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Omitted_read_registry_opens_two_invisible_bodies_successively_without_new_documentary_io(
        string protocol, bool documentaryQuotaExhausted)
    {
        var bodies = OmittedHandleBodies();
        var gateway = new OmittedHandleGateway(bodies, documentaryQuotaExhausted);
        var ordinal = 0; string? first = null; string? second = null;
        var initialHandles = new HashSet<string>(StringComparer.Ordinal);
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var raw = await request.Content!.ReadAsStringAsync();
            var user = WorkspaceUser(raw);
            if (ordinal > 0)
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(raw), 1, 10_000);
            HttpResponseMessage response;
            switch (ordinal++)
            {
                case 0: response = Completion("""{"queries":[{"query":"overview","topK":12}]}"""); break;
                case 1: response = OmittedHandleFunction(protocol, "page", "read_source",
                    """{"sourceKey":"internal-source-1","pageStart":14,"pageCount":1,"topK":20}"""); break;
                case 2:
                    foreach (var row in user.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray())
                        initialHandles.Add(row.GetProperty("evidenceId").GetString()!);
                    first = initialHandles.First();
                    AssertWindowOnlyCapability(user, raw, protocol, first, documentaryQuotaExhausted);
                    response = OmittedHandleFunction(protocol, "first", "read_evidence",
                        JsonSerializer.Serialize(new { evidenceId = first, offset = 0, length = 512 })); break;
                case 3:
                    AssertCanonicalWindow(user, first!, bodies);
                    second = user.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray()
                        .Select(row => row.GetProperty("evidenceId").GetString()!)
                        .First(id => id != first && initialHandles.Contains(id));
                    AssertWindowOnlyCapability(user, raw, protocol, second, documentaryQuotaExhausted);
                    response = OmittedHandleFunction(protocol, "second", "read_evidence",
                        JsonSerializer.Serialize(new { evidenceId = second, offset = 0, length = 512 })); break;
                case 4:
                    AssertCanonicalWindow(user, first!, bodies); AssertCanonicalWindow(user, second!, bodies);
                    Assert.Equal(2, gateway.Requests.Count);
                    response = Completion(JsonSerializer.Serialize(new
                    {
                        outcome = "answered", answerText = "The second procedure is documented [C1].",
                        claims = new[] { new { claimId = "C1", text = "The second procedure is documented.", evidenceIds = new[] { second } } }
                    })); break;
                default: throw new InvalidOperationException("The two window reads require no extra retry or search.");
            }
            return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);
        });
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 10_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(5, result.ProviderCallCount);
        Assert.NotEqual(first, second); Assert.Equal(second, Assert.Single(result.Claims).EvidenceIds.Single());
        Assert.Equal(2, gateway.Requests.Count); Assert.Single(gateway.Requests, request => request.Operation == "read_source");
        Assert.Equal(20, gateway.Evidence.Count);
        if (documentaryQuotaExhausted) Assert.Equal(0, gateway.Budget!.RemainingCalls);
    }

    private static void AssertWindowOnlyCapability(JsonElement user, string wire, string protocol,
        string id, bool documentaryQuotaExhausted)
    {
        var handles = user.GetProperty("omittedEvidenceReadHandles");
        Assert.True(handles.GetProperty("navigationOnly").GetBoolean()); Assert.False(handles.GetProperty("citable").GetBoolean());
        Assert.All(handles.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("content", out _));
            Assert.False(item.TryGetProperty("canonicalContentSha256", out _));
            Assert.False(item.TryGetProperty("sourceReference", out _));
        });
        var visible = user.GetProperty("evidence").EnumerateArray().Select(row => row.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.DoesNotContain(id, visible);
        var premature = new AdvancedAnalysisProviderResult { Outcome = "answered", AnswerText = "Premature [C1].",
            Claims = [new() { ClaimId = "C1", Text = "Premature.", EvidenceIds = [id] }] };
        Assert.Throws<AdvancedAnalysisProviderException>(() => OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(premature, visible));
        using var payload = JsonDocument.Parse(wire);
        var functions = payload.RootElement.GetProperty("tools").EnumerateArray()
            .Select(item => protocol == "responses" ? item : item.GetProperty("function")).ToArray();
        var read = Assert.Single(functions, item => item.GetProperty("name").GetString() == "read_evidence");
        Assert.Contains(id, read.GetProperty("parameters").GetProperty("properties").GetProperty("evidenceId").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.True(user.GetProperty("researchTools").GetProperty("evidenceReadingAllowed").GetBoolean());
        if (documentaryQuotaExhausted)
        {
            Assert.False(user.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
            Assert.DoesNotContain(functions, item => item.GetProperty("name").GetString() is "search_corpus" or "read_source" or "find_source_text");
        }
    }

    private static void AssertCanonicalWindow(JsonElement user, string id, IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies)
    {
        var row = Assert.Single(user.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == id);
        Assert.Equal(0, row.GetProperty("contentOffset").GetInt32());
        Assert.Equal(Assert.Single(bodies, item => item.Reference.EvidenceId == id).Content[..512], row.GetProperty("content").GetString());
    }

    [Fact]
    public void Omitted_read_registry_survives_window_turns_and_fitting_is_pure_and_measures_the_same_handles()
    {
        var bodies = OmittedHandleBodies(); var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway); using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies); context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        var firstPrompt = OmittedHandlePrompt(provider, projection, context);
        using var initial = JsonDocument.Parse(firstPrompt);
        var id = initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = id, offset = 0, length = 512 } } }));
        var updates = InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", read.RootElement, projection, context)!;
        InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context);
        // A window turn does not carry a primary read result. Even a cleared
        // NativeTurn cannot retire a still active, previously disclosed body.
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        var retained = context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!;
        var disclosed = context.GetType().GetProperty("OmittedEvidenceReadHandles")!.GetValue(context)!;
        var next = OmittedHandleProjection(provider, bodies, null!, context);
        Assert.Same(retained, context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context));
        Assert.Same(disclosed, context.GetType().GetProperty("OmittedEvidenceReadHandles")!.GetValue(context));
        var sourceCatalogue = next.GetType().GetProperty("SourceCatalogue")!.GetValue(next)!;
        var budgetPrompt = (string)InvokeSourceCatalogue(provider, "BuildDocumentaryProjectionBudgetPrompt", next,
            sourceCatalogue, null, null, bodies, retained)!;
        Assert.Same(retained, context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context));
        var actualPrompt = OmittedHandlePrompt(provider, next, context);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(budgetPrompt)!["omittedEvidenceReadHandles"],
            JsonNode.Parse(actualPrompt)!["omittedEvidenceReadHandles"]));
        Assert.NotEmpty(JsonNode.Parse(actualPrompt)!["omittedEvidenceReadHandles"]!["items"]!.AsArray());
    }

    [Fact]
    public void Omitted_read_registry_is_not_shared_with_another_job_context()
    {
        var bodies = OmittedHandleBodies(); var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway); using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies); context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var id = prompt.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        var fresh = AccumulatedReadContext(gateway);
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = id, offset = 0, length = 512 } } }));
        Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", read.RootElement, projection, fresh));
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("same_length_body")]
    [InlineData("reference_mutated_in_place")]
    public void Omitted_read_registry_prunes_expired_snapshots_on_the_next_actual_prompt(string change)
    {
        var bodies = OmittedHandleBodies(); var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway); using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies); context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        using var initial = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var id = initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        if (change == "retired") gateway.Active = bodies.Where(item => item.Reference.EvidenceId != id).ToArray();
        else if (change == "same_length_body") gateway.Active = bodies.Select(item => item.Reference.EvidenceId == id
            ? item with { Content = new string('z', item.Content.Length) } : item).ToArray();
        else typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!
                .SetValue(Assert.Single(bodies, item => item.Reference.EvidenceId == id).Reference, "changed-in-place");
        using var next = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var registry = (IEnumerable)context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!;
        Assert.DoesNotContain(registry.Cast<object>(), item => (string)item.GetType().GetProperty("EvidenceId")!.GetValue(item)! == id);
        Assert.DoesNotContain(next.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("evidenceId").GetString() == id);
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = id, offset = 0, length = 512 } } }));
        Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", read.RootElement, projection, context));
    }

    [Fact]
    public void Omitted_read_registry_keeps_at_most_sixty_four_capabilities_and_evicts_oldest_active_entries()
    {
        using var factory = new QueuedHttpClientFactory(); var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 65_536;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var groups = OmittedRegistryGroups(longIds: false); var active = groups.SelectMany(group => group).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        var projection = OmittedEmptyProjection(provider, active);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, OmittedRegistryTurn(groups.Take(8).ToArray()));
        using var initial = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var before = initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.Equal(64, before.Length);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, OmittedRegistryTurn([groups[8]]));
        using var next = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var after = next.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.Equal(64, after.Length);
        Assert.Equal(before.Skip(8).Concat(groups[8].Take(8).Select(item => item.Reference.EvidenceId)), after);
        Assert.All(before.Take(8), id => Assert.Contains(active, item => item.Reference.EvidenceId == id));
        Assert.Equal(64, ((IEnumerable)context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!).Cast<object>().Count());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Omitted_read_registry_metadata_overflow_is_counted_after_native_turn_expiry_and_refused_before_http(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(); var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 65_536; options.NativeObservationReadEnabled = false;
        options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var groups = OmittedRegistryGroups(longIds: true); var active = groups.SelectMany(group => group).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        var projection = OmittedEmptyProjection(provider, active);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, OmittedRegistryTurn(groups.Take(8).ToArray()));
        _ = OmittedHandlePrompt(provider, projection, context);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        var registry = context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!;
        var sources = projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection)!;
        var trial = (string)InvokeSourceCatalogue(provider, "BuildDocumentaryProjectionBudgetPrompt", projection, sources, null, null, active, registry)!;
        var actual = OmittedHandlePrompt(provider, projection, context);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(trial)!["omittedEvidenceReadHandles"], JsonNode.Parse(actual)!["omittedEvidenceReadHandles"]));
        Assert.Equal(64, JsonNode.Parse(actual)!["omittedEvidenceReadHandles"]!["items"]!.AsArray().Count);
        options.MaximumEvidencePromptCharacters = 8_000;
        var task = Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(),
            "writer", "Return JSON.", actual, 128, CancellationToken.None, false, null, projection));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(async () => await task);
        Assert.Equal("advanced_documentary_wire_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    private static AdvancedAnalysisResolvedEvidence[][] OmittedRegistryGroups(bool longIds)
        => Enumerable.Range(0, 9).Select(group => Enumerable.Range(0, 10).Select(index =>
        {
            var id = $"REG-{group}-{index}" + (longIds ? new string('x', 170) : "");
            return PrimaryReadBody(id, "Neutral canonical procedure " + group + " " + index) with
            {
                Reference = new()
                {
                    EvidenceId = id, DocId = "11111111-1111-4111-8111-111111111111",
                    RevisionId = "22222222-2222-4222-8222-222222222222", SourceHash = new string('a', 64),
                    DocPath = "Documents/inspection.pdf", FileName = "inspection.pdf", ChunkId = "chunk-" + id,
                    PageStart = 40 + group, PageEnd = 40 + group
                }
            };
        }).ToArray()).ToArray();

    private static object OmittedEmptyProjection(OpenAiCompatibleAdvancedAnalysisProvider provider,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
    {
        var original = SourceCatalogueProjection(provider, active);
        var type = original.GetType();
        var element = type.BaseType!.GetGenericArguments()[0];
        var empty = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [Array.CreateInstance(element, 0), active.Count], null)!;
        type.GetProperty("SourceCatalogue")!.SetValue(empty, type.GetProperty("SourceCatalogue")!.GetValue(original));
        type.GetProperty("SourceCatalogueEnabled")!.SetValue(empty, true);
        return empty;
    }

    private static object OmittedRegistryTurn(IReadOnlyList<AdvancedAnalysisResolvedEvidence>[] groups)
    {
        var requests = groups.Select(group => new AdvancedAnalysisSearchRequest("", TopK: 20,
            DocId: group[0].Reference.DocId, RevisionId: group[0].Reference.RevisionId, DocPath: group[0].Reference.DocPath,
            PageStart: group[0].Reference.PageStart, PageEnd: group[0].Reference.PageEnd, Operation: "read_source")).ToArray();
        var ids = requests.Select(request => "read-page-" + request.PageStart).ToArray();
        var calls = JsonSerializer.Serialize(requests.Select((request, index) => new { id = ids[index], type = "function",
            function = new { name = "read_source", arguments = JsonSerializer.Serialize(new
                { sourceKey = "internal-source-1", pageStart = request.PageStart, pageCount = 1, topK = 20 }) } }));
        var results = requests.Select((request, index) => new
        {
            Identity = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!,
            Observation = new AdvancedAnalysisSearchObservation("", groups[index], [], 1, index + 1)
        }).ToDictionary(item => item.Identity, item => item.Observation, StringComparer.OrdinalIgnoreCase);
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = type.GetConstructors().Single(ctor => ctor.GetParameters().Length == 8)
            .Invoke([calls, requests, results, null, null, false, false, false]);
        type.GetProperty("CallRequests")!.SetValue(turn, requests.Select((request, index) => (request, index)).ToDictionary(item => ids[item.index], item => item.request));
        type.GetProperty("CallSourceScopes")!.SetValue(turn, requests.Select((request, index) => (request, index)).ToDictionary(item => ids[item.index], item => groups[item.index][0].Reference));
        return turn;
    }
}
