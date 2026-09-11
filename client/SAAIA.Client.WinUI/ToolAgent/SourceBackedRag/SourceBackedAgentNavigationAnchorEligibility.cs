namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticNavigationAnchorEligibilityHint =
        "semanticNavigationAnchorEligible";

    private sealed record NavigationAnchorEligibilityAuditExecution(
        EvidenceBundle Bundle,
        bool Attempted,
        bool ProtocolValid,
        int CandidateCount,
        int EligibleCount,
        int LlmCallCount,
        long ElapsedMilliseconds,
        int? PromptTokens,
        int? CompletionTokens,
        string FailureReason);

    private async Task<NavigationAnchorEligibilityAuditExecution>
        CompleteNavigationAnchorEligibilityAuditAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            ISet<string> auditedNavigationEvidenceIds,
            CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidateObjectType)
            || string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            return NoNavigationAnchorEligibilityAudit(bundle);
        }

        var pendingIds = SelectPendingNavigationEvidenceIds(
                bundle,
                resolvedNavigationEvidenceIds,
                _options.MaximumWorkingEvidenceItems,
                requireResolvableTarget: true)
            .Where(id => !auditedNavigationEvidenceIds.Contains(id))
            .Take(Math.Max(
                1,
                _options.MaximumSemanticCandidatesPerAuditTurn))
            .ToArray();
        if (pendingIds.Length == 0)
            return NoNavigationAnchorEligibilityAudit(bundle);

        var pendingIdSet = pendingIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var candidates = bundle.Items
            .Where(item => pendingIdSet.Contains(item.EvidenceId))
            .ToArray();
        if (candidates.Length == 0)
            return NoNavigationAnchorEligibilityAudit(bundle);

        CandidateAuditExecutionResult execution;
        try
        {
            execution = await CompleteBatchedCandidateAuditAsync(
                    intake,
                    semanticPlan,
                    candidateObjectType,
                    "SAAIA_SOURCE_BACKED_STEP=NavigationAnchorEligibilityAudit\n"
                    + candidateEligibilityRule,
                    string.Empty,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    candidates,
                    ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            foreach (var evidenceId in pendingIds)
                auditedNavigationEvidenceIds.Add(evidenceId);
            return new NavigationAnchorEligibilityAuditExecution(
                ApplyNavigationAnchorEligibilityAnnotations(
                    bundle,
                    pendingIdSet,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                Attempted: true,
                ProtocolValid: false,
                candidates.Length,
                EligibleCount: 0,
                LlmCallCount: 0,
                ElapsedMilliseconds: 0,
                PromptTokens: null,
                CompletionTokens: null,
                FailureReason: "navigation_anchor_audit_transport_error:"
                               + ex.GetType().Name);
        }

        foreach (var evidenceId in pendingIds)
            auditedNavigationEvidenceIds.Add(evidenceId);
        var eligibleIds = execution.Decision.ProtocolValid
            ? execution.Decision.ApprovedCandidates
                .Select(static approval => approval.EvidenceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new NavigationAnchorEligibilityAuditExecution(
            ApplyNavigationAnchorEligibilityAnnotations(
                bundle,
                pendingIdSet,
                eligibleIds),
            Attempted: true,
            execution.Decision.ProtocolValid,
            candidates.Length,
            eligibleIds.Count,
            execution.LlmCallCount,
            execution.ElapsedMilliseconds,
            execution.Completion.PromptTokens,
            execution.Completion.CompletionTokens,
            execution.Decision.FailureReason ?? string.Empty);
    }

    private static EvidenceBundle ApplyNavigationAnchorEligibilityAnnotations(
        EvidenceBundle bundle,
        IReadOnlySet<string> auditedEvidenceIds,
        IReadOnlySet<string> eligibleEvidenceIds)
        => bundle with
        {
            Items = bundle.Items
                .Select(item =>
                {
                    if (!auditedEvidenceIds.Contains(item.EvidenceId))
                        return item;
                    var hints = new Dictionary<string, string>(
                        item.SelectionHints,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [SemanticNavigationAnchorEligibilityHint] =
                            eligibleEvidenceIds.Contains(item.EvidenceId)
                                ? "true"
                                : "false"
                    };
                    return item with { SelectionHints = hints };
                })
                .ToArray()
        };

    private static bool IsNavigationAnchorSemanticallyAvailable(
        EvidenceItem item)
        => !item.SelectionHints.TryGetValue(
               SemanticNavigationAnchorEligibilityHint,
               out var eligible)
           || string.Equals(
               eligible,
               "true",
               StringComparison.OrdinalIgnoreCase);

    private void TraceNavigationAnchorEligibilityAudit(
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        NavigationAnchorEligibilityAuditExecution execution)
    {
        if (!execution.Attempted)
            return;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.navigation_anchor_audit.completed",
            ("turn", turn),
            ("candidates", execution.CandidateCount),
            ("eligible", execution.EligibleCount),
            ("withheld", execution.CandidateCount - execution.EligibleCount),
            ("protocol_valid", execution.ProtocolValid),
            ("llm_calls", execution.LlmCallCount),
            ("duration_ms", execution.ElapsedMilliseconds),
            ("prompt_tokens", execution.PromptTokens),
            ("completion_tokens", execution.CompletionTokens),
            ("failure_reason", execution.FailureReason),
            ("decision_source", "llm_semantic_anchor_audit")));
    }

    private static NavigationAnchorEligibilityAuditExecution
        NoNavigationAnchorEligibilityAudit(EvidenceBundle bundle)
        => new(
            bundle,
            Attempted: false,
            ProtocolValid: true,
            CandidateCount: 0,
            EligibleCount: 0,
            LlmCallCount: 0,
            ElapsedMilliseconds: 0,
            PromptTokens: null,
            CompletionTokens: null,
            FailureReason: string.Empty);
}
