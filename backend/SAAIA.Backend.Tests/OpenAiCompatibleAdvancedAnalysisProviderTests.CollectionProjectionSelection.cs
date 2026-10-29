using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static AdvancedAnalysisLoadDescriptor SelectionCollectionLoad(string mode = "named_item", bool bounded = false)
        => new()
        {
            PlanKind = "multi_item", Deliverable = "documented alternatives", StructuredLayout = true,
            AnswerUnitCount = 4, AtomicEvidenceCount = 4, AtomicEvidenceType = "documented_option",
            AtomicEvidenceMode = mode, SelectionPolicy = "structured_layout", RowCount = 2, ColumnCount = 2,
            RowLabels = ["A", "B"], Columns = ["inspection", "calibration"], BoundedNamedDocumentExtraction = bounded
        };

    private static AdvancedAnalysisResolvedEvidence SelectionEvidence(string id, string text,
        string? title = null, string doc = "doc-a", string revision = "revision-a", string hash = "hash-a", int page = 5)
        => new(new AdvancedAnalysisResultEvidence
        {
            EvidenceId = id, DocId = doc, RevisionId = revision, SourceHash = hash,
            DocPath = doc + ".pdf", FileName = doc + ".pdf", PageStart = page, PageEnd = page, ChunkId = "chunk-" + id
        }, text, title);

    [Fact]
    public void EvidenceSelection_exact_card_chunk_copies_follow_alternatives_without_losing_original_objects()
    {
        const string body = "METHOD ALPHA\nCalibration: preserve ±0.125 mm and −5.25 °C exactly.";
        var overview = new AdvancedAnalysisSourceOverview(1, 177, 450)
        {
            TableRegionObservations = LargeNativeBudgetCatalog(),
            VisualAssets = new("pixels_available", 5, [5], false, "not_performed", false)
        };
        var quality = new AdvancedAnalysisSourceQuality("ok", false, ["canonical_source_links", "native_pdf_image_inventory"]);
        var chunk = SelectionEvidence("chunk", body) with
        {
            SourceOverview = overview, SourceQuality = quality,
            SourceStructure = new("METHOD ALPHA", "METHOD ALPHA", "previous", "next")
            { ChunkType = "docling_canonical_content_v1", ChunkComposition = "canonical_blocks" }
        };
        var card = SelectionEvidence("card", body, "METHOD ALPHA") with
        {
            SourceOverview = overview with { }, SourceQuality = quality with { },
            SourceStructure = new(null, null, null, null)
            { ChunkType = "docling_canonical_content_v1", ChunkComposition = "canonical_blocks" }
        };
        var alternative = SelectionEvidence("alternative", "METHOD BETA\nInspect the separate seal before calibration.", "METHOD BETA");
        var input = new[] { chunk, card, alternative };
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(
            SelectionCollectionLoad(), input, new Dictionary<string, HashSet<string>>());
        Assert.Equal(new[] { "card", "alternative", "chunk" }, result.Select(item => item.Reference.EvidenceId));
        Assert.Same(card, result[0]); Assert.Same(alternative, result[1]); Assert.Same(chunk, result[2]);
        Assert.Equal("previous", result[2].SourceStructure!.PreviousChunkId);
        Assert.True(OpenAiCompatibleAdvancedAnalysisProvider.CanDeferExactProjectionCopy(chunk, card));
        Assert.Equal(body, result[2].Content); Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "chunk", "card", "alternative" }, input.Select(item => item.Reference.EvidenceId));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("page")]
    [InlineData("title")]
    [InlineData("quality")]
    [InlineData("one-character")]
    [InlineData("unicode-form")]
    [InlineData("missing-scope")]
    [InlineData("overview")]
    [InlineData("chunk-type")]
    [InlineData("composition")]
    [InlineData("unassigned-context")]
    public void EvidenceSelection_same_words_are_not_a_duplicate_across_provenance_or_factual_variants(string variant)
    {
        var first = SelectionEvidence("first", "Calibration café: ±0.125 mm.", "METHOD ALPHA");
        var second = SelectionEvidence("second", first.Content, first.ExactTitle,
            doc: variant == "doc" ? "doc-b" : variant == "missing-scope" ? "" : "doc-a",
            revision: variant == "revision" ? "revision-b" : "revision-a",
            hash: variant == "hash" ? "hash-b" : "hash-a", page: variant == "page" ? 6 : 5);
        if (variant == "title") second = second with { ExactTitle = "METHOD BETA" };
        if (variant == "quality") second = second with { SourceQuality = new("review", true, ["ocr_uncertain"]) };
        if (variant == "one-character") second = second with { Content = "Calibration café: ±0.126 mm." };
        if (variant == "unicode-form") second = second with { Content = first.Content.Replace("é", "e\u0301") };
        if (variant == "overview") second = second with { SourceOverview = new(1, 100, 300) { TableRegionObservations = LargeNativeBudgetCatalog() } };
        if (variant == "chunk-type") second = second with { SourceStructure = new(null, null, null, null) { ChunkType = "a_different_surface" } };
        if (variant == "composition") second = second with { SourceStructure = new(null, null, null, null) { ChunkComposition = "table_grid" } };
        if (variant == "unassigned-context") second = second with { SourceStructure = new(null, null, null, null) { ContainsUnassignedPhysicalContext = true } };
        Assert.False(OpenAiCompatibleAdvancedAnalysisProvider.CanDeferExactProjectionCopy(first, second));
    }

    [Fact]
    public void EvidenceSelection_exact_text_with_complementary_query_roles_stays_separate_without_fabricating_a_union()
    {
        var card = SelectionEvidence("card", "Inspection calibration common procedure.", "METHOD ALPHA");
        var copy = SelectionEvidence("copy", card.Content);
        var alternative = SelectionEvidence("other", "Inspection calibration independent method.", "METHOD BETA");
        var queries = new Dictionary<string, HashSet<string>>
        {
            ["card"] = ["inspection common"], ["copy"] = ["calibration common"], ["other"] = ["inspection independent"]
        };
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(
            SelectionCollectionLoad(), [card, copy, alternative], queries);
        Assert.Same(card, result[0]); Assert.Same(copy, result[1]);
        Assert.Equal(new[] { "inspection common" }, queries["card"]);
        Assert.Equal(new[] { "calibration common" }, queries["copy"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void EvidenceSelection_a_titled_original_with_all_query_roles_can_defer_its_exact_body_copy()
    {
        var card = SelectionEvidence("card", "Inspection calibration common procedure.", "METHOD ALPHA");
        var copy = SelectionEvidence("copy", card.Content);
        var alternative = SelectionEvidence("other", "Inspection independent method.", "METHOD BETA");
        var queries = new Dictionary<string, HashSet<string>>
        {
            ["card"] = ["inspection common", "calibration common"], ["copy"] = ["calibration common"],
            ["other"] = ["inspection independent"]
        };
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(
            SelectionCollectionLoad(), [card, copy, alternative], queries);
        Assert.Equal(new[] { "card", "other", "copy" }, result.Select(item => item.Reference.EvidenceId));
        Assert.Same(copy, result[^1]); Assert.Equal(2, queries["card"].Count);
    }

    [Fact]
    public void EvidenceSelection_raw_and_explicit_window_capabilities_are_never_text_deduplicated()
    {
        var first = SelectionEvidence("first", "A literal row ±0.125.");
        var raw = SelectionEvidence("raw", first.Content) with
        {
            SourceObservation = new("observation", "table", "line", 0, "text-sha", "raw-sha", "raw", 0, null, 2, 0, 1, 1, ["secondary_raw"])
        };
        var window = SelectionEvidence("window", first.Content) with
        {
            TableReadWindow = new("read the current row", Operation: "read_source", PageStart: 5, PageEnd: 5)
        };
        Assert.False(OpenAiCompatibleAdvancedAnalysisProvider.CanDeferExactProjectionCopy(first, raw));
        Assert.False(OpenAiCompatibleAdvancedAnalysisProvider.CanDeferExactProjectionCopy(first, window));
    }

    [Fact]
    public void EvidenceSelection_two_explicit_titles_do_not_collapse_through_a_titleless_intermediate()
    {
        var a = SelectionEvidence("a", "The exact common body.", "ALPHA");
        var copy = SelectionEvidence("copy", a.Content);
        var b = SelectionEvidence("b", a.Content, "BETA");
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(
            SelectionCollectionLoad(), [a, copy, b], new Dictionary<string, HashSet<string>>());
        Assert.Equal(new[] { "a", "copy", "b" }, result.Select(item => item.Reference.EvidenceId));
    }

    [Fact]
    public void EvidenceSelection_body_ranking_never_merges_distinct_structural_contexts()
    {
        const string body = "The same complete literal body in two observed structural contexts.";
        var first = SelectionEvidence("first", body, "METHOD ALPHA") with
        { SourceStructure = new("Section A", "Chapter A/Section A", "previous-a", "next-a") };
        var second = SelectionEvidence("second", body) with
        { SourceStructure = new("Section B", "Chapter B/Section B", "previous-b", "next-b") };
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(
            SelectionCollectionLoad(), [second, first], new Dictionary<string, HashSet<string>>());
        Assert.Equal(new[] { "first", "second" }, result.Select(item => item.Reference.EvidenceId));
        Assert.Same(first, result[0]); Assert.Same(second, result[1]);
        Assert.Same(first.Reference, result[0].Reference); Assert.Same(second.Reference, result[1].Reference);
        Assert.Same(first.SourceStructure, result[0].SourceStructure); Assert.Same(second.SourceStructure, result[1].SourceStructure);
        Assert.Equal("Chapter A/Section A", result[0].SourceStructure!.HeadingPath);
        Assert.Equal("previous-b", result[1].SourceStructure!.PreviousChunkId);
        Assert.All(result, item => Assert.Equal(body, item.Content));
    }

    [Fact]
    public void EvidenceSelection_query_roles_receive_fair_turns_without_promoting_an_unrelated_document()
    {
        var noise = SelectionEvidence("noise", "An unrelated corporate legal notice.", "LEGAL", doc: "unrelated");
        var inspections = Enumerable.Range(1, 8).Select(n => SelectionEvidence("i" + n,
            "Inspection option " + n + ": inspect the seal before operation.", "INSPECTION " + n)).ToArray();
        var calibration = SelectionEvidence("calibration", "Calibration option: calibrate the independent sensor.", "CALIBRATION");
        var evidence = new[] { noise }.Concat(inspections).Append(calibration).ToArray();
        var queries = evidence.ToDictionary(item => item.Reference.EvidenceId,
            item => new HashSet<string>([item == calibration ? "calibration sensor" : "inspection seal"]));
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeCollectionEvidenceForPrompt(SelectionCollectionLoad(), evidence, queries);
        Assert.StartsWith("i", result[0].Reference.EvidenceId);
        Assert.Same(calibration, result[1]);
        Assert.Equal(evidence.Length, result.Count); Assert.Same(noise, result[^1]);
    }

    [Theory]
    [InlineData("content_claims", false)]
    [InlineData("named_item", true)]
    public void EvidenceSelection_factual_and_bounded_navigation_extraction_keep_existing_order(string mode, bool bounded)
    {
        var input = new[] { SelectionEvidence("first", "Table of contents."), SelectionEvidence("next", "Calibration instructions.") };
        var result = OpenAiCompatibleAdvancedAnalysisProvider.PrioritizeRepeatableCollectionEvidenceForPrompt(
            SelectionCollectionLoad(mode, bounded), input,
            new Dictionary<string, HashSet<string>> { ["first"] = ["calibration"], ["next"] = ["calibration"] });
        Assert.Same(input, result);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task EvidenceSelection_actual_wire_keeps_existing_budget_counts_and_literal_window_text(string protocol)
    {
        var load = SelectionCollectionLoad();
        var request = new AdvancedAnalysisProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Propose quatre options documentées d’inspection et de calibration.", Language = "fr",
                OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
                Load = load, ResearchState = new() { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
            }, [], []);
        var text = "METHOD ALPHA\nCalibration: preserve ±0.125 mm and −5.25 °C. " + new string('x', 4_000);
        var original = SelectionEvidence("copy", text);
        var titled = SelectionEvidence("titled", text, "METHOD ALPHA");
        var alternative = SelectionEvidence("alternative", "Inspection of a separate seal. " + new string('y', 4_000), "METHOD BETA");
        var input = new[] { original, titled, alternative };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"repeatable_named_items","queries":[{"query":"inspection seal","topK":12},{"query":"calibration sensor","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { message = "intentional_capture_stop" } }) });
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        var gateway = new RecordingToolGateway(input);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(request, gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_http_400", error.ErrorCode);
        var body = factory.Requests[1].Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 8_000);
        var user = WorkspaceUser(body); AssertWireProjection(user, 3);
        var visible = user.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Contains(visible, item => item.GetProperty("evidenceId").GetString() == "alternative");
        Assert.Contains(visible, item => item.GetProperty("evidenceId").GetString() == "titled");
        var copyIndex = Array.FindIndex(visible, item => item.GetProperty("evidenceId").GetString() == "copy");
        if (copyIndex >= 0)
        {
            Assert.True(Array.FindIndex(visible, item => item.GetProperty("evidenceId").GetString() == "alternative") < copyIndex);
            Assert.True(Array.FindIndex(visible, item => item.GetProperty("evidenceId").GetString() == "titled") < copyIndex);
        }
        foreach (var item in visible)
        {
            var source = input.Single(candidate => candidate.Reference.EvidenceId == item.GetProperty("evidenceId").GetString());
            var content = item.GetProperty("content").GetString()!;
            Assert.Equal(source.Content.Substring(item.GetProperty("contentOffset").GetInt32(), content.Length), content);
            Assert.Equal(source.Content.Length, item.GetProperty("originalContentLength").GetInt32());
        }
        Assert.Equal(3, gateway.Evidence.Count);
    }
}
