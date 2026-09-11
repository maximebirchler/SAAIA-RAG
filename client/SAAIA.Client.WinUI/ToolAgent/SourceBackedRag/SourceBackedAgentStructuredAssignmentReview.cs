using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string StructuredAssignmentBatchedReviewFinishReason =
        "structured_assignment_batched_review";

    private sealed record StructuredAssignmentDecision(
        SourceBackedStructuredCellClaim Claim,
        bool Accepted,
        string Reason);

    private sealed record StructuredAssignmentBatchReview(
        bool ProtocolValid,
        IReadOnlyList<StructuredAssignmentDecision> Decisions,
        IReadOnlyList<SourceBackedAgentCompletion> Completions,
        int Attempts);

    private sealed record StructuredAssignmentRevisionExecution(
        SourceBackedAgentCompletion Completion,
        bool ProtocolValid,
        int RevisedCellCount,
        int AvailableCandidateCount,
        string RawOutput,
        string? FailureReason,
        int Attempts,
        bool ContextRecoveryUsed = false,
        int? ExactInputTokens = null);

    private async Task<SemanticReview> ReviewStructuredAssignmentsAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        CancellationToken ct)
        => await ReviewStructuredAssignmentsAsync(
                intake,
                semanticPlan,
                draft,
                bundle,
                observedEvidenceIds,
                reviewOnlyCellIndexes: null,
                ct)
            .ConfigureAwait(false);

    private async Task<SemanticReview> ReviewStructuredAssignmentsAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        IReadOnlySet<int>? reviewOnlyCellIndexes,
        CancellationToken ct)
    {
        var allClaims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(draft.Answer, intake)
            .Where(static claim => claim.EvidenceIds.Count == 1)
            .ToArray();
        var expectedCellCount =
            SourceContractVerifier.CountRequestedStructuredTableCells(intake);
        if (expectedCellCount <= 0 || allClaims.Length != expectedCellCount)
        {
            return await ReviewSemanticsAsync(
                    intake,
                    semanticPlan,
                    draft,
                    bundle,
                    observedEvidenceIds,
                    ct)
                .ConfigureAwait(false);
        }
        var claims = reviewOnlyCellIndexes is null
            ? allClaims
            : allClaims
                .Where((_, index) => reviewOnlyCellIndexes.Contains(index))
                .ToArray();
        if (claims.Length == 0)
        {
            return new SemanticReview(
                "accept",
                new[] { "Aucune cellule revisee ne necessite une nouvelle revue." },
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty,
                FinishReason: StructuredAssignmentBatchedReviewFinishReason);
        }

        var batches = await BuildStructuredAssignmentReviewBatchesAsync(
                intake,
                claims,
                bundle,
                ct)
            .ConfigureAwait(false);
        using var concurrencyGate = new SemaphoreSlim(
            Math.Max(1, _options.MaximumSemanticCandidateAuditConcurrency));

        async Task<StructuredAssignmentBatchReview> ReviewBatchAsync(
            IReadOnlyList<SourceBackedStructuredCellClaim> batchClaims)
        {
            await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await CompleteStructuredAssignmentBatchReviewAsync(
                        intake,
                        batchClaims,
                        bundle,
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                concurrencyGate.Release();
            }
        }

        var batchReviews = await Task.WhenAll(batches.Select(ReviewBatchAsync))
            .ConfigureAwait(false);
        var completions = batchReviews
            .SelectMany(static review => review.Completions)
            .ToArray();
        if (batchReviews.Any(static review => !review.ProtocolValid))
        {
            return new SemanticReview(
                "revise",
                new[]
                {
                    "Une revue semantique en lot n'a pas respecte son protocole apres la reparation bornee."
                },
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Join(
                    Environment.NewLine,
                    completions.Select(static completion => completion.Content)),
                PromptTokens: SumNullable(
                    completions.Select(static completion => completion.PromptTokens)),
                CompletionTokens: SumNullable(
                    completions.Select(static completion => completion.CompletionTokens)),
                FinishReason: StructuredAssignmentBatchedReviewFinishReason,
                Attempts: batchReviews.Sum(static review => review.Attempts),
                TruncationRetryExhausted: true);
        }

        var decisions = batchReviews
            .SelectMany(static review => review.Decisions)
            .OrderBy(static decision =>
                int.Parse(decision.Claim.ClaimRef.AsSpan(1)))
            .ToArray();
        var rejected = decisions
            .Where(static decision => !decision.Accepted)
            .ToArray();
        var reasons = rejected.Length == 0
            ? new[]
            {
                $"{decisions.Length}/{decisions.Length} affectations validees par lots coherents par le LLM."
            }
            : rejected
                .Take(12)
                .Select(static decision =>
                    decision.Claim.RowLabel
                    + "/"
                    + decision.Claim.ColumnHeader
                    + " ["
                    + decision.Claim.EvidenceIds[0]
                    + "]: "
                    + decision.Reason)
                .ToArray();
        return new SemanticReview(
            rejected.Length == 0 ? "accept" : "revise",
            reasons,
            // A placement rejection is not a global evidence rejection. The
            // next LLM revision can still use the item in another position.
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Join(
                Environment.NewLine,
                decisions.Select(static decision =>
                    decision.Claim.ClaimRef
                    + "="
                    + (decision.Accepted ? "ACCEPT" : "REJECT")
                    + ":"
                    + decision.Reason)),
            PromptTokens: SumNullable(
                completions.Select(static completion => completion.PromptTokens)),
            CompletionTokens: SumNullable(
                completions.Select(static completion => completion.CompletionTokens)),
            FinishReason: StructuredAssignmentBatchedReviewFinishReason,
            Attempts: batchReviews.Sum(static review => review.Attempts));
    }

    private async Task<IReadOnlyList<IReadOnlyList<SourceBackedStructuredCellClaim>>>
        BuildStructuredAssignmentReviewBatchesAsync(
            SourceBackedIntake intake,
            IReadOnlyList<SourceBackedStructuredCellClaim> claims,
            EvidenceBundle bundle,
            CancellationToken ct)
    {
        // Without a real tokenizer, retain the conservative row batches used by
        // deterministic test doubles and non-native clients. Native clients can
        // instead review the whole grid whenever its measured request fits.
        if (_llm is not ISourceBackedAgentInputTokenCounter)
        {
            return claims
                .GroupBy(
                    static claim => claim.RowLabel,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                    (IReadOnlyList<SourceBackedStructuredCellClaim>)group.ToArray())
                .ToArray();
        }

        if (await FitsStructuredAssignmentReviewContextAsync(
                intake,
                claims,
                bundle,
                ct)
            .ConfigureAwait(false))
        {
            return new[] { claims };
        }

        // If a global review is too large, keep equal semantic roles together.
        // A column batch lets the LLM compare like-for-like values while every
        // claim still carries its row label. Oversized columns are halved only
        // for the measured mechanical context limit.
        var pending = new Queue<IReadOnlyList<SourceBackedStructuredCellClaim>>(
            claims
                .GroupBy(
                    static claim => claim.ColumnHeader,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                    (IReadOnlyList<SourceBackedStructuredCellClaim>)group.ToArray()));
        var batches = new List<IReadOnlyList<SourceBackedStructuredCellClaim>>();
        while (pending.Count > 0)
        {
            var batch = pending.Dequeue();
            if (batch.Count <= 1
                || await FitsStructuredAssignmentReviewContextAsync(
                        intake,
                        batch,
                        bundle,
                        ct)
                    .ConfigureAwait(false))
            {
                batches.Add(batch);
                continue;
            }

            var split = batch.Count / 2;
            pending.Enqueue(batch.Take(split).ToArray());
            pending.Enqueue(batch.Skip(split).ToArray());
        }

        return batches;
    }

    private async Task<bool> FitsStructuredAssignmentReviewContextAsync(
        SourceBackedIntake intake,
        IReadOnlyList<SourceBackedStructuredCellClaim> claims,
        EvidenceBundle bundle,
        CancellationToken ct)
    {
        var messages = BuildStructuredAssignmentBatchReviewMessages(
            intake,
            claims,
            bundle,
            repairProtocol: false,
            usesStructuredContract:
                _llm is ISourceBackedAgentStructuredLlmClient);
        var exactInputTokens = await CountInputTokensAsync(
                messages,
                Array.Empty<SourceBackedAgentToolDefinition>(),
                requireToolCall: false,
                ct)
            .ConfigureAwait(false);
        return !ExceedsContextBudget(
            exactInputTokens,
            ResolveStructuredAssignmentBatchReviewOutputTokens(claims.Count));
    }

    private static IReadOnlySet<int>? GetStructuredAssignmentRejectedCellIndexes(
        SemanticReview? review)
    {
        if (review is null
            || !string.Equals(
                review.FinishReason,
                StructuredAssignmentBatchedReviewFinishReason,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Regex.Matches(
                review.RawOutput ?? string.Empty,
                @"(?m)^C(\d{2,})=REJECT:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => int.Parse(match.Groups[1].Value) - 1)
            .Where(static index => index >= 0)
            .ToHashSet();
    }
}
