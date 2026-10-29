using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Primary_catalog_budget_charges_the_shared_inventory_once_before_native_discovery(
        string protocol, bool nativeEnabled)
    {
        // A long PDF has an entry for every unavailable/empty physical page in
        // its stored catalogue. The final shared inventory already reports
        // these as a count; none belongs in every primary excerpt's wire cost.
        var catalogue = LargeNativeBudgetCatalog() with
        {
            Pages = Enumerable.Range(1, 177).Select(page => new CanonicalTableObservationPageInventory(
                page, page <= 8 ? "observed" : "unavailable", page <= 8 ? 1 : null,
                page <= 8 ? 1 : null)).ToArray()
        };
        var sources = Enumerable.Range(1, 5).Select(index => BuildEvidence($"E{index}",
            WireTextFact + new string('x', 3_000), docId: "same-document", revisionId: "same-revision") with
        {
            SourceOverview = new(1, 177, 753) { TableRegionObservations = catalogue }
        }).ToArray();
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":1}]}"""),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 14_000;
        options.NativeObservationReadEnabled = nativeEnabled;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(sources), CancellationToken.None);

        var body = factory.Requests[1].Body;
        var current = WorkspaceUser(body);
        var evidence = current.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.InRange(evidence.Length, 1, 4);
        AssertWireProjection(current, 5, evidence.Length);
        foreach (var item in evidence)
        {
            var original = sources.Single(source => source.Reference.EvidenceId == item.GetProperty("evidenceId").GetString());
            var content = item.GetProperty("content").GetString()!;
            Assert.NotEmpty(content);
            Assert.Equal(original.Content[..content.Length], content);
            Assert.Equal(original.Reference.PageStart, item.GetProperty("physicalPageStart").GetInt32());
            Assert.Equal(original.Reference.PageEnd, item.GetProperty("physicalPageEnd").GetInt32());
            Assert.True(item.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(content.Length, item.GetProperty("nextContentOffset").GetInt32());
            Assert.False(item.GetProperty("sourceOverview").TryGetProperty("tableRegionObservations", out _));
        }
        var inventory = Assert.Single(current.GetProperty("alternativeTableReadings").GetProperty("inventories").EnumerateArray());
        Assert.Equal(8, inventory.GetProperty("observationCount").GetInt32());
        Assert.Equal(8, inventory.GetProperty("pages").GetArrayLength());
        Assert.Equal(169, inventory.GetProperty("zeroOrUnavailablePagesNotListed").GetInt32());
        Assert.False(inventory.TryGetProperty("observations", out _));
        Assert.Equal(8, inventory.GetProperty("observationDescriptorsOmittedCount").GetInt32());
        Assert.Equal(0, inventory.GetProperty("nextObservationDescriptorOffset").GetInt32());
        Assert.Contains("read_source tableObservationView=inventory", inventory.GetProperty("observationDescriptorRecovery").GetString());
        foreach (var descriptor in catalogue.Observations!)
            Assert.Equal(0, CountLiteralInJsonStrings(body, descriptor.ObservationId));
        Assert.Equal(177, catalogue.Pages!.Count);
        Assert.All(sources, source => Assert.Same(catalogue, source.SourceOverview!.TableRegionObservations));

        using var wire = JsonDocument.Parse(body);
        var tools = wire.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => protocol == "responses" ? tool : tool.GetProperty("function")).ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "read_evidence");
        var readSource = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "read_source");
        var properties = readSource.GetProperty("parameters").GetProperty("properties");
        Assert.Equal(nativeEnabled, properties.TryGetProperty("nativeObservationView", out _));
        Assert.Equal("internal-source-1", Assert.Single(properties.GetProperty("sourceKey").GetProperty("enum").EnumerateArray()).GetString());
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }
}
