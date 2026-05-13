using System.Security.Cryptography;
using System.Text;

internal static class ContextualTextProjector
{
    public static IReadOnlyList<ProjectedContextualTextEntry> Project(
        string docPath,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        if (retrievalChunks.Count == 0)
            return Array.Empty<ProjectedContextualTextEntry>();

        var fileName = Path.GetFileName(docPath);
        var unitsByOrdinal = units.ToDictionary(unit => unit.Ordinal);
        var headingPathBySectionOrdinal = BuildHeadingPathMap(sections);
        var entries = new List<ProjectedContextualTextEntry>(retrievalChunks.Count);

        foreach (var chunk in retrievalChunks.OrderBy(chunk => chunk.ChunkIndex))
        {
            var section = sections.FirstOrDefault(section => section.Ordinal == chunk.SectionOrdinal);
            unitsByOrdinal.TryGetValue(chunk.UnitOrdinal ?? -1, out var unit);
            var previousUnit = ResolveNeighborUnit(units, chunk.UnitOrdinal, direction: -1);
            var nextUnit = ResolveNeighborUnit(units, chunk.UnitOrdinal, direction: 1);
            var headingPath = ResolveHeadingPath(chunk.SectionOrdinal, headingPathBySectionOrdinal);

            var text = BuildContextualText(fileName, section, headingPath, chunk, unit, previousUnit, nextUnit);
            entries.Add(new ProjectedContextualTextEntry(
                EntryIndex: chunk.ChunkIndex,
                SectionOrdinal: chunk.SectionOrdinal,
                UnitOrdinal: chunk.UnitOrdinal,
                ChunkIndex: chunk.ChunkIndex,
                PageStart: chunk.PageStart,
                PageEnd: chunk.PageEnd,
                Text: text,
                CharCount: text.Length,
                TokenCount: CountTokens(text),
                Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }

        return entries;
    }

    private static string BuildContextualText(
        string fileName,
        ExtractedDocumentSection? section,
        string? headingPath,
        ProjectedRetrievalChunk chunk,
        ExtractedDocumentUnit? unit,
        ExtractedDocumentUnit? previousUnit,
        ExtractedDocumentUnit? nextUnit)
    {
        var sb = new StringBuilder();
        sb.Append("document_name: ").Append(fileName).AppendLine();
        if (!string.IsNullOrWhiteSpace(section?.Title))
            sb.Append("section_title: ").Append(section.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(headingPath))
            sb.Append("heading_path: ").Append(headingPath).AppendLine();
        sb.Append("chunk_type: ").Append(chunk.ChunkType).AppendLine();
        sb.Append("pages: ").Append(chunk.PageStart);
        if (chunk.PageEnd != chunk.PageStart)
            sb.Append('-').Append(chunk.PageEnd);
        sb.AppendLine();
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(unit?.Text))
        {
            sb.AppendLine("context:");
            sb.AppendLine(unit.Text.Trim());
            sb.AppendLine();
        }

        if (ShouldIncludeNeighborContext(previousUnit))
        {
            sb.AppendLine("previous_context:");
            sb.AppendLine(previousUnit!.Text.Trim());
            sb.AppendLine();
        }

        if (ShouldIncludeNeighborContext(nextUnit))
        {
            sb.AppendLine("next_context:");
            sb.AppendLine(nextUnit!.Text.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("excerpt:");
        sb.Append(chunk.Text.Trim());
        return sb.ToString().TrimEnd();
    }

    private static bool ShouldIncludeNeighborContext(ExtractedDocumentUnit? unit)
    {
        if (string.IsNullOrWhiteSpace(unit?.Text))
            return false;

        var text = unit.Text.Trim();
        if (OcrNoiseFilter.LooksLikeProbableNoiseText(text))
            return false;

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        if (string.Equals(signal.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.Ordinal))
            return false;

        if (string.Equals(signal.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.Ordinal)
            && signal.NavigationScore >= 0.72
            && signal.ContentDensityScore < 0.55)
        {
            return false;
        }

        return true;
    }

    private static ExtractedDocumentUnit? ResolveNeighborUnit(
        IReadOnlyList<ExtractedDocumentUnit> units,
        int? unitOrdinal,
        int direction)
    {
        if (!unitOrdinal.HasValue)
            return null;

        var current = units.FirstOrDefault(unit => unit.Ordinal == unitOrdinal.Value);
        if (current is null)
            return null;

        var targetOrdinal = current.Ordinal + direction;
        var neighbor = units.FirstOrDefault(unit => unit.Ordinal == targetOrdinal);
        if (neighbor is null)
            return null;

        return neighbor.SectionOrdinal == current.SectionOrdinal
            ? neighbor
            : null;
    }

    internal static Dictionary<int, string> BuildHeadingPathMap(IReadOnlyList<ExtractedDocumentSection> sections)
    {
        var paths = new Dictionary<int, string>();
        var stack = new List<ExtractedDocumentSection>();

        foreach (var section in sections.OrderBy(section => section.Ordinal))
        {
            while (stack.Count > 0 && stack[^1].Level >= section.Level)
                stack.RemoveAt(stack.Count - 1);

            stack.Add(section);
            paths[section.Ordinal] = string.Join(" > ", stack.Select(item => item.Title));
        }

        return paths;
    }

    internal static string? ResolveHeadingPath(int? sectionOrdinal, IReadOnlyDictionary<int, string> headingPathBySectionOrdinal)
    {
        if (!sectionOrdinal.HasValue)
            return null;

        return headingPathBySectionOrdinal.TryGetValue(sectionOrdinal.Value, out var headingPath)
            ? headingPath
            : null;
    }

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed record ProjectedContextualTextEntry(
    int EntryIndex,
    int? SectionOrdinal,
    int? UnitOrdinal,
    int ChunkIndex,
    int PageStart,
    int PageEnd,
    string Text,
    int CharCount,
    int TokenCount,
    byte[] Checksum);
