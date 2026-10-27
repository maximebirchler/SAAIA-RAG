using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Secondary_table_lines_reach_current_http_evidence_and_native_output_once(string protocol)
    {
        const string literal = "Le seuil est 17 bar. Preserve -5,25 °C and the exact \\\"marked\\\" value.";
        var evidence = SecondaryLineFixture(literal);
        var arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"tableObservationId":"observation-1","tableObservationView":"raw","tableObservationBandOrdinal":null,"tableObservationLineOffset":0,"tableObservationLineLimit":1}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(evidence);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal("raw", gateway.Searches[1].TableObservationView);
        Assert.Equal("observation-1", gateway.Searches[1].TableObservationId);
        foreach (var requestIndex in new[] { 1, 2 })
        {
            var user = WorkspaceUser(factory.Requests[requestIndex].Body);
            var visible = Assert.Single(user.GetProperty("evidence").EnumerateArray());
            Assert.Equal(literal, visible.GetProperty("content").GetString());
            Assert.Equal("table_region_observation_line", visible.GetProperty("evidenceKind").GetString());
            Assert.Equal("secondary_observation", visible.GetProperty("contentRole").GetString());
            Assert.Equal("line-1", visible.GetProperty("sourceObservation").GetProperty("lineId").GetString());
            Assert.Equal("not_verified", visible.GetProperty("sourceQuality").GetProperty("semanticAccuracy").GetString());
            var alternatives = user.GetProperty("alternativeTableReadings");
            var view = Assert.Single(alternatives.GetProperty("views").EnumerateArray());
            Assert.Equal("E1", Assert.Single(view.GetProperty("lines").EnumerateArray()).GetProperty("evidenceId").GetString());
            Assert.False(view.GetProperty("pdfCoverageVerified").GetBoolean());
            Assert.False(alternatives.GetProperty("imageInspectionAvailable").GetBoolean());
            Assert.Equal(1, CountLiteralInJsonStrings(factory.Requests[requestIndex].Body, literal));
        }
        using var wire = JsonDocument.Parse(factory.Requests[2].Body);
        var tool = wire.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Single(item => protocol == "responses"
                ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var output = JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        var outputLine = Assert.Single(output.RootElement.GetProperty("alternativeTableReadings")
            .GetProperty("views")[0].GetProperty("lines").EnumerateArray());
        Assert.Equal("E1", outputLine.GetProperty("evidenceId").GetString());
        Assert.Equal("line-1", outputLine.GetProperty("lineId").GetString());
        Assert.False(outputLine.TryGetProperty("text", out _));
        Assert.DoesNotContain(literal, output.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Secondary_table_line_is_whole_even_above_legacy_character_window(string protocol)
    {
        var literal = "Le seuil est 17 bar. " + new string('x', 2700) + " exact terminal -0,125 mm.";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 32_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(SecondaryLineFixture(literal)), CancellationToken.None);
        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(literal, visible.GetProperty("content").GetString());
        Assert.False(visible.GetProperty("contentTruncated").GetBoolean());
        Assert.Null(visible.GetProperty("nextContentOffset").GetString());
    }

    [Fact]
    public async Task Secondary_table_tools_and_roster_are_absent_for_legacy_sources()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        var user = WorkspaceUser(factory.Requests[1].Body);
        Assert.False(user.TryGetProperty("alternativeTableReadings", out _));
        Assert.DoesNotContain("tableObservationView", factory.Requests[1].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Secondary_table_oversized_line_is_refused_at_same_offset_without_any_literal_prefix(string protocol)
    {
        const string start = "Never admit this partial OCR line -7,5 mm: ";
        var literal = start + new string('q', 20_000);
        const string insufficient = """{"outcome":"insufficient_documentation","answerText":"La ligne entière ne tient pas dans la fenêtre courante.","claims":[]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(insufficient) : Completion(insufficient));
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(SecondaryLineFixture(literal)), CancellationToken.None);
        var request = factory.Requests[1].Body;
        var user = WorkspaceUser(request);
        var visible = Assert.Single(user.GetProperty("evidence").EnumerateArray());
        Assert.Equal(string.Empty, visible.GetProperty("content").GetString());
        var view = user.GetProperty("alternativeTableReadings").GetProperty("views")[0];
        Assert.Equal("line_does_not_fit_model_budget", view.GetProperty("status").GetString());
        Assert.Equal(0, view.GetProperty("nextLineOffset").GetInt32());
        Assert.Empty(view.GetProperty("lines").EnumerateArray());
        Assert.DoesNotContain(start, request, StringComparison.Ordinal);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(request), 1, 8_000);
    }

    [Theory]
    [InlineData("observed", 0)]
    [InlineData("unavailable", null)]
    public async Task Secondary_table_inventory_distinguishes_observed_empty_from_unknown(string state, int? count)
    {
        var original = SecondaryLineFixture("Le seuil est 17 bar.");
        var evidence = original with { SourceObservation = null,
            SourceOverview = original.SourceOverview! with { TableRegionObservations = new(new string('a', 64),
                state, count, count, state == "observed" ? [] : null, state == "observed" ? [] : null) } };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);
        var inventory = WorkspaceUser(factory.Requests[1].Body).GetProperty("alternativeTableReadings").GetProperty("inventories")[0];
        Assert.Equal(state, inventory.GetProperty("availability").GetString());
        if (count is null)
        {
            Assert.Equal(JsonValueKind.Null, inventory.GetProperty("observationCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, inventory.GetProperty("observations").ValueKind);
        }
        else
        {
            Assert.Equal(0, inventory.GetProperty("observationCount").GetInt32());
            Assert.Empty(inventory.GetProperty("observations").EnumerateArray());
        }
    }

    [Fact]
    public async Task Secondary_table_metadata_that_cannot_fit_is_explicitly_refused_before_model_http()
    {
        var fixture = SecondaryLineFixture("Le seuil est 17 bar.");
        var codes = Enumerable.Range(0, 64).Select(index => "code_" + index.ToString("D2") + "_" + new string('x', 150)).ToArray();
        var evidence = fixture with { SourceObservation = fixture.SourceObservation! with { QualitySignals = codes },
            SourceQuality = new(null, null, codes) };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"outcome":"insufficient_documentation","answerText":"Unknown.","claims":[]}"""));
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 8_000;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None));
        Assert.Equal("advanced_secondary_documentary_metadata_budget_exceeded", error.ErrorCode);
        Assert.Single(factory.Requests); // The ordinary planner precedes documentary admission.
    }

    [Fact]
    public void Secondary_table_wire_measure_includes_nested_escaped_tool_output_and_actual_wrappers()
    {
        var output = JsonSerializer.Serialize(new { alternativeTableReadings = new { lines = new[] { "quoted\"\\value", "é\t-3" } } });
        var user = JsonSerializer.Serialize(new { request = "unchanged", evidence = new[] { new { content = "exact\"\\line" } },
            structuredTables = Array.Empty<object>(), alternativeTableReadings = new { views = Array.Empty<object>() } });
        var messages = new object[] { new { role = "system", content = "not documentary" },
            new { role = "tool", tool_call_id = "call_1", content = output }, new { role = "user", content = user } };
        var wire = JsonSerializer.Serialize(new { messages }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var baseline = JsonSerializer.Serialize(new { messages = new object[] { messages[0],
            new { role = "user", content = JsonSerializer.Serialize(new { request = "unchanged" }) } } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(wire.Length - baseline.Length,
            OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire));
        Assert.True(wire.Length - baseline.Length > output.Length + "exact\"\\line".Length);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Secondary_table_line_cannot_be_read_with_a_primary_character_slice_tool(string protocol)
    {
        var primary = BuildEvidence("E1", "Le seuil est 17 bar.");
        var secondary = SecondaryLineFixture("An indivisible alternative line with -7,5 mm.", "E2");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", """{"evidenceId":"E2","offset":0,"length":256}""")
                : NativeCompletion(("read_evidence", """{"evidenceId":"E2","offset":0,"length":256}""")),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(primary, secondary);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Single(gateway.Searches);
        using var wire = JsonDocument.Parse(factory.Requests[2].Body);
        var output = wire.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Single(item => protocol == "responses" ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var result = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal("batch_rejected_before_execution", result.RootElement.GetProperty("status").GetString());
    }

    private static AdvancedAnalysisResolvedEvidence SecondaryLineFixture(string literal, string evidenceId = "E1")
    {
        var item = BuildEvidence("E1", literal);
        return item with
        {
            Reference = new AdvancedAnalysisResultEvidence
            {
                EvidenceId = evidenceId, DocId = item.Reference.DocId, RevisionId = item.Reference.RevisionId,
                SourceHash = new string('a', 64), DocPath = item.Reference.DocPath, FileName = item.Reference.FileName,
                PageStart = 1, PageEnd = 1, AnchorId = "table-observation-anchor-" + new string('b', 64)
            },
            SourceOverview = new(1, 1, 0)
            {
                VisualAssets = new("available", 1, [1], false, "not_performed", false),
                TableRegionObservations = new(new string('a', 64), "observed", 1, 1,
                    [new(1, "observed", 1, 1)], [new("observation-1", 1, "table-1", "#/tables/0", 1, new string('c', 64), "unavailable", null, null)])
            },
            SourceObservation = new("observation-1", "table-1", "line-1", 0,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(literal))).ToLowerInvariant(),
                new string('c', 64), "raw", 0, null, 1, 0, 1, null, ["secondary_table_observation"]),
            SourceQuality = AdvancedAnalysisSourceQuality.FromMetadata(null, null, "[\"secondary_table_observation\"]")
        };
    }

    private static int CountLiteralInJsonStrings(string json, string literal)
    {
        using var document = JsonDocument.Parse(json);
        return Visit(document.RootElement);
        int Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().Sum(property => Visit(property.Value));
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Sum(Visit);
            if (value.ValueKind != JsonValueKind.String) return 0;
            var text = value.GetString()!;
            // Wire content fields contain nested JSON. Decode each JSON envelope
            // rather than counting escaped representations as a different text.
            if (text.StartsWith('{') || text.StartsWith('['))
            {
                try { using var nested = JsonDocument.Parse(text); return Visit(nested.RootElement); }
                catch (JsonException) { }
            }
            return text.Split(literal, StringSplitOptions.None).Length - 1;
        }
    }
}
