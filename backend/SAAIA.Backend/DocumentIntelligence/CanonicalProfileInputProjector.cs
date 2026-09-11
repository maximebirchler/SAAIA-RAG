internal static class CanonicalProfileInputProjector
{
    public static IReadOnlyList<ExtractedDocumentUnit> Project(
        IReadOnlyList<ProjectedRetrievalChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        return chunks
            .Where(static chunk =>
                string.Equals(
                    chunk.ContentRole,
                    RetrievalContentClassifier.ContentRole,
                    StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(chunk.Text))
            .OrderBy(static chunk => chunk.ChunkIndex)
            .Select(static chunk => new ExtractedDocumentUnit(
                Ordinal: chunk.ChunkIndex,
                SectionOrdinal: chunk.SectionOrdinal,
                PageStart: chunk.PageStart,
                PageEnd: chunk.PageEnd,
                Text: chunk.Text,
                CharCount: chunk.Text.Length,
                TokenCount: chunk.TokenCount,
                Checksum: chunk.Checksum,
                OffsetStart: chunk.OffsetStart,
                OffsetEnd: chunk.OffsetEnd,
                ExtractionTextStatus: chunk.ExtractionTextStatus,
                ExtractionTextSparse: chunk.ExtractionTextSparse,
                ExtractionOcrCandidate: chunk.ExtractionOcrCandidate,
                ExtractionQualitySignals: chunk.ExtractionQualitySignals,
                SectionTitle: chunk.SectionTitle,
                HeadingPath: chunk.HeadingPath,
                HeadingLevel: chunk.HeadingLevel))
            .ToArray();
    }
}
