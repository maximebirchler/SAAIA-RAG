using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string DeclareSourceInsufficiencyToolName =
        "declare_source_insufficiency";

    private sealed record SourceInsufficiencySubmission(
        bool Submitted,
        SourceBackedPipelineResult? Result,
        string FailureReason);

    private static SourceBackedAgentToolDefinition
        BuildSourceInsufficiencyTool()
        => new(
            DeclareSourceInsufficiencyToolName,
            "Apres plusieurs actions documentaires sans progres mesure, conclus que les sources observees restent insuffisantes seulement si tu juges qu'aucune nouvelle route utile et fondee n'est encore justifiee. Le code ne prend pas cette decision semantique. Cet appel doit etre l'unique action du tour.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    reason = CompactFollowUpString(
                        10,
                        400,
                        "Explique precisement quelle information sourcee manque encore et pourquoi les routes deja tentees ne permettent pas de la fournir.")
                },
                required = new[] { "reason" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static bool TryReadSourceInsufficiency(
        SourceBackedAgentCompletion completion,
        out string reason,
        out string failureReason)
    {
        reason = string.Empty;
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                DeclareSourceInsufficiencyToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason =
                "source_insufficiency_single_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            failureReason = "source_insufficiency_object_required";
            return false;
        }

        reason = ReadCompactFollowUpString(arguments, "reason");
        if (reason.Length is < 10 or > 400)
        {
            failureReason = "source_insufficiency_reason_invalid";
            return false;
        }

        return true;
    }

    private static bool TryAcceptSourceInsufficiency(
        SourceBackedAgentCompletion completion,
        bool yieldResolutionAllowed,
        int executedRequestCount,
        bool hasNamedDocumentCatalogObservation,
        out string reason,
        out string failureReason)
    {
        reason = string.Empty;
        var terminalAllowed = yieldResolutionAllowed
                              || hasNamedDocumentCatalogObservation;
        failureReason = terminalAllowed
            ? "source_insufficiency_requires_corpus_observation"
            : "source_insufficiency_requires_yield_resolution_turn";
        return terminalAllowed
            && (executedRequestCount > 0
                || hasNamedDocumentCatalogObservation)
            && TryReadSourceInsufficiency(
                completion,
                out reason,
                out failureReason);
    }

    private SourceInsufficiencySubmission
        EvaluateSourceInsufficiencySubmission(
            SourceBackedAgentCompletion completion,
            bool yieldResolutionAllowed,
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
                DeclareSourceInsufficiencyToolName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new SourceInsufficiencySubmission(
                false,
                null,
                string.Empty);
        }

        var accepted = TryAcceptSourceInsufficiency(
                completion,
                yieldResolutionAllowed,
                executedRequests.Count,
                intake.RequestedDocumentResolution is not null,
                out var reason,
                out var failureReason);
        if (!accepted)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.source_insufficiency.rejected",
                ("turn", turn),
                ("error", failureReason),
                ("tool_calls", completion.ToolCalls.Count),
                ("executed_requests", executedRequests.Count),
                ("yield_resolution_allowed", yieldResolutionAllowed),
                ("named_document_catalog_observation",
                    intake.RequestedDocumentResolution is not null)));
            return new SourceInsufficiencySubmission(
                true,
                null,
                failureReason);
        }

        var semanticReason = ReadCompactFollowUpString(
            completion.ToolCalls[0].Arguments,
            "reason");
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.source_insufficiency.declared",
            ("turn", turn),
            ("reason", TrimPromptValue(semanticReason, 400)),
            ("consecutive_no_progress_turns", consecutiveNoProgressTurns),
            ("observed_citable_sources", observedCitableSourceCount),
            ("executed_requests", executedRequests.Count),
            ("named_document_catalog_observation",
                intake.RequestedDocumentResolution is not null),
            ("decision_source", "llm_orchestrator")));
        var result = BuildResult(
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
                "Décision d'insuffisance de l'orchestrateur LLM: "
                + semanticReason
            },
            traces.ToArray());
        return new SourceInsufficiencySubmission(
            true,
            result,
            string.Empty);
    }

#if DEBUG
    internal static (bool Accepted, string FailureReason)
        ReadSourceInsufficiencyForTests(
            string argumentsJson,
            bool yieldResolutionAllowed,
            int executedRequestCount = 1,
            bool hasNamedDocumentCatalogObservation = false)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        var completion = new SourceBackedAgentCompletion(
            string.Empty,
            new[]
            {
                new SourceBackedAgentToolCall(
                    "insufficiency-test",
                    DeclareSourceInsufficiencyToolName,
                    arguments.RootElement.Clone())
            },
            "tool_calls");
        var accepted = TryAcceptSourceInsufficiency(
            completion,
            yieldResolutionAllowed,
            executedRequestCount,
            hasNamedDocumentCatalogObservation,
            out _,
            out var failureReason);
        return (accepted, failureReason);
    }
#endif
}
