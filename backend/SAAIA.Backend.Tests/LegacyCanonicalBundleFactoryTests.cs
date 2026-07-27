using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LegacyCanonicalBundleFactoryTests
{
    [Fact]
    public void Factory_uses_stable_published_chunk_ids_and_cross_validates_bundle()
    {
        var documentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var revisionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string text = "Canonical source text with enough words for a retrieval chunk.";
        var page = new ExtractedPdfPage(
            1,
            text,
            10,
            text.Length,
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)),
            RawText: "Raw source text with enough words for a retrieval chunk.");
        var extraction = new PdfExtractionResult(
            [],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));
        var chunk = new ProjectedRetrievalChunk(
            0,
            0,
            0,
            1,
            1,
            text,
            10,
            [1],
            "unit_exact_v1");
        var hardware = new IngestionHardwareProfile
        {
            ProfileId = "hardware_test",
            LogicalProcessorCount = 4,
            MemoryBytes = 8_000_000_000,
            Devices = [new() { DeviceId = "cpu:0", DeviceType = "cpu" }]
        };

        var bundle = LegacyCanonicalBundleFactory.Create(new(
            documentId,
            revisionId,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            IndexedVersion: 7,
            SourceSha256: new string('a', 64),
            SourceSizeBytes: 123,
            SourceDisplayName: "fixture.pdf",
            CodeRevision: "abcdef12",
            CreatedAtUtc: DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            Hardware: hardware,
            Stages:
            [
                new()
                {
                    StageId = "parse",
                    StageType = "canonical_projection",
                    Engine = "PdfPig",
                    EngineVersion = "0.1.13",
                    OptionsSha256 = new string('d', 64),
                    DeviceId = "cpu:0"
                }
            ],
            Extraction: extraction,
            Sections: [new(0, "Fixture", 1, 1, 1, 0, 0)],
            RetrievalChunks: [chunk]));

        var anchor = Assert.Single(bundle.SourceAnchors);
        var expectedId = DocumentFoundationRepo
            .BuildStableRetrievalChunkId(documentId, 7, 0)
            .ToString("D");
        Assert.Equal(expectedId, anchor.ProjectionId);
        Assert.Empty(CanonicalIngestionBundleValidator.Validate(bundle));
    }
}
