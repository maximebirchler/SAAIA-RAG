using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_unavailable_inventory_reserves_tool_history_before_projecting_primary_excerpts(string protocol)
    {
        var fixture = new NativeAuditGateway();
        var reference = fixture.Evidence[0].Reference;
        var readings = new AdvancedAnalysisAlternativeNativeReadings(reference.DocId, reference.RevisionId!,
            reference.SourceHash, 1, new("unavailable", "no_captured_native_observation", null, 0, null, null), null);
        var catalogue = LargeNativeBudgetCatalog();
        var evidence = fixture.Evidence.Select(item => item with
        {
            Content = new string('x', 880),
            SourceOverview = new(1, 177, 753)
            {
                TableRegionObservations = catalogue, NativeObservations = readings,
                VisualAssets = new("pixels_available", 13, [1], false, "not_performed", false)
            },
            SourceQuality = new("ok", false, ["document_intelligence_docling", "canonical_source_links",
                "figure_semantics_not_extracted", "canonical_native_pdf_image_inventory"])
        }).ToArray();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider);
        var json = (JsonSerializerOptions)type.GetField("JsonOptions", flags)!.GetValue(null)!;
        var read = new AdvancedAnalysisSearchRequest("Read native inventory", Operation: "read_source",
            DocId: reference.DocId, RevisionId: reference.RevisionId, DocPath: reference.DocPath,
            PageStart: 1, PageEnd: 1, TopK: 1, NativeObservationView: "inventory",
            NativeObservationOffset: 0, NativeObservationLimit: 4);
        var observed = new AdvancedAnalysisSearchObservation(read.Query, [], [], 0, 2,
            ReadDiagnostic: new("native_observation_unavailable", 1, 177, 0), AlternativeNativeReadings: readings);
        var key = (string)type.GetMethod("BuildSearchIdentity", flags)!.Invoke(null, [read])!;
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":1,"nativeObservationView":"inventory","nativeObservationOffset":0,"nativeObservationLimit":4}""";
        var calls = JsonSerializer.Serialize(new[] { new { id = "inventory-call", type = "function",
            function = new { name = "read_source", arguments } } });
        var output = protocol == "responses" ? JsonSerializer.Serialize(new[] { new
            { type = "function_call", id = "fc-inventory", call_id = "inventory-call", name = "read_source", arguments } }) : null;
        var turn = Activator.CreateInstance(type.GetNestedType("NativeResearchTurn", flags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object?[] { calls, new[] { read }, new Dictionary<string, AdvancedAnalysisSearchObservation> { [key] = observed },
                null, output, false, false, false }, null)!;
        async Task<string> Capture(bool reserveHistory)
        {
            using var factory = new QueuedHttpClientFactory(protocol == "responses"
                ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
            var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
            options.VisualSourceCitationsEnabled = true; options.MaximumEvidencePromptCharacters = 14_000;
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
            var projection = reserveHistory
                ? type.GetMethod("BuildPromptEvidenceWithPersistentSourceKeys", flags)!.Invoke(provider,
                    new object?[] { BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null, null, null, turn })!
                : type.GetMethod("BuildPromptEvidence", flags)!.Invoke(provider,
                    new object?[] { BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null })!;
            var history = (IReadOnlyList<object>)type.GetMethod("BuildNativeToolTurnMessages", flags)!
                .Invoke(provider, new[] { turn, projection, protocol == "responses" })!;
            try
            {
                await (Task)type.GetMethod("CompleteJsonAsync", flags)!.Invoke(provider,
                    new object?[] { Guid.NewGuid(), "writer", "Return JSON.", JsonSerializer.Serialize(new { evidence = projection }, json),
                        16, CancellationToken.None, false, history, projection })!;
            }
            catch (AdvancedAnalysisProviderException) { Assert.Empty(factory.Requests); throw; }
            return Assert.Single(factory.Requests).Body;
        }
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => Capture(false));
        Assert.Equal("advanced_native_documentary_wire_budget_exceeded", error.ErrorCode);
        var body = await Capture(true);
        var current = WorkspaceUser(body);
        var visible = current.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.NotEmpty(visible);
        Assert.All(visible, item => Assert.Equal(new string('x', 880)[..item.GetProperty("content").GetString()!.Length],
            item.GetProperty("content").GetString()));
        var inventory = Assert.Single(current.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray())
            .GetProperty("inventory");
        Assert.Equal("unavailable", inventory.GetProperty("availability").GetString());
        Assert.Equal("no_captured_native_observation", inventory.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, inventory.GetProperty("observationCount").ValueKind);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }

    [Theory]
    [InlineData("chat-completions", "same")]
    [InlineData("responses", "same")]
    [InlineData("chat-completions", "quality")]
    [InlineData("responses", "quality")]
    [InlineData("chat-completions", "identity")]
    [InlineData("responses", "identity")]
    [InlineData("chat-completions", "document")]
    [InlineData("responses", "document")]
    [InlineData("chat-completions", "source")]
    [InlineData("responses", "source")]
    [InlineData("chat-completions", "page")]
    [InlineData("responses", "page")]
    [InlineData("chat-completions", "offset")]
    [InlineData("responses", "offset")]
    [InlineData("chat-completions", "historical")]
    [InlineData("responses", "historical")]
    public async Task NativeInventoryWireBudget_exact_current_inventory_is_one_surface_but_divergent_or_historical_metadata_stays_complete(
        string protocol, string mutation)
    {
        var fixture = new NativeAuditGateway(exposeInitialInventory: true);
        var evidence = fixture.Evidence.ToArray();
        var captured = evidence[0].SourceOverview!.NativeObservations!;
        captured = mutation switch
        {
            "quality" => captured with { Inventory = captured.Inventory! with
                { Observations = captured.Inventory.Observations!.Select(item => item with { QualitySignals = ["different_captured_warning"] }).ToArray() } },
            "identity" => captured with { RevisionId = "33333333-3333-4333-8333-333333333333" },
            "document" => captured with { DocId = "33333333-3333-4333-8333-333333333333" },
            "source" => captured with { SourceSha256 = new string('b', 64) },
            "page" => captured with { PhysicalPage = 2 },
            "offset" => captured with { Inventory = captured.Inventory! with { Offset = 1 } },
            _ => captured
        };
        if (mutation == "historical") evidence = evidence.Select(item => item with
            { SourceOverview = item.SourceOverview! with { NativeObservations = null } }).ToArray();
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 64_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var json = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetField("JsonOptions", flags)!.GetValue(null)!;
        var projection = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence", flags)!.Invoke(provider,
            new object?[] { BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null })!;
        var read = new AdvancedAnalysisSearchRequest("Read native inventory", Operation: "read_source",
            DocId: evidence[0].Reference.DocId, RevisionId: evidence[0].Reference.RevisionId, PageStart: 1, PageEnd: 1,
            NativeObservationView: "inventory", NativeObservationOffset: 0, NativeObservationLimit: 4);
        var observation = new AdvancedAnalysisSearchObservation(read.Query, evidence.Take(1).ToArray(), [], 0, 1,
            AlternativeNativeReadings: captured);
        var key = (string)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildSearchIdentity", flags)!.Invoke(null, new object[] { read })!;
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"nativeObservationView":"inventory","nativeObservationOffset":0,"nativeObservationLimit":4}""";
        var calls = JsonSerializer.Serialize(new[] { new { id = "inventory-call", type = "function",
            function = new { name = "read_source", arguments } } });
        var responseOutput = protocol == "responses" ? JsonSerializer.Serialize(new[] { new
            { type = "function_call", id = "fc-inventory", call_id = "inventory-call", name = "read_source", arguments } }) : null;
        var turn = Activator.CreateInstance(typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", flags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object?[] { calls, new[] { read }, new Dictionary<string, AdvancedAnalysisSearchObservation> { [key] = observation },
                null, responseOutput, false, false, false }, null)!;
        var history = (IReadOnlyList<object>)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildNativeToolTurnMessages", flags)!
            .Invoke(provider, new[] { turn, projection, protocol == "responses" })!;
        await (Task)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("CompleteJsonAsync", flags)!.Invoke(provider,
            new object?[] { Guid.NewGuid(), "writer", "Return JSON.", JsonSerializer.Serialize(new { evidence = projection }, json),
                16, CancellationToken.None, false, history, projection })!;
        var body = Assert.Single(factory.Requests).Body;
        var current = WorkspaceUser(body);
        using var output = NativeBudgetToolOutput(body, protocol);
        var toolInventory = output.RootElement.GetProperty("nativeInventory");
        if (mutation == "same")
        {
            var declared = Assert.Single(current.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray());
            Assert.Equal(1, CountLiteralInJsonStrings(body, "native-audit"));
            Assert.Equal(declared.GetProperty("sourceKey").GetString(), toolInventory.GetProperty("sourceKey").GetString());
            Assert.Equal(1, toolInventory.GetProperty("physicalPage").GetInt32());
            Assert.Equal(0, toolInventory.GetProperty("offset").GetInt32());
            Assert.Equal(captured.DocId, toolInventory.GetProperty("docId").GetString());
            Assert.Equal(captured.RevisionId, toolInventory.GetProperty("revisionId").GetString());
            Assert.Equal(captured.SourceSha256, toolInventory.GetProperty("sourceSha256").GetString());
            Assert.Equal("current_user.alternativeNativeReadings.inventories[exact_source_page_offset_returned_count].inventory",
                toolInventory.GetProperty("inventoryReference").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(captured.Inventory, json).GetRawText(), declared.GetProperty("inventory").GetRawText());
            Assert.False(toolInventory.TryGetProperty("observations", out _));
            var alternative = output.RootElement.GetProperty("alternativeNativeReadings").GetProperty("inventories")[0];
            Assert.True(alternative.TryGetProperty("inventoryReference", out _));
            Assert.False(alternative.TryGetProperty("inventory", out _));
            Assert.Equal(toolInventory.GetRawText(), alternative.GetRawText());
        }
        else
        {
            Assert.False(toolInventory.TryGetProperty("inventoryReference", out _));
            Assert.Equal(JsonSerializer.SerializeToElement(captured.Inventory, json).GetRawText(), toolInventory.GetRawText());
        }
    }
}
