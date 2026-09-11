namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal static bool IsDocumentIdentityOnlyContentCandidateForTests(
        string atomicEvidenceMode,
        EvidenceItem item,
        string displayValue)
        => IsDocumentIdentityOnlyContentCandidate(
            atomicEvidenceMode,
            item,
            displayValue);

    private static bool IsDocumentIdentityOnlyContentCandidate(
        string atomicEvidenceMode,
        EvidenceItem item,
        string displayValue)
    {
        if (!string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var displayIdentity = NormalizeDocumentIdentity(displayValue);
        var excerptIdentity = NormalizeDocumentIdentity(item.Excerpt);
        var documentIdentity = NormalizeDocumentIdentity(
            Path.GetFileNameWithoutExtension(
                item.DocName
                ?? item.DocPath
                ?? string.Empty));
        return displayIdentity.Length >= 4
               && string.Equals(
                   displayIdentity,
                   excerptIdentity,
                   StringComparison.Ordinal)
               && documentIdentity.Contains(
                   displayIdentity,
                   StringComparison.Ordinal);
    }

    private static string NormalizeDocumentIdentity(string? value)
        => new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static CandidateCollectionAuditDecision
        ApplyDocumentIdentityContentContract(
            CandidateCollectionAuditDecision decision,
            IReadOnlyList<EvidenceItem> candidates,
            string semanticPlan,
            out string[] suppressedEvidenceIds)
    {
        suppressedEvidenceIds = Array.Empty<string>();
        if (!decision.ProtocolValid
            || decision.ApprovedCandidates.Count == 0
            || !string.Equals(
                ReadAtomicEvidenceMode(semanticPlan),
                "content_claim",
                StringComparison.Ordinal))
        {
            return decision;
        }

        var candidateById = candidates.ToDictionary(
            static item => item.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        suppressedEvidenceIds = decision.ApprovedCandidates
            .Where(approval => candidateById.TryGetValue(
                                   approval.EvidenceId,
                                   out var candidate)
                               && IsDocumentIdentityOnlyContentCandidate(
                                   "content_claim",
                                   candidate,
                                   approval.DisplayValue))
            .Select(static approval => approval.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (suppressedEvidenceIds.Length == 0)
            return decision;

        var suppressed = suppressedEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var classifications = decision.Classifications
            .Select(classification => suppressed.Contains(
                    classification.EvidenceId)
                ? classification with
                {
                    Classification = "document_identity_only"
                }
                : classification)
            .ToArray();
        return decision with
        {
            ApprovedCandidates = decision.ApprovedCandidates
                .Where(approval => !suppressed.Contains(approval.EvidenceId))
                .ToArray(),
            RejectedEvidenceIds = decision.RejectedEvidenceIds
                .Concat(suppressedEvidenceIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Classifications = classifications
        };
    }

    private static int CountApprovedCandidateSources(
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds,
        ISet<string> semanticallyAuditedEvidenceIds,
        ISet<string> semanticallyRejectedEvidenceIds)
        => observedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => semanticallyAuditedEvidenceIds.Contains(id)
                         && !semanticallyRejectedEvidenceIds.Contains(id))
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null
                                  && IsMechanicallyCitableCandidate(item))
            .Cast<EvidenceItem>()
            .DistinctBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            // Exact final labels cannot fill two distinct visible positions.
            // This is a mechanical equality contract only; the LLM still owns
            // every semantic decision and which source bearing a repeated label
            // is preferable.
            .DistinctBy(
                static item => GetEvidenceDisplayValue(item),
                StringComparer.OrdinalIgnoreCase)
            .Count();

    private bool TryApplyCandidateCollectionAudit(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<EvidenceItem> collectionCandidates,
        ISet<string> pendingSemanticCandidateIds,
        ref EvidenceBundle bundle,
        LlmEvidenceWorkspace evidenceWorkspace,
        ISet<string> semanticallyAuditedEvidenceIds,
        ISet<string> semanticallyRejectedEvidenceIds,
        ISet<string> observedEvidenceIdSet,
        List<string> observedEvidenceIds,
        int requiredEvidenceCount,
        bool semanticYieldResolutionEnabled,
        ICollection<SourceBackedAgentMessage> messages,
        SourceBackedIntake intake,
        string semanticPlan,
        IReadOnlyList<string> semanticRowLabels,
        IReadOnlyList<string> semanticColumnLabels,
        List<RetrievalRequest> executedRequests,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool compactSingleFollowUpNextTurn,
        ref bool yieldResolutionNextTurn,
        ref bool semanticYieldResolutionPending,
        ref int semanticYieldResolutionContinuationCount,
        ref int semanticYieldResolutionContinuationLimit,
        int maximumRunTurnCeiling,
        ref int maximumRunTurns,
        ref string? semanticReviewFeedback,
        int auditLlmCallCount,
        int auditCandidateDecisionCount,
        int auditProtocolRepairCount,
        int auditLabelReviewLlmCallCount,
        long? auditElapsedMilliseconds,
        CandidateCollectionAuditDecision? directAuditDecision)
    {
        if (!candidateCollectionOpen)
            return false;
        if (directAuditDecision is null)
            return false;
        var decision = ApplyDocumentIdentityContentContract(
            directAuditDecision,
            collectionCandidates,
            semanticPlan,
            out var mechanicallySuppressedDocumentIdentityIds);

        var pendingCount = pendingSemanticCandidateIds.Count;
        var approvedSourceCountBeforeAudit = CountApprovedCandidateSources(
            bundle,
            observedEvidenceIds,
            semanticallyAuditedEvidenceIds,
            semanticallyRejectedEvidenceIds);
        messages.Add(SourceBackedAgentMessage.Assistant(
            completion.Content,
            completion.ToolCalls));
        if (decision.ProtocolValid)
        {
            bundle = ApplyCandidateDisplayValues(
                bundle,
                decision.ApprovedCandidates);
            ApplyCandidateAuditYieldToRetrievalRequests(
                executedRequests,
                decision);
        }
        var application = ApplyCandidateCollectionAudit(
            decision,
            collectionCandidates,
            bundle,
            evidenceWorkspace,
            semanticallyAuditedEvidenceIds,
            semanticallyRejectedEvidenceIds,
            observedEvidenceIdSet,
            observedEvidenceIds,
            pendingSemanticCandidateIds,
            requiredEvidenceCount);
        var structuredColumnCoverage = EvaluateStructuredColumnCoverage(
            bundle,
            observedEvidenceIds,
            semanticallyAuditedEvidenceIds,
            semanticallyRejectedEvidenceIds,
            semanticRowLabels,
            semanticColumnLabels);
        if (application.HasRequiredEvidence
            && structuredColumnCoverage.Applied
            && !structuredColumnCoverage.HasRequiredCoverage)
        {
            application = application with
            {
                HasRequiredEvidence = false,
                Feedback = BuildStructuredColumnCoverageFeedback(
                    structuredColumnCoverage,
                    semanticColumnLabels,
                    semanticRowLabels.Count)
            };
        }
        semanticReviewFeedback = application.Feedback;
        var semanticAuditZeroYieldResolution =
            semanticYieldResolutionEnabled
            && decision.ProtocolValid
            && ShouldRequestSemanticAuditZeroYieldResolution(
                application.ApprovedSourceCount,
                semanticallyRejectedEvidenceIds.Count,
                requiredEvidenceCount,
                executedRequests.Count,
                application.HasRequiredEvidence);
        if (semanticAuditZeroYieldResolution)
        {
            var continuationLimit =
                DetermineSemanticYieldResolutionContinuationLimit(
                    semanticallyRejectedEvidenceIds.Count,
                    requiredEvidenceCount,
                    executedRequests.Count);
            if (!semanticYieldResolutionPending)
            {
                semanticYieldResolutionContinuationCount = 0;
                semanticYieldResolutionContinuationLimit = continuationLimit;
            }
            else
            {
                semanticYieldResolutionContinuationLimit = Math.Min(
                    semanticYieldResolutionContinuationLimit,
                    continuationLimit);
            }
            compactSingleFollowUpNextTurn = true;
            yieldResolutionNextTurn = true;
            semanticYieldResolutionPending = true;
            semanticReviewFeedback = MergeAgentFeedback(
                semanticReviewFeedback,
                BuildSemanticAuditZeroYieldResolutionMessage(
                    application.ApprovedSourceCount,
                    semanticallyRejectedEvidenceIds.Count,
                    requiredEvidenceCount));
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.semantic_audit_zero_yield.resolution_requested",
                ("turn", turn),
                ("approved", application.ApprovedSourceCount),
                ("rejected", semanticallyRejectedEvidenceIds.Count),
                ("latest_batch_approved", decision.ApprovedEvidenceIds.Count),
                ("latest_batch_rejected", decision.RejectedEvidenceIds.Count),
                ("required", requiredEvidenceCount),
                ("executed_requests", executedRequests.Count),
                ("continuation_limit",
                    semanticYieldResolutionContinuationLimit),
                ("decision_source", "mechanical_yield_trigger")));
        }
        else if (decision.ProtocolValid
                 && (application.HasRequiredEvidence
                     || application.ApprovedSourceCount
                     > approvedSourceCountBeforeAudit))
        {
            if (semanticYieldResolutionPending)
            {
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.semantic_audit_zero_yield.resolution_cleared",
                    ("turn", turn),
                    ("approved_sources_before", approvedSourceCountBeforeAudit),
                    ("approved_sources_after", application.ApprovedSourceCount),
                    ("has_required_evidence", application.HasRequiredEvidence),
                    ("decision_source", "llm_evidence_audit")));
            }
            semanticYieldResolutionPending = false;
            semanticYieldResolutionContinuationCount = 0;
            semanticYieldResolutionContinuationLimit =
                MaximumSemanticYieldResolutionContinuations;
        }
        if (decision.ProtocolValid)
        {
            candidateCollectionOpen = !application.HasRequiredEvidence;
            selectionOnlyNextTurn = application.HasRequiredEvidence;
            if (application.HasRequiredEvidence)
            {
                maximumRunTurns = Math.Min(
                    maximumRunTurnCeiling,
                    Math.Max(maximumRunTurns, turn + 2));
            }
            else if (application.ApprovedSourceCount > approvedSourceCountBeforeAudit)
            {
                maximumRunTurns = Math.Min(
                    maximumRunTurnCeiling,
                    Math.Max(maximumRunTurns, turn + 2));
            }
        }
        else
        {
            // The dedicated audit already includes one bounded protocol repair.
            // Do not reopen the same failing candidate set on later orchestration turns.
            selectionOnlyNextTurn = false;
            maximumRunTurns = Math.Min(maximumRunTurns, turn);
        }
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.candidate_audit.completed",
            ("turn", turn),
            ("protocol_valid", decision.ProtocolValid),
            ("mode", "batched_semantic_decision"),
            ("llm_calls", auditLlmCallCount),
            ("batch_calls", Math.Max(
                0,
                auditLlmCallCount - auditProtocolRepairCount)),
            ("candidate_decisions", auditCandidateDecisionCount),
            ("protocol_repairs", auditProtocolRepairCount),
            ("label_review_llm_calls", auditLabelReviewLlmCallCount),
            ("candidates", collectionCandidates.Count),
            ("withheld", decision.ProtocolValid ? 0 : collectionCandidates.Count),
            ("pending_before_audit", pendingCount),
            ("mechanically_suppressed_duplicate_candidates",
                application.MechanicallySuppressedDuplicateCount),
            ("mechanically_suppressed_document_identity_ids",
                mechanicallySuppressedDocumentIdentityIds),
            ("approved", decision.ApprovedEvidenceIds.Count),
            ("rejected", decision.RejectedEvidenceIds.Count),
            ("approved_evidence_ids", string.Join(",", decision.ApprovedEvidenceIds)),
            ("approved_display_values", decision.ApprovedCandidates
                .Select(static candidate =>
                    candidate.EvidenceId + "=" + candidate.DisplayValue)
                .ToArray()),
            ("approved_column_compatibilities", decision.ApprovedCandidates
                .Where(static candidate =>
                    candidate.CompatibleColumnLabels is not null)
                .Select(static candidate =>
                    candidate.EvidenceId
                    + "="
                    + (candidate.CompatibleColumnLabels!.Count == 0
                        ? "NONE"
                        : string.Join(",", candidate.CompatibleColumnLabels)))
                .ToArray()),
            // The orchestrator sorts trace keys before rendering and bounds the
            // aggregate source_fields payload. The audit_* prefix keeps the
            // semantic decision evidence visible ahead of the verbose label
            // inventory without changing any decision in code.
            ("audit_semantic_decisions", decision.Classifications
                .Select(static candidate =>
                    candidate.EvidenceId
                    + "="
                    + candidate.Classification
                    + ":"
                    + candidate.DisplayValue)
                .ToArray()),
            ("audit_semantic_decision_counts", decision.Classifications
                .GroupBy(
                    static item => item.Classification,
                    StringComparer.Ordinal)
                .Select(static group => group.Key + "=" + group.Count())
                .ToArray()),
            ("candidate_value_options", collectionCandidates
                .Select(candidate =>
                    candidate.EvidenceId
                    + "="
                    + string.Join(
                        " | ",
                        BuildCandidateDisplayValueOptions(candidate)))
                .ToArray()),
            ("normalized_label_indexes", decision.Classifications.Count(
                static item => item.DisplayValueIndexNormalized)),
            ("audit_output", TrimPromptValue(completion.Content, 1200)),
            ("rejected_evidence_ids", string.Join(",", decision.RejectedEvidenceIds)),
            ("approved_sources", application.ApprovedSourceCount),
            ("approved_sources_before_audit", approvedSourceCountBeforeAudit),
            ("required", requiredEvidenceCount),
            ("structured_column_coverage_applied",
                structuredColumnCoverage.Applied),
            ("structured_column_coverage_complete",
                structuredColumnCoverage.HasRequiredCoverage),
            ("structured_column_coverage_matched_cells",
                structuredColumnCoverage.MatchedCellCount),
            ("structured_column_coverage_required_cells",
                structuredColumnCoverage.RequiredCellCount),
            ("structured_column_coverage_annotated_candidates",
                structuredColumnCoverage.AnnotatedCandidateCount),
            ("structured_column_coverage_unannotated_candidates",
                structuredColumnCoverage.UnannotatedCandidateCount),
            ("structured_column_coverage_capacities",
                structuredColumnCoverage.CompatibleCandidateCountByColumn
                    .Select(static pair => pair.Key + "=" + pair.Value)
                    .ToArray()),
            ("collection_open", candidateCollectionOpen),
            ("maximum_run_turns_after_audit", maximumRunTurns),
            ("failure_reason", decision.FailureReason),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("duration_ms", auditElapsedMilliseconds
                ?? completion.ServerPredictedMilliseconds),
            ("server_prompt_ms_total", completion.ServerPromptMilliseconds),
            ("server_predicted_ms_total",
                completion.ServerPredictedMilliseconds),
            ("decision_source", "llm_orchestrator")));
        CompactWorkingMessages(
            messages,
            intake,
            semanticPlan,
            bundle,
            observedEvidenceIds,
            executedRequests,
            evidenceWorkspace,
            semanticReviewFeedback,
            includeEvidenceDetails: !candidateCollectionOpen);
        return true;
    }
}
