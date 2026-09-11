using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record AnswerPostWriterStructuredGateResult(
        string FinalAnswer,
        List<ToolMemory.SourceRef>? Sources,
        bool StructuredSourceBackedPlanningFinalResolved);

    private async Task<AnswerPostWriterStructuredGateResult> RunAnswerPostWriterStructuredGateAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string finalPlanningCoverageQuery,
        string finalAnswer,
        RouterPlan plan,
        ToolResults sourceToolResults,
        List<ToolMemory.SourceRef>? sources,
        string? candidateAdjudicationJson,
        CancellationToken ct)
    {
        var structuredSourceBackedPlanningFinalResolved = false;
        if (ShouldGateStructuredSourceBackedPlanningCoverage(finalPlanningCoverageQuery))
        {
            var finalStructuredGateSw = Stopwatch.StartNew();
            var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(finalPlanningCoverageQuery);
            EmitRagTrace(
                "writer.final_structured_gate.start",
                ("target_items", targetItemCount),
                ("answer_chars", finalAnswer?.Length ?? 0),
                ("tool_items", sourceToolResults.Items.Count),
                ("position", "pre_general_post_guards"));
            var writerPlanningSupport = PlanningAnswerSupportAnalysis.Empty;
            if (TryGetVisibleSourceCitedStructuredPlanningSources(
                finalAnswer,
                sourceToolResults,
                finalPlanningCoverageQuery,
                out var visibleCitedFinalSources,
                plan.Language))
            {
                sources = visibleCitedFinalSources;
                _lastAnswerSource = $"structured_planning_visible_cited_writer:{plan.Intent}";
                EmitRagTrace(
                    "writer.final_structured_gate.visible_citations.accepted",
                    ("target_items", targetItemCount),
                    ("sources", visibleCitedFinalSources.Count),
                    ("position", "pre_general_post_guards"));
            }
            else
            {
                var writerFinalSupportSw = Stopwatch.StartNew();
                EmitRagTrace(
                    "writer.final_structured_gate.writer_support.start",
                    ("answer_chars", finalAnswer?.Length ?? 0),
                    ("position", "pre_general_post_guards"));
                writerPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    finalAnswer,
                    sourceToolResults,
                    finalPlanningCoverageQuery,
                    plan.Language);
                writerFinalSupportSw.Stop();
                EmitRagTrace(
                    "writer.final_structured_gate.writer_support.end",
                    ("items", writerPlanningSupport.ItemCount),
                    ("supported", writerPlanningSupport.SupportedItemCount),
                    ("unsupported", writerPlanningSupport.UnsupportedItemCount),
                    ("candidates", writerPlanningSupport.CandidateCount),
                    ("sources", writerPlanningSupport.Sources.Count),
                    ("accepted", writerPlanningSupport.Sources.Count > 0 && !ShouldRejectUnsupportedPlanningAnswerForFinal(writerPlanningSupport, finalPlanningCoverageQuery)),
                    ("ms", writerFinalSupportSw.ElapsedMilliseconds));
                if (writerPlanningSupport.Sources.Count > 0
                    && !ShouldRejectUnsupportedPlanningAnswerForFinal(writerPlanningSupport, finalPlanningCoverageQuery))
                {
                    sources = writerPlanningSupport.Sources.ToList();
                    _lastAnswerSource = $"structured_planning_supported_writer:{plan.Intent}";
                }
                else
                {
                    var repairedFinalGate = false;
                    var preferWriterRepairOverDeterministicRebuild = ShouldGateStructuredSourceBackedPlanningCoverage(finalPlanningCoverageQuery);
                    if (preferWriterRepairOverDeterministicRebuild
                        && ShouldAllowSourceBackedWriterRepairForCurrentTurn(finalPlanningCoverageQuery))
                    {
                        var finalGateRepairSw = Stopwatch.StartNew();
                        EmitRagTrace(
                            "writer.final_structured_gate.repair.start",
                            ("target_items", targetItemCount),
                            ("position", "pre_general_post_guards"));
                        var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                                chatHistory,
                                finalPlanningCoverageQuery,
                                plan,
                                sourceToolResults,
                                ct,
                                precomputedCandidateAdjudicationJson: candidateAdjudicationJson)
                            .ConfigureAwait(false);
                        repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);
                        var repairSupportedByVisibleCitations = TryGetVisibleSourceCitedStructuredPlanningSources(
                            repairAnswer,
                            sourceToolResults,
                            finalPlanningCoverageQuery,
                            out var repairVisibleCitedSources,
                            plan.Language);
                        var repairPlanningSupport = PlanningAnswerSupportAnalysis.Empty;
                        if (!repairSupportedByVisibleCitations)
                        {
                            repairPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                repairAnswer,
                                sourceToolResults,
                                finalPlanningCoverageQuery,
                                plan.Language);
                        }
                        finalGateRepairSw.Stop();
                        var repairSupportedByCandidateSupport = repairPlanningSupport.Sources.Count > 0
                                                                && !ShouldRejectUnsupportedPlanningAnswerForFinal(repairPlanningSupport, finalPlanningCoverageQuery);
                        var repairSupported = repairSupportedByVisibleCitations || repairSupportedByCandidateSupport;
                        EmitRagTrace(
                            "writer.final_structured_gate.repair.end",
                            ("supported", repairSupported),
                            ("supported_by_visible_citations", repairSupportedByVisibleCitations),
                            ("answer_chars", repairAnswer?.Length ?? 0),
                            ("sources", repairSupportedByVisibleCitations ? repairVisibleCitedSources.Count : repairPlanningSupport.Sources.Count),
                            ("items", repairPlanningSupport.ItemCount),
                            ("supported_items", repairPlanningSupport.SupportedItemCount),
                            ("unsupported_items", repairPlanningSupport.UnsupportedItemCount),
                            ("candidates", repairPlanningSupport.CandidateCount),
                            ("ms", finalGateRepairSw.ElapsedMilliseconds));
                        if (!repairSupported
                            && ShouldRetryStructuredPlanningRepairAfterSourceGuardFailure(repairAnswer, finalPlanningCoverageQuery))
                        {
                            var retryFeedback = BuildStructuredPlanningRepairFeedback(
                                repairAnswer,
                                sourceToolResults,
                                finalPlanningCoverageQuery,
                                plan.Language);
                            var retrySw = Stopwatch.StartNew();
                            EmitRagTrace(
                                "writer.final_structured_gate.repair.retry.start",
                                ("answer_chars", repairAnswer?.Length ?? 0),
                                ("feedback_chars", retryFeedback.Length));
                            var retryAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                                    chatHistory,
                                    finalPlanningCoverageQuery,
                                    plan,
                                    sourceToolResults,
                                    ct,
                                    retryFeedback)
                                .ConfigureAwait(false);
                            retryAnswer = RemoveTrailingModelEmittedSourceList(retryAnswer ?? string.Empty);
                            var retrySupportedByVisibleCitations = TryGetVisibleSourceCitedStructuredPlanningSources(
                                retryAnswer,
                                sourceToolResults,
                                finalPlanningCoverageQuery,
                                out var retryVisibleCitedSources,
                                plan.Language);
                            var retryPlanningSupport = PlanningAnswerSupportAnalysis.Empty;
                            if (!retrySupportedByVisibleCitations)
                            {
                                retryPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                    retryAnswer,
                                    sourceToolResults,
                                    finalPlanningCoverageQuery,
                                    plan.Language);
                            }
                            var retrySupportedByCandidateSupport = retryPlanningSupport.Sources.Count > 0
                                                                   && !ShouldRejectUnsupportedPlanningAnswerForFinal(retryPlanningSupport, finalPlanningCoverageQuery);
                            var retrySupported = retrySupportedByVisibleCitations || retrySupportedByCandidateSupport;
                            retrySw.Stop();
                            EmitRagTrace(
                                "writer.final_structured_gate.repair.retry.end",
                                ("supported", retrySupported),
                                ("supported_by_visible_citations", retrySupportedByVisibleCitations),
                                ("answer_chars", retryAnswer?.Length ?? 0),
                                ("sources", retrySupportedByVisibleCitations ? retryVisibleCitedSources.Count : retryPlanningSupport.Sources.Count),
                                ("items", retryPlanningSupport.ItemCount),
                                ("supported_items", retryPlanningSupport.SupportedItemCount),
                                ("unsupported_items", retryPlanningSupport.UnsupportedItemCount),
                                ("candidates", retryPlanningSupport.CandidateCount),
                                ("ms", retrySw.ElapsedMilliseconds));
                            if (retrySupported)
                            {
                                repairAnswer = retryAnswer ?? string.Empty;
                                repairPlanningSupport = retryPlanningSupport;
                                repairSupportedByVisibleCitations = retrySupportedByVisibleCitations;
                                repairSupportedByCandidateSupport = retrySupportedByCandidateSupport;
                                repairSupported = true;
                                repairVisibleCitedSources = retryVisibleCitedSources;
                            }
                        }

                        if (repairSupported)
                        {
                            finalAnswer = repairAnswer ?? string.Empty;
                            sources = repairSupportedByVisibleCitations
                                ? repairVisibleCitedSources
                                : repairPlanningSupport.Sources.ToList();
                            _lastAnswerSource = repairSupportedByVisibleCitations
                                ? $"structured_planning_visible_cited_writer_repair:{plan.Intent}"
                                : $"structured_planning_supported_writer_repair:{plan.Intent}";
                            repairedFinalGate = true;
                        }
                    }

                    if (!repairedFinalGate && preferWriterRepairOverDeterministicRebuild)
                    {
                        EmitRagTrace(
                            "writer.final_structured_gate.deterministic.skipped",
                            ("reason", "strict_planning_requires_writer_decision"),
                            ("candidates", writerPlanningSupport.CandidateCount),
                            ("position", "pre_general_post_guards"));
                        ClientLog.Info(
                            "ToolAgent structured planning final gate skipped deterministic rebuild after writer repair path: " +
                            $"target={targetItemCount} writerItems={writerPlanningSupport.ItemCount} " +
                            $"writerSupported={writerPlanningSupport.SupportedItemCount} " +
                            $"unsupported={writerPlanningSupport.UnsupportedItemCount} " +
                            $"candidates={writerPlanningSupport.CandidateCount} " +
                            $"source={_lastAnswerSource}");
                        LogSourceBackedPlanningTrace(
                            "final-gate-skipped-deterministic-rebuild-after-writer-repair",
                            sourceToolResults,
                            finalPlanningCoverageQuery,
                            plan.Language);
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningCoverageQuery,
                            finalPlanningCoverageQuery,
                            writerPlanningSupport.CandidateCount,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"structured_planning_insufficient_after_writer_repair:{plan.Intent}";
                        repairedFinalGate = true;
                    }

                    if (!repairedFinalGate)
                    {
                        EmitRagTrace(
                            "writer.final_structured_gate.deterministic.skipped",
                            ("reason", "canonical_structured_planning_no_deterministic_rebuild"),
                            ("candidates", writerPlanningSupport.CandidateCount),
                            ("position", "pre_general_post_guards"));
                        ClientLog.Info(
                                "ToolAgent structured planning final gate skipped deterministic rebuild on the canonical path: " +
                            $"target={targetItemCount} writerItems={writerPlanningSupport.ItemCount} " +
                            $"writerSupported={writerPlanningSupport.SupportedItemCount} " +
                            $"unsupported={writerPlanningSupport.UnsupportedItemCount} " +
                            $"candidates={writerPlanningSupport.CandidateCount} " +
                            $"source={_lastAnswerSource}");
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningCoverageQuery,
                            finalPlanningCoverageQuery,
                            writerPlanningSupport.CandidateCount,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"structured_planning_insufficient_without_deterministic_rebuild:{plan.Intent}";
                    }
                }
            }

            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer ?? string.Empty);
            structuredSourceBackedPlanningFinalResolved = true;
            EmitRagTrace(
                "writer.final_structured_gate.end",
                ("answer_source", _lastAnswerSource),
                ("answer_chars", finalAnswer?.Length ?? 0),
                ("sources", sources?.Count ?? 0),
                ("position", "pre_general_post_guards"),
                ("ms", finalStructuredGateSw.ElapsedMilliseconds));
        }


        return new AnswerPostWriterStructuredGateResult(
            finalAnswer ?? string.Empty,
            sources,
            structuredSourceBackedPlanningFinalResolved);
    }
}
