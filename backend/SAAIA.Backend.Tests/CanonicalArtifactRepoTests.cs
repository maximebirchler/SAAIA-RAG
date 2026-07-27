using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalArtifactRepoTests
{
    [Fact]
    public void Compressed_bundle_round_trips_with_content_hash_validation()
    {
        var bundle = CreateBundle();

        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        var restored = CanonicalArtifactRepo.ReadBundleArtifact(artifact);

        Assert.Equal(CanonicalArtifactRepo.BundleArtifactType, artifact.ArtifactType);
        Assert.True(artifact.Payload.LongLength < artifact.ByteSize);
        Assert.Equal(bundle.Document.RevisionId, restored.Document.RevisionId);
        Assert.Equal(bundle.SourceAnchors[0].AnchorId, restored.SourceAnchors[0].AnchorId);
    }

    [Fact]
    public void Reader_rejects_payload_over_the_uncompressed_limit()
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(CreateBundle());

        Assert.Throws<InvalidDataException>(
            () => CanonicalArtifactRepo.ReadBundleArtifact(artifact, maxUncompressedBytes: 16));
    }

    private static CanonicalIngestionBundle CreateBundle()
    {
        var documentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var revisionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var sourceSha256 = new string('a', 64);
        var manifest = new IngestionManifest
        {
            ProcessingRunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DocumentId = documentId,
            RevisionId = revisionId,
            SourceSha256 = sourceSha256,
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            CodeRevision = "abcdef12",
            Hardware = new()
            {
                ProfileId = "test",
                LogicalProcessorCount = 4,
                MemoryBytes = 8_000_000_000,
                Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }]
            },
            Stages =
            [
                new()
                {
                    StageId = "native_parser",
                    StageType = "parse",
                    Engine = "fixture",
                    EngineVersion = "1",
                    OptionsSha256 = new string('d', 64),
                    DeviceId = "cpu"
                }
            ],
            Artifacts =
            [
                new()
                {
                    ArtifactId = "artifact_source",
                    ArtifactType = CanonicalArtifactTypes.SourceDocument,
                    SchemaVersion = "application/pdf",
                    Sha256 = sourceSha256,
                    StorageKey = $"content://sha256/{sourceSha256}",
                    SizeBytes = 123
                }
            ]
        };
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        const string text = "Canonical fixture text";
        var page = new ExtractedPdfPage(
            1,
            text,
            3,
            text.Length,
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)),
            PdfPageExtractionQuality.FromText(text, 3, text.Length),
            RawText: "Raw fixture text");
        var extraction = new PdfExtractionResult(
            [],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));
        var document = LegacyPdfCanonicalDocumentAdapter.Project(
            new(
                documentId,
                revisionId,
                sourceSha256,
                123,
                "fixture.pdf",
                envelope.ManifestSha256,
                "native_parser",
                "native_text",
                "fixture",
                "1"),
            extraction,
            [new(0, "Fixture", 1, 1, 1, 0, 0)]);
        var anchor = LegacyCanonicalSourceAnchorProjector.ProjectPageRange(
            document,
            "chunk_0",
            "retrieval_chunk",
            1,
            1);

        return new()
        {
            Document = document,
            ManifestEnvelope = envelope,
            SourceAnchors = [anchor]
        };
    }
}
