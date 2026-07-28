using System.Text.RegularExpressions;

internal static partial class RetrievalTextHygiene
{
    public static string RepairHyphenatedCompoundSpacing(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return HyphenatedCompoundSpacingRegex().Replace(
            text,
            "${left}${hyphen}${right}");
    }

    [GeneratedRegex(
        @"(?<left>\p{L}[\p{L}\p{M}]*)(?<hyphen>[-\u2010\u2011])\s+(?<right>\p{Ll}[\p{L}\p{M}]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedCompoundSpacingRegex();
}
