using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildCandidateCollectionDecisionMessage(
        SemanticCandidateStrategyOutcome candidateStrategy,
        EvidenceBundle bundle,
        IReadOnlyList<string> approvedEvidenceIds,
        int approvedSourceCount,
        int requiredEvidenceCount,
        string? semanticFeedback,
        bool allowAdaptiveFlatSelection)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "DECISION DE COLLECTE — ne redige pas encore. Le contexte compact "
            + "precedent contient deja la demande, la mission, les preuves refusees "
            + "et les actions consommees.");
        if (!string.IsNullOrWhiteSpace(candidateStrategy.CandidateObjectType)
            || !string.IsNullOrWhiteSpace(
                candidateStrategy.CandidateEligibilityRule))
        {
            builder.Append("HYPOTHESES REVISABLES: type=")
                .Append(TrimPromptValue(
                    candidateStrategy.CandidateObjectType,
                    90))
                .Append(" | eligibilite=")
                .AppendLine(TrimPromptValue(
                    candidateStrategy.CandidateEligibilityRule,
                    130));
        }
        if (candidateStrategy.CandidateScopePaths.Count > 0)
        {
            builder.Append("PERIMETRES: ")
                .AppendLine(string.Join(
                    " | ",
                    candidateStrategy.CandidateScopePaths.Select(path =>
                        string.IsNullOrWhiteSpace(path)
                            ? "corpus complet"
                            : path)));
        }
        builder.Append("CANDIDATS CITABLES OBSERVES: ")
            .Append(approvedSourceCount)
            .Append(" / ")
            .AppendLine(requiredEvidenceCount.ToString());
        var approvedItems = approvedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .DistinctBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (approvedItems.Length > 0)
        {
            builder.AppendLine(
                "PREUVES APPROUVEES DISPONIBLES — inventaire sans ordre de preference:");
            foreach (var item in approvedItems)
            {
                builder.Append("- ")
                    .Append(item.EvidenceId)
                    .Append(" | ")
                    .Append(TrimPromptValue(
                        GetEvidenceDisplayValue(item),
                        120));
                var document = item.DocName ?? item.DocPath;
                if (!string.IsNullOrWhiteSpace(document))
                {
                    builder.Append(" | document=")
                        .Append(TrimPromptValue(document, 100));
                }
                if (item.PageStart is > 0)
                    builder.Append(" | page=").Append(item.PageStart.Value);
                builder.AppendLine();
            }
        }
        if (allowAdaptiveFlatSelection)
        {
            builder.AppendLine(
                "CIBLE PLATE PROVISOIRE: le total ci-dessus n'est pas une cardinalite "
                + "structurelle. Si les preuves auditees suffisent semantiquement a la "
                + "demande, appelle submit_evidence_selection avec le sous-ensemble exact "
                + "que tu veux faire rediger. Si plusieurs interpretations materielles "
                + "restent possibles et que seul l'utilisateur peut trancher, appelle "
                + "request_user_clarification. Sinon, poursuis avec un outil documentaire.");
        }
        if (!string.IsNullOrWhiteSpace(semanticFeedback))
        {
            builder.Append("RETOUR DU DERNIER JUGEMENT: ")
                .AppendLine(TrimPromptValue(
                    semanticFeedback,
                    260));
        }
        builder.AppendLine(
            "Choisis librement une ou plusieurs prochaines actions utiles selon le "
            + "rendement observe. Ne repete ni une route consommee ni les memes termes "
            + "simplement permutes. Choisis l'outil, ses arguments et la strategie suivante "
            + "a partir de la demande, des observations, des refus semantiques et des "
            + "capacites exposees. Pour un livrable compose de plusieurs unites, vise des "
            + "objets sources nommes, sous-themes ou familles encore manquants; ne recopie "
            + "pas le nom du livrable ni une concatenation de ses axes comme requete de "
            + "contenu. Une pagination continue exactement la meme route; une "
            + "route differente recommence a offset 0.");

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateCollectionRetrievalMessages(
            IReadOnlyList<SourceBackedAgentMessage> workingMessages,
            SemanticCandidateStrategyOutcome candidateStrategy,
            EvidenceBundle bundle,
            IReadOnlyList<string> approvedEvidenceIds,
            int approvedSourceCount,
            int requiredEvidenceCount,
            string? semanticFeedback,
            bool allowAdaptiveFlatSelection)
    {
        // The question, mission and consumed actions are already represented in the
        // compact working state. Repeating them here can make the tool schemas alone
        // overflow a 4K context after a large audit.
        var messages = workingMessages.ToList();
        messages.Add(SourceBackedAgentMessage.User(
            BuildCandidateCollectionDecisionMessage(
                candidateStrategy,
                bundle,
                approvedEvidenceIds,
                approvedSourceCount,
                requiredEvidenceCount,
                semanticFeedback,
                allowAdaptiveFlatSelection)));
        return messages;
    }
}
