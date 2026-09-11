namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record FastEvidenceReview(
        string Decision,
        string NextCapability,
        IReadOnlyList<string> EvidenceIds,
        IReadOnlyList<string> LeadEvidenceIds,
        string AnchorExcerpt,
        string Missing,
        string Assessment,
        string Answer,
        bool ProtocolValid,
        bool AnchorVerified,
        SourceBackedAgentCompletion Completion,
        int PromptCharacters = 0,
        int EvidenceContextCharacters = 0,
        int CandidateCount = 0,
        int SourceWindowItemCount = 0,
        long ElapsedMilliseconds = 0,
        string ClarificationQuestion = "",
        string SingleSelectionScopeDecision = "not_needed",
        string SingleSelectionScopeBasis = "",
        bool SingleSelectionScopeProtocolValid = true,
        long SingleSelectionScopeElapsedMilliseconds = 0,
        bool SingleSelectionScopeMechanicallyNormalized = false,
        IReadOnlyList<string>? PresentedCandidateIds = null,
        IReadOnlyList<string>? PresentedEvidenceIds = null,
        bool AnswerMechanicallyRedirectedToWriter = false,
        bool AnswerSemanticallyFinal = false,
        bool SemanticAnswerTransactionAttempted = false,
        bool SemanticResolutionWriterReviewAttempted = false,
        int EvidencePoolBudget = 0,
        int EvidencePoolEligibleItemCount = 0,
        int EvidencePoolTruncatedItemCount = 0,
        string AnswerAdequacy = "",
        bool RequestedDeliverableComplete = false,
        bool MissingUserInputPreventsUniqueResult = false,
        string VisibleContextEvidenceId = "NONE",
        string SingleSelectionScopeReason = "",
        bool SingleSelectionScopeTerminalBudgetOnly = false)
    {
        public bool Ready =>
            ProtocolValid
            && string.Equals(
                Decision,
                "ready",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NextCapability,
                "write",
                StringComparison.OrdinalIgnoreCase);
    }
}
