using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class IngestionManifestContractTests
{
    [Fact]
    public void Manifest_hash_is_independent_from_dictionary_insertion_order()
    {
        var left = CreateManifest();
        left.SchemaBindings["zeta"] = "3";
        left.SchemaBindings["alpha"] = "1";
        left.Stages[0].Attributes["language"] = "fr";
        left.Stages[0].Attributes["dpi"] = "400";

        var right = CreateManifest();
        right.SchemaBindings["alpha"] = "1";
        right.SchemaBindings["zeta"] = "3";
        right.Stages[0].Attributes["dpi"] = "400";
        right.Stages[0].Attributes["language"] = "fr";

        Assert.Equal(
            CanonicalContractJson.ComputeSha256(left),
            CanonicalContractJson.ComputeSha256(right));
    }

    [Fact]
    public void Envelope_hash_excludes_envelope_and_detects_manifest_tampering()
    {
        var envelope = CanonicalContractJson.CreateEnvelope(CreateManifest());

        Assert.True(CanonicalContractJson.HasValidHash(envelope));
        Assert.Empty(IngestionManifestValidator.Validate(envelope));

        envelope.Manifest.CodeRevision = "changed";

        Assert.False(CanonicalContractJson.HasValidHash(envelope));
        Assert.Contains(
            IngestionManifestValidator.Validate(envelope),
            issue => issue.Code == "hash_mismatch");
    }

    [Fact]
    public void Manifest_rejects_an_empty_artifact_inventory()
    {
        var manifest = CreateManifest();
        manifest.Artifacts.Clear();
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);

        Assert.Contains(
            IngestionManifestValidator.Validate(envelope),
            issue => issue.Path == "$.manifest.artifacts" && issue.Code == "required");
    }

    private static IngestionManifest CreateManifest()
        => new()
        {
            ProcessingRunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SourceSha256 = new string('a', 64),
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            CodeRevision = "abcdef12",
            Hardware = new()
            {
                ProfileId = "server-test",
                OperatingSystem = "linux",
                CpuModel = "test",
                LogicalProcessorCount = 4,
                MemoryBytes = 8_000_000_000,
                Devices =
                [
                    new()
                    {
                        DeviceId = "cpu",
                        DeviceType = "cpu",
                        Vendor = "test",
                        Model = "test"
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
                    ModelId = "fixture/model",
                    ModelRevision = "abcdef123456",
                    OptionsSha256 = new string('d', 64),
                    DeviceId = "cpu",
                    Concurrency = 1
                }
            ],
            Artifacts =
            [
                new()
                {
                    ArtifactId = "artifact_source",
                    ArtifactType = CanonicalArtifactTypes.SourceDocument,
                    SchemaVersion = "application/pdf",
                    Sha256 = new string('a', 64),
                    StorageKey = $"content://sha256/{new string('a', 64)}",
                    SizeBytes = 42
                }
            ]
        };
}
