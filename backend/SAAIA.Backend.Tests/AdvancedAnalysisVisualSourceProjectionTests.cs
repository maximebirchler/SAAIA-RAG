using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisVisualSourceProjectionTests
{
    private static readonly Guid Doc = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Revision = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly string Hash = new('a', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    private static CanonicalVisualAssetDescriptor Asset(int page = 2, string? figure = null, string? hash = null, string id = "asset_1")
        => new(id, page, figure, hash ?? Hash, new string('b', 64), "image/png", 640, 900, 1234);

    [Fact]
    public void Visual_source_metadata_is_exact_relative_authenticated_and_never_model_pixels()
    {
        var result = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 1, [Asset()], 0, 4, 20_000);
        var asset = Assert.Single(result.Assets!);
        Assert.Equal(Doc.ToString("D"), asset.DocId);
        Assert.Equal(Revision.ToString("D"), asset.RevisionId);
        Assert.Equal(Hash, asset.SourceSha256);
        Assert.Equal(2, asset.PhysicalPage);
        Assert.Equal(new string('b', 64), asset.ContentSha256);
        Assert.Equal($"/documents/{Doc:D}/revisions/{Revision:D}/assets/asset_1", asset.AuthenticatedApiPath);
        Assert.True(result.AuthenticatedPixelReadAvailable);
        Assert.Null(result.HumanViewerAvailable);
        Assert.False(result.ImageToolAvailable);
        Assert.False(result.ModelPixelsProvided);
        Assert.Equal("not_performed", result.InterpretationStatus);
        Assert.Equal("not_verified", result.SemanticAccuracy);
        Assert.Equal("page", asset.ReferenceKind);
        Assert.Equal(JsonSerializer.Serialize(new { visualSources = result }, Json).Length, result.SerializedWrapperCharacters);
    }

    [Fact]
    public void Visual_source_known_empty_and_unavailable_preserve_distinct_lists_and_counts()
    {
        var empty = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 1, 1, true, 0, [], 0, 4, 20_000);
        Assert.Empty(empty.Assets!);
        Assert.Equal(0, empty.AvailableAssetCount);
        Assert.Equal(0, empty.ReturnedAssetCount);
        var unknown = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 1, 1, false, null, null, 0, 4, 20_000);
        Assert.Null(unknown.Assets);
        Assert.Null(unknown.AvailableAssetCount);
        Assert.Null(unknown.ReturnedAssetCount);
        Assert.Null(unknown.OmittedAssetCount);
        Assert.Null(unknown.AuthenticatedPixelReadAvailable);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("page")]
    [InlineData("external-path")]
    public void Visual_source_mismatched_descriptor_scope_is_refused(string mutation)
    {
        var asset = mutation switch { "source" => Asset(hash: new string('c', 64)), "page" => Asset(page: 3), _ => Asset(id: "https://external.invalid/x") };
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 1, [asset], 0, 4, 20_000));
    }

    [Fact]
    public void Visual_source_pagination_and_budget_preserve_whole_descriptors_with_explicit_retry_offset()
    {
        var page = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 3, [Asset()], 1, 1, 20_000);
        Assert.Single(page.Assets!);
        Assert.Equal(2, page.NextAssetOffset);
        Assert.Equal(2, page.OmittedAssetCount);
        var refused = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 1, [Asset()], 0, 4, 1024);
        Assert.Empty(refused.Assets!);
        Assert.Equal(0, refused.NextAssetOffset);
        Assert.Equal(1, refused.OmittedAssetCount);
        Assert.Equal("asset_does_not_fit_json_budget", refused.Status);
        Assert.DoesNotContain("asset_1", JsonSerializer.Serialize(refused, Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Visual_source_observed_page_region_is_not_promoted_to_a_figure_or_formula_crop()
    {
        var polygon = new CanonicalPolygon { Points = [new() { X = .1, Y = .2 }, new() { X = .5, Y = .2 }, new() { X = .5, Y = .3 }, new() { X = .1, Y = .3 }] };
        var region = new AdvancedAnalysisVisualObservedRegion("E1", "anchor_1", "canonical_source_region", ["block_code"], polygon);
        var result = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 2, [Asset(), Asset(figure: "figure_1", id: "figure_asset")], 0, 4, 20_000,
            new Dictionary<int, AdvancedAnalysisVisualObservedRegion> { { 2, region } });
        Assert.Equal("page_with_observed_region", result.Assets![0].ReferenceKind);
        Assert.Same(region, result.Assets[0].ObservedRegion);
        Assert.Equal("figure_asset", result.Assets[1].ReferenceKind);
        Assert.Null(result.Assets[1].ObservedRegion);
    }

    [Fact]
    public void Visual_source_opt_in_survives_actual_request_json_round_trip()
    {
        var request = JsonSerializer.Deserialize<AdvancedAnalysisSearchRequest>("""{"query":"read","includeVisualSources":true,"visualSourceOffset":4,"visualSourceLimit":2}""", Json)!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request, Json));
        Assert.True(wire.RootElement.GetProperty("includeVisualSources").GetBoolean());
        Assert.Equal(4, wire.RootElement.GetProperty("visualSourceOffset").GetInt32());
        Assert.Equal(2, wire.RootElement.GetProperty("visualSourceLimit").GetInt32());
    }

    [Fact]
    public void Visual_source_flag_off_preserves_legacy_request_json_bytes()
    {
        var normal = JsonSerializer.Serialize(new AdvancedAnalysisSearchRequest("read"), Json);
        var disabled = JsonSerializer.Deserialize<AdvancedAnalysisSearchRequest>("""{"query":"read","includeVisualSources":false}""", Json)!;
        Assert.Equal(normal, JsonSerializer.Serialize(disabled, Json));
        Assert.DoesNotContain("visualSource", normal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visualSourceCitationsEnabled", JsonSerializer.Serialize(new AdvancedAnalysisOptions(), Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Visual_source_storage_asset_budget_has_an_addressable_last_window_and_does_not_limit_physical_pages()
    {
        Assert.Equal(1024, CanonicalVisualAssetRepo.MaximumDocumentAssets);
        var last = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 1200, 1200,
            true, 1024, [Asset(page: 1200)], 1023, 64, 20_000);
        Assert.Single(last.Assets!);
        Assert.Null(last.NextAssetOffset);
        Assert.Equal(1023, last.OmittedAssetCount);

        var end = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 1200, 1200,
            true, 1024, [], 1024, 64, 20_000);
        Assert.Empty(end.Assets!);
        Assert.Null(end.NextAssetOffset);
        Assert.Equal("end_of_inventory", end.Status);
        AdvancedAnalysisVisualSourceReader.ValidateRequest(new AdvancedAnalysisSearchRequest("read", Operation: "read_source",
            IncludeVisualSources: true, VisualSourceOffset: 1024, VisualSourceLimit: 64), enabled: true);
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisVisualSourceProjector.Project(
            Doc, Revision, Hash, 1200, 1200, true, 1025, [Asset(page: 1200)], 1024, 64, 20_000));
    }

    [Theory]
    [InlineData(false, "read_source", true, null, null, "visual_source_citations_disabled")]
    [InlineData(true, "search_corpus", true, null, null, "visual_source_request_invalid")]
    [InlineData(true, "read_source", false, 0, null, "visual_source_request_invalid")]
    [InlineData(true, "read_source", true, 1025, null, "visual_source_request_invalid")]
    public void Visual_source_opt_in_requires_enabled_mode_and_explicit_source_read(
        bool enabled, string operation, bool include, int? offset, int? limit, string code)
    {
        var request = new AdvancedAnalysisSearchRequest("read", Operation: operation,
            IncludeVisualSources: include, VisualSourceOffset: offset, VisualSourceLimit: limit);
        var error = Assert.Throws<AdvancedAnalysisToolException>(() =>
            AdvancedAnalysisVisualSourceReader.ValidateRequest(request, enabled));
        Assert.Equal(code, error.ErrorCode);
    }

    [Fact]
    public void Visual_source_unicode_observed_locator_uses_the_provider_encoder_for_actual_json_measurement()
    {
        var polygon = new CanonicalPolygon
        {
            Points = [new() { X = .1, Y = .1 }, new() { X = .2, Y = .1 }, new() { X = .2, Y = .2 }]
        };
        var region = new AdvancedAnalysisVisualObservedRegion("E1", "ancre_é", "région_observée", [], polygon);
        var result = AdvancedAnalysisVisualSourceProjector.Project(Doc, Revision, Hash, 2, 2, true, 1,
            [Asset()], 0, 4, 20_000, new Dictionary<int, AdvancedAnalysisVisualObservedRegion> { [2] = region });
        Assert.Equal(JsonSerializer.Serialize(new { visualSources = result }, Json).Length, result.SerializedWrapperCharacters);
    }
}
