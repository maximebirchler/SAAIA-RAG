using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildStructuredSourceBackedCandidateBankAnswer(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        int requiredSlots,
        string language,
        string? query)
    {
        language = NormalizeLanguageCode(language);
        var labels = language switch
        {
            "en" => (
                Header: "The retrieved sources cover only part of the requested structure, not enough to fill it without repetition.",
                Intro: "Directly usable items:",
                Next: "To complete the plan cleanly, more items confirmed by the documents are needed."),
            "es" => (
                Header: "Las fuentes recuperadas cubren solo una parte de la estructura solicitada, no lo suficiente para completarla sin repetir.",
                Intro: "Elementos directamente utilizables:",
                Next: "Para completar el plan correctamente, hacen falta más elementos confirmados por los documentos."),
            "pt" => (
                Header: "As fontes recuperadas cobrem apenas uma parte da estrutura pedida, não o suficiente para a completar sem repetição.",
                Intro: "Itens diretamente utilizáveis:",
                Next: "Para completar o plano corretamente, são necessários mais itens confirmados pelos documentos."),
            "de" => (
                Header: "Die gefundenen Quellen decken nur einen Teil der gewünschten Struktur ab, nicht genug für einen Plan ohne Wiederholungen.",
                Intro: "Direkt nutzbare Punkte:",
                Next: "Für einen sauberen vollständigen Plan werden weitere durch Dokumente bestätigte Punkte benötigt."),
            "it" => (
                Header: "Le fonti recuperate coprono solo una parte della struttura richiesta, non abbastanza per completarla senza ripetizioni.",
                Intro: "Elementi direttamente utilizzabili:",
                Next: "Per completare il piano in modo pulito, servono altri elementi confermati dai documenti."),
            _ => (
                Header: "Les sources récupérées couvrent seulement une partie de la structure demandée, pas assez pour la compléter sans répétitions.",
                Intro: "Éléments directement utilisables :",
                Next: "Pour compléter le planning proprement, il faut d'autres éléments confirmés par les documents.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Intro);
        var bankItems = OrderStructuredSourceBackedCandidateBankItems(planItems, query, language);
        var itemLimit = Math.Min(bankItems.Count, Math.Clamp(requiredSlots, 8, 24));
        for (var i = 0; i < itemLimit; i++)
        {
            var candidate = bankItems[i];
            sb.Append("- ");
            sb.Append(FormatSourceBackedCandidateDisplayTitle(candidate));
            sb.Append(' ');
            sb.Append(FormatSourceBackedCandidateOpenToken(candidate, language));
            sb.AppendLine();
        }

        sb.Append(labels.Next);
        return AppendBroadenedSearchOfferIfHelpful(sb.ToString(), query, language);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> OrderStructuredSourceBackedCandidateBankItems(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        string? query,
        string language)
    {
        if (planItems.Count == 0)
            return planItems;

        language = NormalizeLanguageCode(language);
        var periodLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        var slotGroups = BuildStructuredPlanningSlotTermGroups(periodLabels, query);
        if (slotGroups.Count == 0)
            return planItems;

        var primaryMatchesByCandidate = planItems
            .Select(candidate => FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: false))
            .ToArray();
        var primaryCounts = new int[slotGroups.Count];
        foreach (var matches in primaryMatchesByCandidate)
        {
            foreach (var match in matches)
            {
                if ((uint)match < (uint)primaryCounts.Length)
                    primaryCounts[match]++;
            }
        }

        var selected = new List<SourceBackedOptionCandidate>(planItems.Count);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < planItems.Count; i++)
        {
            var candidate = planItems[i];
            var primaryMatches = primaryMatchesByCandidate[i];
            var alternativeMatches = primaryMatches.Length == 0
                ? FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: true)
                : Array.Empty<int>();
            if (alternativeMatches.Length > 0
                && alternativeMatches.All(match => (uint)match < (uint)primaryCounts.Length && primaryCounts[match] > 0))
            {
                continue;
            }

            var key = BuildSourceBackedPlanningCandidateLeadKey(candidate);
            if (selectedKeys.Add(key))
                selected.Add(candidate);
        }

        return selected.Count == 0 ? planItems : selected;
    }

    private static string FormatSourceBackedCandidateReference(SourceBackedOptionCandidate candidate, string language)
    {
        var hit = candidate.Hit;
        var source = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        if (string.IsNullOrWhiteSpace(source))
            source = "source";

        return $"{source} {SourceBackedPagePrefix(language)}{Math.Max(1, hit.PageStart)}";
    }

    private static string FormatSourceBackedCandidateDisplayTitle(SourceBackedOptionCandidate candidate)
    {
        var title = CleanSourceBackedOptionTitle(candidate.Title);
        if (string.IsNullOrWhiteSpace(title))
            title = CollapseWhitespace(candidate.Title);

        return HumanizeSourceBackedDisplayTitle(title);
    }

    private static string HumanizeSourceBackedDisplayTitle(string? title)
    {
        var value = RepairSplitOcrBrokenTitleWords(CollapseWhitespace(title ?? string.Empty)).Trim();
        if (string.IsNullOrWhiteSpace(value) || !LooksLikePredominantlyUppercaseDisplayTitle(value))
            return value;

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            words[i] = HumanizeSourceBackedDisplayWord(words[i], i, words.Length);

        return string.Join(' ', words);
    }

    private static bool LooksLikePredominantlyUppercaseDisplayTitle(string title)
    {
        var letters = title.Where(char.IsLetter).ToArray();
        if (letters.Length < 5)
            return false;

        var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        return upperRatio >= 0.72;
    }

    private static string HumanizeSourceBackedDisplayWord(string word, int index, int totalWords)
    {
        if (string.IsNullOrWhiteSpace(word))
            return string.Empty;

        if (word.Contains('-', StringComparison.Ordinal))
        {
            return string.Join(
                '-',
                word.Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Select((part, partIndex) => HumanizeSourceBackedDisplayWord(
                        part,
                        index == 0 && partIndex == 0 ? 0 : 1,
                        totalWords)));
        }

        var lower = word.ToLowerInvariant();
        if (index > 0 && index < totalWords - 1 && IsLowercaseSourceBackedDisplayParticle(lower))
            return lower;

        if (index == 0)
            return char.ToUpperInvariant(lower[0]) + lower[1..];

        if (word.Length == 1 && char.IsLetter(word[0]))
            return word.ToUpperInvariant();

        return lower;
    }

    private static bool IsLowercaseSourceBackedDisplayParticle(string value)
        => value is "a" or "au" or "aux" or "de" or "du" or "des" or "d" or "et" or "ou" or
            "of" or "the" or "and" or "or" or "with" or
            "con" or "sin" or "para" or "por" or
            "di" or "da" or "del" or "della" or "e" or
            "mit" or "und" or "oder" or "von";

    private static string FormatSourceBackedCandidateOpenToken(SourceBackedOptionCandidate candidate, string language)
    {
        var hit = candidate.Hit;
        var docPath = (hit.DocPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(docPath))
            return $"({FormatSourceBackedCandidateReference(candidate, language)})";

        var page = Math.Max(1, hit.PageStart);
        var sourceLabel = string.IsNullOrWhiteSpace(hit.DocName)
            ? Path.GetFileName(docPath)
            : hit.DocName.Trim();
        if (string.IsNullOrWhiteSpace(sourceLabel))
            sourceLabel = docPath;

        var label = AppendPageToOpenTokenLabel(sourceLabel, page, language);
        return $"([[open|{docPath}|{page}|{label}]])";
    }
}
