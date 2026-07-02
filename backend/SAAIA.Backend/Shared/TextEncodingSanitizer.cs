using System.Text;

internal static class TextEncodingSanitizer
{
    private const char ReplacementCharacter = '\uFFFD';
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly (string Mojibake, string Replacement)[] CommonMojibakeReplacements =
    [
        ("\u00c3\u0080", "\u00c0"),
        ("\u00c3\u0081", "\u00c1"),
        ("\u00c3\u0082", "\u00c2"),
        ("\u00c3\u0087", "\u00c7"),
        ("\u00c3\u0088", "\u00c8"),
        ("\u00c3\u0089", "\u00c9"),
        ("\u00c3\u008a", "\u00ca"),
        ("\u00c3\u008b", "\u00cb"),
        ("\u00c3\u0094", "\u00d4"),
        ("\u00c3\u0099", "\u00d9"),
        ("\u00c3\u009b", "\u00db"),
        ("\u00c3\u00a0", "\u00e0"),
        ("\u00c3\u00a1", "\u00e1"),
        ("\u00c3\u00a2", "\u00e2"),
        ("\u00c3\u00a3", "\u00e3"),
        ("\u00c3\u00a4", "\u00e4"),
        ("\u00c3\u00a7", "\u00e7"),
        ("\u00c3\u00a8", "\u00e8"),
        ("\u00c3\u00a9", "\u00e9"),
        ("\u00c3\u00aa", "\u00ea"),
        ("\u00c3\u00ab", "\u00eb"),
        ("\u00c3\u00ad", "\u00ed"),
        ("\u00c3\u00ae", "\u00ee"),
        ("\u00c3\u00af", "\u00ef"),
        ("\u00c3\u00b1", "\u00f1"),
        ("\u00c3\u00b3", "\u00f3"),
        ("\u00c3\u00b4", "\u00f4"),
        ("\u00c3\u00b5", "\u00f5"),
        ("\u00c3\u00b6", "\u00f6"),
        ("\u00c3\u00b9", "\u00f9"),
        ("\u00c3\u00ba", "\u00fa"),
        ("\u00c3\u00bb", "\u00fb"),
        ("\u00c3\u00bc", "\u00fc"),
        ("\u00c2\u00a0", " "),
        ("\u00c2\u00ab", "\u00ab"),
        ("\u00c2\u00bb", "\u00bb"),
        ("\u00c2\u00b0", "\u00b0"),
        ("\u00c2\u00b2", "\u00b2"),
        ("\u00c2\u00b3", "\u00b3"),
        ("\u00c2\u00b7", "\u00b7"),
        ("\u00c5\u0092", "\u0152"),
        ("\u00c5\u0093", "\u0153"),
        ("\u00e2\u20ac\u00a2", "\u2022"),
        ("\u00e2\u20ac\u00a6", "\u2026"),
        ("\u00e2\u20ac\u02dc", "\u2018"),
        ("\u00e2\u20ac\u2122", "\u2019"),
        ("\u00e2\u20ac\u0153", "\u201c"),
        ("\u00e2\u20ac\u009d", "\u201d"),
        ("\u00e2\u20ac\u201c", "\u2013"),
        ("\u00e2\u20ac\u201d", "\u2014"),
        ("\u00e2\u0080\u00a2", "\u2022"),
        ("\u00e2\u0080\u00a6", "\u2026"),
        ("\u00e2\u0080\u0098", "\u2018"),
        ("\u00e2\u0080\u0099", "\u2019"),
        ("\u00e2\u0080\u009c", "\u201c"),
        ("\u00e2\u0080\u009d", "\u201d"),
        ("\u00e2\u0080\u0093", "\u2013"),
        ("\u00e2\u0080\u0094", "\u2014")
    ];

    private static readonly IReadOnlyDictionary<char, byte> Windows1252ExtendedBytes = new Dictionary<char, byte>
    {
        ['\u20ac'] = 0x80,
        ['\u201a'] = 0x82,
        ['\u0192'] = 0x83,
        ['\u201e'] = 0x84,
        ['\u2026'] = 0x85,
        ['\u2020'] = 0x86,
        ['\u2021'] = 0x87,
        ['\u02c6'] = 0x88,
        ['\u2030'] = 0x89,
        ['\u0160'] = 0x8A,
        ['\u2039'] = 0x8B,
        ['\u0152'] = 0x8C,
        ['\u017d'] = 0x8E,
        ['\u2018'] = 0x91,
        ['\u2019'] = 0x92,
        ['\u201c'] = 0x93,
        ['\u201d'] = 0x94,
        ['\u2022'] = 0x95,
        ['\u2013'] = 0x96,
        ['\u2014'] = 0x97,
        ['\u02dc'] = 0x98,
        ['\u2122'] = 0x99,
        ['\u0161'] = 0x9A,
        ['\u203a'] = 0x9B,
        ['\u0153'] = 0x9C,
        ['\u017e'] = 0x9E,
        ['\u0178'] = 0x9F
    };

    internal static string RepairCommonMojibake(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var repaired = text;
        for (var pass = 0; pass < 4; pass++)
        {
            var before = repaired;
            if (TryRepairUtf8DecodedAsWindows1252(repaired, out var transcoded)
                && IsBetterMojibakeRepair(repaired, transcoded))
            {
                repaired = transcoded;
            }

            if (MayContainCommonMojibake(repaired))
            {
                foreach (var (mojibake, replacement) in CommonMojibakeReplacements)
                    repaired = repaired.Replace(mojibake, replacement, StringComparison.Ordinal);
            }

            if (string.Equals(before, repaired, StringComparison.Ordinal))
                break;
        }

        return repaired.Contains(ReplacementCharacter, StringComparison.Ordinal)
            ? RepairReplacementCharacters(repaired)
            : repaired;
    }

    private static bool MayContainCommonMojibake(string text)
        => text.Contains('\u00c2', StringComparison.Ordinal)
           || text.Contains('\u00c3', StringComparison.Ordinal)
           || text.Contains('\u00e2', StringComparison.Ordinal);

    private static bool TryRepairUtf8DecodedAsWindows1252(string text, out string repaired)
    {
        repaired = text;
        if (!MayContainCommonMojibake(text))
            return false;

        var bytes = new List<byte>(text.Length);
        foreach (var ch in text)
        {
            if (ch <= byte.MaxValue)
            {
                bytes.Add((byte)ch);
                continue;
            }

            if (Windows1252ExtendedBytes.TryGetValue(ch, out var mapped))
            {
                bytes.Add(mapped);
                continue;
            }

            return false;
        }

        try
        {
            repaired = StrictUtf8.GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            repaired = text;
            return false;
        }
    }

    private static bool IsBetterMojibakeRepair(string original, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var originalScore = ComputeMojibakeScore(original);
        var candidateScore = ComputeMojibakeScore(candidate);
        if (candidateScore >= originalScore)
            return false;

        return CountReplacementCharacters(candidate) <= CountReplacementCharacters(original);
    }

    private static int ComputeMojibakeScore(string text)
    {
        var score = CountReplacementCharacters(text) * 5;
        foreach (var ch in text)
        {
            score += ch switch
            {
                '\u00c2' or '\u00c3' or '\u00e2' => 2,
                '\u20ac' or '\u2122' => 1,
                _ => 0
            };
            if (Windows1252ExtendedBytes.ContainsKey(ch))
                score++;
        }

        return score;
    }

    private static int CountReplacementCharacters(string text)
        => text.Count(static ch => ch == ReplacementCharacter);

    private static string RepairReplacementCharacters(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != ReplacementCharacter)
            {
                builder.Append(ch);
                continue;
            }

            var previous = i > 0 ? text[i - 1] : '\0';
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (IsLowerLatinLetter(previous) && IsLowerLatinLetter(next))
                builder.Append("ti");
            else
                builder.Append(' ');
        }

        return builder.ToString();
    }

    private static bool IsLowerLatinLetter(char ch)
        => ch is >= 'a' and <= 'z';
}
