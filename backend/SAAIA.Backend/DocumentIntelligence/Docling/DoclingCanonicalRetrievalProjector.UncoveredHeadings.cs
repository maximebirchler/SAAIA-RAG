internal static partial class DoclingCanonicalRetrievalProjector
{
    internal const string UncoveredHeadingQualityFlag = "canonical_uncovered_observed_heading_context";

    private static void RetainUncoveredObservedHeadings(
        IReadOnlyList<SourceAtom> atoms,
        List<ProjectedRetrievalChunk> chunks,
        int maxWords)
    {
        var uncovered = atoms.Where(atom => atom.IsHeading && !atom.IsNavigation
            && atom.BlockIds.Count > 0 && !chunks.Any(chunk =>
                IngestionWorker.ShouldPublishRetrievalChunk(chunk)
                && chunk.PageStart <= atom.PageNumber && chunk.PageEnd >= atom.PageNumber
                && atom.BlockIds.All(id => (chunk.CanonicalBlockIds ?? [])
                    .Concat(chunk.CanonicalContextBlockIds ?? []).Contains(id, StringComparer.Ordinal))))
            .ToArray();
        if (uncovered.Length == 0)
            return;

        // A heading is an observed source object even when no following body
        // was assigned to it. Preserve that occurrence without inventing a
        // subject relationship or reusing another occurrence's identical text.
        foreach (var heading in uncovered)
        {
            var observation = heading with
            {
                Kind = "unassigned_physical_context",
                IsHeading = false,
                SectionOrdinal = null,
                QualitySignals = heading.QualitySignals.Append(UncoveredHeadingQualityFlag)
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
            foreach (var part in SplitOversizedAtom(observation, maxWords))
            {
                var buffer = new ChunkBuffer(false, RetrievalChunkProjector.PhysicalPageContextChunkType);
                buffer.Add(part);
                chunks.Add(CreateChunk(chunks.Count, buffer));
            }
        }

        // The read/around APIs navigate ChunkIndex. Append-only recovery would
        // place a page-two observation after the last page of the document.
        // Use source-object order, never an inferred title hierarchy.
        var positions = atoms.SelectMany(atom => atom.BlockIds.Concat(atom.TableCellIds)
                .Select(id => (Id: id, atom.GlobalOrder, atom.SubOrder)))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.GlobalOrder).ThenBy(item => item.SubOrder).First(),
                StringComparer.Ordinal);
        var ordered = chunks.Select((chunk, ordinal) =>
        {
            var sourceIds = (chunk.CanonicalBlockIds ?? []).Concat(chunk.CanonicalTableCellIds ?? []);
            var source = sourceIds.Where(positions.ContainsKey).Select(id => positions[id])
                .OrderBy(item => item.GlobalOrder).ThenBy(item => item.SubOrder).ToArray();
            return (Chunk: chunk, Ordinal: ordinal,
                GlobalOrder: source.Length > 0 ? source[0].GlobalOrder : int.MaxValue,
                SubOrder: source.Length > 0 ? source[0].SubOrder : int.MaxValue);
        }).OrderBy(item => item.Chunk.PageStart).ThenBy(item => item.GlobalOrder)
            .ThenBy(item => item.SubOrder).ThenBy(item => item.Ordinal)
            .Select(item => item.Chunk).ToArray();
        chunks.Clear();
        chunks.AddRange(ordered);
    }
}
