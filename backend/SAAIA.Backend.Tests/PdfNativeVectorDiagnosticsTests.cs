using System.Reflection;
using System.Text.Json;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeVectorDiagnosticsTests
{
    [Fact]
    public void Vector_diagnostics_curves_explain_partial_without_creating_rules()
    {
        var vector = Observe("50 100 m 100 110 150 120 200 100 c S\n");
        Assert.Equal("partial", vector.Availability);
        Assert.Equal("unsupported_native_vector_geometry", vector.Reason);
        Assert.Empty(vector.Rules);
        var diagnostics = Diagnostics(vector);
        Assert.Equal("complete", diagnostics.GetProperty("inspectionCoverage").GetString());
        Assert.Equal(1, diagnostics.GetProperty("rejectionCounts").GetProperty("unsupported_command").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("supportedPrimitives").GetInt32());
        var sample = Assert.Single(diagnostics.GetProperty("samples").EnumerateArray());
        Assert.Equal("unsupported_command", sample.GetProperty("reasonCode").GetString());
        Assert.Contains("cubic_bezier", sample.GetProperty("commandKinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("unknown", diagnostics.GetProperty("paintVisibility").GetString());
        Assert.Equal("unknown", diagnostics.GetProperty("effectiveClip").GetString());
    }

    [Theory]
    [InlineData("1 w [3 2] 0 d 50 100 m 250 100 l S\n", "dash_or_cap")]
    [InlineData("1 w 50 100 m 250 120 l S\n", "non_finite_or_non_axis_edges")]
    public void Vector_diagnostics_fixed_reasons_preserve_refusals(string commands, string reason)
    {
        var vector = Observe(commands);
        Assert.Equal("partial", vector.Availability);
        Assert.Empty(vector.Rules);
        Assert.Equal(1, Diagnostics(vector).GetProperty("rejectionCounts").GetProperty(reason).GetInt32());
    }

    [Fact]
    public void Vector_diagnostics_observed_empty_is_known_zero_without_claiming_visibility()
    {
        var vector = Observe("");
        Assert.Equal("observed", vector.Availability);
        Assert.Empty(vector.Rules);
        var diagnostics = Diagnostics(vector);
        Assert.Equal("complete", diagnostics.GetProperty("inspectionCoverage").GetString());
        Assert.Equal(0, diagnostics.GetProperty("primitivesInspected").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("supportedPrimitives").GetInt32());
        Assert.Empty(diagnostics.GetProperty("samples").EnumerateArray());
    }

    [Fact]
    public void Vector_diagnostics_unavailable_frame_is_not_an_observed_zero()
    {
        var vector = Observe("50 100 200 .5 re f\n", rotation: 90);
        Assert.Equal("unavailable", vector.Availability);
        var diagnostics = Diagnostics(vector);
        Assert.Equal("unavailable", diagnostics.GetProperty("inspectionCoverage").GetString());
        Assert.False(diagnostics.TryGetProperty("primitivesInspected", out var count) && count.ValueKind != JsonValueKind.Null);
        Assert.False(diagnostics.TryGetProperty("supportedPrimitives", out var supported) && supported.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void Vector_diagnostics_samples_are_bounded_but_counts_preserve_all_observed_rejections()
    {
        var commands = string.Concat(Enumerable.Repeat("50 100 m 100 110 150 120 200 100 c S\n", 20));
        var vector = Observe(commands);
        Assert.Equal("partial", vector.Availability);
        Assert.Empty(vector.Rules);
        var diagnostics = Diagnostics(vector);
        Assert.Equal(20, diagnostics.GetProperty("primitivesInspected").GetInt32());
        Assert.Equal(20, diagnostics.GetProperty("rejectionCounts").GetProperty("unsupported_command").GetInt32());
        Assert.Equal(8, diagnostics.GetProperty("samples").GetArrayLength());
        Assert.Equal(12, diagnostics.GetProperty("samplesOmitted").GetInt32());
        Assert.All(diagnostics.GetProperty("samples").EnumerateArray(), sample =>
            Assert.Matches("^page_1_path_[0-9]+_subpath_[0-9]+$", sample.GetProperty("primitiveId").GetString()!));
        Assert.DoesNotContain("SOURCE LITERAL", diagnostics.GetRawText());
    }

    [Fact]
    public void Vector_diagnostics_command_budget_keeps_partial_counters_and_empty_refused_rules()
    {
        using var pdf = PdfDocument.Open(PdfNativeVectorGeometryTests.BuildPdf("50 100 200 .5 re f\n"));
        var vector = PdfNativeVectorGeometryObserver.Observe(pdf.GetPage(1), pdf.Structure,
            new string('a', 64), 100, default, maximumCommands: 1);
        Assert.Equal("refused", vector.Availability);
        Assert.Equal("page_vector_command_limit", vector.Reason);
        Assert.Empty(vector.Rules);
        Assert.Equal(1, vector.InspectedCommandCount);
        var diagnostics = Diagnostics(vector);
        Assert.Equal("partial", diagnostics.GetProperty("inspectionCoverage").GetString());
        Assert.Equal(1, diagnostics.GetProperty("primitivesEntered").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("primitivesInspected").GetInt32());
    }

    [Fact]
    public void Vector_diagnostics_do_not_change_supported_rules_or_primary_source()
    {
        var bytes = PdfNativeVectorGeometryTests.BuildPdf("50 100 200 .5 re f\n");
        using var enabled = new MemoryStream(bytes);
        using var disabled = new MemoryStream(bytes);
        var on = Assert.Single(PdfExtractor.Extract(enabled, default, true).Pages);
        var off = Assert.Single(PdfExtractor.Extract(disabled, default).Pages);
        Assert.Equal(off.Text, on.Text);
        Assert.Equal(off.Checksum, on.Checksum);
        Assert.Null(off.NativeVectorObservation);
        Assert.Equal("observed", on.NativeVectorObservation!.Availability);
        var rule = Assert.Single(on.NativeVectorObservation.Rules);
        Assert.Equal(("h", 699.75, 50d, 250d, .5), (rule.Axis, rule.Coordinate, rule.Start, rule.End, rule.Thickness));
        Assert.Equal(1, Diagnostics(on.NativeVectorObservation).GetProperty("supportedPrimitives").GetInt32());
    }

    [Fact]
    public void Vector_diagnostics_manifest_hook_is_optional_and_feature_off_does_not_add_a_stage()
    {
        var method = typeof(DoclingIngestionStageManifestFactory).GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!;
        var parameter = Assert.Single(method.GetParameters(), value => value.Name == "nativeVectorExtraction");
        Assert.True(parameter.IsOptional);
        var options = new DocumentIntelligenceOptions { RegionalTableAlternativeReadingsEnabled = false };
        var stages = CreateStages(method, options, Native(Observe("50 100 200 .5 re f\n")));
        Assert.DoesNotContain(stages, stage => stage.StageId == "native_pdf_vector_observation");
    }

    [Fact]
    public void Vector_diagnostics_stage_persists_content_free_reasons_and_source_page_sample()
    {
        var method = typeof(DoclingIngestionStageManifestFactory).GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!;
        Assert.Contains(method.GetParameters(), value => value.Name == "nativeVectorExtraction");
        var stages = CreateStages(method, new() { RegionalTableAlternativeReadingsEnabled = true },
            Native(Observe("50 100 m 100 110 150 120 200 100 c S\n")));
        var stage = Assert.Single(stages, value => value.StageId == "native_pdf_vector_observation");
        Assert.Equal("observed", stage.Attributes["countsAvailability"]);
        Assert.Equal("1", stage.Attributes["rejection_unsupported_command"]);
        Assert.Equal("unknown", stage.Attributes["paintVisibility"]);
        Assert.DoesNotContain("SOURCE LITERAL", CanonicalContractJson.Serialize(stage));
        Assert.Contains("page_1_path_0_subpath_0", stage.Attributes["pageDiagnosticsJson"]);
    }

    private static JsonElement Diagnostics(PdfNativeVectorObservation vector)
    {
        using var json = JsonDocument.Parse(CanonicalContractJson.Serialize(vector));
        Assert.True(json.RootElement.TryGetProperty("diagnostics", out var diagnostics), "Observed diagnostics must be retained separately from Rules/Availability.");
        Assert.Equal("pdfpig_native_vector_diagnostics_v1", diagnostics.GetProperty("schemaVersion").GetString());
        return diagnostics.Clone();
    }

    private static PdfNativeVectorObservation Observe(string commands, int rotation = 0)
    {
        using var stream = new MemoryStream(PdfNativeVectorGeometryTests.BuildPdf(commands, rotation));
        return Assert.Single(PdfExtractor.Extract(stream, default, true).Pages).NativeVectorObservation!;
    }

    private static PdfExtractionResult Native(PdfNativeVectorObservation vector)
    {
        using var stream = new MemoryStream(PdfNativeVectorGeometryTests.BuildPdf(""));
        var extraction = PdfExtractor.Extract(stream, default);
        return extraction with { Pages = [extraction.Pages[0] with { NativeVectorObservation = vector }] };
    }

    private static IReadOnlyList<IngestionStageManifest> CreateStages(MethodInfo method,
        DocumentIntelligenceOptions options, PdfExtractionResult native)
    {
        var parameters = method.GetParameters();
        var response = new DoclingConvertResponse { Document = new() { JsonContent = new() { SchemaName = "DoclingDocument", Version = "1" } } };
        if (options.RegionalTableAlternativeReadingsEnabled)
        {
            var settings = JsonSerializer.SerializeToElement(new
            {
                schema_version = "saaia_table_observation_settings_v1", enabled = true,
                repair_enabled = options.RegionalTableRepairEnabled, primary_projection = false,
                max_regions = 8, total_timeout_seconds = 60, region_timeout_seconds = 15,
                max_rendered_pixels = 20_000_000, render_scale = options.RegionalTableRepairScale,
                padding_points = options.RegionalTableRepairPaddingPoints, minimum_score = options.RegionalTableRepairMinimumScore,
                selection_policy = "sparse_then_descending_table_tail_v1", budget_scope = "shared_regional_table_work",
                process_isolation = "per_region_linux_process_group", engine = "rapidocr"
            });
            response.Execution = new() { TableObservationSettingsJson = settings };
            response.Document.JsonContent!.TableRegionObservations = new()
            {
                SchemaVersion = "saaia_table_region_observations_v1", Availability = "observed",
                SourceSha256 = new string('a', 64), Settings = settings, SourceTableCount = 0,
                InspectedTableCount = 0, UninspectedTableCount = 0, AttemptedRegionCount = 0,
                CompletedObservationCount = 0, Attempts = [], Observations = []
            };
        }
        object?[] args = [options, new IngestionOptions(), new RagOptions
        {
            QdrantCollection = "fixture", EmbeddingsModel = "fixture", EmbeddingsModelRevision = "fixture",
            EmbeddingsRuntimeRevision = "sha256:fixture", QdrantRuntimeRevision = "sha256:fixture"
        }, response,
        new DoclingIngestionStageTimings(0, 0, 0, 0), "fixture-code", null, null, null, native];
        return Assert.IsAssignableFrom<IReadOnlyList<IngestionStageManifest>>(method.Invoke(null, args.Take(parameters.Length).ToArray()));
    }
}
