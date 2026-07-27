using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalIngestionBundleTests
{
    [Fact]
    public void Bundle_cross_validates_document_manifest_and_anchors()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var manifest = CreateManifest(document);
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        document.ManifestSha256 = envelope.ManifestSha256;
        var anchor = CanonicalContractFixture.CreateAnchor(document);
        var bundle = new CanonicalIngestionBundle
        {
            Document = document,
            ManifestEnvelope = envelope,
            SourceAnchors = [anchor]
        };

        Assert.Empty(CanonicalIngestionBundleValidator.Validate(bundle));

        bundle.ManifestEnvelope.Manifest.RevisionId = Guid.NewGuid();

        Assert.Contains(
            CanonicalIngestionBundleValidator.Validate(bundle),
            issue => issue.Code == "identity_mismatch");
    }

    [Fact]
    public void Bundle_rejects_a_source_artifact_that_does_not_match_the_document()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var manifest = CreateManifest(document);
        manifest.Artifacts[0].Sha256 = new string('f', 64);
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        document.ManifestSha256 = envelope.ManifestSha256;
        var bundle = new CanonicalIngestionBundle
        {
            Document = document,
            ManifestEnvelope = envelope,
            SourceAnchors = [CanonicalContractFixture.CreateAnchor(document)]
        };

        Assert.Contains(
            CanonicalIngestionBundleValidator.Validate(bundle),
            issue => issue.Code == "identity_mismatch"
                     && issue.Message.Contains("Source artifact hash", StringComparison.Ordinal));
    }

    private static IngestionManifest CreateManifest(CanonicalDocument document)
        => new()
        {
            ProcessingRunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DocumentId = document.DocumentId,
            RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256,
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            CodeRevision = "abcdef12",
            Hardware = new()
            {
                ProfileId = "test",
                LogicalProcessorCount = 4,
                MemoryBytes = 8_000_000_000,
                Devices =
                [
                    new()
                    {
                        DeviceId = "cpu",
                        DeviceType = "cpu"
                    }
                ]
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
                    SchemaVersion = document.Source.MediaType,
                    Sha256 = document.Source.Sha256,
                    StorageKey = $"content://sha256/{document.Source.Sha256}",
                    SizeBytes = document.Source.SizeBytes
                }
            ]
        };
}
