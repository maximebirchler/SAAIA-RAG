internal static class TextEncodingSanitizer
{
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
        ("\u00e2\u20ac\u00a2", "\u2022"),
        ("\u00e2\u20ac\u00a6", "\u2026"),
        ("\u00e2\u20ac\u02dc", "\u2018"),
        ("\u00e2\u20ac\u2122", "\u2019"),
        ("\u00e2\u20ac\u0153", "\u201c"),
        ("\u00e2\u20ac\u009d", "\u201d"),
        ("\u00e2\u20ac\u201c", "\u2013"),
        ("\u00e2\u20ac\u201d", "\u2014")
    ];

    internal static string RepairCommonMojibake(string text)
    {
        if (string.IsNullOrEmpty(text) || !MayContainCommonMojibake(text))
            return text;

        var repaired = text;
        foreach (var (mojibake, replacement) in CommonMojibakeReplacements)
            repaired = repaired.Replace(mojibake, replacement, StringComparison.Ordinal);

        return repaired;
    }

    private static bool MayContainCommonMojibake(string text)
        => text.Contains('\u00c2', StringComparison.Ordinal)
           || text.Contains('\u00c3', StringComparison.Ordinal)
           || text.Contains('\u00e2', StringComparison.Ordinal);
}
