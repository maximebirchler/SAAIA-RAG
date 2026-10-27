using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Secondary_table_observation_is_source_bound_and_does_not_enter_primary_projections()
    {
        var baselineSource = BuildDocument();
        var baseline = DoclingCanonicalDocumentAdapter.Project(BuildContext(), baselineSource);
        var source = BuildDocument();
        source.TableRegionObservations = SecondaryEnvelope();

        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var observation = Assert.Single(Assert.Single(document.Pages).TableRegionObservations!.Observations!);
        Assert.Equal(document.DocumentId, observation.DocumentId);
        Assert.Equal(document.RevisionId, observation.RevisionId);
        Assert.Equal(document.Source.Sha256, observation.SourceSha256);
        Assert.Equal(1, observation.PhysicalPage);
        Assert.Equal(document.Pages[0].Tables[0].TableId, observation.TableId);
        Assert.Equal("#/tables/0", observation.SourceTableRef);
        Assert.Equal("not_verified", observation.SemanticAccuracy);
        Assert.False(observation.CanonicalGridRepaired);
        var line = Assert.Single(observation.Lines);
        Assert.Equal("SECONDARY_LITERAL & +5", line.Text);
        Assert.Equal(.12, line.Polygon.Points[0].X, 6);
        Assert.Equal(.525, line.Polygon.Points[0].Y, 6);
        Assert.Equal(new[] { line.LineId }, observation.PhysicalReading.UnassignedLineIds);
        Assert.Equal("unavailable", observation.PhysicalReading.Availability);
        Assert.Equal(observation.RawObservationSha256, Sha(observation.PublicationRawBytes!));
        Assert.DoesNotContain("PublicationRawBytes", CanonicalContractJson.SerializeCanonical(observation));
        Assert.Equal(CanonicalContractJson.SerializeCanonical(baseline.Pages[0].Tables),
            CanonicalContractJson.SerializeCanonical(document.Pages[0].Tables));
        Assert.Equal(CanonicalContractJson.SerializeCanonical(baseline.Pages[0].Blocks),
            CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks));
        Assert.Equal(JsonSerializer.Serialize(DoclingCanonicalRetrievalProjector.Project(baseline, baselineSource, 220, 1)),
            JsonSerializer.Serialize(DoclingCanonicalRetrievalProjector.Project(document, source, 220, 1)));
    }

    [Fact]
    public void Secondary_table_feature_off_omits_new_json_and_true_changes_pipeline_identity()
    {
        var options = new DocumentIntelligenceOptions();
        Assert.False(options.RegionalTableAlternativeReadingsEnabled);
        Assert.DoesNotContain(nameof(options.RegionalTableAlternativeReadingsEnabled), JsonSerializer.Serialize(options));
        var before = IngestionPipelineIdentity.Compute(new RagOptions(), new IngestionOptions(), options);
        options.RegionalTableAlternativeReadingsEnabled = true;
        Assert.Contains(nameof(options.RegionalTableAlternativeReadingsEnabled), JsonSerializer.Serialize(options));
        Assert.NotEqual(before, IngestionPipelineIdentity.Compute(new RagOptions(), new IngestionOptions(), options));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), BuildDocument());
        Assert.DoesNotContain("tableRegionObservations", CanonicalContractJson.SerializeCanonical(canonical));
    }

    [Fact]
    public void Secondary_table_observed_empty_is_distinct_from_unavailable_and_legacy()
    {
        var emptySource = BuildDocument();
        emptySource.Tables.Clear();
        emptySource.Body.Children.RemoveAll(reference => reference.Ref == "#/tables/0");
        emptySource.TableRegionObservations = SecondaryEnvelope();
        emptySource.TableRegionObservations.SourceTableCount = 0;
        emptySource.TableRegionObservations.InspectedTableCount = 0;
        emptySource.TableRegionObservations.AttemptedRegionCount = 0;
        emptySource.TableRegionObservations.CompletedObservationCount = 0;
        emptySource.TableRegionObservations.Attempts = [];
        emptySource.TableRegionObservations.Observations = [];
        var empty = DoclingCanonicalDocumentAdapter.Project(BuildContext(), emptySource).Pages[0].TableRegionObservations!;
        Assert.Equal("observed", empty.Availability);
        Assert.Empty(empty.Observations!);
        Assert.Empty(empty.Attempts!);

        var source = BuildDocument();
        source.TableRegionObservations = SecondaryEnvelope();
        source.TableRegionObservations.Availability = "partial";
        source.TableRegionObservations.Reason = "regional_work_incomplete";
        source.TableRegionObservations.Observations = [];
        source.TableRegionObservations.CompletedObservationCount = 0;
        source.TableRegionObservations.Attempts![0].Status = "timeout";
        source.TableRegionObservations.Attempts[0].Reason = "region_or_document_time_budget_exhausted";
        source.TableRegionObservations.Attempts[0].RawObservationSha256 = null;
        var partial = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source).Pages[0].TableRegionObservations!;
        Assert.Equal("partial", partial.Availability);
        Assert.Empty(partial.Observations!);

        source.TableRegionObservations = new() { SchemaVersion = "saaia_table_region_observations_v1",
            SourceSha256 = new string('a', 64), Availability = "unavailable", Reason = "tables_inventory_unavailable" };
        var unavailable = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source).Pages[0].TableRegionObservations!;
        Assert.Equal("unavailable", unavailable.Availability);
        Assert.Null(unavailable.Observations);
        Assert.Null(unavailable.Attempts);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("rawhash")]
    [InlineData("table")]
    public void Secondary_table_observation_identity_or_payload_mismatch_is_rejected(string mutation)
    {
        var source = BuildDocument();
        source.TableRegionObservations = SecondaryEnvelope();
        if (mutation == "source") source.TableRegionObservations.SourceSha256 = new string('c', 64);
        if (mutation == "rawhash") source.TableRegionObservations.Observations![0].RawObservationSha256 = new string('d', 64);
        if (mutation == "table") source.TableRegionObservations.Observations![0].SourceTableRef = "#/tables/1";
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Theory]
    [InlineData("raw_bytes")]
    [InlineData("lines")]
    public void Secondary_table_predictable_limit_refuses_whole_observation_and_preserves_primary(string limit)
    {
        var source = BuildDocument();
        source.TableRegionObservations = SecondaryEnvelope(lineCount: limit == "lines" ? 5_001 : 1);
        if (limit == "raw_bytes") source.TableRegionObservations.Observations![0].RawObservationJson = new string(' ', 2_000_001);

        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var set = document.Pages[0].TableRegionObservations!;
        Assert.Equal("partial", set.Availability);
        Assert.Empty(set.Observations!);
        Assert.Equal("refused", Assert.Single(set.Attempts!).Status);
        Assert.Null(set.Attempts![0].ObservationId);
        Assert.Equal(4, document.Pages[0].Tables[0].Cells.Count);
        Assert.Equal(0, source.TableObservationProjection!.AdmittedObservations);
        Assert.Equal(1, source.TableObservationProjection.RefusedObservations);
    }

    [Fact]
    public void Secondary_table_activation_and_observed_settings_must_match_signed_options()
    {
        var response = BuildResponse();
        response.Document.JsonContent!.TableRegionObservations = SecondaryEnvelope();
        response.Execution = new() { TableObservationSettingsJson = SecondarySettings() };
        Assert.Throws<InvalidDataException>(() => DoclingTableRegionObservationPolicy.Validate(new(), response));
        var options = new DocumentIntelligenceOptions { RegionalTableAlternativeReadingsEnabled = true };
        DoclingTableRegionObservationPolicy.Validate(options, response);
        options.RegionalTableRepairScale = 3;
        Assert.Throws<InvalidDataException>(() => DoclingTableRegionObservationPolicy.Validate(options, response));
    }

    [Fact]
    public void Secondary_table_manifest_counts_raw_work_and_backend_admission_separately_without_text()
    {
        var response = BuildResponse();
        response.Document.JsonContent!.TableRegionObservations = SecondaryEnvelope();
        response.Execution = new() { TableObservationSettingsJson = SecondarySettings() };
        var options = new DocumentIntelligenceOptions { RegionalTableAlternativeReadingsEnabled = true };
        var before = DoclingTableRegionObservationStage.Create(options, response)!;
        Assert.Equal("unknown", before.Attributes["admittedObservationCount"]);
        DoclingCanonicalDocumentAdapter.Project(BuildContext(), response.Document.JsonContent);
        var after = DoclingTableRegionObservationStage.Create(options, response)!;
        Assert.Equal("1", after.Attributes["sidecarCompletedObservationCount"]);
        Assert.Equal("1", after.Attributes["admittedObservationCount"]);
        Assert.DoesNotContain("SECONDARY_LITERAL", JsonSerializer.Serialize(after));
        Assert.Null(after.ModelRevision);
        Assert.Equal("3.9.1", after.EngineVersion);
        var stages = DoclingIngestionStageManifestFactory.Create(options, new(), BuildRagOptions(), response,
            new(1, 1, 1, 1), "head-source-hash");
        Assert.Single(stages, stage => stage.StageId == "regional_table_secondary_observations");
    }

    [Fact]
    public void Secondary_table_invalid_raw_json_exposes_only_a_static_diagnostic_without_inner_exception()
    {
        var source = BuildDocument();
        source.TableRegionObservations = SecondaryEnvelope();
        var row = source.TableRegionObservations.Observations![0];
        row.RawObservationJson = "{PRIVATE_INVALID_SOURCE";
        row.RawObservationSha256 = Sha(Encoding.UTF8.GetBytes(row.RawObservationJson));
        source.TableRegionObservations.Attempts![0].RawObservationSha256 = row.RawObservationSha256;

        var error = Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));

        Assert.Equal("docling_table_observation_raw_invalid", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE_INVALID_SOURCE", error.ToString());
    }

    [Fact]
    public void Secondary_table_manifest_does_not_invent_an_engine_version_without_an_observation()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.TableRegionObservations = SecondaryEnvelope();
        source.TableRegionObservations.Availability = "partial";
        source.TableRegionObservations.Reason = "regional_work_incomplete";
        source.TableRegionObservations.Observations = [];
        source.TableRegionObservations.CompletedObservationCount = 0;
        source.TableRegionObservations.Attempts![0].Status = "timeout";
        source.TableRegionObservations.Attempts[0].Reason = "region_or_document_time_budget_exhausted";
        source.TableRegionObservations.Attempts[0].RawObservationSha256 = null;
        response.Execution = new() { TableObservationSettingsJson = SecondarySettings() };

        var stage = DoclingTableRegionObservationStage.Create(
            new() { RegionalTableAlternativeReadingsEnabled = true }, response)!;

        Assert.Equal("unattested", stage.EngineVersion);
    }

    [Fact]
    public void Secondary_table_unknown_hash_is_allowed_only_for_a_wholly_unavailable_channel()
    {
        var source = BuildDocument();
        source.TableRegionObservations = new()
        {
            SchemaVersion = "saaia_table_region_observations_v1", Availability = "unavailable",
            Reason = "source_hash_time_budget_exhausted", SourceSha256 = null
        };

        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        Assert.Equal(new string('a', 64), document.Source.Sha256);
        Assert.Equal(4, document.Pages[0].Tables[0].Cells.Count);
        Assert.Equal("unavailable", document.Pages[0].TableRegionObservations!.Availability);
        Assert.Null(document.Pages[0].TableRegionObservations!.Observations);
        source.TableRegionObservations.Observations = [];
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        source.TableRegionObservations = SecondaryEnvelope();
        source.TableRegionObservations.SourceSha256 = null;
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    private static DoclingTableRegionObservationEnvelope SecondaryEnvelope(int lineCount = 1)
    {
        var raw = JsonSerializer.Serialize(new
        {
            engine = "rapidocr", engine_version = "3.9.1", model_id = "PP-OCRv6_det_small+PP-OCRv6_rec_small",
            model_artifacts = new Dictionary<string, string> { ["det.onnx"] = new string('c', 64) },
            duration_ms = 5, coordinate_origin = "TOPLEFT", minimum_confidence = .99,
            runtime_settings = new { onnxruntime_intra_op_num_threads = 2, onnxruntime_inter_op_num_threads = 1,
                observed_session_count = 3, providers = new[] { "CPUExecutionProvider" } },
            lines = Enumerable.Range(0, lineCount).Select(_ => new { text = "SECONDARY_LITERAL & +5", confidence = .99,
                polygon = new[] { new[] { 12d, 105d }, new[] { 40d, 105d }, new[] { 40d, 110d }, new[] { 12d, 110d } } })
        });
        var sha = Sha(Encoding.UTF8.GetBytes(raw));
        return new()
        {
            SchemaVersion = "saaia_table_region_observations_v1", Availability = "observed", SourceSha256 = new string('a', 64),
            Settings = SecondarySettings(), SourceTableCount = 1, InspectedTableCount = 1, UninspectedTableCount = 0,
            AttemptedRegionCount = 1, CompletedObservationCount = 1,
            Attempts = [new() { SourceTableRef = "#/tables/0", PageNumber = 1, Attempted = true,
                Status = "completed", RawObservationSha256 = sha }],
            Observations = [new() { SourceTableRef = "#/tables/0", PageNumber = 1,
                CoordinateOrigin = "TOPLEFT", PageWidthPoints = 100, PageHeightPoints = 200,
                BoundingBox = Box(10, 100, 90, 150, "TOPLEFT"), RawObservationJson = raw, RawObservationSha256 = sha }]
        };
    }

    private static JsonElement SecondarySettings() => JsonSerializer.SerializeToElement(new
    {
        schema_version = "saaia_table_observation_settings_v1", enabled = true, repair_enabled = true,
        primary_projection = false, max_regions = 8, total_timeout_seconds = 60, region_timeout_seconds = 15,
        max_rendered_pixels = 20_000_000, render_scale = 2.5, padding_points = 12, minimum_score = .5,
        selection_policy = "sparse_then_descending_table_tail_v1", budget_scope = "shared_regional_table_work",
        process_isolation = "per_region_linux_process_group", engine = "rapidocr"
    });

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
