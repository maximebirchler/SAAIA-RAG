namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const int FlatEvidenceTournamentMaximumVisibleItems = 12;
    private const int FlatEvidenceTournamentMaximumSelectionItems = 8;

    private async Task<FlatEvidenceAdequacyOutcome>
        ReviewFlatEvidenceAdequacyAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            EvidenceBundle bundle,
            IReadOnlyList<string> approvedEvidenceIds,
            int provisionalTarget,
            string atomicEvidenceMode,
            IReadOnlyList<string> incumbentEvidenceIds,
            HashSet<string> reviewedEvidenceIds,
            string semanticFeedback,
            CancellationToken ct)
    {
        var eligibleItems = SelectFlatEvidenceAdequacyItems(
            bundle,
            approvedEvidenceIds,
            provisionalTarget,
            atomicEvidenceMode);
        var contentClaimTournament = string.Equals(
            atomicEvidenceMode,
            "content_claim",
            StringComparison.OrdinalIgnoreCase);
        if (!contentClaimTournament || eligibleItems.Count == 0)
        {
            return await ReviewFlatEvidenceAdequacyRoundAsync(
                    intake,
                    semanticPlan,
                    bundle,
                    eligibleItems,
                    provisionalTarget,
                    Math.Max(1, provisionalTarget),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    semanticFeedback,
                    ct)
                .ConfigureAwait(false);
        }

        var maximumSelectionItems = Math.Clamp(
            provisionalTarget,
            1,
            FlatEvidenceTournamentMaximumSelectionItems);
        var eligibleById = eligibleItems.ToDictionary(
            static item => item.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var currentSelection = incumbentEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(eligibleById.ContainsKey)
            .Take(maximumSelectionItems)
            .Select(id => eligibleById[id])
            .ToList();
        FlatEvidenceAdequacyOutcome? latestOutcome = null;
        var elapsedMilliseconds = 0L;
        var reviewRounds = 0;
        var reviewedDuringTournament = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var currentSelectionIds = currentSelection
                .Select(static item => item.EvidenceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var challengerCapacity = Math.Max(
                1,
                FlatEvidenceTournamentMaximumVisibleItems
                - currentSelection.Count);
            var challengers = eligibleItems
                .Where(item => !currentSelectionIds.Contains(item.EvidenceId)
                               && !reviewedEvidenceIds.Contains(item.EvidenceId))
                .Take(challengerCapacity)
                .ToArray();
            if (challengers.Length == 0)
            {
                if (reviewRounds > 0)
                    break;

                challengers = currentSelection.Count == 0
                    ? eligibleItems
                        .Take(FlatEvidenceTournamentMaximumVisibleItems)
                        .ToArray()
                    : Array.Empty<EvidenceItem>();
            }

            var roundItems = InterleaveFlatEvidenceTournamentItems(
                currentSelection,
                challengers);
            if (roundItems.Count == 0)
                break;

            var outcome = await ReviewFlatEvidenceAdequacyRoundAsync(
                    intake,
                    semanticPlan,
                    bundle,
                    roundItems,
                    provisionalTarget,
                    maximumSelectionItems,
                    currentSelectionIds,
                    semanticFeedback,
                    ct)
                .ConfigureAwait(false);
            latestOutcome = outcome;
            elapsedMilliseconds += outcome.ElapsedMilliseconds;
            reviewRounds++;
            if (!string.Equals(
                    outcome.Decision,
                    "select",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (outcome.ProtocolValid
                    && string.Equals(
                        outcome.Decision,
                        "continue",
                        StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var item in roundItems)
                    {
                        reviewedEvidenceIds.Add(item.EvidenceId);
                        reviewedDuringTournament.Add(item.EvidenceId);
                    }
                    var usefulIds = outcome.TournamentSelectionEvidenceIds
                                    ?? Array.Empty<string>();
                    currentSelection = usefulIds
                        .Where(eligibleById.ContainsKey)
                        .Select(id => eligibleById[id])
                        .ToList();
                    var retainedIds = currentSelection
                        .Select(static item => item.EvidenceId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var hasUnreviewedRetrievedCandidates = eligibleItems.Any(
                        item => !retainedIds.Contains(item.EvidenceId)
                                && !reviewedEvidenceIds.Contains(item.EvidenceId));
                    if (hasUnreviewedRetrievedCandidates)
                    {
                        continue;
                    }
                }
                return outcome with
                {
                    ElapsedMilliseconds = elapsedMilliseconds,
                    TournamentSelectionEvidenceIds = currentSelection
                        .Select(static item => item.EvidenceId)
                        .ToArray(),
                    ReviewRounds = reviewRounds,
                    ReviewedCandidateCount = reviewedDuringTournament.Count
                };
            }

            foreach (var item in roundItems)
            {
                reviewedEvidenceIds.Add(item.EvidenceId);
                reviewedDuringTournament.Add(item.EvidenceId);
            }
            var selectedIds = ReadFlatEvidenceTournamentSelection(outcome);
            currentSelection = selectedIds
                .Where(eligibleById.ContainsKey)
                .Select(id => eligibleById[id])
                .ToList();
        }

        if (latestOutcome is null)
        {
            latestOutcome = await ReviewFlatEvidenceAdequacyRoundAsync(
                    intake,
                    semanticPlan,
                    bundle,
                    eligibleItems
                        .Take(FlatEvidenceTournamentMaximumVisibleItems)
                        .ToArray(),
                    provisionalTarget,
                    maximumSelectionItems,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    semanticFeedback,
                    ct)
                .ConfigureAwait(false);
            elapsedMilliseconds = latestOutcome.ElapsedMilliseconds;
            reviewRounds = 1;
        }

        return latestOutcome with
        {
            ElapsedMilliseconds = elapsedMilliseconds,
            TournamentSelectionEvidenceIds = currentSelection
                .Select(static item => item.EvidenceId)
                .ToArray(),
            ReviewRounds = reviewRounds,
            ReviewedCandidateCount = reviewedDuringTournament.Count
        };
    }

    private static IReadOnlyList<EvidenceItem>
        InterleaveFlatEvidenceTournamentItems(
            IReadOnlyList<EvidenceItem> incumbents,
            IReadOnlyList<EvidenceItem> challengers)
    {
        var result = new List<EvidenceItem>(incumbents.Count + challengers.Count);
        var maximumCount = Math.Max(incumbents.Count, challengers.Count);
        for (var index = 0; index < maximumCount; index++)
        {
            if (index < challengers.Count)
                result.Add(challengers[index]);
            if (index < incumbents.Count)
                result.Add(incumbents[index]);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadFlatEvidenceTournamentSelection(
        FlatEvidenceAdequacyOutcome outcome)
    {
        var selectionCall = outcome.RoutedCompletion?.ToolCalls.SingleOrDefault(
            static call => string.Equals(
                call.Name,
                SemanticSelectionToolName,
                StringComparison.OrdinalIgnoreCase));
        return selectionCall is null
            ? Array.Empty<string>()
            : ReadFlatAdequacyStringArray(
                selectionCall.Arguments,
                "evidenceIds",
                FlatEvidenceTournamentMaximumSelectionItems);
    }

    private void TraceFlatEvidenceAdequacy(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        FlatEvidenceAdequacyOutcome outcome,
        IReadOnlyList<string> approvedEvidenceIds,
        int provisionalTarget)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.AnswerAdequacyJudge,
            "source_backed_agent_v2.flat_evidence_adequacy.completed",
            ("turn", turn),
            ("decision", outcome.Decision),
            ("reason", outcome.Reason),
            ("protocol_valid", outcome.ProtocolValid),
            ("approved_evidence_ids", approvedEvidenceIds),
            ("provisional_target", provisionalTarget),
            ("review_rounds", outcome.ReviewRounds),
            ("reviewed_candidate_count", outcome.ReviewedCandidateCount),
            ("tournament_selection_evidence_ids",
                outcome.TournamentSelectionEvidenceIds ?? Array.Empty<string>()),
            ("ms", outcome.ElapsedMilliseconds),
            ("prompt_tokens", outcome.Completion.PromptTokens),
            ("completion_tokens", outcome.Completion.CompletionTokens),
            ("finish_reason", outcome.Completion.FinishReason),
            ("decision_source", "llm_answer_adequacy_checkpoint")));
}
