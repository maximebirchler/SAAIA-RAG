using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedCleanTitleCacheMaxEntries = 8192;
    private static readonly object SourceBackedCleanTitleCacheGate = new();
    private static readonly Dictionary<string, string> SourceBackedCleanTitleCache = new(StringComparer.Ordinal);

    private static string CleanSourceBackedOptionTitle(string? value)
    {
        var cacheKey = value ?? string.Empty;
        lock (SourceBackedCleanTitleCacheGate)
        {
            if (SourceBackedCleanTitleCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var cleaned = CleanSourceBackedOptionTitleUncached(value);
        lock (SourceBackedCleanTitleCacheGate)
        {
            if (SourceBackedCleanTitleCache.Count >= SourceBackedCleanTitleCacheMaxEntries)
                SourceBackedCleanTitleCache.Clear();

            SourceBackedCleanTitleCache[cacheKey] = cleaned;
        }

        return cleaned;
    }

    private static string CleanSourceBackedOptionTitleUncached(string? value)
    {
        var compositeTitle = TryCleanCompositeSourceBackedOptionTitle(value);
        if (!string.IsNullOrWhiteSpace(compositeTitle))
            return compositeTitle;

        var title = RepairSplitOcrBrokenTitleWords(HumanizePlanItemTitleV2(value ?? string.Empty));
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var originalTitle = title;
        title = StripLeadingStructuredPlanningFieldLabelFromTitle(title);
        title = StripLeadingCompactOcrContextLabelFromTitle(title);
        title = StripLeadingFusedShortOcrPrefixBeforeTitle(title);
        title = StripTrailingAllCapsContextLabelFromTitle(title);
        title = StripTrailingFusedArticleContextSuffixFromTitle(title);
        title = StripTrailingStructuredPlanningContextSuffixFromTitle(title);
        title = StripTrailingGenericStructuredContextPhraseFromTitle(title);
        title = StripTrailingBrokenPrincipalContextSuffixFromTitle(title);
        title = StripTrailingCompactOcrContextLabelFromTitle(title);
        title = StripLeadingStructuredSectionNoiseFromTitle(title);
        title = StripTrailingStructuredSectionNoiseFromTitle(title);
        title = StripLeadingLowSignalStructuredFieldValuePrefixFromTitle(title);
        title = Regex.Replace(
            title,
            @"^([\p{Lu}0-9 '&/\-,\u00c0-\u017f]{4,48})\s+(?:l['\u2019]|le|la|les|un|une)\b.+$",
            "$1",
            RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"^(?:D(?:E|[\u00c9])J)\s*(?=[\p{Lu}\u00c0-\u017f])", string.Empty, RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"^(?:de|du|des|d['\u2019])\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\bD[E\u00c9]\s+J(?=[\p{Lu}\u00c0-\u017f])", "DEJ ", RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\bD[E\u00c9]J(?=[\p{Lu}\u00c0-\u017f])", "DEJ ", RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"(?<=[\p{Lu}\u00c0-\u017f]{4})(?:HEALTHY|LIGHT|EASY|QUICK|FAST|FACILE|RAPIDE|LEGER|L[E\u00c9]GER)(?=\d|\b)",
            string.Empty,
            RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"(?i)\b([\p{L}]{5,})aux\b", "$1 aux", RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"^(.{3,70}?)(?:\s+(?:pour\s+\d+|for\s+\d+|para\s+\d+|per\s+\d+|pr[e\u00e9]paration|preparation|preparaci[o\u00f3]n|preparacao|zubereitung|method|m[e\u00e9]thode|methode|procedure|proc[e\u00e9]dure|materials?|mat[e\u00e9]riel|materiel|items?)\b).*$",
            "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"^(.{3,70}?)(?:\s+\p{L}{1,3})?\s+\d+\)?\s+(?:\p{L}{1,3}\s+)?(?:min|mn)\b.*$",
            "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"^(.{4,70}?)(?:\s+(?:se|est|sont|peut|peuvent|permet|permettent|pour\s+obtenir|le\s+temps|observer)\b).*$",
            "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"^(?<lead>[\p{Lu}\p{N} '&/\-,]{4,70})\s+\d{1,3}$",
            "${lead}",
            RegexOptions.CultureInvariant);
        title = StripTrailingIsolatedOcrSuffixFromTitle(title);
        title = StripTrailingVariantNoiseFromTitle(title);
        title = StripTrailingStructuredPlanningContextSuffixFromTitle(title);
        title = StripTrailingGenericStructuredContextPhraseFromTitle(title);
        title = StripTrailingBrokenPrincipalContextSuffixFromTitle(title);
        title = RecoverTitleBeforeTrailingGenericPrincipalContext(originalTitle, title);
        title = StripLeadingLowSignalStructuredFieldValuePrefixFromTitle(title);
        title = StripLeadingConnectorFieldValuePrefixBeforeStrongTitle(title);
        title = StripLeadingFusedShortOcrPrefixBeforeTitle(title);
        title = StripTrailingFusedArticleContextSuffixFromTitle(title);
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim(' ', '-', ':', '.', ',', ';');
        if (ShouldRestoreClippedMeaningfulTitleSuffix(originalTitle, title))
            title = originalTitle;

        title = StripLeadingConnectorFieldValuePrefixBeforeStrongTitle(title);
        return title.Length <= 90 ? title : title[..90].TrimEnd();
    }

    private static bool ShouldRestoreClippedMeaningfulTitleSuffix(string originalTitle, string cleanedTitle)
    {
        var original = CollapseWhitespace(originalTitle).Trim(' ', '-', ':', '.', ',', ';');
        var cleaned = CollapseWhitespace(cleanedTitle).Trim(' ', '-', ':', '.', ',', ';');
        if (string.IsNullOrWhiteSpace(original)
            || string.IsNullOrWhiteSpace(cleaned)
            || original.Length <= cleaned.Length + 3
            || !original.StartsWith(cleaned, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedCleaned = NormalizeLexicalLookup(cleaned);
        var originalTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(original))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cleanedTerms = ExtractPlanningAnswerSupportTerms(normalizedCleaned)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (cleanedTerms.Length > 0 && originalTerms.Length >= cleanedTerms.Length)
        {
            var lastCleaned = cleanedTerms[^1];
            var originalAtSamePosition = originalTerms[cleanedTerms.Length - 1];
            if (lastCleaned.Length >= 3
                && originalAtSamePosition.StartsWith(lastCleaned, StringComparison.Ordinal)
                && originalAtSamePosition.Length >= lastCleaned.Length + 1
                && originalAtSamePosition.Length <= lastCleaned.Length + 2)
            {
                return true;
            }
        }

        var lastCleanedToken = Regex.Matches(normalizedCleaned, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastCleanedToken)
            || !(lastCleanedToken.Length <= 3 || IsLowercaseSourceBackedDisplayParticle(lastCleanedToken)))
        {
            return false;
        }

        var suffix = original[cleaned.Length..].Trim(' ', '-', ':', '.', ',', ';');
        var normalizedSuffix = NormalizeLexicalLookup(suffix);
        if (string.IsNullOrWhiteSpace(normalizedSuffix))
            return false;

        if (Regex.IsMatch(
                normalizedSuffix,
                @"^(?:components?|preparation|pr[eÃ©]paration|technique|method|m[eÃ©]thode|procedure|temps|time|duration|operation|nombre|quantit[eÃ©]s?|quantities|pour\s+\d+|for\s+\d+|min|mn|pages?|sources?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var suffixTerms = ExtractQuerySignalTerms(normalizedSuffix)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Take(4)
            .ToArray();
        return suffixTerms.Any(static term => term.Length >= 5);
    }

    private static string RecoverTitleBeforeTrailingGenericPrincipalContext(string originalTitle, string currentTitle)
    {
        var original = CollapseWhitespace(originalTitle).Trim(' ', '-', ':', '.', ',', ';');
        var current = CollapseWhitespace(currentTitle).Trim(' ', '-', ':', '.', ',', ';');
        if (string.IsNullOrWhiteSpace(original))
            return current;

        var normalizedOriginal = NormalizeLexicalLookup(original);
        if (!Regex.IsMatch(normalizedOriginal, @"\b(?:princi|principal|principaux|paux)\b", RegexOptions.CultureInvariant))
            return current;
        if (Regex.IsMatch(
                normalizedOriginal,
                @"\b(?:components?|preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|materials?|mat[eÃ©]riel)\b",
                RegexOptions.CultureInvariant))
        {
            return current;
        }

        var genericTerms = BuildStructuredAxisPlannerGenericInventoryTerms("fr", query: null)
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .Where(static term => !Regex.IsMatch(term, @"^(?:princi|principal|principaux|paux)$", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static term => term.Length)
            .ToArray();

        foreach (var term in genericTerms)
        {
            var match = Regex.Match(
                original,
                @"^(?<lead>.{8,90}?)(?:\s*" + Regex.Escape(term) + @")\s+princ?i?(?:\s*-\s*|\s+)?(?:paux|pales?|pale|pal|p)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var lead = CollapseWhitespace(match.Groups["lead"].Value).Trim(' ', '-', ':', '.', ',', ';');
            var normalizedLead = NormalizeLexicalLookup(lead);
            if (!LooksLikeConcreteTitleBeforeStructuredContextSuffix(lead))
                continue;

            var normalizedCurrent = NormalizeLexicalLookup(current);
            if (string.IsNullOrWhiteSpace(normalizedCurrent)
                || normalizedCurrent.Contains("princi", StringComparison.Ordinal)
                || normalizedCurrent.Contains("paux", StringComparison.Ordinal)
                || normalizedCurrent.StartsWith(normalizedLead, StringComparison.Ordinal)
                || normalizedLead.StartsWith(normalizedCurrent, StringComparison.Ordinal))
            {
                return lead;
            }
        }

        return current;
    }

    private static string StripTrailingVariantNoiseFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = Regex.Match(
            value,
            @"^(?<lead>.{8,80}?)\s+(?:variant|variante|version|versi[oÃ³]n|vers[aÃ£]o)\s+\d+\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? CollapseWhitespace(match.Groups["lead"].Value)
            : value;
    }

    private static string StripTrailingAllCapsContextLabelFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        const string suffixPattern = @"^[A-Z\u00c0-\u00d6\u00d8-\u00de]{4,6}\s+[A-Z\u00c0-\u00d6\u00d8-\u00de]{2,3}(?:\s+[A-Z\u00c0-\u00d6\u00d8-\u00de]{4,}){0,3}$";
        for (var split = 8; split < value.Length - 6; split++)
        {
            var lead = CollapseWhitespace(value[..split]);
            var suffix = CollapseWhitespace(value[split..]);
            if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(suffix))
                continue;

            var previous = value[split - 1];
            var current = value[split];
            if (!char.IsWhiteSpace(previous)
                && !(IsUppercaseOcrBoundaryVowel(previous) && IsUppercaseOcrBoundaryConsonant(current)))
            {
                continue;
            }

            if (!Regex.IsMatch(suffix, suffixPattern, RegexOptions.CultureInvariant))
                continue;

            var suffixTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(suffix)).ToArray();
            var suffixRawTerms = Regex.Matches(
                    suffix,
                    @"[A-Z\u00c0-\u00d6\u00d8-\u00de]+",
                    RegexOptions.CultureInvariant)
                .Select(static match => match.Value)
                .ToArray();
            var leadTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(lead)).ToArray();
            if (leadTerms.Length >= 2
                && suffixTerms.Length is >= 2 and <= 5
                && suffixRawTerms.Any(static term => term.Length <= 3)
                && LooksLikeCleanTitleBeforeIsolatedOcrSuffix(lead))
            {
                return lead;
            }
        }

        return value;
    }

    private static bool IsUppercaseOcrBoundaryVowel(char value)
        => "AEIOUY????????????????????????".Contains(value);

    private static bool IsUppercaseOcrBoundaryConsonant(char value)
        => char.IsUpper(value) && !IsUppercaseOcrBoundaryVowel(value);

    private static string StripTrailingCompactOcrContextLabelFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        for (var split = 8; split < value.Length - 6; split++)
        {
            var lead = CollapseWhitespace(value[..split]);
            var suffix = CollapseWhitespace(value[split..]);
            if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(suffix))
                continue;

            if (!char.IsLetterOrDigit(value[split - 1]) || !char.IsLetter(value[split]))
                continue;

            if (!Regex.IsMatch(
                    suffix,
                    @"^[\p{L}\u00c0-\u017f-]{4,12}\s+[\p{L}\u00c0-\u017f-]{4,12}(?:\s+[\p{L}\u00c0-\u017f-]{4,12}){0,3}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            var leadTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(lead)).ToArray();
            var suffixTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(suffix)).ToArray();
            if (leadTerms.Length >= 2
                && suffixTerms.Length is >= 2 and <= 4
                && IsLikelyCompactOcrContextSuffixFirstTerm(suffixTerms[0])
                && suffixTerms.All(static term => term.Length >= 4)
                && LooksLikeCleanTitleBeforeIsolatedOcrSuffix(lead))
            {
                return lead;
            }
        }

        return value;
    }

    private static string StripTrailingGenericStructuredContextPhraseFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var genericTerms = BuildStructuredAxisPlannerGenericInventoryTerms("fr", query: null)
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
        if (genericTerms.Count == 0)
            return value;

        var maxSplit = Math.Min(80, value.Length - 4);
        for (var split = 4; split <= maxSplit; split++)
        {
            var lead = CollapseWhitespace(value[..split]);
            var suffix = CollapseWhitespace(value[split..]);
            if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(suffix))
                continue;

            var previous = value[split - 1];
            var current = value[split];
            var hasVisibleBoundary = char.IsWhiteSpace(previous) || previous is '-' or ':' or ';' or ',' or '|';
            var hasLikelyOcrBoundary = char.IsLetterOrDigit(previous) && char.IsUpper(current);
            if (!hasVisibleBoundary && !hasLikelyOcrBoundary)
                continue;

            var normalizedSuffix = NormalizeLexicalLookup(suffix);
            var suffixTerms = ExtractQuerySignalTerms(normalizedSuffix)
                .Where(static term => term.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (suffixTerms.Length is < 1 or > 6)
                continue;

            var hasGenericFirstSuffixTerm = IsGenericPlanningCoverageTerm(suffixTerms[0])
                || genericTerms.Contains(suffixTerms[0])
                || genericTerms.Any(generic => generic.Length >= 4 && suffixTerms[0].StartsWith(generic, StringComparison.Ordinal));
            var hasBrokenPrincipalContext = hasGenericFirstSuffixTerm
                && suffixTerms.Any(static term =>
                    term is "princi" or "paux" or "principaux" or "principal" or "principale" or "principales")
                && (suffix.Contains('-', StringComparison.Ordinal) || suffixTerms.Length >= 2);
            var hasGenericContextTerm = suffixTerms.Any(term =>
                IsGenericPlanningCoverageTerm(term)
                || genericTerms.Contains(term)
                || genericTerms.Any(generic => generic.Length >= 4 && term.StartsWith(generic, StringComparison.Ordinal)))
                || hasBrokenPrincipalContext;
            if (!hasGenericContextTerm)
                continue;

            var looksLikeContextSuffix = hasBrokenPrincipalContext
                || suffix.Contains('-', StringComparison.Ordinal)
                || suffixTerms.Any(static term => term is "princi" or "paux" or "principaux" or "principal" or "principale")
                || suffixTerms.Length >= 2;
            if (!looksLikeContextSuffix)
                continue;

            if (LooksLikeConcreteTitleBeforeStructuredContextSuffix(lead))
            {
                return lead;
            }
        }

        return value;
    }

    private static string StripTrailingBrokenPrincipalContextSuffixFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string? bestLead = null;
        var bestScore = int.MinValue;
        var maxSplit = Math.Min(90, value.Length - 6);
        for (var split = 6; split <= maxSplit; split++)
        {
            var lead = CollapseWhitespace(value[..split]).Trim(' ', '-', ':', '.', ',', ';');
            var suffix = CollapseWhitespace(value[split..]).Trim(' ', '-', ':', '.', ',', ';');
            if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(suffix))
                continue;

            var previous = value[split - 1];
            var current = value[split];
            var hasBoundary = char.IsWhiteSpace(previous)
                || previous is '-' or ':' or ';' or ',' or '|'
                || (char.IsLetterOrDigit(previous) && char.IsLetter(current));
            if (!hasBoundary)
                continue;

            var suffixTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(suffix))
                .Where(static term => term.Length >= 3)
                .Take(5)
                .ToArray();
            if (suffixTerms.Length is < 2 or > 4)
                continue;

            var hasBrokenPrincipalTail = suffixTerms.Skip(1).Any(static term => term is "princi" or "paux");
            if (!hasBrokenPrincipalTail)
                continue;

            var firstSuffixTerm = suffixTerms[0];
            var suffixScore = ScoreBrokenPrincipalContextSuffixFirstTerm(firstSuffixTerm);
            if (suffixScore <= 0)
                continue;

            var leadTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(lead))
                .Where(static term => term.Length >= 3)
                .Where(static term => !IsGenericPlanningCoverageTerm(term))
                .Take(6)
                .ToArray();
            if (leadTerms.Length < 1 || leadTerms.All(static term => term.Length < 5))
                continue;

            if (!LooksLikePlausibleTitleBeforeBrokenPrincipalContext(lead))
                continue;

            var score = (suffixScore * 100) + Math.Min(lead.Length, 90);
            if (score > bestScore)
            {
                bestScore = score;
                bestLead = lead;
            }
        }

        return bestLead ?? value;
    }

    private static int ScoreBrokenPrincipalContextSuffixFirstTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized)
            || IsGenericPlanningCoverageTerm(normalized)
            || IsLowercaseSourceBackedDisplayParticle(normalized)
            || normalized is "princi" or "paux"
            || normalized.Length is < 4 or > 12)
        {
            return 0;
        }

        if (Regex.IsMatch(normalized, @"[bcdfghjklmnpqrstvwxyz]{3,}", RegexOptions.CultureInvariant))
            return 0;

        var score = 10;
        if (normalized.Length is >= 5 and <= 8)
            score += 10;
        else if (normalized.Length > 8)
            score -= 8;

        if (Regex.IsMatch(
                normalized,
                @"^(?:bl|br|ch|cl|cr|dr|fl|fr|gl|gr|pl|pr|qu|sc|sk|sl|sm|sn|sp|st|tr|tw|wh)[aeiouy]",
                RegexOptions.CultureInvariant))
        {
            score += 12;
        }
        else if (Regex.IsMatch(normalized, @"^[aeiouy]", RegexOptions.CultureInvariant))
        {
            score += 4;
        }

        return score;
    }

    private static bool LooksLikePlausibleTitleBeforeBrokenPrincipalContext(string? lead)
    {
        var value = CollapseWhitespace(lead ?? string.Empty).Trim(' ', '-', ':', '.', ',', ';');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized)
            || LooksLikeStandaloneStructuredPlanningFieldLabel(normalized)
            || LooksLikeStructuredPlanningFieldOrOcrFragment(normalized)
            || LooksLikeGenericStructuredInventoryTitle(normalized))
        {
            return false;
        }

        var terms = ExtractPlanningAnswerSupportTerms(normalized)
            .Where(static term => term.Length >= 3)
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Take(8)
            .ToArray();
        return terms.Length >= 1 && terms.Any(static term => term.Length >= 5);
    }

    private static bool IsLikelyCompactOcrContextSuffixFirstTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return IsGenericPlanningCoverageTerm(normalized)
            || BuildStructuredAxisPlannerGenericInventoryTerms("fr", query: null)
                .Select(NormalizeLexicalLookup)
                .Any(candidate => string.Equals(candidate, normalized, StringComparison.Ordinal));
    }

    private static string StripTrailingIsolatedOcrSuffixFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = Regex.Replace(
            value,
            @"^(?<lead>(?:[\p{L}\p{N}'\u2019/-]{2,}\s+){3,}[\p{L}\p{N}'\u2019/-]{2,})\s+[oO0]$",
            "${lead}",
            RegexOptions.CultureInvariant).Trim();

        var shortSuffix = Regex.Match(
            value,
            @"^(?<lead>.{10,80}?)\s+(?<suffix>(?:[\p{Lu}0-9]{1,3}|[\p{Lu}][\p{Ll}]{1,2})(?:\s+(?:[\p{Lu}0-9]{1,3}|[\p{Lu}][\p{Ll}]{1,2})){1,4})$",
            RegexOptions.CultureInvariant);
        if (shortSuffix.Success
            && LooksLikeCleanTitleBeforeIsolatedOcrSuffix(shortSuffix.Groups["lead"].Value)
            && LooksLikeIsolatedShortOcrSuffix(shortSuffix.Groups["suffix"].Value))
        {
            return CollapseWhitespace(shortSuffix.Groups["lead"].Value);
        }

        return value;
    }

    private static bool LooksLikeTrailingIsolatedOcrSuffixTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (Regex.IsMatch(
            value,
            @"^(?:[\p{L}\p{N}'\u2019/-]{2,}\s+){4,}[oO0]$",
            RegexOptions.CultureInvariant))
        {
            return true;
        }

        var shortSuffix = Regex.Match(
            value,
            @"^(?<lead>.{10,80}?)\s+(?<suffix>(?:[\p{Lu}0-9]{1,3}|[\p{Lu}][\p{Ll}]{1,2})(?:\s+(?:[\p{Lu}0-9]{1,3}|[\p{Lu}][\p{Ll}]{1,2})){1,4})$",
            RegexOptions.CultureInvariant);
        return shortSuffix.Success
            && LooksLikeCleanTitleBeforeIsolatedOcrSuffix(shortSuffix.Groups["lead"].Value)
            && LooksLikeIsolatedShortOcrSuffix(shortSuffix.Groups["suffix"].Value);
    }

    private static bool LooksLikeCleanTitleBeforeIsolatedOcrSuffix(string? lead)
    {
        var normalized = NormalizeLexicalLookup(lead);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var terms = ExtractPlanningAnswerSupportTerms(normalized)
            .Where(static term => term.Length >= 3)
            .Take(8)
            .ToArray();
        return terms.Length >= 2
            && terms.Any(static term => term.Length >= 5)
            && !Regex.IsMatch(normalized, @"\b(?:de|du|des|a|Ã |au|aux|et|ou|with|and|of)$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeIsolatedShortOcrSuffix(string? suffix)
    {
        var tokens = CollapseWhitespace(suffix ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || tokens.Length > 5)
            return false;

        var unknownShortTokens = 0;
        foreach (var token in tokens)
        {
            var normalized = NormalizeLexicalLookup(token);
            if (normalized.Length is < 1 or > 3)
                return false;
            if (!IsCommonShortNaturalTitleSuffixToken(normalized))
                unknownShortTokens++;
        }

        return unknownShortTokens >= 2;
    }

    private static bool IsCommonShortNaturalTitleSuffixToken(string token)
        => IsLowercaseSourceBackedDisplayParticle(token);

    private static string TryCleanCompositeSourceBackedOptionTitle(string? value)
    {
        var raw = CollapseWhitespace(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw)
            || !Regex.IsMatch(raw, @"\|\||\||//", RegexOptions.CultureInvariant))
        {
            return string.Empty;
        }

        return Regex.Split(raw, @"\s*(?:\|\||\||//)\s*", RegexOptions.CultureInvariant)
            .Select(CleanSourceBackedOptionTitle)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Score = ScoreCompositeSourceBackedOptionTitleVariant(title)
            })
            .Where(static item => item.Score > -50)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Title)
            .FirstOrDefault() ?? string.Empty;
    }

    private static int ScoreCompositeSourceBackedOptionTitleVariant(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return -100;

        var score = 0;
        var terms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length is >= 2 and <= 6)
            score += 30;
        if (terms.Any(static term => term.Length >= 5))
            score += 8;
        if (LooksLikeStandaloneStructuredPlanningFieldLabel(normalized)
            || LooksLikeStructuredPlanningFieldOrOcrFragment(normalized))
        {
            score -= 100;
        }
        if (LooksLikeGenericStructuredInventoryTitle(normalized))
            score -= 80;
        if (Regex.IsMatch(normalized, @"\b(?:components?|preparation|pr[eÃ©]paration|technique)\b", RegexOptions.CultureInvariant))
            score -= 60;

        return score;
    }

    private static readonly Regex LeadingStructuredPlanningFieldLabelTitleRegex = new(
        @"^(?:components?|preparation|pr[eÃ©]paration|technique|m[eÃ©]thode|methode|procedure|etapes?|[eÃ©]tapes?)\s*(?:[:\-/]\s*)?(?<rest>[\p{L}\p{N} '&/,\-\u00c0-\u017f]{4,100})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingUpperStructuredPlanningFieldLabelTitleRegex = new(
        @"^(?:COMPONENTS?|COMPOSANTS?|REQUIREMENTS?|PREPARATION|PR[EÃ‰]PARATION|TECHNIQUE|M[EÃ‰]THODE|METHODE|PROCEDURE|ETAPES?|[EÃ‰]TAPES?)(?<rest>[\p{Lu}0-9 '&/,\-\u00c0-\u017f]{4,100})$",
        RegexOptions.CultureInvariant);

    private static string StripLeadingStructuredPlanningFieldLabelFromTitle(string title)
    {
        var match = LeadingStructuredPlanningFieldLabelTitleRegex.Match(title);
        if (!match.Success)
            match = LeadingUpperStructuredPlanningFieldLabelTitleRegex.Match(title);

        if (!match.Success)
            return title;

        var rest = CollapseWhitespace(match.Groups["rest"].Value).Trim(' ', '-', ':', '.', ',', ';');
        var termCount = ExtractQuerySignalTerms(NormalizeLexicalLookup(rest))
            .Where(static term => term.Length >= 4)
            .Take(2)
            .Count();
        return termCount >= 2 ? rest : title;
    }

    private static string StripLeadingStructuredSectionNoiseFromTitle(string title)
    {
        var cleaned = Regex.Replace(
            title,
            @"^(?:astuces?|tips?|conseils?)(?:[-\s]+)?(?=[\p{Lu}\u00c0-\u017f]{4,})",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var termCount = ExtractQuerySignalTerms(NormalizeLexicalLookup(cleaned))
            .Where(static term => term.Length >= 4)
            .Take(2)
            .Count();
        return string.IsNullOrWhiteSpace(cleaned) || termCount < 2 ? title : cleaned;
    }

    private static string StripTrailingStructuredSectionNoiseFromTitle(string title)
    {
        var cleaned = Regex.Replace(
            title,
            @"(?:\s+|(?<=[\p{L}\u00c0-\u017f])(?=Temps\s+de|Nombre\s+de|Components?|Pr[eÃ©]paration|Technique|Materials?|Items?|Steps?|Method|Procedure))(?:Temps\s+de|Nombre\s+de|Components?|Pr[eÃ©]paration|Technique|Materials?|Items?|Steps?|Method|Procedure)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?:LES\s+[AÃ€]|LE\s+[AÃ€]|LA\s+[AÃ€])$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.CultureInvariant).Trim(' ', '-', ':', '.', ',', ';');

        var termCount = ExtractQuerySignalTerms(NormalizeLexicalLookup(cleaned))
            .Where(static term => term.Length >= 3)
            .Take(2)
            .Count();
        return string.IsNullOrWhiteSpace(cleaned) || termCount < 1 ? title : cleaned;
    }

    private static string StripLeadingCompactOcrContextLabelFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var maxSplit = Math.Min(28, value.Length - 8);
        for (var split = 6; split <= maxSplit; split++)
        {
            var prefix = CollapseWhitespace(value[..split]);
            var rest = CollapseWhitespace(value[split..]);
            if (string.IsNullOrWhiteSpace(prefix)
                || string.IsNullOrWhiteSpace(rest)
                || !char.IsLetterOrDigit(value[split - 1])
                || !char.IsLetter(value[split])
                || !Regex.IsMatch(prefix, @"^[\p{Lu}\p{Lt}0-9-]{6,28}$", RegexOptions.CultureInvariant)
                || !prefix.Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            var restTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(rest))
                .Where(static term => term.Length >= 3)
                .Take(7)
                .ToArray();
            if (restTerms.Length >= 2
                && restTerms.Any(static term => term.Length >= 5)
                && LooksLikeCleanTitleBeforeIsolatedOcrSuffix(rest))
            {
                return rest;
            }
        }

        return value;
    }

    private static string StripLeadingFusedShortOcrPrefixBeforeTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return value;

        var first = parts[0];
        if (!Regex.IsMatch(first, @"^[\p{Lu}\u00c0-\u017f]{7,24}$", RegexOptions.CultureInvariant))
            return value;

        for (var prefixLength = 4; prefixLength <= Math.Min(7, first.Length - 4); prefixLength++)
        {
            var prefix = NormalizeLexicalLookup(first[..prefixLength]);
            if (prefix.Length < 4)
                continue;

            var consonants = prefix.Count(static c => "bcdfghjklmnpqrstvwxyz".IndexOf(c) >= 0);
            if (consonants < Math.Max(3, prefix.Length - 1))
                continue;
            if (!Regex.IsMatch(prefix, @"[bcdfghjklmnpqrstvwxyz]{2}$", RegexOptions.CultureInvariant))
                continue;

            var rebuilt = CollapseWhitespace(first[prefixLength..] + " " + string.Join(' ', parts.Skip(1)));
            if (LooksLikeCleanTitleBeforeIsolatedOcrSuffix(rebuilt))
                return rebuilt;
        }

        return value;
    }

    private static string StripTrailingFusedArticleContextSuffixFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var maxSplit = Math.Min(80, value.Length - 5);
        for (var split = 5; split <= maxSplit; split++)
        {
            if (!char.IsLetter(value[split - 1]) || !char.IsLetter(value[split]))
                continue;
            if (split > 0 && char.IsWhiteSpace(value[split - 1]))
                continue;

            var lead = CollapseWhitespace(value[..split]).Trim(' ', '-', ':', '.', ',', ';');
            var suffix = CollapseWhitespace(value[split..]).Trim(' ', '-', ':', '.', ',', ';');
            if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(suffix))
                continue;

            var normalizedSuffix = NormalizeLexicalLookup(suffix);
            if (!Regex.IsMatch(normalizedSuffix, @"^(?:les|le|la|des|de|du|d|a|au|aux)\b", RegexOptions.CultureInvariant))
                continue;

            var suffixTerms = ExtractQuerySignalTerms(normalizedSuffix)
                .Where(static term => term.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToArray();
            if (suffixTerms.Length is < 1 or > 5)
                continue;

            var suffixLooksLikeContext =
                suffix.Contains('-', StringComparison.Ordinal)
                || suffixTerms.Any(static term => term.Length <= 3 || Regex.IsMatch(term, @"[bcdfghjklmnpqrstvwxyz]{3,}", RegexOptions.CultureInvariant))
                || suffixTerms.Length >= 2;
            if (!suffixLooksLikeContext)
                continue;

            if (LooksLikeCleanTitleBeforeIsolatedOcrSuffix(lead))
                return lead;
        }

        return value;
    }

    private static string StripTrailingStructuredPlanningContextSuffixFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = Regex.Match(
            value,
            @"^(?<lead>.{4,80}?)\s+(?<suffix>(?:[\p{Lu}\p{Lt}][\p{L}\p{N}'\u2019-]{2,14})(?:\s+[\p{Lu}\p{Lt}]?[\p{L}\p{N}'\u2019-]{2,14}){0,5})$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return value;

        var lead = CollapseWhitespace(match.Groups["lead"].Value);
        var suffix = CollapseWhitespace(match.Groups["suffix"].Value);
        var normalizedSuffix = NormalizeLexicalLookup(suffix);
        var suffixTerms = ExtractQuerySignalTerms(normalizedSuffix)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (suffixTerms.Length is < 1 or > 6)
            return value;

        var genericTerms = BuildStructuredAxisPlannerGenericInventoryTerms("fr", query: null)
            .ToHashSet(StringComparer.Ordinal);
        var hasGenericFirstSuffixTerm = IsGenericPlanningCoverageTerm(suffixTerms[0])
            || genericTerms.Contains(suffixTerms[0])
            || genericTerms.Any(generic => generic.Length >= 4 && suffixTerms[0].StartsWith(generic, StringComparison.Ordinal));
        var hasBrokenPrincipalContext = hasGenericFirstSuffixTerm
            && suffixTerms.Any(static term =>
                term is "princi" or "paux" or "principaux" or "principal" or "principale" or "principales")
            && (suffix.Contains('-', StringComparison.Ordinal) || suffixTerms.Length >= 2);
        var hasGenericContextTerm = suffixTerms.Any(term =>
            IsGenericPlanningCoverageTerm(term)
            || genericTerms.Contains(term)
            || genericTerms.Any(generic => generic.Length >= 4 && term.StartsWith(generic, StringComparison.Ordinal)))
            || hasBrokenPrincipalContext;
        if (!hasGenericContextTerm)
            return value;

        var looksLikeOcrContextSuffix =
            hasBrokenPrincipalContext
            || suffix.Contains('-', StringComparison.Ordinal)
            || suffixTerms.Any(static term => term is "princi" or "paux" or "principaux" or "principal" or "principale")
            || suffixTerms.Length >= 2;
        if (!looksLikeOcrContextSuffix)
            return value;

        return LooksLikeConcreteTitleBeforeStructuredContextSuffix(lead) ? lead : value;
    }

    private static bool LooksLikeConcreteTitleBeforeStructuredContextSuffix(string? lead)
    {
        var value = CollapseWhitespace(lead ?? string.Empty).Trim(' ', '-', ':', '.', ',', ';');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized)
            || LooksLikeStandaloneStructuredPlanningFieldLabel(normalized)
            || LooksLikeStructuredPlanningFieldOrOcrFragment(normalized)
            || LooksLikeGenericStructuredInventoryTitle(normalized)
            || Regex.IsMatch(
                normalized,
                @"\b(?:page|pages?|section|chapitre|chapter|annexe|appendix|sommaire|index|table\s+des\s+matieres|contents?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var terms = ExtractPlanningAnswerSupportTerms(normalized)
            .Where(static term => term.Length >= 3)
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Take(8)
            .ToArray();
        if (terms.Length == 0)
            return false;

        if (terms.Length >= 2)
        {
            var lastTerm = terms[^1];
            return terms.Any(static term => term.Length >= 5)
                && (terms.Length >= 3 || lastTerm.Length >= 5);
        }

        var rawTokenCount = Regex.Matches(value, @"[\p{L}\p{N}'\u2019-]+", RegexOptions.CultureInvariant).Count;
        return terms[0].Length >= 5
            && value.Length >= 6
            && rawTokenCount <= 5;
    }

    private static string StripLeadingLowSignalStructuredFieldValuePrefixFromTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = Regex.Match(
            value,
            @"^(?<prefix>[\p{Lu}\p{Ll}\u00c0-\u017f'\u2019]{2,10}(?:\s+(?:et|and|avec|with|&)\s+|\s+)[\p{Lu}\p{Ll}\u00c0-\u017f'\u2019]{2,10}(?:\s+[\p{Lu}\p{Ll}\u00c0-\u017f'\u2019]{2,10})?)\s+(?<rest>[\p{Lu}\p{Lt}0-9][\p{Lu}\p{Lt}0-9 '\u2019&/,\-\u00c0-\u017f]{6,90})$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return value;

        var prefix = CollapseWhitespace(match.Groups["prefix"].Value);
        var rest = CollapseWhitespace(match.Groups["rest"].Value);
        if (!LooksLikeLowSignalStructuredFieldValuePrefix(prefix))
            return value;

        var restTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(rest))
            .Where(static term => term.Length >= 3)
            .Take(8)
            .ToArray();
        if (restTerms.Length < 2 || restTerms.All(static term => term.Length < 5))
            return value;

        var restLetters = rest.Where(char.IsLetter).ToArray();
        if (restLetters.Length < 6)
            return value;

        var upperRatio = restLetters.Count(char.IsUpper) / (double)restLetters.Length;
        return upperRatio >= 0.55 ? rest : value;
    }

    private static string StripLeadingConnectorFieldValuePrefixBeforeStrongTitle(string? title)
    {
        var value = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        foreach (Match boundary in Regex.Matches(value, @"\s+", RegexOptions.CultureInvariant))
        {
            var prefix = CollapseWhitespace(value[..boundary.Index]);
            var rest = CollapseWhitespace(value[(boundary.Index + boundary.Length)..]).Trim(' ', '-', ':', '.', ',', ';');
            if (prefix.Length > 42 || string.IsNullOrWhiteSpace(rest))
                break;

            if (!LooksLikeLowSignalStructuredFieldValuePrefix(prefix))
                continue;

            var restTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(rest))
                .Where(static term => term.Length >= 3)
                .Take(8)
                .ToArray();
            var restLetters = rest.Where(char.IsLetter).ToArray();
            if (restLetters.Length < 6)
                continue;

            var upperRatio = restLetters.Count(char.IsUpper) / (double)restLetters.Length;
            if (upperRatio < 0.55)
                continue;

            if (restTerms.Length >= 2 && restTerms.Any(static term => term.Length >= 5))
                return rest;
        }

        return value;
    }

    private static bool LooksLikeLowSignalStructuredFieldValuePrefix(string? prefix)
    {
        var normalized = NormalizeLexicalLookup(prefix);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var terms = Regex.Matches(normalized, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static term => term is not ("et" or "and" or "avec" or "with"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return terms.Length is >= 2 and <= 4
            && terms.All(static term => term.Length <= 8)
            && Regex.IsMatch(normalized, @"\b(?:et|and|avec|with)\b", RegexOptions.CultureInvariant);
    }
}
