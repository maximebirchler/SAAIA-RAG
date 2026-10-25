using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalChunkProjectorPhysicalPageContextTests
{
    [Fact]
    public void Projection_without_added_physical_context_retains_the_existing_section_order()
    {
        const string body = "MATERIALS PROCEDURE 1. Isolate the supply, record its verified isolation state and inspect the installed module. 2. Connect the reference instrument, measure its output and retain the signed calibration certificate.";
        var units = new[] { Unit(0, 0, 3, body), Unit(1, 1, 2, body) };
        var sections = new[] { new ExtractedDocumentSection(0, "First section", 1, 3, 3, null, null),
            new ExtractedDocumentSection(1, "Second section", 1, 2, 2, null, null) };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 60, 0, 25);

        Assert.DoesNotContain(projected, chunk => chunk.ChunkType == "physical_page_context_v1");
        Assert.Equal([3, 2], projected.Select(chunk => chunk.PageStart));
        Assert.Equal([0, 1], projected.Select(chunk => chunk.ChunkIndex));
        Assert.Equal([0, 1], projected.Select(chunk => chunk.UnitOrdinal!.Value));
    }

    [Fact]
    public void Added_physical_context_keeps_chunk_navigation_in_physical_source_order_with_contiguous_indices()
    {
        const string body = "MATERIALS PROCEDURE 1. Isolate the supply, record its verified isolation state and inspect the installed module. 2. Connect the reference instrument, measure its output and retain the signed calibration certificate.";
        var units = new[]
        {
            Unit(0, 0, 2, body), Unit(1, 0, 2, "Low cost"), Unit(2, 0, 2, "Approved"),
            Unit(3, 0, 3, body.Replace("installed module", "separate sensor", StringComparison.Ordinal)),
            Unit(4, 0, 3, "Low cost"), Unit(5, 0, 3, "Restricted")
        };
        var sections = new[] { new ExtractedDocumentSection(0, "Equipment instructions", 1, 2, 3, null, null) };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 60, 0, 25);

        Assert.Contains(projected, chunk => chunk.ChunkType == "physical_page_context_v1" && chunk.PageStart == 2);
        Assert.Contains(projected, chunk => chunk.ChunkType == "physical_page_context_v1" && chunk.PageStart == 3);
        Assert.Equal(Enumerable.Range(0, projected.Count), projected.Select(chunk => chunk.ChunkIndex));
        for (var index = 1; index < projected.Count; index++)
        {
            var previous = projected[index - 1];
            var current = projected[index];
            Assert.True(previous.PageStart <= current.PageStart,
                $"Chunk navigation returned from page {previous.PageStart} to page {current.PageStart}.");
            if (previous.PageStart == current.PageStart)
                Assert.True(previous.SourceUnitStartOrdinal.GetValueOrDefault(int.MaxValue)
                    <= current.SourceUnitStartOrdinal.GetValueOrDefault(int.MaxValue));
        }
        Assert.All(projected, chunk =>
        {
            var source = chunk.SourceUnitOrdinals!.Select(ordinal => units.Single(unit => unit.Ordinal == ordinal)).ToArray();
            Assert.All(source, unit => Assert.Equal(chunk.PageStart, unit.PageStart));
            Assert.Equal(source[0].OffsetStart, chunk.OffsetStart);
            Assert.Equal(source[^1].OffsetEnd, chunk.OffsetEnd);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reliable_short_page_trailer_remains_readable_without_becoming_an_isolated_or_reassigned_candidate(bool sameSection)
    {
        var sections = Sections();
        var units = new[]
        {
            Unit(0, 0, 1, "The prior subject describes another installed module and retains its separate operating certificate after the original verification has been completed and recorded by the supervisor."),
            Unit(1, 1, 2, "MATERIALS PROCEDURE 1. Isolate the supply, record its verified isolation state and inspect the installed module. 2. Connect the reference instrument, measure its output and retain the signed calibration certificate."),
            Unit(2, sameSection ? 1 : 0, 2, "Cycle : 20 minutes"),
            Unit(3, sameSection ? 1 : 0, 2, "Low cost"),
            Unit(4, sameSection ? 1 : 0, 2, "Approved"),
            Unit(5, sameSection ? 1 : 0, 2, "2")
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 60, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        var context = Assert.Single(published, chunk => chunk.ChunkType == "physical_page_context_v1");
        Assert.Null(context.SectionOrdinal);
        Assert.Null(context.UnitOrdinal);
        Assert.Null(context.SectionTitle);
        Assert.Null(context.HeadingPath);
        Assert.Equal("physical_page_context_unassigned_suffix", context.ChunkComposition);
        Assert.Equal(2, context.PageStart);
        Assert.Equal(2, context.PageEnd);
        Assert.True(context.TokenCount <= 60);
        Assert.Equal([1, 2, 3, 4], context.SourceUnitOrdinals);
        Assert.Equal(units[1].OffsetStart, context.OffsetStart);
        Assert.Equal(units[4].OffsetEnd, context.OffsetEnd);
        Assert.DoesNotContain(5, context.SourceUnitOrdinals!);
        Assert.All(units.Skip(2).Take(3), unit => Assert.Contains(unit.Text, context.Text, StringComparison.Ordinal));
        Assert.Contains(units[1].Text, context.Text, StringComparison.Ordinal);
        var contextual = Assert.Single(ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, [context]));
        Assert.DoesNotContain("section_title:", contextual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("heading_path:", contextual.Text, StringComparison.Ordinal);
        Assert.Contains("chunk_type: physical_page_context_v1", contextual.Text, StringComparison.Ordinal);
        Assert.Contains("context_scope: physical_page", contextual.Text, StringComparison.Ordinal);
        Assert.Contains("section_association: not_asserted", contextual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reliable_page_prefix_remains_readable_as_unassigned_physical_context_beside_the_following_body()
    {
        var sections = Sections();
        var units = Units();

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 60, 0, 25);
        var published = projected.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();

        var context = Assert.Single(published, chunk => chunk.ChunkType == "physical_page_context_v1");
        Assert.Null(context.SectionOrdinal);
        Assert.Null(context.UnitOrdinal);
        Assert.Null(context.SectionTitle);
        Assert.Null(context.HeadingPath);
        Assert.Equal("physical_page_context_unassigned_prefix", context.ChunkComposition);
        Assert.Equal(2, context.PageStart);
        Assert.Equal(2, context.PageEnd);
        Assert.True(context.TokenCount <= 60);
        Assert.Equal([1, 2, 3, 4], context.SourceUnitOrdinals);
        Assert.Equal(units[1].OffsetStart, context.OffsetStart);
        Assert.Equal(units[4].OffsetEnd, context.OffsetEnd);
        Assert.All(units.Skip(1).Take(3), unit =>
        {
            Assert.Equal(0, unit.SectionOrdinal);
            Assert.Contains(unit.Text, context.Text, StringComparison.Ordinal);
        });
        Assert.Contains(units[4].Text, context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(units[0].Text, context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(units[5].Text, context.Text, StringComparison.Ordinal);
        Assert.Contains(published, chunk => chunk.SourceUnitOrdinals?.Contains(5) == true);

        var contextual = Assert.Single(ContextualTextProjector.Project("Documents/equipment.pdf", sections, units, [context]));
        Assert.Contains("chunk_type: physical_page_context_v1", contextual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("section_title:", contextual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("heading_path:", contextual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_physical_prefix_is_not_promoted_to_an_isolated_candidate_without_a_following_substantive_body()
    {
        var units = Units().Take(4).ToArray();

        var projected = RetrievalChunkProjector.ProjectStructureAware(Sections(), units, 60, 0, 25);

        Assert.DoesNotContain(projected, chunk => chunk.ChunkType == "physical_page_context_v1");
    }

    [Fact]
    public void A_physical_prefix_cannot_borrow_a_body_from_another_physical_page()
    {
        var units = Units().Select(unit => unit.SectionOrdinal == 1
            ? unit with { PageStart = 3, PageEnd = 3 } : unit).ToArray();
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Previous subject", 1, 1, 2, 1, null),
            new ExtractedDocumentSection(1, "Calibration", 1, 3, 3, 5, null)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(sections, units, 60, 0, 25);

        Assert.DoesNotContain(projected, chunk => chunk.ChunkType == "physical_page_context_v1"
            && chunk.SourceUnitOrdinals?.Contains(1) == true);
        Assert.DoesNotContain(projected, chunk => chunk.SourceUnitOrdinals?.Contains(1) == true
            && chunk.SourceUnitOrdinals.Contains(4));
    }

    private static ExtractedDocumentSection[] Sections() =>
    [
        new(0, "Previous subject", 1, 1, 2, 1, 4),
        new(1, "Calibration", 1, 2, 2, 5, null)
    ];

    private static ExtractedDocumentUnit[] Units() =>
    [
        Unit(0, 0, 1, "The prior subject describes a separate installed module, records its maintenance state and retains its original operating certificate before the equipment is returned to service."),
        Unit(1, 0, 2, "Cycle : 2 h 45 minutes"),
        Unit(2, 0, 2, "Cost tier intermediate"),
        Unit(3, 0, 2, "Approved"),
        Unit(4, 1, 2, "MATERIALS PROCEDURE 1. Isolate the supply, record its verified isolation state and inspect the installed module. 2. Connect the reference instrument, measure its output and retain the signed calibration certificate."),
        Unit(5, 1, 2, "Continue the independent verification by inspecting each connector, recording every reference reading, comparing the measured values with the documented limits and obtaining supervisor approval before returning the equipment to service.")
    ];

    private static ExtractedDocumentUnit Unit(int ordinal, int section, int page, string text)
        => new(ordinal, section, page, page, text, text.Length,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            [1], ordinal * 1_000, ordinal * 1_000 + text.Length,
            ExtractionTextStatus: "ok", ExtractionQualitySignals: ["text_extraction_ok"]);
}
