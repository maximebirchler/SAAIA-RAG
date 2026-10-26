using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalNativeCoverageProvenanceTests
{
    private const string UnresolvedRole = "literal_native_coverage_unresolved";
    private const string UnresolvedFlag = "canonical_native_literal_coverage_unresolved";

    [Theory]
    [InlineData("The manufac- remains liable.\nturer ture")]
    [InlineData("Do not open the vessel at -5 °C.")]
    public void Uncovered_native_literal_is_retained_without_asserting_a_new_semantic_fact(string native)
    {
        var document = NativeContinuationFixture.Document(
            "The manufacturer remains liable.", "The manufacture remains audited.");
        NativeContinuationFixture.Reconcile(document, native);

        var blocks = document.Pages[0].Blocks.Where(block => block.BlockType == "native_text_recovery").ToArray();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            Assert.Equal(UnresolvedRole, block.Provenance.Attributes["selectionRole"]);
            Assert.Equal("not_asserted", block.Provenance.Attributes["semanticNovelty"]);
            Assert.Equal("whole_observation_occurrence_inventory_v6", block.Provenance.Attributes["coverageAlgorithm"]);
            Assert.Contains(UnresolvedFlag, block.QualityFlags);
        }
        Assert.Contains(NativeContinuationFixture.Units(document), unit =>
            unit.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(native, StringComparison.Ordinal)
            && (unit.ExtractionQualitySignals ?? []).Contains(UnresolvedFlag, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(UnresolvedRole)]
    [InlineData("verified_native_source_gap")]
    public void Current_and_legacy_literal_roles_preserve_an_additional_same_word_occurrence_and_its_quality_signal(string role)
    {
        const string primary = "Open A then B.";
        const string additional = "Open B then A.";
        var document = NativeContinuationFixture.Document(primary);
        var source = NativeContinuationFixture.Block("native_extra", "native_text_recovery", additional, 1);
        source.Provenance.Attributes["selectionRole"] = role;
        source.Provenance.Attributes["semanticNovelty"] = "not_asserted";
        source.QualityFlags.Add(UnresolvedFlag);
        document.Pages[0].Blocks.Add(source);

        var units = NativeContinuationFixture.Units(document);

        Assert.Equal(new[] { primary, additional }, units.Select(unit => unit.Text));
        Assert.Contains(UnresolvedFlag, units[1].ExtractionQualitySignals ?? []);
        Assert.Equal(additional, source.Text.Raw);
        Assert.Equal(role, source.Provenance.Attributes["selectionRole"]);
        var selected = Assert.Single(CanonicalObservationSelector.SelectPage(document.Pages[0]).Blocks,
            block => block.BlockId == source.BlockId);
        Assert.Equal(role, selected.Provenance.Attributes["sourceObservationSelectionRole"]);
        Assert.Equal("not_asserted", selected.Provenance.Attributes["semanticNovelty"]);
    }
}
