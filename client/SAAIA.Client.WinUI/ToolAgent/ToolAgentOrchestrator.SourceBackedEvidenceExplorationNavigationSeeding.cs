using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<bool> TrySeedSourceBackedDocumentNavigationFromEvidenceAsync(
        ToolResults toolResults,
        string language,
        CancellationToken ct,
        Action<string>? onProgress,
        ISet<string>? seededNavigationScopes,
        int maxDocuments = 3)
    {
        var seeds = SelectSourceBackedDocumentNavigationSeeds(toolResults, maxDocuments);
        if (seeds.Count == 0)
            return false;

        var acceptedAny = false;
        foreach (var seed in seeds)
        {
            var scopeKey = BuildSourceBackedDocumentNavigationSeedScopeKey(seed);
            if (string.IsNullOrWhiteSpace(scopeKey))
                continue;
            if (seededNavigationScopes is not null && !seededNavigationScopes.Add(scopeKey))
                continue;

            var args = string.IsNullOrWhiteSpace(seed.DocId)
                ? CreateJsonArgs(new
                {
                    docPath = seed.DocPath,
                    limit = 180,
                    offset = 0
                })
                : CreateJsonArgs(new
                {
                    docRef = seed.DocId,
                    limit = 180,
                    offset = 0
                });

            var sw = Stopwatch.StartNew();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressInspectDocumentStructure(language));
                var navigation = await ExecDocumentsNavigationAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, true));
                if (!DocumentNavigationHasItems(navigation))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.navigation",
                    Result = navigation,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("documents.navigation", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.navigation");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, false));
            }
        }

        return acceptedAny;
    }

    private static bool ShouldRespectLlmRouterGeneralWithoutTools(RouterPlan? plan)
        => plan is not null
           && plan.Origin == RouterPlanOrigin.Llm
           && string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
           && !plan.NeedClarification
           && (plan.ToolCalls is null || plan.ToolCalls.Count == 0);

    private static bool DocumentNavigationHasItems(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
           && items.GetArrayLength() > 0;

    private static int ResolveSourceBackedEvidenceExplorationRagCallBudget(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration
            || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage)
            || currentAnalysis.Kind is "planning" or "broad")
        {
            var budget = MaxBroadExplorationRagToolCalls;
            if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                || currentAnalysis.Kind == "planning")
            {
                var targetSlots = Math.Max(
                    currentAnalysis.TargetSlotCount,
                    ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
                var minimumCandidates = Math.Max(
                    currentAnalysis.MinimumCandidateCount,
                    ResolveMinimumBroadSourceBackedSynthesisHitCount(effectiveUserMessage));
                if (targetSlots >= 10 || minimumCandidates >= 6)
                    budget = Math.Max(
                        budget,
                        Math.Min(48, Math.Max(targetSlots, minimumCandidates) + 18));
            }

            return forceBroadenedExploration
                ? Math.Min(48, Math.Max(budget, MaxBroadExplorationRagToolCalls + 12))
                : budget;
        }

        return MaxRagToolCalls;
    }

    private static int ResolveSourceBackedEvidenceExplorationTimeoutMs(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis)
    {
        if (!UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            && !string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return SourceBackedEvidenceExplorationTimeoutMs;
        }

        var targetSlots = Math.Max(
            currentAnalysis.TargetSlotCount,
            ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
        var minimumCandidates = Math.Max(
            currentAnalysis.MinimumCandidateCount,
            ResolveMinimumBroadSourceBackedSynthesisHitCount(effectiveUserMessage));
        var target = Math.Max(targetSlots, minimumCandidates);
        if (target <= 8)
            return SourceBackedEvidenceExplorationTimeoutMs;

        var extraMs = Math.Min(360000, (target - 8) * 30000);
        return Math.Clamp(
            SourceBackedEvidenceExplorationTimeoutMs + extraMs,
            SourceBackedEvidenceExplorationTimeoutMs,
            MaxSourceBackedEvidenceExplorationTimeoutMs);
    }

    private static int ResolveSourceBackedPlanningWriterTimeoutMs(string? effectiveUserMessage)
    {
        if (ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage))
        {
            return ExtendedSourceBackedPlanningWriterTimeoutMs;
        }

        return SourceBackedPlanningWriterTimeoutMs;
    }

    private static bool ShouldUseStageWideSourceBackedWriterTimeout(string? effectiveUserMessage)
        => !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
           && !LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           && !LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
           && !LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage);

    private static int ResolveSourceBackedContextReadPassLimit(string? effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var surplus = Math.Max(4, (int)Math.Ceiling(targetSlots * 0.5d));
        return Math.Clamp(targetSlots + surplus, 12, 32);
    }

    private static int ResolveSourceBackedContextReadPassBatchLimit(string? effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        return Math.Clamp((int)Math.Ceiling(targetSlots * 0.55d), 6, 12);
    }

    private static bool ShouldSeedSourceBackedNavigationStructure(
        string effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration)
            return true;

        return ShouldAllowSourceBackedBroadResearchPass(
            effectiveUserMessage,
            currentAnalysis,
            forceBroadenedExploration);
    }

    private static bool LooksLikeSourceBackedBroadResearchRequest(string? effectiveUserMessage)
        => LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
           || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
           || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
           || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
           || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
           || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage);

    private static bool ShouldUseResearchSurfacesForBroadRagRequest(string? effectiveUserMessage)
        => LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage)
           || IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage);

    private static bool ShouldAllowSourceBackedBroadResearchPass(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration)
            return true;
        if (!LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage))
            return false;
        if (currentAnalysis.IsSufficient)
            return false;

        if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            || currentAnalysis.Kind == "planning")
        {
            return currentAnalysis.ShouldExplore
                   || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount;
        }

        return currentAnalysis.ShouldExplore
               || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount
               || currentAnalysis.DistinctSourcePageCount < Math.Min(3, Math.Max(1, currentAnalysis.MinimumCandidateCount));
    }

    private async Task<bool> TrySeedSourceBackedNavigationStructureAsync(
        ToolResults toolResults,
        string? categoryScope,
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onProgress,
        ISet<string>? seededNavigationScopes = null)
    {
        var scopeKey = string.IsNullOrWhiteSpace(categoryScope)
            ? "__global__"
            : NormalizeLooseLookup(categoryScope);
        if (seededNavigationScopes is not null && !seededNavigationScopes.Add(scopeKey))
            return false;

        var hasExistingTree = string.IsNullOrWhiteSpace(categoryScope)
                              && toolResults.Items.Any(static item => item.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(item.Error));

        var acceptedAny = false;
        var args = string.IsNullOrWhiteSpace(categoryScope)
            ? CreateJsonArgs(new { depth = 2, format = "json" })
            : CreateJsonArgs(new { path = categoryScope, depth = 3, format = "json" });

        var sw = new Stopwatch();
        if (!hasExistingTree)
        {
            sw.Start();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressInspectDocumentStructure(language));
                var result = await ExecDocumentsTreeAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.tree",
                    Result = result,
                    DurationMs = sw.ElapsedMilliseconds
                });
                _lastToolDurations.Add(("documents.tree", sw.ElapsedMilliseconds, true));
                if (!_mem.LastToolNames.Contains("documents.tree", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.tree");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.tree", sw.ElapsedMilliseconds, false));
            }
        }

        var orientationQueryLimit = UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                                    || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
                                    || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            ? Math.Max(MaxSourceBackedNavigationOrientationQueries, 8)
            : MaxSourceBackedNavigationOrientationQueries;
        foreach (var navigationQuery in BuildSourceBackedNavigationOrientationQueries(effectiveUserMessage, categoryScope)
                     .Take(orientationQueryLimit))
        {
            var navigationArgs = string.IsNullOrWhiteSpace(categoryScope)
                ? CreateJsonArgs(new
                {
                    q = navigationQuery,
                    limit = 120,
                    offset = 0
                })
                : CreateJsonArgs(new
                {
                    path = categoryScope,
                    q = navigationQuery,
                    limit = 120,
                    offset = 0
                });

            sw.Restart();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));
                var navigation = await ExecDocumentsNavigationAsync(navigationArgs, ct).ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, true));
                if (!DocumentNavigationHasItems(navigation))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.navigation",
                    Result = navigation,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("documents.navigation", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.navigation");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, false));
            }
        }

        var summaryOrientationQueryLimit = UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                                           || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
                                           || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            ? Math.Max(MaxSourceBackedSummaryOrientationQueries, 8)
            : MaxSourceBackedSummaryOrientationQueries;
        foreach (var summaryQuery in BuildSourceBackedSummaryOrientationQueries(effectiveUserMessage, categoryScope)
                     .Take(summaryOrientationQueryLimit))
        {
            sw.Restart();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));
                var summary = await ExecSummarySearchAsync(
                        CreateJsonArgs(new
                        {
                            q = summaryQuery,
                            limit = 12,
                            offset = 0
                        }),
                        ct)
                    .ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("summary.search", sw.ElapsedMilliseconds, true));
                if (!SummarySearchHasItems(summary))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "summary.search",
                    Result = summary,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("summary.search", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("summary.search");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("summary.search", sw.ElapsedMilliseconds, false));
            }
        }

        return acceptedAny;
    }

    private static string[] BuildSourceBackedNavigationOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }

    private static string[] BuildSourceBackedSummaryOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }

    private static bool SummarySearchHasItems(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
           && items.GetArrayLength() > 0;
}
