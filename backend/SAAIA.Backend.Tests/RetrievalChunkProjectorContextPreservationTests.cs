using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalChunkProjectorContextPreservationTests
{
    [Fact]
    public void A_short_structured_metadata_boundary_does_not_separate_a_real_title_from_its_body()
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        var units = new[]
        {
            Unit(0, 5, "Primary system"),
            Unit(1, 5, "Category Controls For 4 items"),
            Unit(2, 5, "MATERIALS PROCEDURE 1. Isolate the primary supply and record its verified isolation state. 2. Inspect the primary module, measure its installed pressure and file the signed operating certificate."),
            Unit(3, 5, "Secondary system"),
            Unit(4, 5, "Category Controls For 2 items"),
            Unit(5, 5, "MATERIALS PROCEDURE 1. Connect the separate reference circuit and record its calibration result. 2. Inspect the secondary sensor, compare its output with the approved reference and file the resulting certificate.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.SequenceEqual([0, 1, 2]) == true
            && chunk.Text.Contains("Primary system", StringComparison.Ordinal)
            && chunk.Text.Contains("For 4 items", StringComparison.Ordinal));
        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.SequenceEqual([3, 4, 5]) == true
            && chunk.Text.Contains("Secondary system", StringComparison.Ordinal)
            && chunk.Text.Contains("For 2 items", StringComparison.Ordinal));
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true
            && chunk.SourceUnitOrdinals.Contains(3));
    }

    [Theory]
    [InlineData("Prohibited")]
    [InlineData("Approved")]
    public void Single_word_source_fact_remains_with_its_substantive_body_without_becoming_a_standalone_candidate(string sourceFact)
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        var units = new[]
        {
            Unit(0, 5, "The operator checks the installation, verifies the approved environment, records the isolation state and confirms that the documented operating conditions are satisfied before starting the equipment or opening its enclosure."),
            Unit(1, 5, sourceFact),
            Unit(2, 5, "After completing the inspection, the technician records the verified condition, closes the service record and obtains the supervisor approval before the equipment can be returned to its normal operating state.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(1) == true
            && chunk.Text.Contains(sourceFact, StringComparison.Ordinal));
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.SequenceEqual([1]) == true);
        var context = ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, published);
        Assert.Contains(context, entry => entry.Text.Contains(sourceFact, StringComparison.Ordinal));
    }

    [Fact]
    public void Short_source_preamble_stays_with_its_first_high_signal_body_instead_of_disappearing_at_the_boundary()
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        const string preamble = "Disconnect power first.";
        const string body = "MATERIALS PROCEDURE 1. Isolate the supply, label the installed module, verify the pressure indicator and record the initial state. 2. Connect the reference instrument, measure the output signal and store the signed calibration certificate.";
        var units = new[] { Unit(0, 5, preamble), Unit(1, 5, body) };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true
            && chunk.SourceUnitOrdinals.Contains(1)
            && chunk.Text.Contains(preamble, StringComparison.Ordinal)
            && chunk.Text.Contains(body, StringComparison.Ordinal));
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.SequenceEqual([0]) == true);
    }

    [Theory]
    [InlineData("Indoor use only.")]
    [InlineData("For 4 operators")]
    [InlineData("Pour 4 personnes")]
    public void Single_word_page_heading_keeps_a_short_restriction_before_its_body_without_crossing_the_previous_subject(string restriction)
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 6, null, null) };
        var units = new[]
        {
            Unit(0, 5, "• Inspect the drive module, confirm the isolation state, record the maintenance result and verify the final pressure before returning the equipment to the operator for the remaining checks."),
            Unit(1, 6, "Calibration"),
            Unit(2, 6, restriction),
            Unit(3, 6, "• Connect the reference instrument to the separate sensor circuit, record its calibration reading, compare the measurement with the reference value and file the resulting certificate before releasing this sensor.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(1) == true
            && chunk.SourceUnitOrdinals.Contains(2) && chunk.SourceUnitOrdinals.Contains(3)
            && chunk.Text.Contains("Calibration", StringComparison.Ordinal)
            && chunk.Text.Contains(restriction, StringComparison.Ordinal)
            && chunk.PageStart == 6 && chunk.PageEnd == 6);
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true
            && chunk.SourceUnitOrdinals.Any(ordinal => ordinal >= 1));
    }

    [Theory]
    [InlineData("Indoor use only.")]
    [InlineData("Disconnect power first.")]
    [InlineData("Do not open.")]
    public void Short_source_directive_remains_in_substantive_context_without_becoming_a_standalone_candidate(string directive)
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        var units = new[]
        {
            Unit(0, 5, "The operator checks the installation, verifies the approved environment, records the isolation state and confirms that the documented operating conditions are satisfied before starting the equipment or opening its enclosure."),
            Unit(1, 5, directive),
            Unit(2, 5, "After completing the inspection, the technician records the verified condition, closes the service record and obtains the supervisor approval before the equipment can be returned to its normal operating state.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(1) == true
            && chunk.Text.Contains(directive, StringComparison.Ordinal));
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.SequenceEqual([1]) == true);
        var context = ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, published);
        Assert.Contains(context, entry => entry.Text.Contains(directive, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("• 1,5 l de fluide hydraulique.")]
    [InlineData("• 24 V control supply.")]
    [InlineData("• 250 g of sealing compound.")]
    public void Short_named_measurement_remains_in_its_substantive_canonical_window_and_context(string measurement)
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        var units = new[]
        {
            Unit(0, 5, "The technician inspects the enclosure, verifies the isolation device, identifies the approved consumables and records their reference before servicing the equipment. Keep the source quantities with this procedure."),
            Unit(1, 5, measurement),
            Unit(2, 5, "The technician then closes the enclosure, checks the installed components, records the service result and returns the equipment to its documented operating condition after the final inspection has been completed.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        var owningChunks = published.Where(chunk => chunk.SourceUnitOrdinals?.Contains(1) == true).ToArray();
        Assert.NotEmpty(owningChunks);
        Assert.Contains(owningChunks, chunk => chunk.Text.Contains(measurement, StringComparison.Ordinal));
        Assert.All(owningChunks, chunk =>
        {
            Assert.Equal(5, chunk.PageStart);
            Assert.Equal(5, chunk.PageEnd);
        });
        var context = ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, published);
        Assert.Contains(context, entry => entry.Text.Contains(measurement, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_word_heading_on_a_new_physical_page_keeps_its_title_and_separates_subjects(bool provenHeadingMetadata)
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 6, null, null) };
        const string previousBody = "• Inspect the drive module, confirm the isolation state, record the maintenance result and verify the final pressure before returning the equipment to the operator for the remaining checks.";
        const string nextBody = "• Connect the reference instrument to the separate sensor circuit, record its calibration reading, compare the measurement with the reference value and file the resulting certificate before releasing this sensor.";
        var heading = Unit(1, 6, "Calibration") with
        {
            SectionTitle = provenHeadingMetadata ? "Calibration" : null,
            HeadingPath = provenHeadingMetadata ? "Equipment instructions / Calibration" : null,
            HeadingLevel = provenHeadingMetadata ? 2 : null
        };
        var units = new[] { Unit(0, 5, previousBody), heading, Unit(2, 6, nextBody) };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(1) == true
            && chunk.SourceUnitOrdinals.Contains(2)
            && chunk.Text.Contains("Calibration", StringComparison.Ordinal)
            && chunk.PageStart == 6 && chunk.PageEnd == 6);
        Assert.DoesNotContain(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true
            && chunk.SourceUnitOrdinals.Contains(2));
        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true
            && chunk.Text.Contains(previousBody, StringComparison.Ordinal));
    }

    [Fact]
    public void Preserving_short_context_does_not_admit_page_counters_bare_quantities_or_damaged_text()
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 5, 5, null, null) };
        var units = new[]
        {
            Unit(0, 5, "The operator inspects the equipment, verifies the required isolation, records the serial number, confirms the mechanical condition and files the signed checklist before allowing any maintenance work to start."),
            Unit(1, 5, "31"),
            Unit(2, 5, "31 32 33"),
            Unit(3, 5, "2 units"),
            Unit(4, 5, "For"),
            Unit(5, 5, "• 5 ml damaged fluid.") with { ExtractionQualitySignals = ["replacement_chars_remaining"] },
            Unit(6, 5, "The supervisor reviews the completed inspection, confirms that all documented controls remain effective and signs the release record after the technician has verified the final condition of the equipment.")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 200, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        Assert.NotEmpty(published);
        Assert.All(published, chunk =>
        {
            Assert.DoesNotContain(chunk.SourceUnitOrdinals ?? [], ordinal => ordinal is >= 1 and <= 5);
            Assert.DoesNotContain("damaged fluid", chunk.Text, StringComparison.Ordinal);
        });
        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(0) == true);
        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(6) == true);
    }

    private static ExtractedDocumentUnit Unit(int ordinal, int page, string text)
        => new(ordinal, 0, page, page, text, text.Length,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            [1], ordinal * 1_000, ordinal * 1_000 + text.Length,
            ExtractionTextStatus: "ok", ExtractionQualitySignals: ["text_extraction_ok"]);
}
