using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private async Task<IReadOnlyList<SourceBackedEvidenceExplorationPass>> TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onProgress = null)
    {
        var alreadyTriedQueries = BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(toolResults, effectiveUserMessage, language);
        var categoryHints = BuildSourceBackedLlmCategoryHintsForPrompt(
            effectiveUserMessage,
            MaxSourceBackedLlmEvidencePlannerCategoryHints);
        var system = BuildSourceBackedLlmEvidenceExplorationSystemPrompt(language);
        var user = BuildSourceBackedLlmEvidenceExplorationUserPrompt(
            toolResults,
            currentAnalysis,
            effectiveUserMessage,
            language,
            alreadyTriedQueries,
            categoryHints);

        var sw = Stopwatch.StartNew();
        try
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressPlanRetrievalStrategy(language));
            EmitRagTrace(
                "evidence.llm_planner.start",
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("score", currentAnalysis.Score),
                ("usable_hits", currentAnalysis.UsableHitCount),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("already_tried_queries", alreadyTriedQueries.Count),
                ("system_chars", system.Length),
                ("user_chars", user.Length),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs));
            using var plannerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plannerTimeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    plannerTimeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, true));
            var categoryDecision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(raw);
            var parsedPasses = ParseSourceBackedLlmEvidenceExplorationPasses(raw, alreadyTriedQueries);
            var passes = FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
                parsedPasses,
                effectiveUserMessage,
                language,
                categoryDecision.CategoryScope,
                out var diagnosticPassLabels,
                out var diagnosticQueries);
            var filteredPassCount = passes.Count;
            passes = ApplyResolvedSourceBackedLlmCategoryScopeDecision(
                passes,
                categoryDecision,
                "planner_output",
                out var addedScopeOnlyPass);
            if (ShouldRunLlmSourceBackedCategoryScopeAdjudication(
                    passes,
                    currentAnalysis,
                    effectiveUserMessage,
                    language,
                    categoryHints))
            {
                var adjudicatedCategoryDecision = await TryAdjudicateSourceBackedLlmCategoryScopeAsync(
                        toolResults,
                        currentAnalysis,
                        passes,
                        effectiveUserMessage,
                        language,
                        categoryHints,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
                passes = ApplyResolvedSourceBackedLlmCategoryScopeDecision(
                    passes,
                    adjudicatedCategoryDecision,
                    "adjudication",
                    out var addedAdjudicatedScopeOnlyPass);
                addedScopeOnlyPass |= addedAdjudicatedScopeOnlyPass;
            }
            if (diagnosticPassLabels.Length > 0)
            {
                EmitRagTrace(
                    "evidence.llm_planner.quality_diagnostic",
                    ("reason", "structured_axis_queries_flagged_but_preserved"),
                    ("parsed_passes", parsedPasses.Count),
                    ("kept_passes", filteredPassCount),
                    ("passes_after_scope", passes.Count),
                    ("scope_only_pass_added", addedScopeOnlyPass),
                    ("diagnostic_passes", diagnosticPassLabels.Length),
                    ("diagnostic_labels", diagnosticPassLabels),
                    ("diagnostic_query_samples", diagnosticQueries.Take(8).ToArray()));
            }
            EmitRagTrace(
                "evidence.llm_planner.end",
                ("accepted", passes.Count > 0),
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("raw_chars", raw?.Length ?? 0),
                ("parsed_passes", parsedPasses.Count),
                ("passes", passes.Count),
                ("labels", passes.Select(static pass => pass.Label).ToArray()),
                ("query_count", passes.Sum(static pass => pass.Queries.Length)),
                ("category_scopes", passes
                    .Select(static pass => pass.CategoryScope)
                    .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
            return passes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_planner.timeout",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_planner.cancelled",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("parent_cancelled", ct.IsCancellationRequested));
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_planner.error",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
    }

    private IReadOnlyList<SourceBackedEvidenceExplorationPass> ApplyResolvedSourceBackedLlmCategoryScopeDecision(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        SourceBackedLlmCategoryScopeDecision decision,
        string origin,
        out bool addedScopeOnlyPass)
    {
        addedScopeOnlyPass = false;
        if (string.IsNullOrWhiteSpace(decision.CategoryScope)
            && string.IsNullOrWhiteSpace(decision.Decision)
            && string.IsNullOrWhiteSpace(decision.Confidence)
            && string.IsNullOrWhiteSpace(decision.Reason))
        {
            return passes;
        }

        var resolvedCategoryScope = ResolveLlmPlannedRagCategoryScope(decision.CategoryScope);
        EmitRagTrace(
            "evidence.llm_planner.category_decision",
            ("origin", origin),
            ("decision", decision.Decision),
            ("confidence", decision.Confidence),
            ("raw_category", decision.CategoryScope),
            ("resolved_category", resolvedCategoryScope),
            ("accepted", !string.IsNullOrWhiteSpace(resolvedCategoryScope)),
            ("reason", decision.Reason));
        if (string.IsNullOrWhiteSpace(resolvedCategoryScope))
            return passes;

        var updated = ApplySourceBackedLlmCategoryScopeDecisionOrCreateScopeOnlyPass(
            passes,
            resolvedCategoryScope,
            "llm_planner",
            out var updatedPassCount,
            out addedScopeOnlyPass);
        if (updatedPassCount > 0 || addedScopeOnlyPass)
        {
            EmitRagTrace(
                "evidence.llm_planner.category_scope.applied",
                ("origin", origin),
                ("category", resolvedCategoryScope),
                ("updated_passes", updatedPassCount),
                ("scope_only_pass_added", addedScopeOnlyPass),
                ("labels", updated.Select(static pass => pass.Label).ToArray()));
        }

        return updated;
    }

    private async Task<SourceBackedLlmCategoryScopeDecision> TryAdjudicateSourceBackedLlmCategoryScopeAsync(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses,
        string effectiveUserMessage,
        string language,
        string categoryHints,
        CancellationToken ct,
        Action<string>? onProgress = null)
    {
        if (!HasSourceBackedLlmCategoryHints(categoryHints))
            return new SourceBackedLlmCategoryScopeDecision(null, null, null, null);

        var system = BuildSourceBackedLlmCategoryScopeSystemPrompt(language);
        var user = BuildSourceBackedLlmCategoryScopeUserPrompt(
            toolResults,
            currentAnalysis,
            plannedPasses,
            effectiveUserMessage,
            language,
            categoryHints);

        var sw = Stopwatch.StartNew();
        try
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressPlanRetrievalStrategy(language));
            EmitRagTrace(
                "evidence.llm_category_scope.start",
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("score", currentAnalysis.Score),
                ("usable_hits", currentAnalysis.UsableHitCount),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("pass_count", plannedPasses.Count),
                ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length),
                ("system_chars", system.Length),
                ("user_chars", user.Length),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs));
            using var plannerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plannerTimeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    plannerTimeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, true));
            var decision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(raw);
            EmitRagTrace(
                "evidence.llm_category_scope.end",
                ("accepted", !string.IsNullOrWhiteSpace(decision.CategoryScope)),
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("raw_chars", raw?.Length ?? 0),
                ("decision", decision.Decision),
                ("confidence", decision.Confidence),
                ("category", decision.CategoryScope),
                ("reason", decision.Reason));
            return decision;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_category_scope.timeout",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return new SourceBackedLlmCategoryScopeDecision(null, "timeout", null, "category_scope_llm_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_category_scope.error",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return new SourceBackedLlmCategoryScopeDecision(null, "error", null, "category_scope_llm_error");
        }
    }

    private static bool ShouldUseLlmSourceBackedEvidencePlanner(
        string effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis)
    {
        var canPlanWithoutInitialEvidence =
            LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || ShouldOfferBroadenedSourceSearch(effectiveUserMessage);

        if (string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase)
            || canPlanWithoutInitialEvidence)
            return true;

        if (currentAnalysis.UsableHitCount <= 0)
            return false;

        return LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
               || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeBroadSynthesisRequestShape(effectiveUserMessage)
               || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
               || (currentAnalysis.UsableHitCount > 0 && LooksLikeDocumentaryContentRequest(effectiveUserMessage))
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage);
    }

    private static bool ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPass(
        SourceBackedEvidenceSufficiency currentAnalysis,
        string? effectiveUserMessage,
        int remainingPlannerRounds,
        long elapsedMs,
        int explorationTimeoutMs)
    {
        if (remainingPlannerRounds <= 0)
            return false;

        if (!UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            || !string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(currentAnalysis, effectiveUserMessage))
            return false;

        var remainingMs = explorationTimeoutMs - elapsedMs;
        if (remainingMs < Math.Min(120000, explorationTimeoutMs / 3))
            return false;

        return ShouldUseLlmSourceBackedEvidencePlanner(effectiveUserMessage ?? string.Empty, currentAnalysis);
    }

}
