using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("formula")]
    [InlineData("code")]
    public void Untranscribed_structured_region_remains_visible_when_only_neighboring_prose_is_indexed(string kind)
    {
        const string prose = "The procedure refers to a structured expression on this physical page. Inspect that source region before interpreting its symbols.";
        const string raw = "a i / b j = -12.5";
        var source = FidelityDocument();
        AddFidelityText(source, prose);
        var structured = AddFidelityText(source, raw, kind);
        structured.Text = "";
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = CanonicalContractJson.SerializeCanonical(document);

        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(document, source, 500, 1));

        Assert.Contains("page_contains_untranscribed_" + kind, chunk.ExtractionQualitySignals!);
        Assert.DoesNotContain("observed_block_type_" + kind, chunk.ExtractionQualitySignals!);
        Assert.Equal(prose, chunk.Text);
        var retained = Assert.Single(document.Pages[0].Blocks, block => block.BlockType == kind);
        Assert.Equal(raw, retained.Text.Raw);
        Assert.Empty(retained.Text.Retrieval);
        Assert.NotNull(retained.Polygon);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Theory]
    [InlineData("formula", "a / b = -12.5")]
    [InlineData("code", "return -12.5;")]
    [InlineData("text", "")]
    public void Transcribed_structure_or_empty_ordinary_text_does_not_announce_missing_structured_transcription(string kind, string text)
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Readable source prose remains independently searchable on this page.");
        AddFidelityText(source, text, kind);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var chunks = DoclingCanonicalRetrievalProjector.Project(document, source, 500, 1);

        Assert.All(chunks, chunk => Assert.DoesNotContain(chunk.ExtractionQualitySignals!,
            signal => signal.StartsWith("page_contains_untranscribed_", StringComparison.Ordinal)));
    }
}
