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

    private static void AppendControlExcerpt(StringBuilder sb, string language, IReadOnlyList<RagHitSummary> hits)
    {
        var hit = hits.FirstOrDefault();
        if (hit is null)
            return;

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Passage citÃƒÂ©", "Cited passage", "Pasaje citado", "Passagem citada", "Zitierte Stelle", "Passaggio citato"));
        sb.Append(" : ");
        sb.Append(docLabel);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 360)));
    }

    private static void AppendExactItemControlExcerpt(StringBuilder sb, string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var hit = hits.FirstOrDefault();
        if (hit is null)
            return;
        if (!ShouldIncludeRawSourceExcerptForAnswerLanguage(language, hit))
            return;

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Passage citÃƒÂ©", "Cited passage", "Pasaje citado", "Passagem citada", "Zitierte Stelle", "Passaggio citato"));
        sb.Append(" : ");
        sb.Append(docLabel);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit), maxLength: 360)));
    }

    private static bool ShouldIncludeRawSourceExcerptForAnswerLanguage(string language, RagHitSummary hit)
    {
        var answerLanguage = NormalizeLanguageCode(language);
        var docLanguage = NormalizeDocumentLanguageTag(hit.DocLanguage ?? hit.ProfileLanguage);
        if (string.Equals(docLanguage, "und", StringComparison.Ordinal))
            return string.Equals(answerLanguage, "fr", StringComparison.Ordinal);

        var docPrimary = docLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.Equals(answerLanguage, docPrimary, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceBackedLabel(string language, string fr, string en, string es, string pt, string de, string it)
        => NormalizeLanguageCode(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };

    private static string SourceBackedNotVisibleLabel(string language)
        => SourceBackedLabel(
            language,
            "non visible dans les extraits retenus",
            "not visible in the retained excerpts",
            "no visible en los extractos retenidos",
            "nao visivel nos excertos retidos",
            "in den behaltenen Auszuegen nicht sichtbar",
            "non visibile negli estratti mantenuti");

    private static string SourceBackedPagePrefix(string language)
        => SourceBackedLabel(language, "p.", "p.", "p.", "p.", "S.", "p.");

    private static string SourceBackedVisibleMinutesSuffix(string language)
        => SourceBackedLabel(
            language,
            "min visibles",
            "visible minutes",
            "min visibles",
            "min visiveis",
            "sichtbare Min.",
            "min visibili");

    private static string[] ExtractParameterFacts(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 900);
        var matches = Regex.Matches(
                readable,
                @"(?i)\b(?:programme\s+[A-Z][\p{L}\-]*|mode\s+manuel|fonction\s+[A-Z][\p{L}\-]*|vitesse\s*\d+|speed\s*\d+|\d+\s*(?:Ã‚Â°\s*)?C|\d+\s*(?:min|minutes?|h|heures?))\b",
                RegexOptions.CultureInvariant)
            .Select(static match => CollapseWhitespace(match.Value))
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches;
    }

    private static string[] ExtractVisibleDurationsAndQuantities(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 900);
        return Regex.Matches(
                readable,
                @"(?i)\b(?:(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}|\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}|\d+\s*(?:min|minutes?|h|heures?|hours?))\b",
                RegexOptions.CultureInvariant)
            .Select(static match => CollapseWhitespace(match.Value))
            .Where(static value => LooksLikeVisibleDurationOrQuantityFact(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeVisibleDurationOrQuantityFact(string value)
    {
        var normalized = NormalizeLooseLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b\d+\s*(?:min|minutes?|h|heures?|hours?)\b", RegexOptions.CultureInvariant))
            return true;

        var labelMatch = Regex.Match(normalized, @"(?:^|\s)(?<n>\d{1,3})\s+(?<label>[\p{L}'\u2019.\-]{2,30})\b", RegexOptions.CultureInvariant);
        return labelMatch.Success && IsPlausibleScaleCountLabel(labelMatch.Groups["label"].Value);
    }

    private static string[] ExtractItemizedQuantityFacts(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1200);
        readable = Regex.Replace(readable, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        readable = Regex.Replace(readable, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        readable = RemoveMalformedFrenchFairePastParticipleFragments(readable);
        var compactItemizedSegments = ExtractCompactItemizedSegments(readable);
        var matches = Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12})\s*(?:de|d['Ã¢â‚¬â„¢Ã¢â‚¬Ëœ`])?\s*[\p{L}'Ã¢â‚¬â„¢Ã¢â‚¬Ëœ`\-\s]{2,55}",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanItemizedQuantityFact(match.Value))
            .Where(IsLikelyItemizedSegment)
            .Where(static value => value.Length is >= 5 and <= 90 && !LooksLikeTruncatedItemizedFact(value) && !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        matches.AddRange(Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s+[\p{L}'Ã¢â‚¬â„¢Ã¢â‚¬Ëœ`\-]{3,}(?:\s+(?:de|d['Ã¢â‚¬â„¢Ã¢â‚¬Ëœ`]|du|des)\s*[\p{L}'Ã¢â‚¬â„¢Ã¢â‚¬Ëœ`\-]{3,})?",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanItemizedSegment(match.Value))
            .Where(IsLikelyItemizedSegment));

        matches.AddRange(compactItemizedSegments);
        matches = matches
            .Where(static value => !LooksLikeMaterialOnlyFact(value))
            .Where(static value => !LooksLikeTruncatedProcedureSegment(value))
            .Where(static value => !LooksLikeProcedureInstructionSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (matches.Count == 0)
        {
            matches.AddRange(SplitEvidenceSegments(readable)
                .Where(static segment => ContainsStructuredItemHeading(segment) || CountQuantityLikeSignals(segment) >= 2)
                .Select(static segment => CleanItemizedSegment(segment))
                .Where(static segment => segment.Length is >= 5 and <= 160)
                .Where(static segment => !LooksLikeTruncatedItemizedFact(segment))
                .Where(static segment => !LooksLikeTruncatedProcedureSegment(segment))
                .Where(static segment => !LooksLikeProcedureInstructionSegment(segment))
                .Where(static segment => !LooksLikeMaterialOnlyFact(segment))
                .Take(6));
        }

        return matches.ToArray();
    }

    private static bool LooksLikeProcedureInstructionSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"^(?:a\s+la\s+fin|au\s+bout|dans|puis|ensuite|then|when|after|before|" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(normalized, @"\b(?:vitesse|speed|programme|program|mode|cycle|sequence|operation|execution|parametre|parametres|setting|settings|configuration)\b", RegexOptions.CultureInvariant)
               && Regex.IsMatch(normalized, @"\b\d+\s*(?:min(?:ute)?s?|h|heures?|hours?|s|sec(?:onde)?s?|(?:\u00b0|deg|degres?)\s*c)\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeMaterialOnlyFact(string value)
    {
        if (Regex.IsMatch(
                value ?? string.Empty,
                @"(?i)\b(?:mat[eÃƒÂ©]riel|mat(?:ÃƒÂ©|.)?riel|materials?|equipment)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var normalized = NormalizeLexicalLookup(value);
        return Regex.IsMatch(normalized, @"\b(?:materiel|materials?|equipment|tools?|outils?|devices?|instruments?)\b", RegexOptions.CultureInvariant);
    }

    private static string CleanItemizedQuantityFact(string value)
    {
        var cleaned = CollapseWhitespace((value ?? string.Empty).Trim(' ', '.', ',', ';', ':'));
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\s+\b(?:pour|for|para|per)\s+(?:la|le|les|l['Ã¢â‚¬â„¢]|the|a|el|los|las|o|os|as|il|lo|gli|die|der|das)?\s*[\p{L}'Ã¢â‚¬â„¢\-\s]{2,}$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':');
    }

    private static IEnumerable<string> ExtractCompactItemizedSegments(string readable)
    {
        var scoped = ExtractItemizedScope(readable);
        foreach (var segment in SplitEvidenceSegments(scoped))
        {
            var cleaned = CleanItemizedSegment(segment);
            if (IsLikelyItemizedSegment(cleaned))
                yield return cleaned;
        }
    }

    private static string ExtractItemizedScope(string readable)
    {
        var match = Regex.Match(
            readable,
            @"(?is)\b(?:" + ItemizedSectionHeadingPattern + @")\b\s*[:\-]?\s*(?<body>.+?)(?:\b(?:" + ProcedureSectionHeadingPattern + @")\b|$)",
            RegexOptions.CultureInvariant);
        return match.Success ? CutBeforeSectionHeading(match.Groups["body"].Value, NonItemizedSectionHeadingPattern) : readable;
    }

    private static string ExtractPreparationScope(string readable)
    {
        var proceduralMatch = Regex.Match(
            readable,
            @"(?is)\b(?:" + ProcedureSectionHeadingPattern + @")\b\s*[:\-]?\s*(?<body>.+)$",
            RegexOptions.CultureInvariant);
        if (proceduralMatch.Success)
            return proceduralMatch.Groups["body"].Value;

        var match = Regex.Match(
            readable,
            @"(?is)\b(?:preparation|pr[eÃƒÂ©]paration|preparacion|prepara[cÃƒÂ§][aÃƒÂ£]o|preparazione|zubereitung|operation|operations?|workflow|workflows?|execution|procedure|procedures?|process|processus|instructions?|etapes?|[eÃƒÂ©]tapes?|steps?)\b\s*[:\-]?\s*(?<body>.+)$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["body"].Value : readable;
    }

    private static string CutBeforeSectionHeading(string text, string headingPattern)
    {
        var value = text ?? string.Empty;
        var match = Regex.Match(
            value,
            @"(?is)\b(?:" + headingPattern + @")\b\s*[:\-]?",
            RegexOptions.CultureInvariant);
        return match.Success ? value[..match.Index] : value;
    }

    private static string CleanItemizedSegment(string segment)
    {
        var cleaned = CollapseWhitespace(segment)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:" + NonItemizedSectionHeadingPattern + @")\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:" + ProcedureSectionHeadingPattern + @")\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)^\b(?:" + ItemizedSectionHeadingPattern + @")\b\s*[:\-]?\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
    }

    private static bool IsLikelyItemizedSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length is < 3 or > 100)
            return false;
        if (LooksLikeTruncatedItemizedFact(segment))
            return false;

        var normalized = NormalizeLexicalLookup(segment);
        if (LooksLikeProcedureInstructionSegment(normalized))
            return false;
        if (Regex.IsMatch(normalized, @"\b(?:preparation|operation|workflow|execution|etape|etapes|temps|time|duration|duree|procedure|method|methode|instructions?|steps?)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:min|minutes?|h|heures?|hour|hours|c|celsius)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:a|l)\b", RegexOptions.CultureInvariant))
            return false;
        var leadingNumberLabel = Regex.Match(normalized, @"^\d+(?:[,.]\d+)?\s+(?<label>[\p{L}'\u2019.\-]{2,30})\b", RegexOptions.CultureInvariant);
        if (leadingNumberLabel.Success && LooksLikeProcedureLeadLabel(leadingNumberLabel.Groups["label"].Value))
            return false;
        var leadingWord = Regex.Match(normalized, @"^(?<label>[\p{L}'\u2019.\-]{2,30})\b", RegexOptions.CultureInvariant);
        if (leadingWord.Success && LooksLikeProcedureLeadLabel(leadingWord.Groups["label"].Value))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:dans|puis|quand|when|then|after|before|until|jusqu|pendant)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:materials?|equipment|tools?|outils?|devices?|instruments?)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|[a-z]{1,8}\.?|units?|items?|pieces?)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+\p{L}{3,}(?:\s+\p{L}{2,}){0,5}$", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(normalized, @"^[\p{L}'\-]{3,}(?:\s+[\p{L}'\-]{2,}){0,5}$", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(normalized, @"\b(?:items?|elements?|requirements?|preparation|operation|workflow|execution|procedure|method|methode|technique|materiel|materials?|equipment|tools?|outils?)\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeTruncatedItemizedFact(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        return Regex.IsMatch(normalized, @"\b(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeItemizedListSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:" + ItemizedSectionHeadingPattern + @")\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|[a-z]{1,8}\.?|units?|items?|pieces?)\b", RegexOptions.CultureInvariant);
    }

    private static string[] ExtractProcedureSteps(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1400);
        readable = Regex.Replace(readable, @"(?<=\p{L})\.(?=[1-9]\.)", ". ", RegexOptions.CultureInvariant);
        readable = Regex.Replace(readable, @"(?<=[1-9]\.)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        var proceduralScope = ExtractPreparationScope(readable);
        var numbered = Regex.Matches(
                proceduralScope,
                @"(?i)(?:^|\s)(?:[1-9][\.)]\s*)(?<step>.{18,190}?)(?=\s*[1-9][\.)]\s*|$)",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanProcedureStep(match.Groups["step"].Value))
            .Where(static value => value.Length is >= 12 and <= 220 && !LooksLikeItemizedListSegment(value) && !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        if (numbered.Length > 0)
            return numbered;

        var scopedCandidates = ExtractProcedureCandidateSegments(proceduralScope).ToArray();
        var allCandidates = ExtractProcedureCandidateSegments(readable).ToArray();
        if (allCandidates.Length > scopedCandidates.Length)
            return allCandidates.Take(10).ToArray();

        return scopedCandidates.Take(10).ToArray();
    }

    private static IEnumerable<string> ExtractProcedureCandidateSegments(string text)
    {
        return SplitEvidenceSegments(text)
            .Select(static segment => CleanProcedureStep(segment))
            .Where(static segment => !LooksLikeItemizedListSegment(segment))
            .Where(static segment => !LooksLikeTruncatedProcedureSegment(segment))
            .Where(static segment => LooksLikeProcedureCandidateSegment(segment));
    }

    private static bool LooksLikeProcedureCandidateSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:procedure|procedures?|instructions?|method|methode|etape|etapes|steps?|process|processus)\b", RegexOptions.CultureInvariant))
            return true;

        var wordCount = Regex.Matches(normalized, @"\b\p{L}{3,}\b", RegexOptions.CultureInvariant).Count;
        if (wordCount < 3)
            return false;

        return wordCount >= 3
            || Regex.IsMatch(normalized, @"\b(?:puis|ensuite|avant|apres|lorsque|quand|when|then|after|before|until|jusqu|pendant|pendant\s+que|while)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b\d+(?:[,.]\d+)?\s*(?:s|sec|secs|secondes?|seconds?|min|h|hours?|mm|cm|m|g|kg|mg|ml|cl|l|%|(?:\u00b0|deg|degres?)\s*c)\b", RegexOptions.CultureInvariant)
            || segment.Contains(';', StringComparison.Ordinal)
            || segment.Contains(',', StringComparison.Ordinal);
    }

    private static string CleanProcedureStep(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty).Trim(' ', '.', ';', ':');
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?<=\p{L})\.\s*[1-9]\.?\s*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ';', ':');
    }

    private static string CleanReadableProcedureArtifacts(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty);
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ';', ':');
    }

    private static string RemoveMalformedFrenchFairePastParticipleFragments(string value)
        => Regex.Replace(
            value ?? string.Empty,
            @"(?i)\bfaites?\s+\p{L}{4,}i\b\.?\s*(?=(?:\s*(?:[-*\u2022\u00b7]|\p{Lu})|$))",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static bool LooksLikeTruncatedProcedureSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return Regex.IsMatch(normalized, @"\b(?:de|du|des|d|a|avec|sans|et|puis|jusqu|jusque|pendant|pour)$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\bfaites?\s+\p{L}{4,}i\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l)\s+(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> SplitEvidenceSegments(string text)
    {
        foreach (var segment in Regex.Split(text, @"(?:\s*[\u2022\u00b7]\s*|(?<=[\.;!?])\s+)", RegexOptions.CultureInvariant))
        {
            var cleaned = CollapseWhitespace(segment).Trim(' ', '.', ',', ';', ':');
            if (cleaned.Length is >= 8 and <= 220)
                yield return cleaned;
        }
    }

    private static string FormatReadableEvidenceExcerpt(string? excerpt, int maxLength)
    {
        var text = PrepareReadableEvidenceText(excerpt);
        if (text.Length == 0)
            return string.Empty;

        return text.Length <= maxLength
            ? text
            : text[..maxLength].TrimEnd() + "...";
    }

    private static string PrepareReadableEvidenceText(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\d)(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\p{L})(?=\d+\s*min\b)", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CollapseWhitespace(text);
    }

    private static string FormatSourceBackedEvidenceExcerpt(RagHitSummary hit, string query, int maxLength)
    {
        var evidence = GetBestRagEvidenceText(hit);
        if (LooksLikeShortTechnicalEvidenceTopic(query))
        {
            var focused = FocusShortTechnicalEvidenceText(hit, query, maxLength);
            if (!string.IsNullOrWhiteSpace(focused))
                evidence = focused;
        }

        return FormatReadableEvidenceExcerpt(evidence, maxLength);
    }

    private static string FocusShortTechnicalEvidenceText(RagHitSummary hit, string query, int maxLength)
    {
        var readable = PrepareReadableEvidenceText($"{hit.SectionTitle} {hit.HeadingPath} {hit.FullText} {hit.ContextualSnippet} {BuildRagHitContentCardEvidenceText(hit)}");
        if (string.IsNullOrWhiteSpace(readable))
            readable = PrepareReadableEvidenceText(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(readable))
            return string.Empty;

        var topic = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(query)));
        var phraseIndex = FindShortTechnicalPhraseEvidenceIndex(topic, readable);
        if (phraseIndex < 0)
            phraseIndex = FindShortTechnicalTermEvidenceIndex(topic, readable);
        if (phraseIndex < 0)
            return string.Empty;

        var lead = Math.Max(40, maxLength / 4);
        var start = Math.Max(0, phraseIndex - lead);
        if (start > 0)
        {
            var boundary = readable.LastIndexOfAny(new[] { '.', ';', ':', '\n' }, Math.Min(phraseIndex, readable.Length - 1));
            if (boundary >= 0 && phraseIndex - boundary <= lead + 80)
                start = Math.Min(readable.Length, boundary + 1);
        }

        start = Math.Max(0, start);
        var length = Math.Min(readable.Length - start, Math.Max(maxLength + 120, maxLength));
        var window = readable.Substring(start, length).Trim();
        return start > 0 ? "... " + window : window;
    }

    private static int FindShortTechnicalPhraseEvidenceIndex(string normalizedTopic, string readableEvidence)
    {
        var terms = BuildShortTechnicalEvidenceSelectionTerms(normalizedTopic).ToHashSet(StringComparer.Ordinal);
        var hasProperty = terms.Overlaps(new[] { "propriete", "proprietes", "property", "properties", "caracteristique", "caracteristiques", "characteristic", "characteristics" });
        var hasElectrical = terms.Overlaps(new[] { "electrique", "electriques", "electric", "electrical", "dielectric" });
        var hasThermal = terms.Overlaps(new[] { "thermique", "thermiques", "thermal", "temperature", "temperatures" });

        if (hasProperty && hasElectrical)
            return MatchIndex(readableEvidence, @"\b(?:electrical\s+properties|electric\s+properties|dielectric\s+properties|properties\s+(?:electrical|electric|dielectric)|electrical\s+characteristics|dielectric\s+strength|propri[eÃƒÂ©]t[eÃƒÂ©]s?\s+[eÃƒÂ©]lectriques?|caract[eÃƒÂ©]ristiques?\s+[eÃƒÂ©]lectriques?)\b");

        if (hasProperty && hasThermal)
            return MatchIndex(readableEvidence, @"\b(?:thermal\s+properties|temperature\s+properties|properties\s+thermal|thermal\s+characteristics|melt\s+point|service\s+temperature|propri[eÃƒÂ©]t[eÃƒÂ©]s?\s+thermiques?|caract[eÃƒÂ©]ristiques?\s+thermiques?)\b");

        return -1;
    }

    private static int FindShortTechnicalTermEvidenceIndex(string normalizedTopic, string readableEvidence)
    {
        var propertyTerms = new HashSet<string>(new[] { "propriete", "proprietes", "property", "properties", "caracteristique", "caracteristiques", "characteristic", "characteristics" }, StringComparer.Ordinal);
        var terms = BuildShortTechnicalEvidenceSelectionTerms(normalizedTopic)
            .Where(term => !propertyTerms.Contains(term))
            .OrderByDescending(static term => term.Length)
            .Concat(BuildShortTechnicalEvidenceSelectionTerms(normalizedTopic).Where(propertyTerms.Contains))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var term in terms)
        {
            var pattern = term switch
            {
                "electrique" or "electriques" => @"\b[eÃƒÂ©]lectriques?\b",
                "thermique" or "thermiques" => @"\bthermiques?\b",
                _ => @"\b" + Regex.Escape(term) + @"\b"
            };
            var index = MatchIndex(readableEvidence, pattern);
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static int MatchIndex(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
    }

    private static string GetBestRagEvidenceText(RagHitSummary hit)
    {
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);
        var structuredEvidence = BuildRagHitContentCardEvidenceText(hit);

        if (fullText.Length > excerpt.Length + 80 && LooksLikeTruncatedEvidenceLead(excerpt))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
        if (excerpt.Length >= 40)
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        if (fullText.Length > excerpt.Length + 80)
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
        if (!string.IsNullOrWhiteSpace(structuredEvidence))
            return string.IsNullOrWhiteSpace(excerpt)
                ? structuredEvidence
                : $"{excerpt} {structuredEvidence}";
        if (!string.IsNullOrWhiteSpace(excerpt))
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        return fullText;
    }

    private static string AppendContentCardEvidenceText(string primary, string structuredEvidence)
    {
        primary = CollapseWhitespace(primary ?? string.Empty);
        structuredEvidence = CollapseWhitespace(structuredEvidence ?? string.Empty);
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(structuredEvidence))
            return primary;

        var normalizedPrimary = NormalizeLexicalLookup(primary);
        var normalizedEvidence = NormalizeLexicalLookup(structuredEvidence);
        if (!string.IsNullOrWhiteSpace(normalizedEvidence)
            && normalizedPrimary.Contains(normalizedEvidence, StringComparison.Ordinal))
            return primary;

        return CollapseWhitespace($"{primary} {structuredEvidence}");
    }

    private const int RagHitContentCardEvidenceTextCacheMaxEntries = 512;
    private static readonly object RagHitContentCardEvidenceTextCacheGate = new();
    private static readonly Dictionary<RagHitSummary, string> RagHitContentCardEvidenceTextCache =
        new(ReferenceEqualityComparer.Instance);

    private static string BuildRagHitContentCardEvidenceText(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return string.Empty;

        lock (RagHitContentCardEvidenceTextCacheGate)
        {
            if (RagHitContentCardEvidenceTextCache.TryGetValue(hit, out var cached))
                return cached;
        }

        var text = BuildRagHitContentCardEvidenceTextUncached(hit);
        lock (RagHitContentCardEvidenceTextCacheGate)
        {
            if (RagHitContentCardEvidenceTextCache.Count >= RagHitContentCardEvidenceTextCacheMaxEntries)
                RagHitContentCardEvidenceTextCache.Clear();
            RagHitContentCardEvidenceTextCache[hit] = text;
        }

        return text;
    }

    private static string BuildRagHitContentCardEvidenceTextUncached(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return string.Empty;

        var parts = new List<string>();
        foreach (var evidence in hit.MatchedContentCards.Select(static card => card.Evidence).Where(static evidence => evidence is not null))
        {
            if (evidence!.ScaleBasis is { Count: > 0 } basis)
            {
                parts.Add(CleanContentCardDisplayFactValue(CollapseWhitespace(string.Join(' ', new[]
                {
                    basis.Count.ToString(CultureInfo.InvariantCulture),
                    basis.Label
                }.Where(static value => !string.IsNullOrWhiteSpace(value))))));
            }

            foreach (var fact in evidence.QuantityFacts.Take(8))
            {
                parts.Add(CleanContentCardDisplayFactValue(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    fact.Unit,
                    fact.Label,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value))))));
            }

            foreach (var fact in (evidence.Facts ?? []).Take(12))
            {
                parts.Add(CleanContentCardDisplayFactValue(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Kind,
                    fact.Label,
                    fact.Value,
                    fact.Unit,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value))))));
            }

            foreach (var reason in evidence.NonScalableReasons.Take(6))
            {
                parts.Add(CollapseWhitespace(reason));
            }
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static part => !string.IsNullOrWhiteSpace(part))));
    }

    private static bool LooksLikeTruncatedEvidenceLead(string? value)
    {
        var text = CollapseWhitespace(value ?? string.Empty).Trim();
        text = text.TrimStart('.', '\u2026', ' ', '-', ':');
        if (text.Length < 12)
            return false;

        var normalized = NormalizeLexicalLookup(text);
        if (Regex.IsMatch(normalized, @"^(?:nt|es|e|s|de|du|des|la|le|les|a|au|aux|et|ou)\b", RegexOptions.CultureInvariant))
            return true;

        return char.IsLower(text[0])
            && !Regex.IsMatch(normalized, @"^(?:preparation|operation|workflow|execution|procedure|pour|for|para|per)\b", RegexOptions.CultureInvariant);
    }

}
