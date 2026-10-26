using System.Text.RegularExpressions;

internal static class PdfTextSanitizer
{
    /// <summary>Storage normalization must not guess words, identifiers or unknown glyphs.</summary>
    public static string ForStorage(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        // The PDF/OCR decoder already supplied Unicode. Even a reversible re-encoding
        // would reinterpret a document that literally demonstrates those character sequences.
        var cleaned = text.Replace('\0', ' ');
        cleaned = cleaned.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        cleaned = Regex.Replace(cleaned, @"[\p{Zs}\t]+", " ", RegexOptions.CultureInvariant);
        // U+00AD marks discretionary hyphenation. Hard hyphens remain source evidence.
        cleaned = cleaned.Replace("\u00ad", string.Empty, StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"[\u0001-\u0008\u000B\u000C\u000E-\u001F]+", " ", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @" *\n *", "\n", RegexOptions.CultureInvariant);
        return Regex.Replace(cleaned, @"\n{4,}", "\n\n\n", RegexOptions.CultureInvariant);
    }
}
