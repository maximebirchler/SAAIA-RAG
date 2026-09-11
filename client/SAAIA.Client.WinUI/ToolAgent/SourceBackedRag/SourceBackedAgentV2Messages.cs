using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string? MergeAgentFeedback(params string?[] values)
    {
        var retained = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return retained.Length == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, retained);
    }

    private static void AppendMemoryContext(
        StringBuilder builder,
        SourceBackedMemoryContext? memory,
        int maximumContextTokens,
        int? maximumMemoryCharacters = null)
    {
        if (memory is null)
            return;

        var memoryBuilder = new StringBuilder();
        memoryBuilder.AppendLine("MEMOIRE DE CONVERSATION (contexte seulement, jamais preuve):");
        memoryBuilder.AppendLine("- profil: " + TrimPromptValue(memory.Profile, 120));
        memoryBuilder.AppendLine("- style prefere: " + TrimPromptValue(memory.PreferredStyle, 60));
        memoryBuilder.AppendLine("- mode actif: " + TrimPromptValue(memory.ActiveMode, 40));
        if (!string.IsNullOrWhiteSpace(memory.PreviousUserMessage))
            memoryBuilder.AppendLine("- demande precedente: " + TrimPromptValue(memory.PreviousUserMessage, 220));
        if (!string.IsNullOrWhiteSpace(memory.PreviousAssistantAnswer))
        {
            var hasStructuredTurns = memory.ConversationTurns is { Count: > 0 };
            memoryBuilder.AppendLine("- reponse precedente: " + TrimPromptValue(
                memory.PreviousAssistantAnswer,
                hasStructuredTurns ? 160 : 320));
        }
        if (!string.IsNullOrWhiteSpace(memory.FocusedDocumentName)
            || !string.IsNullOrWhiteSpace(memory.FocusedDocumentPath))
        {
            memoryBuilder.AppendLine("- document actif: " + TrimPromptValue(
                memory.FocusedDocumentName ?? memory.FocusedDocumentPath,
                180));
        }

        AppendStructuredConversationMemory(memoryBuilder, memory);
        builder.AppendLine(BoundMemoryPrompt(
            memoryBuilder.ToString(),
            maximumContextTokens,
            maximumMemoryCharacters));
    }

    private static string BoundMemoryPrompt(
        string prompt,
        int maximumContextTokens,
        int? maximumMemoryCharacters = null)
    {
        var defaultMaximumCharacters = maximumContextTokens <= 4096
            ? 3_200
            : maximumContextTokens <= 8192
                ? 5_600
                : Math.Min(12_000, Math.Max(5_600, maximumContextTokens));
        var maximumCharacters = maximumMemoryCharacters is > 0
            ? Math.Min(defaultMaximumCharacters, maximumMemoryCharacters.Value)
            : defaultMaximumCharacters;
        if (prompt.Length <= maximumCharacters)
            return prompt.TrimEnd();

        const string marker =
            "\n- MEMORY_CONTEXT_TRUNCATED: details remain available in persistent memory.";
        var contentLimit = Math.Max(0, maximumCharacters - marker.Length);
        var cut = prompt.LastIndexOf('\n', Math.Min(contentLimit, prompt.Length - 1));
        if (cut < maximumCharacters / 2)
            cut = contentLimit;
        return prompt[..cut].TrimEnd() + marker;
    }

    private static string BuildMechanicalRepairMessage(SourceVerificationResult verification)
    {
        var errors = verification.Errors
            .Take(12)
            .Select(static error =>
                $"- {error.Code}"
                + (string.IsNullOrWhiteSpace(error.EvidenceId)
                    ? string.Empty
                    : $" [{error.EvidenceId}]")
                + $": {error.Message}");
        return """
               La verification mecanique de la reponse a refuse le brouillon ci-dessus:
               """
               + Environment.NewLine
               + string.Join(Environment.NewLine, errors)
               + Environment.NewLine
               + """
                 Tu gardes la decision semantique. Choisis maintenant de rechercher ce qui manque
                 ou de reviser la reponse avec les preuves deja observees. Les identifiants cites
                 doivent exister, etre visibles dans la reponse et pointer vers un fichier et une
                 page; les entrees de navigation ne peuvent pas etre citees comme preuve.
                 Si tu revises, renvoie uniquement le livrable final complet et concis, sans
                 expliquer la correction, le controle ou les outils. Les marqueurs [E#] demandes
                 restent obligatoires dans le livrable visible.
                 """;
    }

    private static string BuildMechanicalFailureSignature(
        SourceVerificationResult verification)
    {
        var errors = verification.Errors
            .Select(static error => error.Code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal);
        var cited = verification.CitedEvidence
            .Select(static item => item.EvidenceId.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal);
        return string.Join("|", errors) + "#cited=" + string.Join(",", cited);
    }

    private static string BuildBudgetFinalizationMessage(bool separateActionAndWriter)
        => separateActionAndWriter
            ? """
              Le budget d'outils est termine. Prends maintenant la derniere decision
              d'orchestration a partir des meilleures preuves deja observees. Aucun nouvel
              outil documentaire n'est disponible et tu ne dois pas rediger le livrable.
              Pour un plan multi-instance, appelle submit_evidence_selection avec la selection
              exacte demandee. Le redacteur se chargera ensuite du livrable final.
              """
            : """
              Le budget d'exploration est termine. Utilise maintenant les meilleures preuves
              deja observees pour produire le livrable le plus complet et honnete possible.
              N'appelle plus d'outil. Si une limite demeure, qualifie uniquement les cellules
              concernees au lieu d'abandonner les parties suffisamment sourcees. Place [E#]
              immediatement apres chaque fait ou cellule sourcee.
              Retourne uniquement la reponse finale complete et concise, sans analyse,
              preambule de travail, auto-evaluation ni commentaire sur le budget.
              """;

    private static string BuildSemanticSelectionContractRepairMessage(
        int requiredCount,
        int declaredCount,
        IReadOnlyList<string> unknownEvidenceIds,
        IReadOnlyList<string> semanticallyRejectedEvidenceIds,
        IReadOnlyList<string> duplicateVisibleSourceDetails,
        IReadOnlyList<string> duplicateDisplayValueDetails,
        string selectionContractError,
        int? selectableVisibleSourceCount = null,
        IReadOnlyList<string>? nonRenderableEvidenceIds = null)
    {
        var unknown = unknownEvidenceIds.Count == 0
            ? "aucun"
            : string.Join(", ", unknownEvidenceIds);
        var rejected = semanticallyRejectedEvidenceIds.Count == 0
            ? "aucun"
            : string.Join(", ", semanticallyRejectedEvidenceIds);
        var duplicateSources = duplicateVisibleSourceDetails.Count == 0
            ? "aucun"
            : string.Join(", ", duplicateVisibleSourceDetails);
        var duplicateValues = duplicateDisplayValueDetails.Count == 0
            ? "aucune"
            : string.Join(", ", duplicateDisplayValueDetails);
        var nonRenderable = nonRenderableEvidenceIds is null
                            || nonRenderableEvidenceIds.Count == 0
            ? "aucun"
            : string.Join(", ", nonRenderableEvidenceIds);
        return $"""
            DECISION D'ARRET REFUSEE PAR LE CONTRAT MECANIQUE DE TA SELECTION:
            ton plan exige {requiredCount} instances et ton appel contient
            {declaredCount} entrees EvidenceId.
            EvidenceId absents des preuves visibles: {unknown}.
            EvidenceId explicitement rejetes par le juge LLM precedent: {rejected}.
            EvidenceId differents qui designent pourtant la meme source visible:
            {duplicateSources}.
            EvidenceId differents qui produisent pourtant la meme valeur finale exacte:
            {duplicateValues}.
            EvidenceId sans valeur textuelle affichable: {nonRenderable}.
            Erreur de forme de la selection: {selectionContractError}.
            PREUVES DISTINCTES ELIGIBLES ACTUELLEMENT VISIBLES:
            {(selectableVisibleSourceCount?.ToString() ?? "non calcule")}.
            Le controle applique uniquement le nombre, l'existence, la citabilite et la
            decision semantique explicite du juge LLM. Reprends ta decision d'orchestration:
            {(selectableVisibleSourceCount is not null && selectableVisibleSourceCount < requiredCount
                ? $"il est mecaniquement impossible de selectionner {requiredCount} preuves distinctes maintenant; n'appelle pas submit_evidence_selection et choisis librement un outil documentaire pour trouver ce qui manque."
                : $"cherche si necessaire ou appelle submit_evidence_selection avec exactement {requiredCount} EvidenceId visibles, distincts et non rejetes.")}
            Avant toute selection, verifie explicitement qu'aucun identifiant
            n'apparait deux fois, qu'aucun groupe_source n'est choisi plus d'une fois et
            qu'aucune valeur finale exacte n'est repetee.
            evidenceIds est l'unique liste d'identifiants. Dans layout, rowHeader est un
            texte, columns est un tableau de libelles texte et rows est un tableau de
            libelles texte uniquement; rows ne contient ni objet ni EvidenceId.
            """;
    }

    private static string BuildLastExplorationTurnMessage()
        => """
           Ceci est ton dernier tour normal d'exploration avant la premiere redaction.
           Evalue d'abord les preuves deja montrees. Si une dimension explicite manque
           vraiment, regroupe maintenant les recherches independantes utiles; sinon,
           n'appelle aucun outil et commence le livrable. Le juge semantique independant
           pourra exceptionnellement demander ensuite une correction ciblee.
           """;

    private static string BuildSemanticJudgeFeedback(SemanticReview review)
        => $"""
            REVUE SEMANTIQUE INDEPENDANTE: {review.Decision}
            MOTIFS:
            {string.Join(Environment.NewLine, review.Reasons.Select(static reason => "- " + reason))}
            PREUVES EXPLICITEMENT REJETEES PAR LE JUGE LLM:
            {(review.RejectedEvidenceIds.Count == 0 ? "aucune" : string.Join(", ", review.RejectedEvidenceIds))}
            ALTERNATIVES PREFEREES PAR LE JUGE LLM:
            {(review.PreferredAlternativeEvidenceIds.Count == 0 ? "aucune" : string.Join(", ", review.PreferredAlternativeEvidenceIds))}

            Ne defends pas le brouillon refuse. Tu restes l'orchestrateur: si des preuves
            manquent, choisis toi-meme les outils et requetes utiles; si les preuves deja
            observees suffisent, revise directement le livrable. Ne declare pas le resultat
            complet avant d'avoir corrige chacun de ces motifs.
            Si la decision est need_more_evidence, les appels strictement identiques deja
            listes dans ACTIONS DEJA EXECUTEES seront rejetes. Choisis une action
            mecaniquement nouvelle: autre requete, autre document, contexte cible ou
            prochain_offset_exact deja fourni. Tu decides laquelle est semantiquement utile.
            Si tu revises, retourne uniquement le livrable final complet et concis. N'explique
            ni le refus, ni la correction, ni le juge et n'ajoute aucun raisonnement visible.
            """;

}
