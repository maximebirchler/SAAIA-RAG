using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeTypographyGuardrailTests
{
    [Theory]
    [InlineData(false, "unavailable")]
    [InlineData(true, "available")]
    public void Native_typography_alone_does_not_activate_the_existing_native_table_audit(bool existingAuditRequested, string expectedAvailability)
    {
        using var fixture = new NativeTableCoverageFixture(lowerWords: true);
        var structure = fixture.Project(nativeAuditRequested: existingAuditRequested, typography: true);
        var table = Assert.Single(structure.CanonicalDocument!.Pages[0].Tables);
        Assert.Equal(expectedAvailability, table.Provenance.Attributes["nativeTableCoverageAvailability"]);
        Assert.Equal(existingAuditRequested, structure.CanonicalDocument.Pages[0].QualityFlags.Contains(CanonicalNativeTableCoverageTests.Unresolved));
        Assert.Equal(new[] { "HEADER", "UPPER" }, table.Cells.Select(cell => cell.Text.Raw));
    }

    [Fact]
    public void Native_typography_uninspected_composite_descendant_is_not_reported_as_absent_font_program()
    {
        // F2 is a real resource with a descendant descriptor/program declaration;
        // F1 alone supplies primary glyphs. The auxiliary observer does not visit CID descendants.
        var bytes = NativeTypographyPdfFixture.Pdf(secondFont:
            "<< /Type /Font /Subtype /Type0 /BaseFont /CompositeTest /Encoding /Identity-H /DescendantFonts [7 0 R] >>",
            additionalObjects: [
                "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /CompositeTest /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /DW 500 >>",
                "<< /Type /FontDescriptor /FontName /CompositeTest /Flags 32 /ItalicAngle 0 /FontWeight 400 /FontFile2 9 0 R >>",
                "<< /Length 0 >>\nstream\nendstream"]);
        var (document, extraction) = NativeTypographyPdfFixture.Extract(bytes);
        var primary = CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks);
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var composite = Assert.Single(document.Pages[0].NativeTypographyObservations!.Resources!, resource => resource.Subtype == "Type0");
        Assert.Equal("unavailable", composite.DescriptorAvailability);
        Assert.Equal("unavailable", composite.FontProgramDeclaration);
        Assert.Equal("native_typography_composite_descendant_not_observed", composite.DescriptorReason);
        Assert.All(composite.FieldAvailability.Values, value => Assert.Equal("unavailable", value));
        Assert.Null(composite.DescriptorObjectNumber);
        Assert.Equal(primary, CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks));
        Assert.Empty(CanonicalContractValidator.Validate(document));
        composite.DescriptorAvailability = "absent";
        composite.FontProgramDeclaration = "absent";
        composite.ResourceSnapshotSha256 = CanonicalNativeTypographyIdentity.ResourceSnapshot(composite);
        composite.ResourceId = CanonicalNativeTypographyIdentity.ResourceId(document.Source.Sha256, 1, composite);
        Assert.Contains(CanonicalNativeTypographyValidator.Validate(document.Pages[0].NativeTypographyObservations!),
            issue => issue.Code == "native_typography_unobserved_composite");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true, true)]
    public async Task Native_typography_alone_is_auxiliary_and_does_not_change_force_ocr_policy(
        bool typography, bool reconciliation, bool expectedForceOcr, bool forceOcrExplicit = false)
    {
        var content = string.Concat(Enumerable.Repeat("GluedNativeWordsWithoutAnySeparators", 12));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, NativeTypographyPdfFixture.Pdf($"BT /F1 12 Tf 60 600 Td ({content}) Tj ET\n"));
            var options = new DocumentIntelligenceOptions
            {
                Enabled = true, Provider = "docling", DoOcr = true, ForceOcr = forceOcrExplicit,
                NativeTextCoverageReconciliationEnabled = reconciliation, NativePdfImageInventoryEnabled = false,
                RegionalTableAlternativeReadingsEnabled = false, NativeTypographyObservationsEnabled = typography
            };
            bool? observedForceOcr = null;
            await Assert.ThrowsAsync<IOException>(() => IngestionExtractionStage.ExtractAsync(path, options,
                (forceOcr, _) => { observedForceOcr = forceOcr; throw new IOException("synthetic conversion observation boundary"); },
                NullLogger.Instance, CancellationToken.None));
            Assert.Equal(expectedForceOcr, observedForceOcr);
        }
        finally { File.Delete(path); }
    }
}
