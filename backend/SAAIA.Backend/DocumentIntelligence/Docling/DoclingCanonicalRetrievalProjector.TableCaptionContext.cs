using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DoclingCanonicalRetrievalProjector
{
    private const string TableCaptionContextSignal = "canonical_table_explicit_caption_context";
    private const string TableContextUnassignedSignal = "canonical_table_context_unassigned";
    private const string TableCaptionUnavailableSignal = "canonical_table_linked_caption_unavailable_on_page";

    private static IReadOnlyList<SourceAtom> ResolveTableCaptionContext(
        CanonicalTable table, CanonicalPage page, bool navigation)
    {
        var captionIds = table.CaptionBlockIds.ToHashSet(StringComparer.Ordinal);
        if (captionIds.Count == 0) return [];

        // CaptionBlockIds are document-wide references. Resolve them on the
        // table's physical page; a reference on another page cannot silently
        // become local context. Keep the immutable canonical objects intact.
        return page.Blocks.Where(block => captionIds.Contains(block.BlockId)
                && !string.IsNullOrWhiteSpace(block.Text.Retrieval)
                && !CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(block))
            .OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal)
            .Select(block => new SourceAtom(
                block.ReadingOrder, block.Ordinal, page.PageNumber,
                NormalizeText(PdfTextSanitizer.ForStorage(block.Text.Retrieval)),
                "table_caption", block.BlockId, navigation, false, 1, null, false,
                [block.BlockId], block.Spans.Select(span => span.SpanId).ToArray(), [],
                CanonicalSourceQuality.Combine(page, block), IsTableCaption: true))
            .ToArray();
    }
}
