using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", "find_source_text", true)]
    [InlineData("responses", "search_corpus", true)]
    [InlineData("chat-completions", "find_source_text", false)]
    [InlineData("responses", "search_corpus", false)]
    public async Task Primary_tool_inventory_references_only_the_equivalent_current_source_catalogue(
        string protocol, string operation, bool matchingSourceHash)
    {
        var catalogue = LargeNativeBudgetCatalog();
        if (!matchingSourceHash) catalogue = catalogue with { SourceSha256 = new string('d', 64) };
        var original = SecondaryLineFixture("Le seuil est 17 bar.");
        var primary = original with
        {
            SourceObservation = null,
            SourceOverview = original.SourceOverview! with { TableRegionObservations = catalogue }
        };
        var arguments = operation == "find_source_text"
            ? """{"sourceKey":"internal-source-1","query":"seuil","offset":0,"topK":1}"""
            : """{"sourceKey":"","query":"seuil","category":"","documentHint":"","topK":1}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":1}]}"""),
            protocol == "responses" ? NativeResponseFunction(operation, arguments) : NativeCompletion((operation, arguments)),
            Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(primary);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(operation, gateway.Searches[1].Operation);
        Assert.Equal(2, gateway.Searches.Count);

        var body = factory.Requests[2].Body;
        var current = WorkspaceUser(body).GetProperty("alternativeTableReadings");
        var shared = Assert.Single(current.GetProperty("inventories").EnumerateArray());
        Assert.Equal(8, shared.GetProperty("observationDescriptorsOmittedCount").GetInt32());
        Assert.Equal(0, shared.GetProperty("nextObservationDescriptorOffset").GetInt32());
        using var output = InitialTableToolOutput(body, protocol);
        Assert.Equal("executed", output.RootElement.GetProperty("status").GetString());
        Assert.Equal("E1", Assert.Single(output.RootElement.GetProperty("visibleEvidenceIds").EnumerateArray()).GetString());
        var alternative = output.RootElement.GetProperty("alternativeTableReadings");
        var inventory = Assert.Single(alternative.GetProperty("inventories").EnumerateArray());
        Assert.Empty(alternative.GetProperty("views").EnumerateArray());
        if (!matchingSourceHash)
        {
            Assert.False(inventory.TryGetProperty("inventoryReference", out _));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(inventory.GetRawText()), JsonNode.Parse(shared.GetRawText())));
            return;
        }

        Assert.Equal(shared.GetProperty("sourceKey").GetString(), inventory.GetProperty("sourceKey").GetString());
        Assert.Equal("current_user.alternativeTableReadings.inventories[sourceKey]",
            inventory.GetProperty("inventoryReference").GetString());

        // Reconstruct the former wire without changing any other field or
        // correlated tool item. The referenced descriptor preserves its cursor
        // and omission data while reducing the charged documentary payload.
        var previousWire = JsonNode.Parse(body)!.AsObject();
        var messages = previousWire[protocol == "responses" ? "input" : "messages"]!.AsArray();
        var tool = messages.Single(item => protocol == "responses"
            ? item?["type"]?.GetValue<string>() == "function_call_output"
            : item?["role"]?.GetValue<string>() == "tool")!;
        var outputField = protocol == "responses" ? "output" : "content";
        var previousOutput = JsonNode.Parse(tool[outputField]!.GetValue<string>())!.AsObject();
        var previousAlternative = previousOutput["alternativeTableReadings"]!.AsObject();
        previousAlternative["inventories"]!.AsArray()[0] = JsonNode.Parse(shared.GetRawText());
        previousAlternative["instruction"] = current.GetProperty("instruction").GetString();
        previousAlternative["imageInspectionAvailable"] = current.GetProperty("imageInspectionAvailable").GetBoolean();
        tool[outputField] = previousOutput.ToJsonString();
        Assert.True(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(previousWire.ToJsonString())
            > OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body));
    }
}
