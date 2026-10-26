using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalNativeTableCoveragePolicyTests
{
    [Theory]
    [InlineData(2, 100, "page_word_limit")]
    [InlineData(100, 2, "document_word_limit")]
    public void Native_audit_refuses_word_budgets_without_reporting_a_zero_missing_count(int pageLimit, int documentLimit, string reason)
    {
        using var source = new NativeTableCoverageFixture(true);
        var document = source.Canonical();
        var result = CanonicalNativeTableCoverageAuditor.Apply(document, source.Extraction, default, pageLimit, documentLimit);

        Assert.Equal(1, result.RefusedTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        AssertUnknown(document, "refused", reason);
    }

    [Theory]
    [InlineData("partial", "invalid_word_geometry")]
    [InlineData("refused", "page_word_limit")]
    [InlineData("unavailable", "native_word_extraction_error")]
    public void Partial_refused_and_unavailable_observations_never_become_successful_empty_evidence(string availability, string reason)
    {
        using var source = new NativeTableCoverageFixture(true);
        var original = source.Extraction.Pages[0];
        var native = source.Extraction with { Pages = [original with
            { NativeWordObservation = original.NativeWordObservation! with { Availability = availability, Reason = reason } }] };
        var document = source.Canonical();

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, native);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        AssertUnknown(document, availability, reason);
    }

    [Fact]
    public void A_different_pdf_identity_refuses_geometric_comparison_and_keeps_source_cells()
    {
        using var source = new NativeTableCoverageFixture(true);
        var original = source.Extraction.Pages[0];
        var native = source.Extraction with { Pages = [original with
            { NativeWordObservation = original.NativeWordObservation! with { SourceSha256 = new string('b', 64) } }] };
        var document = source.Canonical();

        CanonicalNativeTableCoverageAuditor.Apply(document, native);

        AssertUnknown(document, "unavailable", "source_identity_mismatch");
        Assert.Equal(new[] { "HEADER", "UPPER" }, document.Pages[0].Tables[0].Cells.Select(cell => cell.Text.Raw));
    }

    [Theory]
    [InlineData("rotation", "unsupported_rotation")]
    [InlineData("dimensions", "invalid_page_geometry")]
    [InlineData("nonfinite", "invalid_page_geometry")]
    [InlineData("missing_width", "invalid_page_geometry")]
    [InlineData("missing_height", "invalid_page_geometry")]
    [InlineData("zero_width", "invalid_page_geometry")]
    [InlineData("cell", "invalid_cell_geometry")]
    [InlineData("word", "invalid_word_geometry")]
    [InlineData("polygon", "invalid_table_geometry")]
    public void Unsupported_geometry_is_explicitly_unavailable_instead_of_a_false_zero(string mutation, string reason)
    {
        using var source = new NativeTableCoverageFixture(true);
        var document = source.Canonical();
        var native = source.Extraction;
        if (mutation == "rotation") document.Pages[0].Geometry.RotationDegrees = 90;
        if (mutation == "dimensions") native = native with { Pages = [native.Pages[0] with { WidthPoints = 603 }] };
        if (mutation == "nonfinite") native = native with { Pages = [native.Pages[0] with { WidthPoints = double.NaN }] };
        if (mutation == "missing_width") document.Pages[0].Geometry.Width = null;
        if (mutation == "missing_height") document.Pages[0].Geometry.Height = null;
        if (mutation == "zero_width") document.Pages[0].Geometry.Width = 0;
        if (mutation == "cell") document.Pages[0].Tables[0].Cells[0].Polygon = null;
        if (mutation == "polygon") document.Pages[0].Tables[0].Polygon!.Points.RemoveAt(0);
        if (mutation == "word") native = native with { Pages = [native.Pages[0] with
            { NativeWordObservation = native.Pages[0].NativeWordObservation! with
                { Words = [new(0, "LOWER", double.NaN, 90, 410, 400)] } }] };

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, native);

        Assert.Equal(0, result.AuditedTableCount);
        Assert.Null(result.UnrepresentedWordCount);
        AssertUnknown(document, "unavailable", reason);
    }

    [Fact]
    public void Geometry_warning_does_not_claim_text_loss_when_the_same_word_already_exists_in_a_cell()
    {
        using var source = new NativeTableCoverageFixture(true, lowerText: "UPPER");
        var document = source.Canonical();

        var result = CanonicalNativeTableCoverageAuditor.Apply(document, source.Extraction);

        Assert.Equal(1, result.UnresolvedTableCount);
        Assert.Equal("not_asserted", document.Pages[0].Tables[0].Provenance.Attributes["nativeTableCoverageTextLoss"]);
        Assert.Equal("not_asserted", document.Pages[0].Tables[0].Provenance.Attributes["nativeTableCoverageRelations"]);
        Assert.Equal(new[] { "HEADER", "UPPER" }, document.Pages[0].Tables[0].Cells.Select(cell => cell.Text.Raw));
    }

    [Fact]
    public void Repeated_audit_is_deterministic_and_keeps_a_valid_zero_limited_to_observed_native_words()
    {
        using var source = new NativeTableCoverageFixture(false);
        var document = source.Canonical();
        var first = CanonicalNativeTableCoverageAuditor.Apply(document, source.Extraction);
        var attributes = document.Pages[0].Tables[0].Provenance.Attributes;
        var digest = attributes["nativeTableCoverageEvidenceSha256"];
        var second = CanonicalNativeTableCoverageAuditor.Apply(document, source.Extraction);

        Assert.Equal(0, first.UnrepresentedWordCount);
        Assert.Equal(0, second.UnrepresentedWordCount);
        Assert.Equal(digest, attributes["nativeTableCoverageEvidenceSha256"]);
        Assert.Matches("^[a-f0-9]{64}$", digest);
        Assert.Equal("not_asserted", attributes["nativeTableCoverageFidelity"]);
    }

    [Fact]
    public void Audit_checks_cancellation_before_it_mutates_canonical_provenance()
    {
        using var source = new NativeTableCoverageFixture(true);
        var document = source.Canonical();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CanonicalNativeTableCoverageAuditor.Apply(document, source.Extraction, cancellation.Token));
        Assert.DoesNotContain(document.Pages[0].Tables[0].Provenance.Attributes.Keys,
            key => key.StartsWith("nativeTableCoverage", StringComparison.Ordinal));
    }

    private static void AssertUnknown(CanonicalDocument document, string availability, string reason)
    {
        var attributes = document.Pages[0].Tables[0].Provenance.Attributes;
        Assert.Equal(availability, attributes["nativeTableCoverageAvailability"]);
        Assert.Equal(reason, attributes["nativeTableCoverageOutcome"]);
        Assert.False(attributes.ContainsKey("nativeTableCoverageUnrepresentedWordCount"));
        Assert.False(attributes.ContainsKey("nativeTableCoverageEvidenceSha256"));
    }
}
