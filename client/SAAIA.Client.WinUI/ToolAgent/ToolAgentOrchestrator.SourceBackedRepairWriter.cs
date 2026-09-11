using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string> TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults writerToolResults,
        CancellationToken ct,
        string? repairFeedback = null,
        string? precomputedCandidateAdjudicationJson = null)
    {
        var swRepair = Stopwatch.StartNew();
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        if (!ShouldAllowSourceBackedWriterRepairForCurrentTurn(writerUserMessage))
        {
            EmitRagTrace(
                "writer.repair.skipped",
                ("reason", "not_allowed_for_turn"),
                ("intent", plan.Intent),
                ("user_chars", writerUserMessage.Length),
                ("ms", swRepair.ElapsedMilliseconds));
            return string.Empty;
        }

        var rawRepairToolResults = writerToolResults;
        var writerPromptBudget = ResolveWriterPromptBudget();
        writerToolResults = BuildWriterToolResultsForRuntime(plan, writerToolResults, writerUserMessage, writerPromptBudget);
        var language = NormalizeLanguageCode(plan.Language);
        var hasRagEvidence = writerToolResults.Items.Any(item => item.ToolName is "rag.search" or "rag.multi_search");
        var repairIntentQuery = ResolveSourceBackedFallbackIntentQuery(writerUserMessage);
        var requiresStructuredPlanningCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage)
            || ShouldGateStructuredSourceBackedPlanningCoverage(repairIntentQuery);
        var broadSourceBackedRequest =
            ShouldAvoidRawSourceBackedFallback(writerUserMessage)
            || LooksLikeBroadSynthesisRequestShape(writerUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(writerUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(writerUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(writerUserMessage)
            || LooksLikeGenericCollectionOrListRequest(writerUserMessage);
        var allowStructuredRepairFromVisibleInventory = ShouldAllowStructuredPlanningWriterRepairFromVisibleSourceInventory(
                writerToolResults,
                writerUserMessage)
            || ShouldAllowStructuredPlanningWriterRepairFromVisibleSourceInventory(
                rawRepairToolResults,
                writerUserMessage);
        var allowStructuredRepairFromPartialGate = requiresStructuredPlanningCoverage
            && (ShouldAllowWriterForPartialSourceBackedPlanning(writerToolResults, writerUserMessage, language)
                || ShouldAllowWriterForPartialSourceBackedPlanning(rawRepairToolResults, writerUserMessage, language));
        var writerAllowed = requiresStructuredPlanningCoverage
            ? allowStructuredRepairFromVisibleInventory || allowStructuredRepairFromPartialGate
            : ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, writerUserMessage)
              || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, writerUserMessage)
              || ShouldUseWriterForDocumentaryProbeAnswer(writerToolResults, writerUserMessage)
              || (broadSourceBackedRequest && hasRagEvidence);
        if (!writerAllowed || !hasRagEvidence)
        {
            EmitRagTrace(
                "writer.repair.skipped",
                ("reason", !hasRagEvidence ? "no_rag_evidence" : requiresStructuredPlanningCoverage ? "insufficient_structured_planning_coverage" : "writer_not_allowed"),
                ("intent", plan.Intent),
                ("has_rag_evidence", hasRagEvidence),
                ("writer_allowed", writerAllowed),
                ("structured_planning_coverage_required", requiresStructuredPlanningCoverage),
                ("visible_source_inventory", allowStructuredRepairFromVisibleInventory),
                ("partial_planning_gate", allowStructuredRepairFromPartialGate),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", swRepair.ElapsedMilliseconds));
            return string.Empty;
        }

        var repairEvidenceSelection = ResolveStructuredPlanningWriterGuardToolResults(
            rawRepairToolResults,
            writerToolResults,
            writerUserMessage,
            language);
        var repairEvidenceToolResults = repairEvidenceSelection.ToolResults;
        EmitRagTrace(
            "writer.repair.evidence_inventory.selection",
            ("trace_path", "rag.writer.repair.evidence_inventory"),
            ("trace_step", "select_tool_results"),
            ("basis", repairEvidenceSelection.Basis),
            ("tool_items", repairEvidenceToolResults.Items.Count));

        string? candidateAdjudicationJson;
        var skipCandidateAdjudicationForRepairRetry = !string.IsNullOrWhiteSpace(repairFeedback);
        if (!string.IsNullOrWhiteSpace(precomputedCandidateAdjudicationJson))
        {
            candidateAdjudicationJson = precomputedCandidateAdjudicationJson;
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "repair_reuses_precomputed_adjudication"),
                ("intent", plan.Intent),
                ("tool_items", writerToolResults.Items.Count),
                ("json_chars", candidateAdjudicationJson.Length),
                ("ms", swRepair.ElapsedMilliseconds));
        }
        else if (skipCandidateAdjudicationForRepairRetry)
        {
            candidateAdjudicationJson = null;
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "repair_retry_feedback"),
                ("intent", plan.Intent),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", swRepair.ElapsedMilliseconds));
        }
        else if (requiresStructuredPlanningCoverage)
        {
            candidateAdjudicationJson = null;
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "structured_repair_uses_existing_evidence_inventory"),
                ("intent", plan.Intent),
                ("tool_items", writerToolResults.Items.Count),
                ("evidence_tool_items", repairEvidenceToolResults.Items.Count),
                ("ms", swRepair.ElapsedMilliseconds));
        }
        else
        {
            candidateAdjudicationJson = await TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
                repairEvidenceToolResults,
                writerUserMessage,
                language,
                writerPromptBudget,
                ct).ConfigureAwait(false);
            EmitRagTrace(
                "writer.repair.candidate_adjudication.retrieval_expansion.skipped",
                ("reason", "repair_stage_uses_existing_evidence_bundle"),
                ("tool_items", repairEvidenceToolResults.Items.Count));
        }
        var system = BuildCompactSourceBackedWriterSystemPrompt(
            language,
            plan.Mode,
            LocalizedStrings.NormalizeStyle(_mem.LastStyle),
            repairMode: true);

        var repairEvidenceInventoryChars = ResolveRepairEvidenceInventoryPromptChars(
            writerPromptBudget,
            requiresStructuredPlanningCoverage);
        var repairReferenceIndexChars = ResolveRepairSourceReferenceIndexPromptChars(
            repairEvidenceInventoryChars,
            requiresStructuredPlanningCoverage);
        EmitRagTrace(
            "writer.repair.prompt_budget",
            ("trace_path", "rag.writer.repair.prompt_budget"),
            ("trace_step", "resolved"),
            ("context_tokens", writerPromptBudget.ContextTokens),
            ("tool_results_chars", writerPromptBudget.ToolResultsChars),
            ("evidence_inventory_chars", repairEvidenceInventoryChars),
            ("source_reference_index_chars", repairReferenceIndexChars),
            ("structured_planning", requiresStructuredPlanningCoverage),
            ("candidate_adjudication_chars", candidateAdjudicationJson?.Length ?? 0),
            ("repair_feedback_chars", repairFeedback?.Length ?? 0));

        var user = BuildSourceBackedRepairWriterUserPrompt(
            chatHistory,
            userMessage,
            plan,
            rawRepairToolResults,
            writerToolResults,
            _mem.LastSourcesUsed,
            writerPromptBudget,
            candidateAdjudicationJson,
            repairFeedback);

        var messages = new[]
        {
            ("system", system),
            ("user", user)
        };

        string repair;
        try
        {
            EmitRagTrace(
                "writer.repair.start",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("tool_items", writerToolResults.Items.Count));
            repair = await StreamOrCompleteWithRetryAsync(messages, onDelta: null, ct).ConfigureAwait(false);
            EmitRagTrace(
                "writer.repair.end",
                ("intent", plan.Intent),
                ("answer_chars", repair?.Length ?? 0),
                ("ms", swRepair.ElapsedMilliseconds));
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex))
        {
            EmitRagTrace(
                "writer.repair.overflow",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("error", ex.Message),
                ("ms", swRepair.ElapsedMilliseconds));
            repair = string.Empty;
            var overflowSourceToolResults = rawRepairToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search")
                ? rawRepairToolResults
                : writerToolResults;

            if (overflowSourceToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            {
                var retryUser = BuildOverflowRetryWriterUserPrompt(
                    chatHistory,
                    writerUserMessage,
                    plan,
                    overflowSourceToolResults,
                    BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language),
                    ResolveOverflowRetryEvidenceInventoryPromptChars(
                        writerPromptBudget,
                        requiresStructuredPlanningCoverage),
                    repairFeedback);
                var retryMessages = new[]
                {
                    ("system", BuildCompactSourceBackedWriterSystemPrompt(
                        language,
                        plan.Mode,
                        LocalizedStrings.NormalizeStyle(_mem.LastStyle),
                        repairMode: true,
                        compactRetry: true)),
                    ("user", retryUser)
                };

                try
                {
                    var retryPromptChars = retryMessages[0].Item2.Length + retryUser.Length;
                    EmitRagTrace(
                        "writer.repair.retry.start",
                        ("intent", plan.Intent),
                        ("prompt_chars", retryPromptChars),
                        ("tool_items", overflowSourceToolResults.Items.Count));
                    repair = await StreamOrCompleteWithRetryAsync(retryMessages, onDelta: null, ct).ConfigureAwait(false);
                    _lastAnswerSource = $"repair_writer_context_overflow_compact_retry:{plan.Intent}";
                    EmitRagTrace(
                        "writer.repair.retry.end",
                        ("intent", plan.Intent),
                        ("answer_chars", repair?.Length ?? 0),
                        ("ms", swRepair.ElapsedMilliseconds));
                }
                catch (Exception retryEx) when (IsLlmContextOverflowException(retryEx))
                {
                    EmitRagTrace(
                        "writer.repair.retry.overflow",
                        ("intent", plan.Intent),
                        ("error", retryEx.Message),
                        ("ms", swRepair.ElapsedMilliseconds));
                    return string.Empty;
                }
            }
        }

        repair = (repair ?? string.Empty).Trim();
        repair = RemoveTrailingModelEmittedSourceList(repair);
        repair = ReplaceWriterEvidenceIdReferencesWithCitations(
            repair,
            rawRepairToolResults,
            writerUserMessage,
            plan.Language);
        if (requiresStructuredPlanningCoverage
            && string.IsNullOrWhiteSpace(repairFeedback)
            && LooksLikeCitationOnlyStructuredPlanningAnswer(repair, writerUserMessage))
        {
            var citationOnlyFeedback = BuildStructuredPlanningRepairFeedback(
                repair,
                rawRepairToolResults,
                writerUserMessage,
                plan.Language);
            EmitRagTrace(
                "writer.repair.citation_only_retry.start",
                ("intent", plan.Intent),
                ("answer_chars", repair.Length),
                ("feedback_chars", citationOnlyFeedback.Length),
                ("ms", swRepair.ElapsedMilliseconds));
            var citationOnlyRetry = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    writerUserMessage,
                    plan,
                    rawRepairToolResults,
                    ct,
                    citationOnlyFeedback)
                .ConfigureAwait(false);
            citationOnlyRetry = RemoveTrailingModelEmittedSourceList(citationOnlyRetry ?? string.Empty).Trim();
            var retryStillCitationOnly = LooksLikeCitationOnlyStructuredPlanningAnswer(citationOnlyRetry, writerUserMessage);
            EmitRagTrace(
                "writer.repair.citation_only_retry.end",
                ("intent", plan.Intent),
                ("answer_chars", citationOnlyRetry.Length),
                ("still_citation_only", retryStillCitationOnly),
                ("ms", swRepair.ElapsedMilliseconds));
            if (!string.IsNullOrWhiteSpace(citationOnlyRetry) && !retryStillCitationOnly)
                repair = citationOnlyRetry;
        }
        if (requiresStructuredPlanningCoverage
            && LooksLikeConcreteStructuredPlanningAnswer(repair)
            && !TryGetVisibleSourceCitedStructuredPlanningSources(
                repair,
                rawRepairToolResults,
                writerUserMessage,
                out _,
                plan.Language))
        {
            var citedRepair = await TryAddStructuredPlanningEvidenceIdsWithFocusedWriterAsync(
                repair,
                rawRepairToolResults,
                writerUserMessage,
                plan.Language,
                ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(citedRepair))
                repair = citedRepair;
        }

        var repairLooksLikeNoRagData = ShouldFallbackFromNoRagDataAnswer(repair);
        var repairLooksLikeControlLeak = LooksLikeWriterControlLeak(repair);
        var repairLooksLikePoorPlanning = LooksLikePoorPlanningFallbackAnswer(repair, writerUserMessage);
        var keepStructuredRepairForSourceGuard = requiresStructuredPlanningCoverage
            && !repairLooksLikeNoRagData
            && !repairLooksLikeControlLeak
            && LooksLikeConcreteStructuredPlanningAnswer(repair);

        if (repairLooksLikeNoRagData
            || repairLooksLikeControlLeak
            || (repairLooksLikePoorPlanning && !keepStructuredRepairForSourceGuard))
        {
            EmitRagTrace(
                "writer.repair.rejected",
                ("intent", plan.Intent),
                ("answer_chars", repair.Length),
                ("control_leak", repairLooksLikeControlLeak),
                ("poor_planning", repairLooksLikePoorPlanning),
                ("no_rag_data", repairLooksLikeNoRagData),
                ("ms", swRepair.ElapsedMilliseconds));
            return BuildSourceBackedSafeFallbackAfterRejectedWriter(
                rawRepairToolResults,
                userMessage,
                writerUserMessage,
                language);
        }

        EmitRagTrace(
            "writer.repair.accepted",
            ("intent", plan.Intent),
            ("answer_chars", repair.Length),
            ("poor_planning_advisory", repairLooksLikePoorPlanning),
            ("kept_for_source_guard", keepStructuredRepairForSourceGuard),
            ("ms", swRepair.ElapsedMilliseconds));
        return repair;
    }
}