using System.Diagnostics;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<FastEvidenceReview> ReviewSemanticAnswerTransactionAsync(
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
            return FailedSemanticAnswerTransactionReview(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "protocol_error"),
                "semantic_answer_transaction_evidence_pool_empty",
                string.Empty,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                0,
                0,
                0,
                evidencePoolBudget: evidencePoolBudget,
                evidencePoolEligibleItemCount: evidencePoolEligibleItemCount);
        }
        if (_llm is not ISourceBackedAgentStructuredLlmClient structuredLlm)
        {
            return FailedSemanticAnswerTransactionReview(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "structured_output_unavailable"),
                "semantic_answer_transaction_capability_unavailable",
                string.Empty,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                candidates.Sum(static candidate => candidate.SourceWindow.Count),
                0,
                0,
                evidencePoolBudget: evidencePoolBudget,
                evidencePoolEligibleItemCount: evidencePoolEligibleItemCount);
        }

        var messages = BuildSemanticAnswerTransactionMessages(
            intake,
            semanticPlan,
            candidates);
        var maximumClaimCount = Math.Clamp(
            allowedEvidenceIds.Length * 2,
            1,
            12);
        var maximumTokens = Math.Min(
            _options.MaximumOutputTokens,
            Math.Clamp(192 + allowedEvidenceIds.Length * 64, 320, 640));
        var stopwatch = Stopwatch.StartNew();
        var completion = await structuredLlm.CompleteStructuredAsync(
                messages,
                BuildSemanticAnswerTransactionContract(
                    allowedEvidenceIds,
                    maximumClaimCount),
                maximumTokens,
                ct,
                temperatureOverride: 0)
            .ConfigureAwait(false);
        var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        var rawOutput = completion.Content ?? string.Empty;
        if (!TryReadSemanticAnswerTransaction(
                rawOutput,
                bundle,
                allowedEvidenceIds,
                maximumClaimCount,
                out var decision,
                out var answerAdequacy,
                out var requestedDeliverableComplete,
                out var missingUserInputPreventsUniqueResult,
                out var visibleContextEvidenceId,
                out var leadEvidenceId,
                out var presentation,
                out var claims,
                out var reason,
                out var failureReason))
        {
            return FailedSemanticAnswerTransactionReview(
                completion,
                failureReason,
                rawOutput,
                presentedCandidateIds,
                allowedEvidenceIds,
                candidates.Length,
                candidates.Sum(static candidate => candidate.SourceWindow.Count),
                messages.Sum(static message => message.Content?.Length ?? 0),
                elapsedMilliseconds,
                evidencePoolBudget: evidencePoolBudget,
                evidencePoolEligibleItemCount: evidencePoolEligibleItemCount);
        }

        var answering = string.Equals(decision, "answer", StringComparison.Ordinal);
        var citedEvidenceIds = answering
            ? claims
                .SelectMany(static claim => claim.EvidenceIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        var leadEvidenceIds = !string.Equals(
                leadEvidenceId,
                "NONE",
                StringComparison.OrdinalIgnoreCase)
            ? new[] { leadEvidenceId }
            : citedEvidenceIds;
        var renderedAnswer = answering
            ? RenderStructuredFlatWriterClaims(presentation, claims)
            : string.Empty;
        var anchorEvidenceId = citedEvidenceIds.FirstOrDefault()
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
            Content = renderedAnswer,
            ToolCalls = Array.Empty<SourceBackedAgentToolCall>(),
            FinishReason = "semantic_answer_transaction"
        };
        return new FastEvidenceReview(
            Decision: answering ? "ready" : decision == "clarify" ? "clarify" : "continue",
            NextCapability: nextCapability,
            EvidenceIds: citedEvidenceIds,
            LeadEvidenceIds: leadEvidenceIds,
            AnchorExcerpt: anchorExcerpt,
            Missing: answering || decision == "clarify" ? string.Empty : reason,
            Assessment: reason,
            Answer: renderedAnswer,
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
            AnswerSemanticallyFinal: answering,
            SemanticAnswerTransactionAttempted: true,
            EvidencePoolBudget: evidencePoolBudget,
            EvidencePoolEligibleItemCount: evidencePoolEligibleItemCount,
            EvidencePoolTruncatedItemCount: Math.Max(
                0,
                evidencePoolEligibleItemCount - allowedEvidenceIds.Length),
            AnswerAdequacy: answerAdequacy,
            RequestedDeliverableComplete: requestedDeliverableComplete,
            MissingUserInputPreventsUniqueResult:
                missingUserInputPreventsUniqueResult,
            VisibleContextEvidenceId: visibleContextEvidenceId);
    }

    private static IReadOnlyList<FastEvidenceCandidate>
        AllocateSemanticAnswerEvidencePool(
            IReadOnlyList<FastEvidenceCandidate> candidates,
            int maximumEvidenceItems)
    {
        if (candidates.Count == 0 || maximumEvidenceItems <= 0)
            return Array.Empty<FastEvidenceCandidate>();

        var selectedByCandidate = candidates
            .Select(static _ => new List<EvidenceItem>())
            .ToArray();
        var selectedEvidenceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var maximumDepth = candidates.Max(static candidate =>
            candidate.SourceWindow.Count);
        for (var depth = 0;
             depth < maximumDepth
             && selectedEvidenceIds.Count < maximumEvidenceItems;
             depth++)
        {
            for (var candidateIndex = 0;
                 candidateIndex < candidates.Count
                 && selectedEvidenceIds.Count < maximumEvidenceItems;
                 candidateIndex++)
            {
                var sourceWindow = candidates[candidateIndex].SourceWindow;
                if (depth >= sourceWindow.Count)
                    continue;
                var item = sourceWindow[depth];
                if (selectedEvidenceIds.Add(item.EvidenceId))
                    selectedByCandidate[candidateIndex].Add(item);
            }
        }

        return candidates
            .Select((candidate, index) => new FastEvidenceCandidate(
                candidate.Representative,
                selectedByCandidate[index].ToArray()))
            .Where(static candidate => candidate.SourceWindow.Count > 0)
            .ToArray();
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSemanticAnswerTransactionMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            IReadOnlyList<FastEvidenceCandidate> candidates)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 600));
        context.Append("MISSION_SEMANTIQUE: ")
            .AppendLine(TrimPromptValue(semanticPlan, 600));
        context.Append("LANGUE: ")
            .AppendLine(TrimPromptValue(intake.Language, 40));
        if (intake.ExplicitConstraints.Count > 0)
        {
            context.Append("CONTRAINTES_EXPLICITES: ")
                .AppendLine(string.Join(
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
                "Tu es l'orchestrateur semantique final d'un RAG source-backed. "
                + "Le pool canonique presente est le perimetre documentaire de "
                + "cette transaction, pas une garantie d'exhaustivite sur le "
                + "monde reel. Quand les preuves visibles permettent de produire "
                + "exactement le resultat demande, ne rends pas le livrable "
                + "incomplet seulement a cause d'une possibilite hypothetique "
                + "absente du pool. L'absence dans le pool n'est jamais une preuve "
                + "de non-existence hors du pool. Si la demande exige explicitement "
                + "une conclusion qui depasse ce perimetre, ou si un fait necessaire "
                + "au livrable manque dans les preuves visibles, conserve le manque "
                + "et choisis semantiquement clarify, context ou research selon "
                + "tes declarations. "
                + "Evalue d'abord le livrable reellement demande, y compris ses "
                + "contraintes d'unicite et d'actionnabilite, pas seulement la "
                + "presence de faits lies. Renseigne requestedDeliverableComplete "
                + "uniquement si ce livrable est complet. Renseigne "
                + "missingUserInputPreventsUniqueResult si une donnee que seul "
                + "l'utilisateur peut fournir empeche un resultat unique ou "
                + "actionnable, ou si une ambiguite utilisateur doit etre levee. "
                + "Sinon, si le livrable est incomplet mais qu'une "
                + "preuve visible localise un contexte precis a approfondir, "
                + "renseigne son EvidenceId dans visibleContextEvidenceId; utilise "
                + "NONE si aucune ancre visible ne peut resoudre le manque. "
                + "Prends ensuite une seule decision finale a partir de ces "
                + "declarations et du pool visible. Si le livrable est complet, "
                + "decision=answer: redige "
                + "chaque affirmation comme un claim autonome et assigne uniquement "
                + "les EvidenceIds qui la soutiennent exactement. Choisis clarify "
                + "si la donnee utilisateur manque, context si une preuve visible "
                + "precise fournit une ancre a approfondir, ou research si "
                + "l'information manque et que le livrable reste incomplet sans "
                + "donnee utilisateur ni ancre visible. Explique le besoin dans "
                + "reason. leadEvidenceId est la preuve principale qui ancre ta "
                + "decision quand elle est utile: pour answer, choisis NONE ou un "
                + "EvidenceId aussi cite par un claim; pour clarify et research, "
                + "choisis NONE ou un EvidenceId du pool; pour context, choisis "
                + "obligatoirement un EvidenceId du pool. N'invente aucun fait. "
                + "Renseigne answerAdequacy avant la decision: "
                + "requested_information_present avec decision=answer si le fait "
                + "ou resultat demande est present; requested_information_missing "
                + "avec decision=research s'il manque encore; "
                + "user_clarification_required avec decision=clarify si l'utilisateur "
                + "doit lever une ambiguite; visible_context_required avec "
                + "decision=context si une preuve visible doit etre approfondie. "
                + "Un constat d'absence ne fournit pas le fait demande et ne doit "
                + "pas devenir une reponse negative: utilise "
                + "requested_information_missing et decision=research. "
                + "N'ecris ni citation, ni EvidenceId, ni marqueur de protocole "
                + "dans text. Le code ne fera que valider et rendre ta decision."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static FastEvidenceReview FailedSemanticAnswerTransactionReview(
        SourceBackedAgentCompletion completion,
        string failureReason,
        string rawOutput,
        IReadOnlyList<string> presentedCandidateIds,
        IReadOnlyList<string> presentedEvidenceIds,
        int candidateCount,
        int sourceWindowItemCount,
        int promptCharacters,
        long elapsedMilliseconds,
        int evidencePoolBudget = 0,
        int evidencePoolEligibleItemCount = 0)
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
            SemanticAnswerTransactionAttempted: true,
            EvidencePoolBudget: evidencePoolBudget,
            EvidencePoolEligibleItemCount: evidencePoolEligibleItemCount,
            EvidencePoolTruncatedItemCount: Math.Max(
                0,
                evidencePoolEligibleItemCount - presentedEvidenceIds.Count));
}
