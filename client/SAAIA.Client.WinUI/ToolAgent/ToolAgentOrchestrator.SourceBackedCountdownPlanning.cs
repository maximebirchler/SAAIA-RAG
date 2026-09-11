using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeSourceBackedCountdownPlanningRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksCountdown = Regex.IsMatch(
            normalized,
            @"\b(?:retroplanning|rebours|a\s+rebours|countdown|reverse\s+plan|backward\s+plan|planning\s+inverse|plan\s+inverse)\b",
            RegexOptions.CultureInvariant);
        var asksSchedule = Regex.IsMatch(
            normalized,
            @"\b(?:planning|plan|programme|horaire|schedule)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalized,
                @"\b(?:preparation|preparer|procedure|process|operation|service|livraison|deliver|deadline|echeance|Ã©chÃ©ance|pret|prete|ready|start|finish|end)\b",
                RegexOptions.CultureInvariant);

        return (asksCountdown || asksSchedule)
            && (TryExtractRequestedClockTime(query).HasValue
                || Regex.IsMatch(normalized, @"\b\d{1,2}\s*h(?:\s*\d{2})?\b", RegexOptions.CultureInvariant));
    }

    private static string BuildSourceBackedCountdownPlanningAnswer(ToolResults toolResults, string query, string language)
    {
        if (IsBroadenedSourceSearchConfirmationEnvelope(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var targetTime = TryExtractRequestedClockTime(query);
        if (!targetTime.HasValue)
            return string.Empty;

        var candidates = SelectSourceBackedCountdownPlanningCandidates(toolResults, query)
            .Take(6)
            .ToList();
        if (candidates.Count == 0)
            return string.Empty;

        var timed = candidates
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(candidate => candidate.VisibleMinutes!.Value)
            .ThenByDescending(candidate => candidate.Score)
            .Take(5)
            .ToList();
        if (timed.Count == 0)
            return string.Empty;

        var labels = language switch
        {
            "en" => (
                Header: "I do not have one selected item, so this reverse schedule only uses durations visible in the source excerpts:",
                Start: "start",
                Duration: "visible duration",
                Manual: "to place manually",
                Note: "If you choose the exact source items, I can rebuild a stricter schedule from those source pages only."),
            "es" => (
                Header: "No tengo un elemento unico seleccionado, asi que este plan inverso usa solo duraciones visibles en los extractos fuente:",
                Start: "iniciar",
                Duration: "duracion visible",
                Manual: "colocar manualmente",
                Note: "Si eliges los elementos exactos, puedo rehacer un planning mas estricto solo con esas paginas fuente."),
            "pt" => (
                Header: "Nao tenho um item unico selecionado, por isso este plano inverso usa apenas duracoes visiveis nos excertos fonte:",
                Start: "iniciar",
                Duration: "duracao visivel",
                Manual: "colocar manualmente",
                Note: "Se escolheres os itens exatos, posso refazer um plano mais rigoroso apenas com essas paginas fonte."),
            "de" => (
                Header: "Es ist kein einzelnes Element ausgewaehlt; dieser Rueckwaertsplan nutzt daher nur sichtbare Dauern aus den Quellenauszuegen:",
                Start: "starten",
                Duration: "sichtbare Dauer",
                Manual: "manuell einplanen",
                Note: "Wenn du die genauen Elemente auswaehlst, kann ich den Plan nur mit diesen Quellseiten strenger neu berechnen."),
            "it" => (
                Header: "Non ho un elemento unico selezionato, quindi questo piano a ritroso usa solo durate visibili negli estratti fonte:",
                Start: "avviare",
                Duration: "durata visibile",
                Manual: "da collocare manualmente",
                Note: "Se scegli gli elementi esatti, posso rifare un piano piu rigoroso solo da quelle pagine fonte."),
            _ => (
                Header: "Je n'ai pas un element unique selectionne ; ce retroplanning utilise donc uniquement les durees visibles dans les extraits sources :",
                Start: "lancer",
                Duration: "duree visible",
                Manual: "a caler manuellement",
                Note: "Si tu choisis les elements source exacts, je peux refaire un planning plus strict uniquement depuis ces pages source.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        foreach (var candidate in timed)
        {
            var startTime = targetTime.Value.Subtract(TimeSpan.FromMinutes(candidate.VisibleMinutes!.Value));
            while (startTime < TimeSpan.Zero)
                startTime += TimeSpan.FromDays(1);

            var hit = candidate.Hit;
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(FormatClockTime(startTime));
            sb.Append(" : ");
            sb.Append(labels.Start);
            sb.Append(' ');
            sb.Append(candidate.Title);
            sb.Append(" (");
            sb.Append(labels.Duration);
            sb.Append(" : ");
            sb.Append(candidate.VisibleMinutes.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append(" min, ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.AppendLine(")");
        }

        sb.AppendLine(labels.Note);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<SourceBackedCountdownCandidate> SelectSourceBackedCountdownPlanningCandidates(ToolResults toolResults, string? query)
    {
        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => LooksLikeResolvedRouteTargetHit(hit) || !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        return sourceHits
            .Select(hit =>
            {
                var title = ExtractSourceBackedOptionTitle(hit, query);
                if (string.IsNullOrWhiteSpace(title))
                    title = BuildCountdownFallbackTitle(hit);

                var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                var score = ComputeRagHitLexicalRelevance(query ?? string.Empty, GetRagHitLookupText(hit))
                    + ComputeProcedureCompletenessCueScore(hit)
                    + ComputeStructuredProcedureEvidenceCueScore(hit)
                    + ComputeStructuredProcedureVisibleEvidenceCueScore(hit)
                    + (visibleMinutes.HasValue ? 18 : 0);
                if (LooksLikePageReferenceOnlyHit(hit))
                    score -= 25;
                if (LooksLikeMidProcedureFragment(hit))
                    score -= 6;

                return new SourceBackedCountdownCandidate(hit, title, score, visibleMinutes);
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.VisibleMinutes.HasValue)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildCountdownFallbackTitle(RagHitSummary hit)
    {
        foreach (var raw in new[] { hit.SectionTitle, hit.HeadingPath })
        {
            var title = CleanSourceBackedOptionTitle(raw);
            if (IsUsableSourceBackedOptionTitle(title))
            {
                return title;
            }
        }

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return string.IsNullOrWhiteSpace(docLabel) ? "Element source" : docLabel;
    }

    private static TimeSpan? TryExtractRequestedClockTime(string? query)
    {
        var text = query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var pattern in new[]
                 {
                     @"\b(?<h>[01]?\d|2[0-3])\s*h(?:\s*(?<m>[0-5]\d))?\b",
                     @"\b(?<h>[01]?\d|2[0-3])\s*[:.]\s*(?<m>[0-5]\d)\b",
                     @"\b(?<h>0?[1-9]|1[0-2])\s*(?<ampm>am|pm)\b"
                 })
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success
                ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            if (match.Groups["ampm"].Success)
            {
                var ampm = match.Groups["ampm"].Value.ToLowerInvariant();
                if (ampm == "pm" && hours < 12)
                    hours += 12;
                if (ampm == "am" && hours == 12)
                    hours = 0;
            }

            if (hours is >= 0 and <= 23 && minutes is >= 0 and <= 59)
                return new TimeSpan(hours, minutes, 0);
        }

        var normalized = NormalizeLexicalLookup(text);
        var looseMatch = Regex.Match(
            normalized,
            @"\b(?<h>[01]?\d|2[0-3])\s*h(?:\s*(?<m>[0-5]\d))?\b",
            RegexOptions.CultureInvariant);
        if (looseMatch.Success)
        {
            var hours = int.Parse(looseMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = looseMatch.Groups["m"].Success
                ? int.Parse(looseMatch.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            if (hours is >= 0 and <= 23 && minutes is >= 0 and <= 59)
                return new TimeSpan(hours, minutes, 0);
        }

        return null;
    }

    private static string FormatClockTime(TimeSpan value)
    {
        value = new TimeSpan((value.Hours + 24) % 24, value.Minutes, 0);
        return $"{value.Hours:00}h{value.Minutes:00}";
    }
}
