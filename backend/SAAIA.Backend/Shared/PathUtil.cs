static class PathUtil
{
    public static string NormalizeRelativePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.Replace('\\', '/').Trim();
        while (s.StartsWith("/")) s = s.Substring(1);

        if (s.Contains(".."))
            throw new InvalidOperationException("docPath must be a relative path without '..'");

        return s;
    }
}
