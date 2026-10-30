using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const BindingFlags SourceCatalogueFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task SourceCatalogue_active_sources_survive_primary_projection_and_share_schema_parser_scope(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 8_000;
        options.NativeObservationReadEnabled = false;
        options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var active = SourceCatalogueEvidence();
        var projection = SourceCatalogueProjection(provider, active);
        var visible = ((IEnumerable)projection).Cast<object>().ToArray();
        Assert.Single(visible);
        var prompt = SourceCataloguePrompt(provider, projection);

        var task = Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(), "writer", "Return JSON.",
            prompt, 128, CancellationToken.None, true, null, projection));
        await task;

        var body = Assert.Single(factory.Requests).Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 8_000);
        var user = WorkspaceUser(body);
        var sources = user.GetProperty("sourceCatalogue").GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(4, sources.Length);
        var keys = sources.Select(item => item.GetProperty("sourceKey").GetString()).Order().ToArray();
        var excerpts = user.GetProperty("evidence").EnumerateArray().ToArray();
        var excerpt = Assert.Single(excerpts);
        Assert.Equal("CATALOGUE-E1", excerpt.GetProperty("evidenceId").GetString());
        Assert.DoesNotContain("PRIVATE_BODY_3", user.GetRawText(), StringComparison.Ordinal);
        foreach (var source in sources)
        {
            Assert.Equal(1, source.GetProperty("firstIndexedPhysicalPage").GetInt32());
            Assert.Equal(80, source.GetProperty("lastIndexedPhysicalPage").GetInt32());
            Assert.Equal(400, source.GetProperty("canonicalChunkCount").GetInt32());
            Assert.False(source.TryGetProperty("content", out _));
            Assert.False(source.TryGetProperty("sourceReference", out _));
            Assert.False(source.TryGetProperty("docPath", out _));
        }
        using var envelope = JsonDocument.Parse(body);
        var functions = envelope.RootElement.GetProperty("tools").EnumerateArray()
            .Select(item => protocol == "responses" ? item : item.GetProperty("function")).ToArray();
        foreach (var name in new[] { "read_source", "find_source_text" })
        {
            var function = Assert.Single(functions, item => item.GetProperty("name").GetString() == name);
            Assert.Equal(keys, function.GetProperty("parameters").GetProperty("properties").GetProperty("sourceKey")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()).Order().ToArray());
        }
        var scopedSearch = Assert.Single(functions, item => item.GetProperty("name").GetString() == "search_corpus");
        Assert.Equal(keys.Prepend(string.Empty).Order().ToArray(), scopedSearch.GetProperty("parameters").GetProperty("properties")
            .GetProperty("sourceKey").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).Order().ToArray());
        var readable = Assert.Single(functions, item => item.GetProperty("name").GetString() == "read_evidence")
            .GetProperty("parameters").GetProperty("properties").GetProperty("evidenceId").GetProperty("enum");
        Assert.Equal(excerpt.GetProperty("evidenceId").GetString(), Assert.Single(readable.EnumerateArray()).GetString());

        var visibleIds = excerpts.Where(item => !string.IsNullOrWhiteSpace(item.GetProperty("content").GetString()))
            .Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        var hiddenCarrier = sources.First(item => !visibleIds.Contains(item.GetProperty("carrierEvidenceId").GetString(), StringComparer.Ordinal))
            .GetProperty("carrierEvidenceId").GetString()!;
        AdvancedAnalysisProviderResult ParsedClaim(string id) => (AdvancedAnalysisProviderResult)InvokeSourceCatalogue(null,
            "ParseResult", JsonSerializer.Serialize(new { outcome = "answered", answerText = "Le seuil observé est 17 bar [C1].",
                claims = new[] { new { claimId = "C1", text = "Le seuil observé est 17 bar.", evidenceIds = new[] { id } } } }),
            active, BuildDirectRequest())!;
        // ParseResult recognizes admitted IDs. The separate final visibility
        // gate must still reject a catalogue carrier whose body was omitted.
        var hiddenResult = ParsedClaim(hiddenCarrier);
        Assert.Equal(hiddenCarrier, Assert.Single(Assert.Single(hiddenResult.Claims).EvidenceIds));
        var citationError = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(hiddenResult, visibleIds));
        Assert.Equal("advanced_final_evidence_not_visible", citationError.ErrorCode);
        OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(ParsedClaim(Assert.Single(visibleIds)), visibleIds);

        var omittedSources = active.Where(item => item.Reference.EvidenceId != excerpt.GetProperty("evidenceId").GetString()).ToArray();
        Assert.Equal(3, omittedSources.Length);
        foreach (var source in omittedSources)
        {
            var key = SourceCatalogueKeyFor(projection, source.Reference.DocId);
            var read = SourceCatalogueParse(provider, projection, key, review: false);
            Assert.Equal("read_source", read.Operation);
            Assert.Equal(60, read.TopK);
            Assert.Equal(1, read.PageStart);
            Assert.Equal(4, read.PageEnd);
            Assert.Equal(source.Reference.DocId, read.DocId);
            Assert.Equal(source.Reference.RevisionId, read.RevisionId);
            Assert.Equal(source.Reference.DocPath, read.DocPath);
            var find = SourceCatalogueParse(provider, projection, key, review: true);
            Assert.Equal("find_source_text", find.Operation);
            Assert.Equal(source.Reference.DocId, find.DocId);
            Assert.Equal(source.Reference.RevisionId, find.RevisionId);
            Assert.Equal(source.Reference.DocPath, find.DocPath);
        }
        Assert.Equal(4, active.Length);
        Assert.Equal(8_000, options.MaximumEvidencePromptCharacters);
    }

    [Fact]
    public void SourceCatalogue_retired_source_is_not_resurrected_from_alias_cache_and_surviving_aliases_are_stable()
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var active = SourceCatalogueEvidence();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var before = SourceCatalogueProjection(provider, active, aliases);
        var retiredKey = SourceCatalogueKeyFor(before, active[1].Reference.DocId);
        var retainedKey = SourceCatalogueKeyFor(before, active[2].Reference.DocId);
        var after = SourceCatalogueProjection(provider, [active[0], active[2], active[3]], aliases);

        Assert.Equal(3, SourceCatalogueHandles(after).Length);
        Assert.Equal(4, aliases.Count);
        Assert.Equal(retainedKey, SourceCatalogueKeyFor(after, active[2].Reference.DocId));
        Assert.Null(InvokeSourceCatalogue(null, "ResolveObservedSource", after, retiredKey));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => SourceCatalogueParse(provider, after, retiredKey, review: false));
        Assert.Equal("advanced_planner_protocol_invalid", error.ErrorCode);
        error = Assert.Throws<AdvancedAnalysisProviderException>(() => SourceCatalogueParse(provider, after, "unknown-source", review: true));
        Assert.Equal("advanced_research_review_protocol_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void SourceCatalogue_same_document_revision_with_different_canonical_sha_remains_two_sources()
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var active = SourceCatalogueEvidence();
        var original = active[0];
        var changed = original with
        {
            Reference = SourceCatalogueReference(original.Reference, "CATALOGUE-OTHER-HASH", new string('e', 64))
        };
        var projection = SourceCatalogueProjection(provider, [original, changed]);
        var keys = SourceCatalogueHandles(projection).Select(SourceCatalogueHandleKey).ToArray();
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        var hashes = keys.Select(key => ((AdvancedAnalysisResultEvidence)InvokeSourceCatalogue(null, "ResolveObservedSource", projection, key)!).SourceHash);
        Assert.Equal(new[] { original.Reference.SourceHash, changed.Reference.SourceHash }.Order(), hashes.Order());
    }

    [Fact]
    public void SourceCatalogue_legacy_observation_alias_conflict_is_rejected_before_source_request()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 64_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var projection = SourceCatalogueProjection(provider, SourceCatalogueEvidence());
        var items = ((IEnumerable)projection).Cast<object>().ToArray();
        Assert.True(items.Length >= 2);
        var itemType = items[0].GetType();
        var key = (string)itemType.GetProperty("SourceKey")!.GetValue(items[0])!;
        itemType.GetProperty("SourceKey")!.SetValue(items[1], key);
        var legacy = Array.CreateInstance(itemType, 2); legacy.SetValue(items[0], 0); legacy.SetValue(items[1], 1);
        Assert.Throws<JsonException>(() => InvokeSourceCatalogue(null, "ResolveObservedSource", legacy, key));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => SourceCatalogueParse(provider, legacy, key, review: false));
        Assert.Equal("advanced_planner_protocol_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCatalogue_conflicting_active_path_or_cached_alias_fails_closed(bool sharedAlias)
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var active = SourceCatalogueEvidence();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sharedAlias)
        {
            foreach (var item in active.Take(2))
                aliases.Add(string.Join("|", item.Reference.DocId, item.Reference.RevisionId, item.Reference.SourceHash), "internal-source-1");
        }
        else
        {
            var reference = active[0].Reference;
            active[1] = active[1] with { Reference = new AdvancedAnalysisResultEvidence
            {
                EvidenceId = active[1].Reference.EvidenceId, DocId = reference.DocId, RevisionId = reference.RevisionId,
                SourceHash = reference.SourceHash, DocPath = "Different/canonical.pdf", FileName = "canonical.pdf",
                ChunkId = active[1].Reference.ChunkId, PageStart = 2, PageEnd = 2
            } };
        }
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => SourceCatalogueProjection(provider, active, aliases));
        Assert.Equal("advanced_source_catalogue_identity_conflict", error.ErrorCode);
    }

    [Fact]
    public void SourceCatalogue_handle_does_not_grant_unobserved_native_raw_locator()
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var active = SourceCatalogueEvidence(); var projection = SourceCatalogueProjection(provider, active);
        var prompt = (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", SourceCataloguePrompt(provider, projection), projection)!;
        using var current = JsonDocument.Parse(prompt);
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sourceKey = SourceCatalogueKeyFor(projection, active[2].Reference.DocId), pageStart = 2, pageEnd = 2,
            nativeObservationView = "raw", nativeObservationBlockId = "unobserved-block", nativeObservationRawSha256 = new string('a', 64)
        }));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "RequireObservedNativeRawLocator", arguments.RootElement, current.RootElement));
        Assert.Equal("advanced_native_raw_locator_not_observed", error.ErrorCode);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public void SourceCatalogue_incompressible_active_metadata_refuses_before_http_without_raising_eight_k_cap(string protocol)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        options.MaximumAccumulatedEvidenceItems = 128;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var active = Enumerable.Range(1, 64).Select(index =>
        {
            var item = BuildEvidence("CATALOGUE-LARGE-" + index, "An admitted canonical excerpt.", fileName: new string('"', 160) + ".pdf");
            return item with { Reference = SourceCatalogueReference(item.Reference, item.Reference.EvidenceId, new string('a', 64)),
                SourceOverview = new(1, 1, 1) };
        }).ToArray();

        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => SourceCatalogueProjection(provider, active));

        Assert.Equal("advanced_source_catalogue_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
        Assert.Equal(8_000, options.MaximumEvidencePromptCharacters);
        Assert.Equal(64, active.Length);
        Assert.All(active, item => Assert.Equal("An admitted canonical excerpt.", item.Content));
    }

    private static AdvancedAnalysisResolvedEvidence[] SourceCatalogueEvidence()
        => Enumerable.Range(1, 4).Select(index =>
        {
            var item = BuildEvidence("CATALOGUE-E" + index, "Calibration PRIVATE_BODY_" + index + ": " + new string('"', 10_000),
                fileName: "Manual-" + index + ".pdf", pageStart: index);
            return item with { Reference = SourceCatalogueReference(item.Reference, item.Reference.EvidenceId, new string((char)('a' + index), 64)),
                SourceOverview = new(1, 80, 400) };
        }).ToArray();

    private static AdvancedAnalysisResultEvidence SourceCatalogueReference(AdvancedAnalysisResultEvidence original, string id, string sha)
        => new() { EvidenceId = id, DocId = original.DocId, RevisionId = original.RevisionId, SourceHash = sha,
            DocPath = original.DocPath, FileName = original.FileName, ChunkId = original.ChunkId,
            PageStart = original.PageStart, PageEnd = original.PageEnd };

    private static object SourceCatalogueProjection(OpenAiCompatibleAdvancedAnalysisProvider provider,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IDictionary<string, string>? aliases = null)
        => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(), active,
            new Dictionary<string, HashSet<string>>(), active.Take(1).ToArray(), aliases, null, null)!;

    private static object[] SourceCatalogueHandles(object projection)
        => ((IEnumerable)projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection)!).Cast<object>().ToArray();

    private static string SourceCatalogueHandleKey(object handle)
        => (string)handle.GetType().GetProperty("SourceKey")!.GetValue(handle)!;

    private static string SourceCatalogueKeyFor(object projection, string docId)
        => SourceCatalogueHandleKey(Assert.Single(SourceCatalogueHandles(projection), handle =>
            ((AdvancedAnalysisResultEvidence)InvokeSourceCatalogue(null, "ResolveObservedSource", projection, SourceCatalogueHandleKey(handle))!).DocId == docId));

    private static string SourceCataloguePrompt(OpenAiCompatibleAdvancedAnalysisProvider provider, object projection)
    {
        var prompt = JsonNode.Parse((string)InvokeSourceCatalogue(provider, "BuildWriterUserPrompt", BuildDirectRequest(), projection)!)!.AsObject();
        prompt["researchTools"] = JsonSerializer.SerializeToNode(new { researchAllowed = true, availableCategories = Array.Empty<string>() });
        return prompt.ToJsonString();
    }

    private static AdvancedAnalysisSearchRequest SourceCatalogueParse(OpenAiCompatibleAdvancedAnalysisProvider provider, object projection, string key, bool review)
    {
        var query = review
            ? JsonSerializer.Serialize(new { decision = "search_more", queries = new[] { new { operation = "find_source_text", sourceKey = key, query = "observed literal", offset = 0, topK = 60 } } })
            : JsonSerializer.Serialize(new { queries = new[] { new { operation = "read_source", sourceKey = key, pageStart = 1, pageEnd = 4, topK = 60 } } });
        var parsed = review
            ? InvokeSourceCatalogue(provider, "ParseResearchReview", query, BuildDirectRequest(), Array.Empty<string>(), projection)
            : InvokeSourceCatalogue(provider, "ParsePlan", query, BuildDirectRequest(), Array.Empty<string>(), projection, false);
        return Assert.Single((IReadOnlyList<AdvancedAnalysisSearchRequest>)parsed!);
    }

    private static object? InvokeSourceCatalogue(object? target, string method, params object?[] arguments)
    {
        try { return typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod(method, SourceCatalogueFlags)!.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
