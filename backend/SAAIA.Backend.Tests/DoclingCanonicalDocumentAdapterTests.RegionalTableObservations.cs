using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Regional_table_inconclusive_completed_attempt_keeps_observed_models_without_summary_content()
    {
        var response = TableObservationResponse();

        var stage = TableStage(response);

        Assert.Equal("observed", stage.Attributes["modelObservationAvailability"]);
        Assert.Contains(new string('c', 64), stage.Attributes["observedModelArtifacts"]);
        Assert.Contains("CPUExecutionProvider", stage.Attributes["observedRuntimeSettings"]);
        Assert.DoesNotContain("PRIVATE_OCR_READING", JsonSerializer.Serialize(stage));
        Assert.DoesNotContain("PRIVATE_OCR_READING", JsonSerializer.Serialize(response.TableRepairs));
    }

    [Theory]
    [InlineData("error", "regional_engine_unavailable_or_invalid")]
    [InlineData("not_applied", "region_or_document_time_budget_exhausted")]
    public void Regional_table_uncompleted_attempt_cannot_attest_a_model_observation(string status, string reason)
    {
        var stage = TableStage(TableObservationResponse(status: status, reason: reason));

        Assert.Equal("invalid", stage.Attributes["modelObservationAvailability"]);
        Assert.Equal("[]", stage.Attributes["observedModelArtifacts"]);
    }

    [Fact]
    public void Regional_table_attempt_observation_must_match_its_actual_table_reference()
    {
        var stage = TableStage(TableObservationResponse(attemptRef: "#/tables/1"));

        Assert.Equal("invalid", stage.Attributes["modelObservationAvailability"]);
        Assert.Equal("[]", stage.Attributes["observedRuntimeSettings"]);
    }

    [Fact]
    public void Regional_table_attempt_observation_requires_an_explicit_true_attempt_flag()
    {
        var stage = TableStage(TableObservationResponse(attempted: false));

        Assert.Equal("invalid", stage.Attributes["modelObservationAvailability"]);
    }

    [Fact]
    public void Regional_table_applied_and_inconclusive_observations_are_not_arbitrarily_selected()
    {
        var response = TableObservationResponse();
        var node = JsonSerializer.SerializeToNode(response)!;
        var table = node["document"]!["json_content"]!["tables"]![0]!;
        table["saaia_table_repair"] = new JsonObject
        {
            ["schema_version"] = "saaia_table_text_repair_v1",
            ["ocr_observation"] = TableObservationNode()
        };
        response = JsonSerializer.Deserialize<DoclingConvertResponse>(node.ToJsonString())!;

        var stage = TableStage(response);

        Assert.Equal("invalid", stage.Attributes["modelObservationAvailability"]);
        Assert.Equal("[]", stage.Attributes["observedModelArtifacts"]);
    }

    [Fact]
    public void Regional_table_legacy_attempt_without_observation_stays_unattested()
    {
        var response = TableObservationResponse();
        var node = JsonSerializer.SerializeToNode(response)!;
        node["document"]!["json_content"]!["tables"]![0]!["saaia_table_repair_attempt"]!
            .AsObject().Remove("ocr_observation");
        response = JsonSerializer.Deserialize<DoclingConvertResponse>(node.ToJsonString())!;

        Assert.Equal("unattested", TableStage(response).Attributes["modelObservationAvailability"]);
    }

    private static DoclingConvertResponse TableObservationResponse(string status = "not_applied",
        string reason = "missing_cells_not_covered_by_ocr", string attemptRef = "#/tables/0", bool attempted = true)
    {
        var summary = new { schema_version = "saaia_table_text_repair_v1", table_ref = "#/tables/0",
            status, reason, attempted, ocr_duration_ms = 23 };
        var node = JsonSerializer.SerializeToNode(TableStageResponse(JsonSerializer.Serialize(new[] { summary })))!;
        var attempt = JsonSerializer.SerializeToNode(summary)!;
        attempt["table_ref"] = attemptRef;
        attempt["ocr_observation"] = TableObservationNode();
        node["document"]!["json_content"]!["tables"]![0]!["saaia_table_repair_attempt"] = attempt;
        return JsonSerializer.Deserialize<DoclingConvertResponse>(node.ToJsonString())!;
    }

    private static JsonNode TableObservationNode()
        => JsonSerializer.SerializeToNode(new
        {
            engine = "rapidocr", engine_version = "3.9.1", model_id = "regional-model",
            duration_ms = 23, model_artifacts = new Dictionary<string, string> { ["det.onnx"] = new string('c', 64) },
            runtime_settings = new { providers = new[] { "CPUExecutionProvider" }, observed_session_count = 3,
                onnxruntime_intra_op_num_threads = 2, onnxruntime_inter_op_num_threads = 1 },
            lines = new[] { new { text = "PRIVATE_OCR_READING", confidence = .99 } }
        })!;
}
