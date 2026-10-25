using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionProjectionCoverageDiagnosticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Projection_reports_known_source_coverage_without_leaking_content_or_guessing_legacy_coverage(bool hasUnitProvenance)
    {
        const string body = "PRIVATE_SOURCE_MARKER Inspect the installed equipment, record the verified isolation state and retain the signed operating certificate before returning it to service.";
        const string other = "PRIVATE_OMITTED_MARKER A source restriction that is not in this projected chunk.";
        var units = new[] { Unit(0, body), Unit(1, other) };
        var chunk = new ProjectedRetrievalChunk(0, null, 0, 1, 1, body, 25,
            SHA256.HashData(Encoding.UTF8.GetBytes(body)), "unit_exact_v1",
            SourceUnitOrdinals: hasUnitProvenance ? [0] : null);
        var logger = new DiagnosticRecordingLogger<IngestionProjectionCoverageDiagnosticsTests>();

        IngestionProjectionStage.ProjectIndex("private-source-path.pdf", [], units, [chunk], null, logger);

        var terminal = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "ingestion.project_index"));
        using var fields = JsonDocument.Parse((string)terminal.Values["Measurements"]!);
        AssertCoverage(fields.RootElement, "projected", hasUnitProvenance, 1, 1, body.Length);
        AssertCoverage(fields.RootElement, "published", hasUnitProvenance, 1, 1, body.Length);
        Assert.All(logger.Events, entry =>
        {
            Assert.DoesNotContain("PRIVATE_SOURCE_MARKER", entry.Text);
            Assert.DoesNotContain("PRIVATE_OMITTED_MARKER", entry.Text);
            Assert.DoesNotContain("private-source-path", entry.Text);
        });
    }

    [Fact]
    public void Published_coverage_excludes_rejected_chunks_even_when_raw_projection_covers_every_unit()
    {
        var units = new[] { Unit(0, "Verified operating instructions with the recorded isolation result."),
            Unit(1, "Navigation-only source unit represented by a rejected projection.") };
        var chunks = new[] { Chunk(0, units[0]), Chunk(1, units[1]) with
        {
            ContentRole = RetrievalContentClassifier.NavigationRole,
            NavigationScore = 1,
            ContentDensityScore = 0
        } };
        Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(chunks[0]));
        Assert.False(IngestionWorker.ShouldPublishRetrievalChunk(chunks[1]));

        var fields = ProjectFields(units, chunks);

        AssertCoverage(fields, "projected", true, 2, 0, units.Sum(unit => unit.Text.Length));
        AssertCoverage(fields, "published", true, 1, 1, units[0].Text.Length);
        Assert.Equal(1, fields.GetProperty("published_retrieval_chunks").GetInt32());
        Assert.False(fields.TryGetProperty("unrepresented_source_units", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_or_foreign_provenance_on_a_rejected_chunk_does_not_poison_known_published_coverage(bool foreignOrdinal)
    {
        var units = new[] { Unit(0, "Verified operating instructions with the recorded isolation result."),
            Unit(1, "Navigation-only source unit whose rejected projection has no usable provenance.") };
        var rejected = Chunk(1, units[1]) with
        {
            ContentRole = RetrievalContentClassifier.NavigationRole,
            NavigationScore = 1,
            ContentDensityScore = 0,
            SourceUnitOrdinals = foreignOrdinal ? [99] : null
        };

        var fields = ProjectFields(units, [Chunk(0, units[0]), rejected]);

        AssertCoverage(fields, "projected", false);
        AssertCoverage(fields, "published", true, 1, 1, units[0].Text.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_empty_published_set_has_known_zero_coverage_instead_of_unknown_coverage(bool includeRejectedChunk)
    {
        var units = new[] { Unit(0, "Navigation-only source represented by a rejected projection.") };
        ProjectedRetrievalChunk[] chunks = includeRejectedChunk ? [Chunk(0, units[0]) with
        {
            ContentRole = RetrievalContentClassifier.NavigationRole,
            NavigationScore = 1,
            ContentDensityScore = 0
        }] : [];

        var fields = ProjectFields(units, chunks);

        AssertCoverage(fields, "projected", true, includeRejectedChunk ? 1 : 0,
            includeRejectedChunk ? 0 : 1, includeRejectedChunk ? units[0].Text.Length : 0);
        AssertCoverage(fields, "published", true, 0, 1, 0);
    }

    private static JsonElement ProjectFields(IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> chunks)
    {
        var logger = new DiagnosticRecordingLogger<IngestionProjectionCoverageDiagnosticsTests>();
        IngestionProjectionStage.ProjectIndex("private-source-path.pdf", [], units, chunks, null, logger);
        var terminal = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "ingestion.project_index"));
        using var fields = JsonDocument.Parse((string)terminal.Values["Measurements"]!);
        return fields.RootElement.Clone();
    }

    private static void AssertCoverage(JsonElement fields, string projection, bool available,
        int represented = 0, int unrepresented = 0, long representedCharacters = 0)
    {
        Assert.Equal(available, fields.GetProperty(projection + "_source_unit_coverage_available").GetBoolean());
        if (available)
        {
            Assert.Equal(represented, fields.GetProperty(projection + "_represented_source_units").GetInt32());
            Assert.Equal(unrepresented, fields.GetProperty(projection + "_unrepresented_source_units").GetInt32());
            Assert.Equal(representedCharacters, fields.GetProperty(projection + "_represented_source_unit_characters").GetInt64());
        }
        else
        {
            Assert.False(fields.TryGetProperty(projection + "_represented_source_units", out _));
            Assert.False(fields.TryGetProperty(projection + "_unrepresented_source_units", out _));
            Assert.False(fields.TryGetProperty(projection + "_represented_source_unit_characters", out _));
        }
    }

    private static ProjectedRetrievalChunk Chunk(int index, ExtractedDocumentUnit unit)
        => new(index, null, unit.Ordinal, unit.PageStart, unit.PageEnd, unit.Text, unit.TokenCount,
            unit.Checksum, "unit_exact_v1", SourceUnitOrdinals: [unit.Ordinal]);

    private static ExtractedDocumentUnit Unit(int ordinal, string text) => new(ordinal, null, 1, 1,
        text, text.Length, text.Split(' ').Length, SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
