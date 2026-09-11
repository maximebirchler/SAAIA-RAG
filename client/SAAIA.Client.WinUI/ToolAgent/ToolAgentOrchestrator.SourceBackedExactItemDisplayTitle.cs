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

    private static string ResolveSourceBackedExactItemDisplayTitle(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var candidates = hits
            .SelectMany(ExtractSourceBackedTitleCandidates)
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(title => new
            {
                Title = title,
                Score = ComputeSourceBackedDisplayTitleScore(requestedTitle, title)
            })
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Title.Any(char.IsUpper))
            .ThenBy(static item => item.Title.Length)
            .ToArray();

        var best = candidates.FirstOrDefault(static item => item.Score > 0);
        if (best is not null && LooksLikeOcrTitleRunOn(requestedTitle, best.Title))
            return requestedTitle;

        return best?.Title ?? requestedTitle;
    }

    private static bool LooksLikeOcrTitleRunOn(string requestedTitle, string candidateTitle)
    {
        var requested = NormalizeLexicalLookup(requestedTitle);
        var candidate = NormalizeLexicalLookup(candidateTitle);
        if (requested.Length < 4 || candidate.Length <= requested.Length + 14)
            return false;

        if (candidate.StartsWith(requested, StringComparison.Ordinal))
        {
            return !Regex.IsMatch(candidate[requested.Length..], @"^\s+(?:de|du|des|a|au|aux|with|and|et)\b", RegexOptions.CultureInvariant);
        }

        var index = candidate.IndexOf(requested, StringComparison.Ordinal);
        if (index is > 0 and <= 80)
        {
            var prefix = candidate[..index].Trim();
            return prefix.Length >= 12
                && Regex.IsMatch(prefix, @"\b(?:apres|after|avant|before|prevoir|prevoyez|" + OperationalActionLeadPattern + @")\b", RegexOptions.CultureInvariant);
        }

        return false;
    }

    private static IEnumerable<string> ExtractSourceBackedTitleCandidates(RagHitSummary hit)
    {
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                yield return card.Title;
        }

        foreach (var source in new[] { hit.ContextualSnippet, hit.SectionTitle, hit.HeadingPath })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            foreach (var title in ExtractProfileTitleCandidates(source))
                yield return title;
        }
    }

    private static IEnumerable<string> ExtractProfileTitleCandidates(string value)
    {
        foreach (Match match in Regex.Matches(
            value,
            @"(?i)\bMatched\s+(?:profile|quoted)\s+title\s*:\s*(?<titles>.+?)(?=\s*(?:\|\s*)?(?:Document|Section|HeadingPath|ChunkType|Pages|Evidence)\s*:|$)"))
        {
            var titles = match.Groups["titles"].Value;
            foreach (var title in SplitSourceBackedTitleList(titles))
                yield return title;
        }

        var trimmed = CollapseWhitespace(value);
        if (!trimmed.Contains(':', StringComparison.Ordinal)
            && trimmed.Length is >= 3 and <= 90)
        {
            yield return trimmed;
        }
    }

    private static IEnumerable<string> SplitSourceBackedTitleList(string value)
    {
        foreach (var part in Regex.Split(value ?? string.Empty, @"\s*;\s*"))
        {
            var title = CollapseWhitespace(part)
                .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7');
            if (!string.IsNullOrWhiteSpace(title))
                yield return title;
        }
    }

    private static bool IsUsefulSourceBackedDisplayTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (normalized.Length is < 3 or > 90)
            return false;

        if (LooksLikeProcedureSentenceTitle(normalized))
            return false;

        return normalized is not "document" and not "document profile";
    }

    private static bool IsUsableSourceBackedOptionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var cleaned = CleanSourceBackedOptionTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var normalized = NormalizeLexicalLookup(cleaned);
        return IsUsefulSourceBackedDisplayTitle(cleaned)
            && !LooksLikePlanItemNoise(cleaned)
            && !LooksLikeWeakSourceBackedOptionTitle(cleaned)
            && !LooksLikeProcedureSentenceTitle(normalized);
    }

    private static bool LooksLikeWeakSourceBackedOptionTitle(string? title)
    {
        var raw = CollapseWhitespace(title ?? string.Empty);
        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var lead = StripShortOcrPrefixForTitleQuality(normalized);

        if (LooksLikeNoisyGeneratedSourceBackedExplorationQuery(raw))
            return true;

        if (Regex.IsMatch(
                lead,
                @"^(?:\d+[a-z]?|[ivxlcdm]{1,6})\s+(?:(?:a\s+){0,2}voir|see|refer|consulter|page|section|chapter|part|partie|annexe|appendix|table|index)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var structuredText = NormalizeStructuredScanText(raw);
        var startsWithQuantity = Regex.IsMatch(
            lead,
            @"^\d+(?:[,.]\d+)?\s*(?:x\b|g\b|kg\b|mg\b|ml\b|cl\b|l\b|oz\b|lb\b|lbs\b|mm\b|cm\b|m\b|%|\p{L}{3,})",
            RegexOptions.CultureInvariant);
        var unitMarkerCount = Regex.Matches(
                lead,
                @"\b(?:g|kg|mg|ml|cl|l|oz|lb|lbs|mm|cm|m|%|pieces?|items?|units?|valeurs?|values?)\b",
                RegexOptions.CultureInvariant)
            .Count;
        if (startsWithQuantity
            && (CountNumericFactMarkers(structuredText) >= 2
                || unitMarkerCount > 0
                || ExtractQuerySignalTerms(lead).Count() >= 8))
        {
            return true;
        }

        if (Regex.IsMatch(
                lead,
                @"^(?:\p{L}{1,3}\s+){0,3}(?:mettre|mettez|placer|placez|ajouter|ajoutez|retirer|retirez|ouvrir|ouvrez|fermer|fermez|programmer|programmez|verifier|verifiez|controler|controlez|inspecter|inspectez|noter|notez|signer|signez|former|collecter|set|add|remove|place|put|open|close|program|check|verify|inspect|record|sign)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                lead,
                @"^(?:a\s+voir|voir\s+aussi|see\s+also|refer\s+to|pictogrammes?|pictograms?|symbols?|symboles?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }





        if (unitMarkerCount >= 2 && ExtractQuerySignalTerms(lead).Count() >= 7)
            return true;

        if (CountNumericFactMarkers(structuredText) >= 3)
            return true;

        if (LooksLikeLowValueMarketingOrDocumentHeading(lead))
            return true;

        if (LooksLikeAudienceOrCollectionSourceBackedHeadingTitle(lead))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:become|be)(?:\s+a|\s+the)?\s+(?:master|creator|maker|pro)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b\p{L}{5,}be(?:\s+a|\s+the)?\s+(?:master|creator|maker|pro)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var words = ExtractQuerySignalTerms(normalized).ToArray();
        if (words.Length >= 7
            && Regex.IsMatch(normalized, @"\b(?:et|and|avec|with|puis|then|de|du|des|of|the)\b", RegexOptions.CultureInvariant)
            && CountNumericFactMarkers(structuredText) >= 1)
        {
            return true;
        }

        var letters = raw.Where(char.IsLetter).ToArray();
        if (letters.Length >= 8)
        {
            var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
            if (upperRatio >= 0.82
                && words.Length is >= 2 and <= 5
                && Regex.IsMatch(
                    normalized,
                    @"\b(?:become|be\s+a|be\s+the|welcome|companion|community|creative|solution|solutions|catalog|catalogue|guide|edition|copyright|isbn|introduction|foreword|preface)\b",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        if (words.Length is >= 2 and <= 5
            && Regex.IsMatch(
                normalized,
                @"(?:^|\b)(?:become|be\s+a|be\s+the|devenir|welcome|bienvenue)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(normalized, @"\b(?:procedure|process|policy|control|standard|specification|instruction|requirements?|exigences?|controle|norme)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeAudienceOrCollectionSourceBackedHeadingTitle(string? normalizedTitle)
    {
        var title = NormalizeLexicalLookup(normalizedTitle);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var terms = ExtractQuerySignalTerms(title)
            .Where(static term => term.Length >= 3)
            .Take(8)
            .ToArray();
        if (terms.Length is 0 or > 6)
            return false;

        var hasAudienceCue = Regex.IsMatch(
            title,
            @"\b(?:parents?|parental|familles?|families?|family|enfants?|children|kids?|busy|press[eÃƒÂ©]s?|presses?|actifs?|active)\b",
            RegexOptions.CultureInvariant);
        if (!hasAudienceCue)
            return false;

        return terms.All(static term => Regex.IsMatch(
            term,
            @"^(?:collection|pratique|practical|fut[eÃƒÂ©]e?|smart|parents?|parental|familles?|families?|family|enfants?|children|kids?|busy|press[eÃƒÂ©]s?|presses?|actifs?|active|guide|livre|book|edition|magazine)$",
            RegexOptions.CultureInvariant));
    }

    private static bool LooksLikeLowValueMarketingOrDocumentHeading(string? normalizedTitle)
    {
        var title = NormalizeLexicalLookup(normalizedTitle);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var words = ExtractQuerySignalTerms(title).Take(8).ToArray();
        if (words.Length == 0 || words.Length > 6)
            return false;

        if (Regex.IsMatch(
                title,
                @"^(?:become|welcome|discover|explore|learn|start|getting\s+started|introduction|foreword|preface|copyright|isbn)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                title,
                @"\b(?:catalog|catalogue|companion|community|newsletter|account|rating|share|copyright|isbn)\b",
                RegexOptions.CultureInvariant)
            && words.Length <= 5)
        {
            return true;
        }

        return false;
    }

    private static string StripShortOcrPrefixForTitleQuality(string normalized)
    {
        var value = CollapseWhitespace(normalized);
        for (var i = 0; i < 2; i++)
        {
            var trimmed = Regex.Replace(
                value,
                @"^(?:[a-z]{1,3}|[ivxlcdm]{1,4})\s+(?=\p{L}{4,})",
                string.Empty,
                RegexOptions.CultureInvariant).Trim();
            if (string.Equals(trimmed, value, StringComparison.Ordinal))
                break;

            value = trimmed;
        }

        return value;
    }

    private static bool LooksLikeProcedureSentenceTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an)?\s*(?:occuper|occupez|organiser|organize|planifier|planifiez|schedule|utiliser|use|using|collecter|collectez|choisir|choose|verifier|verify|check|valider|validate|executer|execute|run|lire|read|documenter|document|noter|note|enregistrer|record|consigner|escalader|escalate|prioriser|prioritize|classer|rank|filtrer|filter|comparer|compare|analyser|analyze|analyser|inspecter|inspect|" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeFusedFunctionWordProcedureFragment(normalizedTitle))
            return true;

        if (LooksLikeSubjectlessInstructionFragmentTitle(normalizedTitle))
            return true;

        return Regex.IsMatch(
            normalizedTitle,
            @"\b(?:\d+\s*(?:min|h|hours?|minutes?|seconds?|secondes?|%|(?:\u00b0|deg|degres?)\s*c)|step\s+\d+|etape\s+\d+|page\s+\d+|section\s+\d+)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalizedTitle, @"[.;:!?]\s+\p{L}", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSubjectlessInstructionFragmentTitle(string normalizedTitle)
    {
        var value = StripShortOcrPrefixForTitleQuality(CollapseWhitespace(normalizedTitle));
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var leadMatch = Regex.Match(
            value,
            @"^(?<lead>\p{L}{4,24})(?:\s+(?<rest>.+))?$",
            RegexOptions.CultureInvariant);
        if (!leadMatch.Success || string.IsNullOrWhiteSpace(leadMatch.Groups["rest"].Value))
            return false;

        var lead = leadMatch.Groups["lead"].Value;
        var rest = CollapseWhitespace(leadMatch.Groups["rest"].Value);
        var looksImperative = Regex.IsMatch(lead, @"ez$", RegexOptions.CultureInvariant);
        var looksInfinitive = Regex.IsMatch(lead, @"(?:er|ir|re)$", RegexOptions.CultureInvariant);
        if (!looksImperative && !looksInfinitive)
            return false;

        var startsWithDirectObject = Regex.IsMatch(
            rest,
            @"^(?:l|le|la|les|un|une|des|du|de\s+la|d|the|a|an|some)\b",
            RegexOptions.CultureInvariant);
        var startsWithPreposition = Regex.IsMatch(
            rest,
            @"^(?:a|au|aux|avec|dans|en|sur|sous|pour|to|into|with|in|on|over|under)\b",
            RegexOptions.CultureInvariant);
        var hasProcedureParameterCue = Regex.IsMatch(
            value,
            @"\b(?:pendant|during|durante|while|jusqu|jusque|until|avant|apres|after|before|then|ensuite|puis|minutes?|minute|secondes?|seconds?|heures?|hours?|degres?|degrees?|temperature|vitesse|speed|programme|program|cycle|mode|settings?|parametres?|parameters?|signal|step|etape)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(rest, @"\b\d+(?:[,.]\d+)?\b", RegexOptions.CultureInvariant);
        var startsWithProcedureAdverb = Regex.IsMatch(
            rest,
            @"^(?:encore|again|then|ensuite|puis|about|around|environ|approximately|approx)\b",
            RegexOptions.CultureInvariant);
        var endsWithDanglingProcedureConnector = Regex.IsMatch(
            value,
            @"\b(?:a|au|aux|avec|dans|en|sur|sous|pour|pendant|during|for|to|into|with|in|on|over|under|jusqu|jusque|until)$",
            RegexOptions.CultureInvariant);

        if (looksImperative && (startsWithDirectObject || startsWithPreposition || hasProcedureParameterCue))
            return true;

        if (looksInfinitive && startsWithDirectObject)
            return true;

        if (looksInfinitive && startsWithProcedureAdverb && hasProcedureParameterCue)
            return true;

        return looksInfinitive
            && ((startsWithPreposition && hasProcedureParameterCue)
                || endsWithDanglingProcedureConnector);
    }

    private static bool LooksLikeFusedFunctionWordProcedureFragment(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var fusedFunctionWords = Regex.Matches(
                normalizedTitle,
                @"\b(?:des|les|la|le|du|de|au|aux|un|une)[a-z]{4,}(?:et|and)?\b",
                RegexOptions.CultureInvariant)
            .Count;
        if (fusedFunctionWords == 0)
            return false;

        if (fusedFunctionWords >= 2)
            return true;

        var startsLikeInstruction = Regex.IsMatch(
            normalizedTitle,
            @"^\p{L}{5,}(?:er|ez|ir|re)\b",
            RegexOptions.CultureInvariant);
        var endsWithDanglingConnector = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:a|au|aux|avec|chez|dans|de|des|du|en|et|pour|sans|sous|sur|to|with|in|on|under|over|from)$",
            RegexOptions.CultureInvariant);
        return startsLikeInstruction && endsWithDanglingConnector;
    }

    private static int ComputeSourceBackedDisplayTitleScore(string requestedTitle, string candidateTitle)
    {
        var requested = NormalizeLexicalLookup(requestedTitle);
        var candidate = NormalizeLexicalLookup(candidateTitle);
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
            return 0;

        if (string.Equals(candidate, requested, StringComparison.Ordinal))
            return 100;

        var requestSeeds = new List<string> { requested };
        requestSeeds.AddRange(BuildTypoTolerantQueryVariants(requested).Select(NormalizeLexicalLookup));
        var requestTerms = requestSeeds
            .SelectMany(ExtractRequestedTitleSignalTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestTerms.Length == 0)
            return 0;

        var matchedIndexes = requestTerms
            .Select((term, index) => new { Term = term, Index = index })
            .Where(item => candidate.Contains(item.Term, StringComparison.Ordinal))
            .Select(static item => item.Index)
            .Distinct()
            .OrderBy(static index => index)
            .ToArray();
        if (matchedIndexes.Length == 0)
            return 0;

        var score = matchedIndexes.Length * 20;
        score += matchedIndexes.Sum(index => Math.Max(1, requestTerms.Length - index) * 3);
        if (matchedIndexes.SequenceEqual(Enumerable.Range(0, matchedIndexes.Length)) && matchedIndexes.Length >= 2)
            score += 20;
        if (matchedIndexes[0] > 0)
            score -= matchedIndexes[0] * 10;
        if (matchedIndexes.Length == requestTerms.Length)
            score += 30;
        if (candidate.Contains(requested, StringComparison.Ordinal) || requested.Contains(candidate, StringComparison.Ordinal))
            score += 20;

        return score;
    }

}
