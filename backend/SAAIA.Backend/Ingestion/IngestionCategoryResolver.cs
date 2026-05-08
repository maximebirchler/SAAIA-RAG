static class IngestionCategoryResolver
{
    public static string Normalize(string? category)
        => string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : category.Trim().ToLowerInvariant();

    public static string Derive(string docPath, IngestionOptions options)
    {
        if (options.CategoryFromFirstFolder)
        {
            return DeriveFromDocumentPath(docPath);
        }

        return Normalize(options.DefaultCategory);
    }

    public static string DeriveFromDocumentPath(string docPath)
    {
        var normalized = PathUtil.NormalizeRelativePath(docPath);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? Normalize(parts[0]) : string.Empty;
    }
}
