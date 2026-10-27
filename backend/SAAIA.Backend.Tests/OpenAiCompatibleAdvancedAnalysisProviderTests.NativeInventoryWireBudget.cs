using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
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
            Assert.Equal("current_user.alternativeNativeReadings.inventories[exact_source_page_offset].inventory",
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
