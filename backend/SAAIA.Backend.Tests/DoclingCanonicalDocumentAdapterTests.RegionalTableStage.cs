using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Regional_table_stage_counts_explicit_attempts_separately_from_summary_and_budget_rows()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true},
             {"table_ref":"#/tables/1","status":"not_applied","reason":"region_budget_exhausted","attempted":false},
             {"status":"not_applied","reason":"document_time_or_scan_budget_exhausted","uninspected_table_count":4}]
            """);
        SetTableStageSourceCount(response, 6);

        var stage = TableStage(response);

        Assert.Equal("1", stage.Attributes["observedAttemptCount"]);
        Assert.Equal("3", stage.Attributes["observedSummaryCount"]);
        Assert.Equal("2", stage.Attributes["candidateTableCount"]);
        Assert.Equal("4", stage.Attributes["uninspectedTableCount"]);
        Assert.Equal("budget_limited", stage.Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_legacy_missing_attempt_flag_is_unknown_not_an_attempt()
    {
        var stage = TableStage(TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied"}]
            """));

        Assert.Equal("unknown", stage.Attributes["observedAttemptCount"]);
        Assert.Equal("unavailable", stage.Attributes["attemptCountAvailability"]);
        Assert.Equal("1", stage.Attributes["appliedTableCount"]);
    }

    [Fact]
    public void Regional_table_stage_missing_work_metadata_does_not_claim_zero_candidates()
    {
        var response = TableStageResponse(null);
        var stage = TableStage(response);

        Assert.Equal("unknown", stage.Attributes["observedAttemptCount"]);
        Assert.Equal("unknown", stage.Attributes["observedSummaryCount"]);
        Assert.Equal("unknown", stage.Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_explicit_complete_zero_summary_establishes_no_candidates()
    {
        var response = TableStageResponse("[]", """
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":1,
             "uninspected_table_count":0,"candidate_count":0,"attempted_count":0,"applied_count":0,
             "duration_ms":12,"limit_max_regions":8,"limit_total_timeout_seconds":60}
            """);

        var stage = TableStage(response);

        Assert.Equal("no_candidates_observed", stage.Attributes["outcome"]);
        Assert.Equal("0", stage.Attributes["observedAttemptCount"]);
        Assert.Equal("0", stage.Attributes["candidateTableCount"]);
        Assert.Equal(12, stage.DurationMs);
    }

    [Theory]
    [InlineData("error", "regional_engine_unavailable_or_invalid", true, "degraded")]
    [InlineData("not_applied", "region_budget_exhausted", false, "budget_limited")]
    public void Regional_table_stage_distinguishes_engine_failure_from_budget_limits(
        string status, string reason, bool attempted, string expected)
    {
        var summaries = JsonSerializer.Serialize(new[] { new
        {
            table_ref = "#/tables/0", status, reason, attempted
        } });

        Assert.Equal(expected, TableStage(TableStageResponse(summaries)).Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_hashes_observed_budgets_and_sanitizes_unknown_metadata()
    {
        var one = TableStageResponse("[]");
        var two = TableStageResponse("[]", maxRegions: 7);
        var stage = TableStage(one);
        var changed = TableStage(two);

        Assert.NotEqual(stage.OptionsSha256, changed.OptionsSha256);
        Assert.Contains("\"max_regions\":8", stage.Attributes["observedSettings"]);
        Assert.Contains("\"process_isolation\":\"per_region_linux_process_group\"", stage.Attributes["observedSettings"]);
        Assert.DoesNotContain("PRIVATE_SENTINEL", JsonSerializer.Serialize(stage));
    }

    [Fact]
    public void Regional_table_stage_records_observed_weight_and_cpu_identity_without_global_docling_revision_claim()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """);
        var node = JsonSerializer.SerializeToNode(response)!.AsObject();
        var table = node["document"]!["json_content"]!["tables"]![0]!;
        table["saaia_table_repair"] = JsonNode.Parse("""
            {"schema_version":"saaia_table_text_repair_v1","engine":"RapidOCR","engine_version":"3.9.1",
             "model_id":"PP-OCRv6_det_small+PP-OCRv6_rec_small","ocr_observation":{
               "engine":"rapidocr","engine_version":"3.9.1","model_id":"regional-model",
               "model_artifacts":{"det.onnx":"","rec.onnx":""},
               "runtime_settings":{"providers":["CPUExecutionProvider"],"observed_session_count":3,
                   "onnxruntime_intra_op_num_threads":2,"onnxruntime_inter_op_num_threads":1},
               "lines":[{"text":"PRIVATE_SENTINEL","confidence":0.99,"polygon":[[1,1],[2,1],[2,2],[1,2]]}]}}
            """);
        table["saaia_table_repair"]!["ocr_observation"]!["model_artifacts"]!["det.onnx"] = new string('a', 64);
        table["saaia_table_repair"]!["ocr_observation"]!["model_artifacts"]!["rec.onnx"] = new string('b', 64);
        response = JsonSerializer.Deserialize<DoclingConvertResponse>(node.ToJsonString())!;

        var stage = TableStage(response);

        Assert.Null(stage.ModelRevision);
        Assert.Null(stage.ModelId);
        Assert.Contains(new string('a', 64), stage.Attributes["observedModelArtifacts"]);
        Assert.Contains("CPUExecutionProvider", stage.Attributes["observedRuntimeSettings"]);
        Assert.DoesNotContain("PRIVATE_SENTINEL", JsonSerializer.Serialize(stage));
        Assert.Equal("per_observation_provenance_not_global_model_attestation", stage.Attributes["modelIdentityScope"]);
    }

    [Fact]
    public void Regional_table_stage_keeps_primary_ocr_engine_and_unrelated_timings_unchanged()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"error","attempted":true}]
            """);
        response.Timings["ocr"] = new() { Scope = "page", Count = 1, Times = [0.25] };
        var stages = DoclingIngestionStageManifestFactory.Create(TableStageOptions(), new(), BuildRagOptions(), response,
            new(1, 2, 3, 4), "table-stage-tests");

        Assert.Equal("RapidOCR", Assert.Single(stages, stage => stage.StageId == "docling_ocr").Engine);
        Assert.Equal(250, Assert.Single(stages, stage => stage.StageId == "docling_ocr").DurationMs);
        Assert.Equal("degraded", Assert.Single(stages, stage => stage.StageId == "regional_table_ocr_repair").Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_uninspected_tables_do_not_become_an_observed_zero_attempt_count()
    {
        var response = TableStageResponse("[]", """
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":0,
             "uninspected_table_count":1,"candidate_count":0,"attempted_count":0,"applied_count":0,"duration_ms":1}
            """);

        var stage = TableStage(response);

        Assert.Equal("budget_limited", stage.Attributes["outcome"]);
        Assert.Equal("unknown", stage.Attributes["observedAttemptCount"]);
        Assert.Equal("1", stage.Attributes["uninspectedTableCount"]);
    }

    [Fact]
    public void Regional_table_stage_contradictory_aggregate_cannot_qualify_completed_work()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"not_applied","attempted":true,"reason":"unchanged"}]
            """, """
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":1,
             "uninspected_table_count":0,"candidate_count":1,"attempted_count":0,"applied_count":0,"duration_ms":1}
            """);

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("invalid", stage.Attributes["attemptCountAvailability"]);
    }

    [Fact]
    public void Regional_table_stage_observation_remains_bound_to_its_actual_source_table_after_filtering()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/1","status":"applied","attempted":true}]
            """);
        response.Document.JsonContent!.Tables.Add(new()
        {
            SelfRef = "#/tables/1",
            TableRepair = new()
            {
                OcrObservationJson = JsonSerializer.SerializeToElement(new
                {
                    engine = "rapidocr", engine_version = "3.9.1", model_id = "regional-model",
                    model_artifacts = new Dictionary<string, string> { ["det.onnx"] = new string('a', 64) },
                    runtime_settings = new { providers = new[] { "CPUExecutionProvider" }, observed_session_count = 3,
                        onnxruntime_intra_op_num_threads = 2, onnxruntime_inter_op_num_threads = 1 }
                })
            }
        });

        var stage = TableStage(response);
        foreach (var key in new[] { "observedModelArtifacts", "observedRuntimeSettings" })
        {
            using var parsed = JsonDocument.Parse(stage.Attributes[key]);
            var observation = Assert.Single(parsed.RootElement.EnumerateArray());
            Assert.Equal("#/tables/1", observation.GetProperty("tableRef").GetString());
            Assert.Equal(1, observation.GetProperty("sourceTableOrdinal").GetInt32());
        }
    }

    private static IngestionStageManifest TableStage(DoclingConvertResponse response)
        => Assert.Single(DoclingIngestionStageManifestFactory.Create(TableStageOptions(), new(), BuildRagOptions(), response,
            new(1, 2, 3, 4), "table-stage-tests"), stage => stage.StageId == "regional_table_ocr_repair");

    private static DocumentIntelligenceOptions TableStageOptions()
        => new() { RegionalTableRepairMinimumScore = 0.8 };

    private static DoclingConvertResponse TableStageResponse(string? summaries, string? aggregate = null, int maxRegions = 8)
    {
        var root = JsonSerializer.SerializeToNode(BuildResponse())!.AsObject();
        root["saaia_execution"] = JsonNode.Parse("""
            {"table_repair_enabled":true,"table_repair_scale":2.5,"table_repair_padding_points":12,
             "table_repair_minimum_score":0.8,"table_repair_settings":{
                "schema_version":"saaia_table_repair_settings_v2","max_regions":8,
                "total_timeout_seconds":60,"region_timeout_seconds":15,"render_scale":2.5,
                "padding_points":12,"minimum_score":0.8,"max_rendered_pixels":20000000,
                "process_isolation":"per_region_linux_process_group","engine":"rapidocr","secondary_engine":null,
                "model_resolution":"bundled_local_files_only","unknown_private_field":"PRIVATE_SENTINEL"}}
            """);
        root["saaia_execution"]!["table_repair_settings"]!["max_regions"] = maxRegions;
        if (summaries is null)
            root.Remove("saaia_table_repairs");
        else
            root["saaia_table_repairs"] = JsonNode.Parse(summaries);
        if (aggregate is not null)
            root["saaia_table_repair_summary"] = JsonNode.Parse(aggregate);
        return JsonSerializer.Deserialize<DoclingConvertResponse>(root.ToJsonString())!;
    }
}
