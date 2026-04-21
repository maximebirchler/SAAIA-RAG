using System.Security.Cryptography;
using System.Text;

internal static class RetrievalChunkProjector
{
    private static readonly string ChunkSeparator = Environment.NewLine + Environment.NewLine;

    public static IReadOnlyList<ProjectedRetrievalChunk> Project(
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (chunks.Count == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        var projected = new List<ProjectedRetrievalChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var section = sections
                .Where(s => Overlaps(chunk.PageStart, chunk.PageEnd, s.PageStart, s.PageEnd))
                .OrderByDescending(s => OverlapScore(chunk.PageStart, chunk.PageEnd, s.PageStart, s.PageEnd))
                .ThenBy(s => s.Ordinal)
                .FirstOrDefault();

            var unit = units
                .Where(u => Overlaps(chunk.PageStart, chunk.PageEnd, u.PageStart, u.PageEnd))
                .OrderByDescending(u => OverlapScore(chunk.PageStart, chunk.PageEnd, u.PageStart, u.PageEnd))
                .ThenBy(u => Distance(chunk.ChunkIndex, u.Ordinal))
                .FirstOrDefault();

            projected.Add(CreateProjectedChunk(
                chunk.ChunkIndex,
                section?.Ordinal,
                unit?.Ordinal,
                chunk.PageStart,
                chunk.PageEnd,
                chunk.Text,
                ResolveExcerptOffsetStart(unit, chunk.Text),
                ResolveExcerptOffsetEnd(unit, chunk.Text),
                chunkType: "legacy_word_window_v1"));
        }

        return projected;
    }

    public static IReadOnlyList<ProjectedRetrievalChunk> ProjectStructureAware(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        int maxWords,
        int overlapWords,
        int minWords)
    {
        if (units.Count == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        maxWords = Math.Max(1, maxWords);
        overlapWords = Math.Max(0, overlapWords);
        minWords = Math.Max(1, minWords);

        var orderedUnits = units
            .OrderBy(unit => unit.SectionOrdinal ?? int.MaxValue)
            .ThenBy(unit => unit.PageStart)
            .ThenBy(unit => unit.Ordinal)
            .ToList();

        var chunks = new List<ProjectedRetrievalChunk>();
        var chunkIndex = 0;

        foreach (var sectionGroup in orderedUnits.GroupBy(unit => unit.SectionOrdinal))
        {
            var sectionUnits = sectionGroup
                .OrderBy(unit => unit.PageStart)
                .ThenBy(unit => unit.Ordinal)
                .ToList();

            if (sectionUnits.Count == 0)
                continue;

            var start = 0;
            while (start < sectionUnits.Count)
            {
                var window = new List<ExtractedDocumentUnit>();
                var tokenTotal = 0;
                var cursor = start;

                while (cursor < sectionUnits.Count)
                {
                    var candidate = sectionUnits[cursor];
                    var candidateTokens = Math.Max(1, candidate.TokenCount);

                    if (window.Count > 0 && tokenTotal + candidateTokens > maxWords)
                        break;

                    window.Add(candidate);
                    tokenTotal += candidateTokens;
                    cursor++;
                }

                if (window.Count == 0)
                {
                    window.Add(sectionUnits[start]);
                    cursor = start + 1;
                    tokenTotal = Math.Max(1, sectionUnits[start].TokenCount);
                }

                if (tokenTotal >= minWords || chunks.Count == 0)
                {
                    var first = window[0];
                    var last = window[^1];
                    var chunkText = string.Join(ChunkSeparator, window.Select(unit => unit.Text));
                    int? representativeUnit = window.Count == 1
                        ? first.Ordinal
                        : null;

                    chunks.Add(CreateProjectedChunk(
                        chunkIndex++,
                        first.SectionOrdinal,
                        representativeUnit,
                        first.PageStart,
                        last.PageEnd,
                        chunkText,
                        first.OffsetStart,
                        last.OffsetEnd,
                        chunkType: window.Count == 1 ? "unit_exact_v1" : "section_window_v1"));
                }

                if (cursor >= sectionUnits.Count)
                    break;

                start = ComputeNextStart(sectionUnits, start, cursor, overlapWords);
            }
        }

        if (chunks.Count == 0)
        {
            var fallback = orderedUnits;
            var text = string.Join(ChunkSeparator, fallback.Select(unit => unit.Text));
            var first = fallback[0];
            var last = fallback[^1];
            chunks.Add(CreateProjectedChunk(
                0,
                first.SectionOrdinal,
                fallback.Count == 1 ? first.Ordinal : null,
                first.PageStart,
                last.PageEnd,
                text,
                first.OffsetStart,
                last.OffsetEnd,
                chunkType: "document_window_v1"));
        }

        return chunks;
    }

    private static int ComputeNextStart(
        IReadOnlyList<ExtractedDocumentUnit> units,
        int currentStart,
        int currentEndExclusive,
        int overlapWords)
    {
        if (overlapWords <= 0)
            return currentEndExclusive;

        var overlapTokenCount = 0;
        var nextStart = currentEndExclusive;
        for (var i = currentEndExclusive - 1; i >= currentStart; i--)
        {
            overlapTokenCount += Math.Max(1, units[i].TokenCount);
            nextStart = i;
            if (overlapTokenCount >= overlapWords)
                break;
        }

        return nextStart >= currentEndExclusive
            ? currentEndExclusive
            : Math.Max(nextStart, currentStart + 1);
    }

    private static ProjectedRetrievalChunk CreateProjectedChunk(
        int chunkIndex,
        int? sectionOrdinal,
        int? unitOrdinal,
        int pageStart,
        int pageEnd,
        string text,
        int? offsetStart,
        int? offsetEnd,
        string chunkType)
        => new(
            ChunkIndex: chunkIndex,
            SectionOrdinal: sectionOrdinal,
            UnitOrdinal: unitOrdinal,
            PageStart: pageStart,
            PageEnd: pageEnd,
            Text: text,
            TokenCount: CountTokens(text),
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            ChunkType: chunkType,
            OffsetStart: offsetStart,
            OffsetEnd: offsetEnd);

    private static int? ResolveExcerptOffsetStart(ExtractedDocumentUnit? unit, string excerpt)
    {
        if (unit?.OffsetStart is null || string.IsNullOrWhiteSpace(unit.Text) || string.IsNullOrWhiteSpace(excerpt))
            return null;

        var index = unit.Text.IndexOf(excerpt, StringComparison.Ordinal);
        return index >= 0
            ? unit.OffsetStart.Value + index
            : null;
    }

    private static int? ResolveExcerptOffsetEnd(ExtractedDocumentUnit? unit, string excerpt)
    {
        var start = ResolveExcerptOffsetStart(unit, excerpt);
        return start is null
            ? null
            : start.Value + excerpt.Length;
    }

    private static bool Overlaps(int startA, int endA, int startB, int endB)
        => startA <= endB && startB <= endA;

    private static int OverlapScore(int startA, int endA, int startB, int endB)
        => Math.Max(0, Math.Min(endA, endB) - Math.Max(startA, startB) + 1);

    private static int Distance(int left, int right)
        => Math.Abs(left - right);

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed record ProjectedRetrievalChunk(
    int ChunkIndex,
    int? SectionOrdinal,
    int? UnitOrdinal,
    int PageStart,
    int PageEnd,
    string Text,
    int TokenCount,
    byte[] Checksum,
    string ChunkType,
    int? OffsetStart = null,
    int? OffsetEnd = null);
