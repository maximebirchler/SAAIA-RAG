using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string StructuredQualityAnswer = """{"outcome":"answered","answerText":"Le seuil est -5 °C [C1].","claims":[{"claimId":"C1","text":"Le seuil est -5 °C.","evidenceIds":["E1"]}]}""";

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Structured_table_wire_keeps_a_warning_after_the_former_thirty_two_signal_limit(string protocol)
    {
        var signals = Enumerable.Range(0, 33).Select(index => "observed_warning_" + index)
            .Append("structured_transcription_not_verified").ToArray();
        var evidence = StructuredWireQualityEvidence(signals);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(StructuredQualityAnswer) : Completion(StructuredQualityAnswer));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 64_000;

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var user = WorkspaceUser(factory.Requests[1].Body);
        var table = Assert.Single(user.GetProperty("structuredTables").EnumerateArray());
        Assert.Equal(signals, table.GetProperty("qualitySignals").EnumerateArray().Select(item => item.GetString()));
        Assert.False(table.GetProperty("sourceFidelityValidated").GetBoolean());
        Assert.True(table.GetProperty("complete").GetBoolean());
        Assert.Contains("-5 °C", table.GetProperty("html").GetString());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Structured_table_wire_reports_quality_metadata_limits_instead_of_appearing_complete(string protocol)
    {
        var signals = Enumerable.Range(0, 70).Select(index => "observed_warning_" + index).ToArray();
        var evidence = StructuredWireQualityEvidence(signals);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(StructuredQualityAnswer) : Completion(StructuredQualityAnswer));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 64_000;

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var table = Assert.Single(WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("structuredTables").EnumerateArray());
        Assert.Equal(signals.Take(64), table.GetProperty("qualitySignals").EnumerateArray().Select(item => item.GetString()));
        Assert.True(table.GetProperty("qualityMetadataTruncated").GetBoolean());
        Assert.Equal(6, table.GetProperty("qualitySignalsOmittedCount").GetInt32());
        Assert.False(table.GetProperty("qualityMetadataMalformed").GetBoolean());
        Assert.False(table.GetProperty("sourceFidelityValidated").GetBoolean());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Structured_table_wire_reports_malformed_quality_without_discarding_canonical_rows(string protocol)
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        var evidence = StructuredWireQualityEvidence([]) with
        {
            TableFragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
                "[\"structured_transcription_not_verified\",17,\"bad warning\"]", map)
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(StructuredQualityAnswer) : Completion(StructuredQualityAnswer));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 64_000;

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var table = Assert.Single(WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("structuredTables").EnumerateArray());
        Assert.True(table.GetProperty("complete").GetBoolean());
        Assert.True(table.GetProperty("qualityMetadataMalformed").GetBoolean());
        Assert.False(table.GetProperty("qualityMetadataTruncated").GetBoolean());
        Assert.Equal(new[] { "structured_transcription_not_verified" },
            table.GetProperty("qualitySignals").EnumerateArray().Select(item => item.GetString()));
        Assert.False(table.GetProperty("sourceFidelityValidated").GetBoolean());
        Assert.Contains("-5 °C", table.GetProperty("html").GetString());
    }

    private static AdvancedAnalysisResolvedEvidence StructuredWireQualityEvidence(string[] signals)
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        return new(new AdvancedAnalysisResultEvidence
        {
            EvidenceId = "E1", DocId = "11111111-1111-1111-1111-111111111111",
            RevisionId = "22222222-2222-2222-2222-222222222222", SourceHash = new string('a', 64),
            PageStart = 1, PageEnd = 1, ChunkId = "33333333-3333-3333-3333-333333333333",
            AnchorId = "table-quality-anchor"
        }, html)
        {
            SourceQuality = null,
            TableFragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
                JsonSerializer.Serialize(signals), map)
        };
    }
}

public sealed class StructuredTableWireQualityPreservationTests
{
    [Fact]
    public void Oversized_quality_metadata_is_explicitly_unknown_without_invalidating_the_observed_grid()
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        var fragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
            "[" + new string(' ', 100_001) + "]", map)!;

        Assert.Null(fragment.ErrorCode);
        Assert.Equal(2, fragment.Rows.Count);
        var json = JsonSerializer.SerializeToElement(fragment, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.GetProperty("qualityMetadataMalformed").GetBoolean());
        Assert.True(json.GetProperty("qualityMetadataTruncated").GetBoolean());
        Assert.Empty(fragment.QualitySignals!);
        Assert.Equal(0, json.GetProperty("qualitySignalsOmittedCount").GetInt32());
    }

    [Fact]
    public void Malformed_quality_metadata_keeps_the_observed_grid_and_reports_the_diagnostic()
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        var fragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
            "[\"structured_transcription_not_verified\",17,\"bad warning\"]", map)!;

        Assert.Null(fragment.ErrorCode);
        Assert.Equal(2, fragment.Rows.Count);
        var json = JsonSerializer.SerializeToElement(fragment, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.GetProperty("qualityMetadataMalformed").GetBoolean());
        Assert.Equal(new[] { "structured_transcription_not_verified" },
            json.GetProperty("qualitySignals").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_or_invalid_cell_map_does_not_erase_observed_quality_warnings(bool invalidMap)
    {
        var (html, _) = StructuredTableWireQualityFixture.CreateGrid();
        var fragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
            "[\"structured_transcription_not_verified\"]", invalidMap ? "{" : null)!;

        Assert.Equal(invalidMap ? "structured_table_fragment_invalid" : "structured_table_cell_map_unavailable",
            fragment.ErrorCode);
        Assert.Equal(new[] { "structured_transcription_not_verified" }, fragment.QualitySignals);
    }

    [Fact]
    public void Merged_fragments_union_warnings_and_expose_the_combined_metadata_limit()
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        var firstSignals = Enumerable.Range(0, 40).Select(index => "warning_" + index).ToArray();
        var secondSignals = Enumerable.Range(20, 50).Select(index => "warning_" + index).ToArray();
        AdvancedAnalysisResolvedEvidence Evidence(string id, string[] signals) => new(new()
        {
            EvidenceId = id, DocId = "11111111-1111-1111-1111-111111111111",
            RevisionId = "22222222-2222-2222-2222-222222222222", SourceHash = new string('a', 64),
            PageStart = 1, PageEnd = 1, ChunkId = id + "-chunk", AnchorId = id + "-anchor"
        }, html, TableFragment: AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
            JsonSerializer.Serialize(signals), map));

        var view = Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build(
            [Evidence("E1", firstSignals), Evidence("E2", secondSignals)], null, 20_000));
        var json = JsonSerializer.SerializeToElement(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Enumerable.Range(0, 64).Select(index => "warning_" + index), view.QualitySignals);
        Assert.True(json.GetProperty("qualityMetadataTruncated").GetBoolean());
        Assert.Equal(6, json.GetProperty("qualitySignalsOmittedCount").GetInt32());
        Assert.True(view.Complete);
        Assert.False(view.SourceFidelityValidated);
    }

    [Fact]
    public void Duplicate_warning_sets_above_the_wire_limit_are_counted_once_after_union()
    {
        var (html, map) = StructuredTableWireQualityFixture.CreateGrid();
        var signals = Enumerable.Range(0, 70).Select(index => "warning_" + index).ToArray();
        var fragment = AdvancedAnalysisTableFragment.Parse(html, "[\"heading\",\"value\"]",
            JsonSerializer.Serialize(signals), map)!;
        AdvancedAnalysisResolvedEvidence Evidence(string id) => new(new()
        {
            EvidenceId = id, DocId = "11111111-1111-1111-1111-111111111111",
            RevisionId = "22222222-2222-2222-2222-222222222222", SourceHash = new string('a', 64),
            PageStart = 1, PageEnd = 1, ChunkId = id + "-chunk", AnchorId = id + "-anchor"
        }, html, TableFragment: fragment);

        var view = Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build(
            [Evidence("E1"), Evidence("E2")], null, 20_000));
        var json = JsonSerializer.SerializeToElement(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(64, view.QualitySignals!.Count);
        Assert.True(json.GetProperty("qualityMetadataTruncated").GetBoolean());
        Assert.Equal(6, json.GetProperty("qualitySignalsOmittedCount").GetInt32());
    }
}

internal static class StructuredTableWireQualityFixture
{
    internal static (string Html, string Map) CreateGrid()
    {
        const string html = """<table data-source-table-ordinal="0"><tr><th data-cell-role="column_header">Value</th></tr><tr><td>-5 °C</td></tr></table>""";
        var map = JsonSerializer.Serialize(new CanonicalTableCellMap(CanonicalTableCellMap.CurrentSchemaVersion, 0, 2, 1,
        [new("heading", 0, 0, 1, 1, "column_header", CanonicalTableCellMap.Hash("Value")),
            new("value", 1, 0, 1, 1, "data", CanonicalTableCellMap.Hash("-5 °C"))]));
        return (html, map);
    }
}
