internal static partial class RetrievalChunkProjector
{
    internal const string PhysicalPageContextChunkType = "physical_page_context_v1";

    private static void AddPhysicalPageContextChunks(List<ProjectedRetrievalChunk> chunks,
        IReadOnlyList<ExtractedDocumentUnit> sourceUnits, int maximumTokens, ref int chunkIndex)
    {
        var originalCount = chunks.Count;
        var covered = BuildPublishableCoverageUnitOrdinals(chunks);
        var pages = sourceUnits.Where(unit => unit.PageStart == unit.PageEnd
                && ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow(unit))
            .GroupBy(unit => unit.PageStart).OrderBy(page => page.Key);
        foreach (var page in pages)
        {
            var units = page.OrderBy(unit => unit.Ordinal).ToArray();
            var bodies = units.Select((unit, index) => (unit, index))
                .Where(item => covered.Contains(item.unit.Ordinal) && IsPhysicalContextBody(item.unit))
                .ToArray();
            if (bodies.Length == 0)
                continue;

            AddPhysicalEdgeContext(chunks, covered, units.Take(bodies[0].index).ToArray(),
                bodies[0].unit, isPrefix: true, maximumTokens, ref chunkIndex);
            AddPhysicalEdgeContext(chunks, covered, units.Skip(bodies[^1].index + 1).ToArray(),
                bodies[^1].unit, isPrefix: false, maximumTokens, ref chunkIndex);
        }
        if (chunks.Count == originalCount)
            return;

        // Page navigation follows ChunkIndex downstream. Added edge context
        // must not send that navigation back to a previously emitted page.
        var physicalOrder = chunks.OrderBy(chunk => chunk.PageStart)
            .ThenBy(chunk => chunk.SourceUnitStartOrdinal ?? int.MaxValue)
            .ThenBy(chunk => chunk.ChunkIndex).ToArray();
        chunks.Clear();
        for (var index = 0; index < physicalOrder.Length; index++)
            chunks.Add(physicalOrder[index] with { ChunkIndex = index });
        chunkIndex = chunks.Count;
    }

    private static bool IsPhysicalContextBody(ExtractedDocumentUnit unit)
        => HasSubstantiveWindowBody(unit)
            && !ExtractionQualityPolicy.ShouldRestrictUnitToTargetedReferences(unit);

    private static void AddPhysicalEdgeContext(List<ProjectedRetrievalChunk> chunks, HashSet<int> covered,
        IReadOnlyList<ExtractedDocumentUnit> edge, ExtractedDocumentUnit body, bool isPrefix,
        int maximumTokens, ref int chunkIndex)
    {
        // Only an edge of the physical page is observed here. Its relation to
        // any section remains unknown; an intervening substantive subject must
        // not be treated as a short qualifier of the selected body.
        var context = edge.Where(HasMeaningfulShortSourceText).ToArray();
        if (context.Length == 0 || context.Any(IsPhysicalContextBody)
            || context.All(unit => covered.Contains(unit.Ordinal)))
            return;

        var batch = new List<ExtractedDocumentUnit>();
        foreach (var unit in context)
        {
            var candidate = BuildPhysicalPageContextChunk(body, batch.Append(unit).ToArray(), isPrefix, chunkIndex);
            if (candidate.TokenCount <= maximumTokens)
            {
                batch.Add(unit);
                continue;
            }

            PublishPhysicalPageContext(chunks, covered, body, batch, isPrefix, maximumTokens, ref chunkIndex);
            batch.Clear();
            candidate = BuildPhysicalPageContextChunk(body, [unit], isPrefix, chunkIndex);
            if (candidate.TokenCount <= maximumTokens)
                batch.Add(unit);
        }
        PublishPhysicalPageContext(chunks, covered, body, batch, isPrefix, maximumTokens, ref chunkIndex);
    }

    private static void PublishPhysicalPageContext(List<ProjectedRetrievalChunk> chunks, HashSet<int> covered,
        ExtractedDocumentUnit body, IReadOnlyList<ExtractedDocumentUnit> context, bool isPrefix,
        int maximumTokens, ref int chunkIndex)
    {
        if (context.Count == 0 || context.All(unit => covered.Contains(unit.Ordinal)))
            return;
        var candidate = BuildPhysicalPageContextChunk(body, context, isPrefix, chunkIndex);
        if (candidate.TokenCount > maximumTokens || !IngestionWorker.ShouldPublishRetrievalChunk(candidate))
            return;
        chunks.Add(candidate);
        chunkIndex++;
        foreach (var ordinal in candidate.SourceUnitOrdinals ?? [])
            covered.Add(ordinal);
    }

    private static ProjectedRetrievalChunk BuildPhysicalPageContextChunk(ExtractedDocumentUnit body,
        IReadOnlyList<ExtractedDocumentUnit> context, bool isPrefix, int chunkIndex)
    {
        var units = isPrefix ? context.Append(body).ToArray() : context.Prepend(body).ToArray();
        return CreateProjectedChunk(chunkIndex, sectionOrdinal: null, unitOrdinal: null,
            body.PageStart, body.PageEnd, string.Join(ChunkSeparator, units.Select(unit => unit.Text)),
            units[0].OffsetStart, units[^1].OffsetEnd, PhysicalPageContextChunkType, units) with
        {
            ChunkType = PhysicalPageContextChunkType,
            ChunkComposition = isPrefix ? "physical_page_context_unassigned_prefix"
                : "physical_page_context_unassigned_suffix"
        };
    }
}
