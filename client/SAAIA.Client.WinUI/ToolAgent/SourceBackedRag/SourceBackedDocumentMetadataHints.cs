using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedDocumentMetadataHints
{
    public static string BuildNearestPathSegments(
        EvidenceItem item,
        int maximumSegments = 4)
    {
        var path = string.IsNullOrWhiteSpace(item.DocPath)
            ? item.DocName
            : item.DocPath;
        var segments = SplitPathSegments(path).ToArray();
        if (segments.Length <= 1 || maximumSegments <= 0)
            return string.Empty;

        return string.Join(
            " > ",
            segments
                .Take(segments.Length - 1)
                .Reverse()
                .Take(maximumSegments));
    }

    public static void AppendDocumentFamilyHints(
        StringBuilder builder,
        IEnumerable<EvidenceItem> evidence,
        int maximumItems,
        int maximumSegments = 4)
    {
        var items = evidence.Take(Math.Max(0, maximumItems)).ToArray();
        if (items.Length == 0)
            return;

        builder.AppendLine("DOCUMENT_FAMILY_METADATA_HINTS:");
        foreach (var item in items)
        {
            var nearestSegments = BuildNearestPathSegments(
                item,
                maximumSegments);
            builder.Append("- [")
                .Append(item.EvidenceId)
                .Append("] fileName: ")
                .Append(Trim(item.DocName, 120))
                .Append("; nearestPathSegments: ")
                .AppendLine(Trim(nearestSegments, 220));
        }

        builder.AppendLine(
            "DOCUMENT_FAMILY_METADATA_RULE: the path segments above are ordered "
            + "mechanically from the folder nearest the file to the broadest folder. "
            + "The LLM decides their meaning and final wording; prefer the nearest "
            + "meaningful document-type segment or source title over a broad corpus, "
            + "vendor or topic segment.");
    }

    private static IEnumerable<string> SplitPathSegments(string? path)
        => (path ?? string.Empty)
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment));

    private static string Trim(string? value, int maximumCharacters)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters].TrimEnd() + "...";
    }
}
