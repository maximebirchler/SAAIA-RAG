using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static class StructuredContentLexicon
{
    private const int MaxStructuredItemTitleTokens = 7;

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

    private static readonly string[] StructuredItemTypeTerms =
    [
        "item",
        "items",
        "element",
        "elements",
        "topic",
        "topics",
        "section",
        "sections",
        "procedure",
        "procedures",
        "process",
        "processes"
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

    public static bool ContainsStructuredItemTypeCue(string? value)
    {
        var text = NormalizeForStructuredLookup(value);
        return !string.IsNullOrWhiteSpace(text)
            && StructuredItemTypeTerms.Any(term => ContainsExactTerm(text, term));
    }

    public static bool IsStructuredItemTypeToken(string? value)
    {
        var token = NormalizeStructuredSignalLabel(value);
        return !string.IsNullOrWhiteSpace(token)
            && StructuredItemTypeTerms.Any(term => string.Equals(token, term, StringComparison.Ordinal));
    }

    public static bool IsItemizedCueToken(string? value)
    {
        var token = NormalizeStructuredSignalLabel(value);
        return !string.IsNullOrWhiteSpace(token)
            && ItemizedCueTerms.Any(term => string.Equals(token, term, StringComparison.Ordinal));
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

    public static bool TryExtractStructuredItemTitleLead(string? value, out string title)
    {
        title = string.Empty;
        var text = PrepareStructuredItemTitleText(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = StructuredItemTitleLeadBeforeEvidenceRegex.Match(text);
        if (!match.Success)
            return false;

        var candidate = CleanStructuredItemTitleCandidate(match.Groups["title"].Value);
        if (!IsPlausibleStructuredItemTitle(candidate))
            return false;

        title = candidate;
        return true;
    }

    public static IReadOnlyList<string> ExtractEmbeddedStructuredItemTitles(string? value, int limit = 12)
    {
        if (limit <= 0)
            return [];

        var text = PrepareStructuredItemTitleText(value);
        if (text.Length < 18)
            return [];

        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in StructuredItemTitleBeforeEvidenceRegex.Matches(text))
        {
            var candidate = CleanStructuredItemTitleCandidate(match.Groups["title"].Value);
            if (!IsPlausibleStructuredItemTitle(candidate))
                continue;

            var key = NormalizeStructuredSignalLabel(candidate);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            titles.Add(candidate);
            if (titles.Count >= limit)
                break;
        }

        return titles;
    }

    public static bool TryExtractScaleBasis(string? value, out int count, out string? label)
    {
        count = 0;
        label = null;

        var text = NormalizeForStructuredLookup(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var patterns = new (string Pattern, bool RequiresLabel)[]
        {
            (@"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{0,30}))?\b", true),
            (@"\b(?:base|basis|yield|rendement|batch|lot|serie|set)\s*(?:[:=\-]?\s*)?(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{0,30}))?\b", false),
            (@"\b(?<n>\d{1,3})\s*(?<label>items?|elements?|entries?|units?|unites?|components?|composants?|parts?|pieces?)\b", false)
        };

        foreach (var (pattern, requiresLabel) in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success
                || !int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed is <= 0 or > 200)
            {
                continue;
            }

            var parsedLabel = match.Groups["label"].Success ? match.Groups["label"].Value : null;
            if (requiresLabel && string.IsNullOrWhiteSpace(parsedLabel))
                continue;
            if (LooksLikeMeasurementScaleBasisLabel(parsedLabel))
                continue;
            if (!IsPlausibleScaleBasisLabel(parsedLabel))
                continue;

            count = parsed;
            label = parsedLabel;
            return true;
        }

        return false;
    }

    private static bool LooksLikeMeasurementScaleBasisLabel(string? label)
    {
        var normalized = NormalizeStructuredSignalLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"^(?:g|kg|mg|ml|cl|l|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|s|sec|secs|secondes?|seconds?|min|mins|minutes?|h|hr|hrs|heures?|hours?|jour|jours|day|days|mois|month|months|annee|annees|year|years|deg|degree|degrees|degre|degres|c|celsius|fahrenheit|eur|euro|euros|chf|usd|gbp|page|pages?)$",
            RegexOptions.CultureInvariant);
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

    private static string PrepareStructuredItemTitleText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = Regex.Replace(value, @"[\u0000-\u001F]+", " ", RegexOptions.CultureInvariant);
        text = StructuredUnitBeforeTitleBoundaryRegex.Replace(text, "${unit} ");
        text = LowerOrDigitBeforeStructuredCueRegex.Replace(text, " ");
        text = LowerBeforeNumberedStepRegex.Replace(text, " ");
        text = PunctuationBeforeStructuredQuantityRegex.Replace(text, " ");
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string CleanStructuredItemTitleCandidate(string value)
    {
        var title = Regex.Replace(value ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        title = Regex.Replace(title, @"^\d{1,4}\s+", string.Empty, RegexOptions.CultureInvariant);
        title = title.Trim(' ', '-', ':', ';', '.', ',', '|', '/', '\\', '(', ')', '*', '\u2022');
        return Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool IsPlausibleStructuredItemTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length is < 4 or > 90)
            return false;

        var tokens = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > MaxStructuredItemTitleTokens)
            return false;

        if (title.Count(char.IsDigit) > 2)
            return false;

        var normalized = Normalize(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var normalizedTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedTokens.Length == 0)
            return false;

        var firstToken = normalizedTokens[0];
        if (StructuredItemTitleLeadStopwords.Contains(firstToken))
            return false;

        if (firstToken.Length >= 5 && firstToken.EndsWith("ez", StringComparison.Ordinal))
            return false;

        if (LooksLikeUppercaseHeadingWithBodyTail(tokens))
            return false;

        if (tokens.All(static token => token.All(static ch => !char.IsUpper(ch))))
            return false;

        return tokens.Any(static token => token.Any(char.IsLetter));
    }

    private static bool LooksLikeUppercaseHeadingWithBodyTail(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 5)
            return false;

        return tokens.Take(2).All(LooksLikeAllUppercaseWord)
            && tokens.Skip(2).Any(static token => token.Any(char.IsLower));
    }

    private static bool LooksLikeAllUppercaseWord(string token)
    {
        var letters = token.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(char.IsUpper);
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

    private static bool ContainsExactTerm(string text, string term)
        => Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])",
            RegexOptions.CultureInvariant);

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static readonly HashSet<string> StructuredItemTitleLeadStopwords = new(StringComparer.Ordinal)
    {
        "add",
        "ajouter",
        "ajoutez",
        "appliquer",
        "appliquez",
        "apply",
        "attendre",
        "avec",
        "check",
        "choisir",
        "choisissez",
        "close",
        "configure",
        "configurer",
        "configurez",
        "connect",
        "copy",
        "copier",
        "dans",
        "during",
        "ensuite",
        "enter",
        "faites",
        "fermer",
        "fermez",
        "install",
        "installer",
        "lancez",
        "laisser",
        "materials",
        "material",
        "method",
        "methode",
        "mettre",
        "open",
        "ouvrir",
        "pendant",
        "place",
        "placer",
        "placez",
        "procedure",
        "procedures",
        "programmer",
        "programmez",
        "puis",
        "remove",
        "remplacer",
        "replace",
        "retirer",
        "run",
        "save",
        "select",
        "set",
        "steps",
        "stop",
        "supprimer",
        "use",
        "utiliser",
        "utilisez",
        "validate",
        "valider",
        "validez",
        "verify",
        "verifier",
        "verifiez",
        "while"
    };

    private const string StructuredItemTitleFirstTokenPattern =
        @"(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{1,}|[\p{Lu}]{2,})";

    private const string StructuredItemTitleNextTokenPattern =
        @"(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{1,}|[\p{Lu}]{2,}|[\p{Ll}][\p{Ll}'\u2019\-]{0,16})";

    private const string StructuredItemTitlePattern =
        StructuredItemTitleFirstTokenPattern + @"(?:\s+" + StructuredItemTitleNextTokenPattern + @"){0,6}?";

    private const string StructuredItemEvidencePattern =
        @"(?i:(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\b|(?:items?|elements?|components?|requirements?|materials?|materiaux|materiel|material|preparation|pr[e\u00e9]paration|preparaci[o\u00f3]n|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|method|methode|steps?|[e\u00e9]tapes?|pasos|passos|schritte|procedure|instructions?)\b|\d{1,3}\s*[\.)]\s+\p{L}|\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h|s|sec|secs|secondes?|seconds?)\b)";

    private static readonly Regex StructuredItemTitleLeadBeforeEvidenceRegex = new(
        @"^\s*(?:\d{1,4}\s*)?(?<title>" + StructuredItemTitlePattern + @")(?=\s*" + StructuredItemEvidencePattern + @")",
        RegexOptions.CultureInvariant);

    private static readonly Regex StructuredItemTitleBeforeEvidenceRegex = new(
        @"(?<![\p{L}\p{N}])(?:\d{1,4}\s*)?(?<title>" + StructuredItemTitlePattern + @")(?=\s*" + StructuredItemEvidencePattern + @")",
        RegexOptions.CultureInvariant);

    private static readonly Regex StructuredUnitBeforeTitleBoundaryRegex = new(
        @"(?:(?<=\d)(?<unit>s|h|g|l)(?=\p{Lu})|(?<=[\d\s])(?<unit>sec|secs|secondes?|seconds?|min|kg|mg|ml|cl|oz|lb|units?|unites?|items?|pieces?|personnes?|people|persons?)(?=\p{Lu}))",
        RegexOptions.CultureInvariant);

    private static readonly Regex LowerOrDigitBeforeStructuredCueRegex = new(
        @"(?<=[\p{Ll}\p{Nd}])(?=(?:Pour|For|Para|Per|Fur|Fuer|Zu|Preparation|Pr[e\u00e9]paration|Realisation|R[e\u00e9]alisation|Materials?|Components?|Requirements?|Instructions?|Procedure|Procedures|Method|Steps?|Etapes?)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LowerBeforeNumberedStepRegex = new(
        @"(?<=[\p{Ll}\p{Lo}])(?=\d{1,3}\s*[\.)]\s+\p{L})",
        RegexOptions.CultureInvariant);

    private static readonly Regex PunctuationBeforeStructuredQuantityRegex = new(
        @"(?<=[\.!?:;\)\]\u00ae])(?=\d+(?:[,.]\d+)?(?:/\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|s|sec|secs|secondes?|seconds?|min|h)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
