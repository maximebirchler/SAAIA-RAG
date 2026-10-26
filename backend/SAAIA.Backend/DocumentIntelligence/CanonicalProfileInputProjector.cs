using System.Security.Cryptography;
using System.Text;

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
            .Where(static chunk => chunk.ExtractionQualitySignals?.Contains(
                    CanonicalObservationSelector.AuditOnlySignal, StringComparer.Ordinal) != true
                && (chunk.ChunkType != DoclingCanonicalRetrievalProjector.NativeLayoutAlternativeChunkType
                    || chunk.ExtractionQualitySignals?.Contains(
                        CanonicalObservationSelector.SelectedSupplementSignal, StringComparer.Ordinal) == true))
            .OrderBy(static chunk => chunk.ChunkIndex)
            .Select(static chunk =>
            {
                var text = DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk);
                var literalChanged = text != chunk.Text;
                return new ExtractedDocumentUnit(
                Ordinal: chunk.ChunkIndex,
                SectionOrdinal: chunk.SectionOrdinal,
                PageStart: chunk.PageStart,
                PageEnd: chunk.PageEnd,
                Text: text,
                CharCount: text.Length,
                TokenCount: literalChanged ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length : chunk.TokenCount,
                Checksum: literalChanged ? SHA256.HashData(Encoding.UTF8.GetBytes(text)) : chunk.Checksum,
                OffsetStart: literalChanged ? null : chunk.OffsetStart,
                OffsetEnd: literalChanged ? null : chunk.OffsetEnd,
                ExtractionTextStatus: chunk.ExtractionTextStatus,
                ExtractionTextSparse: chunk.ExtractionTextSparse,
                ExtractionOcrCandidate: chunk.ExtractionOcrCandidate,
                ExtractionQualitySignals: chunk.ExtractionQualitySignals,
                SectionTitle: chunk.SectionTitle,
                HeadingPath: chunk.HeadingPath,
                HeadingLevel: chunk.HeadingLevel);
            })
            .ToArray();
    }
}
