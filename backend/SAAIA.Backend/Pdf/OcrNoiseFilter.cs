using System.Text.RegularExpressions;

internal static partial class OcrNoiseFilter
{
    internal static bool LooksLikeProbableNoiseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length < 32)
            return false;

        if (text.Length > 180)
            return LooksLikeProbableLongNoiseText(text);

        return LooksLikeProbableNoiseSegment(text);
    }

    private static bool LooksLikeProbableLongNoiseText(string text)
    {
        const int windowLength = 180;
        const int stepLength = 120;

        var checkedWindows = 0;
        var noisyWindows = 0;
        for (var start = 0; start < text.Length; start += stepLength)
        {
            var length = Math.Min(windowLength, text.Length - start);
            if (length < 32)
                break;

            checkedWindows++;
            if (LooksLikeProbableNoiseSegment(text.Substring(start, length)))
            {
                noisyWindows++;
                if (noisyWindows >= 2)
                    return true;
            }
        }

        return checkedWindows <= 1 && noisyWindows == 1;
    }

    private static bool LooksLikeProbableNoiseSegment(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 4)
            return false;

        var normalizedTokens = tokens
            .Select(static token => token.ToLowerInvariant())
            .ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var hasUsefulTechnicalIdentifier = ContainsUsefulTechnicalIdentifier(text);

        var mixedLetterDigitTokens = normalizedTokens.Count(ContainsLatinLettersAndDigits);
        var longLowVowelTokens = normalizedTokens.Count(static token =>
            token.Length >= 7
            && ContainsLatinLetter(token)
            && LatinLetterVowelRatio(token) < 0.18);
        var longLetterTokens = normalizedTokens.Count(static token =>
            token.Count(char.IsLetter) >= 7);
        var shortTokens = normalizedTokens.Count(static token => token.Length <= 3);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var hyphenatedLongTokens = normalizedTokens.Count(static token =>
            token.Length >= 10
            && token.Contains('-', StringComparison.Ordinal)
            && token.Count(char.IsLetter) >= 8);
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')' and not '-' and not '+');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        var strongSymbolNoiseShape =
            (symbolRatio >= 0.12 && tokens.Length >= 10)
            || (quoteSlashPipeCount >= 5 && symbolRatio >= 0.05)
            || (symbolRatio >= 0.08 && shortTokens >= 8);
        var rotatedTextNoiseShape =
            symbolRatio >= 0.02
            && tokens.Length >= 18
            && shortTokens >= 10
            && suspiciousInternalCaseTokens >= 4;

        if (hasUsefulTechnicalIdentifier && symbolRatio < 0.16 && quoteSlashPipeCount < 8)
            return false;

        if (commonWordCount >= 2 && !strongSymbolNoiseShape && !rotatedTextNoiseShape)
            return false;

        return mixedLetterDigitTokens >= 2
            || longLowVowelTokens >= 2
            || (quoteSlashPipeCount >= 3 && tokens.Length >= 6 && longLetterTokens >= 3 && shortTokens >= 1)
            || (quoteSlashPipeCount >= 3 && hyphenatedLongTokens >= 1 && longLetterTokens >= 2)
            || (symbolRatio >= 0.055 && mixedLetterDigitTokens >= 1)
            || (symbolRatio >= 0.08 && tokens.Length >= 6)
            || (strongSymbolNoiseShape && commonWordCount <= 1 && tokens.Length >= 4)
            || rotatedTextNoiseShape
            || (commonWordCount == 0 && suspiciousInternalCaseTokens >= 4 && shortTokens >= 6);
    }

    private static string TrimToken(string token)
        => token.Trim('\'', '\u2019', '\u2018', '"', '.', ',', ';', ':', '|', '/', '\\');

    private static bool ContainsLatinLettersAndDigits(string token)
        => ContainsLatinLetter(token) && token.Any(char.IsDigit);

    private static bool ContainsUsefulTechnicalIdentifier(string text)
        => TechnicalIdentifierRegex().IsMatch(text);

    private static bool ContainsLatinLetter(string token)
        => token.Any(IsLatinLetter);

    private static bool IsLatinLetter(char ch)
        => ch is >= 'A' and <= 'Z'
           or >= 'a' and <= 'z'
           or >= '\u00c0' and <= '\u024f'
           or >= '\u1e00' and <= '\u1eff';

    private static bool HasSuspiciousInternalCaseSwitch(string token)
    {
        if (token.Length < 4 || !token.Any(char.IsLower) || !token.Any(char.IsUpper))
            return false;

        for (var i = 1; i < token.Length - 1; i++)
        {
            if (char.IsUpper(token[i]) && char.IsLower(token[i - 1]))
                return true;
        }

        return false;
    }

    private static double LatinLetterVowelRatio(string token)
    {
        var letters = token.Count(IsLatinLetter);
        if (letters == 0)
            return 1;

        var vowels = token.Count(ch => IsLatinLetter(ch) && IsVowel(ch));
        return (double)vowels / letters;
    }

    private static bool IsVowel(char ch)
    {
        ch = char.ToLowerInvariant(ch);
        return ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'y'
            or '\u00e0' or '\u00e2' or '\u00e4'
            or '\u00e9' or '\u00e8' or '\u00ea' or '\u00eb'
            or '\u00ee' or '\u00ef'
            or '\u00f4' or '\u00f6'
            or '\u00f9' or '\u00fb' or '\u00fc'
            or '\u00e1' or '\u00ed' or '\u00f3' or '\u00fa' or '\u00fd'
            or '\u00e6' or '\u0153';
    }

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "de", "der", "des", "die", "du",
        "en", "et", "for", "from", "in", "is", "la", "le", "les", "of", "on", "or", "the",
        "to", "und", "with", "au", "aux", "avec", "dans", "pour", "que", "qui", "sur",
        "ne", "pas", "nicht", "no", "not", "non", "sin", "sem", "sans", "con", "com",
        "el", "los", "las", "una", "un", "para", "por", "esta", "este", "y", "o",
        "il", "lo", "gli", "per", "nel", "nella", "sono", "come",
        "uma", "um", "voce", "nao", "se", "si", "este", "esta",
        "das", "ist", "sind", "mit", "fuer", "von",
        "de", "het", "een", "van", "voor", "met", "op",
        "och", "att", "som", "den", "det",
        "og", "af", "til",
        "ja", "on", "se", "tama",
        "ve", "ile", "icin", "bu"
    };

    [GeneratedRegex(@"[\p{L}\p{N}'\u2019\u2018\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\b(?:EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR|TS|PD|BS|NF|SN|UL|CSA)(?:[\s._/\-]+[A-Z]{1,6}){0,4}[\s._/\-]*\d[A-Z0-9._/\-:]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalIdentifierRegex();
}
