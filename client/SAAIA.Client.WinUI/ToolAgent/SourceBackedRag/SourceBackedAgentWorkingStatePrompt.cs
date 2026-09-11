using System.Globalization;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void CompactWorkingMessages(
        ICollection<SourceBackedAgentMessage> messages,
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        IReadOnlyList<RetrievalRequest> executedRequests,
        LlmEvidenceWorkspace evidenceWorkspace,
        string? semanticReviewFeedback,
        bool emergencyContextRecovery = false,
        bool includeEvidenceDetails = true)
    {
        var requiredEvidenceCount = ReadRequiredAtomicEvidenceCount(semanticPlan);
        var observedOrder = observedEvidenceIds
            .Select(static (id, index) => (id, index))
            .ToDictionary(
                static pair => pair.id,
                static pair => pair.index,
                StringComparer.OrdinalIgnoreCase);
        var observedEvidence = observedEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .ToArray();
        var citableEvidence = observedEvidence
            .Where(IsMechanicallyCitableCandidate)
            .ToArray();
        var observedCitableSourceCount = citableEvidence
            .Select(static item => item.VisibleSourceKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var constrainedContext = _options.MaximumContextTokens <= 4096;
        var aggressiveCompaction =
            constrainedContext
            || emergencyContextRecovery
            || requiredEvidenceCount is > 1;
        var compactSingleSelection = requiredEvidenceCount == 1;
        var planCharacters = compactSingleSelection
            ? 650
            : aggressiveCompaction
            ? emergencyContextRecovery ? 620 : 750
            : 1800;
        var actionCount = aggressiveCompaction
            ? emergencyContextRecovery ? 4 : 6
            : 16;
        var queryCharacters = aggressiveCompaction
            ? emergencyContextRecovery ? 64 : 90
            : 150;
        var documentCharacters = aggressiveCompaction
            ? emergencyContextRecovery ? 54 : 76
            : 120;
        var constrainedEvidenceCapacity = requiredEvidenceCount is > 1
            ? observedCitableSourceCount >= requiredEvidenceCount.Value
                ? Math.Max(
                    requiredEvidenceCount.Value
                    + (emergencyContextRecovery ? 4 : 6),
                    24)
                : Math.Clamp(
                    requiredEvidenceCount.Value / 2 + 4,
                    10,
                    emergencyContextRecovery ? 12 : 16)
            : emergencyContextRecovery ? 10 : 14;
        var evidenceCapacity = aggressiveCompaction
            ? Math.Min(_options.MaximumWorkingEvidenceItems, constrainedEvidenceCapacity)
            : _options.MaximumWorkingEvidenceItems;
        var evidenceValueCharacters = aggressiveCompaction
            ? emergencyContextRecovery ? 80 : 96
            : _options.MaximumWorkingExcerptCharacters;
        var feedbackCharacters = aggressiveCompaction
            ? emergencyContextRecovery ? 420 : 650
            : 1000;
        var compactState = new StringBuilder();
        compactState.AppendLine("ETAT DE TRAVAIL COMPACT — les EvidenceId ci-dessous restent stables.");
        compactState.AppendLine("PLAN DE MISSION PRODUIT PAR LE LLM:");
        compactState.AppendLine(TrimPromptValue(semanticPlan, planCharacters));
        compactState.AppendLine("ACTIONS DEJA EXECUTEES:");
        foreach (var request in executedRequests.TakeLast(actionCount))
        {
            compactState.Append("- ")
                .Append(request.ToolName)
                .Append(" | requete=")
                .Append(TrimPromptValue(request.Query, queryCharacters));
            if (!string.IsNullOrWhiteSpace(request.CategoryPath))
                compactState.Append(" | categorie=").Append(TrimPromptValue(
                    request.CategoryPath,
                    aggressiveCompaction ? 48 : 100));
            if (request.Limit is > 0)
                compactState.Append(" | limite=").Append(request.Limit);
            if (request.Offset is >= 0)
                compactState.Append(" | offset=").Append(request.Offset);
            if (request.MaterializedEvidenceCount is >= 0)
            {
                compactState
                    .Append(" | preuves_materialisees=")
                    .Append(request.MaterializedEvidenceCount);
            }
            if (request.NewEvidenceCount is >= 0)
            {
                compactState
                    .Append(" | nouvelles_preuves=")
                    .Append(request.NewEvidenceCount);
                if (request.NewEvidenceCount == 0)
                    compactState.Append(" | rendement=aucune_nouvelle_preuve");
            }
            if (request.NextOffset is >= 0)
            {
                compactState
                    .Append(" | prochain_offset_exact=")
                    .Append(request.NextOffset);
            }
            if (!string.IsNullOrWhiteSpace(request.DocPath)
                || !string.IsNullOrWhiteSpace(request.DocRef))
            {
                compactState.Append(" | document=")
                    .Append(TrimPromptValue(
                        request.DocPath ?? request.DocRef,
                        documentCharacters));
            }
            if (request.PageStart is > 0)
            {
                compactState.Append(" | pages=")
                    .Append(request.PageStart)
                    .Append('-')
                    .Append(request.PageEnd ?? request.PageStart);
            }
            compactState.AppendLine();
        }

        AppendPendingPaginationContinuations(
            compactState,
            executedRequests,
            aggressiveCompaction
                ? emergencyContextRecovery ? 3 : 5
                : 8,
            queryCharacters,
            documentCharacters,
            aggressiveCompaction);

        var retainedEvidence = evidenceWorkspace.RetainedEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .Where(item => observedOrder.ContainsKey(item.EvidenceId))
            .Where(IsMechanicallyCitableCandidate)
            .Take(evidenceCapacity)
            .ToArray();
        var recentEvidence = citableEvidence
            .Where(item => !evidenceWorkspace.RetainedEvidenceIdSet.Contains(
                item.EvidenceId))
            .OrderByDescending(item =>
                observedOrder.GetValueOrDefault(item.EvidenceId, -1))
            .ToArray();
        var selectedEvidence = includeEvidenceDetails
            ? SelectCompactEvidenceItems(
                    retainedEvidence,
                    recentEvidence,
                    evidenceCapacity)
                .ToArray()
            : Array.Empty<EvidenceItem>();
        var navigationCapacity = emergencyContextRecovery
            ? 8
            : Math.Min(
                _options.MaximumWorkingEvidenceItems,
                Math.Clamp(
                    requiredEvidenceCount.GetValueOrDefault() + 8,
                    12,
                    40));
        var navigationAnchors = observedEvidence
            .Where(static item =>
                item.RiskFlags.Contains(
                    "orientation_only",
                    StringComparer.OrdinalIgnoreCase)
                || item.RiskFlags.Contains(
                    "missing_grounded_content_card_evidence",
                    StringComparer.OrdinalIgnoreCase))
            .GroupBy(
                static item => string.Join(
                    "|",
                    item.DocPath ?? item.DocName ?? item.DocId ?? string.Empty,
                    item.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    item.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    item.CategoryPath ?? string.Empty),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => observedOrder.GetValueOrDefault(item.EvidenceId, -1))
                .First())
            .OrderByDescending(item => observedOrder.GetValueOrDefault(item.EvidenceId, -1))
            .Take(navigationCapacity)
            .ToArray();
        var visibleSourceGroups = selectedEvidence
            .Concat(navigationAnchors)
            .GroupBy(
                static item => item.DocPath ?? item.DocName ?? item.DocId ?? "source-inconnue",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceAliases = visibleSourceGroups
            .Select(static (group, index) => new
            {
                Source = group.Key,
                Alias = "D" + (index + 1).ToString(CultureInfo.InvariantCulture),
                Item = group.First()
            })
            .ToArray();
        var aliasBySource = sourceAliases.ToDictionary(
            static item => item.Source,
            static item => item.Alias,
            StringComparer.OrdinalIgnoreCase);
        var visibleSourceAliases = selectedEvidence
            .Select(static item => item.VisibleSourceKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static (sourceKey, index) => new
            {
                SourceKey = sourceKey,
                Alias = "V" + (index + 1).ToString(
                    "D3",
                    CultureInfo.InvariantCulture)
            })
            .ToDictionary(
                static item => item.SourceKey,
                static item => item.Alias,
                StringComparer.OrdinalIgnoreCase);
        compactState.AppendLine(
            aggressiveCompaction
                ? "SOURCES: D est un alias de lecture. Pour un outil, copie le docPath complet; jamais D dans docId/docPath."
                : "SOURCES COMPACTES: D est un alias de lecture seulement. Ne transmets jamais D1/D2 dans docId ou docPath; pour cibler un outil, copie exactement la valeur docPath ci-dessous dans l'argument docPath. docId accepte uniquement un identifiant backend opaque.");
        foreach (var source in sourceAliases)
        {
            compactState
                .Append("- ")
                .Append(source.Alias)
                .Append(" | docPath=")
                .Append(TrimPromptValue(source.Source, aggressiveCompaction ? 140 : 180));
            if (!string.IsNullOrWhiteSpace(source.Item.CategoryPath))
            {
                compactState
                    .Append(" | categorie=")
                    .Append(TrimPromptValue(source.Item.CategoryPath, 80));
            }
            compactState.AppendLine();
        }
        if (includeEvidenceDetails)
        {
            compactState
                .Append("PREUVES DEJA MONTREES: ")
                .Append(selectedEvidence.Length)
                .Append(" conservees sur ")
                .Append(citableEvidence.Length)
                .Append(" citables observees, reparties sur ")
                .Append(observedCitableSourceCount)
                .AppendLine(" sources visibles distinctes.");
            compactState.AppendLine(
                "groupe_source est mecanique: plusieurs EvidenceId du meme groupe designent la meme source visible et ne comptent qu'une fois pour l'unicite.");
        }
        else
        {
            compactState.AppendLine(
                "DETAILS DES PREUVES: presentes une seule fois dans le message de collecte qui suit.");
        }
        foreach (var item in selectedEvidence)
        {
            var sourceKey = item.DocPath ?? item.DocName ?? item.DocId ?? "source-inconnue";
            if (compactSingleSelection)
            {
                compactState.Append("- ")
                    .Append(item.EvidenceId)
                    .Append(" | ")
                    .Append(item.SourceKind)
                    .Append(evidenceWorkspace.RetainedEvidenceIdSet.Contains(
                        item.EvidenceId)
                        ? " | statut=retenue_par_ta_decision"
                        : " | statut=recente")
                    .Append(" | ")
                    .Append(aliasBySource.GetValueOrDefault(sourceKey, "?"))
                    .Append(" | p.")
                    .Append(item.PageStart?.ToString() ?? "?")
                    .Append(" | ")
                    .AppendLine(TrimPromptValue(item.Excerpt, evidenceValueCharacters));
                if (evidenceWorkspace.RetainedEvidenceIdSet.Contains(
                        item.EvidenceId)
                    && evidenceWorkspace.DecisionNotes.TryGetValue(
                        item.EvidenceId,
                        out var compactDecisionNote))
                {
                    compactState.Append("  note_de_ta_decision=")
                        .AppendLine(TrimPromptValue(
                            compactDecisionNote,
                            120));
                }
                continue;
            }

            if (aggressiveCompaction)
            {
                compactState.Append("- ")
                    .Append(item.EvidenceId)
                    .Append(" | groupe_source=")
                    .Append(visibleSourceAliases.GetValueOrDefault(
                        item.VisibleSourceKey,
                        "?"));
                if (evidenceWorkspace.RetainedEvidenceIdSet.Contains(
                        item.EvidenceId))
                {
                    compactState.Append(" | retenue");
                }
                compactState.Append(" | ")
                    .Append(aliasBySource.GetValueOrDefault(sourceKey, "?"))
                    .Append(" | p.")
                    .Append(item.PageStart?.ToString() ?? "?");
                if (item.PageEnd is > 0 && item.PageEnd != item.PageStart)
                    compactState.Append('-').Append(item.PageEnd);
                if (string.Equals(
                        item.SourceKind,
                        "canonical_content_card",
                        StringComparison.OrdinalIgnoreCase))
                {
                    compactState.Append(" | carte");
                }
                compactState.Append(" | valeur=")
                    .AppendLine(TrimPromptValue(
                        GetEvidenceDisplayValue(item),
                        evidenceValueCharacters));
                continue;
            }

            compactState.Append("- ")
                .Append(item.EvidenceId)
                .Append(" | ")
                .Append(item.SourceKind)
                .Append(" | groupe_source=")
                .Append(visibleSourceAliases.GetValueOrDefault(
                    item.VisibleSourceKey,
                    "?"))
                .Append(evidenceWorkspace.RetainedEvidenceIdSet.Contains(
                    item.EvidenceId)
                    ? " | statut=retenue_par_ta_decision"
                    : " | statut=recente")
                .Append(" | ")
                .Append(aliasBySource.GetValueOrDefault(sourceKey, "?"))
                .Append(" | p.")
                .Append(item.PageStart?.ToString() ?? "?");
            if (item.PageEnd is > 0 && item.PageEnd != item.PageStart)
                compactState.Append('-').Append(item.PageEnd);
            if (!string.IsNullOrWhiteSpace(item.ChunkId))
            {
                compactState.Append(" | chunk=").Append(TrimPromptValue(
                    item.ChunkId,
                    aggressiveCompaction ? 44 : 80));
            }
            if (!string.IsNullOrWhiteSpace(item.ContentCardId))
            {
                compactState.Append(" | contentCardId=").Append(TrimPromptValue(
                    item.ContentCardId,
                    aggressiveCompaction ? 44 : 80));
            }
            if (item.SelectionHints.TryGetValue("kind", out var kind))
                compactState.Append(" | type=").Append(TrimPromptValue(
                    kind,
                    aggressiveCompaction ? 28 : 40));
            AppendEvidenceHeadingPath(
                compactState,
                item,
                aggressiveCompaction ? 70 : 110);
            if (item.SelectionHints.TryGetValue("hasGroundedEvidence", out var grounded))
                compactState.Append(" | grounded=").Append(TrimPromptValue(grounded, 10));
            var displayedEvidence = string.Equals(
                item.SourceKind,
                "canonical_content_card",
                StringComparison.OrdinalIgnoreCase)
                ? GetEvidenceDisplayValue(item)
                : item.Excerpt;
            compactState.Append(" | valeur=").AppendLine(
                TrimPromptValue(displayedEvidence, evidenceValueCharacters));
            if (evidenceWorkspace.RetainedEvidenceIdSet.Contains(item.EvidenceId)
                && evidenceWorkspace.DecisionNotes.TryGetValue(
                    item.EvidenceId,
                    out var decisionNote))
            {
                compactState.Append("  note_de_ta_decision=")
                    .AppendLine(TrimPromptValue(decisionNote, 180));
            }
        }

        var rejectedEvidence = evidenceWorkspace.RejectedEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? (Id: id, Item: item)
                : (Id: id, Item: (EvidenceItem?)null))
            .TakeLast(16)
            .ToArray();
        if (rejectedEvidence.Length > 0)
        {
            compactState.AppendLine(
                "PREUVES REFUSEES PAR TES DECISIONS SEMANTIQUES — ne les reutilise pas et ne relance pas une recherche seulement pour les retrouver:");
            foreach (var rejected in rejectedEvidence)
            {
                compactState.Append("- ")
                    .Append(rejected.Id);
                if (rejected.Item is not null)
                {
                    compactState.Append(" | valeur=")
                        .Append(TrimPromptValue(
                            GetEvidenceDisplayValue(rejected.Item),
                            80));
                }
                if (evidenceWorkspace.DecisionNotes.TryGetValue(
                    rejected.Id,
                    out var decisionNote))
                {
                    compactState.Append(" | motif=")
                        .Append(TrimPromptValue(decisionNote, 140));
                }
                compactState.AppendLine();
            }
        }

        if (navigationAnchors.Length > 0)
        {
            compactState.AppendLine(
                "ANCRES DE NAVIGATION NON CITABLES: utilise-les seulement pour choisir un prochain outil; aucun identifiant E n'est fourni volontairement.");
            foreach (var item in navigationAnchors)
            {
                var sourceKey = item.DocPath ?? item.DocName ?? item.DocId ?? "source-inconnue";
                compactState.Append("- ")
                    .Append(aliasBySource.GetValueOrDefault(sourceKey, "?"))
                    .Append(" | p.")
                    .Append(item.PageStart?.ToString(CultureInfo.InvariantCulture) ?? "?");
                if (item.PageEnd is > 0 && item.PageEnd != item.PageStart)
                    compactState.Append('-').Append(item.PageEnd);
                compactState.Append(" | ").AppendLine(
                    TrimPromptValue(item.Excerpt, evidenceValueCharacters));
            }
        }

        if (!string.IsNullOrWhiteSpace(semanticReviewFeedback))
        {
            compactState.AppendLine("DERNIERE REVUE SEMANTIQUE INDEPENDANTE:");
            compactState.AppendLine(TrimPromptValue(
                semanticReviewFeedback,
                feedbackCharacters));
        }
        compactState.AppendLine(
            "SUITE: decide librement de chercher ce qui manque ou de repondre si toutes les dimensions sont suffisamment prouvees.");
        messages.Clear();
        var useLeanActionPrompt =
            aggressiveCompaction || requiredEvidenceCount == 1;
        messages.Add(SourceBackedAgentMessage.System(
            BuildCompactActionSystemPrompt(useLeanActionPrompt)));
        messages.Add(SourceBackedAgentMessage.User(BuildWorkingUserContext(
            intake,
            _options.MaximumContextTokens,
            emergencyContextRecovery)));
        messages.Add(SourceBackedAgentMessage.User(compactState.ToString().Trim()));
    }

    private static void AppendEvidenceHeadingPath(
        StringBuilder builder,
        EvidenceItem item,
        int maximumCharacters)
    {
        if (!item.SelectionHints.TryGetValue(
                "headingPath",
                out var headingPath)
            || string.IsNullOrWhiteSpace(headingPath))
        {
            return;
        }

        builder
            .Append(" | chemin=")
            .Append(TrimPromptValue(
                headingPath,
                maximumCharacters));
    }

}
