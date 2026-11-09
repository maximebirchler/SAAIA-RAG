using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("retired")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("ambiguous")]
    public void Observed_title_consumption_revalidates_source_without_relying_on_prior_pruning(string change)
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberRetainedLocatorPage(provider, context, body.Reference, 80);
        var projection = SourceCatalogueProjection(provider, [body]);
        var handles = SourceCatalogueHandles(projection);
        if (change == "revision") typeof(SAAIA.Contracts.AdvancedAnalysisResultEvidence).GetProperty("RevisionId")!.SetValue(body.Reference, "changed");
        if (change == "hash") typeof(SAAIA.Contracts.AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(body.Reference, new string('f', 64));
        if (change == "path") typeof(SAAIA.Contracts.AdvancedAnalysisResultEvidence).GetProperty("DocPath")!.SetValue(body.Reference, "changed.pdf");
        var sources = Array.CreateInstance(handles[0].GetType(), change == "retired" ? 0 : change == "ambiguous" ? 2 : 1);
        for (var i = 0; i < sources.Length; i++) sources.SetValue(handles[0], i);
        var retained = context.GetType().GetProperty("RetainedTitleLocators")!.GetValue(context)!;
        Assert.Same(sources, InvokeSourceCatalogue(null, "BuildObservedTitleCatalogue", sources, retained, 98_304));
    }

    [Fact]
    public void Observed_titles_are_whole_bounded_navigation_and_charge_the_identical_sent_projection()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        for (var page = 1; page <= 80; page++) RememberRetainedLocatorPage(provider, context, body.Reference, page);
        var retained = context.GetType().GetProperty("RetainedTitleLocators")!.GetValue(context)!;
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithTitleRegistry", BuildDirectRequest(), new[] { body },
            new Dictionary<string, HashSet<string>>(), new[] { body }, context.GetType().GetProperty("PromptSourceKeys")!.GetValue(context),
            null, null, context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context),
            context.GetType().GetProperty("SourceTitleCursors")!.GetValue(context), retained)!;
        var catalogue = projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection)!;
        var budget = (string)InvokeSourceCatalogue(provider, "BuildDocumentaryProjectionBudgetPrompt", projection, catalogue, null, null, null, null)!;
        var actual = (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", SourceCataloguePrompt(provider, projection), projection)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(budget)!["sourceCatalogue"], JsonNode.Parse(actual)!["sourceCatalogue"]));
        using var document = JsonDocument.Parse(actual);
        var titles = document.RootElement.GetProperty("sourceCatalogue").GetProperty("observedTitles");
        Assert.Equal(64, titles.GetProperty("retainedLocatorCount").GetInt32());
        var count = titles.GetProperty("listedLocatorCount").GetInt32();
        Assert.InRange(count, 1, 63);
        Assert.Equal(64 - count, titles.GetProperty("omittedLocatorCount").GetInt32());
        Assert.All(titles.GetProperty("items").EnumerateArray(), row =>
        {
            Assert.Equal("Documented checkpoint", row.GetProperty("title").GetString());
            Assert.InRange(row.GetProperty("physicalPageStart").GetInt32(), 17, 80);
        });
        Assert.DoesNotContain(body.Content, titles.GetRawText(), StringComparison.Ordinal);
        Assert.Same(retained, context.GetType().GetProperty("RetainedTitleLocators")!.GetValue(context));
        var without = JsonNode.Parse(budget)!.AsObject(); without["sourceCatalogue"]!.AsObject().Remove("observedTitles");
        int Count(string prompt) => OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(JsonSerializer.Serialize(new
        { messages = new[] { new { role = "system", content = "" }, new { role = "user", content = prompt } } }));
        Assert.True(Count(budget) > Count(without.ToJsonString()));
        Assert.InRange(Count(budget), 1, options.MaximumEvidencePromptCharacters);
        var plain = SourceCatalogueProjection(provider, [body]);
        var sources = plain.GetType().GetProperty("SourceCatalogue")!.GetValue(plain)!;
        Assert.Same(sources, InvokeSourceCatalogue(null, "BuildObservedTitleCatalogue", sources, retained, 1));
    }
}
