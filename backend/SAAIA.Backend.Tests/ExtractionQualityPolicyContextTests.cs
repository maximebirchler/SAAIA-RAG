using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ExtractionQualityPolicyContextTests
{
    private const string Body =
        "The technician inspects the isolation module, records the measured pressure, verifies the actuator response and signs the inspection record before handover. "
        + "The operator checks the installed equipment against the approved operating conditions and retains the recorded measurements with the maintenance history. "
        + "The supervisor reviews the completed checklist, confirms the release evidence and stores the signed report with the equipment file before normal operation resumes.";

    [Theory]
    [InlineData("Preparation : 1 hour Duration : 3 h 30 Cost tier Intermediate", false)]
    [InlineData("Classification: isolation level 4", true)]
    public void Reliable_extracted_metadata_is_admissible_context_independently_of_targeted_profile_reference_eligibility(
        string fact,
        bool expectedProfileReferenceEligibility)
    {
        var (_, _, units) = ExtractReliablePage(fact);
        var metadata = Assert.Single(units, unit => unit.Text.Equals(fact, StringComparison.Ordinal));

        AssertReliablePhysicalPageQuality(metadata);
        Assert.True(ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow(metadata));
        Assert.True(ExtractionQualityPolicy.ShouldRestrictUnitToTargetedReferences(metadata));
        Assert.Equal(expectedProfileReferenceEligibility, ExtractionQualityPolicy.ShouldUseUnitForProfileCards(metadata));
        if (expectedProfileReferenceEligibility)
            Assert.Contains("level 4", ExactMatchEntryExtractor.ExtractTargetedReferences(metadata.Text));
    }

    [Theory]
    [InlineData("Preparation : 1 hour Duration : 3 h 30 Cost tier Intermediate")]
    [InlineData("Classification: isolation level 4")]
    public void Reliable_extracted_metadata_reaches_published_context_with_its_substantive_body(string fact)
    {
        var (_, sections, units) = ExtractReliablePage(fact);
        var metadata = Assert.Single(units, unit => unit.Text.Equals(fact, StringComparison.Ordinal));
        var body = Assert.Single(units, unit => unit.Text.Equals(Body, StringComparison.Ordinal));
        AssertReliablePhysicalPageQuality(metadata);
        AssertReliablePhysicalPageQuality(body);

        var chunks = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(metadata.Ordinal) == true
            && chunk.SourceUnitOrdinals.Contains(body.Ordinal)
            && chunk.Text.Contains(fact, StringComparison.Ordinal)
            && chunk.Text.Contains(Body, StringComparison.Ordinal));
        Assert.DoesNotContain(published, chunk =>
            chunk.SourceUnitOrdinals?.SequenceEqual([metadata.Ordinal]) == true);
        var contexts = ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, published);
        Assert.Contains(contexts, context => context.Text.Contains(fact, StringComparison.Ordinal)
            && context.Text.Contains(Body, StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_from_a_physically_sparse_page_remains_restricted()
    {
        const string fact = "Classification: isolation level 4";
        var page = new ExtractedPdfPage(1, fact, CountWords(fact), fact.Length, [1]);
        var units = DocumentUnitExtractor.Extract([page], []);
        var metadata = Assert.Single(units);

        Assert.Equal("low_text", metadata.ExtractionTextStatus);
        Assert.True(metadata.ExtractionTextSparse);
        Assert.Contains("sparse_text_on_page", metadata.ExtractionQualitySignals!);
        Assert.False(ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow(metadata));
    }

    [Fact]
    public void Remaining_encoding_corruption_still_prevents_metadata_from_becoming_context()
    {
        const string fact = "Classification: isolation level 4";
        var text = "Inspection protocol\n\n" + fact + "\n\n" + Body + " Protection \uFFFD remains unreadable.";
        var page = new ExtractedPdfPage(1, text, CountWords(text), text.Length, [1]);
        var section = new ExtractedDocumentSection(0, "Inspection protocol", 1, 1, 1, 0, null);
        var units = DocumentUnitExtractor.Extract([page], [section]);
        var metadata = Assert.Single(units, unit => unit.Text.Equals(fact, StringComparison.Ordinal));

        Assert.Contains("replacement_chars_remaining", metadata.ExtractionQualitySignals!);
        Assert.False(ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow(metadata));
    }

    private static (ExtractedPdfPage Page, IReadOnlyList<ExtractedDocumentSection> Sections, IReadOnlyList<ExtractedDocumentUnit> Units)
        ExtractReliablePage(string fact)
    {
        var text = "Inspection protocol\n\n" + fact + "\n\n" + Body;
        var page = new ExtractedPdfPage(1, text, CountWords(text), text.Length, [1]);
        var sections = new[] { new ExtractedDocumentSection(0, "Inspection protocol", 1, 1, 1, 0, null) };
        var units = DocumentUnitExtractor.Extract([page], sections);
        return (page, sections, units);
    }

    private static void AssertReliablePhysicalPageQuality(ExtractedDocumentUnit unit)
    {
        Assert.Equal("ok", unit.ExtractionTextStatus);
        Assert.False(unit.ExtractionTextSparse);
        Assert.False(unit.ExtractionOcrCandidate);
        Assert.Contains("text_extraction_ok", unit.ExtractionQualitySignals!);
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
