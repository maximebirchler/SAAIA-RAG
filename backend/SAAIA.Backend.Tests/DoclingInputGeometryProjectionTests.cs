using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public async Task Input_geometry_links_repeated_cell_ids_by_actual_table_page_and_provenance()
    {
        var source = BuildDocument();
        source.Pages.Add("2", new() { PageNumber = 2, Size = new() { Width = 200, Height = 400 } });
        source.Tables[0].Provenance.Add(Provenance(Box(10, 100, 90, 140, "TOPLEFT"), 2));
        var response = await GeometryResponseFor(source);
        // Existing primary validation rejects duplicate canonical IDs across pages.
        // The diagnostic must keep separate actual links without relaxing that refusal.
        Assert.Throws<CanonicalContractException>(() => DoclingCanonicalDocumentAdapter.Project(GeometryContext(response), response.Document.JsonContent!));
        var links = response.Document.JsonContent!.InputGeometryCapture!.Data.Links;
        Assert.Equal(8, links.Count);
        Assert.Equal(4, links.Select(l => l.CellId).Distinct().Count());
        Assert.Equal(2, links.Select(l => l.TableId).Distinct().Count());
        var first = links.First(l => l.CellIndex == 0 && l.PhysicalPage == 1);
        var second = links.First(l => l.CellIndex == 0 && l.PhysicalPage == 2);
        Assert.Equal(first.CellId, second.CellId);
        Assert.NotEqual(first.OutputPointBits![0][0], second.OutputPointBits![0][0]);
    }

    [Fact]
    public async Task Input_geometry_primary_text_and_cell_polygons_are_unchanged()
    {
        var fixture = await GeometryFixture();
        var source = JsonSerializer.Deserialize<DoclingDocument>(JsonSerializer.Serialize(fixture.Response.Document.JsonContent))!;
        var plain = DoclingCanonicalDocumentAdapter.Project(fixture.Context, source);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(plain), CanonicalContractJson.SerializeCanonical(fixture.Document));
        Assert.Equal(fixture.SourceSha, fixture.Response.Document.JsonContent!.InputGeometryCapture!.Data.SerializedPdfSha256);
        Assert.DoesNotContain("Raw fixture", Encoding.UTF8.GetString(fixture.Artifact.CopyJson()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Input_geometry_wrong_projection_cell_index_refuses_only_the_channel()
    {
        var source = BuildDocument();
        var response = await GeometryResponseFor(source);
        var context = GeometryContext(response);
        var capture = response.Document.JsonContent!.InputGeometryCapture!;
        var page = new CanonicalPage { PageNumber = 1, Geometry = new() { Width = 100, Height = 200 } };
        capture.Link(0, source.Tables[0], 0, page, 999, "table", new() { CellId = "wrong" }, source.Tables[0].Data.Cells[0], CancellationToken.None);
        var canonical = DoclingCanonicalDocumentAdapter.Project(context, response.Document.JsonContent);
        Assert.NotEmpty(canonical.Pages[0].Tables);
        Assert.Equal("geometry_projection_binding_invalid", capture.Data.Reason);
    }

    [Fact]
    public async Task Input_geometry_real_bundle_factory_binds_prepared_payload_before_envelope_hash()
    {
        var fixture = await GeometryFixture();
        var request = new DoclingCanonicalBundleRequest(fixture.Document.DocumentId, fixture.Document.RevisionId,
            fixture.Artifact.ProcessingRunId, 1, fixture.SourceSha, GeometrySourceBytes.Length, "fixture.pdf", "source22-test",
            DateTimeOffset.Parse("2026-10-27T00:00:00Z"),
            new() { ProfileId = "test", LogicalProcessorCount = 1, MemoryBytes = 1000000000, Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
            new(), [new() { StageId = "stage_fixture", StageType = "canonical_projection", Engine = "fixture", EngineVersion = "1", OptionsSha256 = new string('a', 64), DeviceId = "cpu" }],
            fixture.Document, fixture.Response, []) { InputGeometryArtifact = fixture.Artifact };
        var bundle = DoclingCanonicalBundleFactory.Create(request);
        var descriptor = Assert.Single(bundle.ManifestEnvelope.Manifest.Artifacts, a => a.ArtifactType == DoclingInputGeometryArtifact.ArtifactType);
        Assert.Equal(fixture.Artifact.Sha256, descriptor.Sha256);
        Assert.True(CanonicalContractJson.HasValidHash(bundle.ManifestEnvelope));
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalBundleFactory.Create(request with { DocumentId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Input_geometry_preserves_signed_zero_and_ulp_differences_without_tolerance()
    {
        var source = BuildDocument();
        source.Tables[0].Data.Cells[0].BoundingBox!.Left = BitConverter.Int64BitsToDouble(long.MinValue);
        source.Tables[0].Data.Cells[0].BoundingBox!.Right = double.BitIncrement(50d);
        source.Tables[0].Data.Cells[1].BoundingBox!.Left = 50d;
        var response = await GeometryResponseFor(source);
        DoclingCanonicalDocumentAdapter.Project(GeometryContext(response), response.Document.JsonContent!);
        var data = response.Document.JsonContent!.InputGeometryCapture!.Data;
        Assert.Equal("8000000000000000", data.Links[0].InputDoubleBits["l"]);
        Assert.Contains(data.Tokens, t => t.RawToken == "-0");
        Assert.NotEqual(data.Links[0].InputDoubleBits["r"], data.Links[1].InputDoubleBits["l"]);
        Assert.NotEqual(data.Links[0].OutputPointBits![1][0], data.Links[1].OutputPointBits![0][0]);
    }

    [Fact]
    public async Task Input_geometry_mismatched_serialized_source_refuses_before_artifact_creation()
    {
        var response = await GeometryResponseFor(BuildDocument());
        var context = GeometryContext(response);
        var canonical = DoclingCanonicalDocumentAdapter.Project(context, response.Document.JsonContent!);
        var capture = response.Document.JsonContent!.InputGeometryCapture!;
        var artifact = capture.Prepare(Guid.NewGuid(), canonical.DocumentId, canonical.RevisionId, Guid.NewGuid(),
            new string('f', 64), GeometrySourceBytes.Length, "test", "declared", response, CancellationToken.None);
        Assert.Null(artifact);
        Assert.Equal("serialized_source_identity_mismatch", capture.Data.Reason);
        Assert.NotEmpty(canonical.Pages[0].Tables[0].Cells);
    }

    internal sealed record InputGeometryFixture(Guid Tenant, DoclingCanonicalProjectionContext Context,
        CanonicalDocument Document, DoclingConvertResponse Response, DoclingInputGeometryArtifact Artifact, string SourceSha);
    internal static readonly byte[] GeometrySourceBytes = Encoding.UTF8.GetBytes("%PDF-1.7\npublic geometry witness\n%%EOF");
    internal static async Task<InputGeometryFixture> GeometryFixture(bool twoPages = false)
    {
        var source = BuildDocument();
        if (twoPages)
        {
            source.Pages.Add("2", new() { PageNumber = 2, Size = new() { Width = 200, Height = 400 } });
            source.Tables[0].Provenance.Add(Provenance(Box(10, 100, 90, 140, "TOPLEFT"), 2));
        }
        var response = await GeometryResponseFor(source);
        var context = GeometryContext(response);
        var canonical = DoclingCanonicalDocumentAdapter.Project(context, response.Document.JsonContent!);
        var tenant = Guid.Parse("eeeeeeee-1111-2222-3333-444444444444");
        var artifact = response.Document.JsonContent!.InputGeometryCapture!.Prepare(tenant, context.DocumentId,
            context.RevisionId, Guid.Parse("dddddddd-1111-2222-3333-444444444444"), context.SourceSha256,
            GeometrySourceBytes.Length, "source22-test", "declared-test", response, CancellationToken.None);
        Assert.NotNull(artifact);
        return new(tenant, context, canonical, response, artifact, context.SourceSha256);
    }
    private static DoclingCanonicalProjectionContext GeometryContext(DoclingConvertResponse response)
        => BuildContext() with { SourceSha256 = Convert.ToHexString(SHA256.HashData(GeometrySourceBytes)).ToLowerInvariant(), SourceSizeBytes = GeometrySourceBytes.Length };
    private static async Task<DoclingConvertResponse> GeometryResponseFor(DoclingDocument source)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DoclingConvertResponse { Status = "success", Document = new() { JsonContent = source } }));
        var capture = new DoclingInputGeometryCapture(1, new(), true);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, GeometrySourceBytes);
            await using var input = File.OpenRead(path);
            await capture.SerializePdfAsync(input, Stream.Null, CancellationToken.None);
        }
        finally { File.Delete(path); }
        capture.ObserveResponse(body, CancellationToken.None);
        var result = JsonSerializer.Deserialize<DoclingConvertResponse>(body)!;
        result.Document.JsonContent!.InputGeometryCapture = capture;
        return result;
    }
}
