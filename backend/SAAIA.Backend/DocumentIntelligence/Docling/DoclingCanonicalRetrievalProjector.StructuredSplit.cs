using System.Security.Cryptography;
using System.Text;

internal static partial class DoclingCanonicalRetrievalProjector
{
    internal static bool IsStructuredChunk(ProjectedRetrievalChunk chunk)
        => string.Equals(chunk.ChunkType, TableChunkType, StringComparison.Ordinal)
           || string.Equals(chunk.OriginalChunkType, TableChunkType, StringComparison.Ordinal);

    /// <summary>
    /// Halves a table between source-row groups that no rowspan crosses. The
    /// caller must re-count each complete resulting input with its tokenizer.
    /// Null means an observed cell/relationship is indivisible; a recognized
    /// structured chunk must never fall back to splitting characters or words.
    /// </summary>
    internal static IReadOnlyList<ProjectedRetrievalChunk>? TrySplitStructuredChunk(ProjectedRetrievalChunk chunk)
    {
        if (!IsStructuredChunk(chunk) || chunk.StructuredTableProjection is not { } plan
            || plan.ContentGroups.Count < 2) return null;
        var middle = (plan.ContentGroups.Count + 1) / 2;
        return new[]
        {
            CreateStructuredFragment(chunk, plan with { ContentGroups = plan.ContentGroups.Take(middle).ToArray() }),
            CreateStructuredFragment(chunk, plan with { ContentGroups = plan.ContentGroups.Skip(middle).ToArray() })
        };
    }

    private static ProjectedRetrievalChunk CreateStructuredFragment(
        ProjectedRetrievalChunk chunk, CanonicalTableChunkProjection plan)
    {
        var text = RenderTableChunkText(plan);
        var cells = plan.HeaderGroups.Concat(plan.ContentGroups).SelectMany(group => group.Cells).ToArray();
        // Repeated column headers keep their original source IDs; content links
        // belong only to cells retained in the fragment. Heading context links
        // remain explicit, separate from the source content links.
        return chunk with
        {
            Text = text,
            TokenCount = CountTokens(text),
            Checksum = SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            CanonicalTableCellIds = Distinct(cells.Select(cell => cell.CellId)),
            CanonicalBlockIds = Distinct(cells.SelectMany(cell => cell.BlockIds)),
            CanonicalSpanIds = Distinct(cells.SelectMany(cell => cell.SpanIds)),
            StructuredTableProjection = plan,
            ExtractionQualitySignals = Distinct((chunk.ExtractionQualitySignals ?? [])
                .Append("canonical_table_tokenizer_structural_split_v1"))
        };
    }
}
