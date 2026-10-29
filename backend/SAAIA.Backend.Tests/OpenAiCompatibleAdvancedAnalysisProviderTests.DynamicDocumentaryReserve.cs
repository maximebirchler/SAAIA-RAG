using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task EvidenceSelection_unread_catalogue_uses_dynamic_documentary_capacity_while_legacy_keeps_its_reserve(string protocol)
    {
        const int maximum = 14_000;
        var catalogue = LargeNativeBudgetCatalog();
        var sources = Enumerable.Range(1, 6).Select(index => BuildEvidence("E" + index,
            "Method " + index + ": " + WireTextFact + new string((char)('a' + index), 3_000),
            docId: "same-document", revisionId: "same-revision") with
        {
            SourceOverview = new(1, 177, 753) { TableRegionObservations = catalogue }
        }).ToArray();
        var collectionCosts = new List<int>();
        var visibleCounts = new List<int>();
        foreach (var nativeEnabled in new[] { false, true })
        {
            using var factory = new QueuedHttpClientFactory(
                Completion("""{"queries":[{"query":"method exact","topK":12}]}"""),
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = JsonContent.Create(new { error = new { message = "intentional_capture_stop" } }) });
            var options = WorkspaceOptions(protocol);
            options.MaximumEvidencePromptCharacters = maximum;
            options.NativeObservationReadEnabled = nativeEnabled;
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
                    BuildDirectRequest(), new RecordingToolGateway(sources), CancellationToken.None));
            Assert.Equal("advanced_llm_http_400", error.ErrorCode);
            var body = factory.Requests[1].Body;
            var current = WorkspaceUser(body);
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, maximum);
            Assert.Empty(current.GetProperty("structuredTables").EnumerateArray());
            Assert.Empty(current.GetProperty("alternativeTableReadings").GetProperty("views").EnumerateArray());
            Assert.False(current.TryGetProperty("alternativeNativeReadings", out _));
            var visible = current.GetProperty("evidence").EnumerateArray().ToArray();
            visibleCounts.Add(visible.Length);
            AssertWireProjection(current, sources.Length, visible.Length);
            foreach (var item in visible)
            {
                var original = sources.Single(source => source.Reference.EvidenceId == item.GetProperty("evidenceId").GetString());
                var text = item.GetProperty("content").GetString()!;
                Assert.Equal(original.Content[..text.Length], text);
                Assert.Equal(text.Length, item.GetProperty("nextContentOffset").GetInt32());
            }
            var json = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
                .GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var payload = JsonNode.Parse(body)!.AsObject();
            var message = payload[protocol == "responses" ? "input" : "messages"]!.AsArray().OfType<JsonObject>()
                .Single(item => item["role"]?.GetValue<string>() == "user");
            var user = JsonNode.Parse(message["content"]!.GetValue<string>())!.AsObject();
            foreach (var field in new[] { "structuredTables", "alternativeTableReadings", "visualSources",
                "visualSourceCapabilities", "alternativeNativeReadings", "nativeObservationCapabilities" }) user.Remove(field);
            message["content"] = user.ToJsonString(json);
            collectionCosts.Add(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(payload.ToJsonString(json)));
        }
        Assert.InRange(collectionCosts[0], 1, maximum / 2);
        Assert.InRange(collectionCosts[1], maximum / 2 + 1, maximum);
        Assert.True(visibleCounts[1] > visibleCounts[0]);
        Assert.All(sources, source => Assert.Same(catalogue, source.SourceOverview!.TableRegionObservations));
    }
}
