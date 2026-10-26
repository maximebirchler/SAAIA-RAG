using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ExtractionQualityPromptBudgetTests(ITestOutputHelper output)
{
    // Diagnostic codes/scalars observed in v14's metadata-only capture; no PDF
    // text or expected documentary facts are copied into these synthetic hits.
    private static RagItemExtractionQuality TypicalQuality() => new()
    {
        ExtractionSource = "docling_canonical_observations", OcrAttempted = true, OcrApplied = false,
        DocumentQualityStatus = "extraction_ok_with_page_review", DocumentExtractionConfidence = 0.82,
        DocumentManualReviewRecommended = true, PageQualityStatus = "manual_review_probable_ocr_noise",
        PageExtractionConfidence = 0.45, PageManualReviewRecommended = true,
        TextStatus = "ok", OcrRecommended = false, ChunkTextStatus = "ok", ChunkTextSparse = false, ChunkOcrCandidate = false,
        Signals = ["text_extraction_ok", "page_review_recommended", "document_intelligence_docling", "canonical_table_sparse_grid",
            "figure_semantics_not_extracted", "canonical_table_native_region_coverage_unavailable", "canonical_native_pdf_image_inventory", "canonical_observation_page_view"],
        ChunkQualitySignals = ["document_intelligence_docling", "canonical_source_links", "canonical_table_sparse_grid",
            "figure_semantics_not_extracted", "canonical_table_native_region_coverage_unavailable", "canonical_native_pdf_image_inventory",
            "canonical_table_grid_html_v1", "canonical_table_explicit_caption_context"],
        SignalsMetadata = new("unavailable"), ChunkQualitySignalsMetadata = new("unavailable"),
        DiagnosticSummary = new()
        {
            NativeTextStatus = "ok", NativeOcrRecommended = false, OcrMode = "docling_auto", OcrDurationMs = 0,
            OcrAppliedReason = "docling_ocr_task_not_observed", PageCount = 1, TextPageCount = 1,
            EmptyPageCount = 0, SparsePageCount = 0, ImagePageCount = 1, PageWarningCount = 1, PageReviewRecommendedCount = 1,
            RetrievalChunkQuality = new() { TotalChunkCount = 35, SearchableChunkCount = 35, RejectedChunkCount = 0, ManualReviewRecommended = false }
        }
    };

    private static readonly SourceBackedIntake Intake = new("Compare the recorded measurements in these four source documents.", "rag.answer", [], [], false, "en");

    private static EvidenceBundle Bundle(RagItemExtractionQuality quality, int count)
    {
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(new
        {
            hits = Enumerable.Range(1, count).Select(i => new
            {
                docId = $"doc-{i}", docPath = $"fixtures/reference-{i}.pdf", pageStart = 1, pageEnd = 1,
                text = $"The recorded source measurement is {i} units.", extractionQuality = quality
            }).ToArray()
        }) });
        return EvidenceBundleBuilder.FromToolResults(tools, Intake.UserQuestion);
    }

    private static IReadOnlyList<SourceBackedAgentMessage> WriterMessages(EvidenceBundle bundle) =>
        (IReadOnlyList<SourceBackedAgentMessage>)typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildSelectedEvidenceWriterMessages", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [Intake, bundle, bundle.Items.Select(item => item.EvidenceId).ToArray(), "content_claim", null, null, null])!;

    [Fact]
    public void Typical_four_source_quality_payload_preserves_warnings_without_serializing_null_scalars()
    {
        var messages = WriterMessages(Bundle(TypicalQuality(), 4));
        var context = string.Join('\n', messages.Select(message => message.Content));
        Assert.Contains("canonical_table_native_region_coverage_unavailable", context);
        Assert.Contains("canonical_table_explicit_caption_context", context);
        Assert.Contains("unavailable", context);
        Assert.DoesNotContain("\"ocrLanguages\":null", context);
        Assert.DoesNotContain("\"ocrFailureReason\":null", context);
    }

    [Fact]
    public void Typical_four_source_writer_fits_existing_4k_estimation_contract()
    {
        var messages = WriterMessages(Bundle(TypicalQuality(), 4));
        var estimated = LlmCostBudgetGuard.EstimateInputTokens(messages, []);
        output.WriteLine($"productionFallbackEstimatedInput={estimated}; output=900; reserve=64; context=4096");
        // This is the existing production fallback estimator, not a measurement
        // from a model tokenizer. Native exact-token admission remains separate.
        Assert.True(estimated + 900 + 64 <= 4096, $"Estimated writer input grew to {estimated} tokens.");
    }

    [Fact]
    public async Task Oversized_quality_preserves_flags_and_is_rejected_by_context_preflight_before_completion()
    {
        var quality = TypicalQuality();
        quality.Signals = Enumerable.Range(1, 64).Select(i => $"signal_{i}_" + new string('x', 220)).ToList();
        quality.ChunkQualitySignals = quality.Signals;
        quality.SignalsMetadata = new("complete", 0, false, false);
        quality.ChunkQualitySignalsMetadata = new("complete", 0, false, false);
        var bundle = Bundle(quality, 1);
        var llm = new ControlledCounter();
        var runner = new SourceBackedAgentV2Runner(llm, new NoTools(), new(4, 4, 8, 360, 900, MaximumContextTokens: 4096));
        var prepare = typeof(SourceBackedAgentV2Runner).GetMethod("PrepareSemanticReviewRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)prepare.Invoke(runner, [Intake, "Use the grounded evidence.", new WriterDraft("The recorded measurement is one unit [E1].", ["E1"]), bundle, new[] { "E1" }, CancellationToken.None])!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.True(llm.CountCalls >= 1);
        Assert.Equal(0, llm.Completions);
        Assert.Contains("signal_64_", llm.LastPrompt);
    }

    private sealed class NoTools : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName, JsonElement arguments, CancellationToken ct)
            => throw new InvalidOperationException("No retrieval belongs to this controlled prompt test.");
    }

    private sealed class ControlledCounter : ISourceBackedAgentLlmClient, ISourceBackedAgentInputTokenCounter
    {
        public int CountCalls { get; private set; }
        public int Completions { get; private set; }
        public string? LastPrompt { get; private set; }
        public Task<int?> CountInputTokensAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, CancellationToken ct, bool requireToolCall = false)
        {
            CountCalls++;
            LastPrompt = string.Join('\n', messages.Select(message => message.Content));
            // A deterministic injected count exercises the real context gate.
            // It does not claim a native tokenizer was executed by this unit test.
            return Task.FromResult<int?>(LlmCostBudgetGuard.EstimateInputTokens(messages, tools));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            Completions++;
            throw new InvalidOperationException("Oversized request must not reach completion.");
        }
    }
}
