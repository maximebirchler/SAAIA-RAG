internal static class PdfTextSanitizer
{
    public static string ForStorage(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text.IndexOf('\0', StringComparison.Ordinal) < 0
            ? text
            : text.Replace('\0', ' ');
    }
}
