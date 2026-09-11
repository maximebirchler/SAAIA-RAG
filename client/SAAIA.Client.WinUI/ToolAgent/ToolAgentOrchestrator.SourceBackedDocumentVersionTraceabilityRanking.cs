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
    private static bool DocumentStatusSurfaceMatches(RagHitSummary hit, IReadOnlyList<string> requestedSurfaceTerms)
    {
        if (requestedSurfaceTerms.Count == 0)
            return false;

        var doc = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath}");
        if (string.IsNullOrWhiteSpace(doc))
            return false;

        foreach (var term in requestedSurfaceTerms)
        {
            if (term.Length <= 2)
            {
                if (Regex.IsMatch(doc, $@"\b{Regex.Escape(term)}\b", RegexOptions.CultureInvariant))
                    return true;
            }
            else if (doc.Contains(term, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeDocumentStatusHit(RagHitSummary hit)
        => !string.IsNullOrWhiteSpace(GetDocumentStatusToken(hit));

    private static bool LooksLikeMainDocumentHit(RagHitSummary hit)
    {
        var doc = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath}");
        if (string.IsNullOrWhiteSpace(doc))
            return false;

        return !Regex.IsMatch(
            doc,
            @"\b(?:ac|a\d+|pra\d+|corrigendum|correction|berichtigung|errata|amendment|amendement)\b",
            RegexOptions.CultureInvariant);
    }

    private static string? GetDocumentStatusToken(RagHitSummary hit)
    {
        var doc = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath}");
        if (Regex.IsMatch(doc, @"\bpra\d*\b", RegexOptions.CultureInvariant))
            return "draft_amendment";
        if (Regex.IsMatch(doc, @"\bac\b|corrigendum|correction|berichtigung|errata", RegexOptions.CultureInvariant))
            return "correction";
        if (Regex.IsMatch(doc, @"\ba\d+\b|amendment|amendement", RegexOptions.CultureInvariant))
            return "amendment";
        return null;
    }

    private static int? ExtractDocumentVersionYear(RagHitSummary hit)
    {
        var text = $"{hit.DocName} {hit.DocPath}";
        var years = ExtractDocumentVersionYears(text);
        return years.Length == 0 ? null : years.Max();
    }

    private static int[] ExtractDocumentVersionYears(string text)
        => Regex.Matches(text ?? string.Empty, @"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.CultureInvariant)
            .Select(match => int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : (int?)null)
            .Where(static year => year.HasValue)
            .Select(static year => year!.Value)
            .Distinct()
            .ToArray();

    private sealed record DocumentTraceabilityRankedHit(RagHitSummary Hit, int Index, string? Status, int? Year, double Score);

    private static IReadOnlyList<DocumentTraceabilityRankedHit> KeepLatestTraceabilityVersionsByReference(IReadOnlyList<DocumentTraceabilityRankedHit> ranked)
    {
        var latestByReference = ranked
            .GroupBy(item => GetDocumentSpecificReferenceKey(item.Hit), StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => new
            {
                Reference = group.Key,
                LatestYear = group.Select(static item => item.Year).Where(static year => year.HasValue).DefaultIfEmpty().Max()
            })
            .Where(static item => item.LatestYear.HasValue)
            .ToDictionary(static item => item.Reference, static item => item.LatestYear!.Value, StringComparer.Ordinal);

        if (latestByReference.Count == 0)
        {
            return DropHistoricalTraceabilityVersionsWhenCurrentExists(ranked);
        }

        var byYear = ranked
            .Where(item =>
            {
                var reference = GetDocumentSpecificReferenceKey(item.Hit);
                if (string.IsNullOrWhiteSpace(reference)
                    || !latestByReference.TryGetValue(reference, out var latestYear))
                {
                    return true;
                }

                return !item.Year.HasValue || item.Year.Value == latestYear;
            })
            .ToList();
        return DropHistoricalTraceabilityVersionsWhenCurrentExists(byYear);
    }

    private static IReadOnlyList<DocumentTraceabilityRankedHit> KeepHistoricalTraceabilityVersionsByReference(IReadOnlyList<DocumentTraceabilityRankedHit> ranked)
    {
        var byArchiveSurface = KeepArchiveSurfaceTraceabilityVersionsByReference(ranked);
        if (byArchiveSurface.Count < ranked.Count)
            return byArchiveSurface;

        var oldestByReference = ranked
            .GroupBy(item => GetDocumentSpecificReferenceKey(item.Hit), StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => new
            {
                Reference = group.Key,
                OldestYear = group.Select(static item => item.Year).Where(static year => year.HasValue).DefaultIfEmpty().Min()
            })
            .Where(static item => item.OldestYear.HasValue)
            .ToDictionary(static item => item.Reference, static item => item.OldestYear!.Value, StringComparer.Ordinal);

        if (oldestByReference.Count == 0)
            return ranked;

        return ranked
            .Where(item =>
            {
                var reference = GetDocumentSpecificReferenceKey(item.Hit);
                if (string.IsNullOrWhiteSpace(reference)
                    || !oldestByReference.TryGetValue(reference, out var oldestYear))
                {
                    return true;
                }

                return !item.Year.HasValue || item.Year.Value == oldestYear;
            })
            .ToList();
    }

    private static IReadOnlyList<DocumentTraceabilityRankedHit> DropHistoricalTraceabilityVersionsWhenCurrentExists(IReadOnlyList<DocumentTraceabilityRankedHit> ranked)
    {
        var referencesWithCurrent = ranked
            .GroupBy(item => GetDocumentSpecificReferenceKey(item.Hit), StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Where(static group => group.Any(item => LooksLikeHistoricalDocumentVersionHit(item.Hit))
                                   && group.Any(item => !LooksLikeHistoricalDocumentVersionHit(item.Hit)))
            .Select(static group => group.Key!)
            .ToHashSet(StringComparer.Ordinal);
        if (referencesWithCurrent.Count == 0)
            return ranked;

        return ranked
            .Where(item =>
            {
                var reference = GetDocumentSpecificReferenceKey(item.Hit);
                return string.IsNullOrWhiteSpace(reference)
                       || !referencesWithCurrent.Contains(reference)
                       || !LooksLikeHistoricalDocumentVersionHit(item.Hit);
            })
            .ToList();
    }

    private static IReadOnlyList<DocumentTraceabilityRankedHit> KeepArchiveSurfaceTraceabilityVersionsByReference(IReadOnlyList<DocumentTraceabilityRankedHit> ranked)
    {
        var referencesWithArchive = ranked
            .GroupBy(item => GetDocumentSpecificReferenceKey(item.Hit), StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Where(static group => group.Any(item => LooksLikeHistoricalDocumentVersionHit(item.Hit)))
            .Select(static group => group.Key!)
            .ToHashSet(StringComparer.Ordinal);
        if (referencesWithArchive.Count == 0)
            return ranked;

        return ranked
            .Where(item =>
            {
                var reference = GetDocumentSpecificReferenceKey(item.Hit);
                return string.IsNullOrWhiteSpace(reference)
                       || !referencesWithArchive.Contains(reference)
                       || LooksLikeHistoricalDocumentVersionHit(item.Hit);
            })
            .ToList();
    }

    private static IReadOnlyList<DocumentTraceabilityRankedHit> KeepContrastingTraceabilityVersionsByReference(IReadOnlyList<DocumentTraceabilityRankedHit> ranked)
    {
        var result = new List<DocumentTraceabilityRankedHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(DocumentTraceabilityRankedHit item)
        {
            var key = $"{item.Hit.DocPath}|{item.Hit.PageStart}|{item.Hit.PageEnd}";
            if (seen.Add(key))
                result.Add(item);
        }

        foreach (var group in ranked
                     .GroupBy(item => GetDocumentSpecificReferenceKey(item.Hit), StringComparer.Ordinal)
                     .Where(static group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var historical = group.FirstOrDefault(item => LooksLikeHistoricalDocumentVersionHit(item.Hit));
            var current = group.FirstOrDefault(item => !LooksLikeHistoricalDocumentVersionHit(item.Hit));
            if (historical is null || current is null)
                continue;

            Add(current);
            Add(historical);
        }

        foreach (var item in ranked)
            Add(item);

        return result;
    }

    private static string GetDocumentReferenceFamilyKey(RagHitSummary hit)
    {
        var doc = NormalizeLexicalLookup($"{hit.DocName} {Path.GetFileNameWithoutExtension(hit.DocPath)}");
        if (string.IsNullOrWhiteSpace(doc))
            return string.Empty;

        var standard = MatchDocumentStandardReference(doc);
        if (standard.Success)
            return BuildDocumentReferenceFamilyKey(standard);

        doc = Regex.Replace(
            doc,
            @"\b(?:19|20)\d{2}\b|\b(?:ac|a\d+|pra\d+|corrigendum|correction|berichtigung|errata|amendment|amendement|main|principal|requirements?|document|doc|file|pdf)\b",
            " ",
            RegexOptions.CultureInvariant);
        var tokens = ExtractQuerySignalTerms(doc)
            .Where(static term => term.Length >= 3)
            .Where(static term => !Regex.IsMatch(term, @"^(?:safety|machinery|part|parts|general|principles|design|standard|norme|normes)$", RegexOptions.CultureInvariant))
            .Take(4)
            .ToArray();

        return tokens.Length == 0 ? string.Empty : string.Join(' ', tokens);
    }

    private static string GetDocumentSpecificReferenceKey(RagHitSummary hit)
    {
        var doc = NormalizeLexicalLookup($"{hit.DocName} {Path.GetFileNameWithoutExtension(hit.DocPath)}");
        if (string.IsNullOrWhiteSpace(doc))
            return string.Empty;

        var standard = MatchDocumentStandardReference(doc);
        if (standard.Success)
            return BuildDocumentSpecificReferenceKey(standard);

        return GetDocumentReferenceFamilyKey(hit);
    }

    private static IReadOnlyList<string> ExtractRequestedDocumentSpecificReferenceKeys(string normalizedQuery)
    {
        var keys = new List<string>();
        foreach (Match match in Regex.Matches(
                     normalizedQuery ?? string.Empty,
                     DocumentStandardReferencePattern,
                     RegexOptions.CultureInvariant))
        {
            var family = BuildDocumentReferenceFamilyKey(match);
            var part1 = match.Groups["part1"].Success ? match.Groups["part1"].Value : string.Empty;
            var part2 = match.Groups["part2"].Success ? match.Groups["part2"].Value : string.Empty;
            if (!string.IsNullOrWhiteSpace(part1) && !string.IsNullOrWhiteSpace(part2))
            {
                keys.Add($"{family} {part1}");
                keys.Add($"{family} {part2}");
            }
            else if (!string.IsNullOrWhiteSpace(part1))
            {
                keys.Add($"{family} {part1}");
            }
            else
            {
                keys.Add(family);
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private const string DocumentStandardReferencePattern =
        @"\b(?<prefix>fd\s+cen\s+tr|cen\s+tr|iso|en|iec|din|sn|nf)\s+(?<number>\d{2,})(?:(?:[\s_\-./]+)(?<part1>\d{1,3}))?(?:(?:\s*/\s*|[\s_]+)(?<part2>\d{1,3}))?\b";

    private static Match MatchDocumentStandardReference(string normalizedText)
        => Regex.Match(normalizedText ?? string.Empty, DocumentStandardReferencePattern, RegexOptions.CultureInvariant);

    private static string BuildDocumentReferenceFamilyKey(Match match)
    {
        var prefix = CollapseWhitespace(match.Groups["prefix"].Value);
        var number = match.Groups["number"].Value;
        return string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(number)
            ? string.Empty
            : $"{prefix} {number}";
    }

    private static string BuildDocumentSpecificReferenceKey(Match match)
    {
        var family = BuildDocumentReferenceFamilyKey(match);
        if (string.IsNullOrWhiteSpace(family))
            return string.Empty;

        var part = match.Groups["part1"].Success ? match.Groups["part1"].Value : string.Empty;
        return string.IsNullOrWhiteSpace(part)
            ? family
            : $"{family} {part}";
    }

    private static bool TraceabilityHitMatchesRequestedReference(RagHitSummary hit, IReadOnlyList<string> requestedReferenceKeys)
    {
        if (requestedReferenceKeys.Count == 0)
            return false;

        var hitKey = GetDocumentSpecificReferenceKey(hit);
        if (string.IsNullOrWhiteSpace(hitKey))
            return false;

        return requestedReferenceKeys.Any(key => string.Equals(key, hitKey, StringComparison.Ordinal));
    }
}
