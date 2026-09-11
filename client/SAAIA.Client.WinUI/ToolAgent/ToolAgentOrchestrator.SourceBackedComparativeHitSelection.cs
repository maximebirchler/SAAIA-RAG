using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<RagHitSummary> SelectComparativeDocumentaryHits(IEnumerable<RagHitSummary> hits, string query, int maxHits)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        var entityAnchors = ExtractComparativeEntityAnchorTerms(query);
        var shouldRequireEntityCoverage = entityAnchors.Length >= 2;

        var scored = hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                EntityMatches = GetMatchedComparativeEntityAnchorIndexes(hit, entityAnchors),
                ExplicitReference = MatchesExplicitComparativeDocumentReference(query, hit),
                Score = ComputeComparativeDocumentaryEvidenceScore(hit, evidenceQuery, focusTerms)
            })
            .Where(item => item.Score > 0 || item.ExplicitReference)
            .Where(item => !shouldRequireEntityCoverage || item.EntityMatches.Length > 0 || item.ExplicitReference)
            .Select(item => new ComparativeScoredHit(
                item.Hit,
                item.Index,
                item.EntityMatches,
                item.Score + (item.EntityMatches.Length * 10.0) + (item.ExplicitReference ? 5.0 : 0.0)))
            .OrderByDescending(item => item.EntityMatches.Length)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToList();
        if (scored.Count == 0)
            return Array.Empty<RagHitSummary>();

        if (shouldRequireEntityCoverage)
            return SelectComparativeEntityCoverageHits(scored, entityAnchors.Length, maxHits);

        var bestByDocument = scored
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Hit.DocPath) ? item.Hit.DocName : item.Hit.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToList();

        if (bestByDocument.Count >= 2)
            return bestByDocument.Take(maxHits).Select(item => item.Hit).ToList();

        return scored.Take(maxHits).Select(item => item.Hit).ToList();
    }

    private sealed record ComparativeScoredHit(RagHitSummary Hit, int Index, int[] EntityMatches, double Score);

    private static IReadOnlyList<RagHitSummary> SelectComparativeEntityCoverageHits(
        IReadOnlyList<ComparativeScoredHit> scored,
        int entityCount,
        int maxHits)
    {
        var selected = new List<ComparativeScoredHit>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredEntities = new HashSet<int>();

        for (var entityIndex = 0; entityIndex < entityCount && selected.Count < maxHits; entityIndex++)
        {
            var best = scored
                .Where(item => item.EntityMatches.Contains(entityIndex))
                .Where(item => !selectedKeys.Contains(BuildRagHitIdentityKey(item.Hit)))
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .FirstOrDefault();
            if (best is null)
                continue;

            selected.Add(best);
            selectedKeys.Add(BuildRagHitIdentityKey(best.Hit));
            foreach (var match in best.EntityMatches)
                coveredEntities.Add(match);
        }

        foreach (var item in scored
                     .Where(item => !selectedKeys.Contains(BuildRagHitIdentityKey(item.Hit)))
                     .OrderByDescending(item => item.EntityMatches.Count(match => !coveredEntities.Contains(match)))
                     .ThenByDescending(item => item.Score)
                     .ThenBy(item => item.Index))
        {
            if (selected.Count >= maxHits)
                break;

            selected.Add(item);
            selectedKeys.Add(BuildRagHitIdentityKey(item.Hit));
            foreach (var match in item.EntityMatches)
                coveredEntities.Add(match);
        }

        return selected.Select(static item => item.Hit).ToList();
    }

    private static string[][] ExtractComparativeEntityAnchorTerms(string? query)
    {
        return ExtractComparativeEntityPhrases(query)
            .Select(phrase => ExtractQuerySignalTerms(NormalizeLexicalLookup(phrase))
                .Where(term => !IsComparativeRetrievalNoiseTerm(term))
                .Where(term => term.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray())
            .Where(static terms => terms.Length > 0)
            .Take(8)
            .ToArray();
    }

    private static IEnumerable<string> ExtractComparativeEntityPhrases(string? query)
    {
        var text = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var match = Regex.Match(
            text,
            @"(?i)\b(?:entre|between|zwischen|tra|fra)\s+(?<items>.+?)(?:,\s*)?\b(?:quel|quelle|quels|quelles|lequel|laquelle|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\b",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                text,
                @"(?i)\b(?:compare|comparer|comparaison|comparatif|comparative|versus|vs\.?|confronta|confrontare|vergleiche|vergleichen)\s+(?<items>.+?)(?:[?.!]|$)",
                RegexOptions.CultureInvariant);
        }

        if (!match.Success)
            yield break;

        var itemsText = Regex.Replace(
            match.Groups["items"].Value,
            @"(?i)\b(?:quel|quelle|quels|quelles|lequel|laquelle|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);

        foreach (var part in Regex.Split(
                     itemsText,
                     @"\s*(?:,|;|/|\bet\b|\bou\b|\band\b|\bor\b|\by\b|\be\b|\bund\b|\boder\b)\s*",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var phrase = CollapseWhitespace(part)
                .Trim(' ', '.', ',', ';', ':', '?', '!', '"', '\'', '\u00ab', '\u00bb');
            if (phrase.Length is >= 3 and <= 90)
                yield return phrase;
        }
    }

    private static int[] GetMatchedComparativeEntityAnchorIndexes(
        RagHitSummary hit,
        IReadOnlyList<string[]> entityAnchors)
    {
        if (entityAnchors.Count == 0)
            return Array.Empty<int>();

        var lookup = NormalizeLexicalLookup(GetRagHitComparativeEntityLookupText(hit));
        if (string.IsNullOrWhiteSpace(lookup))
            return Array.Empty<int>();

        var matches = new List<int>();
        for (var i = 0; i < entityAnchors.Count; i++)
        {
            var terms = entityAnchors[i];
            if (terms.Length == 0)
                continue;

            var matched = terms.Count(term => ComparativeEntityTermMatches(lookup, term));
            var required = terms.Length <= 3 ? terms.Length : terms.Length - 1;
            if (matched >= required)
                matches.Add(i);
        }

        return matches.ToArray();
    }

    private static bool ComparativeEntityTermMatches(string lookup, string term)
    {
        if (lookup.Contains(term, StringComparison.Ordinal))
            return true;

        return term.Length > 4
               && term.EndsWith('s')
               && lookup.Contains(term[..^1], StringComparison.Ordinal);
    }

    private static double ComputeComparativeDocumentaryEvidenceScore(RagHitSummary hit, string evidenceQuery, IReadOnlyList<string> focusTerms)
    {
        var primaryText = GetRagHitPrimaryEvidenceText(hit);
        var lookupText = GetRagHitLookupText(hit);
        var normalizedPrimary = NormalizeLexicalLookup(primaryText);
        var normalizedLookup = NormalizeLexicalLookup(lookupText);
        if (string.IsNullOrWhiteSpace(normalizedPrimary) && string.IsNullOrWhiteSpace(normalizedLookup))
            return 0;

        if (focusTerms.Count > 0
            && !focusTerms.Any(term =>
                normalizedPrimary.Contains(term, StringComparison.Ordinal)
                || normalizedLookup.Contains(term, StringComparison.Ordinal)))
        {
            return 0;
        }

        var score = ComputeRagHitLexicalRelevance(evidenceQuery, primaryText) * 8
            + ComputeRagHitLexicalRelevance(evidenceQuery, lookupText) * 2
            + Math.Min(2.0, Math.Max(0.0, hit.Score));

        var structuredProcedureScore = ComputeStructuredProcedureEvidenceCueScore(hit);
        score += structuredProcedureScore * 3.0;
        if (structuredProcedureScore >= 6
            && focusTerms.Count > 0
            && focusTerms.Any(term => normalizedLookup.Contains(term, StringComparison.Ordinal)))
        {
            score += 6;
        }

        if (Regex.IsMatch(normalizedPrimary, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|min|h)\b", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(normalizedPrimary, @"(?:^|\s)[1-9][\.)]\s+", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(normalizedPrimary, @"\b(?:" + ExactItemStructureHeadingPattern + @")\b", RegexOptions.CultureInvariant))
            score += 2;

        if (LooksLikeMidProcedureFragment(hit))
            score -= 80;
        else if (HasSourceBackedProfileTitle(hit)
                 || !string.IsNullOrWhiteSpace(ExtractSourceBackedOptionTitle(hit, query: null)))
            score += 4;

        if (Regex.IsMatch(normalizedPrimary, @"\b(?:introduction|bienvenue|sommaire|contents|index|overview|presentation|nous avons reuni|nous sommes prets)\b", RegexOptions.CultureInvariant)
            && structuredProcedureScore < 4)
        {
            score -= 16;
        }

        if (string.Equals(hit.EmbeddingBasis, "document_profile_v1", StringComparison.OrdinalIgnoreCase))
            score -= 5;

        return score;
    }

}
