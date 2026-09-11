using System.Diagnostics;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<FastEvidenceReview> ReviewSemanticResolutionWriterReviewAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        int maximumCandidateCount,
        CancellationToken ct)
    {
        var evidencePoolBudget = Math.Clamp(
            Math.Max(1, maximumCandidateCount) * 2,
            2,
            12);
        var eligibleCandidates = BuildFastEvidenceCandidates(
                bundle,
                candidateEvidenceIds,
                semanticallyRejectedEvidenceIds,
                maximumCandidateCount,
                maximumSourceWindowItems: null)
            .Select(candidate => new FastEvidenceCandidate(
                candidate.Representative,
                candidate.SourceWindow
                    .Where(IsMechanicallyCitableCandidate)
                    .Where(HasRenderableEvidenceValue)
                    .DistinctBy(
                        static item => item.EvidenceId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .Where(static candidate => candidate.SourceWindow.Count > 0)
            .ToArray();
        var evidencePoolEligibleItemCount = eligibleCandidates
            .SelectMany(static candidate => candidate.SourceWindow)
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var candidates = AllocateSemanticAnswerEvidencePool(
                eligibleCandidates,
                evidencePoolBudget)
            .ToArray();
        var presentedCandidateIds = candidates
            .Select(static candidate => candidate.Representative.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowedEvidenceIds = candidates
            .SelectMany(static candidate => candidate.SourceWindow)
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedEvidenceIds.Length == 0)
        {
            return FailedSemanticResolutionReview(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "protocol_error"),
                "semantic_resolution_evidence_pool_empty",
                string.Empty,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                0,
                0,
                0,
                evidencePoolBudget,
                evidencePoolEligibleItemCount);
        }
        if (_llm is not ISourceBackedAgentStructuredLlmClient structuredLlm)
        {
            return FailedSemanticResolutionReview(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "structured_output_unavailable"),
                "semantic_resolution_capability_unavailable",
                string.Empty,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                candidates.Sum(static candidate => candidate.SourceWindow.Count),
                0,
                0,
                evidencePoolBudget,
                evidencePoolEligibleItemCount);
        }

        var messages = BuildSemanticResolutionMessages(intake, semanticPlan, candidates);
        var maximumTokens = Math.Min(
            _options.MaximumOutputTokens,
            Math.Clamp(192 + allowedEvidenceIds.Length * 32, 256, 480));
        var stopwatch = Stopwatch.StartNew();
        var completion = await structuredLlm.CompleteStructuredAsync(
                messages,
                BuildSemanticResolutionContract(allowedEvidenceIds),
                maximumTokens,
                ct,
                temperatureOverride: 0)
            .ConfigureAwait(false);
        var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        var rawOutput = completion.Content ?? string.Empty;
        if (!TryReadSemanticResolution(
                rawOutput,
                allowedEvidenceIds,
                out var decision,
                out var answerAdequacy,
                out var requestedDeliverableComplete,
                out var missingUserInputPreventsUniqueResult,
                out var visibleContextEvidenceId,
                out var selectedEvidenceIds,
                out var leadEvidenceIds,
                out var reason,
                out var failureReason))
        {
            return FailedSemanticResolutionReview(
                completion,
                failureReason,
                rawOutput,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                candidates.Sum(static candidate => candidate.SourceWindow.Count),
                messages.Sum(static message => message.Content?.Length ?? 0),
                elapsedMilliseconds,
                evidencePoolBudget,
                evidencePoolEligibleItemCount);
        }

        var answering = string.Equals(decision, "answer", StringComparison.Ordinal);
        var anchorEvidenceId = selectedEvidenceIds.FirstOrDefault()
                               ?? leadEvidenceIds.FirstOrDefault();
        var anchorExcerpt = anchorEvidenceId is not null
                            && bundle.ById.TryGetValue(anchorEvidenceId, out var anchor)
            ? CompactReviewExcerpt(anchor.Excerpt, 180)
            : "NONE";
        var nextCapability = decision switch
        {
            "answer" => "write",
            "clarify" => "clarification",
            "context" => "documents_context",
            _ => "research"
        };
        var semanticCompletion = completion with
        {
            Content = string.Empty,
            ToolCalls = Array.Empty<SourceBackedAgentToolCall>(),
            FinishReason = "semantic_resolution"
        };
        return new FastEvidenceReview(
            Decision: answering ? "ready" : decision == "clarify" ? "clarify" : "continue",
            NextCapability: nextCapability,
            EvidenceIds: selectedEvidenceIds,
            LeadEvidenceIds: leadEvidenceIds,
            AnchorExcerpt: anchorExcerpt,
            Missing: answering || decision == "clarify" ? string.Empty : reason,
            Assessment: reason,
            Answer: string.Empty,
            ProtocolValid: true,
            AnchorVerified: anchorEvidenceId is not null,
            Completion: semanticCompletion,
            PromptCharacters: messages.Sum(static message => message.Content?.Length ?? 0),
            EvidenceContextCharacters: messages[1].Content?.Length ?? 0,
            CandidateCount: candidates.Length,
            SourceWindowItemCount: candidates.Sum(static candidate => candidate.SourceWindow.Count),
            ElapsedMilliseconds: elapsedMilliseconds,
            ClarificationQuestion: decision == "clarify" ? reason : string.Empty,
            PresentedCandidateIds: presentedCandidateIds,
            PresentedEvidenceIds: allowedEvidenceIds,
            AnswerSemanticallyFinal: false,
            SemanticResolutionWriterReviewAttempted: true,
            EvidencePoolBudget: evidencePoolBudget,
            EvidencePoolEligibleItemCount: evidencePoolEligibleItemCount,
            EvidencePoolTruncatedItemCount: Math.Max(
                0,
                evidencePoolEligibleItemCount - allowedEvidenceIds.Length),
            AnswerAdequacy: answerAdequacy,
            RequestedDeliverableComplete: requestedDeliverableComplete,
            MissingUserInputPreventsUniqueResult: missingUserInputPreventsUniqueResult,
            VisibleContextEvidenceId: visibleContextEvidenceId);
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildSemanticResolutionMessages(
        SourceBackedIntake intake,
        string semanticPlan,
        IReadOnlyList<FastEvidenceCandidate> candidates)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE: ").AppendLine(TrimPromptValue(intake.UserQuestion, 600));
        context.Append("MISSION_SEMANTIQUE: ").AppendLine(TrimPromptValue(semanticPlan, 600));
        context.Append("LANGUE: ").AppendLine(TrimPromptValue(intake.Language, 40));
        if (intake.ExplicitConstraints.Count > 0)
        {
            context.Append("CONTRAINTES_EXPLICITES: ").AppendLine(string.Join(
                " | ",
                intake.ExplicitConstraints.Select(static constraint =>
                    TrimPromptValue(constraint, 120))));
        }
        context.AppendLine("POOL_DE_PREUVES_CANONIQUES_BORNE:");
        foreach (var item in candidates
                     .SelectMany(static candidate => candidate.SourceWindow)
                     .DistinctBy(
                         static item => item.EvidenceId,
                         StringComparer.OrdinalIgnoreCase))
        {
            AppendCitableEvidenceWriterBlock(context, item, 520);
        }

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le juge de resolution d'un RAG source-backed. Tu decides "
                + "uniquement entre quatre issues. Choisis answer lorsque le pool visible "
                + "contient les faits necessaires au livrable demande et place dans "
                + "evidenceIds toutes et seulement les preuves a transmettre au writer. "
                + "Choisis research lorsqu'une information documentaire necessaire manque "
                + "du pool; evidenceIds doit alors etre vide. Choisis clarify lorsqu'un "
                + "choix ou une contrainte de l'utilisateur manque et empeche un resultat "
                + "unique; evidenceIds doit etre vide et reason formule la question utile. "
                + "Choisis context lorsque la preuve visible est seulement une ancre ou un "
                + "localisateur dont le contenu exact doit etre ouvert; evidenceIds contient "
                + "exactement cette ancre. PORTEE_COMPARATIVE: pour une comparaison "
                + "explicitement bornee au pool visible, answer est permis lorsque les faits "
                + "necessaires de toutes les alternatives visibles pertinentes sont presents; "
                + "n'exige pas un candidat hypothetique hors pool. PORTEE_GLOBALE: une "
                + "pretention mondiale, universelle ou exhaustive reste research sans preuve "
                + "explicite de couverture. PREUVES_DE_SUPPORT: evidenceIds contient toutes et "
                + "seulement les preuves necessaires au claim, meme si le livrable comporte une "
                + "seule unite; la cardinalite du livrable ne limite pas la cardinalite des "
                + "preuves. PRIORITE_CONTEXTUELLE: context prime sur research lorsqu'une preuve "
                + "visible est un localisateur concret non encore ouvert vers le contenu "
                + "demande; research s'applique s'il n'existe aucun localisateur exploitable ou "
                + "si son contenu ouvert reste insuffisant. RAISON_CONCISE: reason doit etre "
                + "complete, concise et rester sous la borne du contrat. "
                + "SELECTION_COMPLETE_ET_LEADS: evidenceIds = ensemble complet des preuves "
                + "necessaires; leadEvidenceIds = sous-ensemble ordonne des preuves principales "
                + "choisi par toi. Pour answer, les deux listes sont non vides et chaque lead "
                + "appartient a evidenceIds. Pour context, elles contiennent exactement le meme "
                + "unique ID. Pour research ou clarify, elles sont toutes deux vides. Le code "
                + "n'infere jamais un lead et transporte ton ordre declare. Tu ne rediges aucun "
                + "claim, texte final, presentation ou citation. Le pool presente borne cette "
                + "transaction "
                + "documentaire sans prouver l'exhaustivite mondiale. L'absence dans le "
                + "pool ne prouve jamais une non-existence hors pool. Le code valide la "
                + "forme et derive les alias de compatibilite sans changer ta decision."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static FastEvidenceReview FailedSemanticResolutionReview(
        SourceBackedAgentCompletion completion,
        string failureReason,
        string rawOutput,
        IReadOnlyList<string> presentedCandidateIds,
        IReadOnlyList<string> presentedEvidenceIds,
        int candidateCount,
        int sourceWindowItemCount,
        int promptCharacters,
        long elapsedMilliseconds,
        int evidencePoolBudget,
        int evidencePoolEligibleItemCount)
        => new(
            Decision: "continue",
            NextCapability: "research",
            EvidenceIds: Array.Empty<string>(),
            LeadEvidenceIds: Array.Empty<string>(),
            AnchorExcerpt: "NONE",
            Missing: failureReason,
            Assessment: string.Empty,
            Answer: string.Empty,
            ProtocolValid: false,
            AnchorVerified: false,
            Completion: completion with
            {
                Content = string.Empty,
                ToolCalls = Array.Empty<SourceBackedAgentToolCall>(),
                FinishReason = "protocol_error",
                ProtocolError = failureReason,
                ProtocolRawOutput = rawOutput
            },
            PromptCharacters: promptCharacters,
            CandidateCount: candidateCount,
            SourceWindowItemCount: sourceWindowItemCount,
            ElapsedMilliseconds: elapsedMilliseconds,
            PresentedCandidateIds: presentedCandidateIds,
            PresentedEvidenceIds: presentedEvidenceIds,
            SemanticResolutionWriterReviewAttempted: true,
            EvidencePoolBudget: evidencePoolBudget,
            EvidencePoolEligibleItemCount: evidencePoolEligibleItemCount,
            EvidencePoolTruncatedItemCount: Math.Max(
                0,
                evidencePoolEligibleItemCount - presentedEvidenceIds.Count));
}
