using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record NamedDocumentObservationDecisionOutcome(
        SourceBackedIntake Intake,
        SourceBackedAgentCompletion? TerminalCompletion,
        bool Attempted,
        int Attempts,
        int PromptTokens,
        int CompletionTokens,
        string Decision,
        string FailureReason);

    private async Task<NamedDocumentObservationDecisionOutcome>
        PrepareNamedDocumentObservationDecisionAsync(
            SourceBackedIntake intake,
            NamedDocumentInitialActionPreparation actionPreparation,
            IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
            CancellationToken ct)
    {
        var observation = intake.RequestedDocumentResolution;
        if (!actionPreparation.SuppressPlannedFirstAction
            || observation is null)
        {
            return new NamedDocumentObservationDecisionOutcome(
                intake,
                TerminalCompletion: null,
                Attempted: false,
                Attempts: 0,
                PromptTokens: 0,
                CompletionTokens: 0,
                Decision: string.Empty,
                FailureReason: string.Empty);
        }

        var currentIntake = intake;
        var currentPreparation = actionPreparation;
        var originalQuarantinedActions =
            actionPreparation.QuarantinedActions.ToArray();
        var catalogRetryUsed = false;
        var promptTokens = 0;
        var completionTokens = 0;
        var failureReason = string.Empty;
        var immediateInsufficiencyReconsiderationIssued = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var transitionTools = BuildNamedDocumentTransitionPortfolio(
                currentIntake,
                currentPreparation,
                catalogRetryUsed,
                availableTools);
            var completion = await _llm.CompleteAsync(
                    BuildNamedDocumentObservationDecisionMessages(
                        currentIntake,
                        currentPreparation,
                        attempt,
                        failureReason),
                    transitionTools,
                    Math.Clamp(_options.MaximumActionTokens, 192, 480),
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: true)
                .ConfigureAwait(false);
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();
            if (TryReadNamedDocumentTransition(
                    completion,
                    currentIntake,
                    currentPreparation,
                    transitionTools,
                    availableTools,
                    rejectImmediateInsufficiency:
                        RequiresNamedReferenceReconsideration(
                            currentIntake,
                            currentPreparation,
                            availableTools)
                        && !immediateInsufficiencyReconsiderationIssued,
                    out var effectiveIntake,
                    out var terminalCompletion,
                    out var decision,
                    out var retryCatalog,
                    out failureReason))
            {
                if (retryCatalog)
                {
                    if (catalogRetryUsed || _namedDocumentResolver is null)
                    {
                        failureReason = catalogRetryUsed
                            ? "named_document_catalog_retry_already_used"
                            : "named_document_catalog_retry_unavailable";
                        continue;
                    }

                    catalogRetryUsed = true;
                    var refreshedObservation =
                        await ResolveRequestedDocumentAsync(
                                currentIntake with
                                {
                                    InitialToolCalls = originalQuarantinedActions
                                },
                                ct)
                            .ConfigureAwait(false);
                    if (refreshedObservation is null)
                    {
                        failureReason =
                            "named_document_catalog_retry_unavailable";
                        continue;
                    }
                    currentIntake = currentIntake with
                    {
                        RequestedDocumentResolution = refreshedObservation,
                        InitialToolCalls = originalQuarantinedActions
                    };
                    currentPreparation =
                        PrepareNamedDocumentInitialActions(currentIntake);
                    currentIntake = currentPreparation.Intake;
                    if (!currentPreparation.SuppressPlannedFirstAction)
                    {
                        return new NamedDocumentObservationDecisionOutcome(
                            currentIntake,
                            TerminalCompletion: null,
                            Attempted: true,
                            Attempts: attempt,
                            promptTokens,
                            completionTokens,
                            Decision: "retry_catalog_resolved",
                            FailureReason: string.Empty);
                    }
                    failureReason = string.Empty;
                    continue;
                }

                return new NamedDocumentObservationDecisionOutcome(
                    effectiveIntake,
                    terminalCompletion,
                    Attempted: true,
                    Attempts: attempt,
                    promptTokens,
                    completionTokens,
                    decision,
                    FailureReason: string.Empty);
            }
            if (string.Equals(
                    failureReason,
                    "named_document_immediate_insufficiency_requires_reference_reconsideration",
                    StringComparison.Ordinal))
            {
                immediateInsufficiencyReconsiderationIssued = true;
            }
        }

        throw new InvalidOperationException(
            "The named-document transition remained outside its mechanical "
            + "contract after three attempts: " + failureReason);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildNamedDocumentObservationDecisionMessages(
            SourceBackedIntake intake,
            NamedDocumentInitialActionPreparation preparation,
            int attempt,
            string failureReason)
    {
        var observation = intake.RequestedDocumentResolution!;
        var payload = JsonSerializer.Serialize(new
        {
            requestedReference = observation.RequestedReference,
            namedReferenceKind = ReadExplicitNamedReferenceKind(intake),
            status = observation.Status.ToString().ToLowerInvariant(),
            catalogObservationComplete = observation.CatalogObservationComplete,
            reasonCode = observation.ReasonCode,
            transitionState = preparation.ReasonCode,
            exactMatchCount = observation.ExactMatchCount,
            candidates = observation.Candidates.Select(static candidate => new
            {
                candidate.DocId,
                candidate.DocPath,
                candidate.DocName,
                candidate.RevisionId,
                candidate.SourceHash
            }),
            quarantinedActions = preparation.QuarantinedActions.Select(static action => new
            {
                action.ToolName,
                arguments = action.Arguments
            })
        }, ClientJson.CamelCase);
        var repair = attempt > 1
            ? "\nTRANSITION PRECEDENTE INVALIDE: " + failureReason
            : string.Empty;
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique du RAG. Le code a observe "
                + "l'identite dans le catalogue courant et suspendu une action. "
                + "L'observation n'est pas une preuve de contenu. Appelle "
                + "exactement un outil de transition expose; un outil absent "
                + "est incompatible avec l'etat courant. Toute recherche "
                + "alternative doit signaler qu'elle n'utilise pas le document "
                + "demande. Si la reference est en realite un sujet, produit, "
                + "modele ou entite plutot qu'un artefact documentaire exact, "
                + "reclasse-la avec l'outil expose; ne deduis pas une insuffisance "
                + "du corpus de la seule absence d'identite documentaire."),
            SourceBackedAgentMessage.User(
                "DEMANDE UTILISATEUR:\n" + intake.UserQuestion.Trim()
                + "\nLANGUE: " + intake.Language
                + "\nOBSERVATION CATALOGUE TYPEE:\n" + payload
                + repair)
        };
    }

    private void AddNamedDocumentObservationDecisionTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        NamedDocumentObservationDecisionOutcome outcome)
    {
        if (!outcome.Attempted)
            return;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_named_document_observation.decision_completed",
            ("decision", outcome.Decision),
            ("document_scope", outcome.Intake.DocumentScope),
            ("attempts", outcome.Attempts),
            ("prompt_tokens", outcome.PromptTokens),
            ("completion_tokens", outcome.CompletionTokens),
            ("failure_reason", outcome.FailureReason),
            ("decision_source", "llm_orchestrator")));
    }

    private void AddNamedDocumentPreparationTraces(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        NamedDocumentInitialActionPreparation actionPreparation,
        NamedDocumentObservationDecisionOutcome observationDecision)
    {
        AddNamedDocumentInitialActionTrace(
            traces, traceId, ref traceSequence, actionPreparation);
        AddNamedDocumentObservationDecisionTrace(
            traces, traceId, ref traceSequence, observationDecision);
    }
}
