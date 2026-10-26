/// <summary>
/// Rejects source observations that would otherwise disappear during page projection.
/// A missing page is never guessed from a neighbour or the item's parent.
/// </summary>
internal static class DoclingSourceCoverageValidator
{
    internal static void Validate(DoclingDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var unlocatedTexts = source.Texts.Count(item => item.Provenance.Count == 0
            && (!string.IsNullOrWhiteSpace(item.Text) || !string.IsNullOrWhiteSpace(item.OriginalText)));
        var unlocatedTables = source.Tables.Count(item => item.Provenance.Count == 0
            && (item.Data.RowCount > 0 || item.Data.ColumnCount > 0 || item.Data.Cells.Count > 0));
        var unlocatedPictures = source.Pictures.Count(item => item.Provenance.Count == 0);
        if (unlocatedTexts + unlocatedTables + unlocatedPictures > 0)
        {
            // Counts and a stable reason code are safe for diagnostics; do not include PDF text.
            throw new InvalidDataException(
                $"docling_unlocated_source_items texts={unlocatedTexts} tables={unlocatedTables} pictures={unlocatedPictures}");
        }
    }
}
