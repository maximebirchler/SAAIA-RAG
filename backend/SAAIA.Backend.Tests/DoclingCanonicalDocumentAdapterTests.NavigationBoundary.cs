using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void Navigation_only_pages_end_inherited_heading_context_but_mixed_pages_do_not(bool mixedBody, int navigationPages)
    {
        var source = FidelityDocument();
        const string heading = "Source overview";
        const string following = "Independent body text after the navigation pages.";
        AddFidelityText(source, heading, "section_header", top: 195, bottom: 185);
        for (var page = 1; page <= navigationPages; page++)
        {
            source.Pages[page.ToString()] = new() { PageNumber = page, Size = new() { Width = 100, Height = 200 } };
            var table = new DoclingTableItem
            {
                SelfRef = $"#/tables/{page - 1}", Parent = new() { Ref = "#/body" },
                Label = "document_index", ContentLayer = "body",
                Provenance = [Provenance(Box(10, 170, 90, 100, "BOTTOMLEFT"), page)],
                Data = new() { RowCount = 1, ColumnCount = 2, Cells = [
                    Cell(0, 0, "Topic " + page, Box(10, 160, 60, 140, "BOTTOMLEFT")),
                    Cell(0, 1, "12", Box(60, 160, 90, 140, "BOTTOMLEFT"))] }
            };
            source.Tables.Add(table);
            source.Body.Children.Add(new() { Ref = table.SelfRef });
            if (mixedBody) AddFidelityText(source, "Body paragraph independent from the local navigation table.", page: page, top: 80, bottom: 60);
        }
        var followingPage = navigationPages + 1;
        source.Pages[followingPage.ToString()] = new() { PageNumber = followingPage, Size = new() { Width = 100, Height = 200 } };
        AddFidelityText(source, following, page: followingPage);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1);
        var body = Assert.Single(chunks, chunk => chunk.PageStart == followingPage);
        Assert.Equal(navigationPages, chunks.Count(chunk => chunk.ContentRole == RetrievalContentClassifier.NavigationRole));
        if (mixedBody)
        {
            Assert.Equal(heading, body.SectionTitle);
            Assert.Contains(following, body.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(following, body.Text);
            Assert.Null(body.SectionTitle);
            Assert.Null(body.SectionOrdinal);
            Assert.Empty(body.CanonicalContextBlockIds!);
            Assert.Null(Assert.Single(CanonicalProfileInputProjector.Project([body])).SectionOrdinal);
            Assert.Null(Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source), unit => unit.PageStart == followingPage).SectionOrdinal);
            Assert.Contains(chunks, chunk => chunk.Text == heading);
        }
    }
}
