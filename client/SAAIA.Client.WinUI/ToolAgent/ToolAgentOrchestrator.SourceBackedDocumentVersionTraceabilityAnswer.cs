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
    private static string? TryBuildDocumentVersionTraceabilityAnswer(ToolResults toolResults, string query, string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        if (!LooksLikeDocumentVersionTraceabilityRequest(intentQuery))
            return null;

        if (RequiresStructuredSourceBackedPlanningCoverage(intentQuery))
        {
            return null;
        }

        language = NormalizeLanguageCode(language);
        var hits = SelectDocumentVersionTraceabilityHits(toolResults, intentQuery, maxHits: 4).ToList();
        if (hits.Count == 0)
            return null;

        var normalized = NormalizeLexicalLookup(query);
        var asksReplacement = Regex.IsMatch(
            normalized,
            @"\b(?:remplace|remplacer|replacement|replace|replaces|substitue|supersede|supersedes|automatiquement|automatically)\b",
            RegexOptions.CultureInvariant);
        var asksMainOrStatus = Regex.IsMatch(
            normalized,
            @"\b(?:(?:document|doc|fichier|file)\s+(?:principal|main)|\bac\b|corrigendum|correction|berichtigung|amendment|amendement)\b",
            RegexOptions.CultureInvariant);
        var asksLatestDefault = Regex.IsMatch(
            normalized,
            @"\b(?:ne\s+precise\s+pas|sans\s+preciser|sans\s+dire.{0,40}annee|without\s+specifying|without\s+saying.{0,40}year|derniere|latest|newest|recent|recente|current|actuelle|plus\s+recente)\b",
            RegexOptions.CultureInvariant);
        var asksProof = Regex.IsMatch(
            normalized,
            @"\b(?:prouve|prouver|preuve|prove|proves|proof|trace|tracabilite|traceability|utilise\s+bien|uses?\s+the\s+right|bonne\s+version|correct\s+version|version|fichier\s+proche|nearby\s+file)\b",
            RegexOptions.CultureInvariant);
        var latestYear = hits
            .Select(ExtractDocumentVersionYear)
            .Where(static year => year.HasValue)
            .Select(static year => year!.Value)
            .DefaultIfEmpty()
            .Max();

        var header = language switch
        {
            "en" when asksReplacement => "I cannot prove an automatic replacement from the retrieved excerpts. Keep the related documents separate and verify an explicit replacement/adoption clause:",
            "en" when asksMainOrStatus => "Use both levels: the main document for the baseline requirement, and the correction/amendment document for the associated change. The retrieved excerpts do not prove that the correction replaces the full main document:",
            "en" when asksLatestDefault && latestYear > 0 => $"If the user does not specify a year, cite the latest version supported by the selected sources first, and keep older versions as historical context. The latest detected year in these sources is {latestYear}:",
            "en" when asksLatestDefault => "If the user does not specify a year, cite the latest version supported by the selected sources first, and keep older versions as historical context:",
            "en" when asksProof => "To prove the answer uses the intended version, cite the exact file name and page for each source, and keep nearby versions separated:",
            "en" => "Here is the traceability I can establish from the retrieved pages:",
            "es" => "Esta es la trazabilidad que puedo establecer a partir de las pÃƒÂ¡ginas recuperadas:",
            "pt" => "Esta ÃƒÂ© a rastreabilidade que posso estabelecer a partir das pÃƒÂ¡ginas recuperadas:",
            "de" => "Diese Nachverfolgbarkeit kann ich aus den gefundenen Seiten ableiten:",
            "it" => "Questa ÃƒÂ¨ la tracciabilitÃƒÂ  che posso stabilire dalle pagine recuperate:",
            _ when asksReplacement => "Je ne peux pas prouver un remplacement automatique avec les extraits retrouvÃƒÂ©s. Je garde donc les documents liÃƒÂ©s sÃƒÂ©parÃƒÂ©s et je vÃƒÂ©rifie seulement les clauses explicites de remplacement ou d'adoption :",
            _ when asksMainOrStatus => "Je m'appuie uniquement sur les sources disponibles : il faut distinguer deux niveaux, le document principal qui porte l'exigence de base, puis l'AC/correctif/amendement qui porte la modification associÃƒÂ©e. Les extraits retrouvÃƒÂ©s ne prouvent pas que le correctif remplace tout le document principal :",
            _ when asksLatestDefault && latestYear > 0 => $"Si l'utilisateur ne prÃƒÂ©cise pas l'annÃƒÂ©e, je cite d'abord la version la plus rÃƒÂ©cente soutenue par les sources sÃƒÂ©lectionnÃƒÂ©es, puis je garde les anciennes versions comme contexte historique. L'annÃƒÂ©e la plus rÃƒÂ©cente dÃƒÂ©tectÃƒÂ©e dans ces sources est {latestYear} :",
            _ when asksLatestDefault => "Si l'utilisateur ne prÃƒÂ©cise pas l'annÃƒÂ©e, je cite d'abord la version la plus rÃƒÂ©cente soutenue par les sources sÃƒÂ©lectionnÃƒÂ©es, puis je garde les anciennes versions comme contexte historique :",
            _ when asksProof => "Pour prouver que la rÃƒÂ©ponse utilise la bonne version, je cite le nom exact du fichier et la page, en sÃƒÂ©parant les versions ou fichiers proches :",
            _ => "Voici la traÃƒÂ§abilitÃƒÂ© que je peux ÃƒÂ©tablir ÃƒÂ  partir des pages retrouvÃƒÂ©es :"
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        var duplicateLabels = FindDuplicateDocumentVersionTraceabilityLabels(hits);
        foreach (var hit in hits)
        {
            var docLabel = BuildDocumentVersionTraceabilityHitLabel(hit, duplicateLabels);
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            if (hit.PageEnd > hit.PageStart)
            {
                sb.Append('-');
                sb.Append(hit.PageEnd);
            }

            if (!string.IsNullOrWhiteSpace(excerpt))
            {
                sb.Append(" : ");
                sb.Append(excerpt);
            }

            sb.AppendLine();
        }

        var caveat = language == "en"
            ? "Conclusion: use these pages as traceability evidence, but do not infer a replacement, status change or full applicability unless the cited page explicitly says so."
            : "Conclusion : ces pages servent de preuves de traÃƒÂ§abilitÃƒÂ©. Je n'en dÃƒÂ©duis pas un remplacement, un changement de statut ou une applicabilitÃƒÂ© complÃƒÂ¨te si la page citÃƒÂ©e ne le dit pas explicitement.";
        sb.Append(caveat);
        return sb.ToString().TrimEnd();
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromDocumentVersionTraceabilityHits(ToolResults toolResults, string query, int maxSources = 4)
    {
        var hits = SelectDocumentVersionTraceabilityHits(toolResults, query, maxSources).ToList();
        var duplicateLabels = FindDuplicateDocumentVersionTraceabilityLabels(hits);
        return hits
            .Select(hit =>
            {
                var source = BuildSourceRefFromRagHit(hit);
                source.Label = BuildDocumentVersionTraceabilityHitLabel(hit, duplicateLabels);
                return source;
            })
            .ToList();
    }

    private static HashSet<string> FindDuplicateDocumentVersionTraceabilityLabels(IReadOnlyList<RagHitSummary> hits)
        => hits
            .GroupBy(static hit => string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Select(hit => hit.DocPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string BuildDocumentVersionTraceabilityHitLabel(RagHitSummary hit, IReadOnlySet<string> duplicateLabels)
    {
        var label = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        if (string.IsNullOrWhiteSpace(label) || !duplicateLabels.Contains(label))
            return label;

        var context = GetNearestDisambiguatingPathSegment(hit.DocPath);
        return string.IsNullOrWhiteSpace(context) ? label : $"{label} ({context})";
    }

    private static string GetNearestDisambiguatingPathSegment(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return string.Empty;

        var segments = docPath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var segment = segments[i].Trim();
            if (segment.Length == 0)
                continue;

            var normalized = NormalizeLexicalLookup(segment);
            if (Regex.IsMatch(normalized, @"^(?:pdf|documents?|docs?|sources?|files?|fichiers?)$", RegexOptions.CultureInvariant))
                continue;

            return segment;
        }

        return string.Empty;
    }
}
