using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalLexicalExpansion
{

    internal static IReadOnlyList<string> BuildDocumentProfileSpecificityTokens(string query)
        => ExtractLexicalQueryTokens(query)
            .Where(static token => token.Length >= 5 || IsReferenceLikeLookupTerm(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !IsGenericDocumentProfileSpecificityToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();


    internal static bool IsGenericDocumentProfileSpecificityToken(string token)
        => StructuredContentLexicon.IsStructuredItemTypeToken(token)
            || token is "procedure" or "procedures" or "document" or "documents"
            or "fichier" or "fichiers" or "source" or "sources"
            or "guide" or "guides" or "fiche" or "fiches"
            or "scan" or "scans" or "scanne" or "scannee" or "scannes" or "scannees" or "ocr"
            or "confiance" or "confidence" or "comment" or "demande" or "issue" or "indiquer"
            or "answer" or "reponse" or "repondre"
            or "sale" or "sales" or "salee" or "salees";


    internal static IReadOnlyList<string> ExtractLexicalQueryTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    internal static IReadOnlyList<string> BuildLexicalContentFallbackTerms(string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return Array.Empty<string>();

        var terms = new HashSet<string>(StringComparer.Ordinal);
        var canonicalTokens = new List<string>();
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            foreach (var variant in variants)
                terms.Add(variant);

            var canonical = variants
                .OrderBy(static variant => ContainsNonAscii(variant) ? 1 : 0)
                .ThenBy(static variant => variant.Length)
                .ThenBy(static variant => variant, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(canonical))
                canonicalTokens.Add(canonical);
        }

        AddAdjacentPhraseTerms(terms, canonicalTokens);

        var surfaceTokens = ExtractLexicalQuerySurfaceTokens(query);
        if (surfaceTokens.Count > 0)
        {
            foreach (var token in surfaceTokens)
                terms.Add(token);

            AddAdjacentPhraseTerms(terms, surfaceTokens);
        }

        AddAdjacentAlphaNumericPhraseTerms(terms, query);
        AddCrossLanguageTechnicalPhraseVariants(terms, canonicalTokens);

        foreach (var quotedPhrase in ExtractQuotedLookupPhrases(query))
        {
            terms.Add(quotedPhrase);
            var foldedPhrase = FoldDiacritics(quotedPhrase);
            if (!string.Equals(foldedPhrase, quotedPhrase, StringComparison.Ordinal))
                terms.Add(foldedPhrase);
        }

        foreach (var focusedPhrase in ExtractFocusedLookupPhrases(query))
        {
            terms.Add(focusedPhrase);
            var foldedPhrase = FoldDiacritics(focusedPhrase);
            if (!string.Equals(foldedPhrase, focusedPhrase, StringComparison.Ordinal))
                terms.Add(foldedPhrase);
        }

        var filteredTerms = terms
            .Where(static term => term.Length >= 4)
            .Where(static term => !LexicalStopwords.Contains(term))
            .Where(static term => !SpecificAnchorStopwords.Contains(term))
            .ToArray();

        var phraseTerms = filteredTerms
            .Where(static term => term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);
        var singleTerms = filteredTerms
            .Where(static term => !term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);

        return phraseTerms
            .Concat(singleTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static void AddCrossLanguageTechnicalPhraseVariants(HashSet<string> terms, IReadOnlyList<string> canonicalTokens)
    {
        if (canonicalTokens.Count < 2)
            return;

        var normalized = $" {string.Join(' ', canonicalTokens)} ";
        if (ContainsAny(normalized, " salle propre ", " salles propres ", " clean room ", " clean rooms ", " cleanroom ", " cleanrooms "))
        {
            terms.Add("salle propre");
            terms.Add("salles propres");
            terms.Add("clean room");
            terms.Add("clean rooms");
            terms.Add("cleanroom");
            terms.Add("cleanrooms");
        }
        if (ContainsAny(
                normalized,
                " safety data sheet ",
                " safety data sheets ",
                " material safety data sheet ",
                " material safety data sheets ",
                " fiche de donnees de securite ",
                " fiches de donnees de securite ",
                " fiche donnees securite ",
                " fiches donnees securite ",
                " fiche de securite ",
                " fiches de securite ")
            || ContainsAny(normalized, " msds ", " sds ", " fds ")
            || (ContainsAny(normalized, " safety ", " securite ")
                && ContainsAny(normalized, " data sheet ", " data sheets ", " fiche ", " fiches ", " donnees ")))
        {
            terms.Add("msds");
            terms.Add("sds");
            terms.Add("fds");
            terms.Add("safety data sheet");
            terms.Add("safety data sheets");
            terms.Add("material safety data sheet");
            terms.Add("material safety data sheets");
            terms.Add("fiche de donnees de securite");
            terms.Add("fiches de donnees de securite");
            terms.Add("fiche de securite");
            terms.Add("fiches de securite");
        }
        if (ContainsAny(normalized, " old ", " older ", " previous ", " prior ", " archived ", " archive ", " archives ", " historical ", " legacy ", " obsolete ", " ancien ", " ancienne ", " anciens ", " anciennes ", " precedente ", " precedent ", " anterieure ", " anterieur ")
            && ContainsAny(normalized, " version ", " versions ", " edition ", " editions ", " document ", " documents "))
        {
            terms.Add("old version");
            terms.Add("old versions");
            terms.Add("previous version");
            terms.Add("previous versions");
            terms.Add("prior version");
            terms.Add("archived version");
            terms.Add("archived document");
            terms.Add("historical version");
            terms.Add("ancienne version");
            terms.Add("anciennes versions");
            terms.Add("version precedente");
            terms.Add("version anterieure");
        }
        if (ContainsAny(normalized, " current ", " latest ", " newest ", " recent ", " newer ", " actuelle ", " actuel ", " courante ", " courant ", " derniere ", " nouvelle ", " nouveau ")
            && ContainsAny(normalized, " version ", " versions ", " edition ", " editions ", " document ", " documents "))
        {
            terms.Add("current version");
            terms.Add("current versions");
            terms.Add("latest version");
            terms.Add("latest versions");
            terms.Add("newest version");
            terms.Add("version actuelle");
            terms.Add("version courante");
            terms.Add("derniere version");
            terms.Add("nouvelle version");
        }
    }


    internal static string? SelectCrossLanguageTechnicalPhraseVariant(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return null;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase)).ToLowerInvariant()} ";
        if (ContainsAny(normalized, " salle propre ", " salles propres "))
            return "cleanroom";
        if (ContainsAny(normalized, " clean room ", " clean rooms ", " cleanroom ", " cleanrooms "))
            return "salle propre";
        if (ContainsAny(normalized, " fiche de donnees de securite ", " fiche donnees securite ", " fiche de securite ", " fds "))
            return "safety data sheet";
        if (ContainsAny(normalized, " material safety data sheet ", " safety data sheet ", " msds ", " sds "))
            return "fiche de donnees de securite";
        if (ContainsAny(normalized, " ancienne version ", " anciennes versions ", " version precedente ", " version anterieure "))
            return "old version";
        if (ContainsAny(normalized, " old version ", " old versions ", " previous version ", " prior version ", " archived version ", " historical version "))
            return "ancienne version";
        if (ContainsAny(normalized, " version actuelle ", " version courante ", " derniere version ", " nouvelle version "))
            return "current version";
        if (ContainsAny(normalized, " current version ", " latest version ", " newest version "))
            return "version courante";

        return null;
    }


    internal static IReadOnlyList<string> BuildSupplementalSparseLexicalFallbackTerms(string query)
    {
        var defaultTerms = BuildLexicalContentFallbackTerms(query);
        if (defaultTerms.Count == 0)
            return defaultTerms;

        var surfaceNormalized = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var normalized = FoldDiacritics(surfaceNormalized);
        if (string.IsNullOrWhiteSpace(normalized)
            || !ShouldPreferComparativeDocumentDiversity(query))
        {
            return defaultTerms;
        }

        var betweenMatch = Regex.Match(
            normalized,
            @"\b(?:entre|between)\s+(?<target>[\p{L}\p{Nd}\s\-:;,]{8,180}?)(?=\s+(?:comment|how|pourquoi|why|explique|explain)\b|[\?\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (betweenMatch.Success)
        {
            var betweenSubject = betweenMatch.Groups["target"].Value.Trim();
            var betweenTermSet = new HashSet<string>(BuildLexicalContentFallbackTerms(betweenSubject), StringComparer.Ordinal);
            AddReversedAdjacentSupplementalSubjectPhrases(betweenTermSet, ExtractLexicalQueryTokens(betweenSubject));
            var betweenTerms = betweenTermSet
                .Where(static term => !string.IsNullOrWhiteSpace(term))
                .Where(static term => !LexicalStopwords.Contains(term))
                .Where(static term => !SpecificAnchorStopwords.Contains(term))
                .Where(static term => !PrimaryAnchorStopwords.Contains(term))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (betweenTerms.Length > 0)
                return betweenTerms;
        }

        const string choicePattern =
            @"\b(?:choisir|choix|choose|select|selection|recommande|recommend|prefer|favor|favour|privilegie|priorise)\b";
        var choiceMatch = Regex.Match(
            normalized,
            choicePattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!choiceMatch.Success || choiceMatch.Index <= 0)
            return defaultTerms;

        var subjectText = normalized[..choiceMatch.Index].Trim();
        var surfaceChoiceMatch = Regex.Match(
            surfaceNormalized,
            choicePattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var subjectSurfaceText = surfaceChoiceMatch.Success && surfaceChoiceMatch.Index > 0
            ? surfaceNormalized[..surfaceChoiceMatch.Index].Trim()
            : SliceSurfaceNormalizedByFoldedLength(surfaceNormalized, choiceMatch.Index).Trim();
        var subjectTermSet = new HashSet<string>(BuildLexicalContentFallbackTerms(subjectSurfaceText), StringComparer.Ordinal);
        foreach (var term in BuildLexicalContentFallbackTerms(subjectText))
            subjectTermSet.Add(term);
        AddReversedAdjacentSupplementalSubjectPhrases(subjectTermSet, ExtractLexicalQuerySurfaceTokens(subjectSurfaceText));
        AddReversedAdjacentSupplementalSubjectPhrases(subjectTermSet, ExtractLexicalQueryTokens(subjectText));

        var subjectTerms = subjectTermSet
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Where(static term => !LexicalStopwords.Contains(term))
            .Where(static term => !SpecificAnchorStopwords.Contains(term))
            .Where(static term => !PrimaryAnchorStopwords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return subjectTerms.Length == 0 ? defaultTerms : subjectTerms;
    }


    private static void AddReversedAdjacentSupplementalSubjectPhrases(
        HashSet<string> terms,
        IReadOnlyList<string> subjectTokens)
    {
        if (subjectTokens.Count < 2)
            return;

        var cleanTokens = subjectTokens
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(FoldDiacritics(token)))
            .Where(static token => !SpecificAnchorStopwords.Contains(FoldDiacritics(token)))
            .Where(static token => !PrimaryAnchorStopwords.Contains(FoldDiacritics(token)))
            .Where(static token => !BroadDiversityAbstractAssistTokens.Contains(FoldDiacritics(token)))
            .Take(8)
            .ToArray();
        if (cleanTokens.Length < 2)
            return;

        for (var i = 0; i < cleanTokens.Length - 1; i++)
        {
            var leftVariants = BuildSupplementalSubjectPhraseTokenVariants(cleanTokens[i]);
            var rightVariants = BuildSupplementalSubjectPhraseTokenVariants(cleanTokens[i + 1]);

            foreach (var right in rightVariants)
            {
                foreach (var left in leftVariants)
                {
                    if (!string.Equals(right, left, StringComparison.Ordinal))
                        terms.Add($"{right} {left}");
                }
            }
        }
    }


    private static IReadOnlyList<string> BuildSupplementalSubjectPhraseTokenVariants(string token)
    {
        var folded = FoldDiacritics(token);
        if (string.IsNullOrWhiteSpace(folded))
            return Array.Empty<string>();

        var variants = new HashSet<string>(BuildLexicalTokenVariants(folded), StringComparer.Ordinal);
        variants.Add(folded);
        if (folded.Length >= 5
            && (folded.EndsWith("s", StringComparison.Ordinal) || folded.EndsWith("x", StringComparison.Ordinal)))
        {
            variants.Add(folded[..^1]);
        }
        foreach (var variant in variants.ToArray())
        {
            if (variant.Length >= 6 && variant.EndsWith("e", StringComparison.Ordinal))
                variants.Add(variant + "r");
        }

        return variants
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .Take(5)
            .ToArray();
    }


    private static string SliceSurfaceNormalizedByFoldedLength(string surfaceNormalized, int foldedLength)
    {
        if (string.IsNullOrWhiteSpace(surfaceNormalized) || foldedLength <= 0)
            return string.Empty;

        var consumedFoldedLength = 0;
        var surfaceLength = 0;
        foreach (var ch in surfaceNormalized)
        {
            var foldedCharLength = FoldDiacritics(ch.ToString()).Length;
            if (consumedFoldedLength + foldedCharLength > foldedLength)
                break;

            consumedFoldedLength += foldedCharLength;
            surfaceLength++;
            if (consumedFoldedLength == foldedLength)
                break;
        }

        return surfaceNormalized[..Math.Min(surfaceLength, surfaceNormalized.Length)];
    }


    internal static IReadOnlyList<string> BuildDocumentProfileLexicalTerms(string query)
    {
        var terms = new HashSet<string>(
            BuildLexicalContentFallbackTerms(query).Where(static term => !IsGenericDocumentProfileLexicalTerm(term)),
            StringComparer.Ordinal);
        foreach (var token in ExtractLexicalQuerySurfaceTokens(query))
        {
            if (!IsGenericDocumentProfileLexicalTerm(token))
                terms.Add(token);
        }
        var specificityTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in BuildDocumentProfileSpecificityTokens(query))
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                terms.Add(variant);
                specificityTerms.Add(variant);
            }
        }

        var filteredTerms = terms
            .Where(static term => term.Length >= 4 || IsShortTitleSignalToken(term))
            .ToArray();
        var phraseTerms = filteredTerms
            .Where(static term => term.Contains(' '))
            .OrderByDescending(term => specificityTerms.Contains(term))
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Take(64);
        var singleTerms = filteredTerms
            .Where(static term => !term.Contains(' '))
            .OrderByDescending(term => specificityTerms.Contains(term))
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Take(64);

        return phraseTerms
            .Concat(singleTerms)
            .Take(128)
            .ToArray();
    }


    private static bool IsGenericDocumentProfileLexicalTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return true;

        return term
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsGenericDocumentProfileSpecificityToken);
    }


    internal static IReadOnlyList<string> ExtractTitleLookupPhrases(string query)
    {
        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count > 0)
            return quotedPhrases;

        var dateAnchoredPhrases = ExtractDateAnchoredTitleLookupPhrases(query)
            .SelectMany(phrase => BuildSurfaceTitleLookupPhraseVariants(query, phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (dateAnchoredPhrases.Length > 0)
            return dateAnchoredPhrases;

        var explicitFilePhrases = ExtractExplicitFileLookupPhraseGroups(query)
            .SelectMany(static group => group)
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .SelectMany(phrase => BuildSurfaceTitleLookupPhraseVariants(query, phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var compactCodePhrases = ExtractCompactDocumentCodeLookupPhrases(query)
            .SelectMany(phrase => BuildSurfaceTitleLookupPhraseVariants(query, phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (explicitFilePhrases.Length > 0)
        {
            if (compactCodePhrases.Length > 0
                && !ContainsExplicitFilenameSurface(query)
                && !ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query))
            {
                return compactCodePhrases;
            }

            return explicitFilePhrases;
        }

        var standardReferencePhrases = ExtractStandardReferenceTitleLookupPhrases(query)
            .SelectMany(phrase => BuildSurfaceTitleLookupPhraseVariants(query, phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (standardReferencePhrases.Length > 0)
            return standardReferencePhrases;

        if (compactCodePhrases.Length > 0
            && !ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query))
        {
            return compactCodePhrases;
        }

        if (!ShouldTreatFocusedLookupAsPreciseTitle(query))
            return Array.Empty<string>();

        return ExtractFocusedLookupPhrases(query)
            .Where(static phrase => ExtractTitlePruneTokens(phrase).Length is >= 2 and <= 8)
            .SelectMany(phrase => BuildSurfaceTitleLookupPhraseVariants(query, phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    internal static IReadOnlyList<string> ExtractCompactDocumentCodeLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return Regex.Matches(
                query,
                @"\b[\p{L}\p{Nd}]{2,8}\b",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static token => token.Any(char.IsDigit)
                                   || token.Where(char.IsLetter).All(char.IsUpper))
            .Select(static token => NormalizeForLexicalSignal(token))
            .Where(IsLikelyCompactDocumentCodeOperand)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    internal static IReadOnlyList<string> ExtractCompactDocumentCodeProfileRecoveryPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return ExtractCompactDocumentCodeLookupPhrases(query)
            .Concat(ExtractBareVsComparativeOperands(query).Where(IsLikelyCompactDocumentCodeOperand))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    internal static bool SelectionsCoverCompactDocumentCodePhrase(string phrase, IReadOnlyList<RagMatch> selected)
    {
        var normalizedPhrase = NormalizeForLexicalSignal(phrase);
        if (string.IsNullOrWhiteSpace(normalizedPhrase) || selected.Count == 0)
            return false;

        var compactPhrase = normalizedPhrase.Replace(" ", string.Empty, StringComparison.Ordinal);
        return selected.Any(match =>
        {
            var titleSignal = NormalizeForLexicalSignal(string.Join(' ', new[] { match.DocName, match.DocPath }
                .Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (string.IsNullOrWhiteSpace(titleSignal))
                return false;

            return titleSignal.Contains(" " + normalizedPhrase + " ", StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(compactPhrase)
                    && titleSignal.Replace(" ", string.Empty, StringComparison.Ordinal).Contains(compactPhrase, StringComparison.Ordinal));
        });
    }


    internal static bool ShouldAllowCompactDocumentCodeEmptyRecovery(string query, bool hasExplicitFileDocumentHint)
        => !hasExplicitFileDocumentHint
           || !ContainsExplicitFilenameSurface(query);


    internal static bool ShouldAllowLexicalProfileEmptyRecovery(string query, bool hasExplicitFileDocumentHint)
        => !hasExplicitFileDocumentHint
           || !ContainsExplicitFilenameSurface(query);


    private static bool ContainsExplicitFilenameSurface(string query)
        => !string.IsNullOrWhiteSpace(query)
           && (query.Contains('`', StringComparison.Ordinal)
               || query.Contains('_', StringComparison.Ordinal)
               || SafeRegexIsMatch(
                   query,
                   @"\.[a-z0-9]{2,5}\b",
                   RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                   TimeSpan.FromMilliseconds(100)));


    internal static IReadOnlyList<string> ExtractDateAnchoredTitleLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var phrases = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (TryReadDayMonthYear(tokens, index, out var dayMonthYear))
            {
                AddDateAnchoredTitleLookupPhrases(tokens, dayMonthYear, phrases);
                continue;
            }

            if (TryReadMonthDayYear(tokens, index, out var monthDayYear))
                AddDateAnchoredTitleLookupPhrases(tokens, monthDayYear, phrases);
        }

        return phrases
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }


    internal static bool SelectionsCoverDateAnchoredDocument(string query, IReadOnlyList<RagMatch> selected)
    {
        if (selected.Count == 0)
            return false;

        var phrases = ExtractDateAnchoredTitleLookupPhrases(query);
        if (phrases.Count == 0)
            return true;

        var normalizedPhrases = phrases
            .Select(NormalizeForLexicalSignal)
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .ToArray();
        if (normalizedPhrases.Length == 0)
            return true;

        return selected.Any(match =>
        {
            var title = NormalizeForLexicalSignal(string.Join(' ', new[]
            {
                match.DocName,
                match.DocPath,
                match.SectionTitle,
                match.HeadingPath
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (string.IsNullOrWhiteSpace(title))
                return false;

            return normalizedPhrases.Any(phrase =>
            {
                if (ContainsOrderedPhraseWindowInNormalizedText(title, phrase, maxGapChars: 18))
                    return true;

                var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return tokens.Length > 0 && tokens.All(token => ContainsExactNormalizedToken(title, token));
            });
        });
    }


    private static bool TryReadDayMonthYear(
        IReadOnlyList<string> tokens,
        int index,
        [NotNullWhen(true)] out DateTitleLookup? result)
    {
        result = null;
        if (!TryParseDayToken(tokens[index], out var day))
            return false;

        if (index + 2 < tokens.Count
            && TryParseMonthToken(tokens[index + 1], out var month)
            && TryParseYearToken(tokens[index + 2], out var year))
        {
            result = new DateTitleLookup(index, index + 2, year, month, day);
            return true;
        }

        if (index + 3 < tokens.Count
            && TryParseDayToken(tokens[index + 1], out var rangeEndDay)
            && TryParseMonthToken(tokens[index + 2], out month)
            && TryParseYearToken(tokens[index + 3], out year))
        {
            result = new DateTitleLookup(index, index + 3, year, month, rangeEndDay);
            return true;
        }

        return false;
    }


    private static bool TryReadMonthDayYear(
        IReadOnlyList<string> tokens,
        int index,
        [NotNullWhen(true)] out DateTitleLookup? result)
    {
        result = null;
        if (!TryParseMonthToken(tokens[index], out var month))
            return false;

        if (index + 2 < tokens.Count
            && TryParseDayToken(tokens[index + 1], out var day)
            && TryParseYearToken(tokens[index + 2], out var year))
        {
            result = new DateTitleLookup(index, index + 2, year, month, day);
            return true;
        }

        if (index + 3 < tokens.Count
            && TryParseDayToken(tokens[index + 1], out _)
            && TryParseDayToken(tokens[index + 2], out var rangeEndDay)
            && TryParseYearToken(tokens[index + 3], out year))
        {
            result = new DateTitleLookup(index, index + 3, year, month, rangeEndDay);
            return true;
        }

        return false;
    }


    private static void AddDateAnchoredTitleLookupPhrases(
        IReadOnlyList<string> tokens,
        DateTitleLookup date,
        List<string> phrases)
    {
        var month = date.Month.ToString("00", CultureInfo.InvariantCulture);
        var day = date.Day.ToString("00", CultureInfo.InvariantCulture);
        var year = date.Year.ToString(CultureInfo.InvariantCulture);
        var canonicalDate = $"{year} {month} {day}";
        var compactDate = $"{year}{month}{day}";
        var anchors = ExtractDateTitleAnchorTokens(tokens, date.StartIndex, date.EndIndex);
        var specificAnchors = anchors
            .Where(static token => !DateTitleGenericAnchorTokens.Contains(token))
            .Where(static token => !DateTitleInstructionAnchorTokens.Contains(token))
            .TakeLast(3)
            .Reverse()
            .ToArray();
        var genericAnchors = anchors
            .Where(static token => DateTitleGenericAnchorTokens.Contains(token))
            .TakeLast(2)
            .Reverse()
            .ToArray();

        phrases.Add(canonicalDate);
        phrases.Add(compactDate);

        foreach (var specific in specificAnchors)
        {
            phrases.Add($"{specific} {canonicalDate}");
            phrases.Add($"{specific} {compactDate}");
            foreach (var generic in genericAnchors)
            {
                phrases.Add($"{specific} {generic} {canonicalDate}");
                phrases.Add($"{generic} {specific} {canonicalDate}");
            }
        }

        if (specificAnchors.Length == 0)
        {
            foreach (var generic in genericAnchors)
                phrases.Add($"{generic} {canonicalDate}");
        }
    }


    private static IReadOnlyList<string> ExtractDateTitleAnchorTokens(
        IReadOnlyList<string> tokens,
        int dateStartIndex,
        int dateEndIndex)
    {
        var anchors = new List<string>();
        var start = Math.Max(0, dateStartIndex - 8);
        for (var index = start; index < dateStartIndex; index++)
        {
            if (IsDateTitleAnchorToken(tokens[index]))
                anchors.Add(tokens[index]);
        }

        return anchors
            .Distinct(StringComparer.Ordinal)
            .TakeLast(6)
            .ToArray();
    }


    private static bool IsDateTitleAnchorToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !LexicalStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token)
           && !TitleConnectorTokens.Contains(token)
           && !TryParseMonthToken(token, out _);


    private static bool TryParseDayToken(string token, out int day)
    {
        day = 0;
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out day)
            && day is >= 1 and <= 31;
    }


    private static bool TryParseYearToken(string token, out int year)
    {
        year = 0;
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && year is >= 1900 and <= 2099;
    }


    private static bool TryParseMonthToken(string token, out int month)
        => DateTitleMonthNumbers.TryGetValue(token.TrimEnd('.'), out month);


    private sealed record DateTitleLookup(int StartIndex, int EndIndex, int Year, int Month, int Day);


    private static readonly HashSet<string> DateTitleGenericAnchorTokens = new(StringComparer.Ordinal)
    {
        "meeting",
        "meetings",
        "minute",
        "minutes",
        "reunion",
        "reunions",
        "compte",
        "rendu",
        "protokoll",
        "protokolle",
        "verbale",
        "verbali",
        "acta",
        "atas"
    };


    private static readonly HashSet<string> DateTitleInstructionAnchorTokens = new(StringComparer.Ordinal)
    {
        "besoin",
        "reponse",
        "claire",
        "clair",
        "simple",
        "maximum",
        "ligne",
        "lignes",
        "need",
        "answer",
        "response",
        "clear",
        "simple",
        "maximum",
        "lines",
        "line",
        "explique",
        "explain",
        "retrouve",
        "retrieve",
        "extract",
        "extraire"
    };


    private static readonly Dictionary<string, int> DateTitleMonthNumbers = new(StringComparer.Ordinal)
    {
        ["jan"] = 1,
        ["january"] = 1,
        ["janvier"] = 1,
        ["januar"] = 1,
        ["enero"] = 1,
        ["janeiro"] = 1,
        ["gennaio"] = 1,
        ["feb"] = 2,
        ["february"] = 2,
        ["fevrier"] = 2,
        ["februar"] = 2,
        ["febrero"] = 2,
        ["fevereiro"] = 2,
        ["febbraio"] = 2,
        ["mar"] = 3,
        ["march"] = 3,
        ["mars"] = 3,
        ["marz"] = 3,
        ["maerz"] = 3,
        ["marzo"] = 3,
        ["marco"] = 3,
        ["apr"] = 4,
        ["april"] = 4,
        ["avril"] = 4,
        ["abril"] = 4,
        ["aprile"] = 4,
        ["may"] = 5,
        ["mai"] = 5,
        ["mayo"] = 5,
        ["maio"] = 5,
        ["maggio"] = 5,
        ["jun"] = 6,
        ["june"] = 6,
        ["juin"] = 6,
        ["juni"] = 6,
        ["junio"] = 6,
        ["junho"] = 6,
        ["giugno"] = 6,
        ["jul"] = 7,
        ["july"] = 7,
        ["juillet"] = 7,
        ["juli"] = 7,
        ["julio"] = 7,
        ["julho"] = 7,
        ["luglio"] = 7,
        ["aug"] = 8,
        ["august"] = 8,
        ["aout"] = 8,
        ["agosto"] = 8,
        ["sep"] = 9,
        ["sept"] = 9,
        ["september"] = 9,
        ["septembre"] = 9,
        ["septiembre"] = 9,
        ["setembro"] = 9,
        ["settembre"] = 9,
        ["okt"] = 10,
        ["oct"] = 10,
        ["october"] = 10,
        ["octobre"] = 10,
        ["oktober"] = 10,
        ["octubre"] = 10,
        ["outubro"] = 10,
        ["ottobre"] = 10,
        ["nov"] = 11,
        ["november"] = 11,
        ["novembre"] = 11,
        ["noviembre"] = 11,
        ["novembro"] = 11,
        ["dec"] = 12,
        ["december"] = 12,
        ["decembre"] = 12,
        ["dezember"] = 12,
        ["diciembre"] = 12,
        ["dezembro"] = 12,
        ["dicembre"] = 12
    };


    internal static IReadOnlyList<string> ExtractStandardReferenceTitleLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var phrases = new List<string>();
        var shouldExpandFamilyReferences = ShouldExpandStandardReferenceFamilyLookup(query);
        foreach (var reference in ExactMatchEntryExtractor.ExtractTargetedReferences(query))
        {
            var normalized = ExactMatchEntryExtractor.NormalizeForLookup(reference);
            if (LooksLikeStandardReferenceTitleLookupPhrase(normalized))
            {
                phrases.Add(normalized);
                if (shouldExpandFamilyReferences)
                    phrases.AddRange(BuildStandardReferenceFamilyPartLookupPhrases(normalized));
            }

            foreach (var variant in ExactMatchEntryExtractor.ExpandReferenceVariants(reference))
            {
                var normalizedVariant = ExactMatchEntryExtractor.NormalizeForLookup(variant);
                if (LooksLikeStandardReferenceTitleLookupPhrase(normalizedVariant))
                {
                    phrases.Add(normalizedVariant);
                    if (shouldExpandFamilyReferences)
                        phrases.AddRange(BuildStandardReferenceFamilyPartLookupPhrases(normalizedVariant));
                }
            }
        }

        phrases.AddRange(ExtractSlashSeparatedStandardPartLookupPhrases(query));

        return phrases
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static phrase => phrase.Length)
            .ThenBy(static phrase => phrase, StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }


    private static bool ShouldExpandStandardReferenceFamilyLookup(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return ShouldPreferComparativeDocumentDiversity(query)
               || ContainsAny(
                   normalized,
                   " famille ",
                   " family ",
                   " serie ",
                   " series ",
                   " gamme ",
                   " collection ",
                   " parts ",
                   " part ",
                   " parties ",
                   " partie ",
                   " volumes ",
                   " volume ",
                   " documents lies ",
                   " related documents ",
                   " documents connexes ",
                   " normes liees ",
                   " related standards ");
    }


    private static IEnumerable<string> BuildStandardReferenceFamilyPartLookupPhrases(string phrase)
    {
        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(phrase);
        if (!TryExtractStandardReferenceFamilyRoot(normalized, out var root))
            yield break;

        for (var part = 1; part <= 8; part++)
            yield return $"{root} {part.ToString(CultureInfo.InvariantCulture)}";
    }


    private static bool TryExtractStandardReferenceFamilyRoot(string phrase, [NotNullWhen(true)] out string? root)
    {
        root = null;
        if (!LooksLikeStandardReferenceTitleLookupPhrase(phrase))
            return false;

        var tokens = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 2)
            return false;

        if (!tokens[^1].All(char.IsDigit) || tokens[^1].Length < 3)
            return false;

        if (!tokens.Take(tokens.Length - 1).Any(IsStandardReferencePrefixToken))
            return false;

        root = string.Join(' ', tokens);
        return true;
    }


    internal static bool IsStandardReferencePrefixToken(string token)
        => token is "en" or "iso" or "iec" or "astm" or "din" or "nfpa" or "api" or "ansi" or "ul" or "cen" or "tr" or "ts" or "pd" or "bs" or "nist" or "sp";


    internal static IReadOnlyList<string> ExtractDocumentStatusOperandLookupTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var terms = new List<string>();
        if (ContainsAny(normalized, " berichtigung "))
            terms.Add("berichtigung");
        if (ContainsAny(normalized, " corrigendum ", " corrigenda ", " rectificatif ", " erratum ", " errata "))
            terms.AddRange(["corrigendum", "corrigenda", "rectificatif", "erratum", "ac", "berichtigung"]);
        if (ContainsAny(normalized, " correction ", " notice de correction "))
            terms.Add("correction");
        if (Regex.IsMatch(normalized, @"\bpr\s*a1\b", RegexOptions.CultureInvariant))
            terms.AddRange(["pra1", "pr a1"]);
        if (Regex.IsMatch(normalized, @"\ba1\b", RegexOptions.CultureInvariant))
            terms.Add("a1");
        if (Regex.IsMatch(normalized, @"\bac\b", RegexOptions.CultureInvariant))
            terms.Add("ac");
        if (ContainsAny(normalized, " baseline ", " baselines "))
            terms.AddRange(["baseline", "baselines"]);
        terms.AddRange(ExtractComparativeVersionOperandLookupTerms(query));

        return terms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    internal static bool ShouldMergeDocumentStatusOperandSelectionsForComparativeVersionRoute(
        string query,
        IReadOnlyList<string> statusOperandTerms)
    {
        if (statusOperandTerms.Count == 0 || !ShouldPreferComparativeDocumentDiversity(query))
            return false;

        var comparativeOperands = ExtractComparativeVersionOperandLookupTerms(query)
            .ToHashSet(StringComparer.Ordinal);
        if (comparativeOperands.Count == 0)
            return false;

        return statusOperandTerms
            .Select(static term => NormalizeForLexicalSignal(term).Replace(" ", string.Empty, StringComparison.Ordinal))
            .Any(comparativeOperands.Contains);
    }


    internal static List<RagMatch> PrioritizeDocumentStatusOperandSelections(
        string query,
        IReadOnlyList<string> statusOperandTerms,
        IReadOnlyList<RagMatch> matches)
    {
        if (statusOperandTerms.Count == 0 || matches.Count <= 1)
            return matches.ToList();

        var topicTokens = ExtractDocumentStatusOperandQueryTopicTokens(query, statusOperandTerms);
        var preferStatusDocument = ShouldPreferDocumentStatusOperandDocument(query, statusOperandTerms);
        var prioritized = matches
            .Select(match =>
            {
                var statusTitleHits = CountDocumentStatusOperandTitleHits(statusOperandTerms, match);
                var topicHits = CountDocumentStatusOperandTopicHits(topicTokens, match);
                var boostedScore = Math.Min(
                    1.05,
                    match.Score
                    + Math.Min(0.24, topicHits * 0.08)
                    + (statusTitleHits > 0 ? 0.03 : 0.0));
                return new
                {
                    Match = match with { Score = boostedScore },
                    StatusTitleHits = statusTitleHits,
                    TopicHits = topicHits,
                    GroupKey = TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null
                };
            })
            .ToList();

        var maxTopicHits = prioritized.Max(static item => item.TopicHits);
        var bestGroupKeys = Array.Empty<string>();
        if (maxTopicHits > 0)
        {
            bestGroupKeys = prioritized
                .Where(item => item.TopicHits == maxTopicHits)
                .Select(static item => item.GroupKey)
                .Where(static groupKey => !string.IsNullOrWhiteSpace(groupKey))
                .Select(static groupKey => groupKey!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (bestGroupKeys.Length > 0)
            {
                prioritized = prioritized
                    .Where(item =>
                        item.TopicHits == maxTopicHits
                        || (TryExtractReferenceVersionGroupKey(item.Match, out var groupKey)
                            && ReferenceVersionGroupKeyIsCompatible(bestGroupKeys, groupKey)))
                    .ToList();
            }
        }

        return prioritized
            .OrderByDescending(item => bestGroupKeys.Length > 0
                                       && item.GroupKey is not null
                                       && ReferenceVersionGroupKeyIsCompatible(bestGroupKeys, item.GroupKey)
                ? 1
                : 0)
            .ThenByDescending(item => preferStatusDocument && item.StatusTitleHits > 0 ? 1 : 0)
            .ThenByDescending(static item => item.TopicHits)
            .ThenByDescending(static item => item.StatusTitleHits)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .ToList();
    }


    private static bool ShouldPreferDocumentStatusOperandDocument(
        string query,
        IReadOnlyList<string> statusOperandTerms)
    {
        if (string.IsNullOrWhiteSpace(query) || statusOperandTerms.Count == 0)
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(
                normalized,
                " document principal ",
                " norme principale ",
                " standard principal ",
                " norme complete ",
                " standard complet ",
                " main document ",
                " main standard ",
                " complete standard "))
        {
            return false;
        }

        return statusOperandTerms
            .Select(static term => NormalizeForLexicalSignal(term).Replace(" ", string.Empty, StringComparison.Ordinal))
            .Any(static term => term is "corrigendum"
                or "corrigenda"
                or "rectificatif"
                or "erratum"
                or "berichtigung"
                or "correction");
    }


    internal static void ReattachDocumentStatusOperandCompanions(
        string query,
        IReadOnlyList<string> statusOperandTerms,
        IReadOnlyList<RagMatch>? candidatePool,
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        int topK)
    {
        if (topK <= 0
            || selected.Count == 0
            || selected.Count >= topK
            || statusOperandTerms.Count == 0
            || candidatePool is not { Count: > 0 })
        {
            return;
        }

        var selectedGroupKeys = selected
            .Select(static match => TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null)
            .Where(static groupKey => !string.IsNullOrWhiteSpace(groupKey))
            .Select(static groupKey => groupKey!)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (selectedGroupKeys.Length == 0)
            return;

        var selectedStatusGroupKeys = selected
            .Where(match => CountDocumentStatusOperandTitleHits(statusOperandTerms, match) > 0)
            .Select(static match => TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null)
            .Where(static groupKey => !string.IsNullOrWhiteSpace(groupKey))
            .Select(static groupKey => groupKey!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var preferStatusDocument = ShouldPreferDocumentStatusOperandDocument(query, statusOperandTerms);
        var companions = candidatePool
            .Where(match => CountDocumentStatusOperandTitleHits(statusOperandTerms, match) > 0)
            .Select(match => new
            {
                Match = match,
                GroupKey = TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null,
                StatusTitleHits = CountDocumentStatusOperandTitleHits(statusOperandTerms, match)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.GroupKey))
            .Where(item => ReferenceVersionGroupKeyIsCompatible(selectedGroupKeys, item.GroupKey!))
            .Where(item => !selectedStatusGroupKeys.Contains(item.GroupKey!))
            .Where(item => selected.All(existing => !string.Equals(
                NormalizeRagDocPath(existing.DocPath),
                NormalizeRagDocPath(item.Match.DocPath),
                StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static item => item.StatusTitleHits)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .ToList();
        if (companions.Count == 0)
            return;

        foreach (var companion in companions)
        {
            if (selected.Count >= topK)
                break;

            var dedupKey = BuildMatchDedupKey(companion);
            if (!selectedKeys.Add(dedupKey))
                continue;
            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, companion)))
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            if (preferStatusDocument)
                selected.Insert(0, companion);
            else
                selected.Add(companion);

            if (TryExtractReferenceVersionGroupKey(companion, out var groupKey))
                selectedStatusGroupKeys.Add(groupKey);
        }
    }


    internal static IReadOnlyList<string> ExtractDocumentStatusOperandQueryTopicTokens(
        string query,
        IReadOnlyList<string> statusOperandTerms)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var statusTokens = statusOperandTerms
            .SelectMany(static term => NormalizeForLexicalSignal(term)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        var normalized = NormalizeForLexicalSignal(query);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(token => !statusTokens.Contains(token))
            .Where(static token => token is not "document"
                and not "documents"
                and not "fichier"
                and not "fichiers"
                and not "source"
                and not "sources"
                and not "norme"
                and not "normes"
                and not "standard"
                and not "standards"
                and not "principal"
                and not "principale"
                and not "complete"
                and not "role"
                and not "annee"
                and not "statut"
                and not "correction"
                and not "amendement"
                and not "version"
                and not "versions"
                and not "dois"
                and not "doit"
                and not "peux"
                and not "comment"
                and not "repondre"
                and not "signaler"
                and not "existe"
                and not "appliquer"
                and not "citer"
                and not "regarder"
                and not "expliquer"
                and not "remplace"
                and not "contenu")
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }


    private static int CountDocumentStatusOperandTopicHits(IReadOnlyList<string> topicTokens, RagMatch match)
    {
        if (topicTokens.Count == 0)
            return 0;

        var titleText = " " + NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            match.DocName,
            match.DocPath,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))) + " ";
        if (string.IsNullOrWhiteSpace(titleText))
            return 0;

        var candidateTokens = titleText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return topicTokens.Count(token => ContainsDocumentStatusTopicToken(candidateTokens, token));
    }


    private static bool ContainsDocumentStatusTopicToken(IReadOnlySet<string> candidateTokens, string token)
    {
        if (candidateTokens.Count == 0 || string.IsNullOrWhiteSpace(token))
            return false;
        if (candidateTokens.Contains(token))
            return true;

        if (token.Length >= 4)
        {
            if (candidateTokens.Contains(token + "s"))
                return true;
            if (token.EndsWith("s", StringComparison.Ordinal) && candidateTokens.Contains(token[..^1]))
                return true;
        }

        return false;
    }


    internal static int CountDocumentStatusOperandTitleHits(IReadOnlyList<string> statusOperandTerms, RagMatch match)
    {
        var titleText = " " + NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            match.DocName,
            match.DocPath,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))) + " ";
        if (string.IsNullOrWhiteSpace(titleText))
            return 0;

        var count = 0;
        foreach (var term in statusOperandTerms)
        {
            var normalizedTerm = NormalizeForLexicalSignal(term);
            if (string.IsNullOrWhiteSpace(normalizedTerm))
                continue;
            var compactTerm = normalizedTerm.Replace(" ", string.Empty, StringComparison.Ordinal);
            var allowCompactTermMatch = compactTerm.Length > 2 || compactTerm.Any(char.IsDigit);
            if (titleText.Contains(" " + normalizedTerm + " ", StringComparison.Ordinal)
                || (allowCompactTermMatch && titleText.Contains(compactTerm, StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }


    internal static bool ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
        string query,
        string? docId,
        string? docPath)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        if (!string.IsNullOrWhiteSpace(docId) || !string.IsNullOrWhiteSpace(docPath))
            return false;
        if (ExtractStandardReferenceTitleLookupPhrases(query).Count > 0
            || ExtractExplicitFileLookupPhrases(query).Count > 0
            || ExtractDateAnchoredTitleLookupPhrases(query).Count > 0
            || ExtractQuotedLookupPhrases(query).Count > 0)
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(
                normalized,
                " ce document ",
                " ce fichier ",
                " ce pdf ",
                " ce texte ",
                " cette source ",
                " cette version ",
                " this document ",
                " this file ",
                " this pdf ",
                " this source ",
                " this version "))
        {
            return !HasSpecificUnscopedDocumentTopicAnchor(normalized);
        }

        var statusTerms = ExtractDocumentStatusOperandLookupTerms(query);
        if (statusTerms.Count == 0)
            return false;
        if (ExtractVersionYears(query).Count > 0)
            return false;
        if (HasSpecificUnscopedStatusTopicAnchor(normalized))
            return false;

        return statusTerms.All(static term => term is "ac" or "a1");
    }


    private static bool HasSpecificUnscopedDocumentTopicAnchor(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var meaningfulTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 5)
            .Where(static token => token is not "document"
                and not "documents"
                and not "fichier"
                and not "fichiers"
                and not "source"
                and not "sources"
                and not "version"
                and not "versions"
                and not "ancienne"
                and not "ancien"
                and not "absente"
                and not "absent"
                and not "dossier"
                and not "comparer"
                and not "compare"
                and not "comparison"
                and not "comparaison"
                and not "quelles"
                and not "quelle"
                and not "quels"
                and not "client"
                and not "comprendre"
                and not "comment"
                and not "quelque"
                and not "chose"
                and not "dessus"
                and not "autour"
                and not "citees"
                and not "citer"
                and not "which"
                and not "what"
                and not "where"
                and not "about"
                and not "understand"
                and not "missing")
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();

        return meaningfulTokens >= 2;
    }


    private static bool HasSpecificUnscopedStatusTopicAnchor(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var meaningfulTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 6)
            .Where(static token => token is not "document"
                and not "documents"
                and not "fichier"
                and not "fichiers"
                and not "source"
                and not "sources"
                and not "standard"
                and not "standards"
                and not "principale"
                and not "principal"
                and not "complete"
                and not "corrigendum"
                and not "corrigenda"
                and not "correction"
                and not "amendement"
                and not "amendments"
                and not "version"
                and not "versions"
                and not "ancienne"
                and not "ancien"
                and not "dossier"
                and not "appliquer"
                and not "citer"
                and not "regarder"
                and not "expliquer"
                and not "remplace"
                and not "automatiquement"
                and not "contenu")
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();

        return meaningfulTokens >= 2;
    }


    internal static bool SelectionsCoverStandardReferencePhrases(
        IReadOnlyList<string> standardReferencePhrases,
        IReadOnlyList<RagMatch> selected)
    {
        if (standardReferencePhrases.Count == 0 || selected.Count == 0)
            return false;

        return standardReferencePhrases.All(phrase =>
        {
            var normalizedPhrase = NormalizeForLexicalSignal(phrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                return true;

            return selected.Any(match =>
                StandardReferencePhraseMatchesText(normalizedPhrase, match.DocName)
                || StandardReferencePhraseMatchesText(normalizedPhrase, match.DocPath)
                || StandardReferencePhraseMatchesText(normalizedPhrase, match.HeadingPath)
                || StandardReferencePhraseMatchesText(normalizedPhrase, match.SectionTitle));
        });
    }


    internal static void PruneUnrequestedStandardReferenceSelections(string query, List<RagMatch> selected)
    {
        if (string.IsNullOrWhiteSpace(query) || selected.Count < 2)
            return;

        var standardReferencePhrases = ExtractStandardReferenceTitleLookupPhrases(query);
        if (standardReferencePhrases.Count == 0)
            return;
        var discriminatingPhrases = standardReferencePhrases
            .Where(IsDiscriminatingStandardReferencePrunePhrase)
            .ToArray();
        var pruningPhrases = discriminatingPhrases.Length > 0
            ? discriminatingPhrases
            : standardReferencePhrases.ToArray();
        if (pruningPhrases.Length == 0)
            pruningPhrases = standardReferencePhrases.ToArray();

        if (!SelectionsCoverStandardReferencePhrases(pruningPhrases, selected))
            return;

        selected.RemoveAll(match => IsUnrequestedStandardReferenceSelection(pruningPhrases, match));
    }


    private static bool IsDiscriminatingStandardReferencePrunePhrase(string phrase)
    {
        var tokens = NormalizeForLexicalSignal(phrase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return false;

        var numericTokens = tokens
            .Where(static token => token.Any(char.IsDigit))
            .ToArray();
        if (numericTokens.Length >= 2)
            return true;
        if (numericTokens.Length == 1 && numericTokens[0].All(char.IsDigit) && numericTokens[0].Length >= 4)
            return true;

        return false;
    }


    private static bool IsUnrequestedStandardReferenceSelection(
        IReadOnlyList<string> standardReferencePhrases,
        RagMatch match)
    {
        if (standardReferencePhrases.Any(phrase =>
        {
            var normalizedPhrase = NormalizeForLexicalSignal(phrase);
            return !string.IsNullOrWhiteSpace(normalizedPhrase)
                && (StandardReferencePhraseMatchesText(normalizedPhrase, match.DocName)
                    || StandardReferencePhraseMatchesText(normalizedPhrase, match.DocPath)
                    || StandardReferencePhraseMatchesText(normalizedPhrase, match.HeadingPath)
                    || StandardReferencePhraseMatchesText(normalizedPhrase, match.SectionTitle));
        }))
        {
            return false;
        }

        return LooksLikeStandardReferenceDocumentSurface($"{match.DocPath} {match.DocName}");
    }


    private static bool LooksLikeStandardReferenceDocumentSurface(string? candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText))
            return false;

        var normalized = NormalizeForLexicalSignal(candidateText);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (IsStandardReferencePrefixToken(tokens[i])
                && tokens.Skip(i + 1).Take(2).Any(static token => token.Any(char.IsDigit)))
            {
                return true;
            }

            if (i + 1 < tokens.Length
                && tokens[i].All(char.IsDigit)
                && tokens[i].Length >= 2
                && tokens[i + 1].Any(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }


    internal static bool StandardReferencePhraseMatchesText(string normalizedPhrase, string? candidateText)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhrase) || string.IsNullOrWhiteSpace(candidateText))
            return false;

        var normalizedCandidate = " " + NormalizeForLexicalSignal(candidateText) + " ";
        var compactPhrase = normalizedPhrase.Replace(" ", string.Empty, StringComparison.Ordinal);
        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalizedCandidate.Contains(" " + normalizedPhrase + " ", StringComparison.Ordinal)
            || compactCandidate.Contains(compactPhrase, StringComparison.Ordinal)
            || StandardReferencePhraseMatchesTokenSequence(normalizedPhrase, normalizedCandidate);
    }


    private static bool StandardReferencePhraseMatchesTokenSequence(string normalizedPhrase, string normalizedCandidate)
    {
        var phraseTokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (phraseTokens.Length < 2)
            return false;

        var candidateTokens = normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateIndex = 0;
        var started = false;
        foreach (var phraseToken in phraseTokens)
        {
            var matched = false;
            while (candidateIndex < candidateTokens.Length)
            {
                var candidateToken = candidateTokens[candidateIndex++];
                if (candidateToken == phraseToken)
                {
                    matched = true;
                    started = true;
                    break;
                }

                if (started && !IsStandardReferencePrefixToken(candidateToken))
                    return false;
            }

            if (!matched)
                return false;
        }

        return true;
    }


    internal static IReadOnlyList<string> ExtractSlashSeparatedStandardPartLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(query).ToLowerInvariant();
        var phrases = new List<string>();
        foreach (Match match in Regex.Matches(
                     normalized,
                     @"\b(?<prefix>en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp)\s+(?<number>\d{2,6})\s*[- ]\s*(?<first>\d{1,2})(?<rest>(?:\s*/\s*\d{1,2})+)\b",
                     RegexOptions.CultureInvariant))
        {
            var prefix = match.Groups["prefix"].Value;
            var number = match.Groups["number"].Value;
            phrases.Add($"{prefix} {number} {match.Groups["first"].Value}");
            foreach (Match partMatch in Regex.Matches(match.Groups["rest"].Value, @"\d{1,2}", RegexOptions.CultureInvariant))
                phrases.Add($"{prefix} {number} {partMatch.Value}");
        }

        return phrases
            .Where(static phrase => LooksLikeStandardReferenceTitleLookupPhrase(phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }


    internal static bool LooksLikeStandardReferenceTitleLookupPhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        return SafeRegexIsMatch(
            phrase,
            @"^(?:(?:en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp)\s+\d|\d{2,6}\s+\d{1,4}[a-z]\d*)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }


    private static string NormalizeExplicitFileDocumentHintPhrase(string rawHint)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(rawHint)).ToLowerInvariant();
        return TrimExplicitFileHintLeadingNoise(normalized);
    }


    internal static IReadOnlyList<string> ExtractExplicitFileLookupPhrases(string query)
        => ExtractExplicitFileLookupPhraseGroups(query)
            .SelectMany(static group => group)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();


    internal static IReadOnlyList<IReadOnlyList<string>> ExtractExplicitFileLookupPhraseGroups(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<IReadOnlyList<string>>();

        var groups = new List<IReadOnlyList<string>>();
        foreach (var rawHint in ExtractExplicitFileDocumentHintPhrases(query))
        {
            foreach (var phrase in SplitExplicitFileHintSegments(NormalizeExplicitFileDocumentHintPhrase(rawHint)))
            {
                var titlePruneTokenCount = ExtractTitlePruneTokens(phrase).Length;
                var documentSignalTokenCount = phrase
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Count(IsDocumentHintSignalToken);
                if ((titlePruneTokenCount is < 1 or > 24)
                    && documentSignalTokenCount == 0)
                {
                    continue;
                }

                var phrases = new List<string>();
                phrases.Add(phrase);
                var withoutExtension = TrimTrailingFileExtensionToken(phrase);
                if (!string.IsNullOrWhiteSpace(withoutExtension)
                    && !string.Equals(withoutExtension, phrase, StringComparison.Ordinal))
                {
                    phrases.Add(withoutExtension);
                }

                groups.Add(phrases
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
            }
        }

        foreach (var rawHint in ExtractStrongExtensionlessDocumentHintPhrases(query))
        {
            var phrase = NormalizeStrongDocumentNameHintPhrase(rawHint);
            if (string.IsNullOrWhiteSpace(phrase))
                continue;
            if (ExtractTitlePruneTokens(phrase).Length is < 1 or > 24)
                continue;

            groups.Add(new[] { phrase });
        }

        return groups
            .Where(static group => group.Count > 0)
            .GroupBy(static group => string.Join('\u001f', group), StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(12)
            .ToArray();
    }


    private static IEnumerable<string> BuildSurfaceTitleLookupPhraseVariants(string query, string foldedPhrase)
    {
        var surfacePhrase = TryExtractSurfacePhraseForFoldedLookup(query, foldedPhrase);
        if (!string.IsNullOrWhiteSpace(surfacePhrase))
            yield return surfacePhrase;

        yield return foldedPhrase;
    }


    private static string? TryExtractSurfacePhraseForFoldedLookup(string query, string foldedPhrase)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(foldedPhrase))
            return null;

        var surfaceTokens = ExactMatchEntryExtractor.NormalizeForLookup(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var targetTokens = foldedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (surfaceTokens.Length == 0 || targetTokens.Length == 0 || targetTokens.Length > surfaceTokens.Length)
            return null;

        var foldedSurfaceTokens = surfaceTokens
            .Select(static token => FoldDiacritics(token).ToLowerInvariant())
            .ToArray();

        for (var index = 0; index <= surfaceTokens.Length - targetTokens.Length; index++)
        {
            var matches = true;
            for (var offset = 0; offset < targetTokens.Length; offset++)
            {
                if (!string.Equals(
                        foldedSurfaceTokens[index + offset],
                        targetTokens[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return string.Join(' ', surfaceTokens.Skip(index).Take(targetTokens.Length));
        }

        return null;
    }


    internal static IReadOnlyList<string> BuildTitleLookupSqlTokens(IReadOnlyList<string> phrases)
    {
        if (phrases.Count == 0)
            return Array.Empty<string>();

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in phrases)
        {
            foreach (var normalizedPhrase in new[]
                     {
                         ExactMatchEntryExtractor.NormalizeForLookup(phrase),
                         FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase))
                     })
            {
                foreach (var token in normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!IsQuotedLookupSignalToken(token))
                        continue;

                    tokens.Add(token);
                    foreach (var variant in BuildLexicalTokenVariants(token))
                        tokens.Add(variant);
                }
            }
        }

        return tokens
            .Where(static token => token.Length >= 4 || token.All(char.IsDigit))
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }


    internal static bool ShouldTreatFocusedLookupAsPreciseTitle(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        if (LooksLikeShortAudienceConstrainedGenericFragment(query))
            return false;
        if (ExtractQuotedLookupPhrases(query).Count > 0)
            return true;

        var phrases = ExtractFocusedLookupPhrases(query);
        if (phrases.Count == 0)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (ContainsOperationalSettingLookupIntent(normalized)
            && phrases.Any(static phrase => ExtractTitlePruneTokens(phrase).Length is >= 2 and <= 8))
        {
            return true;
        }

        if (ContainsDocumentOverviewIntent(query)
            || ShouldPreferComparativeDocumentDiversity(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsEnumerativeLookupIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsGuidanceOrAdviceSelectionIntent(normalized)
            || IsRecommendationSelectionQuery(normalized))
        {
            return false;
        }

        return phrases.Any(static phrase => ExtractTitlePruneTokens(phrase).Length is >= 2 and <= 8);
    }


    internal static IReadOnlyList<string> ExtractQuotedLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var matches = System.Text.RegularExpressions.Regex.Matches(
            query,
            "(?:\u00c2?\u00ab|\u201c|\u00e2\u20ac\u0153|\"|`)(?<phrase>.{3,80}?)(?:\u00c2?\u00bb|\u201d|\u00e2\u20ac\u009d|\"|`)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (matches.Count == 0)
            return Array.Empty<string>();

        return matches
            .Select(static match => NormalizeQuotedLookupPhrase(match.Groups["phrase"].Value))
            .Where(static phrase => phrase.Length >= 4)
            .Where(static phrase => phrase.Count(static ch => ch == ' ') >= 1 || IsSingleTokenQuotedLookupPhrase(phrase))
            .Where(static phrase => !LooksLikeQuotedResponseShapePhrase(phrase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static bool LooksLikeQuotedResponseShapePhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase)).ToLowerInvariant() + " ";
        if (ContainsAny(
                normalized,
                " ancienne source ",
                " nouvelle source ",
                " source ancienne ",
                " source nouvelle ",
                " old source ",
                " new source ",
                " older source ",
                " newer source ")
            && ContainsAny(normalized, " changement ", " changements ", " change ", " changes ", " impact ", " impacts "))
        {
            return true;
        }

        if (LooksLikeQuotedGenericUserQuestionPhrase(normalized))
            return true;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 3 or > 12)
            return false;

        var shapeTokens = tokens.Count(static token => QuotedResponseShapeTokens.Contains(token));
        return shapeTokens >= 3 && shapeTokens >= Math.Max(3, (int)Math.Ceiling(tokens.Length * 0.75));
    }


    private static bool LooksLikeQuotedGenericUserQuestionPhrase(string normalizedPhrase)
        => ContainsAny(
               normalizedPhrase,
               " quel ",
               " quelle ",
               " quels ",
               " quelles ",
               " lequel ",
               " laquelle ",
               " which ",
               " what ",
               " cual ",
               " qual ",
               " quale ",
               " welche ",
               " welcher ")
           && ContainsAny(
               normalizedPhrase,
               " version ",
               " versions ",
               " source ",
               " sources ",
               " document ",
               " documents ",
               " fichier ",
               " fichiers ",
               " correct ",
               " correcte ",
               " vrai ",
               " vraie ",
               " bonne ",
               " utiliser ",
               " use ");


    internal static readonly HashSet<string> QuotedResponseShapeTokens = new(StringComparer.Ordinal)
    {
        "ancien",
        "ancienne",
        "anciens",
        "anciennes",
        "old",
        "older",
        "nouveau",
        "nouvelle",
        "nouveaux",
        "nouvelles",
        "new",
        "newer",
        "source",
        "sources",
        "changement",
        "changements",
        "change",
        "changes",
        "impact",
        "impacts",
        "colonne",
        "colonnes",
        "column",
        "columns",
        "tableau",
        "table",
        "version",
        "versions",
        "date",
        "dates",
        "page",
        "pages",
        "preuve",
        "preuves",
        "evidence",
        "citation",
        "citations",
        "difference",
        "differences",
        "perimetre",
        "scope",
        "recouvrement",
        "overlap",
        "priorite",
        "priority",
        "statut",
        "status"
    };


    private static bool TryExtractDelimitedUserDemandTopic(string query, out string topic)
    {
        topic = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        query = RepairReplacementAccentMarkers(query);
        foreach (Match match in Regex.Matches(
                     query,
                     """(?:\u00c2?\u00ab|\u201c|\u00e2\u20ac\u0153|"|`)(?<phrase>.{3,180}?)(?:\u00c2?\u00bb|\u201d|\u00e2\u20ac\u009d|"|`)""",
                     RegexOptions.CultureInvariant))
        {
            var candidate = NormalizeQuotedLookupPhrase(match.Groups["phrase"].Value);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var before = query[..match.Index];
            var after = query[(match.Index + match.Length)..];
            var context = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(before + " " + after)).ToLowerInvariant()} ";
            if (!Regex.IsMatch(
                    context,
                    @"\b(?:demande|demandes|demander|question|questions|requete|requetes|dit|disant|orienter|orientation|utilisateur|client|asked|asks|ask|request|requests|says|said|user|customer|customers|prepare|preparer|response|answer|answers|responder|rispondere|antwort|antworten)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            topic = candidate;
            return true;
        }

        return false;
    }


    internal static string NormalizeQuotedLookupPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return string.Empty;

        var cleaned = RepairReplacementAccentMarkers(phrase)
            .Replace('\u00c2', ' ')
            .Replace("\u00e2\u20ac\u0153", " ")
            .Replace("\u00e2\u20ac\u009d", " ")
            .Replace("\u00e2\u20ac\u2122", "'");

        return ExactMatchEntryExtractor.NormalizeForLookup(cleaned);
    }


    private static string RepairReplacementAccentMarkers(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('?', StringComparison.Ordinal))
            return value;

        var repaired = Regex.Replace(value, @"(?<=\p{L})\?(?=\p{L})", "e", RegexOptions.CultureInvariant);
        repaired = Regex.Replace(repaired, @"(?<!\p{L})\?(?=\p{L})", "e", RegexOptions.CultureInvariant);
        return repaired;
    }


    private static bool IsSingleTokenQuotedLookupPhrase(string phrase)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase));
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(' '))
            return false;

        return IsQuotedLookupSignalToken(normalized)
            && !SpecificAnchorStopwords.Contains(normalized)
            && !PrimaryAnchorStopwords.Contains(normalized);
    }


    internal static double ComputeQuotedLookupCandidateScore(IReadOnlyList<string> quotedPhrases, string? candidateText)
    {
        if (quotedPhrases.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty);
        var bestScore = 0.0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(quotedPhrase));
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var tokens = normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsQuotedLookupSignalToken)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var compactPhrase = normalizedPhrase.Replace(" ", string.Empty);
            var hasExactPhrase = normalizedCandidate.Contains(normalizedPhrase, StringComparison.Ordinal)
                || (compactPhrase.Length >= 8 && compactCandidate.Contains(compactPhrase, StringComparison.Ordinal));

            var matched = tokens.Count(token => ContainsQuotedLookupToken(normalizedCandidate, compactCandidate, token));
            if (!hasExactPhrase && matched < ComputeRequiredQuotedLookupMatches(tokens.Length))
                continue;
            if (!hasExactPhrase
                && tokens.Length >= 4
                && !ContainsQuotedLookupToken(normalizedCandidate, compactCandidate, tokens[0]))
            {
                continue;
            }
            if (!hasExactPhrase
                && tokens.Length >= 5
                && !ContainsQuotedLookupToken(normalizedCandidate, compactCandidate, tokens[^1]))
            {
                continue;
            }

            var coverage = tokens.Length == 0
                ? hasExactPhrase ? 1.0 : 0.0
                : matched / (double)tokens.Length;
            var score = (hasExactPhrase ? 12.0 : 0.0)
                + (coverage * 8.0)
                + Math.Min(3, matched) * 0.5;
            score += ComputeQuotedPhrasePlacementBonus(quotedPhrase, candidateText);

            if (tokens.Length >= 4 && matched >= tokens.Length - 1)
                score += 2.0;

            bestScore = Math.Max(bestScore, score);
        }

        return bestScore;
    }


    private static int ComputeRequiredQuotedLookupMatches(int tokenCount)
        => tokenCount switch
        {
            <= 0 => 0,
            1 => 1,
            2 => 2,
            3 => 2,
            _ => Math.Max(3, (int)Math.Ceiling(tokenCount * 0.75))
        };


    internal static double ComputeQuotedPhrasePlacementBonus(string quotedPhrase, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(quotedPhrase) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var foldedCandidate = FoldDiacritics(candidateText);
        var foldedPhrase = FoldDiacritics(quotedPhrase);
        if (foldedPhrase.Length < 4)
            return 0.0;

        var best = 0.0;
        var index = foldedCandidate.IndexOf(foldedPhrase, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var slice = foldedCandidate.Substring(index, Math.Min(foldedPhrase.Length, foldedCandidate.Length - index));
            var letters = slice.Where(char.IsLetter).ToArray();
            var uppercaseRatio = letters.Length == 0
                ? 0.0
                : letters.Count(char.IsUpper) / (double)letters.Length;
            var prefix = foldedCandidate.Substring(Math.Max(0, index - 32), Math.Min(32, index));
            var placement = uppercaseRatio >= 0.70 ? 7.0 : 0.0;
            if (LooksLikeMeasuredListLead(prefix))
                placement -= 4.0;

            best = Math.Max(best, placement);
            index = foldedCandidate.IndexOf(foldedPhrase, index + foldedPhrase.Length, StringComparison.OrdinalIgnoreCase);
        }

        return best;
    }


    private static bool LooksLikeMeasuredListLead(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            prefix,
            @"(?:\d+[.,]?\d*|\b(?:g|kg|mg|ml|cl|l|litre|litres|oz|lb|unit|units|unite|unites|item|items)\b)\s*(?:de|d')?\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }


    internal static bool IsQuotedLookupSignalToken(string token)
        => (token.Length >= 4 || token.Any(char.IsDigit))
           && !LexicalStopwords.Contains(token);


    private static bool ContainsQuotedLookupToken(string normalizedCandidate, string compactCandidate, string token)
    {
        if (token.All(char.IsDigit))
        {
            return normalizedCandidate
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(token, StringComparer.Ordinal);
        }

        foreach (var variant in BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
        {
            if (normalizedCandidate.Contains(variant, StringComparison.Ordinal)
                || (variant.Length >= 4 && compactCandidate.Contains(variant, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }


    internal static IReadOnlyList<string> ExtractLexicalQuerySurfaceTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(FoldDiacritics(token)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static void AddAdjacentAlphaNumericPhraseTerms(HashSet<string> terms, string query)
    {
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)));
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, ExactMatchEntryExtractor.NormalizeForLookup(query));
    }


    private static void AddAdjacentAlphaNumericPhraseTermsFromNormalized(HashSet<string> terms, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return;

        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAlphaNumericPhraseToken)
            .Take(24)
            .ToArray();
        if (tokens.Length < 2)
            return;

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var phrase = $"{tokens[i]} {tokens[i + 1]}";
            if (IsAlphaNumericPhraseTerm(phrase))
            {
                terms.Add(phrase);
                var compactPhrase = $"{tokens[i]}{tokens[i + 1]}";
                if (IsReferenceLikeLookupTerm(compactPhrase))
                    terms.Add(compactPhrase);
            }
        }
    }


    private static bool IsAlphaNumericPhraseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 24)
            return false;

        var hasLetter = token.Any(char.IsLetter);
        var hasDigit = token.Any(char.IsDigit);
        if (!hasLetter && !hasDigit)
            return false;
        if (!token.All(static ch => char.IsLetterOrDigit(ch)))
            return false;

        var folded = FoldDiacritics(token);
        if (hasLetter && !hasDigit)
            return token.Length >= 3 && !LexicalStopwords.Contains(folded);
        if (hasDigit && !hasLetter)
            return token.Length <= 8;

        return token.Length >= 2 && !LexicalStopwords.Contains(folded);
    }


    private static bool IsAlphaNumericPhraseTerm(string phrase)
    {
        if (phrase.Length is < 4 or > 40)
            return false;

        var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        var hasDigit = parts.Any(static part => part.Any(char.IsDigit));
        var hasLetter = parts.Any(static part => part.Any(char.IsLetter));
        if (!hasDigit || !hasLetter)
            return false;

        return parts.Any(static part => part.Any(char.IsLetter) && part.Length >= 3);
    }


    internal static IReadOnlyList<string> BuildLexicalTokenVariants(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Array.Empty<string>();

        var variants = new HashSet<string>(StringComparer.Ordinal) { token };

        AddSimpleGenderNumberVariants(variants, token);
        AddConstraintSemanticVariants(variants, token);

        if (token.Length >= 6 && token.EndsWith("es", StringComparison.Ordinal))
            variants.Add(token[..^1]);
        if (token.Length >= 6 && (token.EndsWith("s", StringComparison.Ordinal) || token.EndsWith("x", StringComparison.Ordinal)))
            variants.Add(token[..^1]);
        if (token.Length >= 7 && token.EndsWith("eux", StringComparison.Ordinal))
            variants.Add(token[..^3] + "e");
        if (token.Length >= 7 && token.EndsWith("te", StringComparison.Ordinal))
            variants.Add(token[..^1]);
        return variants
            .Where(static variant => variant.Length >= 4 || IsShortTitleSignalToken(variant))
            .Where(static variant => variant.Any(char.IsLetter))
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static void AddConstraintSemanticVariants(HashSet<string> variants, string token)
    {
        switch (token)
        {
            case "materiel":
            case "materiels":
            case "material":
            case "materials":
                variants.Add("materielle");
                variants.Add("materielles");
                variants.Add("equipement");
                variants.Add("equipements");
                variants.Add("outil");
                variants.Add("outils");
                variants.Add("equipment");
                variants.Add("tool");
                variants.Add("tools");
                variants.Add("device");
                variants.Add("devices");
                variants.Add("accessory");
                variants.Add("accessories");
                break;

            case "risque":
            case "risques":
            case "risk":
            case "risks":
            case "ratage":
            case "ratages":
            case "failure":
            case "failures":
                variants.Add("difficulte");
                variants.Add("difficultes");
                variants.Add("difficulty");
                variants.Add("difficulties");
                variants.Add("danger");
                variants.Add("dangers");
                variants.Add("erreur");
                variants.Add("erreurs");
                variants.Add("error");
                variants.Add("errors");
                break;

            case "temps":
            case "time":
            case "duration":
            case "duree":
                variants.Add("minutes");
                variants.Add("minute");
                variants.Add("horaire");
                variants.Add("schedule");
                break;

            case "propriete":
            case "proprietes":
            case "caracteristique":
            case "caracteristiques":
            case "characteristic":
            case "characteristics":
                variants.Add("propriete");
                variants.Add("proprietes");
                variants.Add("property");
                variants.Add("properties");
                variants.Add("characteristic");
                variants.Add("characteristics");
                break;

            case "electrique":
            case "electriques":
            case "electric":
            case "electrical":
            case "electricity":
                variants.Add("electrique");
                variants.Add("electriques");
                variants.Add("electric");
                variants.Add("electrical");
                variants.Add("electricity");
                variants.Add("power");
                variants.Add("wiring");
                variants.Add("dielectric");
                break;

            case "thermique":
            case "thermiques":
            case "thermal":
                variants.Add("thermique");
                variants.Add("thermiques");
                variants.Add("thermal");
                variants.Add("temperature");
                variants.Add("temperatures");
                variants.Add("melt point");
                variants.Add("melting point");
                break;

            case "machine":
            case "machines":
            case "machinery":
                variants.Add("machine");
                variants.Add("machines");
                variants.Add("machinery");
                variants.Add("equipment");
                variants.Add("industrial");
                break;

            case "baseline":
            case "baselines":
                variants.Add("baseline");
                variants.Add("baselines");
                break;

            case "objectif":
            case "objectifs":
            case "objective":
            case "objectives":
            case "goal":
            case "goals":
                variants.Add("objectif");
                variants.Add("objectifs");
                variants.Add("objective");
                variants.Add("objectives");
                variants.Add("goal");
                variants.Add("goals");
                break;

            case "pays":
            case "country":
            case "countries":
                variants.Add("pays");
                variants.Add("country");
                variants.Add("countries");
                break;

            case "atteint":
            case "atteinte":
            case "atteints":
            case "atteintes":
            case "atteindre":
            case "achieve":
            case "achieved":
            case "achieves":
            case "reached":
                variants.Add("atteint");
                variants.Add("atteindre");
                variants.Add("achieve");
                variants.Add("achieved");
                variants.Add("reached");
                break;

            case "etiquette":
            case "etiquettes":
            case "label":
            case "labels":
            case "sign":
            case "signs":
                variants.Add("etiquette");
                variants.Add("etiquettes");
                variants.Add("label");
                variants.Add("labels");
                variants.Add("sign");
                variants.Add("signs");
                break;

            case "securite":
            case "safety":
            case "security":
                variants.Add("securite");
                variants.Add("safety");
                variants.Add("security");
                variants.Add("msds");
                variants.Add("sds");
                variants.Add("fds");
                variants.Add("safety data sheet");
                variants.Add("material safety data sheet");
                variants.Add("fiche de donnees de securite");
                break;

            case "msds":
            case "sds":
            case "fds":
                variants.Add("msds");
                variants.Add("sds");
                variants.Add("fds");
                variants.Add("safety data sheet");
                variants.Add("material safety data sheet");
                variants.Add("fiche de donnees de securite");
                variants.Add("fiche de securite");
                break;

            case "certificat":
            case "certificats":
            case "certificate":
            case "certificates":
            case "certification":
            case "certifications":
            case "certifie":
            case "certifiee":
            case "certifies":
            case "certifiees":
            case "certified":
                variants.Add("certificat");
                variants.Add("certificats");
                variants.Add("certificate");
                variants.Add("certificates");
                variants.Add("certification");
                variants.Add("certifications");
                variants.Add("certified");
                variants.Add("attestation");
                break;

            case "norme":
            case "normes":
            case "standard":
            case "standards":
                variants.Add("norme");
                variants.Add("normes");
                variants.Add("standard");
                variants.Add("standards");
                break;

            case "fiche":
            case "fiches":
            case "datasheet":
            case "datasheets":
            case "specification":
            case "specifications":
                variants.Add("fiche technique");
                variants.Add("fiches techniques");
                variants.Add("technical sheet");
                variants.Add("technical sheets");
                variants.Add("technical data");
                variants.Add("datasheet");
                variants.Add("datasheets");
                variants.Add("data sheet");
                variants.Add("data sheets");
                variants.Add("specification");
                variants.Add("specifications");
                variants.Add("spec sheet");
                variants.Add("spec sheets");
                variants.Add("safety data sheet");
                variants.Add("material safety data sheet");
                variants.Add("fiche de donnees de securite");
                break;

            case "technique":
            case "techniques":
            case "technical":
                variants.Add("technique");
                variants.Add("techniques");
                variants.Add("technical");
                variants.Add("technical data");
                variants.Add("datasheet");
                variants.Add("data sheet");
                variants.Add("specification");
                break;

            case "ancienne":
            case "ancien":
            case "anciennes":
            case "anciens":
            case "precedente":
            case "precedent":
            case "anterieure":
            case "anterieur":
            case "old":
            case "older":
            case "previous":
            case "prior":
            case "archived":
            case "archive":
            case "archives":
            case "historical":
            case "legacy":
            case "obsolete":
                variants.Add("ancienne version");
                variants.Add("anciennes versions");
                variants.Add("version precedente");
                variants.Add("version anterieure");
                variants.Add("old version");
                variants.Add("old versions");
                variants.Add("previous version");
                variants.Add("previous versions");
                variants.Add("archived version");
                variants.Add("archived document");
                variants.Add("historical version");
                break;

            case "actuelle":
            case "actuel":
            case "courante":
            case "courant":
            case "derniere":
            case "nouvelle":
            case "nouveau":
            case "current":
            case "latest":
            case "newest":
            case "recent":
            case "newer":
                variants.Add("version actuelle");
                variants.Add("version courante");
                variants.Add("derniere version");
                variants.Add("nouvelle version");
                variants.Add("current version");
                variants.Add("current versions");
                variants.Add("latest version");
                variants.Add("latest versions");
                variants.Add("newest version");
                break;

            case "manuel":
            case "manuels":
            case "manual":
            case "manuals":
            case "emploi":
            case "emplois":
            case "instruction":
            case "instructions":
                variants.Add("manuel");
                variants.Add("manuels");
                variants.Add("manual");
                variants.Add("manuals");
                variants.Add("mode emploi");
                variants.Add("mode d emploi");
                variants.Add("operating instruction");
                variants.Add("operating instructions");
                variants.Add("instructions for use");
                break;

            case "mode":
            case "modes":
                variants.Add("mode emploi");
                variants.Add("mode d emploi");
                variants.Add("manual");
                variants.Add("manuals");
                variants.Add("operating instructions");
                break;

            case "modele":
            case "model":
            case "models":
            case "reference":
            case "references":
                variants.Add("modele");
                variants.Add("model");
                variants.Add("models");
                variants.Add("reference");
                variants.Add("references");
                variants.Add("product");
                variants.Add("produit");
                break;

            case "dommage":
            case "dommages":
            case "damage":
            case "damages":
            case "damaged":
                variants.Add("dommage");
                variants.Add("dommages");
                variants.Add("damage");
                variants.Add("damages");
                variants.Add("damaged");
                break;

            case "accidentel":
            case "accidentelle":
            case "accidentels":
            case "accidentelles":
            case "accidental":
            case "accident":
            case "accidents":
                variants.Add("accidentel");
                variants.Add("accidentelle");
                variants.Add("accidentels");
                variants.Add("accidentelles");
                variants.Add("accidental");
                variants.Add("accident");
                variants.Add("accidents");
                break;

            case "couverture":
            case "couvert":
            case "couverte":
            case "couverts":
            case "couvertes":
            case "cover":
            case "covers":
            case "covered":
            case "coverage":
                variants.Add("couverture");
                variants.Add("couvert");
                variants.Add("couverte");
                variants.Add("couverts");
                variants.Add("couvertes");
                variants.Add("cover");
                variants.Add("covers");
                variants.Add("covered");
                variants.Add("coverage");
                break;

            case "garantie":
            case "garanties":
            case "warranty":
            case "warranties":
                variants.Add("garantie");
                variants.Add("garanties");
                variants.Add("cover");
                variants.Add("coverage");
                variants.Add("warranty");
                variants.Add("warranties");
                break;

            case "sinistre":
            case "sinistres":
            case "claim":
            case "claims":
                variants.Add("sinistre");
                variants.Add("sinistres");
                variants.Add("claim");
                variants.Add("claims");
                variants.Add("notification");
                variants.Add("incident");
                variants.Add("incidents");
                break;

            case "exclusion":
            case "exclusions":
            case "excluded":
            case "exclude":
            case "excludes":
                variants.Add("exclusion");
                variants.Add("exclusions");
                variants.Add("excluded");
                variants.Add("exclude");
                variants.Add("excludes");
                variants.Add("not covered");
                break;

            case "juridique":
            case "juridiques":
            case "legal":
            case "law":
                variants.Add("juridique");
                variants.Add("juridiques");
                variants.Add("legal");
                variants.Add("law");
                break;

            case "conseil":
            case "conseils":
            case "advice":
            case "advisory":
                variants.Add("conseil");
                variants.Add("conseils");
                variants.Add("advice");
                variants.Add("advisory");
                break;

            case "bien":
            case "biens":
            case "property":
            case "properties":
            case "belonging":
            case "belongings":
            case "asset":
            case "assets":
            case "item":
            case "items":
                variants.Add("bien");
                variants.Add("biens");
                variants.Add("property");
                variants.Add("properties");
                variants.Add("belonging");
                variants.Add("belongings");
                variants.Add("asset");
                variants.Add("assets");
                variants.Add("item");
                variants.Add("items");
                break;

            case "assure":
            case "assures":
            case "assuree":
            case "assurees":
            case "insured":
            case "insurance":
                variants.Add("assure");
                variants.Add("assures");
                variants.Add("assuree");
                variants.Add("assurees");
                variants.Add("insured");
                variants.Add("insurance");
                variants.Add("covered");
                break;

            case "preuve":
            case "preuves":
            case "proof":
            case "proofs":
            case "evidence":
                variants.Add("preuve");
                variants.Add("preuves");
                variants.Add("proof");
                variants.Add("proofs");
                variants.Add("evidence");
                break;

            case "fournir":
            case "fourni":
            case "fournie":
            case "fournis":
            case "fournies":
            case "provide":
            case "provided":
            case "providing":
            case "submit":
            case "submitted":
            case "required":
                variants.Add("fournir");
                variants.Add("fourni");
                variants.Add("fournie");
                variants.Add("fournis");
                variants.Add("fournies");
                variants.Add("provide");
                variants.Add("provided");
                variants.Add("providing");
                variants.Add("submit");
                variants.Add("submitted");
                variants.Add("required");
                break;

            case "exigence":
            case "exigences":
            case "requirement":
            case "requirements":
                variants.Add("exigence");
                variants.Add("exigences");
                variants.Add("requirement");
                variants.Add("requirements");
                variants.Add("shall");
                variants.Add("must");
                break;

            case "laboratoire":
            case "laboratoires":
            case "laboratory":
            case "laboratories":
                variants.Add("laboratoire");
                variants.Add("laboratoires");
                variants.Add("laboratory");
                variants.Add("laboratories");
                variants.Add("lab");
                variants.Add("labs");
                break;

            case "sorbonne":
            case "sorbonnes":
            case "fume":
            case "hood":
            case "hoods":
            case "cupboard":
            case "cupboards":
                variants.Add("sorbonne");
                variants.Add("sorbonnes");
                variants.Add("fume");
                variants.Add("fume hood");
                variants.Add("fume hoods");
                variants.Add("fume cupboard");
                variants.Add("fume cupboards");
                variants.Add("cupboard");
                variants.Add("cupboards");
                break;

            case "fournisseur":
            case "fournisseurs":
            case "supplier":
            case "suppliers":
            case "vendor":
            case "vendors":
                variants.Add("fournisseur");
                variants.Add("fournisseurs");
                variants.Add("supplier");
                variants.Add("suppliers");
                variants.Add("vendor");
                variants.Add("vendors");
                break;

            case "conduite":
            case "conduct":
                variants.Add("conduite");
                variants.Add("conduct");
                variants.Add("code");
                variants.Add("supplier code");
                variants.Add("supplier conduct");
                variants.Add("code conduct");
                variants.Add("code of conduct");
                variants.Add("ethical standards");
                variants.Add("responsible business");
                variants.Add("business practices");
                variants.Add("behaviour");
                variants.Add("behavior");
                break;

            case "ethique":
            case "ethiques":
            case "ethical":
            case "ethics":
                variants.Add("ethique");
                variants.Add("ethiques");
                variants.Add("ethical");
                variants.Add("ethics");
                variants.Add("code");
                variants.Add("conduct");
                variants.Add("supplier code");
                variants.Add("code conduct");
                variants.Add("code of conduct");
                variants.Add("ethical standards");
                variants.Add("responsible business");
                variants.Add("business practices");
                break;

            case "integrite":
            case "integrity":
                variants.Add("integrite");
                variants.Add("integrity");
                variants.Add("compliance");
                variants.Add("ethics");
                variants.Add("policy");
                break;

            case "politique":
            case "politiques":
            case "policy":
            case "policies":
                variants.Add("politique");
                variants.Add("politiques");
                variants.Add("policy");
                variants.Add("policies");
                break;

            case "banque":
            case "bank":
                variants.Add("banque");
                variants.Add("bank");
                variants.Add("world");
                variants.Add("mondiale");
                break;

            case "mondiale":
            case "world":
                variants.Add("mondiale");
                variants.Add("world");
                variants.Add("bank");
                variants.Add("banque");
                break;

            case "approche":
            case "approaches":
            case "approach":
                variants.Add("approche");
                variants.Add("approach");
                variants.Add("approaches");
                variants.Add("method");
                variants.Add("methods");
                break;

            case "concurrence":
            case "competitive":
            case "competition":
                variants.Add("concurrence");
                variants.Add("competitive");
                variants.Add("competition");
                variants.Add("procurement");
                variants.Add("selection");
                break;

            case "evaluation":
            case "evaluations":
            case "evaluate":
            case "assessment":
            case "assessments":
                variants.Add("evaluation");
                variants.Add("evaluations");
                variants.Add("evaluate");
                variants.Add("assessment");
                variants.Add("assessments");
                variants.Add("criteria");
                break;

            case "condition":
            case "conditions":
            case "term":
            case "terms":
                variants.Add("condition");
                variants.Add("conditions");
                variants.Add("term");
                variants.Add("terms");
                variants.Add("contract");
                variants.Add("contractual");
                break;

            case "paiement":
            case "paiements":
            case "payment":
            case "payments":
                variants.Add("paiement");
                variants.Add("paiements");
                variants.Add("payment");
                variants.Add("payments");
                variants.Add("term");
                variants.Add("terms");
                variants.Add("condition");
                variants.Add("conditions");
                break;

            case "resiliation":
            case "resiliations":
            case "resilier":
            case "termination":
            case "terminate":
            case "terminated":
            case "cancellation":
            case "cancel":
            case "canceled":
            case "cancelled":
                variants.Add("resiliation");
                variants.Add("resiliations");
                variants.Add("termination");
                variants.Add("terminate");
                variants.Add("terminated");
                variants.Add("cancellation");
                variants.Add("cancel");
                variants.Add("canceled");
                variants.Add("cancelled");
                break;

            case "livraison":
            case "livraisons":
            case "delivery":
            case "deliveries":
            case "deliver":
            case "reception":
            case "receipt":
            case "receipts":
            case "receiving":
            case "acceptance":
                variants.Add("livraison");
                variants.Add("livraisons");
                variants.Add("delivery");
                variants.Add("deliveries");
                variants.Add("deliver");
                variants.Add("reception");
                variants.Add("receipt");
                variants.Add("receipts");
                variants.Add("receiving");
                variants.Add("acceptance");
                break;

            case "environnement":
            case "environnemental":
            case "environnementale":
            case "environment":
            case "environmental":
            case "esg":
            case "sustainability":
            case "sustainable":
            case "climate":
            case "carbon":
            case "emission":
            case "emissions":
                variants.Add("environnement");
                variants.Add("environment");
                variants.Add("environmental");
                variants.Add("esg");
                variants.Add("sustainability");
                variants.Add("sustainable");
                variants.Add("climate");
                variants.Add("carbon");
                variants.Add("emission");
                variants.Add("emissions");
                break;

            case "traitant":
            case "traitants":
            case "subcontract":
            case "subcontracts":
            case "subcontractor":
            case "subcontractors":
            case "subcontracting":
                variants.Add("traitant");
                variants.Add("traitants");
                variants.Add("subcontract");
                variants.Add("subcontracts");
                variants.Add("subcontractor");
                variants.Add("subcontractors");
                variants.Add("subcontracting");
                break;

            case "rabais":
            case "remise":
            case "remises":
            case "discount":
            case "discounts":
            case "rebate":
            case "rebates":
                variants.Add("rabais");
                variants.Add("remise");
                variants.Add("remises");
                variants.Add("discount");
                variants.Add("discounts");
                variants.Add("rebate");
                variants.Add("rebates");
                break;

            case "conformite":
            case "conforme":
            case "conformes":
            case "conformity":
            case "compliance":
            case "compliant":
                variants.Add("conformite");
                variants.Add("conformity");
                variants.Add("compliance");
                variants.Add("integrity");
                variants.Add("ethics");
                variants.Add("requirement");
                variants.Add("requirements");
                break;

            case "transparent":
            case "transparente":
            case "transparents":
            case "transparentes":
            case "transparence":
            case "transparency":
                variants.Add("transparent");
                variants.Add("transparence");
                variants.Add("transparency");
                variants.Add("disclosure");
                variants.Add("integrity");
                break;

            case "verifier":
            case "verifie":
            case "verifies":
            case "verify":
            case "check":
            case "checks":
                variants.Add("verifier");
                variants.Add("verify");
                variants.Add("check");
                variants.Add("checks");
                variants.Add("review");
                variants.Add("assess");
                variants.Add("assessment");
                break;

            case "regle":
            case "regles":
            case "rule":
            case "rules":
                variants.Add("regle");
                variants.Add("regles");
                variants.Add("rule");
                variants.Add("rules");
                variants.Add("requirement");
                variants.Add("requirements");
                variants.Add("policy");
                variants.Add("policies");
                break;

            case "exception":
            case "exceptions":
            case "waiver":
            case "waivers":
            case "exemption":
            case "exemptions":
                variants.Add("exception");
                variants.Add("exceptions");
                variants.Add("waiver");
                variants.Add("waivers");
                variants.Add("exemption");
                variants.Add("exemptions");
                variants.Add("deviation");
                variants.Add("deviations");
                break;

            case "propose":
            case "proposer":
            case "offre":
            case "offres":
            case "offer":
            case "offers":
            case "proposal":
            case "proposals":
                variants.Add("propose");
                variants.Add("offer");
                variants.Add("offers");
                variants.Add("proposal");
                variants.Add("proposals");
                variants.Add("bid");
                variants.Add("bids");
                break;

            case "marche":
            case "cheap":
            case "cheaper":
            case "low":
            case "cost":
            case "costs":
            case "price":
            case "prices":
            case "priced":
            case "cher":
            case "chere":
            case "chers":
            case "cheres":
                variants.Add("marche");
                variants.Add("cheap");
                variants.Add("cheaper");
                variants.Add("low");
                variants.Add("cost");
                variants.Add("costs");
                variants.Add("price");
                variants.Add("prices");
                variants.Add("priced");
                variants.Add("expensive");
                variants.Add("pricing");
                break;

            case "documente":
            case "documentee":
            case "documentes":
            case "documentees":
            case "documented":
            case "documentation":
                variants.Add("documente");
                variants.Add("documented");
                variants.Add("documentation");
                variants.Add("evidence");
                variants.Add("records");
                variants.Add("supporting");
                break;

            case "responsabilite":
            case "responsabilites":
            case "responsibility":
            case "responsibilities":
            case "liability":
            case "liabilities":
                variants.Add("responsabilite");
                variants.Add("responsabilites");
                variants.Add("responsibility");
                variants.Add("responsibilities");
                variants.Add("liability");
                variants.Add("liabilities");
                variants.Add("obligation");
                variants.Add("obligations");
                break;

            case "contractuel":
            case "contractuelle":
            case "contractuelles":
            case "contractual":
            case "contract":
            case "contracts":
                variants.Add("contractuel");
                variants.Add("contractuelle");
                variants.Add("contractuelles");
                variants.Add("contractual");
                variants.Add("contract");
                variants.Add("contracts");
                variants.Add("conditions");
                variants.Add("obligations");
                break;

            case "obligation":
            case "obligations":
                variants.Add("obligation");
                variants.Add("obligations");
                variants.Add("requirements");
                variants.Add("duties");
                variants.Add("must");
                break;

            case "conflit":
            case "conflits":
            case "conflict":
            case "conflicts":
                variants.Add("conflit");
                variants.Add("conflits");
                variants.Add("conflict");
                variants.Add("conflicts");
                variants.Add("interest");
                variants.Add("interests");
                break;

            case "interet":
            case "interets":
            case "interest":
            case "interests":
                variants.Add("interet");
                variants.Add("interets");
                variants.Add("interest");
                variants.Add("interests");
                variants.Add("conflict");
                variants.Add("conflicts");
                break;

            case "confidentialite":
            case "confidentiality":
            case "privacy":
                variants.Add("confidentialite");
                variants.Add("confidentiality");
                variants.Add("privacy");
                variants.Add("data");
                variants.Add("personal");
                break;

            case "donnees":
            case "data":
                variants.Add("donnees");
                variants.Add("data");
                variants.Add("personal");
                variants.Add("privacy");
                variants.Add("protection");
                break;

            case "personnelles":
            case "personal":
                variants.Add("personnelles");
                variants.Add("personal");
                variants.Add("data");
                variants.Add("privacy");
                break;

            case "couleur":
            case "couleurs":
            case "color":
            case "colors":
            case "colour":
            case "colours":
                variants.Add("couleur");
                variants.Add("couleurs");
                variants.Add("color");
                variants.Add("colors");
                variants.Add("colour");
                variants.Add("colours");
                break;

            case "poudre":
            case "poudres":
            case "poudrage":
            case "powder":
            case "powders":
            case "powdered":
            case "powdering":
                variants.Add("poudre");
                variants.Add("poudrage");
                variants.Add("powder");
                variants.Add("powders");
                variants.Add("powdered");
                variants.Add("powder coating");
                variants.Add("powder coatings");
                variants.Add("powder-coated");
                variants.Add("coating powder");
                break;

            case "revetement":
            case "revetements":
            case "coating":
            case "coatings":
            case "coated":
                variants.Add("revetement");
                variants.Add("revetements");
                variants.Add("coating");
                variants.Add("coatings");
                variants.Add("coated");
                variants.Add("surface treatment");
                variants.Add("finish");
                variants.Add("finishing");
                break;

            case "pictogramme":
            case "pictogrammes":
            case "pictogram":
            case "pictograms":
            case "symbole":
            case "symboles":
            case "symbol":
            case "symbols":
                variants.Add("pictogramme");
                variants.Add("pictogrammes");
                variants.Add("pictogram");
                variants.Add("pictograms");
                variants.Add("symbole");
                variants.Add("symboles");
                variants.Add("symbol");
                variants.Add("symbols");
                variants.Add("sign");
                variants.Add("signs");
                break;

            case "raccord":
            case "raccords":
            case "raccorder":
            case "connexion":
            case "connexions":
            case "connection":
            case "connections":
            case "connector":
            case "connectors":
            case "coupling":
            case "couplings":
            case "fitting":
            case "fittings":
                variants.Add("raccord");
                variants.Add("raccords");
                variants.Add("connexion");
                variants.Add("connexions");
                variants.Add("connection");
                variants.Add("connections");
                variants.Add("connector");
                variants.Add("connectors");
                variants.Add("coupling");
                variants.Add("couplings");
                variants.Add("fitting");
                variants.Add("fittings");
                variants.Add("pipe");
                variants.Add("pipes");
                break;

            case "hygienique":
            case "hygieniques":
            case "hygienic":
            case "hygiene":
            case "sanitaire":
            case "sanitaires":
            case "sanitary":
                variants.Add("hygienique");
                variants.Add("hygieniques");
                variants.Add("hygienic");
                variants.Add("hygiene");
                variants.Add("sanitaire");
                variants.Add("sanitaires");
                variants.Add("sanitary");
                variants.Add("clean");
                break;

            case "alimentaire":
            case "alimentaires":
            case "alimentary":
            case "food":
            case "foods":
                variants.Add("alimentaire");
                variants.Add("alimentaires");
                variants.Add("food");
                variants.Add("foods");
                variants.Add("industry");
                variants.Add("industries");
                variants.Add("application");
                variants.Add("applications");
                break;

            case "enfant":
            case "enfants":
            case "child":
            case "children":
            case "kid":
            case "kids":
                variants.Add("enfant");
                variants.Add("enfants");
                variants.Add("child");
                variants.Add("children");
                variants.Add("kid");
                variants.Add("kids");
                break;

            case "apprenti":
            case "apprentis":
            case "apprentice":
            case "apprentices":
            case "debutant":
            case "debutants":
            case "beginner":
            case "beginners":
                variants.Add("apprenti");
                variants.Add("apprentis");
                variants.Add("apprentice");
                variants.Add("apprentices");
                variants.Add("debutant");
                variants.Add("debutants");
                variants.Add("beginner");
                variants.Add("beginners");
                break;
        }
    }


    private static void AddSimpleGenderNumberVariants(HashSet<string> variants, string token)
    {
        if (token.Length < 6)
            return;

        if (token.EndsWith("aises", StringComparison.Ordinal)
            || token.EndsWith("antes", StringComparison.Ordinal)
            || token.EndsWith("entes", StringComparison.Ordinal)
            || token.EndsWith("ales", StringComparison.Ordinal))
        {
            variants.Add(token[..^2]);
        }
        else if (token.EndsWith("aise", StringComparison.Ordinal)
                 || token.EndsWith("ante", StringComparison.Ordinal)
                 || token.EndsWith("ente", StringComparison.Ordinal)
                 || token.EndsWith("ale", StringComparison.Ordinal))
        {
            variants.Add(token[..^1]);
        }

        if (token.EndsWith("euses", StringComparison.Ordinal))
            variants.Add(token[..^5] + "eux");
        else if (token.EndsWith("euse", StringComparison.Ordinal))
            variants.Add(token[..^4] + "eux");

        if (token.EndsWith("ives", StringComparison.Ordinal))
            variants.Add(token[..^4] + "if");
        else if (token.EndsWith("ive", StringComparison.Ordinal))
            variants.Add(token[..^3] + "if");

        if (token.EndsWith("elles", StringComparison.Ordinal))
            variants.Add(token[..^5] + "el");
        else if (token.EndsWith("elle", StringComparison.Ordinal))
            variants.Add(token[..^4] + "el");
    }


    private static void AddAdjacentPhraseTerms(HashSet<string> terms, IReadOnlyList<string> canonicalTokens)
    {
        if (canonicalTokens.Count < 2)
            return;

        var clean = canonicalTokens
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(8)
            .ToArray();

        for (var n = 2; n <= 3; n++)
        {
            if (clean.Length < n)
                continue;

            for (var i = 0; i <= clean.Length - n; i++)
            {
                AddPhraseWithSurfaceVariants(terms, clean.Skip(i).Take(n).ToArray());
            }
        }
    }


    private static void AddPhraseWithSurfaceVariants(HashSet<string> terms, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return;

        var tokenVariants = tokens
            .Select(BuildPhraseSurfaceVariants)
            .Where(static variants => variants.Count > 0)
            .ToArray();
        if (tokenVariants.Length != tokens.Count)
            return;

        var phrases = new List<string> { string.Empty };
        foreach (var variants in tokenVariants)
        {
            var next = new List<string>();
            foreach (var prefix in phrases)
            {
                foreach (var variant in variants)
                {
                    var phrase = string.IsNullOrWhiteSpace(prefix)
                        ? variant
                        : $"{prefix} {variant}";
                    next.Add(phrase);
                    if (next.Count >= 32)
                        break;
                }

                if (next.Count >= 32)
                    break;
            }

            phrases = next;
        }

        foreach (var phrase in phrases)
        {
            if (phrase.Length is >= 9 and <= 80)
                terms.Add(phrase);
        }
    }


    private static IReadOnlyList<string> BuildPhraseSurfaceVariants(string token)
        => BuildLexicalTokenVariants(token)
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .OrderBy(variant => string.Equals(variant, token, StringComparison.Ordinal) ? 0 : ContainsNonAscii(variant) ? 1 : 2)
            .ThenBy(static variant => variant.Length)
            .ThenBy(static variant => variant, StringComparer.Ordinal)
            .Take(5)
            .ToArray();


    internal static bool ContainsNonAscii(string value)
        => value.Any(static ch => ch > 127);


    internal static string ExpandRetrievalQuery(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        _ = category;

        var repairedQuery = RepairReplacementAccentMarkers(query);
        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(repairedQuery);
        var normalized = FoldDiacritics(lookup);
        if (string.IsNullOrWhiteSpace(normalized))
            return repairedQuery.Trim();

        var trimmed = repairedQuery.Trim();
        var expandedTerms = BuildQueryExpansionTerms(normalized);
        var additions = new List<string>();
        if (!string.Equals(normalized, lookup, StringComparison.Ordinal))
            additions.Add(normalized);
        foreach (var term in expandedTerms)
        {
            if (!lookup.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
                additions.Add(term);
        }

        return additions.Count == 0
            ? trimmed
            : $"{trimmed} {string.Join(' ', additions.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }


    internal static string ResolvePrimaryRetrievalQuery(string query, string? category)
    {
        if (TryExtractDelimitedUserDemandTopic(query, out var delimitedTopic))
            return ExpandRetrievalQuery(delimitedTopic, category);

        if (ShouldTreatQuotedLookupAsTopic(query))
        {
            var quotedTopicPhrase = ExtractFocusedLookupPhrases(query).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(quotedTopicPhrase))
                return ExpandRetrievalQuery(quotedTopicPhrase, category);
        }

        if (ContainsDocumentOverviewIntent(query))
            return ExpandRetrievalQuery(query, category);
        if (LooksLikeShortAudienceConstrainedGenericFragment(query))
            return query.Trim();

        var focusedLookupPhrase = ExtractFocusedLookupPhrases(query).FirstOrDefault();
        return ExpandRetrievalQuery(
            string.IsNullOrWhiteSpace(focusedLookupPhrase) ? query : focusedLookupPhrase,
            category);
    }


    internal static string ResolveLexicalRetrievalSurface(string query, string retrievalQuery)
        => TryExtractDelimitedUserDemandTopic(query, out _)
            && !string.IsNullOrWhiteSpace(retrievalQuery)
            || ShouldUseExpandedLexicalRetrievalSurface(query, retrievalQuery)
            ? retrievalQuery
            : query;


    private static bool ShouldUseExpandedLexicalRetrievalSurface(string query, string retrievalQuery)
    {
        if (string.IsNullOrWhiteSpace(query)
            || string.IsNullOrWhiteSpace(retrievalQuery)
            || string.Equals(query.Trim(), retrievalQuery.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsDocumentOverviewIntent(query)
            || HasExplicitFileLookupIntent(query)
            || ExtractQuotedLookupPhrases(query).Count > 0)
        {
            return false;
        }

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(RepairReplacementAccentMarkers(query)));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokenCount = ExtractLexicalQueryTokens(normalized)
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (tokenCount is < 1 or > 4)
            return false;

        return BuildQueryExpansionTerms(normalized).Count > 0;
    }


    internal static IReadOnlyList<string> BuildQueryExpansionTerms(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return Array.Empty<string>();

        var tokens = ExtractLexicalQueryTokens(normalizedQuery)
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length == 0)
            return Array.Empty<string>();

        var variants = tokens
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .Where(static variant => !string.IsNullOrWhiteSpace(variant))
            .Where(IsCorpusAgnosticQueryExpansionVariant)
            .Where(variant => !normalizedQuery.Contains(variant, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();

        var expanded = tokens
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .ToHashSet(StringComparer.Ordinal);
        if (expanded.Any(static token => token is "accidental" or "accident")
            && expanded.Any(static token => token is "damage" or "damages"))
        {
            variants.Insert(0, "accidental damage");
        }
        if (expanded.Any(static token => token is "legal")
            && expanded.Any(static token => token is "advice" or "conseil"))
        {
            variants.Insert(0, "legal advice");
        }
        if (expanded.Any(static token => token is "cover" or "coverage" or "covered")
            && ContainsAny($" {normalizedQuery} ", " pas ", " non ", " not "))
        {
            variants.Insert(0, "not covered");
        }
        if (expanded.Any(static token => token is "insured" or "insurance" or "covered")
            && expanded.Any(static token => token is "property" or "properties" or "belonging" or "belongings" or "asset" or "assets" or "item" or "items"))
        {
            variants.Insert(0, "personal belongings");
            variants.Insert(0, "covered property");
            variants.Insert(0, "insured property");
        }
        if (expanded.Any(static token => token is "proof" or "proofs" or "evidence")
            && expanded.Any(static token => token is "provide" or "provided" or "providing" or "submit" or "submitted" or "required"))
        {
            variants.Insert(0, "documents required");
            variants.Insert(0, "supporting documents");
            variants.Insert(0, "supporting evidence");
            variants.Insert(0, "provide evidence");
        }
        if (expanded.Any(static token => token is "property" or "properties" or "propriete" or "proprietes" or "characteristic" or "characteristics")
            && expanded.Any(static token => token is "electric" or "electrical" or "electricity" or "dielectric"))
        {
            variants.Insert(0, "dielectric properties");
            variants.Insert(0, "electrical properties");
        }
        if (expanded.Any(static token => token is "property" or "properties" or "propriete" or "proprietes" or "characteristic" or "characteristics")
            && expanded.Any(static token => token is "thermal" or "temperature" or "temperatures" or "melt point" or "melting point"))
        {
            variants.Insert(0, "temperature properties");
            variants.Insert(0, "thermal properties");
        }

        return variants
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }


    internal static string BuildLexicalProfileEmptyRecoveryQuery(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var expanded = ExpandRetrievalQuery(query, category);
        if (string.IsNullOrWhiteSpace(expanded)
            || string.Equals(expanded.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(expanded));
        var tokens = ExtractLexicalQueryTokens(normalized)
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
        return tokens.Length == 0
            ? string.Empty
            : string.Join(' ', tokens);
    }


    private static bool IsCorpusAgnosticQueryExpansionVariant(string variant)
        => variant switch
        {
            "damage" or "damages" or "damaged" or "accidental" or "accident" or "accidents" => true,
            "cover" or "covers" or "covered" or "coverage" => true,
            "warranty" or "warranties" => true,
            "claim" or "claims" or "notification" or "incident" or "incidents" => true,
            "excluded" or "exclude" or "excludes" or "not covered" => true,
            "legal" or "law" or "advice" or "advisory" => true,
            "insured" or "insurance" or "belonging" or "belongings" => true,
            "proof" or "proofs" or "evidence" or "provide" or "provided" or "providing" or "submit" or "submitted" or "required" => true,
            "objective" or "objectives" or "goal" or "goals" => true,
            "country" or "countries" or "achieve" or "achieved" or "reached" => true,
            "powder" or "powders" or "powdered" or "powdering" or "coating" or "coatings" or "coated" => true,
            "powder coating" or "powder coatings" or "powder-coated" or "coating powder" => true,
            "surface treatment" or "finish" or "finishing" => true,
            "propriete" or "proprietes" or "property" or "properties" or "characteristic" or "characteristics" => true,
            "electric" or "electrical" or "electricity" or "dielectric" => true,
            "thermal" or "temperature" or "temperatures" or "melt point" or "melting point" => true,
            _ => false
        };


    internal static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                _ = ch switch
                {
                    'œ' => sb.Append("oe"),
                    'Œ' => sb.Append("OE"),
                    'æ' => sb.Append("ae"),
                    'Æ' => sb.Append("AE"),
                    'ß' => sb.Append("ss"),
                    'ø' => sb.Append('o'),
                    'Ø' => sb.Append('O'),
                    'ł' => sb.Append('l'),
                    'Ł' => sb.Append('L'),
                    'đ' => sb.Append('d'),
                    'Đ' => sb.Append('D'),
                    _ => sb.Append(ch)
                };
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

}
