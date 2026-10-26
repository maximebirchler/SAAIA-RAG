using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("retrieval")]
    [InlineData("units")]
    [InlineData("section_tree")]
    [InlineData("block")]
    [InlineData("table")]
    public void A_proven_layout_disagreement_does_not_assign_merged_left_facts_to_the_last_right_heading(string projection)
    {
        var source = FidelityDocument();
        const string leftHeading = "LEFT COLUMN HEADING";
        const string rightHeading = "RIGHT COLUMN HEADING";
        const string merged = "LEFT ALPHA DETAIL RIGHT ECHO DETAIL LEFT BRAVO DETAIL RIGHT FOXTROT DETAIL";
        AddFidelityText(source, leftHeading, "section_header");
        AddFidelityText(source, rightHeading, "section_header");
        AddFidelityText(source, merged);
        if (projection == "table")
        {
            var table = BuildDocument().Tables[0];
            table.Captions.Clear();
            source.Tables.Add(table);
            source.Body.Children.Add(new() { Ref = table.SelfRef });
        }
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        AddFidelityText(source, "Independent next page subject", "section_header", page: 2);
        const string otherBody = "The next physical page records an independent verified operation and its exact maintenance result.";
        AddFidelityText(source, otherBody, page: 2);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var native = FidelityNative((1, leftHeading + "\n" + rightHeading + "\n" + merged));
        string[] ordered = [leftHeading, "LEFT ALPHA DETAIL", "LEFT BRAVO DETAIL", rightHeading,
            "RIGHT ECHO DETAIL", "RIGHT FOXTROT DETAIL"];
        native.Pages[0] = native.Pages[0] with
        {
            WidthPoints = 100, HeightPoints = 200, NativeLayoutAlgorithm = "test_observed_columns",
            NativeLayoutBlocks = ordered.Select((text, index) => new ExtractedPdfLayoutBlock(index, text,
                index < 3 ? 10 : 60, index < 3 ? 45 : 95, 180 - index % 3 * 20,
                170 - index % 3 * 20, "test_observed_columns")).ToArray()
        };
        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical, native);
        Assert.Equal(1, summary.RecoveredLayoutPageCount);
        var original = Assert.Single(canonical.Pages[0].Blocks, block => block.Text.Canonical == merged);
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Canonical == leftHeading);
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Canonical == rightHeading);

        if (projection == "retrieval")
        {
            var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
                chunk => chunk.ChunkType != DoclingCanonicalRetrievalProjector.NativeLayoutAlternativeChunkType
                    && chunk.Text.Contains(merged, StringComparison.Ordinal));
            Assert.Null(chunk.SectionTitle);
            Assert.Null(chunk.HeadingPath);
            Assert.Null(chunk.SectionOrdinal);
            Assert.Contains("not_asserted", chunk.ChunkComposition ?? "", StringComparison.Ordinal);
            Assert.Contains(original.BlockId, chunk.CanonicalBlockIds!);
        }
        else if (projection == "units")
        {
            var unit = Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source),
                unit => unit.Text == merged);
            Assert.Null(unit.SectionOrdinal);
            Assert.Contains("section_association_not_asserted", unit.ExtractionQualitySignals!);
        }
        else if (projection == "section_tree")
        {
            Assert.All(canonical.SectionTree, section => Assert.DoesNotContain(original.BlockId, section.ContentBlockIds));
        }
        else if (projection == "block")
        {
            Assert.True(CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(original));
            Assert.Equal("not_asserted", original.Provenance.Attributes["sectionAssociation"]);
        }
        else
        {
            var table = Assert.Single(canonical.Pages[0].Tables);
            Assert.Equal(new[] { "A", "B", "C", "D" }, table.Cells.Select(cell => cell.Text.Canonical));
            var tableChunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
                chunk => chunk.ChunkType == DoclingCanonicalRetrievalProjector.TableChunkType);
            Assert.Null(tableChunk.SectionTitle);
            Assert.Null(tableChunk.HeadingPath);
            Assert.NotEmpty(tableChunk.CanonicalTableCellIds!);
        }

        var otherUnit = Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source), unit => unit.Text == otherBody);
        Assert.NotNull(otherUnit.SectionOrdinal);
        var otherChunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
            chunk => chunk.Text.Contains(otherBody, StringComparison.Ordinal));
        Assert.Equal("Independent next page subject", otherChunk.SectionTitle);
    }

    [Fact]
    public void A_page_without_layout_disagreement_keeps_its_explicit_heading_association()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Verified operating instructions", "section_header");
        const string body = "Inspect the installed operating equipment and record the verified maintenance result before service.";
        AddFidelityText(source, body);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var native = FidelityNative((1, "Verified operating instructions\n" + body));
        Assert.Equal(0, CanonicalNativeTextCoverageReconciler.Apply(canonical, native).RecoveredLayoutPageCount);
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1));
        Assert.Equal("Verified operating instructions", chunk.SectionTitle);
        Assert.NotNull(Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source),
            unit => unit.Text == body).SectionOrdinal);
    }
}
