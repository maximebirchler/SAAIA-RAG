using System.Globalization;
using System.Text;

internal static class DocumentLanguageResolver
{
    internal static string? NormalizeLanguageTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        if (string.Equals(normalized, "und", StringComparison.Ordinal))
            return "und";

        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return null;
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return null;
        if (parts.Skip(1).Any(static part => part.Length is < 2 or > 8 || !part.All(char.IsLetterOrDigit)))
            return null;

        return string.Join('-', parts);
    }

    internal static string NormalizeLanguageTagOrUnd(string? language)
        => NormalizeLanguageTag(language) ?? "und";

    internal static string PrimarySubtag(string? language)
    {
        var normalized = NormalizeLanguageTag(language);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    internal static string? FirstKnownLanguage(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeLanguageTag(candidate);
            if (!string.IsNullOrWhiteSpace(normalized) && !string.Equals(normalized, "und", StringComparison.Ordinal))
                return normalized;
        }

        return null;
    }

    internal static string? DetectDominantLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var scriptLanguage = DetectDominantScriptLanguage(text);
        if (!string.IsNullOrWhiteSpace(scriptLanguage))
            return scriptLanguage;

        var normalized = " " + FoldDiacritics(CollapseWhitespace(text)).ToLowerInvariant() + " ";
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var scores = LatinLanguageSignals
            .Select(language => new
            {
                language.Language,
                Score = language.Signals.Count(signal => normalized.Contains(signal, StringComparison.Ordinal))
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Language, StringComparer.Ordinal)
            .ToArray();

        var best = scores.FirstOrDefault();
        if (best is null || best.Score < 2)
            return DetectLatinMorphologyLanguage(normalized);

        var runnerUp = scores.Skip(1).FirstOrDefault()?.Score ?? 0;
        if (best.Score == runnerUp && best.Score < 4)
            return DetectLatinMorphologyLanguage(normalized);

        return best.Language;
    }

    private static string? DetectLatinMorphologyLanguage(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dutchScore = 0;
        foreach (var token in tokens)
        {
            if (token.Contains("heids", StringComparison.Ordinal) || token.EndsWith("heid", StringComparison.Ordinal))
                dutchScore += 2;
            if (token.Contains("ij", StringComparison.Ordinal))
                dutchScore++;
            if (token.StartsWith("onder", StringComparison.Ordinal) && token.Length >= 7)
                dutchScore++;
        }

        return dutchScore >= 2 ? "nl" : null;
    }

    private static string? DetectDominantScriptLanguage(string text)
    {
        var counts = new ScriptCounts();
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                continue;

            counts.TotalLetters++;
            if (IsArabic(ch)) counts.Arabic++;
            else if (IsHebrew(ch)) counts.Hebrew++;
            else if (IsCyrillic(ch)) counts.Cyrillic++;
            else if (IsGreek(ch)) counts.Greek++;
            else if (IsDevanagari(ch)) counts.Devanagari++;
            else if (IsThai(ch)) counts.Thai++;
            else if (IsHangul(ch)) counts.Hangul++;
            else if (IsHiraganaOrKatakana(ch)) counts.Kana++;
            else if (IsHan(ch)) counts.Han++;
        }

        if (counts.TotalLetters == 0)
            return null;

        if (IsDominantScript(counts.Arabic, counts.TotalLetters, minCount: 6)) return "ar";
        if (IsDominantScript(counts.Hebrew, counts.TotalLetters, minCount: 6)) return "he";
        if (IsDominantScript(counts.Greek, counts.TotalLetters, minCount: 6)) return "el";
        if (IsDominantScript(counts.Devanagari, counts.TotalLetters, minCount: 6)) return "hi";
        if (IsDominantScript(counts.Thai, counts.TotalLetters, minCount: 6)) return "th";
        if (IsDominantScript(counts.Hangul, counts.TotalLetters, minCount: 4)) return "ko";
        if (counts.Kana >= 3 && IsDominantScript(counts.Kana + counts.Han, counts.TotalLetters, minCount: 3, minRatio: 0.18d)) return "ja";
        if (IsDominantScript(counts.Han, counts.TotalLetters, minCount: 3, minRatio: 0.18d)) return "zh";
        if (IsDominantScript(counts.Cyrillic, counts.TotalLetters, minCount: 6))
            return DetectCyrillicLanguage(text) ?? "ru";

        return null;
    }

    private static string? DetectCyrillicLanguage(string text)
    {
        var normalized = " " + CollapseWhitespace(text).ToLowerInvariant() + " ";
        var ukrainianHits = CountSignals(normalized, [" що ", " для ", " та ", " це ", " які ", " і "]);
        var russianHits = CountSignals(normalized, [" и ", " в ", " не ", " на ", " что ", " это ", " для "]);
        var ukrainianSpecificLetters = normalized.Count(static ch => ch is 'і' or 'ї' or 'є' or 'ґ');
        if (ukrainianSpecificLetters >= 2)
            ukrainianHits += 2;

        if (ukrainianHits >= 2 && ukrainianHits > russianHits)
            return "uk";

        return russianHits >= 2 ? "ru" : null;
    }

    private static int CountSignals(string normalized, IReadOnlyList<string> signals)
        => signals.Count(signal => normalized.Contains(signal, StringComparison.Ordinal));

    private static bool IsDominantScript(int scriptCount, int totalLetters, int minCount)
        => IsDominantScript(scriptCount, totalLetters, minCount, minRatio: 0.35d);

    private static bool IsDominantScript(int scriptCount, int totalLetters, int minCount, double minRatio)
        => scriptCount >= minCount && scriptCount >= Math.Max(minCount, (int)Math.Ceiling(totalLetters * minRatio));

    private static bool IsArabic(char ch)
        => ch is >= '\u0600' and <= '\u06FF'
            || ch is >= '\u0750' and <= '\u077F'
            || ch is >= '\u08A0' and <= '\u08FF'
            || ch is >= '\uFB50' and <= '\uFDFF'
            || ch is >= '\uFE70' and <= '\uFEFF';

    private static bool IsHebrew(char ch)
        => ch is >= '\u0590' and <= '\u05FF';

    private static bool IsCyrillic(char ch)
        => ch is >= '\u0400' and <= '\u052F'
            || ch is >= '\u2DE0' and <= '\u2DFF'
            || ch is >= '\uA640' and <= '\uA69F';

    private static bool IsGreek(char ch)
        => ch is >= '\u0370' and <= '\u03FF'
            || ch is >= '\u1F00' and <= '\u1FFF';

    private static bool IsDevanagari(char ch)
        => ch is >= '\u0900' and <= '\u097F';

    private static bool IsThai(char ch)
        => ch is >= '\u0E00' and <= '\u0E7F';

    private static bool IsHangul(char ch)
        => ch is >= '\uAC00' and <= '\uD7AF'
            || ch is >= '\u1100' and <= '\u11FF'
            || ch is >= '\u3130' and <= '\u318F';

    private static bool IsHiraganaOrKatakana(char ch)
        => ch is >= '\u3040' and <= '\u30FF'
            || ch is >= '\u31F0' and <= '\u31FF';

    private static bool IsHan(char ch)
        => ch is >= '\u3400' and <= '\u4DBF'
            || ch is >= '\u4E00' and <= '\u9FFF'
            || ch is >= '\uF900' and <= '\uFAFF';

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static readonly (string Language, IReadOnlyList<string> Signals)[] LatinLanguageSignals =
    [
        ("fr", [" le ", " la ", " les ", " des ", " une ", " pour ", " avec ", " dans ", " cette ", " vous ", " nous ", " elle ", " elles ", " leur "]),
        ("en", [" the ", " and ", " with ", " from ", " this ", " that ", " for ", " you ", " your ", " they ", " their ", " shall ", " should "]),
        ("es", [" el ", " los ", " las ", " una ", " para ", " con ", " esta ", " este ", " que ", " por ", " del ", " como ", " sus "]),
        ("pt", [" para ", " com ", " uma ", " esta ", " este ", " que ", " por ", " voce ", " nao ", " sao ", " dos ", " das "]),
        ("de", [" der ", " die ", " das ", " und ", " mit ", " fuer ", " von ", " ist ", " sind ", " nicht ", " diese ", " dieser ", " werden "]),
        ("it", [" il ", " lo ", " gli ", " per ", " con ", " una ", " questo ", " questa ", " che ", " dei ", " delle ", " sono "]),
        ("nl", [" de ", " het ", " een ", " en ", " van ", " voor ", " met ", " deze ", " die ", " dit ", " zijn ", " niet "]),
        ("sv", [" och ", " att ", " med ", " for ", " som ", " den ", " det ", " denna ", " inte ", " vara ", " har "]),
        ("no", [" og ", " med ", " for ", " som ", " den ", " det ", " denne ", " ikke ", " være ", " har "]),
        ("da", [" og ", " med ", " for ", " som ", " den ", " det ", " denne ", " ikke ", " være ", " har "]),
        ("fi", [" ja ", " on ", " se ", " tama ", " kanssa ", " varten ", " ei ", " ovat ", " jota ", " jotka "]),
        ("pl", [" oraz ", " jest ", " dla ", " ten ", " ta ", " tego ", " przez ", " nie ", " oraz ", " ktory "]),
        ("tr", [" ve ", " ile ", " icin ", " bu ", " bir ", " olarak ", " veya ", " degil ", " olan "]),
        ("cs", [" a ", " je ", " pro ", " tento ", " tato ", " ktere ", " jako ", " nebo ", " neni "]),
        ("ro", [" si ", " este ", " pentru ", " acest ", " aceasta ", " care ", " cu ", " din ", " nu "]),
        ("id", [" dan ", " yang ", " untuk ", " dengan ", " ini ", " dari ", " pada ", " tidak ", " sebagai "]),
        ("vi", [" va ", " cua ", " cho ", " voi ", " nay ", " cac ", " khong ", " trong ", " duoc "])
    ];

    private sealed class ScriptCounts
    {
        public int TotalLetters { get; set; }
        public int Arabic { get; set; }
        public int Hebrew { get; set; }
        public int Cyrillic { get; set; }
        public int Greek { get; set; }
        public int Devanagari { get; set; }
        public int Thai { get; set; }
        public int Hangul { get; set; }
        public int Kana { get; set; }
        public int Han { get; set; }
    }
}
