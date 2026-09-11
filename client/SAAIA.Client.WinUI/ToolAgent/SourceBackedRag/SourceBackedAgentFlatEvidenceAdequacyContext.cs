using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<EvidenceItem> SelectFlatEvidenceAdequacyItems(
        EvidenceBundle bundle,
        IReadOnlyList<string> evidenceIds,
        int provisionalTarget,
        string atomicEvidenceMode)
    {
        var eligibleItems = evidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null
                                  && IsMechanicallyCitableCandidate(item)
                                  && HasRenderableEvidenceValue(item))
            .Cast<EvidenceItem>()
            .ToArray();
        return string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.OrdinalIgnoreCase)
            ? eligibleItems
            : eligibleItems
                .DistinctBy(
                    static item => item.VisibleSourceKey,
                    StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(provisionalTarget, 1, 8))
                .ToArray();
    }

    private static string BuildFlatEvidenceAdequacyContext(
        SourceBackedIntake intake,
        string semanticPlan,
        IReadOnlyList<EvidenceItem> evidence,
        int provisionalTarget,
        IReadOnlySet<string> incumbentEvidenceIds,
        string semanticFeedback)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE ORIGINALE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 420));
        context.Append("CIBLE PROVISOIRE DU ROUTEUR: ")
            .Append(provisionalTarget)
            .AppendLine(
                " preuves. Ce nombre n'est pas une obligation utilisateur ni une grille.");
        context.Append("MISSION PROVISOIRE: ")
            .AppendLine(TrimPromptValue(semanticPlan, 420));
        if (!string.IsNullOrWhiteSpace(semanticFeedback))
        {
            context.Append("RETOUR SEMANTIQUE DU BROUILLON PRECEDENT: ")
                .AppendLine(TrimPromptValue(semanticFeedback, 620));
        }
        if (incumbentEvidenceIds.Count > 0)
        {
            context.AppendLine(
                "REVUE COMPARATIVE: certains candidats portent le statut "
                + "selection_llm_precedente et d'autres nouveau_candidat. Le statut "
                + "precedent n'est ni une approbation ni une preference. Recompare "
                + "semantiquement tous les candidats visibles, remplace librement les "
                + "anciens candidats plus faibles et retourne le meilleur pool de preuves.");
        }
        context.AppendLine(
            "PREUVES APPROUVEES DISPONIBLES — sans ordre de preference:");
        foreach (var item in evidence)
        {
            context.Append("- ")
                .Append(item.EvidenceId)
                .Append(" | ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 120))
                .Append(" | extrait=")
                .Append(TrimPromptValue(item.Excerpt, 240));
            if (incumbentEvidenceIds.Count > 0)
            {
                context.Append(" | statut=")
                    .Append(incumbentEvidenceIds.Contains(item.EvidenceId)
                        ? "selection_llm_precedente"
                        : "nouveau_candidat");
            }
            var document = item.DocName ?? item.DocPath;
            if (!string.IsNullOrWhiteSpace(document))
            {
                context.Append(" | document=")
                    .Append(TrimPromptValue(document, 100));
            }
            if (item.PageStart is > 0)
                context.Append(" | page=").Append(item.PageStart.Value);
            context.AppendLine();
        }
        context.AppendLine(
            "STRUCTURE DU LIVRABLE: un nombre de points, une liste, un tableau, "
            + "un resume ou une formulation orientee decision decrit la forme de "
            + "la reponse a produire. Cette structure n'a pas besoin d'exister deja "
            + "dans une source sous la meme forme. Evalue plutot si les extraits "
            + "visibles contiennent assez de claims documentaires distincts pour "
            + "que le writer puisse synthetiser fidelement le livrable demande.");
        context.AppendLine(
            "QUALITE DU POOL: chaque preuve selectionnee doit soutenir directement au "
            + "moins un claim demande. Une identite de document, une mention editoriale "
            + "ou un contexte administratif ne prouve pas a lui seul le contenu du corps "
            + "du document, sauf si la demande porte explicitement sur cette information. "
            + "Ne remplis jamais la cible provisoire avec des preuves faibles ou redondantes.");
        context.AppendLine(
            "ANCRAGE DOCUMENTAIRE: si la demande originale nomme clairement un "
            + "document, ce document definit la portee attendue. Lorsque les claims "
            + "manquants peuvent appartenir a ce document et qu'une preuve visible "
            + "permet de l'ancrer, choisis submit_flat_evidence_context_gap avec "
            + "l'EvidenceId correspondant. Choisis le gap global seulement si la "
            + "demande originale exige reellement un autre document, ou si aucune "
            + "preuve visible ne permet d'ancrer le document nomme.");
        var requestedDocument =
            SourceBackedQuestionFocus.ExtractFirstSpecificDocumentName(intake);
        if (!string.IsNullOrWhiteSpace(requestedDocument))
        {
            context.Append("DOCUMENT EXPLICITEMENT NOMME: ")
                .AppendLine(TrimPromptValue(requestedDocument, 180));
        }
        context.AppendLine(
            "Decide depuis la demande originale et ces preuves. Ne continue jamais "
            + "uniquement pour atteindre la cible provisoire. selectionner signifie que "
            + "le sous-ensemble choisi permet une reponse utile, complete et honnete. "
            + "Avant de selectionner, verifie toutes les composantes explicitement demandees "
            + "dans la demande originale. Utilise submit_flat_evidence_selection seulement "
            + "si aucune composante demandee ne manque; sinon utilise "
            + "submit_flat_evidence_context_gap si ces manques appartiennent au meme item "
            + "ou document qu'une preuve visible, et choisis son EvidenceId. Utilise "
            + "submit_flat_evidence_gap si un autre item, document ou une recherche globale "
            + "est necessaire. Avec ce gap global, usefulEvidenceIds doit conserver uniquement "
            + "le meilleur sous-ensemble partiel deja utile, meme si ce pool reste insuffisant; "
            + "il peut etre vide. Cela ne declare jamais la reponse suffisante. Dans les deux "
            + "cas, nomme chaque manque documentaire. clarifier "
            + "signifie que plusieurs interpretations materielles restent possibles et "
            + "que seul l'utilisateur peut trancher. continuer exige un manque documentaire "
            + "precis que la recherche peut encore resoudre.");
        context.AppendLine(
            "COHERENCE DE PROVENANCE: plusieurs documents restent autorises pour une "
            + "comparaison, une aggregation explicitement demandee ou des claims clairement "
            + "compatibles. Refuse en revanche une fusion silencieuse de variantes, versions, "
            + "positions ou procedures documentaires en un seul objet coherent. Dans ce cas, "
            + "selectionne un ensemble coherent, distingue explicitement les variantes dans "
            + "le livrable, ou demande les preuves/contexte encore necessaires.");
        return context.ToString().Trim();
    }
}
