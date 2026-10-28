using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalExactMatchEntryProjectionTests
{
    [Fact]
    public async Task Canonical_exact_entries_bind_to_final_split_chunks_without_borrowing_independent_unit_ordinals()
    {
        var corpus = await BuildCorpusAsync(filterOneChunk: true);
        Assert.True(corpus.Chunks.Count > corpus.Units.Count);
        Assert.NotEmpty(corpus.Index.ExactEntries);
        Assert.All(corpus.Index.ExactEntries, entry =>
        {
            Assert.Null(entry.UnitOrdinal);
            var chunk = Assert.Single(corpus.Chunks, chunk => chunk.ChunkIndex == entry.SourceRetrievalChunkIndex);
            Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(chunk));
            Assert.Contains(entry.Text, DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk), StringComparison.Ordinal);
            Assert.Empty(chunk.SourceUnitOrdinals!);
            Assert.NotEmpty(chunk.CanonicalBlockIds!);
        });
        var matching = corpus.Index.ExactEntries.Where(entry => entry.Text == "IND570").ToArray();
        Assert.NotEmpty(matching);
        Assert.All(matching, entry => Assert.Contains("IND570", corpus.Chunks.Single(chunk => chunk.ChunkIndex == entry.SourceRetrievalChunkIndex).Text));
        Assert.DoesNotContain(corpus.Index.ExactEntries, entry => entry.SourceRetrievalChunkIndex == corpus.FilteredIndex);
        Assert.Contains(corpus.Index.ExactEntries, entry => entry.Text.Contains("ÉCLAIR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canonical_aliases_absent_from_literal_source_are_counted_and_never_published_as_exact_evidence()
    {
        var corpus = await BuildCorpusAsync();
        var changed = corpus.ProfileUnits.Select(unit => unit with { Text = "SYNTHETIC-NONLITERAL-999 source absent from the observed chunk." }).ToArray();
        var result = CanonicalExactMatchEntryProjector.Project(changed, corpus.Chunks);
        Assert.Empty(result.Entries);
        Assert.True(result.NonLiteralCandidates > 0);
        var logger = new DiagnosticRecordingLogger<CanonicalExactMatchEntryProjectionTests>();
        var projected = IngestionProjectionStage.ProjectIndex("synthetic.pdf", corpus.Sections, corpus.Units, corpus.Chunks, changed, logger);
        Assert.Empty(projected.ExactEntries);
        var terminal = Assert.Single(logger.Events, row => Equals(row.Values.GetValueOrDefault("Operation"), "ingestion.project_index"));
        using var diagnostics = System.Text.Json.JsonDocument.Parse((string)terminal.Values["Measurements"]!);
        Assert.Equal("canonical_retrieval_chunk_v1", diagnostics.RootElement.GetProperty("exact_source_binding").GetString());
        Assert.Equal(result.NonLiteralCandidates, diagnostics.RootElement.GetProperty("exact_nonliteral_candidates_refused").GetInt32());
        Assert.All(logger.Events, row => Assert.DoesNotContain("SYNTHETIC-NONLITERAL-999", row.Text));
        Assert.Throws<InvalidDataException>(() => CanonicalExactMatchEntryProjector.Project(
            [changed[0] with { Ordinal = int.MaxValue }], corpus.Chunks));
    }

    [Fact]
    public async Task Unit_projection_keeps_real_unit_binding_and_does_not_acquire_canonical_chunk_identity()
    {
        var corpus = await BuildCorpusAsync();
        var result = IngestionProjectionStage.ProjectIndex("synthetic.pdf", corpus.Sections, corpus.Units, [], null, NullLogger.Instance);
        Assert.NotEmpty(result.ExactEntries);
        Assert.All(result.ExactEntries, entry =>
        {
            Assert.NotNull(entry.UnitOrdinal);
            Assert.Null(entry.SourceRetrievalChunkIndex);
        });
    }

    internal sealed record Corpus(CanonicalDocument Document, IReadOnlyList<ExtractedPdfPage> Pages,
        IReadOnlyList<ExtractedDocumentSection> Sections, IReadOnlyList<ExtractedDocumentUnit> Units,
        IReadOnlyList<ProjectedRetrievalChunk> Chunks, IReadOnlyList<ExtractedDocumentUnit> ProfileUnits,
        IngestionIndexProjection Index, int? FilteredIndex);

    internal static async Task<Corpus> BuildCorpusAsync(bool filterOneChunk = false)
    {
        var before = string.Join(" ", Enumerable.Repeat("Preparation inspection verification records ensure a complete operating procedure.", 8));
        var source = new DoclingDocument
        {
            SchemaName = "DoclingDocument", Version = "1.10.0",
            Pages = { ["1"] = new() { PageNumber = 1, Size = new() { Width = 600, Height = 800 } } },
            Texts = [Text(0, "section_header", "Synthetic equipment guide", 750),
                Text(1, "text", before + " The IND570 terminal preserves the verified isolation record before operation resumes. " + before, 650),
                Text(2, "text", "L'équipement conserve une référence ÉCLAIR-4281 et sa température exacte de 32 °C dans la procédure vérifiée.", 450)],
            Body = new() { Children = [new() { Ref = "#/texts/0" }, new() { Ref = "#/texts/1" }, new() { Ref = "#/texts/2" }] }
        };
        var document = DoclingCanonicalDocumentAdapter.Project(new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            1234, "synthetic.pdf", new string('b', 64), "stage_fixture", "docling-serve-test", "sha256:test", 0.92), source);
        var units = CanonicalExtractionViewProjector.ProjectUnits(document, source);
        var sections = CanonicalDocumentSectionProjector.Project(document);
        var pages = CanonicalExtractionViewProjector.Project(document, [], source).Pages;
        var initial = DoclingCanonicalRetrievalProjector.Project(document, source, 220, 1);
        var admission = await IngestionEmbeddingInputStage.PrepareAsync(initial, IngestionWorker.ShouldPublishRetrievalChunk,
            chunk => chunk.Text, text => text, (text, _) => Task.FromResult(text.Length), new TeiInputBudget(170, 32), CancellationToken.None);
        var chunks = admission.Chunks.ToArray();
        int? filteredIndex = null;
        if (filterOneChunk)
        {
            var position = Array.FindIndex(chunks, chunk => !chunk.Text.Contains("IND570", StringComparison.Ordinal));
            Assert.True(position >= 0);
            filteredIndex = chunks[position].ChunkIndex;
            chunks[position] = chunks[position] with { ContentRole = RetrievalContentClassifier.NavigationRole, NavigationScore = 1, ContentDensityScore = 0 };
        }
        var profile = CanonicalProfileInputProjector.Project(chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray());
        var index = IngestionProjectionStage.ProjectIndex("synthetic.pdf", sections, units, chunks, profile, NullLogger.Instance);
        return new(document, pages, sections, units, chunks, profile, index, filteredIndex);
    }

    private static DoclingTextItem Text(int index, string label, string text, double top) => new()
    {
        SelfRef = "#/texts/" + index, Parent = new() { Ref = "#/body" }, Label = label, OriginalText = text, Text = text,
        ContentLayer = "body", Level = label == "section_header" ? 1 : null,
        Provenance = [new() { PageNumber = 1, BoundingBox = new() { Left = 50, Right = 550, Top = top, Bottom = top - 60, CoordinateOrigin = "BOTTOMLEFT" } }]
    };
}
