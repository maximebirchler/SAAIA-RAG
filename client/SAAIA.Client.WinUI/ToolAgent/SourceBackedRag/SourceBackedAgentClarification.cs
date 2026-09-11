using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string RequestSourceBackedClarificationToolName =
        "request_user_clarification";

    private sealed record SourceBackedClarificationSubmission(
        bool Submitted,
        SourceBackedPipelineResult? Result,
        string FailureReason);

    private static SourceBackedAgentToolDefinition
        BuildSourceBackedClarificationTool()
        => new(
            RequestSourceBackedClarificationToolName,
            "Apres observation du corpus, suspends la recherche seulement si plusieurs interpretations materielles restent possibles et que seul l'utilisateur peut trancher. Ne l'utilise jamais pour une incertitude que les outils documentaires peuvent resoudre. Cet appel doit etre la seule action du tour.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    understanding = CompactFollowUpString(
                        2,
                        300,
                        "Une phrase preservant la demande, sans mentionner les alternatives, la question ou les options."),
                    options = new
                    {
                        type = "array",
                        items = CompactFollowUpString(
                            1,
                            180,
                            "Option concrete que l'utilisateur peut choisir."),
                        minItems = 2,
                        maxItems = 4
                    },
                    executionImpact = CompactFollowUpString(
                        2,
                        240,
                        "Ce que la reponse changera dans la portee, les contraintes ou le livrable."),
                    ambiguityKind = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "scope", "constraints", "deliverable",
                            "source_strategy", "source_identity", "other"
                        }
                    }
                },
                required = new[]
                {
                    "understanding", "options", "executionImpact",
                    "ambiguityKind"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static bool TryReadSourceBackedClarification(
        SourceBackedAgentCompletion completion,
        string language,
        out SourceBackedClarificationDecision? decision,
        out string failureReason)
    {
        decision = null;
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                RequestSourceBackedClarificationToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason =
                "source_backed_clarification_single_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            failureReason =
                "source_backed_clarification_object_required";
            return false;
        }

        var understanding =
            ReadCompactFollowUpString(arguments, "understanding");
        var executionImpact =
            ReadCompactFollowUpString(arguments, "executionImpact");
        var ambiguityKind =
            ReadCompactFollowUpString(arguments, "ambiguityKind");
        if (!TryGetPropertyIgnoreCase(arguments, "options", out var optionsValue)
            || optionsValue.ValueKind != JsonValueKind.Array)
        {
            failureReason =
                "source_backed_clarification_options_required";
            return false;
        }

        var submittedOptions = optionsValue
            .EnumerateArray()
            .ToArray();
        var options = submittedOptions
            .Where(static option => option.ValueKind == JsonValueKind.String)
            .Select(static option => option.GetString()?.Trim() ?? string.Empty)
            .Where(static option => option.Length > 0)
            .ToArray();
        if (understanding.Length is < 2 or > 300
            || executionImpact.Length is < 2 or > 240
            || submittedOptions.Length is < 2 or > 4
            || options.Length != submittedOptions.Length
            || options.Any(static option => option.Length > 180)
            || options.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != options.Length
            || ambiguityKind is not (
                "scope" or "constraints" or "deliverable"
                or "source_strategy" or "source_identity" or "other"))
        {
            failureReason =
                "source_backed_clarification_contract_invalid";
            return false;
        }

        var message = ClarificationPresentation.BuildChoiceMessage(
            understanding,
            options,
            language);

        decision = new SourceBackedClarificationDecision(
            message,
            options,
            executionImpact,
            ambiguityKind);
        return true;
    }

    private SourceBackedClarificationSubmission
        EvaluateSourceBackedClarificationSubmission(
            SourceBackedAgentCompletion completion,
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
            int observedEvidenceCount)
    {
        if (!completion.ToolCalls.Any(static call => string.Equals(
                call.Name,
                RequestSourceBackedClarificationToolName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new SourceBackedClarificationSubmission(
                false,
                null,
                string.Empty);
        }

        SourceBackedClarificationDecision? decision = null;
        var failureReason =
            "source_backed_clarification_requires_corpus_observation";
        var hasNamedDocumentCatalogObservation =
            intake.RequestedDocumentResolution is not null;
        var accepted = (executedRequests.Count > 0
                        || hasNamedDocumentCatalogObservation)
            && TryReadSourceBackedClarification(
                completion,
                intake.Language,
                out decision,
                out failureReason);
        if (!accepted || decision is null)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.clarification.rejected",
                ("turn", turn),
                ("error", failureReason),
                ("tool_calls", completion.ToolCalls.Count),
                ("executed_requests", executedRequests.Count),
                ("named_document_catalog_observation",
                    hasNamedDocumentCatalogObservation)));
            return new SourceBackedClarificationSubmission(
                true,
                null,
                failureReason);
        }

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.clarification.requested",
            ("turn", turn),
            ("observed_evidence", observedEvidenceCount),
            ("executed_requests", executedRequests.Count),
            ("named_document_catalog_observation",
                hasNamedDocumentCatalogObservation),
            ("ambiguity_kind", decision.AmbiguityKind),
            ("option_count", decision.Options.Count),
            ("execution_impact", decision.ExecutionImpact),
            ("decision_source", "llm_orchestrator")));
        var result = BuildResult(
            intake,
            executedRequests,
            bundle,
            firstDraft,
            latestDraft,
            verification,
            repaired,
            semanticAccepted: false,
            semanticallyRejectedEvidenceIds,
            new[] { decision.ExecutionImpact },
            traces.ToArray(),
            decision);
        return new SourceBackedClarificationSubmission(
            true,
            result,
            string.Empty);
    }

#if DEBUG || SAAIA_TEST_HOOKS
    internal static SourceBackedAgentToolDefinition
        BuildSourceBackedClarificationToolForTests()
        => BuildSourceBackedClarificationTool();

    internal static (bool Accepted, SourceBackedClarificationDecision? Decision,
        string FailureReason) ReadSourceBackedClarificationForTests(
        string argumentsJson,
        bool includeAnotherCall = false)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        var calls = new List<SourceBackedAgentToolCall>
        {
            new(
                "clarification-test",
                RequestSourceBackedClarificationToolName,
                arguments.RootElement.Clone())
        };
        if (includeAnotherCall)
        {
            calls.Add(new SourceBackedAgentToolCall(
                "other-test",
                "rag_search",
                JsonSerializer.SerializeToElement(new { query = "test" })));
        }
        var completion = new SourceBackedAgentCompletion(
            string.Empty,
            calls,
            "tool_calls");
        var accepted = TryReadSourceBackedClarification(
            completion,
            "fr",
            out var decision,
            out var failureReason);
        return (accepted, decision, failureReason);
    }
#endif
}
