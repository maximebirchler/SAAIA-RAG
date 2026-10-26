using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Observed_heading_coverage_keeps_a_peer_replaced_before_any_body_as_physical_context()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Equipment operating catalogue", "section_header");
        const string observed = "Document identity and revision marker";
        AddFidelityText(source, observed, "section_header");
        AddFidelityText(source, "Maintenance index", "section_header");
        AddFidelityText(source, "Inspect the installed equipment and record its verified operating condition before maintenance.");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1);

        var retained = Assert.Single(chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk),
            chunk => chunk.Text.Contains(observed, StringComparison.Ordinal));
        AssertUnassignedObservedHeading(canonical, observed, retained);
        Assert.Contains(CanonicalDocumentSectionProjector.Project(canonical), section => section.Title == observed);
    }

    [Fact]
    public void Observed_heading_coverage_keeps_a_trailing_title_before_only_unassigned_native_recovery()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Verified operating procedure", "section_header");
        const string body = "Inspect the installed equipment and record its verified operating condition before maintenance.";
        AddFidelityText(source, body);
        const string observed = "Calibration ending note preserved verbatim";
        AddFidelityText(source, observed, "section_header");
        const string recovery = "Residual pressure must be checked before releasing the upper equipment cover.";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        CanonicalNativeTextCoverageReconciler.Apply(canonical,
            FidelityNative((1, "Verified operating procedure\n" + body + "\n" + observed + "\n" + recovery)));

        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1);

        var retained = Assert.Single(chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk),
            chunk => chunk.Text.Contains(observed, StringComparison.Ordinal));
        AssertUnassignedObservedHeading(canonical, observed, retained);
        var recovered = Assert.Single(chunks, chunk => chunk.Text.Contains(recovery, StringComparison.Ordinal));
        Assert.Null(recovered.SectionTitle);
        Assert.DoesNotContain(observed, recovered.Text, StringComparison.Ordinal);
        Assert.Equal("Verified operating procedure", Assert.Single(chunks,
            chunk => chunk.Text.Contains(body, StringComparison.Ordinal)).SectionTitle);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Observed_heading_coverage_preserves_a_title_only_page_within_the_normal_word_budget(int maxWords)
    {
        var source = FidelityDocument();
        const string observed = "Calibration record reference identifying the current operating equipment revision";
        AddFidelityText(source, observed, "section_header");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = Assert.Single(canonical.Pages[0].Blocks);

        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, maxWords, 1);

        Assert.NotEmpty(chunks);
        Assert.Equal(observed, string.Join(" ", chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.TokenCount <= maxWords);
            Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(chunk));
            Assert.Contains("canonical_uncovered_observed_heading_context", chunk.ExtractionQualitySignals!);
            Assert.Contains(block.BlockId, chunk.CanonicalBlockIds!);
            Assert.Null(chunk.SectionTitle);
            Assert.Null(chunk.SectionOrdinal);
            Assert.Equal(1, chunk.PageStart);
            Assert.Equal(1, chunk.PageEnd);
        });
    }

    [Fact]
    public void Observed_heading_coverage_does_not_duplicate_a_title_already_used_as_context()
    {
        var source = FidelityDocument();
        const string observed = "Verified operating procedure";
        AddFidelityText(source, observed, "section_header");
        AddFidelityText(source, "Inspect the installed equipment and record its verified operating condition before maintenance.");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1);

        var chunk = Assert.Single(chunks);
        Assert.Equal(observed, chunk.SectionTitle);
        Assert.Single(chunk.CanonicalContextBlockIds!);
    }

    [Fact]
    public void Observed_heading_coverage_does_not_turn_a_proven_physical_page_counter_into_a_title()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "1", "page_footer", furniture: true);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        Assert.Empty(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Observed_heading_coverage_tracks_distinct_source_occurrences_on_their_actual_page(bool samePage)
    {
        var source = FidelityDocument();
        const string observed = "Repeated source calibration heading";
        if (samePage) AddFidelityText(source, "Verified document catalogue", "section_header");
        var first = AddFidelityText(source, observed, "section_header");
        if (!samePage) source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        var second = AddFidelityText(source, observed, "section_header", page: samePage ? 1 : 2);
        AddFidelityText(source, "Inspect the installed equipment and record its verified operating condition before maintenance.",
            page: samePage ? 1 : 2);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var firstBlock = Assert.Single(canonical.Pages[0].Blocks,
            block => block.Provenance.Attributes["doclingSelfRef"] == first.SelfRef);
        var secondBlock = Assert.Single(canonical.Pages.SelectMany(page => page.Blocks),
            block => block.Provenance.Attributes["doclingSelfRef"] == second.SelfRef);

        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1)
            .Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        var firstChunk = Assert.Single(chunks, chunk => (chunk.CanonicalBlockIds ?? []).Contains(firstBlock.BlockId));
        Assert.Equal(1, firstChunk.PageStart);
        Assert.Equal(1, firstChunk.PageEnd);
        Assert.Contains(chunks, chunk => (chunk.CanonicalContextBlockIds ?? []).Contains(secondBlock.BlockId)
            && chunk.PageStart == (samePage ? 1 : 2));
        Assert.Null(firstChunk.SectionTitle);
        Assert.Contains("canonical_uncovered_observed_heading_context", firstChunk.ExtractionQualitySignals!);
    }

    private static void AssertUnassignedObservedHeading(SAAIA.Contracts.DocumentIntelligence.CanonicalDocument canonical,
        string observed, ProjectedRetrievalChunk retained)
    {
        var block = Assert.Single(canonical.Pages[0].Blocks, block => block.Text.Canonical == observed);
        Assert.Contains(block.BlockId, retained.CanonicalBlockIds!);
        Assert.Equal(RetrievalChunkProjector.PhysicalPageContextChunkType, retained.ChunkType);
        Assert.Contains("not_asserted", retained.ChunkComposition ?? "", StringComparison.Ordinal);
        Assert.Contains("canonical_uncovered_observed_heading_context", retained.ExtractionQualitySignals!);
        Assert.Null(retained.SectionTitle);
        Assert.Null(retained.HeadingPath);
        Assert.Null(retained.SectionOrdinal);
        Assert.Equal(1, retained.PageStart);
        Assert.Equal(1, retained.PageEnd);
    }
}
