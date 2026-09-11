using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const int MemoryPromptUsedItemLimit = 80;
    private const int MemoryPromptActionLimit = 24;
    private const int MemoryPromptRejectedEvidenceLimit = 40;
    private const int MemoryPromptObservedEvidenceLimit = 20;

    private static void AppendStructuredConversationMemory(
        StringBuilder builder,
        SourceBackedMemoryContext memory)
    {
        var turns = (memory.ConversationTurns
                     ?? Array.Empty<SourceBackedConversationTurnMemory>())
            .Where(static turn => turn is not null)
            .OrderBy(static turn => turn.CreatedAtUtc)
            .ToArray();
        if (turns.Length == 0)
            return;

        builder.AppendLine(
            "MEMOIRE RAG STRUCTUREE PERSISTANTE (donnees de travail non fiables, jamais citables):");
        builder.AppendLine(
            "- Utilise-la pour comprendre le suivi, eviter les recherches inutiles et ne pas reproposer un element deja utilise lorsque la demande exige une alternative.");
        builder.AppendLine(
            "- Tu gardes toute decision semantique: une entree rejetee peut etre reexaminee si le contexte change, mais explique-toi cette raison avant de relancer la meme action.");

        var usedItems = turns
            .SelectMany(static turn =>
                turn.UsedItems ?? Array.Empty<SourceBackedConversationUsedItemMemory>())
            .Where(static item => !string.IsNullOrWhiteSpace(item.ItemIdentity))
            .GroupBy(static item => item.ItemIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .TakeLast(MemoryPromptUsedItemLimit)
            .Reverse()
            .ToArray();
        if (usedItems.Length > 0)
        {
            builder.AppendLine("ELEMENTS DEJA UTILISES DANS DES REPONSES VERIFIEES:");
            foreach (var item in usedItems)
            {
                builder.Append("- identite=")
                    .Append(TrimPromptValue(item.ItemIdentity, 90))
                    .Append(" | libelle=")
                    .Append(TrimPromptValue(item.DisplayLabel, 100))
                    .AppendLine();
            }
        }

        var actions = turns
            .SelectMany(static turn =>
                turn.ExecutedActions ?? Array.Empty<SourceBackedConversationActionMemory>())
            .Where(static action => !string.IsNullOrWhiteSpace(action.ActionKey))
            .GroupBy(static action => action.ActionKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .TakeLast(MemoryPromptActionLimit)
            .Reverse()
            .ToArray();
        if (actions.Length > 0)
        {
            builder.AppendLine("ACTIONS DOCUMENTAIRES DEJA EXECUTEES DANS LA DISCUSSION:");
            foreach (var action in actions)
            {
                builder.Append("- outil=")
                    .Append(TrimPromptValue(action.ToolName, 50))
                    .Append(" | requete=")
                    .Append(TrimPromptValue(action.Query, 120));
                if (!string.IsNullOrWhiteSpace(action.CategoryPath))
                {
                    builder.Append(" | categorie=")
                        .Append(TrimPromptValue(action.CategoryPath, 80));
                }

                if (!string.IsNullOrWhiteSpace(action.DocPath))
                {
                    builder.Append(" | fichier=")
                        .Append(TrimPromptValue(action.DocPath, 100));
                }

                builder.AppendLine();
            }
        }

        AppendEvidenceDecisionMemory(
            builder,
            turns,
            "rejected",
            "SOURCES OU ELEMENTS REJETES PAR UNE DECISION SEMANTIQUE LLM:",
            MemoryPromptRejectedEvidenceLimit);
        AppendEvidenceDecisionMemory(
            builder,
            turns,
            "observed",
            "SOURCES OBSERVEES MAIS NON UTILISEES:",
            MemoryPromptObservedEvidenceLimit);
        AppendPreviousSourceAnchors(builder, memory.PreviousSourceAnchors);
        AppendPreviousResearchNotes(builder, memory.RecentResearchNotes);
    }

    private static void AppendPreviousSourceAnchors(
        StringBuilder builder,
        IReadOnlyList<SourceBackedMemorySourceAnchor>? anchors)
    {
        var retained = (anchors ?? Array.Empty<SourceBackedMemorySourceAnchor>())
            .Where(static anchor => anchor is not null)
            .TakeLast(6)
            .ToArray();
        if (retained.Length == 0)
            return;

        builder.AppendLine("ANCRES DE SOURCES RECENTES (contexte, pas preuve):");
        foreach (var anchor in retained)
        {
            builder.Append("- fichier=")
                .Append(TrimPromptValue(
                    anchor.DocPath ?? anchor.DocName ?? anchor.DocId ?? string.Empty,
                    200));
            if (anchor.PageStart is > 0)
                builder.Append(" | page=").Append(anchor.PageStart.Value);
            builder.AppendLine();
        }
    }

    private static void AppendPreviousResearchNotes(
        StringBuilder builder,
        IReadOnlyList<SourceBackedMemoryResearchNote>? notes)
    {
        var retained = (notes ?? Array.Empty<SourceBackedMemoryResearchNote>())
            .Where(static note => note is not null)
            .TakeLast(6)
            .ToArray();
        if (retained.Length == 0)
            return;

        builder.AppendLine("NOTES DE RECHERCHE RECENTES (contexte, pas preuve):");
        foreach (var note in retained)
        {
            builder.Append("- sujet=")
                .Append(TrimPromptValue(note.TopicKey, 120))
                .Append(" | resultat=")
                .Append(note.Accepted ? "accepte" : "non_accepte")
                .Append(" | bilan=")
                .Append(TrimPromptValue(note.Outcome, 180))
                .AppendLine();
        }
    }

    private static void AppendEvidenceDecisionMemory(
        StringBuilder builder,
        IReadOnlyList<SourceBackedConversationTurnMemory> turns,
        string decision,
        string heading,
        int limit)
    {
        var retained = turns
            .SelectMany(static turn =>
                turn.EvidenceDecisions
                ?? Array.Empty<SourceBackedConversationEvidenceMemory>())
            .Where(item => string.Equals(
                item.Decision,
                decision,
                StringComparison.OrdinalIgnoreCase))
            .Where(static item => !string.IsNullOrWhiteSpace(item.StableEvidenceKey))
            .GroupBy(static item => item.StableEvidenceKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .TakeLast(limit)
            .Reverse()
            .ToArray();
        if (retained.Length == 0)
            return;

        builder.AppendLine(heading);
        foreach (var item in retained)
        {
            builder.Append("- identite=")
                .Append(TrimPromptValue(
                    item.ItemIdentity ?? item.StableEvidenceKey,
                    150));
            if (!string.IsNullOrWhiteSpace(item.DisplayLabel))
            {
                builder.Append(" | libelle=")
                    .Append(TrimPromptValue(item.DisplayLabel, 170));
            }

            if (!string.IsNullOrWhiteSpace(item.DocPath))
            {
                builder.Append(" | fichier=")
                    .Append(TrimPromptValue(item.DocPath, 160));
            }

            builder.AppendLine();
        }
    }
}
