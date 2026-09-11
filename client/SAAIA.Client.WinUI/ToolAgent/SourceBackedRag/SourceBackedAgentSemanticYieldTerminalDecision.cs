using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string ResolveSourceYieldToolName =
        "resolve_source_yield";

    private sealed record SemanticYieldTerminalSubmission(
        bool Submitted,
        SourceBackedPipelineResult? Result,
        string FailureReason);

    private static SourceBackedAgentToolDefinition
        BuildSemanticYieldTerminalDecisionTool()
        => new(
            ResolveSourceYieldToolName,
            "Choisis la resolution finale apres observation des sources: clarification si seul l'utilisateur peut lever une ambiguite materielle, sinon insufficiency. Pour insufficiency, le message decrit uniquement la preuve manquante observee, sans affirmer l'absence dans le document ou le corpus, sans explication causale et sans connaissance externe.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    decision = new
                    {
                        type = "string",
                        @enum = new[] { "clarification", "insufficiency" }
                    },
                    message = CompactFollowUpString(
                        10,
                        240,
                        "Question concise a poser, ou preuve manquante constatee dans les recherches visibles; aucune cause ni connaissance externe.")
                },
                required = new[] { "decision", "message" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private SemanticYieldTerminalSubmission
        EvaluateSemanticYieldTerminalDecision(
            SourceBackedAgentCompletion completion,
            bool terminalDecisionAllowed,
            SourceBackedIntake intake,
            IReadOnlyList<RetrievalRequest> executedRequests,
            EvidenceBundle bundle,
            WriterDraft? firstDraft,
            WriterDraft? latestDraft,
            SourceVerificationResult? verification,
            bool repaired,
            IReadOnlyCollection<string> semanticallyRejectedEvidenceIds,
            ICollection<SourceBackedTraceEvent> traces,
            string traceId,
            ref int traceSequence,
            int turn,
            int consecutiveNoProgressTurns,
            int observedCitableSourceCount)
    {
        if (!completion.ToolCalls.Any(static call => string.Equals(
                call.Name,
                ResolveSourceYieldToolName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new SemanticYieldTerminalSubmission(
                false,
                null,
                string.Empty);
        }

        var failureReason = "semantic_yield_terminal_turn_required";
        var decision = string.Empty;
        var message = string.Empty;
        var accepted = terminalDecisionAllowed
            && executedRequests.Count > 0
            && completion.ToolCalls.Count == 1
            && completion.ToolCalls[0].Arguments.ValueKind
            == JsonValueKind.Object;
        if (accepted)
        {
            var arguments = completion.ToolCalls[0].Arguments;
            decision = ReadCompactFollowUpString(arguments, "decision");
            message = ReadCompactFollowUpString(arguments, "message");
            accepted = decision is "clarification" or "insufficiency"
                       && message.Length is >= 10 and <= 240;
            if (!accepted)
                failureReason = "semantic_yield_terminal_contract_invalid";
        }

        if (!accepted)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_rejected",
                ("turn", turn),
                ("error", failureReason),
                ("tool_calls", completion.ToolCalls.Count),
                ("executed_requests", executedRequests.Count)));
            return new SemanticYieldTerminalSubmission(
                true,
                null,
                failureReason);
        }

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_accepted",
            ("turn", turn),
            ("decision", decision),
            ("message", TrimPromptValue(message, 240)),
            ("decision_source", "llm_orchestrator")));

        if (decision == "clarification")
        {
            var clarification = new SourceBackedClarificationDecision(
                message,
                Array.Empty<string>(),
                message,
                "other");
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.EvidenceJudge,
                "source_backed_agent_v2.clarification.requested",
                ("turn", turn),
                ("observed_evidence", observedCitableSourceCount),
                ("executed_requests", executedRequests.Count),
                ("ambiguity_kind", "other"),
                ("option_count", 0),
                ("execution_impact", message),
                ("decision_source", "llm_orchestrator")));
            return new SemanticYieldTerminalSubmission(
                true,
                BuildResult(
                    intake,
                    executedRequests,
                    bundle,
                    firstDraft,
                    latestDraft,
                    verification,
                    repaired,
                    semanticAccepted: false,
                    semanticallyRejectedEvidenceIds,
                    new[] { message },
                    traces.ToArray(),
                    clarification),
                string.Empty);
        }

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.source_insufficiency.declared",
            ("turn", turn),
            ("reason", TrimPromptValue(message, 240)),
            ("consecutive_no_progress_turns", consecutiveNoProgressTurns),
            ("observed_citable_sources", observedCitableSourceCount),
            ("executed_requests", executedRequests.Count),
            ("decision_source", "llm_orchestrator")));
        return new SemanticYieldTerminalSubmission(
            true,
            BuildResult(
                intake,
                executedRequests,
                bundle,
                firstDraft,
                latestDraft,
                verification,
                repaired: true,
                semanticAccepted: false,
                semanticallyRejectedEvidenceIds,
                new[]
                {
                    BuildMechanicallyGroundedTerminalInsufficiencyNote(
                        intake,
                        executedRequests.Count,
                        observedCitableSourceCount)
                },
                traces.ToArray()),
            string.Empty);
    }

    internal static string BuildMechanicallyGroundedTerminalInsufficiencyNote(
        SourceBackedIntake intake,
        int executedRequestCount,
        int observedCitableSourceCount)
    {
        if (string.Equals(
                intake.Language,
                "fr",
                StringComparison.OrdinalIgnoreCase))
        {
            var actions = executedRequestCount == 1
                ? "1 action documentaire"
                : executedRequestCount + " actions documentaires";
            var sourceConclusion = observedCitableSourceCount switch
            {
                0 => "qu'aucune source citable non rejetée ne suffisait",
                1 => "que la seule source citable non rejetée ne suffisait",
                _ => "que les "
                     + observedCitableSourceCount
                     + " sources citables non rejetées ne suffisaient"
            };
            return "Après "
                   + actions
                   + ", le juge LLM a conclu "
                   + sourceConclusion
                   + " au livrable demandé.";
        }

        var englishActions = executedRequestCount == 1
            ? "1 document-retrieval action"
            : executedRequestCount + " document-retrieval actions";
        var englishSourceConclusion = observedCitableSourceCount switch
        {
            0 => "no non-rejected citable source was sufficient",
            1 => "the single non-rejected citable source was insufficient",
            _ => "the "
                 + observedCitableSourceCount
                 + " non-rejected citable sources were insufficient"
        };
        return "After "
               + englishActions
               + ", the LLM judge concluded that "
               + englishSourceConclusion
               + " for the requested deliverable.";
    }
}
