using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string GetPlanExtractionText(RagHitSummary hit)
        => string.IsNullOrWhiteSpace(hit.FullText) ? hit.Excerpt : hit.FullText!;

    private static IEnumerable<string> EnumeratePlanExtractionTexts(RagHitSummary hit)
    {
        var texts = new[]
        {
            hit.FullText,
            hit.Excerpt,
            hit.ContextualSnippet,
            CollapseWhitespace(string.Join(' ', new[] { hit.FullText, hit.Excerpt }
                .Where(static value => !string.IsNullOrWhiteSpace(value))))
        };
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var normalized = CollapseWhitespace(text ?? string.Empty);
            if (normalized.Length == 0 || !emitted.Add(normalized))
                continue;

            yield return normalized;
        }
    }

    private static IEnumerable<string> ExtractPlanItemTitleCandidatesV2(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            yield break;

        var first = ExtractPlanItemTitleV2(text);
        if (!string.IsNullOrWhiteSpace(first))
            yield return first;

        foreach (var quotedTitle in ExtractQuotedSourceBackedItemTitleCandidates(text))
            yield return quotedTitle;

        const string structureLabelPattern =
            @"components?|composants?|constituents?|constituants?|inputs?|requirements?|exigences?|quantit(?:y|ies)|quantit[eÃƒÂ©]s?|values?|valeurs?|materials?|mat[eÃƒÂ©]riel|items?|[eÃƒÂ©]l[eÃƒÂ©]ments?|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|preparation|pr[eÃƒÂ©]paration|technique|operation|workflow|temps\s+total|total\s+time";
        var sectionLeadPattern =
            $@"(?i)(?:^|[.!?]\s+)(?<title>\p{{Lu}}[\p{{L}}'\u2019 \-/]{{5,80}}?)(?:\.|\s)\s*(?:{structureLabelPattern})\b";
        foreach (Match match in Regex.Matches(text, sectionLeadPattern, RegexOptions.CultureInvariant))
        {
            var title = HumanizePlanItemTitleV2(match.Groups["title"].Value);
            if (!LooksLikePlanPageHeading(text, title) && IsUsableSourceBackedOptionTitle(title))
                yield return title;
        }
    }

    private static IEnumerable<string> ExtractQuotedSourceBackedItemTitleCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in Regex.Matches(
            text,
            @"[\u00ab\u00bb\u201c\u201d""]\s*(?<title>[\p{L}\p{N}][^\u00ab\u00bb\u201c\u201d""]{3,90}?)\s*[\u00ab\u00bb\u201c\u201d""]",
            RegexOptions.CultureInvariant))
        {
            var title = CollapseWhitespace(match.Groups["title"].Value)
                .Trim(' ', '.', ',', ';', ':', '-', '\'', '"');
            if (!string.IsNullOrWhiteSpace(title))
                yield return title;
        }
    }

    private static string ExtractPlanItemTitleV2(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"^\d+", string.Empty, RegexOptions.CultureInvariant).Trim();
        text = Regex.Replace(text, @"(?<=[\p{Ll}])(?=(?:Pour|For|Para|Per)\b)", " ", RegexOptions.CultureInvariant);
        const string structureLabelPattern =
            @"components?|composants?|constituents?|constituants?|inputs?|requirements?|exigences?|quantit(?:y|ies)|quantit[eÃƒÂ©]s?|values?|valeurs?|materials?|mat[eÃƒÂ©]riel|items?|[eÃƒÂ©]l[eÃƒÂ©]ments?|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|preparation|pr[eÃƒÂ©]paration|technique|operation|workflow|temps\s+total|total\s+time";
        var patterns = new[]
        {
            $@"(?i)^(?<title>[\p{{Lu}}\p{{Lt}}0-9][\p{{Lu}}\p{{Lt}}0-9 '&/,\-\u00c0-\u017f]{{5,90}}?)\s+[\p{{Lu}}\p{{Lt}}][\p{{L}}'\u2019\-]{{3,32}}\s*(?:[:*]|\u2022)\s*.{{0,240}}\b(?:{structureLabelPattern})\b",
            @"^(?<title>[\p{Lu}\p{Lt}0-9][\p{Lu}\p{Lt}0-9 '\u2019&/,\-\u00c0-\u017f]{5,120}?)(?:\s+\d+[\.)]\s|\s+[Ã¢â‚¬Â¢\u2022]\s)",
            $@"(?i)^(?<title>\p{{Lu}}[\p{{L}}'\u2019 &/,\-]{{5,90}}?)\s+(?:{structureLabelPattern})\b",
            @"^(?:[\p{Lu}\p{Lt}][\p{Ll}]{2,24})?(?<title>[\p{Lu}\p{Lt}][\p{Lu}\p{Lt}0-9 '&/,\-]{5,90}?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*(?:eur|euros?|chf))",
            @"(?i)^(?<title>\p{Lu}[\p{L}'\u2019 \-/]{5,80}?)\s+(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}\b",
            $@"(?i)\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{{1,3}}\s+[\p{{L}}'\u2019.\-]{{2,30}}\s+(?<title>\p{{Lu}}[\p{{L}}'\u2019 \-/]{{5,80}}?)(?:\s+(?:{structureLabelPattern}|\d+\s*min))",
            $@"(?i)(?:^|[\s:;])(?<title>\p{{Lu}}[\p{{Lu}}0-9 '&/,\-]{{5,90}}?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*(?:eur|euros?|chf)|{structureLabelPattern})",
            $@"(?i)\b(?<title>\p{{Lu}}[\p{{L}}'\u2019 \-/]{{5,80}})\s+(?:\d+\s*(?:items?|elements?|units?|pieces?)|{structureLabelPattern})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var title = HumanizePlanItemTitleV2(match.Groups["title"].Value);
            if (!LooksLikePlanPageHeading(text, title) && IsUsableSourceBackedOptionTitle(title))
                return title;
        }

        return string.Empty;
    }

    private static bool LooksLikePlanPageHeading(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
            return false;

        var escapedTitle = Regex.Escape(CollapseWhitespace(title));
        return Regex.IsMatch(
            CollapseWhitespace(text),
            $@"(?:^|\s)(?:\d+\s*)?\|\s*{escapedTitle}\s*(?:Pour|For|Para|Per)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string HumanizePlanItemTitleV2(string value)
    {
        var title = CollapseWhitespace(value);
        title = Regex.Replace(title, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ", RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"(?<=[\p{Lu}\p{Lt}]{4})\b(?=(?:WITHOUT|SENZA|SELON|AVEC|SANS|PARA|OHNE|WITH|POUR|AUX|DES|AND|FOR|CON|SIN|MIT|PER|DU|AU|A|D['\u2019])\b)",
            " ",
            RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"\b(?:WITHOUT|SENZA|SELON|AVEC|SANS|PARA|OHNE|WITH|POUR|AUX|DES|AND|FOR|CON|SIN|MIT|PER|DU|AU|A|D['\u2019])(?=[\p{Lu}\p{Lt}]{4})",
            "$0 ",
            RegexOptions.CultureInvariant);
        title = RepairSplitOcrBrokenTitleWords(title);
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', ':');
        return title;
    }

    private static string RepairSplitOcrBrokenTitleWords(string? value)
    {
        var repaired = CollapseWhitespace(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(repaired))
            return string.Empty;

        return Regex.Replace(
            repaired,
            @"\b(?<left>[\p{L}]{2,4})\s+(?<right>[\p{L}]{4,10})\b",
            match => LooksLikeUppercaseOcrSplitTitleWord(match.Groups["left"].Value, match.Groups["right"].Value)
                ? match.Groups["left"].Value + match.Groups["right"].Value
                : match.Value,
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeUppercaseOcrSplitTitleWord(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var normalizedLeft = NormalizeLexicalLookup(left);
        var normalizedRight = NormalizeLexicalLookup(right);
        if (normalizedLeft.Length is < 2 or > 4 || normalizedRight.Length is < 4 or > 10)
            return false;

        if (normalizedLeft is "a" or "au" or "aux" or "de" or "du" or "des" or "la" or "le" or "les"
            or "avec" or "sans" or "pour" or "chez" or "vers" or "dans" or "sous" or "plus"
            or "ma" or "ta" or "sa" or "mon" or "ton" or "son" or "mes" or "tes" or "ses"
            or "notre" or "votre" or "leur" or "nos" or "vos" or "leurs"
            or "with" or "and" or "the" or "for" or "of" or "von" or "und" or "mit"
            or "my" or "your" or "our" or "their"
            or "con" or "com" or "per" or "para" or "las" or "los" or "les" or "des" or "une")
        {
            return false;
        }

        var leftLetters = left.Where(char.IsLetter).ToArray();
        var rightLetters = right.Where(char.IsLetter).ToArray();
        if (leftLetters.Length != left.Length || rightLetters.Length != right.Length)
            return false;

        var leftUpperRatio = leftLetters.Count(char.IsUpper) / (double)leftLetters.Length;
        var rightUpperRatio = rightLetters.Count(char.IsUpper) / (double)rightLetters.Length;
        return leftUpperRatio >= 0.75 && rightUpperRatio >= 0.75;
    }

    private static bool LooksLikePlanItemNoise(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (normalized.Length < 4)
            return true;
        if (normalized.Length > 90)
            return true;

        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && char.IsLower(trimmed[0]))
            return true;

        if (Regex.IsMatch(normalized, @"^(?:si|lorsque|quand|when|if)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(normalized, @"^(?:en\s+)?moins\s+de\b|^under\s+\d+\b|^less\s+than\s+\d+\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(
                normalized,
            @"\b(?:liste|source|sources|page|pages|sommaire|index|contents|catalogue|copyright|isbn|edition|components?|components?|preparation|operation|workflow|execution|organisation|planning|calendrier|modele|outils?|tools?|elements?|[eÃƒÂ©]l[eÃƒÂ©]ments?|conseils?|consiste|prendre|heures?|temps|documents?|disponibles?|materiel|service|utilisez|utiliser|choisissez|installation|lors|ouvrir|programmer|extraire|volonte|limiter|limit)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"(^|\s)\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|lbs)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:p\s*)?(?:preparation|operation|workflow)\s*\d*$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:de|du|des|d|a|au|aux|of|the|for|with|con|por|para|per|di|da|von|zu)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:le|la|les|un|une|der|die|das|il|lo|gli|el|los|las)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var meaningfulTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 4)
            .ToArray();
        if (meaningfulTerms.Length == 0)
            return true;

        var letterCount = normalized.Count(char.IsLetter);
        return letterCount < Math.Max(4, normalized.Length / 3);
    }

    internal static string BuildSourceBackedExtractiveHeader(string language, bool noExplicitPairing)
    {
        language = NormalizeLanguageCode(language);
        if (noExplicitPairing)
        {
            return language switch
            {
                "en" => "I did not find a passage that connects every part of the request. The useful passages I can cite from the available documents are:",
                "es" => "No he encontrado un pasaje que conecte todas las partes de la solicitud. Los pasajes ÃƒÂºtiles que puedo citar en los documentos disponibles son:",
                "pt" => "NÃƒÂ£o encontrei uma passagem que ligue todas as partes do pedido. Os excertos ÃƒÂºteis que posso citar nos documentos disponÃƒÂ­veis sÃƒÂ£o:",
                "de" => "Ich habe keine Stelle gefunden, die alle Teile der Anfrage verbindet. Die nÃƒÂ¼tzlichen Passagen aus den verfÃƒÂ¼gbaren Dokumenten sind:",
                "it" => "Non ho trovato un passaggio che colleghi tutte le parti della richiesta. I passaggi utili che posso citare nei documenti disponibili sono:",
                _ => "Je n'ai pas trouvÃƒÂ© de passage qui relie tous les ÃƒÂ©lÃƒÂ©ments de la demande. Les passages utiles que je peux citer dans les documents disponibles sont :"
            };
        }

        return language switch
        {
            "en" => "Here are the useful passages found in the available documents:",
            "es" => "Estos son los pasajes ÃƒÂºtiles encontrados en los documentos disponibles:",
            "pt" => "Estes sÃƒÂ£o os excertos ÃƒÂºteis encontrados nos documentos disponÃƒÂ­veis:",
            "de" => "Hier sind die nÃƒÂ¼tzlichen Passagen aus den verfÃƒÂ¼gbaren Dokumenten:",
            "it" => "Ecco i passaggi utili trovati nei documenti disponibili:",
            _ => "Voici les passages utiles trouvÃƒÂ©s dans les documents disponibles :"
        };
    }

}
