using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("#/tables/1")]
    [InlineData("#/tables/999999999")]
    public void Regional_table_stage_summary_cannot_name_an_unobserved_or_out_of_inventory_source_table(string sourceRef)
    {
        var response = TableStageResponse(JsonSerializer.Serialize(new[] { new
        {
            table_ref = sourceRef, status = "applied", attempted = true
        } }));
        SetTableStageSourceCount(response, 1);

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("invalid", stage.Attributes["attemptCountAvailability"]);
    }

    [Fact]
    public void Regional_table_stage_duplicate_actual_source_refs_are_not_unique_provenance()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """);
        SetTableStageSourceCount(response, 2);
        response.Document.JsonContent!.Tables[1].SelfRef = "#/tables/0";

        Assert.Equal("degraded", TableStage(response).Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_reference_must_resolve_to_its_actual_source_array_item()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """);
        SetTableStageSourceCount(response, 2);
        (response.Document.JsonContent!.Tables[0], response.Document.JsonContent.Tables[1]) =
            (response.Document.JsonContent.Tables[1], response.Document.JsonContent.Tables[0]);

        Assert.Equal("degraded", TableStage(response).Attributes["outcome"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Regional_table_stage_malformed_aggregate_is_not_an_available_remaining_table_counter(int remaining)
    {
        var response = TableStageResponse(JsonSerializer.Serialize(new[] { new
        {
            status = "not_applied", reason = "document_time_or_scan_budget_exhausted",
            uninspected_table_count = remaining
        } }));
        SetTableStageSourceCount(response, 2);

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("unknown", stage.Attributes["uninspectedTableCount"]);
    }

    [Fact]
    public void Regional_table_stage_two_aggregates_do_not_establish_scan_coverage()
    {
        var response = TableStageResponse("""
            [{"status":"not_applied","reason":"document_time_or_scan_budget_exhausted","uninspected_table_count":2},
             {"status":"not_applied","reason":"document_time_or_scan_budget_exhausted","uninspected_table_count":3}]
            """);
        SetTableStageSourceCount(response, 6);

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("unknown", stage.Attributes["uninspectedTableCount"]);
    }

    [Fact]
    public void Regional_table_stage_elapsed_time_over_the_observed_budget_is_not_completed_or_clamped()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """, """
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":1,
             "uninspected_table_count":0,"candidate_count":1,"attempted_count":1,"applied_count":1,
             "duration_ms":120000,"limit_max_regions":8,"limit_total_timeout_seconds":60}
            """);

        var stage = TableStage(response);

        Assert.NotEqual("completed", stage.Attributes["outcome"]);
        Assert.Equal(120000, stage.DurationMs);
    }

    [Theory]
    [InlineData("1e100")]
    [InlineData("-1")]
    [InlineData("\"not_a_duration\"")]
    public void Regional_table_stage_malformed_duration_is_unknown_and_degraded(string durationJson)
    {
        var aggregate = JsonNode.Parse("""
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":1,
             "uninspected_table_count":0,"candidate_count":1,"attempted_count":1,"applied_count":1,"duration_ms":0}
            """)!;
        aggregate["duration_ms"] = JsonNode.Parse(durationJson);
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """, aggregate.ToJsonString());

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Null(stage.DurationMs);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    public void Regional_table_stage_nonobject_settings_do_not_echo_unknown_input_or_claim_a_valid_configuration(string settingsJson)
    {
        var node = JsonSerializer.SerializeToNode(TableStageResponse("[]"))!;
        node["saaia_execution"]!["table_repair_settings"] = JsonNode.Parse(settingsJson);
        var response = JsonSerializer.Deserialize<DoclingConvertResponse>(node.ToJsonString())!;

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("invalid", stage.Attributes["configurationAvailability"]);
        Assert.Equal("unknown", stage.Attributes["observedAttemptCount"]);
    }

    [Fact]
    public void Regional_table_stage_unrecognized_repair_schema_is_not_completed_work()
    {
        var response = TableStageResponse("""
            [{"schema_version":"unrecognized_future_protocol","table_ref":"#/tables/0","status":"applied","attempted":true}]
            """);

        Assert.Equal("degraded", TableStage(response).Attributes["outcome"]);
    }

    [Fact]
    public void Regional_table_stage_cannot_treat_an_omitted_source_table_as_an_observed_complete_scan()
    {
        var response = TableStageResponse("""
            [{"table_ref":"#/tables/0","status":"applied","attempted":true}]
            """, """
            {"schema_version":"saaia_table_text_repair_v1","inspected_table_count":1,
             "uninspected_table_count":0,"candidate_count":1,"attempted_count":1,"applied_count":1,"duration_ms":1}
            """);
        SetTableStageSourceCount(response, 2);

        var stage = TableStage(response);

        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("unknown", stage.Attributes["uninspectedTableCount"]);
    }

    private static void SetTableStageSourceCount(DoclingConvertResponse response, int count)
    {
        var source = response.Document.JsonContent!;
        var template = JsonSerializer.Serialize(source.Tables[0]);
        source.Tables.Clear();
        for (var index = 0; index < count; index++)
        {
            var table = JsonSerializer.Deserialize<DoclingTableItem>(template)!;
            table.SelfRef = "#/tables/" + index;
            source.Tables.Add(table);
        }
    }
}
