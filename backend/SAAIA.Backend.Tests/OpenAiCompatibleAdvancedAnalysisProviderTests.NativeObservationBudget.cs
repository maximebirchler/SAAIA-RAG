using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 1)]
    [InlineData("responses", 1)]
    public async Task Native_documentary_budget_counts_source_catalog_once_and_keeps_short_lines_visible(
        string protocol, int nativeLineCount)
    {
        var fixture = new NativeAuditGateway();
        var codes = Enumerable.Range(1, 10).Select(index => "observed_source_warning_" + index).ToArray();
        var catalog = LargeNativeBudgetCatalog();
        var evidence = fixture.NativeEvidence.Take(nativeLineCount).Select(item => item with
        {
            SourceOverview = item.SourceOverview! with { TableRegionObservations = catalog },
            NativeObservation = item.NativeObservation! with { QualitySignals = codes },
            SourceQuality = new(null, null, codes)
        }).ToArray();
        var body = await CaptureNativeBudgetAsync(protocol, evidence, fixture.RawSha);
        var current = WorkspaceUser(body);
        var native = current.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Equal(nativeLineCount, native.Length);
        foreach (var item in native)
        {
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
            var ordinal = item.GetProperty("nativeObservation").GetProperty("reference").GetProperty("lineOrdinal").GetInt32();
            Assert.Equal(fixture.NativeLines[ordinal], item.GetProperty("content").GetString());
            Assert.Equal(1, CountLiteralInJsonStrings(body, fixture.NativeLines[ordinal]));
            Assert.Equal(codes, item.GetProperty("sourceQuality").GetProperty("signals").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("not_verified", item.GetProperty("sourceQuality").GetProperty("semanticAccuracy").GetString());
        }
        var inventory = Assert.Single(current.GetProperty("alternativeTableReadings").GetProperty("inventories").EnumerateArray());
        Assert.Equal(8, inventory.GetProperty("observationCount").GetInt32());
        Assert.Equal(45, inventory.GetProperty("zeroOrUnavailablePagesNotListed").GetInt32());
        Assert.Equal(8, inventory.GetProperty("observations").GetArrayLength());
        foreach (var descriptor in catalog.Observations!)
            Assert.Equal(1, CountLiteralInJsonStrings(body, descriptor.ObservationId));
        using var output = NativeBudgetToolOutput(body, protocol);
        var tableReference = output.RootElement.GetProperty("alternativeTableReadings").GetProperty("inventories")[0];
        Assert.Equal(inventory.GetProperty("sourceKey").GetString(), tableReference.GetProperty("sourceKey").GetString());
        Assert.True(tableReference.TryGetProperty("inventoryReference", out _));
        var nativeReference = output.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        var currentView = current.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        foreach (var key in new[] { "sourceKey", "physicalPage", "blockId", "rawTextSha256", "offset", "limit" })
            Assert.Equal(currentView.GetProperty(key).GetRawText(), nativeReference.GetProperty(key).GetRawText());
        Assert.True(nativeReference.TryGetProperty("metadataReference", out _));
        Assert.Equal(native.Select(item => item.GetProperty("evidenceId").GetString()),
            nativeReference.GetProperty("lines").EnumerateArray().Select(item => item.GetProperty("evidenceId").GetString()));
        Assert.Equal(codes, currentView.GetProperty("qualitySignals").EnumerateArray().Select(value => value.GetString()));
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }


    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_documentary_budget_refuses_two_whole_lines_before_http_then_accepts_one_at_same_offset(string protocol)
    {
        var fixture = new NativeAuditGateway();
        var evidence = fixture.NativeEvidence.Select(item => item with
        {
            SourceOverview = item.SourceOverview! with { TableRegionObservations = LargeNativeBudgetCatalog() },
            NativeObservation = item.NativeObservation! with
            {
                QualitySignals = Enumerable.Range(1, 10).Select(index => "observed_source_warning_" + index).ToArray()
            },
            SourceQuality = new(null, null,
                Enumerable.Range(1, 10).Select(index => "observed_source_warning_" + index).ToArray())
        }).ToArray();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            CaptureNativeBudgetAsync(protocol, evidence, fixture.RawSha));
        Assert.Equal("advanced_native_documentary_wire_budget_exceeded", error.ErrorCode);
        Assert.Equal(0, evidence[0].NativeObservation!.LineOffset);
        var body = await CaptureNativeBudgetAsync(protocol, evidence.Take(1).ToArray(), fixture.RawSha);
        var current = WorkspaceUser(body);
        var view = Assert.Single(current.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray());
        Assert.Equal(0, view.GetProperty("offset").GetInt32());
        Assert.Equal(1, view.GetProperty("nextOffset").GetInt32());
        Assert.Equal(fixture.NativeLines[0], Assert.Single(current.GetProperty("evidence").EnumerateArray()).GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    public async Task Native_documentary_budget_does_not_relabel_a_divergent_catalogue(string protocol, bool wrongSourceSha)
    {
        var fixture = new NativeAuditGateway();
        var first = LargeNativeBudgetCatalog();
        var divergent = wrongSourceSha ? first with { SourceSha256 = new string('d', 64) }
            : first with { Observations = first.Observations!.Select(item => item with { LineCount = item.LineCount + 1 }).ToArray() };
        var evidence = fixture.NativeEvidence.Select((item, index) => item with
        {
            SourceOverview = item.SourceOverview! with { TableRegionObservations = index == 0 ? first : divergent }
        }).ToArray();
        var body = await CaptureNativeBudgetAsync(protocol, evidence, fixture.RawSha, 64_000, [evidence[1]]);
        using var output = NativeBudgetToolOutput(body, protocol);
        var inventory = Assert.Single(output.RootElement.GetProperty("alternativeTableReadings").GetProperty("inventories").EnumerateArray());
        Assert.False(inventory.TryGetProperty("inventoryReference", out _));
        Assert.Equal(divergent.Observations![0].LineCount, inventory.GetProperty("observations")[0].GetProperty("lineCount").GetInt32());
        // A subset with a different complete window must also retain its own
        // metadata instead of referencing the larger current-user window.
        var view = output.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        Assert.False(view.TryGetProperty("metadataReference", out _));
        Assert.True(view.TryGetProperty("qualitySignals", out _));
    }

    private static JsonDocument NativeBudgetToolOutput(string body, string protocol)
    {
        using var wire = JsonDocument.Parse(body);
        var tool = wire.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Single(item => protocol == "responses"
                ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        return JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
    }

    private static async Task<string> CaptureNativeBudgetAsync(string protocol,
        AdvancedAnalysisResolvedEvidence[] evidence, string rawSha, int maximumCharacters = 14_000,
        AdvancedAnalysisResolvedEvidence[]? returnedEvidence = null)
    {
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = maximumCharacters;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var json = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetField("JsonOptions", flags)!.GetValue(null)!;
        var projection = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence", flags)!.Invoke(provider,
            new object?[] { BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null })!;
        var user = JsonSerializer.Serialize(new { evidence = projection, researchTools = new { tools = Array.Empty<object>() } }, json);
        var read = new AdvancedAnalysisSearchRequest("Read retained native literal audit", DocId: evidence[0].Reference.DocId,
            RevisionId: evidence[0].Reference.RevisionId, PageStart: 1, PageEnd: 1, Operation: "read_source",
            NativeObservationView: "raw", NativeObservationBlockId: "native-audit", NativeObservationRawSha256: rawSha,
            NativeObservationOffset: 0, NativeObservationLimit: evidence.Length);
        var observation = new AdvancedAnalysisSearchObservation(read.Query, returnedEvidence ?? evidence, [], 0, 1);
        var key = (string)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildSearchIdentity", flags)!
            .Invoke(null, new object[] { read })!;
        var arguments = JsonSerializer.Serialize(new { sourceKey = "internal-source-1", nativeObservationView = "raw",
            nativeObservationBlockId = "native-audit", nativeObservationRawSha256 = rawSha,
            nativeObservationOffset = 0, nativeObservationLimit = evidence.Length, pageStart = 1, pageEnd = 1 });
        var calls = JsonSerializer.Serialize(new[] { new { id = "catalog-read", type = "function",
            function = new { name = "read_source", arguments } } });
        var outputs = protocol == "responses" ? JsonSerializer.Serialize(new[] { new { type = "function_call",
            id = "fc-catalog-read", call_id = "catalog-read", name = "read_source", arguments } }) : null;
        var turnType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", flags)!;
        var turn = Activator.CreateInstance(turnType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object?[] { calls, new[] { read }, new Dictionary<string, AdvancedAnalysisSearchObservation> { [key] = observation },
                null, outputs, false, false, false }, null)!;
        var history = (IReadOnlyList<object>)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildNativeToolTurnMessages", flags)!
            .Invoke(provider, new[] { turn, projection, protocol == "responses" })!;
        try
        {
            await (Task)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("CompleteJsonAsync", flags)!.Invoke(provider,
                new object?[] { Guid.NewGuid(), "writer", "Return JSON.", user, 16, CancellationToken.None, false, history })!;
        }
        catch (AdvancedAnalysisProviderException)
        {
            Assert.Empty(factory.Requests);
            throw;
        }

        return Assert.Single(factory.Requests).Body;
    }

    private static CanonicalTableObservationInventory LargeNativeBudgetCatalog()
        => new(new string('a', 64), "partial", 8, null,
            Enumerable.Range(1, 53).Select(page => new CanonicalTableObservationPageInventory(page,
                page <= 8 ? "observed" : "unavailable", page <= 8 ? 1 : null, page <= 8 ? 1 : null)).ToArray(),
            Enumerable.Range(1, 8).Select(index => new CanonicalTableObservationDescriptor(
                "table_observation_" + index + "_" + new string('a', 64), index,
                "table_" + index + "_" + new string('b', 32), "#/tables/" + index,
                120, new string('c', 64), "available", 1, 0)).ToArray());
}
