using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record AnswerPostWriterRagResult(
        string FinalAnswer,
        List<ToolMemory.SourceRef>? Sources,
        bool StructuredSourceBackedPlanningFinalResolved);

    private async Task<AnswerPostWriterRagResult> RunAnswerPostWriterRagSourceGuardsAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        ToolResults writerToolResults,
        ToolResults sourceToolResults,
        string finalPlanningCoverageQuery,
        string finalAnswer,
        List<ToolMemory.SourceRef>? sources,
        bool usedRagSearch,
        bool usedSourcesResolve,
        bool usedSummarySearch,
        bool shouldUseSourceBackedOptionAnswer,
        string? candidateAdjudicationJson,
        CancellationToken ct)
    {
        var structuredSourceBackedPlanningFinalResolved = false;
        if (usedRagSearch && LooksLikeDegenerateLlmOutput(finalAnswer) && !LooksLikeWeeklyPlanningRequest(userMessage))
        {
            EmitRagTrace(
                "writer_guard.degenerate_output_deterministic.skipped",
                ("reason", "canonical_degenerate_output_requires_llm_repair_or_insufficient"),
                ("query", userMessage));
            finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                plan.Language,
                userMessage,
                userMessage,
                sources?.Count ?? 0,
                searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
            sources = new List<ToolMemory.SourceRef>();
            _lastAnswerSource = $"writer_guard_degenerate_output_insufficient_without_deterministic_fallback:{plan.Intent}";
        }

        if (usedRagSearch)
        {
            var writerPostSourcesSw = Stopwatch.StartNew();
            EmitRagTrace(
                "writer.post.sources.start",
                ("intent", plan.Intent),
                ("tool_items", sourceToolResults.Items.Count));
            if (LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
                sources = DeriveSourcesFromCountdownPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
                sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeSourceBackedPairingRecommendationRequest(userMessage))
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (shouldUseSourceBackedOptionAnswer)
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (ShouldUseSourceBackedExtractiveAnswer(userMessage, toolResults)
                || LooksLikeSourceBackedActionRequest(userMessage)
                || LooksLikeComparativeDocumentaryRequest(userMessage))
                sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
            else
                sources = DeriveSourcesFromRagHits(sourceToolResults);

            if (sources.Count == 0)
                sources = DeriveSourcesFromRagHits(sourceToolResults);
            writerPostSourcesSw.Stop();
            EmitRagTrace(
                "writer.post.sources.end",
                ("sources", sources.Count),
                ("elapsed_ms", writerPostSourcesSw.ElapsedMilliseconds));

            var structuredGateResult = await RunAnswerPostWriterStructuredGateAsync(
                    chatHistory,
                    finalPlanningCoverageQuery,
                    finalAnswer,
                    plan,
                    sourceToolResults,
                    sources,
                    candidateAdjudicationJson,
                    ct)
                .ConfigureAwait(false);
            finalAnswer = structuredGateResult.FinalAnswer;
            sources = structuredGateResult.Sources;
            structuredSourceBackedPlanningFinalResolved = structuredGateResult.StructuredSourceBackedPlanningFinalResolved;

            if (!structuredSourceBackedPlanningFinalResolved && LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
            {
                EmitRagTrace(
                    "writer_guard.countdown_deterministic.skipped",
                    ("reason", "canonical_countdown_guard_no_deterministic_fallback"),
                    ("query", userMessage));
            }

            if (!structuredSourceBackedPlanningFinalResolved && ShouldFallbackFromNoRagDataAnswer(finalAnswer))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    && (ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
                        || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage))
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false)
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(repairAnswer))
                {
                    EmitRagTrace(
                        "writer_guard.no_rag_data_deterministic.skipped",
                        ("reason", "canonical_no_rag_data_requires_llm_repair"),
                        ("query", userMessage));
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        userMessage,
                        userMessage,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"writer_guard_no_rag_data_insufficient_without_deterministic_fallback:{plan.Intent}";
                }

                if (!string.IsNullOrWhiteSpace(repairAnswer))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved)
            {
                EmitRagTrace(
                    "writer_guard.anchor_deterministic_checks.skipped",
                    ("reason", "canonical_anchor_checks_are_mechanical_only"),
                    ("query", userMessage));
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && (ShouldPreferPartialEvidenceFallbackOverOptions(sourceToolResults, userMessage)
                    || LooksLikeUnsupportedBroadOptionComposition(userMessage, finalAnswer))
                && ShouldReplaceOverPromotedSourceBackedOptionAnswer(finalAnswer, sourceToolResults, userMessage))
            {
                EmitRagTrace(
                    "writer_guard.overpromoted_options_deterministic.skipped",
                    ("reason", "canonical_overpromoted_options_no_deterministic_fallback"),
                    ("query", userMessage));
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    userMessage,
                    sources?.Count ?? 0,
                    searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                sources = new List<ToolMemory.SourceRef>();
                _lastAnswerSource = $"writer_guard_overpromoted_options_insufficient_without_deterministic_fallback:{plan.Intent}";
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && LooksLikeUnderusedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage)
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    userMessage,
                    plan,
                    writerToolResults,
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage)
                    && !LooksLikeUnderusedSourceBackedPlanningAnswer(repairAnswer, sourceToolResults, userMessage))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_underused_planning_sources_repaired:{plan.Intent}";
                }
                else
                {
                    EmitRagTrace(
                        "writer_guard.underused_planning_deterministic.skipped",
                        ("reason", "canonical_underused_planning_requires_llm_repair"),
                        ("query", userMessage));
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        userMessage,
                        userMessage,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"writer_guard_underused_planning_sources_insufficient_without_deterministic_rebuild:{plan.Intent}";
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved && LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_poor_planning_repaired:{plan.Intent}";
                }
                else
                {
                    EmitRagTrace(
                        "writer_guard.poor_planning_deterministic.skipped",
                        ("reason", "canonical_poor_planning_requires_llm_repair"),
                        ("query", userMessage));
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        userMessage,
                        userMessage,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"writer_guard_poor_planning_insufficient_without_deterministic_fallback:{plan.Intent}";
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && sources is { Count: > 0 }
                && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
            {
                sources.Clear();
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && usedRagSearch
                && LooksLikeUnsupportedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage, plan.Language))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                        chatHistory,
                        userMessage,
                        plan,
                        writerToolResults,
                        ct).ConfigureAwait(false)
                    : string.Empty;
                repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage)
                    && !LooksLikeUnsupportedSourceBackedPlanningAnswer(repairAnswer, sourceToolResults, userMessage, plan.Language))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_unsupported_planning_items_repaired:{plan.Intent}";
                }
                else
                {
                    EmitRagTrace(
                        "writer_guard.unsupported_planning_deterministic.skipped",
                        ("reason", "canonical_unsupported_planning_requires_llm_repair"),
                        ("query", userMessage));
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        userMessage,
                        userMessage,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"writer_guard_unsupported_planning_items_insufficient_without_deterministic_rebuild:{plan.Intent}";
                }
            }
        }
        else if (usedSourcesResolve)
        {
            var resolved = TryBuildSourceFromResolveResult(toolResults);
            if (resolved is not null)
                sources = new List<ToolMemory.SourceRef> { resolved };
        }
        else if (usedSummarySearch)
        {
            sources = DeriveSourcesFromSummarySearch(toolResults);
        }


        return new AnswerPostWriterRagResult(
            finalAnswer ?? string.Empty,
            sources,
            structuredSourceBackedPlanningFinalResolved);
    }
}
