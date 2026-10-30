using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", "contract")]
    [InlineData("responses", "contract")]
    [InlineData("chat-completions", "agent")]
    [InlineData("responses", "agent")]
    public async Task AdmittedCardNavigation_omitted_bodies_have_existing_source_reads_but_no_citation_permission(string protocol, string style)
    {
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol); options.SynthesisPromptStyle = style;
        options.MaximumEvidencePromptCharacters = 8_000;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        var request = NavigationRequest();
        var active = Enumerable.Range(1, 4).Select(index => NavigationCard("NAV-" + index, "METHOD " + index,
            source: index, page: 10 * index, end: 10 * index + 8) with
            { Content = "UNREAD_BODY_" + index + ": " + new string('"', 10_000) }).ToArray();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var projection = NavigationProjection(provider, request, active, aliases);
        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(), "writer", "Return JSON.",
            NavigationPrompt(provider, request, projection), 128, CancellationToken.None, true, null, projection));
        var body = Assert.Single(factory.Requests).Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 8_000);
        var user = WorkspaceUser(body);
        var nav = user.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(4, nav.GetProperty("activeCardEvidenceCount").GetInt32());
        Assert.Equal(4, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal(4, nav.GetProperty("listedLocatorCount").GetInt32());
        Assert.Equal(0, nav.GetProperty("omittedLocatorCount").GetInt32());
        Assert.Contains("not evidence", nav.GetProperty("instruction").GetString());
        Assert.Contains("page-window limit", nav.GetProperty("instruction").GetString());
        AssertNavigationWireBudget(user, 2_000);
        var visible = user.GetProperty("evidence").EnumerateArray().Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.InRange(visible.Length, 1, 3);
        var hidden = active.First(item => !visible.Contains(item.Reference.EvidenceId, StringComparer.Ordinal));
        using var envelope = JsonDocument.Parse(body);
        var readable = Assert.Single(envelope.RootElement.GetProperty("tools").EnumerateArray()
            .Select(item => protocol == "responses" ? item : item.GetProperty("function")),
            item => item.GetProperty("name").GetString() == "read_evidence")
            .GetProperty("parameters").GetProperty("properties").GetProperty("evidenceId").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.DoesNotContain(hidden.Reference.EvidenceId, readable);
        var locator = Assert.Single(nav.GetProperty("locators").EnumerateArray(),
            item => item.GetProperty("exactTitle").GetString() == hidden.ExactTitle);
        Assert.Equal(hidden.Reference.PageEnd, locator.GetProperty("physicalPageEnd").GetInt32());
        Assert.False(locator.TryGetProperty("evidenceId", out _));
        Assert.DoesNotContain("UNREAD_BODY_" + hidden.Reference.EvidenceId!.Split('-').Last(), user.GetRawText(), StringComparison.Ordinal);
        var key = locator.GetProperty("sourceKey").GetString()!;
        var page = locator.GetProperty("physicalPageStart").GetInt32();
        using var calls = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new
        {
            id = "navigation-read", type = "function", function = new { name = "read_source",
                arguments = JsonSerializer.Serialize(new { sourceKey = key, pageStart = page, pageEnd = page + 3, topK = 4 }) }
        } }));
        var normalized = (string)InvokeSourceCatalogue(provider, "NormalizeNativeResearchCalls", calls.RootElement, user.GetRawText())!;
        var read = Assert.Single((IReadOnlyList<AdvancedAnalysisSearchRequest>)InvokeSourceCatalogue(provider,
            "ParsePlan", normalized, request, Array.Empty<string>(), projection, false)!);
        Assert.Equal(hidden.Reference.DocId, read.DocId); Assert.Equal(hidden.Reference.RevisionId, read.RevisionId);
        Assert.Equal(hidden.Reference.DocPath, read.DocPath); Assert.Equal(page + 3, read.PageEnd);
        var findJson = JsonSerializer.Serialize(new { decision = "search_more", queries = new[]
            { new { operation = "find_source_text", sourceKey = key, query = hidden.ExactTitle, offset = 0, topK = 4 } } });
        var find = Assert.Single((IReadOnlyList<AdvancedAnalysisSearchRequest>)InvokeSourceCatalogue(provider,
            "ParseResearchReview", findJson, request, Array.Empty<string>(), projection)!);
        Assert.Equal(hidden.ExactTitle, find.Query);
        using var hiddenRead = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceId = hidden.Reference.EvidenceId, offset = 0, length = 256 }));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadArguments", hiddenRead.RootElement, user, null));
        Assert.Equal("advanced_native_evidence_read_invalid", error.ErrorCode);
        var result = (AdvancedAnalysisProviderResult)InvokeSourceCatalogue(null, "ParseResult", JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = "An assertion [C1].", claims = new[]
                { new { claimId = "C1", text = "An assertion.", evidenceIds = new[] { hidden.Reference.EvidenceId } } }
        }), active, BuildDirectRequest())!;
        Assert.Equal("advanced_final_evidence_not_visible", Assert.Throws<AdvancedAnalysisProviderException>(() =>
            OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(result, visible)).ErrorCode);
        var returned = hidden with { Content = "CANONICAL_RETURNED_BODY: inspect the isolated seal before operation." };
        var afterRead = NavigationProjection(provider, request, [returned], aliases);
        using var after = JsonDocument.Parse(NavigationPrompt(provider, request, afterRead));
        Assert.Contains("CANONICAL_RETURNED_BODY", after.RootElement.GetProperty("evidence").GetRawText());
        var measurement = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "llm.call");
        using var metrics = JsonDocument.Parse(measurement.Values["Measurements"]!.ToString()!);
        Assert.Equal(4, metrics.RootElement.GetProperty("admitted_card_locator_observed_count").GetInt32());
        Assert.Equal(4, metrics.RootElement.GetProperty("admitted_card_locator_listed_count").GetInt32());
        Assert.Equal(0, metrics.RootElement.GetProperty("admitted_card_locator_omitted_count").GetInt32());
        Assert.Equal(nav.GetProperty("wireCharacters").GetInt32(), metrics.RootElement.GetProperty("admitted_card_locator_wire_chars").GetInt32());
        Assert.Equal(2_000, metrics.RootElement.GetProperty("admitted_card_locator_wire_cap").GetInt32());
        Assert.DoesNotContain("METHOD", metrics.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("UNREAD_BODY", metrics.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AdmittedCardNavigation_all_33_current_cards_can_fit_independently_of_112_excerpt_visibility_and_twenty_coordinates()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 64_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var cards = Enumerable.Range(1, 33).Select(index => NavigationCard("CARD-" + index, "METHOD " + index,
            source: 1 + index % 4, page: index)).ToArray();
        var chunks = Enumerable.Range(1, 79).Select(index => NavigationCard("CHUNK-" + index, null,
            source: 1 + index % 4, page: index, card: false)).ToArray();
        var request = NavigationRequest();
        var projection = NavigationProjection(provider, request, chunks.Concat(cards).ToArray());
        using var prompt = JsonDocument.Parse(NavigationPrompt(provider, request, projection));
        var nav = prompt.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(33, nav.GetProperty("activeCardEvidenceCount").GetInt32());
        Assert.Equal(33, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal(33, nav.GetProperty("listedLocatorCount").GetInt32());
        Assert.Equal(0, nav.GetProperty("omittedLocatorCount").GetInt32());
        Assert.Equal(4, prompt.RootElement.GetProperty("sourceCatalogue").GetProperty("sources").GetArrayLength());
        AssertNavigationWireBudget(prompt.RootElement, 8_000);
        var contract = prompt.RootElement.GetProperty("selectionContract");
        Assert.Equal(20, contract.GetProperty("requiredClaimCount").GetInt32());
        Assert.False(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
        Assert.True(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
    }

    [Fact]
    public void AdmittedCardNavigation_unlocatable_cards_do_not_acquire_titles_pages_or_source_permissions()
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var untitled = NavigationCard("UNTITLED", null);
        var invalidPages = NavigationCard("INVALID-PAGES", "INVALID METHOD", page: 0);
        var missingScope = NavigationCard("MISSING-SCOPE", "UNKNOWN METHOD", source: 2);
        missingScope = missingScope with { Reference = new AdvancedAnalysisResultEvidence
        {
            EvidenceId = missingScope.Reference.EvidenceId, ContentCardId = missingScope.Reference.ContentCardId,
            DocId = missingScope.Reference.DocId, DocPath = missingScope.Reference.DocPath, PageStart = 5, PageEnd = 5
        } };
        var request = NavigationRequest();
        var projection = NavigationProjection(provider, request, [NavigationCard("VALID", "VALID METHOD"), untitled, invalidPages, missingScope]);
        using var prompt = JsonDocument.Parse(NavigationPrompt(provider, request, projection));
        var catalogue = prompt.RootElement.GetProperty("sourceCatalogue");
        Assert.Single(catalogue.GetProperty("sources").EnumerateArray());
        var nav = catalogue.GetProperty("admittedCardLocators");
        Assert.Equal(4, nav.GetProperty("activeCardEvidenceCount").GetInt32());
        Assert.Equal(3, nav.GetProperty("unlocatableCardEvidenceCount").GetInt32());
        Assert.Equal(1, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal("VALID METHOD", Assert.Single(nav.GetProperty("locators").EnumerateArray()).GetProperty("exactTitle").GetString());
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void AdmittedCardNavigation_deduplicates_only_exact_title_pages_and_canonical_scope_and_retires_current_cards()
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var original = NavigationCard("ORIGINAL", "Exact METHOD");
        var exactCopy = NavigationCard("COPY", "Exact METHOD");
        var otherSource = NavigationCard("OTHER-SOURCE", "Exact METHOD", source: 2);
        var otherHash = NavigationCard("OTHER-HASH", "Exact METHOD", hash: new string('e', 64));
        var otherPage = NavigationCard("OTHER-PAGE", "Exact METHOD", page: 6);
        var otherCase = NavigationCard("OTHER-CASE", "exact METHOD");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var request = NavigationRequest();
        var before = NavigationProjection(provider, request, [original, exactCopy, otherSource, otherHash, otherPage, otherCase], aliases);
        using var first = JsonDocument.Parse(NavigationPrompt(provider, request, before));
        var nav = first.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(6, nav.GetProperty("activeCardEvidenceCount").GetInt32());
        Assert.Equal(5, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal(5, nav.GetProperty("listedLocatorCount").GetInt32());
        var sameTitle = nav.GetProperty("locators").EnumerateArray().Where(item => item.GetProperty("exactTitle").GetString() == "Exact METHOD").ToArray();
        Assert.Equal(4, sameTitle.Length);
        Assert.Equal(3, sameTitle.Select(item => item.GetProperty("sourceKey").GetString()).Distinct().Count());
        var retiredKey = sameTitle.First(item => item.GetProperty("sourceKey").GetString() != sameTitle[0].GetProperty("sourceKey").GetString())
            .GetProperty("sourceKey").GetString()!;
        var after = NavigationProjection(provider, request, [original], aliases);
        using var current = JsonDocument.Parse(NavigationPrompt(provider, request, after));
        var remaining = current.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(1, remaining.GetProperty("observedLocatorCount").GetInt32());
        Assert.Single(remaining.GetProperty("locators").EnumerateArray());
        Assert.Null(InvokeSourceCatalogue(null, "ResolveObservedSource", after, retiredKey));
        Assert.Equal(3, aliases.Count);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(64_000)]
    public void AdmittedCardNavigation_oversized_control_unicode_metadata_keeps_whole_entries_and_announces_exact_omissions(int cap)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = cap;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var hugeTitle = "LONG\u0001\"\\\r\n" + string.Concat(Enumerable.Repeat("É\U0001F9EA", 10_000));
        var exactTitle = "Case\u0001\"\\\r\nÉ\U0001F9EA";
        var active = new[] { NavigationCard("HUGE", hugeTitle), NavigationCard("SHORT", exactTitle) }
            .Concat(Enumerable.Range(0, 150).Select(index => NavigationCard("MANY-" + index, "METHOD " + index, page: 10 + index))).ToArray();
        object Project() => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", NavigationRequest(), active,
            new Dictionary<string, HashSet<string>>(), active.Skip(1).Take(1).ToArray(), null, null, null)!;
        var projection = Project();
        using var prompt = JsonDocument.Parse(NavigationPrompt(provider, NavigationRequest(), projection));
        var nav = prompt.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(152, nav.GetProperty("observedLocatorCount").GetInt32());
        var listed = nav.GetProperty("locators").EnumerateArray().ToArray();
        Assert.InRange(listed.Length, 1, 128);
        Assert.Equal(152 - listed.Length, nav.GetProperty("omittedLocatorCount").GetInt32());
        Assert.Contains(listed, item => item.GetProperty("exactTitle").GetString() == exactTitle);
        Assert.DoesNotContain(listed, item => item.GetProperty("exactTitle").GetString() == hugeTitle);
        Assert.All(listed, item => Assert.Contains(active, card => card.ExactTitle == item.GetProperty("exactTitle").GetString()
            && card.Reference.PageStart == item.GetProperty("physicalPageStart").GetInt32()
            && card.Reference.PageEnd == item.GetProperty("physicalPageEnd").GetInt32()));
        AssertNavigationWireBudget(prompt.RootElement, Math.Min(cap / 4, 8_000));
        var repeated = Project();
        using var repeatedPrompt = JsonDocument.Parse(NavigationPrompt(provider, NavigationRequest(), repeated));
        Assert.Equal(nav.GetRawText(), repeatedPrompt.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators").GetRawText());
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("factual")]
    [InlineData("unstructured")]
    [InlineData("distinct")]
    [InlineData("single")]
    public void AdmittedCardNavigation_nonrepeatable_calls_keep_existing_catalogue_metadata(string kind)
    {
        using var factory = new QueuedHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        var request = NavigationRequest(kind);
        var card = NavigationCard("NAV", "METHOD");
        var withoutCard = NavigationCard("NAV", "METHOD", card: false);
        using var first = JsonDocument.Parse(NavigationPrompt(provider, request, NavigationProjection(provider, request, [card])));
        using var second = JsonDocument.Parse(NavigationPrompt(provider, request, NavigationProjection(provider, request, [withoutCard])));
        var catalogue = first.RootElement.GetProperty("sourceCatalogue");
        Assert.False(catalogue.TryGetProperty("admittedCardLocators", out _));
        Assert.Equal(second.RootElement.GetProperty("sourceCatalogue").GetRawText(), catalogue.GetRawText());
        Assert.Empty(factory.Requests);
    }

    private static AdvancedAnalysisProviderRequest NavigationRequest(string kind = "repeatable")
        => new(Guid.NewGuid(), Guid.NewGuid(), "test-user", new AdvancedAnalysisHandoffEnvelope
        {
            HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow, Language = "fr",
            RequestText = "Document twenty inspection and calibration coordinates; named options may be reused.",
            OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
            Load = new() { PlanKind = "bounded_grid", Deliverable = "documented options", StructuredLayout = kind != "unstructured",
                AnswerUnitCount = kind == "single" ? 1 : 20, AtomicEvidenceCount = 4, AtomicEvidenceType = "documented_option",
                AtomicEvidenceMode = kind == "factual" ? "content_claims" : "named_item",
                SelectionPolicy = kind == "distinct" ? "distinct" : "structured_layout", RowCount = 5, ColumnCount = 4,
                RowLabels = ["A", "B", "C", "D", "E"], Columns = ["inspection", "calibration", "isolation", "verification"] },
            ResearchState = new() { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
        }, [], []);

    private static AdvancedAnalysisResolvedEvidence NavigationCard(string id, string? title, int source = 1,
        int page = 5, int? end = null, string? hash = null, bool card = true)
    {
        var doc = new Guid(source, 0, 0, new byte[8]).ToString("D");
        var revision = new Guid(source + 100, 0, 0, new byte[8]).ToString("D");
        return new(new AdvancedAnalysisResultEvidence
        {
            EvidenceId = id, ContentCardId = card ? "card-" + id : null, DocId = doc, RevisionId = revision,
            SourceHash = hash ?? new string('a', 64), DocPath = "Manual-" + source + ".pdf", FileName = "Manual-" + source + ".pdf",
            PageStart = page, PageEnd = end ?? page, ChunkId = "chunk-" + id
        }, "Canonical body " + id + ": inspect the isolated seal before operation.", title) { SourceOverview = new(1, 200, 600) };
    }

    private static object NavigationProjection(OpenAiCompatibleAdvancedAnalysisProvider provider, AdvancedAnalysisProviderRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IDictionary<string, string>? aliases = null)
        => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request, active,
            new Dictionary<string, HashSet<string>>(), active.Take(1).ToArray(), aliases, null, null)!;

    private static string NavigationPrompt(OpenAiCompatibleAdvancedAnalysisProvider provider, AdvancedAnalysisProviderRequest request, object projection)
    {
        var prompt = JsonNode.Parse((string)InvokeSourceCatalogue(provider, "BuildWriterUserPrompt", request, projection)!)!.AsObject();
        prompt["researchTools"] = JsonSerializer.SerializeToNode(new
            { researchAllowed = true, evidenceReadingAllowed = true, maximumQueries = 4, availableCategories = Array.Empty<string>() });
        return (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", prompt.ToJsonString(), projection)!;
    }

    private static void AssertNavigationWireBudget(JsonElement user, int expectedCap)
    {
        var catalogue = user.GetProperty("sourceCatalogue");
        var navigation = catalogue.GetProperty("admittedCardLocators");
        var baseline = JsonNode.Parse(catalogue.GetRawText())!.AsObject(); baseline.Remove("admittedCardLocators");
        var options = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetField("JsonOptions", SourceCatalogueFlags)!.GetValue(null)!;
        int Size(string text) => JsonSerializer.Serialize(text, options).Length - 2;
        var exactDelta = Size(catalogue.GetRawText()) - Size(baseline.ToJsonString(options));
        Assert.Equal(exactDelta, navigation.GetProperty("wireCharacters").GetInt32());
        Assert.Equal(expectedCap, navigation.GetProperty("wireCharacterLimit").GetInt32());
        Assert.InRange(exactDelta, 1, expectedCap);
        Assert.Equal(navigation.GetProperty("listedLocatorCount").GetInt32(), navigation.GetProperty("locators").GetArrayLength());
        Assert.Equal(navigation.GetProperty("observedLocatorCount").GetInt32(),
            navigation.GetProperty("listedLocatorCount").GetInt32() + navigation.GetProperty("omittedLocatorCount").GetInt32());
    }
}
