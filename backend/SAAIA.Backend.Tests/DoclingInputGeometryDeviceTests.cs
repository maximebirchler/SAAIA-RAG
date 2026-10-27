using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_geometry_stage_device_real_factory_bundle_uses_actual_declared_hardware(bool refused)
    {
        var request = await GeometryDeviceRequest(refused, enabled: true);
        Assert.Contains(request.Hardware.Devices, device => device.DeviceId == "cpu:0");
        Assert.DoesNotContain(request.Hardware.Devices, device => device.DeviceId == "cpu");
        var bundle = DoclingCanonicalBundleFactory.Create(request);
        var stage = Assert.Single(bundle.ManifestEnvelope.Manifest.Stages, value => value.StageId == "docling_input_geometry");
        Assert.Contains(bundle.ManifestEnvelope.Manifest.Hardware.Devices, device => device.DeviceId == stage.DeviceId);
        Assert.Equal(refused ? "refused" : "observed", stage.Attributes["availability"]);
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
    }

    [Fact]
    public async Task Input_geometry_stage_device_validator_still_refuses_an_undeclared_device()
    {
        var request = await GeometryDeviceRequest(refused: false, enabled: true);
        var stage = Assert.Single(request.Stages, value => value.StageId == "docling_input_geometry");
        stage.DeviceId = "not-declared";
        var error = Assert.Throws<CanonicalContractException>(() => DoclingCanonicalBundleFactory.Create(request));
        Assert.Contains(error.Issues, issue => issue.Code == "broken_reference" && issue.Path.EndsWith(".deviceId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Input_geometry_stage_device_off_keeps_the_stage_list_unchanged()
    {
        var request = await GeometryDeviceRequest(refused: false, enabled: false);
        Assert.DoesNotContain(request.Stages, stage => stage.StageId == "docling_input_geometry");
        request.Conversion.Document.JsonContent!.InputGeometryCapture = null;
        var withoutCapture = DoclingIngestionStageManifestFactory.Create(request.Options, new(), BuildRagOptions(),
            request.Conversion, new(1, 2, 3, 4), request.CodeRevision);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(request.Stages), CanonicalContractJson.SerializeCanonical(withoutCapture));
    }

    private static async Task<DoclingCanonicalBundleRequest> GeometryDeviceRequest(bool refused, bool enabled)
    {
        var fixture = await GeometryFixture();
        if (refused)
        {
            var capture = new DoclingInputGeometryCapture(1, new(maximumTicks: 0), true);
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(path, GeometrySourceBytes);
                await using var input = File.OpenRead(path);
                await capture.SerializePdfAsync(input, Stream.Null, CancellationToken.None);
            }
            finally { File.Delete(path); }
            Assert.Equal("refused", capture.Data.Availability);
            fixture.Response.Document.JsonContent!.InputGeometryCapture = capture;
        }
        var options = new DocumentIntelligenceOptions { DoclingInputGeometryCaptureEnabled = enabled };
        var stages = DoclingIngestionStageManifestFactory.Create(options, new(), BuildRagOptions(), fixture.Response,
            new(1, 2, 3, 4), "source22-test");
        return new(fixture.Document.DocumentId, fixture.Document.RevisionId, fixture.Artifact.ProcessingRunId, 1,
            fixture.SourceSha, GeometrySourceBytes.Length, "fixture.pdf", "source22-test",
            DateTimeOffset.Parse("2026-10-27T00:00:00Z"), IngestionHardwareProfiler.Capture(), options, stages,
            fixture.Document, fixture.Response, [])
        {
            InputGeometryArtifact = refused || !enabled ? null : fixture.Artifact
        };
    }
}
