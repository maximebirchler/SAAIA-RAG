using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record AnswerPostCriticSourceAlignmentResult(
        string FinalAnswer,
        List<ToolMemory.SourceRef>? Sources);

    private async Task<AnswerPostCriticSourceAlignmentResult> RunAnswerPostCriticSourceAlignmentAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        ToolResults writerToolResults,
        ToolResults sourceToolResults,
        string finalPlanningCoverageQuery,
        string finalAnswer,
        List<ToolMemory.SourceRef>? sources,
        bool structuredSourceBackedPlanningFinalResolved,
        bool usedRagSearch,
        bool useGeneralChatPrompt,
        bool shouldAvoidRawSourceBackedFallback,
        CancellationToken ct,
        Action<string>? onProgress)
    {
        if (!structuredSourceBackedPlanningFinalResolved
            && ShouldRunCriticPass(plan, toolResults, useGeneralChatPrompt, userMessage))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressCheckAlignmentWithSources(plan.Language));

            finalAnswer = await RunCriticPassAsync(chatHistory, userMessage, plan, writerToolResults, finalAnswer ?? string.Empty, ct).ConfigureAwait(false);
            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage)
            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
        {
            var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                chatHistory,
                userMessage,
                plan,
                writerToolResults,
                ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(repairAnswer)
                && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
            {
                finalAnswer = repairAnswer;
                sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                _lastAnswerSource = $"post_critic_guard_poor_planning_repaired:{plan.Intent}";
            }
            else
            {
                EmitRagTrace(
                    "post_critic_guard.poor_planning_deterministic.skipped",
                    ("reason", "canonical_post_critic_requires_llm_repair_or_insufficient"),
                    ("sources", sources?.Count ?? 0),
                    ("query", userMessage));
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    userMessage,
                    sources?.Count ?? 0,
                    searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                sources = new List<ToolMemory.SourceRef>();
                _lastAnswerSource = $"post_critic_guard_poor_planning_insufficient_without_deterministic_fallback:{plan.Intent}";
            }
        }

        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
        {
            sources.Clear();
        }
        else if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && LooksLikeUnsupportedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage, plan.Language))
        {
            EmitRagTrace(
                "post_critic_guard.unsupported_planning_deterministic.skipped",
                ("reason", "canonical_post_critic_unsupported_planning_requires_llm_repair"),
                ("sources", sources?.Count ?? 0),
                ("query", userMessage));
            finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                plan.Language,
                userMessage,
                userMessage,
                sources?.Count ?? 0,
                searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
            sources = new List<ToolMemory.SourceRef>();
            _lastAnswerSource = $"post_critic_guard_unsupported_planning_items_insufficient_without_deterministic_rebuild:{plan.Intent}";
        }
        else if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && ShouldFallbackFromNoRagDataAnswer(finalAnswer))
        {
            finalAnswer = BuildSourceBackedSafeFallbackAnswer(
                sourceToolResults,
                userMessage,
                plan.Language,
                shouldAvoidRawSourceBackedFallback);
            sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
        }

        finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer ?? string.Empty);
        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && LooksLikeWriterControlLeak(finalAnswer)
            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
        {
            var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                chatHistory,
                userMessage,
                plan,
                writerToolResults,
                ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(repairAnswer)
                && !LooksLikeWriterControlLeak(repairAnswer)
                && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
            {
                finalAnswer = repairAnswer;
                sources = LooksLikeAnyDocumentaryPlanningRequest(userMessage)
                    ? DeriveSourcesFromPlanningHits(sourceToolResults, userMessage)
                    : DeriveSourcesFromRagHits(sourceToolResults);
                _lastAnswerSource = $"post_writer_guard_control_leak_repaired:{plan.Intent}";
            }
            else
            {
                EmitRagTrace(
                    "post_writer_guard.control_leak_deterministic.skipped",
                    ("reason", "canonical_control_leak_requires_llm_repair_or_insufficient"),
                    ("query", userMessage));
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    userMessage,
                    sources?.Count ?? 0,
                    searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                sources = new List<ToolMemory.SourceRef>();
                _lastAnswerSource = $"post_writer_guard_control_leak_insufficient_without_deterministic_fallback:{plan.Intent}";
            }

            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        if (!structuredSourceBackedPlanningFinalResolved && usedRagSearch && sources is { Count: > 0 })
        {
            var finalPlanningSupportQuery = !string.IsNullOrWhiteSpace(finalPlanningCoverageQuery)
                ? finalPlanningCoverageQuery
                : userMessage;
            if (LooksLikeAnyDocumentaryPlanningRequest(finalPlanningSupportQuery))
            {
                var planningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    finalAnswer,
                    sourceToolResults,
                    finalPlanningSupportQuery,
                    plan.Language);
                if (ShouldRejectUnsupportedPlanningAnswerForFinal(planningSupport, finalPlanningSupportQuery))
                {
                    ClientLog.Info(
                        "ToolAgent planning answer rejected after final support check: " +
                        $"items={planningSupport.ItemCount} supported={planningSupport.SupportedItemCount} " +
                        $"unsupported={planningSupport.UnsupportedItemCount} candidates={planningSupport.CandidateCount} " +
                        $"source={_lastAnswerSource}");
                    LogSourceBackedPlanningTrace(
                        "final-support-check-rejected",
                        sourceToolResults,
                        finalPlanningSupportQuery,
                        plan.Language);
                    if (ShouldGateStructuredSourceBackedPlanningCoverage(finalPlanningSupportQuery))
                    {
                        EmitRagTrace(
                            "writer.final_support_check.deterministic_rebuild.skipped",
                            ("reason", "strict_planning_requires_writer_decision"),
                            ("candidates", planningSupport.CandidateCount));
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningSupportQuery,
                            finalPlanningSupportQuery,
                            planningSupport.CandidateCount,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources.Clear();
                        _lastAnswerSource = $"post_writer_guard_planning_item_source_mismatch:{plan.Intent}";
                    }
                    else
                    {
                        EmitRagTrace(
                            "writer.final_support_check.deterministic_rebuild.skipped",
                            ("reason", "canonical_source_alignment_requires_llm_repair_or_insufficient"),
                            ("candidates", planningSupport.CandidateCount));
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningSupportQuery,
                            finalPlanningSupportQuery,
                            planningSupport.CandidateCount,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources.Clear();
                        _lastAnswerSource = $"post_writer_guard_planning_item_source_mismatch_without_deterministic_rebuild:{plan.Intent}";
                    }
                }
                else if (planningSupport.Sources.Count > 0)
                {
                    sources = planningSupport.Sources.ToList();
                }
            }

            var finalSourceAlignmentQuery = !string.IsNullOrWhiteSpace(finalPlanningCoverageQuery)
                ? finalPlanningCoverageQuery
                : userMessage;
            var reconciledSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(finalAnswer, sources, finalSourceAlignmentQuery);
            if (reconciledSources.Count > 0)
            {
                sources = reconciledSources;
            }
            else if (ShouldRequireVisibleSourcesToBeCited(finalAnswer, finalSourceAlignmentQuery)
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(finalSourceAlignmentQuery))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    finalSourceAlignmentQuery,
                    plan,
                    writerToolResults,
                    ct).ConfigureAwait(false);
                repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);

                var repairSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(
                    repairAnswer,
                    DeriveSourcesForSourceBackedFallback(sourceToolResults, finalSourceAlignmentQuery),
                    finalSourceAlignmentQuery);

                if (!string.IsNullOrWhiteSpace(repairAnswer) && repairSources.Count > 0)
                {
                    finalAnswer = repairAnswer;
                    sources = repairSources;
                    _lastAnswerSource = $"post_writer_guard_source_alignment_repaired:{plan.Intent}";
                }
                else
                {
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        finalSourceAlignmentQuery,
                        finalSourceAlignmentQuery,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"post_writer_guard_source_alignment_insufficient:{plan.Intent}";
                }
            }
        }


        return new AnswerPostCriticSourceAlignmentResult(finalAnswer ?? string.Empty, sources);
    }
}
