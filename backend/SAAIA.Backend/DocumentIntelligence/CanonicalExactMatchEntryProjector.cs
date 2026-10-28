/// <summary>Canonical exact entries belong to final retrieval chunks, not the independent page/unit view.</summary>
internal static class CanonicalExactMatchEntryProjector
{
    internal static (IReadOnlyList<ExtractedExactMatchEntry> Entries, int NonLiteralCandidates) Project(
        IReadOnlyList<ExtractedDocumentUnit> profileUnits, IReadOnlyList<ProjectedRetrievalChunk> chunks)
    {
        var published = chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToDictionary(chunk => chunk.ChunkIndex);
        if (profileUnits.Any(unit => !published.ContainsKey(unit.Ordinal)))
            throw new InvalidDataException("canonical_exact_profile_chunk_missing");
        var entries = new List<ExtractedExactMatchEntry>();
        var nonLiteral = 0;
        foreach (var entry in ExactMatchEntryExtractor.Extract(profileUnits))
        {
            if (entry.UnitOrdinal is not { } index || !published.TryGetValue(index, out var chunk))
                throw new InvalidDataException("canonical_exact_profile_chunk_missing");
            // Reference aliases that never occur in source are not exact source
            // evidence. Keep the source chunk/RAW unchanged and count the refusal.
            if (!DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk).Contains(entry.Text, StringComparison.Ordinal))
            {
                nonLiteral++;
                continue;
            }
            entries.Add(entry with { EntryIndex = entries.Count, UnitOrdinal = null, SourceRetrievalChunkIndex = index });
        }
        return (entries, nonLiteral);
    }
}
