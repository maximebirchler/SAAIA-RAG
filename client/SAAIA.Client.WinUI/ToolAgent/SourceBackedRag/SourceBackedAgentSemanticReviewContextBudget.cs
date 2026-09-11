namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SemanticReviewRequest(
        IReadOnlyList<SourceBackedAgentMessage> Messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> Tools,
        int MaximumOutputTokens,
        bool ContextRecoveryUsed,
        string EvidenceContextMode,
        int? ExactInputTokens,
        int CitedEvidenceCharacters);

    private async Task<SemanticReviewRequest> PrepareSemanticReviewRequestAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        CancellationToken ct)
    {
        var citedEvidenceCharacters = SourceContractVerifier
            .ExtractEvidenceIds(draft.Answer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(bundle.ById.ContainsKey)
            .Sum(id => bundle.ById[id].Excerpt?.Length ?? 0);
        var messages = BuildSemanticJudgeMessages(
            intake,
            semanticPlan,
            draft,
            bundle,
            observedEvidenceIds,
            constrainedContext: false,
            emergencyContextRecovery: false,
            includeCompleteCitedEvidence: true);
        var tools = BuildSemanticReviewSubmissionTool();
        var maximumOutputTokens = _options.MaximumSemanticReviewTokens;
        var exactInputTokens = await CountInputTokensAsync(
                messages,
                tools,
                requireToolCall: true,
                ct)
            .ConfigureAwait(false);
        if (!ExceedsContextBudget(exactInputTokens, maximumOutputTokens))
        {
            return new SemanticReviewRequest(
                messages,
                tools,
                maximumOutputTokens,
                ContextRecoveryUsed: false,
                EvidenceContextMode: "complete_cited_evidence",
                ExactInputTokens: exactInputTokens,
                CitedEvidenceCharacters: citedEvidenceCharacters);
        }

        messages = BuildSemanticJudgeMessages(
            intake,
            semanticPlan,
            draft,
            bundle,
            observedEvidenceIds,
            constrainedContext: true,
            emergencyContextRecovery: false,
            includeCompleteCitedEvidence: true);
        maximumOutputTokens = Math.Min(maximumOutputTokens, 512);
        exactInputTokens = await CountInputTokensAsync(
                messages,
                tools,
                requireToolCall: true,
                ct)
            .ConfigureAwait(false);
        if (exactInputTokens is { } measuredInputTokens
            && ExceedsContextBudget(exactInputTokens, maximumOutputTokens))
        {
            var availableOutputTokens =
                _options.MaximumContextTokens
                - measuredInputTokens
                - ContextSafetyReserveTokens;
            if (availableOutputTokens < 64)
            {
                throw new InvalidOperationException(
                    "Semantic review request remains outside the measured "
                    + "context budget after emergency compaction: "
                    + $"input={measuredInputTokens}, "
                    + $"context={_options.MaximumContextTokens}.");
            }

            maximumOutputTokens = Math.Min(
                maximumOutputTokens,
                availableOutputTokens);
        }

        return new SemanticReviewRequest(
            messages,
            tools,
            maximumOutputTokens,
            ContextRecoveryUsed: true,
            EvidenceContextMode: "measured_compact_fallback",
            ExactInputTokens: exactInputTokens,
            CitedEvidenceCharacters: citedEvidenceCharacters);
    }
}
