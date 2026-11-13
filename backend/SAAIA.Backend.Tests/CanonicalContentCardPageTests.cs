using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalContentCardPageTests
{
    [Theory]
    [InlineData(2, 6, 6)]
    [InlineData(2, 2, 4)]
    public void Card_range_contains_the_heading_and_its_actual_canonical_context(
        int headingPage, int contextStart, int contextEnd)
    {
        const string content = "Inspection requirements\nRecord the measured pressure before opening the isolation valve.";
        var profile = DocumentProfileProjector.Project("Synthetic/Inspection.pdf",
            [new(contextStart, content, 14, content.Length, [1])],
            [new(0, "Inspection requirements", 1, headingPage, contextEnd + 2, null, null)],
            [new(7, 0, contextStart, contextEnd, content, content.Length, 14, [2])], [],
            preferStructuredSources: true);

        var card = Assert.Single(profile.ContentCards);
        Assert.Equal(headingPage, card.PageStart);
        Assert.Equal(contextEnd, card.PageEnd);
        Assert.All(card.Evidence!.Facts!, fact =>
        {
            Assert.InRange(fact.PageStart!.Value, card.PageStart!.Value, card.PageEnd!.Value);
            Assert.InRange(fact.PageEnd!.Value, card.PageStart!.Value, card.PageEnd!.Value);
        });
        Assert.Equal("deterministic_canonical_v6", profile.ProfileVersion);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(9, 10)]
    public void Card_does_not_borrow_a_context_outside_its_canonical_section(int start, int end)
    {
        const string text = "Record the pressure before inspection.";
        var profile = DocumentProfileProjector.Project("Synthetic/Inspection.pdf",
            [new(2, text, 6, text.Length, [1])],
            [new(0, "Inspection requirements", 1, 2, 8, null, null)],
            [new(7, 0, start, end, text, text.Length, 6, [2])], [],
            preferStructuredSources: true);
        Assert.Empty(profile.ContentCards);
    }

    [Fact]
    public void An_unresolved_canonical_card_cannot_promote_a_metadata_excerpt_to_source_body()
    {
        // This helper runs only when the canonical source join returned no chunk.
        const string metadata = """
            {"schemaVersion":"canonical_section_anchor_evidence_v1","facts":[
              {"kind":"canonical_heading","sourceText":"Inspection requirements","pageStart":2,"pageEnd":2},
              {"kind":"canonical_source","label":"retrieval_chunk_index","value":"7","pageStart":6,"pageEnd":6},
              {"kind":"canonical_section_context","sourceText":"Record the pressure.","pageStart":6,"pageEnd":6}]}
            """;
        var text = AdvancedAnalysisEvidenceResolver.BuildGroundedContentCardText("Inspection requirements", metadata);
        Assert.Equal("Inspection requirements", text);
        Assert.Equal("navigation", RetrievalContentClassifier.AnalyzeEvidenceContent(text, "Inspection requirements").ContentRole);
    }
}
