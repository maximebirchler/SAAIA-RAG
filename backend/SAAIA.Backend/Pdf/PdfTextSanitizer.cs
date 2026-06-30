using System.Text.RegularExpressions;

internal static class PdfTextSanitizer
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
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @" *\n *", "\n", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return cleaned;
    }
}
