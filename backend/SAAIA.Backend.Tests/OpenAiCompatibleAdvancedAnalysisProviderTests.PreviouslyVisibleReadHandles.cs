using System.Collections;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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
    public async Task Previously_visible_primary_bodies_remain_reopenable_after_a_later_read_displaces_them(
        string protocol, bool documentaryQuotaExhausted)
    {
        const int cap = 16_000;
        var first = VisibleReadBodies("EARLIER", 14, 6, 650);
        var later = VisibleReadBodies("LATER", 15, 20, 2_100);
        var searchOnly = VisibleReadBodies("SEARCH-ONLY", 11, 1, 650).Single();
        var gateway = new VisibleReadContinuityGateway(first, later, searchOnly, documentaryQuotaExhausted);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var wireHintCounts = new List<int>();
        string? displaced = null;
        var ordinal = 0;
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync();
            var user = WorkspaceUser(wire);
            wireHintCounts.Add(user.TryGetProperty("omittedEvidenceReadHandles", out var wireHints)
                ? wireHints.GetProperty("items").GetArrayLength() : 0);
            if (ordinal > 0)
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, cap);
            HttpResponseMessage response;
            switch (ordinal++)
            {
                case 0:
                    response = Completion("""{"queries":[{"query":"overview","topK":12}]}""");
                    break;
                case 1:
                    response = OmittedHandleFunction(protocol, "earlier-page", "read_source",
                        """{"sourceKey":"internal-source-1","pageStart":14,"pageCount":1,"topK":20}""");
                    break;
                case 2:
                    // The entire first read is present, so no omission existed when its IDs were learned.
                    foreach (var body in first)
                    {
                        var item = Assert.Single(user.GetProperty("evidence").EnumerateArray(),
                            item => item.GetProperty("evidenceId").GetString() == body.Reference.EvidenceId);
                        Assert.Equal(body.Content, item.GetProperty("content").GetString());
                    }
                    if (user.TryGetProperty("omittedEvidenceReadHandles", out var initialHandles))
                        Assert.DoesNotContain(initialHandles.GetProperty("items").EnumerateArray(),
                            item => first.Any(body => body.Reference.EvidenceId == item.GetProperty("evidenceId").GetString()));
                    response = OmittedHandleFunction(protocol, "later-page", "read_source",
                        """{"sourceKey":"internal-source-1","pageStart":15,"pageCount":1,"topK":20}""");
                    break;
                case 3:
                    var visible = user.GetProperty("evidence").EnumerateArray()
                        .Select(item => item.GetProperty("evidenceId").GetString()).ToHashSet(StringComparer.Ordinal);
                    displaced = first.Select(body => body.Reference.EvidenceId!).FirstOrDefault(id => !visible.Contains(id));
                    Assert.NotNull(displaced);
                    Assert.Contains(gateway.Evidence, item => item.Reference.EvidenceId == displaced);
                    var offered = user.GetProperty("omittedEvidenceReadHandles").GetProperty("items");
                    Assert.Contains(offered.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == displaced);
                    Assert.DoesNotContain(offered.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == searchOnly.Reference.EvidenceId);
                    AssertWindowOnlyCapability(user, wire, protocol, displaced!, documentaryQuotaExhausted);
                    Assert.Equal(3, gateway.Requests.Count);
                    response = OmittedHandleFunction(protocol, "recover-earlier-body", "read_evidence",
                        JsonSerializer.Serialize(new { evidenceId = displaced, offset = 0, length = 512 }));
                    break;
                case 4:
                    AssertCanonicalWindow(user, displaced!, first);
                    Assert.Equal(3, gateway.Requests.Count);
                    response = Completion(JsonSerializer.Serialize(new
                    {
                        outcome = "answered", answerText = "The earlier documented condition is available [C1].",
                        claims = new[] { new { claimId = "C1", text = "The earlier condition is documented.", evidenceIds = new[] { displaced } } }
                    }));
                    break;
                default:
                    throw new InvalidOperationException("Recovering an already read body requires no new documentary IO or retry.");
            }
            return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);
        });
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = cap;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(displaced, Assert.Single(result.Claims).EvidenceIds.Single());
        Assert.Equal(3, gateway.Requests.Count);
        Assert.Equal(2, gateway.Requests.Count(request => request.Operation == "read_source"));
        Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_evidence");
        if (documentaryQuotaExhausted) Assert.Equal(0, gateway.Budget!.RemainingCalls);
        var probes = logger.Events.Where(entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "llm.call")).Select(entry =>
        {
            using var measurements = JsonDocument.Parse((string)entry.Values["Measurements"]!);
            Assert.DoesNotContain(first[0].Content, entry.Text, StringComparison.Ordinal);
            return measurements.RootElement.GetProperty("omitted_evidence_read_handle_hint_count").GetInt32();
        }).ToArray();
        Assert.Equal(wireHintCounts, probes);
        Assert.Equal(0, probes[2]); // A was visible; its private metadata does not create public handles yet.
        Assert.True(probes[3] > 0); // The actual later wire now offers the displaced canonical IDs.
    }

    [Fact]
    public void Previously_visible_read_registration_is_pure_during_fitting_and_never_grants_unread_ids()
    {
        var first = VisibleReadBodies("READ", 14, 6, 650);
        var unread = VisibleReadBodies("SEARCH-ONLY", 11, 1, 650).Concat(VisibleReadBodies("UNPUBLISHED", 15, 1, 650));
        var active = first.Concat(unread).ToArray();
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        var turn = OmittedRegistryTurn([first]);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var registry = context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!;
        var original = registry.GetValue(context);
        var trial = OmittedHandleProjection(provider, active, turn, context);
        Assert.Same(original, registry.GetValue(context));
        Assert.Empty((IEnumerable)registry.GetValue(context)!);
        using var firstPrompt = JsonDocument.Parse(OmittedHandlePrompt(provider, trial, context));
        Assert.False(firstPrompt.RootElement.TryGetProperty("omittedEvidenceReadHandles", out _));
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        using var next = JsonDocument.Parse(OmittedHandlePrompt(provider, OmittedEmptyProjection(provider, active), context));
        var handles = next.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items");
        Assert.Equal(first.Select(item => item.Reference.EvidenceId), handles.EnumerateArray().Select(item => item.GetProperty("evidenceId").GetString()));
        Assert.All(handles.EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("content", out _));
            Assert.False(item.TryGetProperty("sourceReference", out _));
            Assert.False(item.TryGetProperty("canonicalContentSha256", out _));
        });
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("body_same_length")]
    [InlineData("reference_in_place")]
    [InlineData("duplicate_empty")]
    [InlineData("hash")]
    [InlineData("pages")]
    public void Previously_visible_read_capability_expires_before_reopening_if_canonical_identity_changes(string change)
    {
        var body = VisibleReadBodies("READ", 14, 1, 650).Single();
        var gateway = new AccumulatedReadGateway([body]);
        var context = AccumulatedReadContext(gateway);
        var turn = OmittedRegistryTurn([[body]]);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        using var first = JsonDocument.Parse(OmittedHandlePrompt(provider, OmittedHandleProjection(provider, [body], turn, context), context));
        Assert.Single((IEnumerable)context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        gateway.Active = change switch
        {
            "retired" => [],
            "body_same_length" => [body with { Content = "Y" + body.Content[1..] }],
            "duplicate_empty" => [body, body with { Content = "" }],
            "hash" => [body with { Reference = VisibleReference(body.Reference, sourceHash: "changed") }],
            "pages" => [body with { Reference = VisibleReference(body.Reference, page: 15) }],
            _ => [body]
        };
        if (change == "reference_in_place")
            typeof(AdvancedAnalysisResultEvidence).GetProperty(nameof(AdvancedAnalysisResultEvidence.DocPath))!
                .SetValue(body.Reference, "Documents/changed.pdf");
        var projection = OmittedEmptyProjection(provider, gateway.Active);
        using var next = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        Assert.False(next.RootElement.TryGetProperty("omittedEvidenceReadHandles", out _));
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new
            { evidenceReads = new[] { new { evidenceId = body.Reference.EvidenceId, offset = 0, length = 512 } } }));
        Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadRequests", read.RootElement, projection, context));
    }

    [Fact]
    public void Previously_visible_read_registry_keeps_only_the_last_sixty_four_canonical_metadata_entries()
    {
        var groups = Enumerable.Range(0, 4).Select(index => VisibleReadBodies("READ-" + index, 14 + index, 20, 650)).ToArray();
        var active = groups.SelectMany(group => group).ToArray();
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        var turn = OmittedRegistryTurn(groups);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        using var visible = JsonDocument.Parse(OmittedHandlePrompt(provider, OmittedHandleProjection(provider, active, turn, context), context));
        Assert.Equal(80, visible.RootElement.GetProperty("evidence").GetArrayLength());
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        using var omitted = JsonDocument.Parse(OmittedHandlePrompt(provider, OmittedEmptyProjection(provider, active), context));
        var handles = omitted.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items");
        Assert.Equal(active.Skip(16).Select(item => item.Reference.EvidenceId), handles.EnumerateArray().Select(item => item.GetProperty("evidenceId").GetString()));
    }

    [Fact]
    public void Current_nonprefix_omissions_keep_their_promised_handles_when_visible_read_metadata_exceeds_sixty_four()
    {
        var groups = Enumerable.Range(0, 4).Select(index => VisibleReadBodies("READ-" + index, 14 + index, 20, 650)).ToArray();
        var active = groups.SelectMany(group => group).ToArray();
        var omittedIds = groups.SelectMany(group => new[] { group[0].Reference.EvidenceId!, group[10].Reference.EvidenceId! })
            .ToHashSet(StringComparer.Ordinal);
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, OmittedRegistryTurn(groups));
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var all = SourceCatalogueProjection(provider, active);
        var type = all.GetType();
        var itemType = type.BaseType!.GetGenericArguments()[0];
        var shown = ((IEnumerable)all).Cast<object>().Where(item =>
            !omittedIds.Contains((string)itemType.GetProperty("EvidenceId")!.GetValue(item)!)).ToArray();
        Assert.Equal(72, shown.Length);
        var array = Array.CreateInstance(itemType, shown.Length);
        for (var i = 0; i < shown.Length; i++) array.SetValue(shown[i], i);
        var projection = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [array, active.Length], null)!;
        type.GetProperty("SourceCatalogue")!.SetValue(projection, type.GetProperty("SourceCatalogue")!.GetValue(all));
        type.GetProperty("SourceCatalogueEnabled")!.SetValue(projection, true);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var offered = prompt.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items");
        Assert.Equal(omittedIds.Order(StringComparer.Ordinal), offered.EnumerateArray()
            .Select(item => item.GetProperty("evidenceId").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(64, ((IEnumerable)context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context)!).Cast<object>().Count());
    }

    private static AdvancedAnalysisResultEvidence VisibleReference(AdvancedAnalysisResultEvidence source, string? sourceHash = null, int? page = null)
        => new()
        {
            EvidenceId = source.EvidenceId, DocId = source.DocId, RevisionId = source.RevisionId,
            SourceHash = sourceHash ?? source.SourceHash, DocPath = source.DocPath, FileName = source.FileName,
            ChunkId = source.ChunkId, PageStart = page ?? source.PageStart, PageEnd = page ?? source.PageEnd
        };

    private static AdvancedAnalysisResolvedEvidence[] VisibleReadBodies(string prefix, int page, int count, int padding)
        => Enumerable.Range(0, count).Select(index => PrimaryReadBody(prefix + "-" + index,
            "Canonical maintenance condition " + prefix + "-" + index + ": " + new string('x', padding)) with
        {
            Reference = new()
            {
                EvidenceId = prefix + "-" + index, DocId = "11111111-1111-4111-8111-111111111111",
                RevisionId = "22222222-2222-4222-8222-222222222222", SourceHash = "sha256:test",
                DocPath = "Documents/inspection.pdf", FileName = "inspection.pdf", ChunkId = prefix + "-chunk-" + index,
                PageStart = page, PageEnd = page
            }
        }).ToArray();

    private sealed class VisibleReadContinuityGateway(IReadOnlyList<AdvancedAnalysisResolvedEvidence> first,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> later, AdvancedAnalysisResolvedEvidence searchOnly,
        bool exhaustAfterLater) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _active = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public AdvancedAnalysisToolBudget? Budget => exhaustAfterLater
            ? new(3, Requests.Count, 10_000, Requests.Count, 128, _active.Count) : null;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> found = Requests.Count switch
            {
                1 when request.Operation == "search_corpus" => [searchOnly],
                2 when request.Operation == "read_source" && request.PageStart == 14 => first,
                3 when request.Operation == "read_source" && request.PageStart == 15 => later,
                _ => throw new InvalidOperationException("Only the initial search and two distinct explicit page reads are allowed.")
            };
            foreach (var body in found)
                if (_active.All(item => item.Reference.EvidenceId != body.Reference.EvidenceId)) _active.Add(body);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 1, Requests.Count));
        }
    }
}
