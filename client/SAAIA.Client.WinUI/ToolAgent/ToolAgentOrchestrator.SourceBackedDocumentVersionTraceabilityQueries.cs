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
    private static bool LooksLikeDocumentVersionTraceabilityRequest(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasDocumentSurface = Regex.IsMatch(
            normalized,
            @"\b(?:document|documents|doc|fichier|file|pdf|source|sources|principal|main|policy|policies|politique|standard|standards|norme|normes|certificate|certificates|certificat|certificats|iso|iec|en)\b",
            RegexOptions.CultureInvariant);
        var hasVersionOrStatus = Regex.IsMatch(
            normalized,
            @"\b(?:version|versions|edition|editions|annee|annees|year|years|status|statut|ac|a\d+|pra\d+|corrigendum|correction|berichtigung|amendment|amendement|remplace|replace|replacement|latest|derniere|recente|current|actuelle|ancienne|anciennes|older|old|precedente|previous)\b",
            RegexOptions.CultureInvariant);
        var hasDocumentStatusSurface = Regex.IsMatch(
            normalized,
            @"\b(?:ac|a\d+|pra\d+|corrigendum|correction|correctif|rectificatif|berichtigung|erratum|errata|amendment|amendement)\b",
            RegexOptions.CultureInvariant);
        var hasTraceabilityIntent = Regex.IsMatch(
            normalized,
            @"\b(?:separement|separately|prouve|prouver|preuve|prove|proves|proof|trace|tracabilite|traceability|utilise\s+bien|bonne\s+version|correct\s+version|fichier\s+proche|nearby\s+file|regarder|look|appliquer|apply|remplace|replace|replacement|contient|contenir|contenu|complete|complet|tout|toute|full|whole|contains|content|verifier|verify|check|issue|ancienne|anciennes|older|old|precedente|previous|signaler|signal|mentionne|mentionner|mention|mentions|cite|cites|cited|citation|confuse|confuses|confusing|confusion|existe|existence|exists?|annee|annees|year|years|vient|viennent|comes?|dire|specifie|specifies?|precise|preciser)\b",
            RegexOptions.CultureInvariant);
        var hasStandardReference = MatchDocumentStandardReference(normalized).Success;

        return (hasVersionOrStatus || hasStandardReference)
            && hasTraceabilityIntent
            && (hasDocumentSurface || hasStandardReference || hasDocumentStatusSurface || TryExtractPdfFileNameRequestedTitle(query) is not null);
    }

    private static string BuildDocumentVersionTraceabilityExactSearchQuery(string exactTitle, string query)
    {
        var normalizedTitle = NormalizeLexicalLookup(exactTitle);
        var compact = new List<string>();
        var reference = MatchDocumentStandardReference(normalizedTitle);
        if (reference.Success)
        {
            var referenceKey = BuildDocumentSpecificReferenceKey(reference);
            if (!string.IsNullOrWhiteSpace(referenceKey))
                compact.Add(referenceKey);
        }

        foreach (var year in ExtractDocumentVersionYears(exactTitle).OrderByDescending(static year => year).Take(2))
            compact.Add(year.ToString(CultureInfo.InvariantCulture));

        foreach (var status in ExtractRequestedDocumentStatusSurfaceTerms(NormalizeLexicalLookup($"{exactTitle} {query}")).Take(2))
            compact.Add(status);

        AddDocumentVersionTraceabilitySurfaceTerms(compact, NormalizeLexicalLookup($"{exactTitle} {query}"));

        var parts = new List<string> { exactTitle };
        parts.AddRange(compact);
        return CollapseWhitespace(string.Join(' ', parts.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static string BuildDocumentVersionTraceabilitySearchQuery(string query)
    {
        var exactTitle = TryExtractPdfFileNameRequestedTitle(query);
        if (!string.IsNullOrWhiteSpace(exactTitle))
            return BuildDocumentVersionTraceabilityExactSearchQuery(exactTitle!, query);

        var normalized = NormalizeLexicalLookup(query);
        var compact = new List<string>();
        var reference = MatchDocumentStandardReference(normalized);
        if (reference.Success)
        {
            var referenceKey = BuildDocumentSpecificReferenceKey(reference);
            if (!string.IsNullOrWhiteSpace(referenceKey))
                compact.Add(referenceKey);
        }

        foreach (var year in ExtractDocumentVersionYears(query).OrderByDescending(static year => year).Take(2))
            compact.Add(year.ToString(CultureInfo.InvariantCulture));

        foreach (var status in ExtractRequestedDocumentStatusSurfaceTerms(normalized).Take(2))
            compact.Add(status);

        AddDocumentVersionTraceabilitySurfaceTerms(compact, normalized);

        if (compact.Count > 0)
            return CollapseWhitespace(string.Join(' ', compact.Distinct(StringComparer.OrdinalIgnoreCase)));

        return string.Empty;
    }

    private static string[] BuildDocumentVersionTraceabilitySearchQueries(string query)
    {
        var queries = new List<string>();

        void Add(string? value)
        {
            var candidate = CollapseWhitespace(value ?? string.Empty);
            if (candidate.Length == 0)
                return;

            if (!queries.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
                queries.Add(candidate);
        }

        Add(BuildDocumentVersionTraceabilitySearchQuery(query));
        Add(query);
        Add(NormalizeRagQueryForRetrieval(query));

        var quotedOrRequestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(quotedOrRequestedTitle))
            Add(BuildDocumentVersionTraceabilityExactSearchQuery(quotedOrRequestedTitle!, query));

        return queries.Take(4).ToArray();
    }

    private static void AddDocumentVersionTraceabilitySurfaceTerms(List<string> terms, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery) || HistoricalDocumentVersionSurfaceIsNegated(normalizedQuery))
            return;

        var hasHistorical = ContainsHistoricalDocumentVersionSurface(normalizedQuery);
        var hasCurrent = ContainsCurrentDocumentVersionSurface(normalizedQuery);
        var hasContrast = LooksLikeDocumentVersionContrastRequest(normalizedQuery);

        if (hasHistorical)
        {
            terms.Add("ancienne version");
            terms.Add("old versions");
            terms.Add("old version");
        }

        if (hasCurrent || hasContrast)
        {
            terms.Add("version courante");
            terms.Add("current versions");
            terms.Add("current version");
        }

        if (hasContrast)
        {
            terms.Add("nouvelle version");
            terms.Add("latest version");
        }
    }

    private static IReadOnlyList<string> ExtractRequestedDocumentStatusTokens(string normalizedQuery)
    {
        var tokens = new List<string>();
        if (Regex.IsMatch(normalizedQuery, @"\bpra\d*\b", RegexOptions.CultureInvariant))
            tokens.Add("draft_amendment");
        if (Regex.IsMatch(normalizedQuery, @"\ba\d+\b", RegexOptions.CultureInvariant))
            tokens.Add("amendment");
        if (Regex.IsMatch(normalizedQuery, @"\bac\b|corrigendum|correction|berichtigung|errata", RegexOptions.CultureInvariant))
            tokens.Add("correction");
        return tokens.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> ExtractRequestedDocumentStatusSurfaceTerms(string normalizedQuery)
    {
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
                     normalizedQuery ?? string.Empty,
                     @"\b(?:pra\d+|a\d+|ac|corrigendum|correction|correctif|rectificatif|berichtigung|erratum|errata|amendment|amendement)\b",
                     RegexOptions.CultureInvariant))
        {
            terms.Add(match.Value);
        }

        return terms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
