using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static class StructuredContentLexicon
{
    private static readonly string[] ItemizedCueTerms =
    [
        "materials",
        "materiales",
        "materiais",
        "materiaux",
        "components",
        "componentes",
        "composants",
        "requirements",
        "requirement",
        "quantities",
        "quantites",
        "values",
        "valeurs",
        "items",
        "elements",
        "materiel",
        "material"
    ];

    private static readonly string[] ProcedureCueTerms =
    [
        "procedure",
        "preparation",
        "preparacion",
        "preparacao",
        "preparazione",
        "zubereitung",
        "methode",
        "method",
        "etapes",
        "steps",
        "pasos",
        "passos",
        "schritte",
        "instruction"
    ];

    private static readonly string[] GovernanceCueTerms =
    [
        "warning",
        "warnings",
        "caution",
        "cautions",
        "attention",
        "consigne",
        "consignes",
        "instruction",
        "instructions"
    ];

    private static readonly string[] StructuredAnswerCueTerms =
    [
        "materials",
        "materiaux",
        "components",
        "composants",
        "procedure",
        "preparation",
        "preparacion",
        "preparacao",
        "preparazione",
        "zubereitung",
        "methode",
        "method",
        "etapes",
        "steps",
        "pasos",
        "passos",
        "schritte",
        "instruction",
        "materiel",
        "material",
        "requirement",
        "warning",
        "caution"
    ];

    private static readonly string[] RetrievalProcedureCueTerms =
        ProcedureCueTerms
            .Concat(["realisation", "workflow", "process", "technique"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static readonly string[] StructuredContextCueTerms =
        ItemizedCueTerms
            .Concat(ProcedureCueTerms)
            .Concat(GovernanceCueTerms)
            .Concat([
                "standard",
                "standards",
                "reference",
                "references",
                "norme",
                "normes",
                "safety",
                "security",
                "securite"
            ])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static bool ContainsStructuredAnswerCue(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return StructuredAnswerCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool ContainsItemizedCue(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && ItemizedCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool ContainsProcedureCue(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && ProcedureCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool ContainsGovernanceCue(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && GovernanceCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool ContainsRetrievalProcedureCue(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && RetrievalProcedureCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool ContainsStructuredContentContext(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && StructuredContextCueTerms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    public static bool TryExtractScaleBasis(string? value, out int count, out string? label)
    {
        count = 0;
        label = null;

        var text = NormalizeForStructuredLookup(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var patterns = new[]
        {
            @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30}))?\b",
            @"\b(?:base|basis|yield|rendement|batch|lot|serie|set)\s*(?:[:=\-]?\s*)?(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30}))?\b",
            @"\b(?<n>\d{1,3})\s*(?<label>items?|elements?|entries?|units?|unites?|components?|composants?|parts?|pieces?)\b"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success
                || !int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed is <= 0 or > 200)
            {
                continue;
            }

            var parsedLabel = match.Groups["label"].Success ? match.Groups["label"].Value : null;
            if (!IsPlausibleScaleBasisLabel(parsedLabel))
                continue;

            count = parsed;
            label = parsedLabel;
            return true;
        }

        return false;
    }

    public static bool IsPlausibleScaleBasisLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return true;

        var normalized = NormalizeStructuredSignalLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return !Regex.IsMatch(
            normalized,
            @"^(?:g|kg|mg|ml|cl|l|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|s|sec|secs|secondes?|seconds?|min|mins|minutes?|h|hr|hrs|heures?|hours?|jour|jours|day|days|mois|month|months|annee|annees|year|years|eur|euro|euros|chf|usd|gbp|page|pages?)$",
            RegexOptions.CultureInvariant);
    }

    public static bool ContainsNonScalableQuantityContext(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && (Regex.IsMatch(
                    text,
                    @"\b(?:safety|security|securite|hazard|danger|risk|risque|warning|caution|compliance|conformite|regulatory|reglementaire|law|legal|norme|standard|limit|limite|threshold|seuil|mandatory|required|obligatoire|shall|must|interdit|prohibited|forbidden)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    text,
                    @"\b(?:temperature|pressure|pression|voltage|tension|current|courant|speed|vitesse|frequency|frequence|torque|couple|setting|reglage|parametre|parameter|distance|dimension|diameter|diametre|angle|concentration|dosage|ph)\b",
                    RegexOptions.CultureInvariant));
    }

    public static bool IsNonScalableQuantityUnit(string? unit)
    {
        var normalized = NormalizeStructuredSignalLabel(unit);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (Regex.IsMatch(normalized, @"^(?:deg|degree|degrees|degre|degres|c|celsius|fahrenheit)$", RegexOptions.CultureInvariant))
            return true;

        if (normalized == "pct" || normalized == "percent" || normalized == "pourcent")
            return true;

        return Regex.IsMatch(
            normalized,
            @"^(?:s|sec|secs|secondes?|seconds?|min|mins|minutes?|h|hr|hrs|heures?|hours?|jour|jours|day|days|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|eur|euro|euros|chf|usd|gbp|page|pages?|items?|elements?|entries?|units?|unites?|personnes?|persons?|people)$",
            RegexOptions.CultureInvariant);
    }

    public static bool LooksLikeStructuredLeadMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = GetLeadMarkerWindow(value);
        if (Regex.IsMatch(
                value,
                @"(?:^|[^\p{L}\p{N}]|(?<=[\p{Ll}])(?=[\p{Lu}]))(?:pour|for|para|per|fur|fuer)\s+\d+|(?:^|[^\p{L}\p{N}]|(?<=[\p{Ll}])(?=[\p{Lu}]))(?:items?|elements?|components?|requirements?|materials?|materiaux|materiel|material|preparation|pr[e\u00e9]paration|preparaci[o\u00f3]n|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|method|methode|steps?|[e\u00e9]tapes?|pasos|passos|schritte|procedure|instructions?)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            return true;
        }

        var text = NormalizeForStructuredLookup(value);
        if (Regex.IsMatch(text, @"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per|fur|fuer)\s+\d+", RegexOptions.CultureInvariant))
            return true;

        return ContainsItemizedCue(value) || ContainsProcedureCue(value);
    }

    private static string GetLeadMarkerWindow(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        if (text.Length == 0)
            return string.Empty;

        var boundary = Regex.Match(text, @"[.!?](?:\s|(?=\p{Lu})|$)", RegexOptions.CultureInvariant);
        return boundary.Success && boundary.Index > 0
            ? text[..boundary.Index]
            : text;
    }

    public static bool LooksLikeStructuredQuantityList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var bulletCount = CountBulletMarkers(value);
        if (bulletCount < 3)
            return false;

        var measurementCount = Regex.Matches(
            Normalize(value),
            @"\b\d+(?:[\.,]\d+)?\s?(?:kg|g|mg|l|ml|cl|h|min|s|mm|cm|m|bar|v|a|w|kw|nm|%|deg|c)\b",
            RegexOptions.CultureInvariant).Count;
        return measurementCount >= 2;
    }

    public static string NormalizeForStructuredLookup(string? value)
        => Normalize(value);

    public static string NormalizeStructuredSignalLabel(string? value)
    {
        var folded = Normalize(value);
        var normalized = Regex.Replace(folded, @"[^\p{L}\p{N}]+", "_", RegexOptions.CultureInvariant)
            .Trim('_');
        return normalized;
    }

    private static string Normalize(string? value)
        => FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value ?? string.Empty)).ToLowerInvariant();

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        Span<char> buffer = normalized.Length <= 1024 ? stackalloc char[normalized.Length] : new char[normalized.Length];
        var index = 0;
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                buffer[index++] = ch;
        }

        return new string(buffer[..index]).Normalize(NormalizationForm.FormC);
    }
}
