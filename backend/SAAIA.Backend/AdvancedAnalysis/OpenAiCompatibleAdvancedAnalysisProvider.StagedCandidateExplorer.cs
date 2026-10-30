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
        var navigatorTurns = new List<NativeResearchTurn>();
        IReadOnlyList<PromptEvidenceItem> rememberedNavigatorEvidence = [];
        var navigationMemory = new CandidateNavigationMemory([], 0);
        var navigationSinceLastJudge = 0;
        string? navigatorBoundedReason = null;
        var reserveNavigationUsed = false;
        var reserveJudgePending = false;
        var evidenceWindowReviewPending = false;
        var nativeHistoryOmittedTurnCount = 0;
        var capacityRepairAttempted = false;
        var mergeBlockedAdmissions = 0;
        var repairObservedAdmissionBound = 0;
        IReadOnlySet<string>? repairCandidateEvidenceAllowlist = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, observations) = await BuildStagedCandidateObservationsAsync(
                    request,
                    context,
                    cancellationToken,
                    repairCandidateEvidenceAllowlist)
                .ConfigureAwait(false);
            context.CandidateInventoryAdmissionBlockedCount += mergeBlockedAdmissions + repairObservedAdmissionBound;
            if (context.Tools.Budget?.EvidenceRotationEnabled is true)
            {
                var activeIds = context.Tools.Evidence.Select(item => item.Reference.EvidenceId).ToHashSet(StringComparer.Ordinal);
                rememberedNavigatorEvidence = rememberedNavigatorEvidence.Where(item => item.EvidenceId is { } id && activeIds.Contains(id)).ToArray();
                var retainedWindows = navigationMemory.Windows.Where(item => item.EvidenceId is { } id && activeIds.Contains(id)).ToArray();
                navigationMemory = new(retainedWindows,
                    navigationMemory.OmittedWindowCount + navigationMemory.Windows.Count - retainedWindows.Length);
            }
            rememberedNavigatorEvidence = RememberCandidateNavigatorEvidence(
                rememberedNavigatorEvidence,
                observations);
            navigationMemory = RememberCandidateNavigationWindows(navigationMemory, observations);
            var coverage = BuildCandidateExplorerCoverage(
                request,
                context.CandidateInventory);
            var judgeBatch = BuildCandidateJudgeBatch(
                context.CandidateInventory,
                observations);
            var remainingModelCalls = maximumCalls - completions.Count - reservedFinalCalls;
            var toolBudget = context.Tools.Budget;
            var documentaryHeadroom = context.ResourceLimit is null
                                     && (toolBudget is null
                                         || toolBudget.RemainingCalls > 0
                                         && toolBudget.RemainingElapsedMilliseconds > 0
                                         && toolBudget.EvidenceOperationsAllowed);
            var canNavigate = remainingModelCalls >= 2
                              && navigatorBoundedReason is null
                              && (documentaryHeadroom
                                  || observations.Any(HasUnprojectedCanonicalContent));
            var needsBodyIds = context.CandidateInventory.Where(item => item.Status == "needs_body")
                .SelectMany(item => item.BodyEvidenceIds).ToHashSet(StringComparer.Ordinal);
            var capacityRepairEvidence = observations.Where(item => item.EvidenceId is { } id
                && needsBodyIds.Contains(id) && HasUnprojectedCanonicalContent(item)).ToArray();
            var requestCapacityRepair = context.CandidateInventoryAdmissionBlockedCount > 0
                                        && !capacityRepairAttempted && canNavigate
                                        && capacityRepairEvidence.Length > 0;
            var reserveMissing = coverage.Roles.Any(role =>
                role.BodyVerifiedCount < role.ReserveTargetCount);
            var requestReserveNavigation = coverage.Ready && reserveMissing
                                           && !reserveNavigationUsed && canNavigate
                                           && context.CandidateInventoryAdmissionBlockedCount == 0;
            if (coverage.Ready && !requestReserveNavigation
                               && !(reserveJudgePending && judgeBatch.Candidates.Count > 0
                                    && remainingModelCalls > 0))
            {
                context.CandidateExplorerDossier = BuildStagedReadyDossier(
                    coverage,
                    reserveMissing
                        ? "The Candidate Judge produced a complete distinct assignment. The optional reserve remains below target after the bounded reserve opportunity or available budget."
                        : "The Candidate Judge produced a complete distinct assignment and the configured reserve targets.");
                context.FocusSearches.Clear();
                context.NativeTurn = null;
                return;
            }
            // After the first qualification round, gather one further documentary
            // batch before paying for a tiny Judge batch. Never postpone the last
            // available qualification or repeatedly navigate without judging.
            var deferSmallJudgeBatch = judgeRound > 0
                                       && judgeBatch.Candidates.Count < 3
                                       && navigationSinceLastJudge < 2
                                       && !reserveJudgePending
                                       && !evidenceWindowReviewPending
                                       && context.CandidateInventoryAdmissionBlockedCount == 0
                                       && coverage.MaximumAssignableCount + judgeBatch.Candidates.Count
                                       < coverage.RequiredDistinctCount
                                       && remainingModelCalls >= 3
                                       && canNavigate;
            if (judgeBatch.Candidates.Count > 0 && remainingModelCalls > 0
                && !deferSmallJudgeBatch && !requestReserveNavigation)
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
                        cancellationToken,
                        canonicalProjection: observations)
                    .ConfigureAwait(false);
                completions.Add(completion);
                var updates = ParseCandidateJudgeUpdates(
                    completion.Content,
                    request,
                    context.CandidateInventory,
                    coverage,
                    judgeBatch);
                var blockedRepairDiscoveries = 0;
                if (capacityRepairAttempted)
                {
                    var retainedKeys = context.CandidateInventory.Select(item => item.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    blockedRepairDiscoveries = updates.Count(item => !retainedKeys.Contains(item.Key));
                    updates = updates.Where(item => retainedKeys.Contains(item.Key)).ToArray();
                }
                context.CandidateInventory = MergeCandidateInventory(
                    context.CandidateInventory,
                    updates,
                    out var blockedJudgeAdmissions);
                blockedJudgeAdmissions += blockedRepairDiscoveries;
                mergeBlockedAdmissions += blockedJudgeAdmissions;
                context.CandidateInventoryAdmissionBlockedCount += blockedJudgeAdmissions;
                await SaveCandidateInventoryCheckpointAsync(
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
                navigatorFeedback = null;
                navigationSinceLastJudge = 0;
                reserveJudgePending = false;
                evidenceWindowReviewPending = false;
                continue;
            }

            if (context.CandidateInventoryAdmissionBlockedCount > 0 && !requestCapacityRepair)
            {
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                    coverage,
                    capacityRepairAttempted
                        ? "The active inventory remains at admission capacity after its single local repair opportunity and the pending Judge queue. No further discovery or repair cycle will run. This is an operational capacity limit, not corpus absence."
                        : "New canonical discoveries could not enter the bounded active candidate inventory after the pending Judge queue was discharged. This is an admission capacity limit, not corpus absence.");
                context.NativeTurn = null;
                return;
            }

            if (!canNavigate)
            {
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                    coverage,
                    navigatorBoundedReason
                    ?? "The staged pipeline preserved the final Writer/Critic calls before another complete Navigator-to-Judge cycle could run.");
                context.NativeTurn = null;
                return;
            }

            var navigatorEvidence = requestCapacityRepair
                ? capacityRepairEvidence
                : BuildCandidateNavigatorEvidence(rememberedNavigatorEvidence, context.CandidateInventory);
            var navigatorHistory = BuildCandidateNavigatorHistory(
                navigatorTurns, navigatorEvidence, out var omittedHistoryTurns);
            nativeHistoryOmittedTurnCount += omittedHistoryTurns;
            var navigatorPrompt = BuildCandidateNavigatorUserPrompt(
                request,
                context,
                coverage,
                navigatorEvidence,
                navigatorFeedback,
                maximumCalls - completions.Count - reservedFinalCalls,
                rememberedNavigatorEvidence,
                documentaryHeadroom && !requestCapacityRepair,
                navigationMemory,
                nativeHistoryOmittedTurnCount,
                requestCapacityRepair);
            if (requestCapacityRepair)
            {
                capacityRepairAttempted = true;
                repairObservedAdmissionBound = Math.Max(0,
                    context.CandidateInventoryAdmissionBlockedCount - mergeBlockedAdmissions);
                repairCandidateEvidenceAllowlist = context.CandidateInventory
                    .SelectMany(item => item.BodyEvidenceIds.Concat(item.LocatorEvidenceIds))
                    .ToHashSet(StringComparer.Ordinal);
            }
            context.Tools.BeginEvidenceBatch(RetainedResearchEvidenceIds(context, observations)
                .Concat(navigatorEvidence.Select(item => item.EvidenceId).OfType<string>())
                .Distinct(StringComparer.Ordinal).ToArray());
            var navigator = await CompleteJsonAsync(
                    request.JobId,
                    $"candidate-navigator-{++navigatorRound}",
                    BuildCandidateNavigatorSystemPrompt(),
                    navigatorPrompt,
                    Math.Clamp(_options.CandidateExplorerMaxTokens, 512, 16_384),
                    cancellationToken,
                    allowNativeResearch: true,
                    nativeToolTurnMessages: navigatorHistory,
                    canonicalProjection: observations)
                .ConfigureAwait(false);
            completions.Add(navigator);

            if (!RequestsCorpusResearch(navigator.Content))
            {
                var terminalReason = ParseCandidateNavigatorTerminal(
                    navigator.Content);
                if (requestCapacityRepair)
                    terminalReason = "Admission capacity remains bounded after its single local repair opportunity: "
                                     + terminalReason;
                if (coverage.Ready)
                {
                    context.CandidateExplorerDossier = BuildStagedReadyDossier(
                        coverage,
                        "A complete distinct assignment was preserved; additional reserve navigation was bounded: " + terminalReason);
                    context.NativeTurn = null;
                    return;
                }
                if (judgeBatch.Candidates.Count > 0)
                {
                    navigatorBoundedReason = terminalReason;
                    continue;
                }
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(
                    coverage,
                    terminalReason);
                context.NativeTurn = null;
                return;
            }

            IReadOnlyList<AdvancedAnalysisSearchRequest> queries;
            var evidenceReadCount = 0;
            try
            {
                if (navigator.NativeArgumentFeedback is { } nativeFeedback)
                    throw new AdvancedAnalysisProviderException("advanced_native_tool_arguments_invalid",
                        researchArgumentFeedback: nativeFeedback);
                using var document = JsonDocument.Parse(UnwrapJson(navigator.Content));
                if (!document.RootElement.TryGetProperty("queries", out var values)
                    || values.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException();
                }
                queries = values.GetArrayLength() == 0 ? [] : ParseResearchReview(
                    JsonSerializer.Serialize(new
                    {
                        decision = "search_more",
                        queries = values
                    }, JsonOptions),
                    request,
                    context.AvailableCategories,
                    observations);
                if (requestCapacityRepair && queries.Count > 0)
                {
                    context.CandidateExplorerDossier = BuildStagedBoundedDossier(coverage,
                        "The single capacity repair opportunity requested a new documentary operation. It was not executed; only local canonical body windows were allowed. Admission capacity is not corpus absence.");
                    context.NativeTurn = null;
                    return;
                }
                EnsureResearchBatchFitsToolBudget(
                    context.Tools,
                    queries,
                    context.PreviouslyExecuted);
                if (document.RootElement.TryGetProperty("evidenceReads", out _)
                    && navigator.NativeToolCallsJson is null)
                    throw new JsonException();
                evidenceReadCount = ApplyEvidenceReadRequests(
                    navigator.Content, navigatorEvidence, context);
                if (queries.Count == 0 && !document.RootElement.TryGetProperty("evidenceReads", out _))
                    throw new JsonException();
            }
            catch (AdvancedAnalysisProviderException error) when (
                error.ResearchArgumentFeedback is { } feedback
                && !requestCapacityRepair && navigatorCorrection++ == 0)
            {
                if (navigator.NativeToolCallsJson is { } rejectedCalls)
                    navigatorTurns.Add(new(rejectedCalls, null,
                        new Dictionary<string, AdvancedAnalysisSearchObservation>(), feedback,
                        navigator.NativeResponseOutputJson));
                navigatorFeedback = BuildResearchArgumentFeedbackForPrompt(feedback);
                continue;
            }
            catch (Exception error) when (requestCapacityRepair
                && error is AdvancedAnalysisProviderException or JsonException or InvalidOperationException)
            {
                context.CandidateExplorerDossier = BuildStagedBoundedDossier(coverage,
                    "The single local capacity repair opportunity supplied invalid arguments and made no documentary change. No retry or new discovery will run; admission capacity is not corpus absence.");
                context.NativeTurn = null;
                return;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_candidate_navigator_protocol_invalid");
            }

            var hasNewOperation = evidenceReadCount > 0 || queries.Any(query =>
                !context.PreviouslyExecuted.Contains(BuildSearchIdentity(query)));
            if (!hasNewOperation)
            {
                if (requestCapacityRepair)
                {
                    context.CandidateExplorerDossier = BuildStagedBoundedDossier(coverage,
                        "The single local capacity repair opportunity made no new canonical projection. Admission capacity remains bounded, not evidence of corpus absence.");
                    context.NativeTurn = null;
                    return;
                }
                if (navigatorCorrection++ > 0)
                {
                    context.CandidateExplorerDossier = coverage.Ready
                        ? BuildStagedReadyDossier(coverage,
                            "A complete distinct assignment was preserved; optional reserve navigation repeated operations after one bounded correction.")
                        : BuildStagedBoundedDossier(coverage,
                            "The Navigator repeated already executed operations after one bounded correction.");
                    context.NativeTurn = null;
                    return;
                }
                navigatorFeedback = new
                {
                    reasonCode = "search_already_executed",
                    instruction = "Choose a different exact title, offset, valid page window or source. The repeated operations will not run again."
                };
                if (navigator.NativeToolCallsJson is { } duplicateCalls)
                    navigatorTurns.Add(new(duplicateCalls, queries,
                        new Dictionary<string, AdvancedAnalysisSearchObservation>(),
                        ResponseOutputItemsJson: navigator.NativeResponseOutputJson));
                continue;
            }

            var rememberedFocus = RememberFocusedSearches(
                context.FocusSearches,
                queries);
            context.FocusSearches.Clear();
            context.FocusSearches.AddRange(rememberedFocus);
            var navigatorResults = new Dictionary<string, AdvancedAnalysisSearchObservation>(
                StringComparer.OrdinalIgnoreCase);
            var retainedForBatch = RetainedResearchEvidenceIds(context, observations)
                .Concat(navigatorEvidence.Select(item => item.EvidenceId).OfType<string>())
                .ToHashSet(StringComparer.Ordinal);
            context.Tools.BeginEvidenceBatch(retainedForBatch);
            if (context.Tools.Budget?.EvidenceRotationEnabled is true)
                await SaveCandidateInventoryCheckpointAsync(context, cancellationToken, retainedForBatch).ConfigureAwait(false);
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
            PruneRetiredEvidenceCopies(context);
            RememberLatestExplicitRead(context, queries, navigatorResults);
            context.ResourceLimit ??= resourceLimit;
            if (navigator.NativeToolCallsJson is { } executedCalls)
                navigatorTurns.Add(new(executedCalls, queries, navigatorResults,
                    ResponseOutputItemsJson: navigator.NativeResponseOutputJson,
                    EvidenceReadsStored: evidenceReadCount > 0));
            if (evidenceReadCount > 0)
            {
                evidenceWindowReviewPending = true;
                await SaveCandidateInventoryCheckpointAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            navigationSinceLastJudge++;
            if (requestReserveNavigation)
            {
                reserveNavigationUsed = true;
                reserveJudgePending = true;
            }
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
            CancellationToken cancellationToken,
            IReadOnlySet<string>? candidateEvidenceAllowlist = null)
    {
        PruneRetiredEvidenceCopies(context);
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
            .OrderBy(item => item.Status == "awaiting_review"
                             || item.Status == "body_verified" && item.TargetRoles.Count == 0
                ? 0
                : 1)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        focused = PrioritizeCandidateInventoryEvidence(
            evidence,
            judgeFirstInventory,
            focused);
        focused = PrioritizeEvidenceReadWindows(evidence, focused, context.EvidenceReadWindows);
        focused = PrioritizeLatestExplicitRead(evidence, focused, context.LatestExplicitReadEvidenceIds);
        var observations = BuildPromptEvidenceWithPersistentSourceKeys(
            request,
            evidence,
            context.RetrievalQueriesByEvidenceId,
            focused,
            context.PromptSourceKeys,
            context.EvidenceReadWindows);
        var observedInventory = ObserveCandidateInventory(
            request,
            context.CandidateInventory,
            candidateEvidenceAllowlist is null ? observations
                : observations.Where(item => item.EvidenceId is { } id
                    && candidateEvidenceAllowlist.Contains(id)).ToArray(),
            out var blockedDiscoveryCount,
            context.ObservedCandidateEvidenceIds,
            observeUntitledBodies: true);
        context.CandidateInventoryAdmissionBlockedCount = blockedDiscoveryCount;
        if (JsonSerializer.Serialize(observedInventory, JsonOptions)
            != JsonSerializer.Serialize(context.CandidateInventory, JsonOptions))
        {
            context.CandidateInventory = observedInventory;
            await SaveCandidateInventoryCheckpointAsync(
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        context.Tools.BeginEvidenceBatch(RetainedResearchEvidenceIds(context, observations));
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
