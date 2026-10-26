using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Structured_table_projection_uses_its_explicit_caption_in_every_fragment_instead_of_prior_heading()
    {
        const string priorTitle = "Previous independent topic";
        const string captionText = "Attached measurement legend";
        var cells = new List<DoclingTableCell>
        {
            Cell(0, 0, "Key", Box(10, 100, 50, 105, "TOPLEFT"), columnHeader: true),
            Cell(0, 1, "Value", Box(50, 100, 90, 105, "TOPLEFT"), columnHeader: true)
        };
        for (var row = 1; row < 9; row++)
        {
            cells.Add(Cell(row, 0, $"Reference marker {row} retained", Box(10, 100 + row * 5, 50, 105 + row * 5, "TOPLEFT")));
            cells.Add(Cell(row, 1, $"Recorded limit {row} unchanged", Box(50, 100 + row * 5, 90, 105 + row * 5, "TOPLEFT")));
        }
        var source = StructuredTableSource(9, 2, cells.ToArray());
        source.Texts[0].Text = source.Texts[0].OriginalText = priorTitle;
        source.Texts[2].Text = source.Texts[2].OriginalText = captionText;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var page = Assert.Single(canonical.Pages);
        var table = Assert.Single(page.Tables);
        var caption = Assert.Single(page.Blocks, block => block.Text.Retrieval == captionText);
        var prior = Assert.Single(page.Blocks, block => block.Text.Retrieval == priorTitle);

        // The relationship already exists in the source graph. This test asks
        // projection to honor it, not to infer topics from words or proximity.
        Assert.Equal(new[] { caption.BlockId }, table.CaptionBlockIds);
        Assert.Contains(canonical.Relations, relation => relation.RelationType == "caption_for"
            && relation.SourceId == caption.BlockId && relation.TargetId == table.TableId);

        var chunks = TableChunks(canonical, source, maxWords: 16);
        Assert.True(chunks.Count > 1, "The caption must survive actual row-group fragmentation.");
        Assert.All(chunks, chunk =>
        {
            Assert.Contains(captionText, chunk.Text, StringComparison.Ordinal);
            Assert.Contains(caption.BlockId, chunk.CanonicalContextBlockIds!);
            Assert.DoesNotContain(prior.BlockId, chunk.CanonicalContextBlockIds!);
            Assert.NotEqual(priorTitle, chunk.SectionTitle);
            Assert.DoesNotContain(priorTitle, chunk.HeadingPath ?? "", StringComparison.Ordinal);
            Assert.Null(chunk.SectionTitle);
            Assert.Null(chunk.SectionOrdinal);
            Assert.Null(chunk.HeadingPath);
            Assert.Contains(captionText, DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk), StringComparison.Ordinal);
            Assert.DoesNotContain(priorTitle, DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk), StringComparison.Ordinal);
            Assert.Contains("canonical_table_explicit_caption_context", chunk.ExtractionQualitySignals!);
            Assert.NotEmpty(chunk.CanonicalTableCellIds!);
            Assert.DoesNotContain(caption.BlockId, chunk.CanonicalBlockIds!);
            Assert.Equal(table.TableId, chunk.StructuredTableProjection!.TableId);
        });
        Assert.Equal(table.Cells.Select(cell => cell.CellId).Order(),
            chunks.SelectMany(chunk => chunk.CanonicalTableCellIds!).Distinct().Order());
    }

    [Fact]
    public void Structured_table_projection_does_not_borrow_an_unlinked_caption_as_table_context()
    {
        const string unlinkedCaptionText = "Unlinked instrument illustration legend";
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "Key", Box(10, 100, 50, 110, "TOPLEFT"), columnHeader: true),
            Cell(0, 1, "Value", Box(50, 100, 90, 110, "TOPLEFT"), columnHeader: true),
            Cell(1, 0, "Marker", Box(10, 110, 50, 120, "TOPLEFT")),
            Cell(1, 1, "-5", Box(50, 110, 90, 120, "TOPLEFT")));
        source.Tables[0].Captions.Clear();
        source.Texts[2].Text = source.Texts[2].OriginalText = unlinkedCaptionText;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var page = Assert.Single(canonical.Pages);
        var table = Assert.Single(page.Tables);
        var unlinked = Assert.Single(page.Blocks, block => block.Text.Retrieval == unlinkedCaptionText);

        Assert.Empty(table.CaptionBlockIds);
        Assert.DoesNotContain(canonical.Relations, relation => relation.RelationType == "caption_for"
            && relation.SourceId == unlinked.BlockId && relation.TargetId == table.TableId);
        var chunk = Assert.Single(TableChunks(canonical, source));
        Assert.DoesNotContain(unlinkedCaptionText, chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(unlinked.BlockId, chunk.CanonicalContextBlockIds!);
        Assert.NotEqual(unlinkedCaptionText, chunk.SectionTitle);
        Assert.DoesNotContain(unlinkedCaptionText, chunk.HeadingPath ?? "", StringComparison.Ordinal);
        Assert.Equal(4, chunk.CanonicalTableCellIds!.Count);
        Assert.Contains(page.Blocks, block => block.BlockId == unlinked.BlockId);
        Assert.Null(chunk.SectionTitle);
        Assert.Null(chunk.SectionOrdinal);
        Assert.Null(chunk.HeadingPath);
        Assert.Empty(chunk.CanonicalContextBlockIds!);
        Assert.Contains("canonical_table_context_unassigned", chunk.ExtractionQualitySignals!);
    }

    [Fact]
    public void Structured_table_projection_does_not_import_a_linked_caption_from_another_physical_page()
    {
        const string foreignCaptionText = "Caption observed on another page";
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "Key", Box(10, 100, 50, 110, "TOPLEFT"), columnHeader: true),
            Cell(0, 1, "Value", Box(50, 100, 90, 110, "TOPLEFT"), columnHeader: true),
            Cell(1, 0, "Marker", Box(10, 110, 50, 120, "TOPLEFT")),
            Cell(1, 1, "-5", Box(50, 110, 90, 120, "TOPLEFT")));
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        source.Texts[2].Text = source.Texts[2].OriginalText = foreignCaptionText;
        source.Texts[2].Provenance[0].PageNumber = 2;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var tablePage = Assert.Single(canonical.Pages, page => page.PageNumber == 1);
        var foreignPage = Assert.Single(canonical.Pages, page => page.PageNumber == 2);
        var table = Assert.Single(tablePage.Tables);
        var caption = Assert.Single(foreignPage.Blocks, block => block.Text.Retrieval == foreignCaptionText);
        Assert.Equal(new[] { caption.BlockId }, table.CaptionBlockIds);
        Assert.Contains(canonical.Relations, relation => relation.RelationType == "caption_for"
            && relation.SourceId == caption.BlockId && relation.TargetId == table.TableId);

        var chunk = Assert.Single(TableChunks(canonical, source));
        Assert.Equal(1, chunk.PageStart);
        Assert.Equal(1, chunk.PageEnd);
        Assert.DoesNotContain(foreignCaptionText, chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignCaptionText, DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk), StringComparison.Ordinal);
        Assert.DoesNotContain(caption.BlockId, chunk.CanonicalContextBlockIds!);
        Assert.Null(chunk.SectionTitle);
        Assert.Null(chunk.HeadingPath);
        Assert.Contains("canonical_table_linked_caption_unavailable_on_page", chunk.ExtractionQualitySignals!);
        Assert.Contains("canonical_table_context_unassigned", chunk.ExtractionQualitySignals!);
        Assert.Equal(4, chunk.CanonicalTableCellIds!.Count);
        Assert.Contains(foreignPage.Blocks, block => block.BlockId == caption.BlockId);
    }
}
