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
    private static IReadOnlyList<RagHitSummary> SelectDocumentVersionTraceabilityHits(ToolResults toolResults, string query, int maxHits)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var requestedReferenceKeys = ExtractRequestedDocumentSpecificReferenceKeys(normalizedQuery);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit)
                || LooksLikeDocumentStatusHit(hit)
                || TraceabilityHitMatchesRequestedReference(hit, requestedReferenceKeys))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit)
                || LooksLikeDocumentStatusHit(hit)
                || TraceabilityHitMatchesRequestedReference(hit, requestedReferenceKeys))
            .ToList();
        if (allHits.Count == 0 || maxHits <= 0)
            return [];

        var queryStatusTokens = ExtractRequestedDocumentStatusTokens(normalizedQuery).ToHashSet(StringComparer.Ordinal);
        var queryStatusSurfaceTerms = ExtractRequestedDocumentStatusSurfaceTerms(normalizedQuery);
        var asksReplacement = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:remplace|remplacer|replacement|replace|replaces|substitue|supersede|supersedes|automatiquement|automatically)\b",
            RegexOptions.CultureInvariant);
        var asksLatestDefault = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:ne\s+precise\s+pas|sans\s+preciser|sans\s+dire.{0,40}annee|without\s+specifying|without\s+saying.{0,40}year|derniere|latest|newest|recent|recente|plus\s+recente|current|actuelle)\b",
            RegexOptions.CultureInvariant);
        var asksHistoricalVersion = LooksLikeHistoricalDocumentVersionRequest(normalizedQuery);
        var asksVersionContrast = LooksLikeDocumentVersionContrastRequest(normalizedQuery);
        var maxYear = asksLatestDefault
            ? allHits.Select(ExtractDocumentVersionYear).Where(static year => year.HasValue).DefaultIfEmpty().Max()
            : null;
        var explicitYears = ExtractDocumentVersionYears(normalizedQuery);

        var ranked = allHits
            .Select((hit, index) => new DocumentTraceabilityRankedHit(
                hit,
                index,
                GetDocumentStatusToken(hit),
                ExtractDocumentVersionYear(hit),
                Score:
                    ComputeRagHitLexicalRelevance(query, $"{hit.DocName} {hit.DocPath}") * 5
                    + ComputeRagHitLexicalRelevance(query, $"{hit.SectionTitle} {hit.HeadingPath}") * 2
                    + ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit))
                    + ComputeBackendSelectionPriority(hit)
                    + (hit.RetrievalQueryIndex == 0 ? 12 : 0)
                    + ((hit.RetrievalHitRank is >= 0 and <= 1) ? 8 : 0)
                    + (LooksLikeDocumentStatusHit(hit) ? 8 : 0)
                    + (LooksLikeMainDocumentHit(hit) ? 5 : 0)
                    + hit.Score))
            .Select(item => item with
            {
                Score = item.Score
                        + (!string.IsNullOrWhiteSpace(item.Status) && queryStatusTokens.Contains(item.Status!) ? 45 : 0)
                        + (DocumentStatusSurfaceMatches(item.Hit, queryStatusSurfaceTerms) ? 80 : 0)
                        + (maxYear.HasValue && item.Year == maxYear ? 24 : 0)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .ToList();
        if (asksVersionContrast && explicitYears.Length == 0 && queryStatusTokens.Count == 0 && !asksReplacement)
        {
            ranked = KeepContrastingTraceabilityVersionsByReference(ranked)
                .ToList();
        }
        else if (asksHistoricalVersion && explicitYears.Length == 0 && queryStatusTokens.Count == 0 && !asksReplacement)
        {
            ranked = KeepHistoricalTraceabilityVersionsByReference(ranked)
                .ToList();
        }
        else if (explicitYears.Length > 0 && queryStatusTokens.Count == 0 && !asksReplacement)
        {
            var explicitYearHits = ranked
                .Where(item => item.Year.HasValue && explicitYears.Contains(item.Year.Value))
                .ToList();
            if (explicitYearHits.Count > 0)
                ranked = explicitYearHits;
        }
        else if (queryStatusTokens.Count == 0 && !asksReplacement)
        {
            ranked = KeepLatestTraceabilityVersionsByReference(ranked)
                .ToList();
        }

        var selected = new List<RagHitSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(RagHitSummary hit)
        {
            if (selected.Count >= maxHits)
                return;

            var key = $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}";
            if (seen.Add(key))
                selected.Add(hit);
        }

        var wantsMainAndStatus = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:(?:document|doc|fichier|file)\s+(?:principal|main)|\bac\b|corrigendum|correction|berichtigung|amendment|amendement)\b",
            RegexOptions.CultureInvariant);
        if (wantsMainAndStatus)
        {
            var statusHit = ranked.FirstOrDefault(static item => LooksLikeDocumentStatusHit(item.Hit));
            if (statusHit is not null)
                Add(statusHit.Hit);

            var mainHit = ranked.FirstOrDefault(static item => LooksLikeMainDocumentHit(item.Hit));
            if (mainHit is not null)
                Add(mainHit.Hit);

            if (selected.Count >= 2)
                return selected;
        }

        if (asksLatestDefault && maxYear.HasValue)
        {
            var latestHit = ranked.FirstOrDefault(item => item.Year == maxYear.Value);
            if (latestHit is not null)
                Add(latestHit.Hit);
        }

        if (asksVersionContrast)
        {
            var contrastFamily =
                selected
                    .Select(GetDocumentReferenceFamilyKey)
                    .FirstOrDefault(static family => !string.IsNullOrWhiteSpace(family))
                ?? ranked
                    .Select(item => GetDocumentReferenceFamilyKey(item.Hit))
                    .FirstOrDefault(static family => !string.IsNullOrWhiteSpace(family));

            static bool SameFamily(string? expectedFamily, RagHitSummary hit)
            {
                if (string.IsNullOrWhiteSpace(expectedFamily))
                    return true;

                var family = GetDocumentReferenceFamilyKey(hit);
                return string.IsNullOrWhiteSpace(family)
                       || string.Equals(family, expectedFamily, StringComparison.Ordinal);
            }

            var historicalHit = ranked.FirstOrDefault(item =>
                SameFamily(contrastFamily, item.Hit)
                && LooksLikeHistoricalDocumentVersionHit(item.Hit))
                ?? ranked.FirstOrDefault(item => LooksLikeHistoricalDocumentVersionHit(item.Hit));
            if (historicalHit is not null)
                Add(historicalHit.Hit);

            var historicalFamily = historicalHit is null
                ? string.Empty
                : GetDocumentReferenceFamilyKey(historicalHit.Hit);
            var currentHit = string.IsNullOrWhiteSpace(historicalFamily)
                ? null
                : ranked.FirstOrDefault(item =>
                    SameFamily(historicalFamily, item.Hit)
                    && !LooksLikeHistoricalDocumentVersionHit(item.Hit));
            if (currentHit is null && historicalHit is not null)
            {
                currentHit = ranked
                    .Where(static item => !LooksLikeHistoricalDocumentVersionHit(item.Hit))
                    .Select(item => new
                    {
                        Item = item,
                        Overlap = ComputeDocumentVersionReferenceOverlapScore(historicalHit.Hit, item.Hit)
                    })
                    .Where(static item => item.Overlap > 0)
                    .OrderByDescending(static item => item.Overlap)
                    .ThenByDescending(static item => item.Item.Score)
                    .Select(static item => item.Item)
                    .FirstOrDefault();
            }

            currentHit ??= ranked.FirstOrDefault(item =>
                SameFamily(contrastFamily, item.Hit)
                && !LooksLikeHistoricalDocumentVersionHit(item.Hit));
            currentHit ??= ranked.FirstOrDefault(item => !LooksLikeHistoricalDocumentVersionHit(item.Hit));
            if (currentHit is not null)
                Add(currentHit.Hit);
        }

        foreach (var requestedReferenceKey in requestedReferenceKeys)
        {
            var referenceHit = ranked.FirstOrDefault(item =>
                string.Equals(GetDocumentSpecificReferenceKey(item.Hit), requestedReferenceKey, StringComparison.Ordinal));
            if (referenceHit is not null)
                Add(referenceHit.Hit);
        }

        if (queryStatusTokens.Count > 0)
        {
            foreach (var status in queryStatusTokens)
            {
                var statusHit = ranked.FirstOrDefault(item => string.Equals(item.Status, status, StringComparison.Ordinal));
                if (statusHit is not null)
                    Add(statusHit.Hit);
            }
        }

        if (explicitYears.Length > 0)
        {
            var yearsToRepresent = asksLatestDefault
                ? explicitYears.OrderByDescending(static year => year).ToArray()
                : explicitYears;
            foreach (var year in yearsToRepresent)
            {
                var yearHit = ranked.FirstOrDefault(item => item.Year == year);
                if (yearHit is not null)
                    Add(yearHit.Hit);
            }

            if (asksReplacement && selected.Count >= 2)
                return selected;
        }

        var prefersHistoricalSurface = asksHistoricalVersion
            && explicitYears.Length == 0
            && queryStatusTokens.Count == 0
            && !asksReplacement;
        if (prefersHistoricalSurface)
        {
            foreach (var item in ranked.Where(static item => LooksLikeHistoricalDocumentVersionHit(item.Hit)))
                Add(item.Hit);

            if (selected.Count >= maxHits)
                return selected;
        }

        var dominantFamily = selected
            .Select(GetDocumentReferenceFamilyKey)
            .FirstOrDefault(static family => !string.IsNullOrWhiteSpace(family))
            ?? GetDocumentReferenceFamilyKey(ranked[0].Hit);
        foreach (var item in ranked)
        {
            if (!string.IsNullOrWhiteSpace(dominantFamily))
            {
                var family = GetDocumentReferenceFamilyKey(item.Hit);
                if (!string.IsNullOrWhiteSpace(family)
                    && !string.Equals(family, dominantFamily, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            Add(item.Hit);
        }

        return selected;
    }

    private static bool LooksLikeHistoricalDocumentVersionRequest(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;
        if (HistoricalDocumentVersionSurfaceIsNegated(normalizedQuery))
            return false;

        if (Regex.IsMatch(
                normalizedQuery,
                @"\b(?:compare|comparer|comparaison|vs|versus|difference\s+entre|difference\s+between|differences\s+between)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:ancien(?:ne)?s?\s+versions?|ancien(?:ne)?\s+edition|version\s+(?:precedente|anterieure)|old\s+version|older\s+version|previous\s+version|prior\s+version|archived\s+(?:version|document)|archives?|legacy\s+version|obsolete\s+version|historical\s+version)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDocumentVersionContrastRequest(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery) || HistoricalDocumentVersionSurfaceIsNegated(normalizedQuery))
            return false;

        var hasHistorical = ContainsHistoricalDocumentVersionSurface(normalizedQuery);
        if (!hasHistorical)
            return false;

        var hasCurrent = ContainsCurrentDocumentVersionSurface(normalizedQuery);
        var hasContrast = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:compare|comparer|comparaison|vs|versus|difference|differences|distinguer|distingue|distinguish|equivalence|equivalent|equivaut|courante|current|latest|actuelle|nouvelle|newest)\b",
            RegexOptions.CultureInvariant);

        return hasCurrent || hasContrast;
    }

    private static bool LooksLikeHistoricalDocumentVersionHit(RagHitSummary hit)
    {
        var doc = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath}");
        if (string.IsNullOrWhiteSpace(doc))
            return false;

        return Regex.IsMatch(
            doc,
            @"\b(?:old\s+versions?|old_versions?|older|previous|prior|archived?|archives?|historical|legacy|obsolete|ancienne?s?|ancien?s?|precedent(?:e)?s?|anterieur(?:e)?s?)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeDocumentVersionReferenceOverlapScore(RagHitSummary anchor, RagHitSummary candidate)
    {
        var anchorTerms = ExtractDocumentVersionReferenceTerms(anchor);
        if (anchorTerms.Count == 0)
            return 0;

        var score = 0;
        var anchorLead = GetDocumentVersionLeadingReferenceToken(anchor);
        var candidateLead = GetDocumentVersionLeadingReferenceToken(candidate);
        if (!string.IsNullOrWhiteSpace(anchorLead)
            && string.Equals(anchorLead, candidateLead, StringComparison.Ordinal))
        {
            score += 12;
        }

        foreach (var term in ExtractDocumentVersionReferenceTerms(candidate))
        {
            if (!anchorTerms.Contains(term))
                continue;

            score += Regex.IsMatch(term, @"\d", RegexOptions.CultureInvariant)
                ? 3
                : term.Length <= 3 ? 2 : 1;
        }

        return score;
    }

    private static string GetDocumentVersionLeadingReferenceToken(RagHitSummary hit)
    {
        var normalized = NormalizeLexicalLookup($"{hit.DocName} {Path.GetFileNameWithoutExtension(hit.DocPath)}");
        foreach (Match match in Regex.Matches(normalized, @"[a-z0-9]{2,}", RegexOptions.CultureInvariant))
        {
            var term = match.Value;
            if (Regex.IsMatch(
                    term,
                    @"^(?:old|older|version|versions|current|latest|newest|recent|archive|archives|archived|historical|legacy|obsolete|ancienne|anciennes|ancien|anciens|nouvelle|nouvelles|actuelle|actuelles|courante|courantes|document|documents|pdf|file|files)$",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            return term;
        }

        return string.Empty;
    }

    private static HashSet<string> ExtractDocumentVersionReferenceTerms(RagHitSummary hit)
    {
        var normalized = NormalizeLexicalLookup($"{hit.DocName} {Path.GetFileNameWithoutExtension(hit.DocPath)}");
        var terms = Regex.Matches(normalized, @"[a-z0-9]{2,}", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static term => !Regex.IsMatch(
                term,
                @"^(?:old|older|version|versions|current|latest|newest|recent|archive|archives|archived|historical|legacy|obsolete|ancienne|anciennes|ancien|anciens|nouvelle|nouvelles|actuelle|actuelles|courante|courantes|document|documents|pdf|file|files|data|sheet|sheets|safety|technical|technique)$",
                RegexOptions.CultureInvariant))
            .ToHashSet(StringComparer.Ordinal);

        return terms;
    }
}
