using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task RunStagedCandidateExplorerAsync(
        AdvancedAnalysisProviderRequest request,
        bool runSemanticCritic,
        SynthesisResearchContext context,
        List<CompletionResult> completions,
        CancellationToken cancellationToken)
    {
        if (!context.CandidateInventoryCheckpointLoaded)
        {
            await RestoreCandidateInventoryCheckpointAsync(
                    request,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var reservedFinalCalls = 1 + (runSemanticCritic ? 1 : 0);
        var maximumCalls = Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024);
        object? navigatorFeedback = null;
        var navigatorCorrection = 0;
        var judgeRound = 0;
        var navigatorRound = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, observations) = await BuildStagedCandidateObservationsAsync(
                    request,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            var coverage = BuildCandidateExplorerCoverage(
                request,
                context.CandidateInventory);
            if (coverage.Ready)
            {
                context.CandidateExplorerDossier = BuildStagedReadyDossier(
                    coverage,
                    "The staged Candidate Judge produced a complete distinct assignment.");
                context.FocusSearches.Clear();
                context.NativeTurn = null;
                return;
            }

            var judgeBatch = BuildCandidateJudgeBatch(
                context.CandidateInventory,
                observations);
            var remainingModelCalls = maximumCalls - completions.Count - reservedFinalCalls;
            if (judgeBatch.Candidates.Count > 0 && remainingModelCalls > 0)
            {
                var completion = await CompleteJsonAsync(
                        request.JobId,
                        $"candidate-judge-{++judgeRound}",
                        BuildCandidateJudgeSystemPrompt(),
                        BuildCandidateJudgeUserPrompt(
                            request,
                            context.CandidateInventory,
                            coverage,
                            judgeBatch),
                        Math.Clamp(_options.CandidateExplorerMaxTokens, 512, 16_384),
                        cancellationToken)
                    .ConfigureAwait(false);
                completions.Add(completion);
                var updates = ParseCandidateJudgeUpdates(
                    completion.Content,
                    request,
                    context.CandidateInventory,
                    coverage,
                    judgeBatch);
                context.CandidateInventory = MergeCandidateInventory(
                    context.CandidateInventory,
                    updates);
                await SaveCandidateInventoryCheckpointAsync(
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
                navigatorFeedback = null;
                continue;
            }

            var toolBudget = context.Tools.Budget;
            var canNavigate = remainingModelCalls >= 2
                              && context.ResourceLimit is null
                              && (toolBudget is null
                                  || toolBudget.RemainingCalls > 0
                                  && toolBudget.RemainingElapsedMilliseconds > 0
                                  && toolBudget.RemainingEvidenceItems is not 0);
            if (!canNavigate)
            {
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                    coverage,
                    "The staged pipeline preserved the final Writer/Critic calls before another complete Navigator-to-Judge cycle could run.");
                context.NativeTurn = null;
                return;
            }

            var navigatorEvidence = BuildCandidateNavigatorEvidence(
                observations,
                context.CandidateInventory);
            var navigatorPrompt = BuildCandidateNavigatorUserPrompt(
                request,
                context,
                coverage,
                navigatorEvidence,
                navigatorFeedback,
                maximumCalls - completions.Count - reservedFinalCalls);
            var navigator = await CompleteJsonAsync(
                    request.JobId,
                    $"candidate-navigator-{++navigatorRound}",
                    BuildCandidateNavigatorSystemPrompt(),
                    navigatorPrompt,
                    Math.Clamp(_options.CandidateExplorerMaxTokens, 512, 16_384),
                    cancellationToken,
                    allowNativeResearch: true)
                .ConfigureAwait(false);
            completions.Add(navigator);

            if (!RequestsCorpusResearch(navigator.Content))
            {
                var terminalReason = ParseCandidateNavigatorTerminal(
                    navigator.Content);
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                    coverage,
                    terminalReason);
                context.NativeTurn = null;
                return;
            }

            IReadOnlyList<AdvancedAnalysisSearchRequest> queries;
            try
            {
                using var document = JsonDocument.Parse(UnwrapJson(navigator.Content));
                if (!document.RootElement.TryGetProperty("queries", out var values)
                    || values.ValueKind != JsonValueKind.Array
                    || values.GetArrayLength() == 0)
                {
                    throw new JsonException();
                }
                queries = ParseResearchReview(
                    JsonSerializer.Serialize(new
                    {
                        decision = "search_more",
                        queries = values
                    }, JsonOptions),
                    request,
                    context.AvailableCategories,
                    navigatorEvidence);
                EnsureResearchBatchFitsToolBudget(
                    context.Tools,
                    queries,
                    context.PreviouslyExecuted);
            }
            catch (AdvancedAnalysisProviderException error) when (
                error.ResearchArgumentFeedback is { } feedback
                && navigatorCorrection++ == 0)
            {
                navigatorFeedback = BuildResearchArgumentFeedbackForPrompt(feedback);
                continue;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_candidate_navigator_protocol_invalid");
            }

            var hasNewOperation = queries.Any(query =>
                !context.PreviouslyExecuted.Contains(BuildSearchIdentity(query)));
            if (!hasNewOperation)
            {
                if (navigatorCorrection++ > 0)
                {
                    context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                        coverage,
                        "The Navigator repeated already executed operations after one bounded correction.");
                    context.NativeTurn = null;
                    return;
                }
                navigatorFeedback = new
                {
                    reasonCode = "search_already_executed",
                    instruction = "Choose a different exact title, offset, valid page window or source. The repeated operations will not run again."
                };
                continue;
            }

            var rememberedFocus = RememberFocusedSearches(
                context.FocusSearches,
                queries);
            context.FocusSearches.Clear();
            context.FocusSearches.AddRange(rememberedFocus);
            var navigatorResults = new Dictionary<string, AdvancedAnalysisSearchObservation>(
                StringComparer.OrdinalIgnoreCase);
            var resourceLimit = await ExecuteSearchBatchAsync(
                    context.Tools,
                    queries,
                    context.PreviouslyExecuted,
                    context.PriorSearches,
                    context.EvidenceGroups,
                    context.RetrievalQueriesByEvidenceId,
                    cancellationToken,
                    observationsBySearch: navigatorResults,
                    allowResourceLimitFeedback: true)
                .ConfigureAwait(false);
            context.ResourceLimit ??= resourceLimit;
            context.NativeTurn = null;
            var retryableLimit = navigatorResults.Values
                .Select(result => result.ResourceLimit)
                .FirstOrDefault(limit => limit is { StopsResearch: false });
            navigatorFeedback = retryableLimit is null
                ? null
                : new
                {
                    reasonCode = retryableLimit.ReasonCode,
                    retryableLimit,
                    instruction = "The requested bounded read returned too many items. Retry with a smaller valid window or a larger permitted topK; do not infer source absence."
                };
            navigatorCorrection = 0;
        }
    }

    private async Task<(IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
            IReadOnlyList<PromptEvidenceItem> Observations)>
        BuildStagedCandidateObservationsAsync(
            AdvancedAnalysisProviderRequest request,
            SynthesisResearchContext context,
            CancellationToken cancellationToken)
    {
        var evidence = FilterEvidenceToRequestedDocumentSet(
            request,
            OrderEvidenceForPrompt(
                context.Tools.Evidence,
                context.EvidenceGroups));
        var focused = PrioritizeFocusedEvidenceForPrompt(
            evidence,
            context.RetrievalQueriesByEvidenceId,
            context.FocusSearches);
        var judgeFirstInventory = context.CandidateInventory
            .OrderBy(item => item.Status == "body_verified"
                             && item.TargetRoles.Count == 0
                ? 0
                : 1)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        focused = PrioritizeCandidateInventoryEvidence(
            evidence,
            judgeFirstInventory,
            focused);
        var observations = BuildPromptEvidenceWithPersistentSourceKeys(
            request,
            evidence,
            context.RetrievalQueriesByEvidenceId,
            focused,
            context.PromptSourceKeys);
        var observedInventory = ObserveCandidateInventory(
            request,
            context.CandidateInventory,
            observations);
        if (JsonSerializer.Serialize(observedInventory, JsonOptions)
            != JsonSerializer.Serialize(context.CandidateInventory, JsonOptions))
        {
            context.CandidateInventory = observedInventory;
            await SaveCandidateInventoryCheckpointAsync(
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return (evidence, observations);
    }

    private static CandidateExplorerDossier BuildStagedReadyDossier(
        CandidateExplorerCoverage coverage,
        string reason)
        => new(
            "ready",
            reason,
            coverage.RequiredDistinctCount,
            coverage.BodyVerifiedDistinctCount,
            coverage.MaximumAssignableCount,
            coverage.Roles,
            coverage.MissingByRole,
            coverage.ProposedAssignments,
            coverage.BodyEvidenceIds,
            coverage.EligibleCandidateKeys,
            "proposedAssignments is a deterministic feasible placement over Candidate Judge targetRoles. Use its assigned candidate for each coordinate and never reuse one candidate. Refine a title only from the same assigned substantive body.");

    private static CandidateExplorerDossier BuildStagedBoundedDossier(
        CandidateExplorerCoverage coverage,
        string reason)
        => new(
            "bounded_gap",
            reason,
            coverage.RequiredDistinctCount,
            coverage.BodyVerifiedDistinctCount,
            coverage.MaximumAssignableCount,
            coverage.Roles,
            coverage.MissingByRole,
            coverage.ProposedAssignments,
            coverage.BodyEvidenceIds,
            coverage.EligibleCandidateKeys,
            "The staged pipeline stopped with a measured assignment gap. Use only proposedAssignments for supported coordinates and describe missingByRole without converting an operational bound into corpus absence.");
}
