using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<SourceBackedAgentV2Runner.SemanticReview> ReviewEvidenceOverviewFinalDraftAsync(
        string userRequest, string responseLanguage, string documentPath,
        WriterDraft draft, EvidenceBundle bundle, CancellationToken ct)
    {
        var reviewer = await CreateSourceBackedAgentV2RunnerAsync(
            "rag.summarize_doc", "overview_final_review", ct, null, null).ConfigureAwait(false);
        var intake = new SourceBackedIntake(userRequest, "document_summary",
            Array.Empty<string>(), Array.Empty<string>(), false, responseLanguage,
            QuestionFocus: "content", RequestedDocumentName: documentPath);
        using var terminalReview = SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
        var review = await reviewer.ReviewSemanticsAsync(intake,
            "MODE_PREUVES_ATOMIQUES: content_claim", draft, bundle,
            bundle.Items.Select(item => item.EvidenceId).ToArray(), ct).ConfigureAwait(false);
        EmitRagTrace("document_overview.semantic_review.completed",
            ("decision", review.Decision), ("reasons", review.Reasons),
            ("attempts", review.Attempts), ("evidence_context_mode", review.EvidenceContextMode),
            ("cited_evidence_characters", review.CitedEvidenceCharacters),
            ("exact_input_tokens", review.ExactInputTokens),
            ("prompt_tokens", review.PromptTokens), ("completion_tokens", review.CompletionTokens),
            ("context_recovery_used", review.ContextRecoveryUsed),
            ("contract_adjusted", review.ContractAdjusted),
            ("truncation_retry_exhausted", review.TruncationRetryExhausted));
        return review;
    }
}
