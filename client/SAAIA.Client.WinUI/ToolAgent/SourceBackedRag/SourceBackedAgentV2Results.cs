using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string? ValidateToolCall(
        SourceBackedAgentToolCall call,
        out string internalToolName)
    {
        internalToolName = string.Empty;
        if (!string.IsNullOrWhiteSpace(call.ArgumentError))
            return call.ArgumentError;
        if (!SourceBackedAgentToolCatalog.TryResolveInternalName(
                call.Name,
                call.Arguments,
                out internalToolName))
            return "unknown_source_backed_tool";
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            return "tool_arguments_must_be_an_object";
        return null;
    }

    private static bool IsTruncatedToolProtocolCompletion(
        SourceBackedAgentCompletion completion)
        => string.Equals(
               completion.FinishReason,
               "length",
               StringComparison.OrdinalIgnoreCase)
           && (completion.ToolCalls.Any(static call =>
                   !string.IsNullOrWhiteSpace(call.ArgumentError))
               || (completion.ToolCalls.Count == 0
                   && string.IsNullOrWhiteSpace(completion.Content)));

    private static bool IsContextWindowExceeded(HttpRequestException exception)
    {
        var message = exception.Message;
        return exception.StatusCode == System.Net.HttpStatusCode.BadRequest
               && (message.Contains(
                       "exceeds the available context size",
                       StringComparison.OrdinalIgnoreCase)
                   || (message.Contains("context", StringComparison.OrdinalIgnoreCase)
                       && (message.Contains("exceed", StringComparison.OrdinalIgnoreCase)
                           || message.Contains("too long", StringComparison.OrdinalIgnoreCase)
                           || message.Contains("maximum", StringComparison.OrdinalIgnoreCase))));
    }

    private static WriterDraft BuildDraft(string? content)
    {
        var answer = content?.Trim() ?? string.Empty;
        return new WriterDraft(
            answer,
            SourceContractVerifier.ExtractEvidenceIds(answer).ToArray());
    }

    private static SourceBackedPipelineResult BuildResult(
        SourceBackedIntake intake,
        IReadOnlyList<RetrievalRequest> executedRequests,
        EvidenceBundle bundle,
        WriterDraft? firstDraft,
        WriterDraft? latestDraft,
        SourceVerificationResult? verification,
        bool repaired,
        bool semanticAccepted,
        IReadOnlyCollection<string> semanticallyRejectedEvidenceIds,
        IReadOnlyList<string> semanticReviewReasons,
        IReadOnlyList<SourceBackedTraceEvent> traces,
        SourceBackedClarificationDecision? clarification = null)
    {
        var answerReady = clarification is null
                          && verification?.IsValid == true
                          && semanticAccepted;
        var citedIds = verification?.CitedEvidence
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        var decision = new EvidenceJudgeDecision(
            clarification is not null
                ? "clarify"
                : answerReady
                    ? "answer_ready"
                    : "insufficient_evidence",
            citedIds,
            bundle.Items
                .Select(static item => item.EvidenceId)
                .Except(citedIds, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Array.Empty<RetrievalRequest>(),
            clarification is not null || answerReady
                ? Array.Empty<string>()
                : verification?.Errors.Count > 0
                    ? verification.Errors
                        .Select(static error => error.Code + ": " + error.Message)
                        .ToArray()
                    : semanticReviewReasons.Count > 0
                        ? semanticReviewReasons
                        : new[] { "agent_turn_budget_exhausted_before_semantically_accepted_answer" });
        var retrievalPlan = new RetrievalPlan(
            "Native LLM tool loop; tool choice and stopping decisions were made by the LLM.",
            executedRequests,
            NeedsClarification: clarification is not null);
        var effectiveVerification = clarification is not null
            ? null
            : !answerReady && verification?.IsValid == true
            ? new SourceVerificationResult(
                false,
                (semanticReviewReasons.Count > 0
                        ? semanticReviewReasons
                        : new[] { "Le juge semantique n'a pas accepte le livrable." })
                    .Select(static reason => new SourceVerificationError(
                        "llm_answer_adequacy_failed",
                        reason,
                        null,
                        SourceBackedPipelineStep.AnswerAdequacyJudge))
                    .ToArray(),
                verification.CitedEvidence)
            : verification;

        var resultId = "source-backed-agent-v2-result-" + Guid.NewGuid().ToString("N");
        var conversationMemory = SourceBackedConversationMemoryFactory.Create(
            resultId,
            intake,
            executedRequests,
            bundle,
            citedIds,
            semanticallyRejectedEvidenceIds,
            clarification is not null,
            answerReady,
            semanticReviewReasons);
        return new SourceBackedPipelineResult(
            resultId,
            intake,
            retrievalPlan,
            bundle with { TraceEvents = traces },
            decision,
            firstDraft,
            answerReady ? latestDraft : null,
            effectiveVerification,
            repaired,
            traces,
            conversationMemory,
            clarification);
    }
}
