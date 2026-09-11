using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCompactResearchTransitionDecisionMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            IReadOnlyList<string> activeScopePaths,
            int approvedSourceCount,
            int requiredEvidenceCount,
            string? semanticFeedback,
            int maximumWorkingEvidenceItems)
    {
        var context = new StringBuilder();
        context.AppendLine("CONTEXTE COMPACT DE RECUPERATION");
        context.Append("OBJECTIF UTILISATEUR: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 320));
        context.Append("ETAT: ")
            .Append(approvedSourceCount)
            .Append(" preuves approuvees sur ")
            .Append(requiredEvidenceCount)
            .AppendLine(" attendues.");
        context.Append("MISSION LLM: ")
            .AppendLine(TrimPromptValue(semanticPlan, 320));
        if (!string.IsNullOrWhiteSpace(candidateObjectType))
        {
            context.Append("TYPE DE PREUVE: ")
                .AppendLine(TrimPromptValue(candidateObjectType, 120));
        }
        if (!string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            context.Append("ELIGIBILITE: ")
                .AppendLine(TrimPromptValue(candidateEligibilityRule, 200));
        }
        if (activeScopePaths.Count > 0)
        {
            context.Append("PERIMETRES LLM: ")
                .AppendLine(string.Join(
                    " | ",
                    activeScopePaths.Take(4).Select(static path =>
                        string.IsNullOrWhiteSpace(path)
                            ? "corpus complet"
                            : TrimPromptValue(path, 90))));
        }

        var pagination = DescribePaginationContinuationOptions(
            executedRequests,
            2);
        if (!string.IsNullOrWhiteSpace(pagination))
        {
            context.Append("CONTINUATIONS PAGINEES: ")
                .AppendLine(TrimPromptValue(pagination, 320));
        }
        context.AppendLine("RENDEMENT RECENT:");
        foreach (var request in executedRequests.TakeLast(4))
        {
            context.Append("- ")
                .Append(request.ToolName)
                .Append(" | q=")
                .Append(string.IsNullOrWhiteSpace(request.Query)
                    ? "vide"
                    : TrimPromptValue(request.Query, 70))
                .Append(" | offset=")
                .Append(request.Offset)
                .Append(" | nouvelles=")
                .Append(request.NewEvidenceCount)
                .Append(" | materialisees=")
                .Append(request.MaterializedEvidenceCount);
            if (request.NextOffset is >= 0)
                context.Append(" | nextOffset=").Append(request.NextOffset);
            context.AppendLine();
        }

        var retrievalConditions = BuildObservedRetrievalConditions(bundle, 4);
        if (retrievalConditions.Length > 0)
            context.AppendLine(retrievalConditions);
        context.AppendLine(BuildDocumentFocusInventory(
            bundle,
            Math.Min(4, Math.Max(1, maximumWorkingEvidenceItems))));
        var pendingNavigationIds = SelectPendingNavigationEvidenceIds(
                bundle,
                resolvedNavigationEvidenceIds,
                maximumWorkingEvidenceItems,
                requireResolvableTarget: false)
            .Take(3)
            .ToArray();
        if (pendingNavigationIds.Length > 0)
        {
            context.AppendLine("ANCRES DE NAVIGATION NON RESOLUES:");
            foreach (var evidenceId in pendingNavigationIds)
            {
                if (!bundle.ById.TryGetValue(evidenceId, out var item))
                    continue;
                context.Append("- ")
                    .Append(evidenceId)
                    .Append(" | ")
                    .AppendLine(TrimPromptValue(GetEvidenceDisplayValue(item), 100));
            }
        }
        if (!string.IsNullOrWhiteSpace(semanticFeedback))
        {
            context.Append("BESOIN RESTANT DU JUGE LLM: ")
                .AppendLine(TrimPromptValue(semanticFeedback, 620));
        }
        context.AppendLine(
            "Choisis semantiquement une seule prochaine action parmi les outils exposes. "
            + "Conserve le type de preuve, l'eligibilite et le besoin restant; n'invente "
            + "aucune contrainte. Compare le rendement recent et evite une route sans gain.");
        context.AppendLine(
            "Pour le meme document: expand_document_context lit les voisins d'une preuve; "
            + "refine_focused_document_search cherche le corps par similarite; "
            + "refine_document_navigation cherche seulement structure, titres, sommaire ou "
            + "index. Les routes globales cherchent un autre document. Une continuation "
            + "paginee conserve la route et utilise sa prochaine page inedite.");
        context.AppendLine(
            "Appelle exactement un outil avec ses arguments, sans reponse finale ni outil "
            + "de secours avant la prochaine observation.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique du RAG. Ce contexte a ete reduit "
                + "mecaniquement pour respecter la fenetre, sans decision semantique du "
                + "code. Choisis toi-meme l'unique prochaine action au meilleur "
                + "rendement marginal, en preservant strictement l'objectif et le retour du juge."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCompactCandidateCollectionRetrievalMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            SemanticCandidateStrategyOutcome candidateStrategy,
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            IReadOnlyList<string> approvedEvidenceIds,
            int approvedSourceCount,
            int requiredEvidenceCount,
            string? semanticFeedback,
            int maximumWorkingEvidenceItems,
            bool allowAdaptiveFlatSelection)
    {
        var messages = BuildCompactResearchTransitionDecisionMessages(
                intake,
                semanticPlan,
                candidateStrategy.CandidateObjectType,
                candidateStrategy.CandidateEligibilityRule,
                bundle,
                resolvedNavigationEvidenceIds,
                executedRequests,
                candidateStrategy.CandidateScopePaths,
                approvedSourceCount,
                requiredEvidenceCount,
                semanticFeedback,
                maximumWorkingEvidenceItems)
            .ToList();
        var decision = new StringBuilder();
        decision.AppendLine(
            "DECISION DE COLLECTE COMPACTE: le contexte ci-dessus remplace "
            + "mecaniquement l'historique long; il ne remplace aucune decision du LLM.");
        var visibleItems = approvedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .Take(4)
            .ToArray();
        if (visibleItems.Length > 0)
        {
            decision.AppendLine("ECHANTILLON DU VIVIER APPROUVE, SANS PREFERENCE:");
            foreach (var item in visibleItems)
            {
                decision.Append("- ")
                    .Append(item.EvidenceId)
                    .Append(" | ")
                    .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 90))
                    .Append(" | extrait=")
                    .AppendLine(TrimPromptValue(item.Excerpt, 120));
            }
        }
        if (allowAdaptiveFlatSelection)
        {
            decision.AppendLine(
                "submit_evidence_selection reste autorise uniquement si le besoin restant "
                + "est maintenant couvert par un sous-ensemble exact du vivier. Sinon, "
                + "choisis une action documentaire; request_user_clarification exige une "
                + "ambiguite materielle que seul l'utilisateur peut trancher.");
        }
        decision.AppendLine(
            "Appelle exactement un outil expose. Ne redige aucune reponse finale avant "
            + "la prochaine observation ou une selection explicite de preuves.");
        messages.Add(SourceBackedAgentMessage.User(decision.ToString().Trim()));
        return messages;
    }
}
