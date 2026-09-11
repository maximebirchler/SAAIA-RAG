using System.Globalization;
using System.Text;

// Final-writer prompt context; domain examples are instructions for the LLM.
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SelectedEvidenceWriterGroup(
        EvidenceItem Representative,
        IReadOnlyList<EvidenceItem> CitableEvidence);

    private sealed record SelectedEvidenceWriterContext(
        IReadOnlyList<SelectedEvidenceWriterGroup> Groups,
        IReadOnlyList<string> AllowedEvidenceIds,
        IReadOnlyList<IReadOnlyList<string>> RequiredEvidenceIdGroups);

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSelectedEvidenceWriterMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<string> selectedEvidenceIds,
            string atomicEvidenceMode,
            string? revisionInstruction = null,
            string? semanticJudgeAssessment = null,
            IReadOnlyList<string>? semanticJudgeLeadEvidenceIds = null)
    {
        var contentClaimMode = string.Equals(
            atomicEvidenceMode,
            "content_claim",
            StringComparison.OrdinalIgnoreCase);
        var writerContext = BuildSelectedEvidenceWriterContext(
            bundle,
            selectedEvidenceIds,
            atomicEvidenceMode);
        var selectedEvidenceGroups = writerContext.Groups;
        var citableEvidenceCount = selectedEvidenceGroups
            .Sum(static group => group.CitableEvidence.Count);
        var excerptCharacters = Math.Clamp(
            4800 / Math.Max(1, citableEvidenceCount),
            360,
            2200);
        var context = new StringBuilder();
        context.AppendLine("DEMANDE ORIGINALE:");
        context.AppendLine(TrimPromptValue(intake.UserQuestion, 900));
        AppendQuestionFocusContext(context, intake);
        if (intake.ExplicitConstraints.Count > 0)
        {
            context.Append("CONTRAINTES EXPLICITES: ")
                .AppendLine(string.Join(
                    " | ",
                    intake.ExplicitConstraints.Select(constraint =>
                        TrimPromptValue(constraint, 180))));
        }
        if (intake.MemoryContext is { } memory)
        {
            context.Append("LANGUE ET STYLE: ")
                .Append(TrimPromptValue(
                    FirstNonBlank(memory.PreferredLanguage, intake.Language),
                    40))
                .Append(" | ")
                .AppendLine(TrimPromptValue(memory.PreferredStyle, 120));
        }

        context.Append("NOMBRE_DE_PREUVES_SELECTIONNEES: ")
            .AppendLine(selectedEvidenceGroups.Count.ToString(
                CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(semanticJudgeAssessment))
        {
            context.AppendLine("HANDOFF_SEMANTIQUE_DU_JUGE:");
            context.AppendLine("DECISION: answer");
            context.Append("EVIDENCEIDS_SELECTIONNES_PAR_LE_JUGE: ")
                .AppendLine(string.Join(',', selectedEvidenceIds));
            context.Append("LEADEVIDENCEIDS_PRINCIPAUX_PAR_LE_JUGE: ")
                .AppendLine(string.Join(
                    ',',
                    semanticJudgeLeadEvidenceIds ?? Array.Empty<string>()));
            context.Append("RAISON_DU_JUGE: ")
                .AppendLine(TrimPromptValue(semanticJudgeAssessment, 900));
            context.AppendLine(
                "ROLE_DES_PREUVES: les EvidenceIds ci-dessus sont la selection complete "
                + "du juge; les LeadEvidenceIds sont ses preuves principales explicites. "
                + "La fenetre contextuelle citable peut contenir "
                + "des preuves supplementaires; ne les recopie pas automatiquement "
                + "dans le livrable.");
            context.AppendLine("FENETRE_CONTEXTUELLE_CITABLE:");
        }
        context.AppendLine(contentClaimMode
            ? "POOL DE PREUVES CONTENT_CLAIM EXACTEMENT AUTORISE:"
            : "PREUVES EXACTEMENT AUTORISEES PAR UNITE SELECTIONNEE:");
        for (var groupIndex = 0;
             groupIndex < selectedEvidenceGroups.Count;
             groupIndex++)
        {
            var evidenceGroup = selectedEvidenceGroups[groupIndex];
            context.Append("UNITE_SELECTIONNEE ")
                .AppendLine((groupIndex + 1).ToString(
                    CultureInfo.InvariantCulture));
            foreach (var item in evidenceGroup.CitableEvidence)
            {
                AppendCitableEvidenceWriterBlock(
                    context,
                    item,
                    excerptCharacters);
            }
        }
        context.AppendLine(
            "PORTEE: reponds uniquement a ce que demande l'utilisateur.");
        if (!string.IsNullOrWhiteSpace(revisionInstruction))
        {
            context.AppendLine("CORRECTION MECANIQUE OBLIGATOIRE:");
            context.AppendLine(TrimPromptValue(revisionInstruction, 900));
        }

        var writerSystemPrompt = contentClaimMode
            ? """
                Tu es le redacteur final d'un RAG source-backed en MODE CONTENT_CLAIM.
                Reponds directement dans la langue demandee, uniquement avec le pool de
                preuves autorise. Le pool n'est pas une liste d'elements a recopier: utilise
                les claims directement soutenus qui repondent a la demande et ignore une
                preuve redondante si elle n'ajoute aucun claim utile.

                Chaque affirmation documentaire significative doit porter la citation locale
                [E#] de la PREUVE_CITABLE qui soutient exactement ce claim. Un meme EvidenceId
                peut soutenir plusieurs claims distincts et donc etre cite plusieurs fois,
                mais seulement si son propre EXTRAIT soutient reellement chacun de ces claims.
                Ne cite jamais un EvidenceId hors du pool. Ne place pas plusieurs EvidenceIds
                en grappe apres une proposition indistincte et ne fusionne jamais
                silencieusement des variantes, versions, positions ou procedures
                documentaires.

                """
            : """
                Tu es le redacteur final d'un RAG source-backed. Reponds directement dans la
                langue demandee, uniquement avec les PREUVES_CITABLES affichees. Chaque
                UNITE_SELECTIONNEE represente un element retenu semantiquement par le juge.
                Realise chaque unite avec au moins une de ses preuves citables; n'en choisis
                pas une a la place d'une autre et n'omets aucune unite selectionnee.

                Chaque affirmation documentaire significative doit porter localement
                l'EvidenceId exact de l'EXTRAIT qui la soutient. Une unite peut necessiter
                plusieurs EvidenceIds si ses faits viennent de plusieurs extraits. Ne cite
                jamais un EvidenceId dont tu n'utilises pas le texte, ne cite jamais un
                identifiant hors de son unite et utilise chaque EvidenceId au plus une fois.
                Regroupe, si necessaire, les faits soutenus par un meme extrait avant son
                unique marqueur. Ne fusionne jamais deux unites selectionnees dans une seule
                proposition indistincte.

                """;
        writerSystemPrompt = writerSystemPrompt.TrimEnd()
            + "\n\n" + BuildSelectedWriterFidelityInstructions(contentClaimMode);

        return new[]
        {
            SourceBackedAgentMessage.System(writerSystemPrompt),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static SelectedEvidenceWriterContext
        BuildSelectedEvidenceWriterContext(
            EvidenceBundle bundle,
            IReadOnlyList<string> selectedEvidenceIds,
            string atomicEvidenceMode)
    {
        var contentClaimMode = string.Equals(
            atomicEvidenceMode,
            "content_claim",
            StringComparison.OrdinalIgnoreCase);
        var selectedEvidence = selectedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .ToArray();
        var groups = contentClaimMode
            ? selectedEvidence
                .Select(static item => new SelectedEvidenceWriterGroup(
                    item,
                    new[] { item }))
                .ToArray()
            : selectedEvidence
                .GroupBy(
                    static item => BuildSelectedEvidenceSourceWindowKey(item),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var selectedInGroup = group
                        .DistinctBy(
                            static item => item.EvidenceId,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var representative = selectedInGroup[0];
                    var allWindowItems = OrderSourceWindowInDocumentOrder(
                            bundle.Items.Where(item =>
                                !item.RiskFlags.Contains(
                                    "orientation_only",
                                    StringComparer.OrdinalIgnoreCase)
                                && IsMechanicallyCitableCandidate(item)
                                && HasRenderableEvidenceValue(item)
                                && string.Equals(
                                    BuildSelectedEvidenceSourceWindowKey(item),
                                    BuildSelectedEvidenceSourceWindowKey(
                                        representative),
                                    StringComparison.OrdinalIgnoreCase)))
                        .DistinctBy(
                            static item => item.EvidenceId,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var boundedWindow = selectedInGroup
                        .Concat(allWindowItems)
                        .DistinctBy(
                            static item => item.EvidenceId,
                            StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToArray();
                    return new SelectedEvidenceWriterGroup(
                        representative,
                        OrderSourceWindowInDocumentOrder(boundedWindow).ToArray());
                })
                .ToArray();
        var allowedEvidenceIds = groups
            .SelectMany(static group => group.CitableEvidence)
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredEvidenceIdGroups = contentClaimMode
            ? Array.Empty<IReadOnlyList<string>>()
            : groups
                .Select(static group =>
                    (IReadOnlyList<string>)group.CitableEvidence
                        .Select(static item => item.EvidenceId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray())
                .ToArray();
        return new SelectedEvidenceWriterContext(
            groups,
            allowedEvidenceIds,
            requiredEvidenceIdGroups);
    }

    private static void AppendCitableEvidenceWriterBlock(
        StringBuilder context,
        EvidenceItem item,
        int excerptCharacters)
    {
        context.AppendLine("PREUVE_CITABLE:");
        context.Append('[').Append(item.EvidenceId).AppendLine("]");
        context.Append("TITRE: ")
            .AppendLine(TrimPromptValue(GetEvidenceDisplayValue(item), 180));
        context.Append("SOURCE: ")
            .Append(TrimPromptValue(
                item.DocPath ?? item.DocName ?? item.DocId ?? "source-inconnue",
                220))
            .Append(" | page ")
            .Append(item.PageStart?.ToString(CultureInfo.InvariantCulture) ?? "?");
        if (!string.IsNullOrWhiteSpace(item.ChunkId))
        {
            context.Append(" | chunk ")
                .Append(TrimPromptValue(item.ChunkId, 90));
        }
        if (!string.IsNullOrWhiteSpace(item.AnchorId))
        {
            context.Append(" | anchor ")
                .Append(TrimPromptValue(item.AnchorId, 90));
        }
        if (!string.IsNullOrWhiteSpace(item.ContentCardId))
        {
            context.Append(" | content-card ")
                .Append(TrimPromptValue(item.ContentCardId, 90));
        }
        if (!string.IsNullOrWhiteSpace(item.RevisionId))
        {
            context.Append(" | revision ")
                .Append(TrimPromptValue(item.RevisionId, 90));
        }
        if (!string.IsNullOrWhiteSpace(item.SourceHash))
        {
            context.Append(" | hash ")
                .Append(TrimPromptValue(item.SourceHash, 90));
        }
        context.AppendLine();
        context.AppendLine("EXTRAIT:");
        context.AppendLine(TrimPromptValue(item.Excerpt, excerptCharacters));
    }

    private static string BuildSelectedEvidenceSourceWindowKey(
        EvidenceItem item)
        => BuildAgentSourceWindowKey(item);

    private static string FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first)
            ? first.Trim()
            : second?.Trim() ?? string.Empty;
}
