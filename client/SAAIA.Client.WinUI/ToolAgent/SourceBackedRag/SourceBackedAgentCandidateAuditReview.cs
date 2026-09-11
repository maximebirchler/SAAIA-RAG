using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CandidateDisplayValueReviewResult(
        IReadOnlyList<SourceBackedAgentCompletion> Completions,
        CandidateCollectionAuditDecision Decision);

    private async Task<CandidateDisplayValueReviewResult>
        CompleteCandidateDisplayValueReviewAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            CancellationToken ct)
    {
        var directLabelContract =
            _llm is ISourceBackedAgentStructuredLlmClient;
        var messages = BuildCandidateDisplayValueReviewMessages(
            intake,
            semanticPlan,
            candidateObjectType,
            candidateEligibilityRule,
            semanticRowHeader,
            semanticRowLabels,
            semanticColumnLabels,
            candidates,
            directLabelContract);
        var tools = BuildCandidateBatchAuditTool(candidates);
        var completions = new List<SourceBackedAgentCompletion>(2);
        CandidateCollectionAuditDecision? decision = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptMessages = messages.ToList();
            if (attempt > 1)
            {
                attemptMessages.Add(SourceBackedAgentMessage.User(
                    "REPARATION DU CONTRAT: retourne une decision complete et exacte pour chaque cle cN, sans commentaire."));
            }
            var completion = _llm is ISourceBackedAgentStructuredLlmClient structuredLlm
                ? await structuredLlm.CompleteStructuredAsync(
                        attemptMessages,
                        BuildCandidateParentReviewContract(candidates),
                        ResolveCandidateParentReviewOutputTokens(candidates.Count),
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        attemptMessages,
                        tools,
                        ResolveCandidateBatchOutputTokens(candidates.Count),
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            completions.Add(completion);
            decision = _llm is ISourceBackedAgentStructuredLlmClient
                ? ReadCandidateParentReviewDecision(completion, candidates)
                : ReadCandidateBatchAuditDecision(completion, candidates);
            if (decision.ProtocolValid)
                break;
        }

        return new CandidateDisplayValueReviewResult(
            completions,
            decision!);
    }

    private static CandidateCollectionAuditDecision
        ApplyCandidateDisplayValueReview(
            CandidateCollectionAuditDecision original,
            CandidateCollectionAuditDecision review,
            IReadOnlyList<EvidenceItem> reviewedCandidates)
    {
        var reviewedIds = reviewedCandidates
            .Select(static candidate => candidate.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approvals = original.ApprovedCandidates
            .Where(approval => !reviewedIds.Contains(approval.EvidenceId))
            .Concat(review.ProtocolValid
                ? review.ApprovedCandidates
                : Array.Empty<CandidateCollectionApproval>())
            .ToArray();
        var rejectedIds = original.RejectedEvidenceIds
            .Where(id => !reviewedIds.Contains(id))
            .Concat(review.ProtocolValid
                ? review.RejectedEvidenceIds
                : reviewedIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var classifications = original.Classifications
            .Where(classification =>
                !reviewedIds.Contains(classification.EvidenceId))
            .Concat(review.ProtocolValid
                ? review.Classifications
                : reviewedCandidates.Select(candidate =>
                    new CandidateCollectionClassification(
                        candidate.EvidenceId,
                        OtherWrongTypeClassification,
                        GetEvidenceDisplayValue(candidate))))
            .ToArray();
        return new CandidateCollectionAuditDecision(
            true,
            approvals,
            rejectedIds,
            classifications,
            null);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateDisplayValueReviewMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            bool useDirectLabelContract)
    {
        var prompt = new StringBuilder()
            .Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 420))
            .Append("MISSION: ")
            .AppendLine(TrimPromptValue(semanticPlan, 420));
        if (!string.IsNullOrWhiteSpace(candidateObjectType)
            || !string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            prompt.Append("TYPE DOCUMENTAIRE ATOMIQUE DECIDE PAR LE LLM: ")
                .AppendLine(TrimPromptValue(candidateObjectType, 120))
                .Append("REGLE D'ELIGIBILITE DECIDEE PAR LE LLM: ")
                .AppendLine(TrimPromptValue(candidateEligibilityRule, 240));
        }
        var axes = new[] { semanticRowHeader }
            .Concat(semanticRowLabels)
            .Concat(semanticColumnLabels)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (axes.Length > 0)
        {
            prompt.Append("AXES A NE PAS UTILISER COMME VALEURS: ")
                .AppendLine(string.Join(" | ", axes));
        }
        prompt.AppendLine("CANDIDATS AUX LIBELLES DIRECTS OU PARENTS A REVERIFIER:");
        for (var candidateIndex = 0;
             candidateIndex < candidates.Count;
             candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            prompt.Append(CandidateAuditKey(candidateIndex))
                .Append(" [")
                .Append(candidate.EvidenceId)
                .Append("]: ");
            var options = BuildCandidateBatchDisplayValueOptions(candidate);
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                if (optionIndex > 0)
                    prompt.Append(" ; ");
                prompt.Append(optionIndex + 1)
                    .Append("=\"")
                    .Append(TrimPromptValue(options[optionIndex], 100))
                    .Append('"');
            }
            if (candidate.SelectionHints.TryGetValue("kind", out var kind))
            {
                prompt.Append(" | structure=")
                    .Append(TrimPromptValue(kind, 32));
            }
            if (candidate.SelectionHints.TryGetValue(
                    "headingPath",
                    out var headingPath))
            {
                prompt.Append(" | chemin=")
                    .Append(TrimPromptValue(headingPath, 120));
            }
            prompt.Append(" | extrait=")
                .Append(TrimPromptValue(candidate.Excerpt, 160));
            prompt.AppendLine();
        }
        prompt.Append(useDirectLabelContract
            ? "DECISION: chaine vide=REJET; sinon recopie exactement le premier libelle qui nomme lui-meme l'instance."
            : "DECISION: 0=REJET; sinon index du premier libelle qui nomme lui-meme l'instance.");
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu verifies uniquement les libelles visibles de candidats dont le "
                + "premier jugement peut avoir confondu un fragment avec son parent. "
                + "Pour CHAQUE cle, lis et evalue CHAQUE option independamment avant de "
                + "decider. Le premier libelle peut etre une etape, une quantite ou un "
                + "fragment invalide alors qu'une option parent suivante nomme exactement "
                + "l'instance concrete, unique et autonome attendue: dans ce cas, accepte "
                + "ce parent. Les options sont ordonnees du plus direct au plus ancestral: "
                + "parmi les options valides, choisis toujours la premiere. Ne rejette le "
                + "candidat que si AUCUNE option ne nomme l'instance attendue. Une categorie, "
                + "collection, rubrique, role ou axe ne devient jamais valide parce qu'elle "
                + "est un parent. Procedure obligatoire: (1) evalue option 1; (2) continue "
                + "avec toutes les options restantes meme si l'option 1 est invalide; "
                + "(3) accepte la premiere option valide, sinon choisis le motif de rejet "
                + "qui decrit le mieux l'ensemble. Retourne uniquement l'objet JSON."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateBatchAuditMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            bool useOrderedDecisionContract)
    {
        if (candidates.Count > 0
            && candidates.All(HasResolvedSourceAnchorLabel))
        {
            return BuildCanonicalTitleCandidateBatchAuditMessages(
                semanticPlan,
                candidateObjectType,
                candidateEligibilityRule,
                candidates,
                useOrderedDecisionContract);
        }

        var compact = candidates.Count > 24;
        var prompt = new StringBuilder()
            .Append("DEMANDE ORIGINALE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, compact ? 360 : 500))
            .Append("MISSION STRUCTUREE: ")
            .AppendLine(TrimPromptValue(semanticPlan, compact ? 300 : 520));
        if (!string.IsNullOrWhiteSpace(candidateObjectType)
            || !string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            prompt.Append("HYPOTHESE DE TYPE (revisable): ")
                .AppendLine(TrimPromptValue(candidateObjectType, compact ? 100 : 160))
                .Append("HYPOTHESE D'ELIGIBILITE (revisable): ")
                .AppendLine(TrimPromptValue(
                    candidateEligibilityRule,
                    compact ? 160 : 240));
        }
        var placementAxes = new[] { semanticRowHeader }
            .Concat(semanticRowLabels)
            .Concat(semanticColumnLabels)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (placementAxes.Length > 0)
        {
            prompt.Append("AXES DE PLACEMENT DEJA FOURNIS PAR LE LLM (ne sont pas des valeurs candidates): ")
                .AppendLine(string.Join(
                    " | ",
                    placementAxes.Select(value => TrimPromptValue(
                        value,
                        compact ? 48 : 72))));
        }
        prompt.AppendLine("CANDIDATS A JUGER DANS CE LOT:");
        for (var candidateIndex = 0;
             candidateIndex < candidates.Count;
             candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var displayValues = BuildCandidateBatchInitialDisplayValueOptions(candidate);
            prompt.Append(CandidateAuditKey(candidateIndex))
                .Append(" [")
                .Append(candidate.EvidenceId)
                .Append(']')
                .Append(" | type=")
                .Append(candidate.SelectionHints.TryGetValue(
                    "kind",
                    out var kind)
                        ? TrimPromptValue(kind, 24)
                        : TrimPromptValue(candidate.SourceKind, 24))
                .Append(" | libelles=");
            for (var index = 0; index < displayValues.Count; index++)
            {
                if (index > 0)
                    prompt.Append(" ; ");
                prompt.Append(index + 1)
                    .Append("=\"")
                    .Append(TrimPromptValue(
                        displayValues[index],
                        compact ? 48 : 92))
                    .Append('"');
            }
            if (candidate.SelectionHints.TryGetValue(
                    "headingPath",
                    out var headingPath))
            {
                prompt.Append(" | chemin=")
                    .Append(TrimPromptValue(
                        headingPath,
                        compact ? 64 : 80));
            }
            prompt.Append(" | extrait=")
                .AppendLine(TrimPromptValue(
                    candidate.Excerpt,
                    compact ? 80 : 120));
        }
        prompt.AppendLine()
            .Append("ORDRE DES LIBELLES: du plus direct au plus ancestral. ")
            .AppendLine("Choisis le premier qui nomme lui-meme l'instance attendue.")
            .Append(useOrderedDecisionContract
                ? "DECISION: retourne decisions=[...] dans l'ordre c1,c2,...; 0=REJET et n=libelle n."
                : "DECISION: pour chaque cle cN, 0=REJET; n=libelle n.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le juge semantique de precision des candidats. Lis dans "
                + "PREUVES_ATOMIQUES le type d'instance attendu. La seule question est: "
                + "le libelle choisi nomme-t-il LUI-MEME une instance unique, concrete, "
                + "autonome et retrouvable de ce type? Le fait que l'extrait ou la rubrique "
                + "contienne, annonce ou conseille de bonnes instances ne suffit jamais. "
                + "Rejette avec 0 tout axe de placement, en-tete, role, horaire, periode, "
                + "categorie, collection, rubrique, liste, sommaire, conseil, exemple "
                + "generique, instruction, fragment, metadonnee ou document global. Rejette "
                + "aussi un libelle identique a un axe affiche ou qui n'en est qu'une "
                + "variante mineure. Un autre libelle du chemin ne peut sauver un fragment "
                + "que s'il nomme lui-meme une instance unique du type attendu; jamais une "
                + "categorie ou une collection. Les libelles sont ordonnes du plus direct "
                + "au plus ancestral: si plusieurs conviennent, choisis toujours le premier; "
                + "ne remplace jamais un nom direct precis par un ancetre generique. "
                + "Exemple generique: si le type attendu est "
                + "un equipement, 'Equipements', 'Conseils' et 'Modele du matin' valent 0, "
                + "alors que 'Modele AX-17' peut etre accepte si la preuve le soutient. "
                + "Ne demande pas qu'une instance atomique satisfasse a elle seule tout le "
                + "livrable compose. Les hypotheses restent revisables. En cas de doute, "
                + "choisis 0. Sinon choisis l'index exact du meilleur libelle. Juge chaque "
                + "candidat independamment, sans quota. Retourne uniquement l'objet JSON "
                + "compact, sans markdown ni commentaire."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCanonicalTitleCandidateBatchAuditMessages(
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            IReadOnlyList<EvidenceItem> candidates,
            bool useOrderedDecisionContract)
    {
        var expectedType = string.IsNullOrWhiteSpace(candidateObjectType)
            ? ReadSemanticCandidateObjectType(semanticPlan)
            : candidateObjectType;
        var prompt = new StringBuilder()
            .Append("TYPE ATOMIQUE ATTENDU: ")
            .AppendLine(TrimPromptValue(expectedType, 120));
        if (!string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            prompt.Append("REGLE D'ELIGIBILITE LLM: ")
                .AppendLine(TrimPromptValue(candidateEligibilityRule, 180));
        }
        prompt.AppendLine("CANDIDATS A JUGER DANS CE LOT:");
        for (var candidateIndex = 0;
             candidateIndex < candidates.Count;
             candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            candidate.SelectionHints.TryGetValue(
                "sourceAnchorLabel",
                out var canonicalTitle);
            prompt.Append(CandidateAuditKey(candidateIndex))
                .Append(" [")
                .Append(candidate.EvidenceId)
                .Append("] | titre_canonique=\"")
                .Append(TrimPromptValue(canonicalTitle, 100))
                .Append("\" | structure=")
                .Append(candidate.SelectionHints.TryGetValue(
                    "kind",
                    out var kind)
                        ? TrimPromptValue(kind, 28)
                        : TrimPromptValue(candidate.SourceKind, 28))
                .Append(" | preuve=")
                .AppendLine(TrimPromptValue(candidate.Excerpt, 88));
        }
        prompt.AppendLine(useOrderedDecisionContract
            ? "DECISION: retourne decisions=[...] dans l'ordre c1,c2,...; 1 accepte exactement le titre canonique et 0 rejette."
            : "DECISION: pour chaque cle cN, 1 accepte exactement le titre canonique; 0 rejette.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Le backend a deja epingle mecaniquement le titre canonique exact et sa preuve. "
                + "Tu gardes la seule decision semantique: ce TITRE nomme-t-il lui-meme une "
                + "instance concrete, autonome et utilisable du TYPE ATOMIQUE attendu? Accepte "
                + "un titre valide meme si l'extrait contient ensuite des composants, des "
                + "etapes ou des instructions: ces details prouvent l'instance et ne transforment "
                + "pas son titre en fragment. Rejette seulement un titre qui nomme clairement "
                + "une categorie, collection, rubrique, role, horaire, instruction, metadonnee, "
                + "document global ou mauvais type. Une instance n'a pas a convenir a chaque "
                + "case du livrable; son placement sera juge plus tard. N'invente, ne renomme "
                + "et ne generalise aucun titre. Juge chaque candidat independamment, sans quota, "
                + "et retourne uniquement l'objet JSON entier.") ,
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static int ResolveCandidateBatchOutputTokens(int candidateCount)
        // A grammar-constrained object can still be emitted with whitespace and
        // line breaks by a small local model. Reserve enough tokens for the full
        // key set so a semantically non-zero decision is not truncated merely
        // because it is less compact than an all-zero response.
        => Math.Clamp(64 + Math.Max(1, candidateCount) * 12, 128, 640);

    private static int ResolveCandidateParentReviewOutputTokens(int candidateCount)
        => Math.Clamp(64 + Math.Max(1, candidateCount) * 14, 128, 480);

    private static IReadOnlyList<string> BuildCandidateBatchDisplayValueOptions(
        EvidenceItem candidate)
        => BuildCandidateDisplayValueOptions(candidate)
            .Take(4)
            .ToArray();

    private static IReadOnlyList<string> BuildCandidateBatchInitialDisplayValueOptions(
        EvidenceItem candidate)
    {
        if (candidate.SelectionHints.TryGetValue(
                "sourceAnchorLabel",
                out var canonicalTitle)
            && !string.IsNullOrWhiteSpace(canonicalTitle))
        {
            return new[] { TrimDisplayValue(canonicalTitle, 160) };
        }

        return BuildCandidateDisplayValueOptions(candidate)
            .Take(4)
            .ToArray();
    }

    private static bool HasResolvedSourceAnchorLabel(EvidenceItem candidate)
        => candidate.SelectionHints.TryGetValue(
               "sourceAnchorLabel",
               out var sourceAnchorLabel)
           && !string.IsNullOrWhiteSpace(sourceAnchorLabel);

    private static string FormatCandidateAuditDecision(
        CandidateCollectionAuditDecision decision)
        => decision.ApprovedCandidates.Count == 0
            ? "AUCUN"
            : string.Join(
                Environment.NewLine,
                decision.ApprovedCandidates.Select(static approval =>
                    approval.EvidenceId + "=" + approval.DisplayValue));

    private static SourceBackedAgentCompletion
        AggregateCandidateBatchCompletions(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            string content,
            string finishReason)
        => new(
            content,
            Array.Empty<SourceBackedAgentToolCall>(),
            finishReason,
            SumNullable(completions.Select(static item => item.PromptTokens)),
            SumNullable(completions.Select(static item => item.CompletionTokens)),
            SumNullable(completions.Select(static item => item.ServerCacheTokens)),
            SumNullable(completions.Select(
                static item => item.ServerPromptTokensEvaluated)),
            SumNullable(completions.Select(
                static item => item.ServerPromptMilliseconds)),
            SumNullable(completions.Select(static item =>
                item.ServerPredictedTokens)),
            SumNullable(completions.Select(static item =>
                item.ServerPredictedMilliseconds)));

}
