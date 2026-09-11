namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CandidateAuditExecutionResult(
        SourceBackedAgentCompletion Completion,
        CandidateCollectionAuditDecision Decision,
        int LlmCallCount,
        int CandidateDecisionCount,
        int ProtocolRepairCount,
        int LabelReviewLlmCallCount,
        long ElapsedMilliseconds,
        int ExecutedBatchCount,
        int InputBudgetSplitCount,
        int LargestExecutedBatchCandidateCount,
        int? MaximumMeasuredInputTokens,
        bool ColumnCompatibilityApplied,
        bool ColumnCompatibilityProtocolValid,
        int ColumnCompatibilityLlmCallCount,
        int ColumnCompatibilityBatchCount,
        int ColumnCompatibilityInputBudgetSplitCount,
        long ColumnCompatibilityElapsedMilliseconds,
        string? ColumnCompatibilityFailureReason);

    private sealed record CandidateAuditTurnPreparation(
        IReadOnlyList<EvidenceItem> Candidates)
    {
        public bool IsDedicated => Candidates.Count > 0;
    }

    private CandidateAuditTurnPreparation PrepareCandidateAuditTurn(
        bool auditEnabled,
        bool collectionOpen,
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds,
        IReadOnlySet<string> pendingSemanticCandidateIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds)
    {
        var maximumCandidatesThisTurn = Math.Min(
            _options.MaximumWorkingEvidenceItems,
            _options.MaximumSemanticCandidatesPerAuditTurn);
        if (!auditEnabled || !collectionOpen)
        {
            return new CandidateAuditTurnPreparation(
                Array.Empty<EvidenceItem>());
        }

        var candidates = SelectCandidateCollectionItems(
            bundle,
            observedEvidenceIds,
            pendingSemanticCandidateIds,
            semanticallyRejectedEvidenceIds,
            maximumCandidatesThisTurn);
        return new CandidateAuditTurnPreparation(
            candidates);
    }

    private async Task<(
        SourceBackedAgentCompletion Completion,
        CandidateAuditExecutionResult? Audit)>
        CompleteActionOrCandidateAuditAsync(
            bool dedicatedCandidateAuditTurn,
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            IReadOnlyList<SourceBackedAgentMessage> actionMessages,
            IReadOnlyList<SourceBackedAgentToolDefinition> actionTools,
            int maximumTokens,
            bool requireToolCall,
            CancellationToken ct)
    {
        if (dedicatedCandidateAuditTurn)
        {
            var audit = await CompleteBatchedCandidateAuditAsync(
                    intake,
                    semanticPlan,
                    candidateObjectType,
                    candidateEligibilityRule,
                    semanticRowHeader,
                    semanticRowLabels,
                    semanticColumnLabels,
                    candidates,
                    ct)
                .ConfigureAwait(false);
            return (audit.Completion, audit);
        }

        var completion = await _llm.CompleteAsync(
                actionMessages,
                actionTools,
                maximumTokens,
                ct,
                temperatureOverride: null,
                requireToolCall: requireToolCall)
            .ConfigureAwait(false);
        return (completion, null);
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var materialized = values.ToArray();
        return materialized.All(static value => value.HasValue)
            ? materialized.Sum(static value => value!.Value)
            : null;
    }

    private static double? SumNullable(IEnumerable<double?> values)
    {
        var materialized = values.ToArray();
        return materialized.All(static value => value.HasValue)
            ? materialized.Sum(static value => value!.Value)
            : null;
    }

    private void AddCandidateAuditPerformanceTrace(
        CandidateAuditExecutionResult? execution,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        if (execution is null)
            return;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.candidate_audit.performance",
            ("turn", turn),
            ("candidates", execution.CandidateDecisionCount),
            ("executed_batches", execution.ExecutedBatchCount),
            ("input_budget_splits", execution.InputBudgetSplitCount),
            ("largest_executed_batch_candidates",
                execution.LargestExecutedBatchCandidateCount),
            ("maximum_measured_input_tokens", execution.MaximumMeasuredInputTokens),
            ("maximum_context_tokens", _options.MaximumContextTokens),
            ("llm_calls", execution.LlmCallCount),
            ("label_resolution_llm_calls", execution.LabelReviewLlmCallCount),
            ("semantic_audit_llm_calls", Math.Max(
                0,
                execution.LlmCallCount - execution.LabelReviewLlmCallCount)),
            ("protocol_repairs", execution.ProtocolRepairCount),
            ("column_compatibility_applied",
                execution.ColumnCompatibilityApplied),
            ("column_compatibility_protocol_valid",
                execution.ColumnCompatibilityProtocolValid),
            ("column_compatibility_llm_calls",
                execution.ColumnCompatibilityLlmCallCount),
            ("column_compatibility_batches",
                execution.ColumnCompatibilityBatchCount),
            ("column_compatibility_input_budget_splits",
                execution.ColumnCompatibilityInputBudgetSplitCount),
            ("column_compatibility_duration_ms",
                execution.ColumnCompatibilityElapsedMilliseconds),
            ("column_compatibility_failure_reason",
                execution.ColumnCompatibilityFailureReason),
            ("prompt_tokens_total", execution.Completion.PromptTokens),
            ("completion_tokens_total", execution.Completion.CompletionTokens),
            ("server_prompt_ms_total", execution.Completion.ServerPromptMilliseconds),
            ("server_predicted_ms_total",
                execution.Completion.ServerPredictedMilliseconds),
            ("duration_ms", execution.ElapsedMilliseconds),
            ("decision_source", "llm_orchestrator")));
    }
}
