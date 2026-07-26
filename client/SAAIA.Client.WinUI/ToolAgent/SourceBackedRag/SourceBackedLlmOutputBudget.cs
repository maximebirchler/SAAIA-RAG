namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedLlmOutputBudget
{
    public static int? TryResolveJsonMaxTokens(string prompt, int configuredMaxTokens)
    {
        if (string.IsNullOrWhiteSpace(prompt)
            || !prompt.Contains("SAAIA_SOURCE_BACKED_STEP=", StringComparison.OrdinalIgnoreCase))
            return null;

        var ceiling = Math.Clamp(configuredMaxTokens, 512, 1600);
        var requested = ResolveStepBudget(prompt);
        var floor = ContainsExactStep(prompt, "EvidenceStatusReviewActionRepair")
            ? 96
            : ContainsExactStep(prompt, "StructuredValueTypeFitDecisionBatch")
                ? 96
            : ContainsExactStep(prompt, "StructuredValueTypeFitAtomicRepair")
                ? 160
                : 256;
        return Math.Clamp(Math.Min(requested, ceiling), floor, 1600);
    }

    private static int ResolveStepBudget(string prompt)
    {
        // Live planner generations have completed between 304 and 422 output
        // tokens. A 640-token no-EOS generation can exceed the Planner's
        // transport timeout on the supported local 3B runtime, so keep a
        // measured margin while bounding only the initial Planner contract.
        if (ContainsExactStep(prompt, "Planner"))
            return 480;

        // The full intake audit serializes both typed axes, but leaving it on
        // the generic 640-token Planner budget lets a no-EOS local generation
        // overrun its dedicated transport timeout. Keep the same bounded room
        // as the measured initial Planner contract.
        if (ContainsExactStep(prompt, "PlannerIntakeReview"))
            return 480;

        if (ContainsExactStep(prompt, "PlannerIntakeColumnAxisAdjudication")
            || ContainsExactStep(prompt, "PlannerIntakeColumnAxisContractRepair"))
        {
            return 256;
        }

        if (ContainsExactStep(prompt, "PlannerIntakeShapeAdjudication"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerIntakeColumnAxisSelectionPatch"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerIntakeRowAxisHeaderRolePatch"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerIntakeColumnAxisAnchorPatch"))
            return 480;

        if (ContainsExactStep(prompt, "PlannerIntakeColumnAxisCompletenessReview"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerIntakeColumnAxisMissingAnchors"))
            return 256;

        // The four-facet structured-term schema needs more room than its visible
        // JSON character count suggests with the local model tokenizer. A live
        // A measured run stopped exactly at the former 480-token ceiling with an
        // incomplete object after adding the three typed term arrays. Keep one
        // measured safety margin for the initial call and both focused repairs.
        if (ContainsExactStep(prompt, "PlannerCompactFacetPlanContractRepair")
            || ContainsExactStep(prompt, "PlannerCompactFacetPlanFormatRepair")
            || ContainsExactStep(prompt, "PlannerCompactFacetPlan"))
        {
            return 640;
        }

        if (ContainsExactStep(prompt, "PlannerAcceptedScopeCoherenceReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedScopeCoherenceReview")
            || ContainsExactStep(prompt, "PlannerAcceptedScopeAuditContractRetry")
            || ContainsExactStep(prompt, "PlannerAcceptedScopeAuditContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedScopeAudit"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerAcceptedQueryAuditContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryAudit"))
            return 640;

        if (ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefDeliverableReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefDeliverableReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefDeliverableReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReusableItemClassReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReusableItemClassReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReusableItemClassReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefFacetCoverageReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefFacetCoverageReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefFacetCoverageReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributesReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributesReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributesReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributeCandidateValuesReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributeCandidateValuesReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefAttributeCandidateValuesReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefSemanticRepairContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefSemanticRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBriefContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQuerySourceDomainBrief"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerAcceptedQueryYieldReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldReviewFinal")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldRetryReview")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldReview"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerAcceptedQueryYieldItemReviewContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldItemReview"))
            return 256;

        if (ContainsExactStep(prompt, "PlannerAcceptedQueryYieldRetryRepairContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldRetryRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldRepairContractRepair")
            || ContainsExactStep(prompt, "PlannerAcceptedQueryYieldRepair"))
            return 480;

        if (ContainsStep(prompt, "PlannerRowIndependentQueryRepair"))
            return 480;

        if (ContainsStep(prompt, "PlannerFacetAssignmentPatch"))
            return 256;

        if (ContainsStep(prompt, "Planner"))
            return 640;

        if (ContainsStep(prompt, "EvidenceJudgeActionRepair"))
            return 400;

        if (ContainsExactStep(prompt, "CanonicalCardShortlistRetry")
            || ContainsExactStep(prompt, "CanonicalCardShortlist"))
            return 320;

        if (ContainsExactStep(prompt, "EvidenceStatusReviewActionRepair"))
            return 96;

        if (ContainsStep(prompt, "EvidenceStatusReviewFormatRepair"))
            return 320;

        if (ContainsStep(prompt, "EvidenceStatusReview"))
            return 320;

        if (ContainsStep(prompt, "EvidenceJudgeFinalSelectionAtomicRepair"))
            return 256;

        if (ContainsStep(prompt, "EvidenceJudgeFinalSelectionRepair"))
            return 360;

        if (ContainsStep(prompt, "EvidenceJudgeSelectionRepair"))
            return 400;

        if (ContainsStep(prompt, "EvidenceJudgeFormatRepair"))
            return 420;

        if (ContainsStep(prompt, "EvidenceJudgeBudgetStopRepair"))
            return 420;

        if (ContainsStep(prompt, "EvidenceJudgeBudgetStop"))
            return 420;

        if (ContainsStep(prompt, "EvidenceJudge"))
            return 400;

        if (ContainsExactStep(prompt, "CanonicalColumnSemanticRoles"))
            return 256;

        if (ContainsStep(prompt, "StructuredValueTypeFitAtomicRepair"))
            return 160;

        if (ContainsExactStep(prompt, "StructuredValueTypeFitDecisionBatch"))
            return 128;

        if (ContainsStep(prompt, "StructuredValueTypeFitJudge"))
            return 640;

        if (ContainsExactStep(prompt, "StructuredValueTypeFollowUpRepair"))
            return 256;

        if (ContainsStep(prompt, "StructuredValueTypeRepair"))
            return 1000;

        if (ContainsStep(prompt, "StructuredThinCellAtomicRepair"))
            return 480;

        if (ContainsStep(prompt, "AnswerAdequacyRevisionValidationFormatRepair"))
            return 520;

        if (ContainsStep(prompt, "AnswerAdequacyRevisionValidation"))
            return 640;

        if (ContainsStep(prompt, "AnswerAdequacyActionContractRetry"))
            return 640;

        if (ContainsStep(prompt, "AnswerAdequacyJudge"))
            return 1000;

        if (ContainsStep(prompt, "StructuredCellRepair"))
            return 1000;

        if (ContainsStep(prompt, "Repair"))
            return 900;

        if (ContainsStep(prompt, "Writer"))
            return 900;

        return 800;
    }

    private static bool ContainsStep(string prompt, string step)
        => prompt.Contains("SAAIA_SOURCE_BACKED_STEP=" + step, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExactStep(string prompt, string step)
    {
        var marker = "SAAIA_SOURCE_BACKED_STEP=" + step;
        var searchStart = 0;
        while (searchStart < prompt.Length)
        {
            var index = prompt.IndexOf(marker, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var end = index + marker.Length;
            if (end == prompt.Length || prompt[end] is '\r' or '\n')
                return true;

            searchStart = end;
        }
        return false;
    }
}
