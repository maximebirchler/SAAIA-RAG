using System.Diagnostics;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitCandidateBatchAuditToolName =
        "submit_candidate_batch_audit";

    private sealed record CandidateAuditBatchResult(
        SourceBackedAgentCompletion Completion,
        CandidateCollectionAuditDecision Decision,
        int LlmCallCount,
        int ProtocolRepairCount,
        int LabelReviewLlmCallCount);

    private async Task<CandidateAuditExecutionResult>
        CompleteBatchedCandidateAuditAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var pendingBatches = new Queue<IReadOnlyList<EvidenceItem>>();
        EnqueueCandidateAuditBatches(
            pendingBatches,
            candidates,
            Math.Max(
                1,
                _options.MaximumSemanticCandidatesPerAuditBatch),
            _options.SemanticCandidateLabelResolutionEnabled);
        var completedBatches = new List<CandidateAuditBatchResult>();
        var inputBudgetSplitCount = 0;
        var largestExecutedBatchCandidateCount = 0;
        int? maximumMeasuredInputTokens = null;

        while (pendingBatches.Count > 0)
        {
            var batch = pendingBatches.Dequeue();
            var messages = BuildCandidateBatchAuditMessages(
                intake,
                semanticPlan,
                candidateObjectType,
                candidateEligibilityRule,
                semanticRowHeader,
                semanticRowLabels,
                semanticColumnLabels,
                batch,
                useOrderedDecisionContract:
                    _llm is ISourceBackedAgentStructuredLlmClient);
            var tools = BuildCandidateBatchAuditTool(batch);
            var measurementTools = _llm is ISourceBackedAgentStructuredLlmClient
                ? Array.Empty<SourceBackedAgentToolDefinition>()
                : tools;
            var maximumOutputTokens = ResolveCandidateBatchOutputTokens(
                batch.Count);
            var measuredInputTokens = await CountInputTokensAsync(
                    messages,
                    measurementTools,
                    requireToolCall: measurementTools.Count > 0,
                    ct)
                .ConfigureAwait(false);
            if (measuredInputTokens is { } measured)
            {
                maximumMeasuredInputTokens = Math.Max(
                    maximumMeasuredInputTokens ?? 0,
                    measured);
            }
            if (ExceedsContextBudget(
                    measuredInputTokens,
                    maximumOutputTokens)
                && batch.Count > 1)
            {
                inputBudgetSplitCount++;
                var split = batch.Count / 2;
                pendingBatches.Enqueue(batch.Take(split).ToArray());
                pendingBatches.Enqueue(batch.Skip(split).ToArray());
                continue;
            }
            largestExecutedBatchCandidateCount = Math.Max(
                largestExecutedBatchCandidateCount,
                batch.Count);

            CandidateLabelResolutionResult? labelResolution = null;
            var auditedBatch = batch;
            var useResolvedLabelAudit = false;
            if (_options.SemanticCandidateLabelResolutionEnabled)
            {
                var candidatesToResolve = batch
                    .Where(candidate =>
                        !HasResolvedSourceAnchorLabel(candidate)
                        && BuildCandidateBatchDisplayValueOptions(candidate).Count > 1)
                    .ToArray();
                if (candidatesToResolve.Length > 0)
                {
                    labelResolution = await CompleteCandidateLabelResolutionAsync(
                            candidatesToResolve,
                            ct)
                        .ConfigureAwait(false);
                    if (!labelResolution.Decision.ProtocolValid)
                    {
                        completedBatches.Add(new CandidateAuditBatchResult(
                            AggregateCandidateBatchCompletions(
                                labelResolution.Completions,
                                string.Empty,
                                "protocol_error"),
                            labelResolution.Decision,
                            labelResolution.Completions.Count,
                            Math.Max(0, labelResolution.Completions.Count - 1),
                            labelResolution.Completions.Count));
                        break;
                    }
                    auditedBatch = ApplyCandidateLabelResolutions(
                        batch,
                        labelResolution.Decision.ApprovedCandidates);
                }

                if (auditedBatch.Count > 0
                    && auditedBatch.All(HasResolvedSourceAnchorLabel))
                {
                    messages = BuildResolvedCandidateAuditMessages(
                        intake,
                        semanticPlan,
                        candidateObjectType,
                        candidateEligibilityRule,
                        semanticRowHeader,
                        semanticRowLabels,
                        semanticColumnLabels,
                        auditedBatch);
                    tools = BuildResolvedCandidateAuditTool(auditedBatch);
                    useResolvedLabelAudit = true;
                }
            }

            var completed = auditedBatch.Count == 0
                ? new CandidateAuditBatchResult(
                    AggregateCandidateBatchCompletions(
                        Array.Empty<SourceBackedAgentCompletion>(),
                        "AUCUN",
                        "stop"),
                    new CandidateCollectionAuditDecision(
                        true,
                        Array.Empty<CandidateCollectionApproval>(),
                        Array.Empty<string>(),
                        Array.Empty<CandidateCollectionClassification>(),
                        null),
                    0,
                    0,
                    0)
                : await CompleteCandidateAuditBatchAsync(
                    intake,
                    semanticPlan,
                    candidateObjectType,
                    candidateEligibilityRule,
                    semanticRowHeader,
                    semanticRowLabels,
                    semanticColumnLabels,
                    auditedBatch,
                    messages,
                    tools,
                    maximumOutputTokens,
                    useResolvedLabelAudit,
                    reviewDisplayValueAfterAudit:
                        !_options.SemanticCandidateLabelResolutionEnabled,
                    ct)
                .ConfigureAwait(false);
            var identityCheckedDecision =
                ApplyExactLayoutAxisIdentityContract(
                    completed.Decision,
                    semanticRowHeader,
                    semanticRowLabels,
                    semanticColumnLabels);
            if (!ReferenceEquals(identityCheckedDecision, completed.Decision))
            {
                completed = completed with
                {
                    Completion = completed.Completion with
                    {
                        Content = identityCheckedDecision.ProtocolValid
                            ? FormatCandidateAuditDecision(identityCheckedDecision)
                            : string.Empty
                    },
                    Decision = identityCheckedDecision
                };
            }
            if (labelResolution is not null
                && labelResolution.Completions.Count > 0)
            {
                var combinedCompletions = labelResolution.Completions
                    .Concat(new[] { completed.Completion })
                    .ToArray();
                completed = new CandidateAuditBatchResult(
                    AggregateCandidateBatchCompletions(
                        combinedCompletions,
                        completed.Decision.ProtocolValid
                            ? FormatCandidateAuditDecision(completed.Decision)
                            : string.Empty,
                        completed.Decision.ProtocolValid
                            ? "stop"
                            : "protocol_error"),
                    completed.Decision,
                    completed.LlmCallCount + labelResolution.Completions.Count,
                    completed.ProtocolRepairCount
                    + Math.Max(0, labelResolution.Completions.Count - 1),
                    labelResolution.Completions.Count);
            }
            completedBatches.Add(completed);
            if (!completed.Decision.ProtocolValid)
                break;
        }

        stopwatch.Stop();
        var completions = completedBatches
            .Select(static batch => batch.Completion)
            .ToList();
        var invalidBatch = completedBatches.FirstOrDefault(static batch =>
            !batch.Decision.ProtocolValid);
        CandidateCollectionAuditDecision decision;
        if (invalidBatch is not null)
        {
            decision = new CandidateCollectionAuditDecision(
                false,
                Array.Empty<CandidateCollectionApproval>(),
                Array.Empty<string>(),
                Array.Empty<CandidateCollectionClassification>(),
                invalidBatch.Decision.FailureReason);
        }
        else
        {
            var approvals = completedBatches
                .SelectMany(static batch =>
                    batch.Decision.ApprovedCandidates)
                .ToArray();
            var rejectedIds = completedBatches
                .SelectMany(static batch =>
                    batch.Decision.RejectedEvidenceIds)
                .ToArray();
            var classifications = completedBatches
                .SelectMany(static batch =>
                    batch.Decision.Classifications)
                .ToArray();
            decision = new CandidateCollectionAuditDecision(
                true,
                approvals,
                rejectedIds,
                classifications,
                null);
        }

        var columnCompatibility =
            await CompleteCandidateColumnCompatibilityAsync(
                    intake,
                    semanticRowLabels,
                    semanticColumnLabels,
                    candidates,
                    decision.ApprovedCandidates,
                    ct)
                .ConfigureAwait(false);
        completions.AddRange(columnCompatibility.Completions);
        if (decision.ProtocolValid
            && columnCompatibility.Applied
            && columnCompatibility.ProtocolValid)
        {
            decision = decision with
            {
                ApprovedCandidates = decision.ApprovedCandidates
                    .Select(approval => approval with
                    {
                        CompatibleColumnLabels =
                            columnCompatibility
                                .CompatibleColumnLabelsByEvidenceId
                                .GetValueOrDefault(
                                    approval.EvidenceId,
                                    Array.Empty<string>())
                    })
                    .ToArray()
            };
        }

        return new CandidateAuditExecutionResult(
            AggregateCandidateBatchCompletions(
                completions,
                decision.ProtocolValid
                    ? FormatCandidateAuditDecision(decision)
                    : string.Empty,
                decision.ProtocolValid ? "stop" : "protocol_error"),
            decision,
            completedBatches.Sum(static batch => batch.LlmCallCount)
            + columnCompatibility.Completions.Count,
            candidates.Count,
            completedBatches.Sum(static batch => batch.ProtocolRepairCount)
            + columnCompatibility.ProtocolRepairCount,
            completedBatches.Sum(static batch =>
                batch.LabelReviewLlmCallCount),
            stopwatch.ElapsedMilliseconds,
            completedBatches.Count,
            inputBudgetSplitCount,
            largestExecutedBatchCandidateCount,
            maximumMeasuredInputTokens,
            columnCompatibility.Applied,
            columnCompatibility.ProtocolValid,
            columnCompatibility.Completions.Count,
            columnCompatibility.ExecutedBatchCount,
            columnCompatibility.InputBudgetSplitCount,
            columnCompatibility.ElapsedMilliseconds,
            columnCompatibility.FailureReason);
    }

    private static void EnqueueCandidateAuditBatches(
        Queue<IReadOnlyList<EvidenceItem>> destination,
        IReadOnlyList<EvidenceItem> candidates,
        int maximumBatchSize,
        bool separateByLabelResolutionProfile)
    {
        if (!separateByLabelResolutionProfile)
        {
            EnqueueCandidateAuditGroup(destination, candidates, maximumBatchSize);
            return;
        }

        // A resolved source identity enables the compact semantic classification
        // contract. One unresolved neighbour must not downgrade every canonical
        // candidate in the same retrieval result to the less precise binary
        // contract. Keep the three mechanical resolution profiles homogeneous;
        // semantic suitability is still decided exclusively by the LLM.
        EnqueueCandidateAuditGroup(
            destination,
            candidates.Where(HasResolvedSourceAnchorLabel).ToArray(),
            maximumBatchSize);
        EnqueueCandidateAuditGroup(
            destination,
            candidates.Where(candidate =>
                    !HasResolvedSourceAnchorLabel(candidate)
                    && BuildCandidateBatchDisplayValueOptions(candidate).Count > 1)
                .ToArray(),
            maximumBatchSize);
        EnqueueCandidateAuditGroup(
            destination,
            candidates.Where(candidate =>
                    !HasResolvedSourceAnchorLabel(candidate)
                    && BuildCandidateBatchDisplayValueOptions(candidate).Count <= 1)
                .ToArray(),
            maximumBatchSize);
    }

    private static void EnqueueCandidateAuditGroup(
        Queue<IReadOnlyList<EvidenceItem>> destination,
        IReadOnlyList<EvidenceItem> candidates,
        int maximumBatchSize)
    {
        foreach (var batch in candidates.Chunk(Math.Max(1, maximumBatchSize)))
            destination.Enqueue(batch);
    }

    private async Task<CandidateAuditBatchResult>
        CompleteCandidateAuditBatchAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            IReadOnlyList<SourceBackedAgentMessage> initialMessages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maximumOutputTokens,
            bool useResolvedLabelAudit,
            bool reviewDisplayValueAfterAudit,
            CancellationToken ct)
    {
        var completions = new List<SourceBackedAgentCompletion>(2);
        CandidateCollectionAuditDecision? decision = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var messages = initialMessages.ToList();
            if (attempt > 1)
            {
                messages.Add(SourceBackedAgentMessage.User(
                    "REPARATION DU CONTRAT: "
                    + (decision?.FailureReason ?? "sortie absente")
                    + ". Retourne une decision JSON entiere valide pour chaque cle "
                    + "cN, sans texte supplementaire."));
            }

            var completion = _llm is ISourceBackedAgentStructuredLlmClient
                structuredLlm
                ? await structuredLlm.CompleteStructuredAsync(
                        messages,
                        useResolvedLabelAudit
                            ? BuildResolvedCandidateAuditContract(candidates)
                            : BuildCandidateBatchAuditContract(candidates),
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        messages,
                        tools,
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            completions.Add(completion);
            decision = useResolvedLabelAudit
                ? ReadResolvedCandidateAuditDecision(completion, candidates)
                : ReadCandidateBatchAuditDecision(completion, candidates);
            if (decision.ProtocolValid)
                break;
        }

        var labelReviewLlmCallCount = 0;
        var labelReviewRepairCount = 0;
        if (decision!.ProtocolValid && reviewDisplayValueAfterAudit)
        {
            var selectedDisplayValueById = decision.ApprovedCandidates
                .ToDictionary(
                    static approval => approval.EvidenceId,
                    static approval => approval.DisplayValue,
                    StringComparer.OrdinalIgnoreCase);
            var rejectedCandidateIds = decision.RejectedEvidenceIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var labelReviewCandidates = candidates
                .Where(candidate =>
                {
                    if (HasResolvedSourceAnchorLabel(candidate))
                        return false;
                    var options = BuildCandidateBatchDisplayValueOptions(candidate);
                    if (options.Count <= 1)
                        return false;
                    if (rejectedCandidateIds.Contains(candidate.EvidenceId))
                        return true;
                    if (!selectedDisplayValueById.TryGetValue(
                            candidate.EvidenceId,
                            out var selectedDisplayValue))
                    {
                        return false;
                    }
                    return !string.Equals(
                        selectedDisplayValue,
                        options[0],
                        StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            if (labelReviewCandidates.Length > 0)
            {
                var labelReview = await CompleteCandidateDisplayValueReviewAsync(
                        intake,
                        semanticPlan,
                        candidateObjectType,
                        candidateEligibilityRule,
                        semanticRowHeader,
                        semanticRowLabels,
                        semanticColumnLabels,
                        labelReviewCandidates,
                        ct)
                    .ConfigureAwait(false);
                completions.AddRange(labelReview.Completions);
                labelReviewLlmCallCount = labelReview.Completions.Count;
                labelReviewRepairCount = Math.Max(
                    0,
                    labelReview.Completions.Count - 1);
                decision = ApplyCandidateDisplayValueReview(
                    decision,
                    labelReview.Decision,
                    labelReviewCandidates);
            }
        }

        return new CandidateAuditBatchResult(
            AggregateCandidateBatchCompletions(
                completions,
                decision!.ProtocolValid
                    ? FormatCandidateAuditDecision(decision)
                    : string.Empty,
                decision.ProtocolValid ? "stop" : "protocol_error"),
            decision,
            completions.Count,
            Math.Max(0, completions.Count - 1 - labelReviewLlmCallCount)
            + labelReviewRepairCount,
            labelReviewLlmCallCount);
    }

}
