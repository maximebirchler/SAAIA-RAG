using System.Text;
using System.Diagnostics;

// LLM-owned semantic review protocol; domain examples belong to its prompt.
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record FastEvidenceCandidate(
        EvidenceItem Representative,
        IReadOnlyList<EvidenceItem> SourceWindow);
    private sealed record FastEvidenceProtocolDecision(
        string Action,
        string EvidenceId,
        string AnchorId,
        string Text,
        bool ProtocolValid);
    private static bool CanAttemptFastEvidenceReview(
        SourceBackedIntake intake,
        bool requireEvidenceSelection,
        int? requiredEvidenceCount,
        SemanticLayoutDimensions? semanticLayoutDimensions)
        => requireEvidenceSelection
           && requiredEvidenceCount == 1
           && semanticLayoutDimensions is null;

    private async Task<FastEvidenceReview> ReviewInitialEvidenceAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        int maximumCandidateCount,
        CancellationToken ct)
    {
        if (_options.SemanticResolutionWriterReviewEnabled)
            return await ReviewSemanticResolutionWriterReviewAsync(intake, semanticPlan, bundle, candidateEvidenceIds,
                semanticallyRejectedEvidenceIds, maximumCandidateCount, ct).ConfigureAwait(false);
        if (_options.SemanticAnswerTransactionEnabled)
            return await ReviewSemanticAnswerTransactionAsync(intake, semanticPlan, bundle, candidateEvidenceIds,
                semanticallyRejectedEvidenceIds, maximumCandidateCount, ct).ConfigureAwait(false);
        var eligibleCandidates = BuildFastEvidenceCandidates(
            bundle,
            candidateEvidenceIds,
            semanticallyRejectedEvidenceIds,
            maximumCandidateCount);
        var eligibleIds = eligibleCandidates
            .Select(static candidate =>
                candidate.Representative.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentedCandidateIds = eligibleCandidates
            .Select(static candidate =>
                candidate.Representative.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var presentedEvidenceIds = eligibleCandidates
            .SelectMany(static candidate => candidate.SourceWindow)
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var context = new StringBuilder();
        context.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 500));
        if (intake.ExplicitConstraints.Count > 0)
        {
            context.Append("CONTRAINTES EXPLICITES: ")
                .AppendLine(string.Join(
                    " | ",
                    intake.ExplicitConstraints.Select(constraint =>
                        TrimPromptValue(constraint, 100))));
        }
        AppendFastEvidenceQuestionFocusContext(context, intake);
        if (intake.MemoryContext?.ConversationTurns is { Count: > 0 })
        {
            AppendMemoryContext(
                context,
                intake.MemoryContext,
                _options.MaximumContextTokens,
                maximumMemoryCharacters: 250);
        }
        var atomicEvidenceExpectation = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "PREUVES_ATOMIQUES:",
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(atomicEvidenceExpectation))
        {
            var expectationSeparator = atomicEvidenceExpectation.IndexOf(':');
            var expectationValue = expectationSeparator >= 0
                ? atomicEvidenceExpectation[(expectationSeparator + 1)..].Trim()
                : atomicEvidenceExpectation;
            context.Append("TYPE_DE_PREUVE_ATTENDU: ")
                .AppendLine(TrimPromptValue(
                    expectationValue,
                    140));
        }
        var documentFamilyQuestion =
            SourceBackedQuestionFocus.IsDocumentFamilyOrTypeQuestion(intake);
        context.AppendLine("PREUVES CITABLES:");
        foreach (var candidate in eligibleCandidates)
        {
            var item = candidate.Representative;
            context.Append("CANDIDAT [")
                .Append(item.EvidenceId)
                .Append("] ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 100))
                .Append(" | ")
                .Append(BuildFastEvidenceReviewMetadata(
                    item,
                    documentFamilyQuestion))
                .Append(" | ")
                .AppendLine(BuildFastEvidenceSourceWindow(candidate));
        }
        if (documentFamilyQuestion)
        {
            SourceBackedDocumentMetadataHints.AppendDocumentFamilyHints(
                context,
                eligibleCandidates.Select(static candidate =>
                    candidate.Representative),
                maximumItems: 5);
        }
        var reviewSystem = documentFamilyQuestion
            ? """
              La DEMANDE decide du sens. Compare toutes les preuves sans favoriser
              leur ordre. Une fenetre partageant un EvidenceId est ordonnee. Utilise
              uniquement les identites, metadonnees et extraits visibles.

              La question porte sur la famille ou le type DU DOCUMENT: classe-le
              au niveau documentaire visible le plus specifique, sans le confondre
              avec son sujet, produit, materiau ou norme.

              Retourne uniquement un objet JSON avec exactement action,
              evidenceId, anchorId, text. action=answer si une reponse autonome
              tient en 300 caracteres; writer si les preuves suffisent mais exigent
              plus; clarify si plusieurs candidats materiellement differents conviennent
              et que seul l'utilisateur peut choisir; context pour lire autour de
              l'ancre; research pour une autre source. evidenceId est l'identifiant du CANDIDAT choisi. anchorId est
              l'identifiant [E#] d'un EXTRAIT visible appartenant a ce candidat.
              Pour clarify, evidenceId=NONE, anchorId=NONE et text est une question
              utilisateur concise.
              Pour answer, text contient la reponse dans la langue demandee sans
              aucune citation. Pour writer, text est vide. Pour context/research,
              text explique brievement ce qui manque. Si aucune piste n'est utile,
              utilise research avec evidenceId=NONE et anchorId=NONE. Aucun autre
              texte.
              """
            : """
              La DEMANDE decide du sens. Compare toutes les preuves sans favoriser
              leur ordre. Une fenetre partageant un EvidenceId est ordonnee. Utilise
              uniquement les identites et extraits visibles; n'invente aucun detail.

              Une demande explicite de recette, d'etapes ou de methode exige une
              methode actionnable complete: utilise writer si la preuve suffit,
              sinon context/research. « Donne-moi un element facile a faire » reste
              une recommandation, pas une demande de procedure: utilise answer avec
              le nom exact et un ou deux faits visibles, en 180 caracteres maximum.

              Retourne uniquement un objet JSON avec exactement action,
              evidenceId, anchorId, text. action vaut answer, writer, context ou
              research, ou clarify si plusieurs candidats materiellement differents
              conviennent et que seul l'utilisateur peut choisir. evidenceId est
              l'identifiant du CANDIDAT choisi. anchorId
              est l'identifiant [E#] d'un EXTRAIT visible appartenant a ce candidat.
              Pour clarify, evidenceId=NONE, anchorId=NONE et text est une question
              utilisateur concise; ne clarifie pas si un candidat domine clairement.
              Pour answer, text contient la reponse dans la langue demandee, sans
              aucune citation. Pour writer, text est vide. Pour context/research,
              text explique brievement ce qui manque. Si aucune piste n'est utile,
              utilise research avec evidenceId=NONE et anchorId=NONE. N'ajoute
              aucun autre texte.
              """;
        var reviewMessages = new[]
        {
            SourceBackedAgentMessage.System(reviewSystem),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
        IReadOnlyList<SourceBackedAgentToolDefinition> reviewTools =
            Array.Empty<SourceBackedAgentToolDefinition>();
        var promptCharacters = reviewMessages.Sum(
            static message => message.Content?.Length ?? 0);
        var evidenceContextCharacters = context.Length;
        var sourceWindowItemCount = eligibleCandidates.Sum(
            static candidate => candidate.SourceWindow.Count);
        var reviewStopwatch = Stopwatch.StartNew();
        var reviewMaxTokens = _options.MaximumActionTokens;
        var completion = _llm is ISourceBackedAgentStructuredLlmClient
            structuredLlm
            ? await structuredLlm.CompleteStructuredAsync(
                    reviewMessages,
                    BuildFastEvidenceReviewContract(eligibleCandidates),
                    reviewMaxTokens,
                    ct,
                    temperatureOverride: 0)
                .ConfigureAwait(false)
            : await _llm.CompleteAsync(
                    reviewMessages,
                    reviewTools,
                    reviewMaxTokens,
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: false)
                .ConfigureAwait(false);
        var reviewElapsedMilliseconds = reviewStopwatch.ElapsedMilliseconds;

        var protocolDecision = ReadFastEvidenceProtocolDecision(completion);
        var maximumInlineAnswerCharacters = documentFamilyQuestion
            ? 300
            : 180;
        var oversizedAnswerEnvelopeRecoverable =
            !protocolDecision.ProtocolValid
            && string.Equals(
                protocolDecision.Action,
                "answer",
                StringComparison.Ordinal)
            && protocolDecision.EvidenceId.Length > 0
            && protocolDecision.AnchorId.Length > 0
            && protocolDecision.Text.Length > 300;
        if (!protocolDecision.ProtocolValid
            && !oversizedAnswerEnvelopeRecoverable)
        {
            return new FastEvidenceReview(
                "continue",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "NONE",
                "La revue compacte n'a pas respecte son contrat.",
                string.Empty,
                string.Empty,
                false,
                false,
                completion,
                promptCharacters,
                evidenceContextCharacters,
                eligibleCandidates.Count,
                sourceWindowItemCount,
                ElapsedMilliseconds: reviewElapsedMilliseconds,
                PresentedCandidateIds: presentedCandidateIds,
                PresentedEvidenceIds: presentedEvidenceIds);
        }

        var answerMechanicallyRedirectedToWriter =
            string.Equals(
                protocolDecision.Action,
                "answer",
                StringComparison.Ordinal)
            && protocolDecision.Text.Length
                > maximumInlineAnswerCharacters;
        var submittedAction = answerMechanicallyRedirectedToWriter
            ? "writer"
            : protocolDecision.Action;
        var submittedAnswer = submittedAction == "answer";
        var submittedClarification = submittedAction == "clarify";
        var submittedContinue =
            submittedAction is "context" or "research";
        var bestEvidenceId = protocolDecision.EvidenceId;
        var anchorEvidenceId = protocolDecision.AnchorId;
        var decisionText = answerMechanicallyRedirectedToWriter
            ? string.Empty
            : protocolDecision.Text;
        var nextCapability = submittedAction switch
        {
            "answer" or "writer" => "write",
            "clarify" => "clarification",
            "context" => "documents_context",
            _ => "research"
        };
        var selectedNone = string.Equals(
            bestEvidenceId,
            "NONE",
            StringComparison.OrdinalIgnoreCase);
        var selectedCandidate = selectedNone
            ? null
            : eligibleCandidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Representative.EvidenceId,
                    bestEvidenceId,
                    StringComparison.OrdinalIgnoreCase));
        var anchorEvidenceItem = selectedCandidate is null
            ? null
            : selectedCandidate.SourceWindow.FirstOrDefault(item =>
                string.Equals(
                    item.EvidenceId,
                    anchorEvidenceId,
                    StringComparison.OrdinalIgnoreCase));
        var anchorVerified = anchorEvidenceItem is not null;
        var anchorExcerpt = anchorVerified
            ? CompactReviewExcerpt(anchorEvidenceItem!.Excerpt, 180)
            : "NONE";
        IReadOnlyList<string> evidenceIds =
            submittedContinue
            || submittedClarification
            || selectedNone
            || !anchorVerified
                ? Array.Empty<string>()
                : new[] { anchorEvidenceItem!.EvidenceId };
        IReadOnlyList<string> leadEvidenceIds =
            anchorVerified
                ? new[] { anchorEvidenceItem!.EvidenceId }
                : Array.Empty<string>();
        var answer = submittedAnswer && anchorVerified
            ? BuildFastEvidenceCitedAnswer(
                decisionText,
                anchorEvidenceItem!.EvidenceId)
            : string.Empty;
        var hasValidSelectedAnchor =
            selectedCandidate is not null
            && anchorVerified
            && eligibleIds.Contains(
                selectedCandidate.Representative.EvidenceId);
        var researchWithoutLead =
            submittedAction == "research"
            && selectedNone
            && string.Equals(
                anchorEvidenceId,
                "NONE",
                StringComparison.OrdinalIgnoreCase);
        var protocolValid =
            submittedAction is "answer" or "writer" or "clarify" or "context" or "research"
            && (protocolDecision.ProtocolValid
                || oversizedAnswerEnvelopeRecoverable)
            && (!submittedAnswer || answer.Length >= 8)
            && answer.Length <= maximumInlineAnswerCharacters
            && nextCapability is
                "write"
                or "clarification"
                or "documents_context"
                or "research"
            && (submittedClarification
                ? eligibleCandidates.Count >= 2
                  && selectedNone
                  && string.Equals(
                      anchorEvidenceId,
                      "NONE",
                      StringComparison.OrdinalIgnoreCase)
                  && decisionText.Length >= 8
                : submittedAction is "answer" or "writer"
                ? hasValidSelectedAnchor
                  && evidenceIds.Count == 1
                  && string.Equals(
                      nextCapability,
                      "write",
                      StringComparison.Ordinal)
                : submittedAction == "context"
                    ? hasValidSelectedAnchor
                      && leadEvidenceIds.Count == 1
                      && evidenceIds.Count == 0
                      && decisionText.Length >= 8
                    : (researchWithoutLead || hasValidSelectedAnchor)
                      && evidenceIds.Count == 0
                      && decisionText.Length >= 8);

        SingleSelectionScopeReview? singleSelectionScopeReview = null;
        var singleSelectionScopeTerminalBudgetOnly = false;
        if (protocolValid
            && submittedAction is "answer" or "writer"
            && eligibleCandidates.Count >= 2
            && selectedCandidate is not null)
        {
            try
            {
                singleSelectionScopeReview =
                    await ReviewSingleSelectionScopeAsync(
                            intake,
                            eligibleCandidates,
                            selectedCandidate,
                            ct)
                        .ConfigureAwait(false);
            }
            catch (SourceBackedLlmBudgetExceededException ex) when (
                string.Equals(
                    ex.Reason,
                    "terminal_budget_only",
                    StringComparison.Ordinal))
            {
                // The primary Qwen review is already complete and valid. Keep
                // that semantic choice; only the optional scope refinement was
                // refused before I/O by the cumulative budget.
                singleSelectionScopeTerminalBudgetOnly = true;
            }

            if (singleSelectionScopeReview is not null
                && !singleSelectionScopeReview.ProtocolValid)
            {
                protocolValid = false;
            }
            else if (singleSelectionScopeReview is not null
                     && string.Equals(
                         singleSelectionScopeReview.Decision,
                         "clarify",
                         StringComparison.OrdinalIgnoreCase))
            {
                return new FastEvidenceReview(
                    "clarify",
                    "clarification",
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    "NONE",
                    string.Empty,
                    singleSelectionScopeReview.Reason,
                    string.Empty,
                    true,
                    false,
                    completion,
                    promptCharacters,
                    evidenceContextCharacters,
                    eligibleCandidates.Count,
                    sourceWindowItemCount,
                    reviewElapsedMilliseconds,
                    singleSelectionScopeReview.Question,
                    singleSelectionScopeReview.Decision,
                    singleSelectionScopeReview.Basis,
                    singleSelectionScopeReview.ProtocolValid,
                    singleSelectionScopeReview.ElapsedMilliseconds,
                    singleSelectionScopeReview.MechanicallyNormalized,
                    presentedCandidateIds,
                    presentedEvidenceIds,
                    SingleSelectionScopeReason: singleSelectionScopeReview.Reason);
            }
        }
        var decision = submittedClarification
            ? "clarify"
            : submittedContinue
                ? "continue"
                : "ready";
        var protocolFailure =
            "La decision doit designer un candidat et un identifiant d'extrait "
            + "visibles et compatibles; il faut approfondir ou rechercher une "
            + "autre preuve.";

        return new FastEvidenceReview(
            decision,
            nextCapability,
            evidenceIds,
            !submittedContinue && evidenceIds.Count > 0
                ? evidenceIds
                : leadEvidenceIds.Count > 0
                    ? leadEvidenceIds
                    : selectedCandidate is null
                        ? Array.Empty<string>()
                        : new[]
                        {
                            selectedCandidate.Representative.EvidenceId
                        },
            anchorExcerpt,
            !protocolValid
                ? protocolFailure
                : submittedClarification
                    ? string.Empty
                : submittedContinue
                    ? decisionText
                    : string.Empty,
            protocolValid && !submittedContinue
                ? anchorExcerpt
                : string.Empty,
            answer,
            protocolValid,
            anchorVerified,
            completion,
            promptCharacters,
            evidenceContextCharacters,
            eligibleCandidates.Count,
            sourceWindowItemCount,
            reviewElapsedMilliseconds,
            submittedClarification && protocolValid
                ? decisionText
                : string.Empty,
            singleSelectionScopeReview?.Decision ?? "not_needed",
            singleSelectionScopeReview?.Basis ?? string.Empty,
            singleSelectionScopeReview?.ProtocolValid ?? true,
            singleSelectionScopeReview?.ElapsedMilliseconds ?? 0,
            singleSelectionScopeReview?.MechanicallyNormalized ?? false,
            presentedCandidateIds,
            presentedEvidenceIds,
            answerMechanicallyRedirectedToWriter,
            SingleSelectionScopeReason:
                singleSelectionScopeReview?.Reason ?? string.Empty,
            SingleSelectionScopeTerminalBudgetOnly:
                singleSelectionScopeTerminalBudgetOnly);
    }

}
