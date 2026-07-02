using System.Text.RegularExpressions;

internal static partial class PdfTextSanitizer
{
    public static string ForStorage(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = text.IndexOf('\0', StringComparison.Ordinal) < 0
            ? text
            : text.Replace('\0', ' ');
        cleaned = TextEncodingSanitizer.RepairCommonMojibake(cleaned);
        cleaned = NormalizeTextLayout(cleaned);
        return cleaned;
    }

    private static string NormalizeTextLayout(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ')
            .Replace('\u1680', ' ')
            .Replace('\u2000', ' ')
            .Replace('\u2001', ' ')
            .Replace('\u2002', ' ')
            .Replace('\u2003', ' ')
            .Replace('\u2004', ' ')
            .Replace('\u2005', ' ')
            .Replace('\u2006', ' ')
            .Replace('\u2007', ' ')
            .Replace('\u2008', ' ')
            .Replace('\u2009', ' ')
            .Replace('\u200a', ' ')
            .Replace('\u202f', ' ')
            .Replace('\u205f', ' ')
            .Replace('\u3000', ' ')
            .Replace("\u00ad", string.Empty, StringComparison.Ordinal);

        cleaned = Regex.Replace(cleaned, @"[\u0001-\u0008\u000B\u000C\u000E-\u001F]+", " ", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"(?<=\p{L})[\-\u2010\u2011\u2012\u2013\u2014]\s*\n\s*(?=\p{Ll})", string.Empty, RegexOptions.CultureInvariant);
        cleaned = RepairFrenchImperativePronounJoins(cleaned);
        cleaned = RepairCommonOcrStandardReferences(cleaned);
        cleaned = RepairCommonOcrEmbeddedSpaces(cleaned);
        cleaned = RepairCommonOcrFusedWords(cleaned);
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @" *\n *", "\n", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return cleaned;
    }

    private static string RepairFrenchImperativePronounJoins(string text)
        => FrenchImperativePronounJoinRegex().Replace(text, static match =>
        {
            var verb = match.Groups["verb"].Value;
            if (LooksLikeFrenchImperativePronounJoinFalsePositive(verb))
                return match.Value;

            return $"{verb}-{match.Groups["pronoun"].Value}";
        });

    private static bool LooksLikeFrenchImperativePronounJoinFalsePositive(string verb)
        => verb.ToLowerInvariant() is "assez" or "chez";

    private static string RepairCommonOcrStandardReferences(string text)
    {
        var repaired = CommonOcrStandardReferencePrefixRegex().Replace(text, static match =>
        {
            var prefix = match.Groups["prefix"].Value;
            var key = prefix.ToLowerInvariant();
            var number = match.Groups["number"].Value;
            var compactNumber = Regex.Replace(number, @"\s+", string.Empty, RegexOptions.CultureInvariant);

            var repairedPrefix = key switch
            {
                "nepa" when compactNumber.StartsWith("70", StringComparison.Ordinal)
                    || compactNumber.StartsWith("79", StringComparison.Ordinal) => "nfpa",
                "ieg" or "tec" => "iec",
                _ => null
            };

            if (repairedPrefix is null)
                return match.Value;

            return ApplyObservedCasing(repairedPrefix, prefix) + match.Groups["sep"].Value + number;
        });

        return CommonOcrStandardHandbookPrefixRegex().Replace(repaired, static match =>
            ApplyObservedCasing("doe", match.Groups["prefix"].Value)
            + match.Groups["sep"].Value
            + match.Groups["rest"].Value);
    }

    private static string RepairCommonOcrEmbeddedSpaces(string text)
        => CommonOcrEmbeddedSpaceWordRegex().Replace(text, static match =>
        {
            var observed = match.Value;
            var key = Regex.Replace(observed, @"\s+", " ", RegexOptions.CultureInvariant).ToLowerInvariant();
            if (!CommonOcrEmbeddedSpaceWordRepairs.TryGetValue(key, out var repaired))
                return match.Value;

            return ApplyObservedCasing(repaired, observed);
        });

    private static string RepairCommonOcrFusedWords(string text)
        => CommonOcrFusedWordRegex().Replace(text, static match =>
        {
            var word = match.Groups["word"].Value;
            var key = word.ToLowerInvariant();
            if (!CommonOcrFusedWordRepairs.TryGetValue(key, out var repaired))
                return match.Value;

            return ApplyObservedCasing(repaired, word);
        });

    private static string ApplyObservedCasing(string repairedLower, string left, string right)
    {
        if (left.All(char.IsUpper) && right.All(char.IsUpper))
            return repairedLower.ToUpperInvariant();

        if (char.IsUpper(left[0]))
            return char.ToUpperInvariant(repairedLower[0]) + repairedLower[1..];

        return repairedLower;
    }

    private static string ApplyObservedCasing(string repairedLower, string observed)
    {
        if (observed.All(char.IsUpper))
            return repairedLower.ToUpperInvariant();

        if (char.IsUpper(observed[0]))
            return char.ToUpperInvariant(repairedLower[0]) + repairedLower[1..];

        return repairedLower;
    }

    private static readonly IReadOnlyDictionary<string, string> CommonOcrEmbeddedSpaceWordRepairs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cop per"] = "copper",
            ["develo per"] = "developer",
            ["jum per"] = "jumper",
            ["opera tion"] = "operation",
            ["pa per"] = "paper",
            ["pro per"] = "proper",
            ["up per"] = "upper"
        };

    private static readonly IReadOnlyDictionary<string, string> CommonOcrFusedWordRepairs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forthe"] = "for the",
            ["groundfault"] = "ground-fault",
            ["andimpacttests"] = "and impact tests",
            ["moldedcase"] = "molded-case",
            ["multiplechoice"] = "multiple-choice",
            ["ofthe"] = "of the",
            ["openended"] = "open-ended",
            ["recommende"] = "recommended",
            ["safetyselated"] = "safety-related",
            ["shortcircuit"] = "short-circuit",
            ["threedimensional"] = "three-dimensional"
        };

    [GeneratedRegex(@"(?<![\p{L}\p{N}\-])(?<verb>\p{L}{3,}ez)(?<pronoun>le|la|les|lui|leur|en|y|nous|vous)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FrenchImperativePronounJoinRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:cop\s+per|develo\s+per|jum\s+per|opera\s+tion|pa\s+per|pro\s+per|up\s+per)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommonOcrEmbeddedSpaceWordRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<word>andimpacttests|forthe|groundfault|moldedcase|multiplechoice|ofthe|openended|recommende|safetyselated|shortcircuit|threedimensional)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommonOcrFusedWordRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<prefix>NEPA|IEG|TEC)(?<sep>[\s._/\-]+)(?<number>\d[\p{L}\p{N}._/\-]*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommonOcrStandardReferencePrefixRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<prefix>DOF)(?<sep>[\s._/\-]+)(?<rest>HDBK)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommonOcrStandardHandbookPrefixRegex();
}
