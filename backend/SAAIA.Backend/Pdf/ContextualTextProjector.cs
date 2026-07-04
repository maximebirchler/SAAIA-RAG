using System.Security.Cryptography;
using System.Text;

internal static class ContextualTextProjector
{
    internal const string SchemaVersion = "contextual_text_v2";

    public static IReadOnlyList<ProjectedContextualTextEntry> Project(
        string docPath,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        if (retrievalChunks.Count == 0)
            return Array.Empty<ProjectedContextualTextEntry>();

        var fileName = Path.GetFileName(docPath);
        var sectionByOrdinal = sections.ToDictionary(section => section.Ordinal);
        var unitsByOrdinal = units.ToDictionary(unit => unit.Ordinal);
        var headingPathBySectionOrdinal = BuildHeadingPathMap(sections);
        var includeNeighborContextByOrdinal = new Dictionary<int, bool>();
        var entries = new List<ProjectedContextualTextEntry>(retrievalChunks.Count);

        foreach (var chunk in retrievalChunks.OrderBy(chunk => chunk.ChunkIndex))
        {
            ExtractedDocumentSection? section = null;
            if (chunk.SectionOrdinal.HasValue)
                sectionByOrdinal.TryGetValue(chunk.SectionOrdinal.Value, out section);

            unitsByOrdinal.TryGetValue(chunk.UnitOrdinal ?? -1, out var unit);
            var previousUnit = ResolveNeighborUnit(unitsByOrdinal, unit, direction: -1);
            var nextUnit = ResolveNeighborUnit(unitsByOrdinal, unit, direction: 1);
            var includeCurrentUnit = ShouldIncludeCurrentUnitContext(unit, chunk);
            var includePreviousUnit = ShouldIncludeNeighborContext(previousUnit, includeNeighborContextByOrdinal);
            var includeNextUnit = ShouldIncludeNeighborContext(nextUnit, includeNeighborContextByOrdinal);
            var headingPath = ResolveHeadingPath(chunk.SectionOrdinal, headingPathBySectionOrdinal);

            var text = BuildContextualText(
                fileName,
                section,
                headingPath,
                chunk,
                unit,
                includeCurrentUnit,
                previousUnit,
                includePreviousUnit,
                nextUnit,
                includeNextUnit);
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
                Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                SchemaVersion: SchemaVersion,
                SectionTitle: section?.Title,
                HeadingPath: headingPath,
                ChunkType: chunk.ChunkType,
                ContentRole: chunk.ContentRole,
                NavigationReason: chunk.NavigationReason,
                NavigationScore: chunk.NavigationScore,
                ContentDensityScore: chunk.ContentDensityScore,
                SourceUnitOrdinals: chunk.SourceUnitOrdinals ?? Array.Empty<int>(),
                SourceUnitStartOrdinal: chunk.SourceUnitStartOrdinal,
                SourceUnitEndOrdinal: chunk.SourceUnitEndOrdinal,
                SourceUnitCount: chunk.SourceUnitCount,
                ChunkComposition: chunk.ChunkComposition,
                IncludesCurrentUnitContext: includeCurrentUnit,
                IncludesPreviousContext: includePreviousUnit,
                IncludesNextContext: includeNextUnit));
        }

        return entries;
    }

    private static string BuildContextualText(
        string fileName,
        ExtractedDocumentSection? section,
        string? headingPath,
        ProjectedRetrievalChunk chunk,
        ExtractedDocumentUnit? unit,
        bool includeCurrentUnit,
        ExtractedDocumentUnit? previousUnit,
        bool includePreviousUnit,
        ExtractedDocumentUnit? nextUnit,
        bool includeNextUnit)
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

        sb.AppendLine("excerpt:");
        sb.AppendLine(chunk.Text.Trim());
        sb.AppendLine();

        if (includeCurrentUnit)
        {
            sb.AppendLine("context:");
            sb.AppendLine(unit!.Text.Trim());
            sb.AppendLine();
        }

        if (includePreviousUnit)
        {
            sb.AppendLine("previous_context:");
            sb.AppendLine(previousUnit!.Text.Trim());
            sb.AppendLine();
        }

        if (includeNextUnit)
        {
            sb.AppendLine("next_context:");
            sb.AppendLine(nextUnit!.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static bool ShouldIncludeCurrentUnitContext(ExtractedDocumentUnit? unit, ProjectedRetrievalChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(unit?.Text))
            return false;

        if (TextEquals(unit.Text, chunk.Text))
            return false;

        return ShouldIncludeUnitTextContext(unit);
    }

    private static bool TextEquals(string left, string right)
        => string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.Ordinal);

    private static string NormalizeForComparison(string text)
        => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool ShouldIncludeNeighborContext(
        ExtractedDocumentUnit? unit,
        Dictionary<int, bool> includeNeighborContextByOrdinal)
    {
        if (string.IsNullOrWhiteSpace(unit?.Text))
            return false;

        if (includeNeighborContextByOrdinal.TryGetValue(unit.Ordinal, out var cached))
            return cached;

        return CacheIncludeNeighborContext(includeNeighborContextByOrdinal, unit.Ordinal, ShouldIncludeUnitTextContext(unit));
    }

    private static bool ShouldIncludeUnitTextContext(ExtractedDocumentUnit unit)
    {
        var text = unit.Text.Trim();
        if (OcrNoiseFilter.LooksLikeProbableNoiseText(text))
            return false;

        if (!ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow(unit))
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

    private static bool CacheIncludeNeighborContext(Dictionary<int, bool> cache, int unitOrdinal, bool value)
    {
        cache[unitOrdinal] = value;
        return value;
    }

    private static ExtractedDocumentUnit? ResolveNeighborUnit(
        IReadOnlyDictionary<int, ExtractedDocumentUnit> unitsByOrdinal,
        ExtractedDocumentUnit? current,
        int direction)
    {
        if (current is null)
            return null;

        if (!unitsByOrdinal.TryGetValue(current.Ordinal + direction, out var neighbor))
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
    byte[] Checksum,
    string SchemaVersion = ContextualTextProjector.SchemaVersion,
    string? SectionTitle = null,
    string? HeadingPath = null,
    string? ChunkType = null,
    string? ContentRole = null,
    string? NavigationReason = null,
    double NavigationScore = 0.0,
    double ContentDensityScore = 0.0,
    IReadOnlyList<int>? SourceUnitOrdinals = null,
    int? SourceUnitStartOrdinal = null,
    int? SourceUnitEndOrdinal = null,
    int? SourceUnitCount = null,
    string? ChunkComposition = null,
    bool IncludesCurrentUnitContext = false,
    bool IncludesPreviousContext = false,
    bool IncludesNextContext = false);
