using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 8_000)]
    [InlineData("responses", 8_000)]
    [InlineData("chat-completions", 64_000)]
    [InlineData("responses", 64_000)]
    public async Task AdmittedCardNavigation_actual_http_span_equals_metadata_and_probe_with_compact_outer_encoding(
        string protocol, int cap)
    {
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = cap;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        const string title = "Exact <tag attr=\"quoted\"> & \\path\\ 漢字 😀 e\u0301\tMETHOD";
        var oversized = "OVERSIZED " + string.Concat(Enumerable.Repeat(title, 1_000));
        var active = new[] { NavigationCard("LONG", oversized, page: 3, end: 8),
            NavigationCard("EXACT", title, page: 17, end: 20), NavigationCard("SHORT", "SHORT METHOD", page: 29, end: 29) };
        var request = NavigationRequest();
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request, active,
            new Dictionary<string, HashSet<string>>(), active.Skip(1).Take(1).ToArray(), null, null, null)!;
        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(), "writer", "Return JSON.",
            NavigationPrompt(provider, request, projection), 128, CancellationToken.None, true, null, projection));
        var body = Assert.Single(factory.Requests).Body;
        var user = WorkspaceUser(body);
        var catalogue = user.GetProperty("sourceCatalogue");
        var nav = catalogue.GetProperty("admittedCardLocators");
        var locators = nav.GetProperty("locators").EnumerateArray().ToArray();
        Assert.Equal(3, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal(2, nav.GetProperty("listedLocatorCount").GetInt32());
        Assert.Equal(1, nav.GetProperty("omittedLocatorCount").GetInt32());
        Assert.DoesNotContain(locators, item => item.GetProperty("exactTitle").GetString() == oversized);
        var exact = Assert.Single(locators, item => item.GetProperty("exactTitle").GetString() == title);
        Assert.Equal(17, exact.GetProperty("physicalPageStart").GetInt32());
        Assert.Equal(20, exact.GetProperty("physicalPageEnd").GetInt32());
        Assert.Contains(locators, item => item.GetProperty("exactTitle").GetString() == "SHORT METHOD");

        var inner = NavigationLegacyInnerOptions();
        var compact = new JsonSerializerOptions(inner) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var control = JsonNode.Parse(catalogue.GetRawText())!.AsObject(); control.Remove("admittedCardLocators");
        // This escaped substring is present verbatim in the captured HTTP body.
        // Compare that real span with an independently serialized control, not
        // the product's size helper or a second copy of its returned number.
        var actualSpan = JsonSerializer.Serialize(catalogue.GetRawText(), compact)[1..^1];
        var controlSpan = JsonSerializer.Serialize(control.ToJsonString(inner), compact)[1..^1];
        Assert.Contains(actualSpan, body, StringComparison.Ordinal);
        var exactDelta = actualSpan.Length - controlSpan.Length;
        Assert.Equal(exactDelta, nav.GetProperty("wireCharacters").GetInt32());
        AssertNavigationWireBudget(user, Math.Min(cap / 4, 8_000));
        var legacyDelta = JsonSerializer.Serialize(catalogue.GetRawText(), inner).Length
            - JsonSerializer.Serialize(control.ToJsonString(inner), inner).Length;
        Assert.True(exactDelta < legacyDelta, "The HTTP metric must count compact outer escapes.");
        var measurement = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "llm.call");
        using var metrics = JsonDocument.Parse(measurement.Values["Measurements"]!.ToString()!);
        Assert.Equal(exactDelta, metrics.RootElement.GetProperty("admitted_card_locator_wire_chars").GetInt32());
        Assert.Equal(Math.Min(cap / 4, 8_000), metrics.RootElement.GetProperty("admitted_card_locator_wire_cap").GetInt32());
        Assert.DoesNotContain(title, metrics.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(64_000)]
    public void AdmittedCardNavigation_compact_metric_does_not_expand_legacy_locator_fitting(int cap)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = cap;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var request = NavigationRequest();
        var sample = new[] { NavigationCard("BOUNDARY", "BOUNDARY", page: 7, end: 11),
            NavigationCard("SHORT", "SHORT METHOD", page: 19, end: 19) };
        using var referencePrompt = JsonDocument.Parse(NavigationPrompt(provider, request, NavigationProjection(provider, request, sample)));
        var catalogue = referencePrompt.RootElement.GetProperty("sourceCatalogue");
        var candidate = JsonNode.Parse(catalogue.GetRawText())!.AsObject();
        var navigation = candidate["admittedCardLocators"]!.AsObject();
        var only = navigation["locators"]![0]!.DeepClone();
        navigation["locators"] = new JsonArray(only);
        navigation["listedLocatorCount"] = 1; navigation["omittedLocatorCount"] = 1;
        var control = JsonNode.Parse(catalogue.GetRawText())!.AsObject(); control.Remove("admittedCardLocators");
        var inner = NavigationLegacyInnerOptions();
        var compact = new JsonSerializerOptions(inner) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var limit = Math.Min(cap / 4, 8_000);
        (string Title, int Legacy, int Actual) MeasureTitle(int length)
        {
            var title = "BOUNDARY \"É😀\" " + new string('x', length);
            only["exactTitle"] = title;
            navigation["wireCharacters"] = 0;
            int legacy = 0;
            for (var fixedPoint = 0; fixedPoint < 8; fixedPoint++)
            {
                legacy = NavigationCatalogueDelta(candidate, control, inner, inner);
                if (navigation["wireCharacters"]!.GetValue<int>() == legacy) break;
                navigation["wireCharacters"] = legacy;
            }
            return (title, legacy, NavigationCatalogueDelta(candidate, control, inner, compact));
        }
        var lower = 1;
        var upper = limit;
        while (lower < upper)
        {
            var midpoint = lower + (upper - lower) / 2;
            if (MeasureTitle(midpoint).Legacy > limit) upper = midpoint;
            else lower = midpoint + 1;
        }
        var boundary = MeasureTitle(lower);
        Assert.True(boundary.Legacy > limit);
        Assert.InRange(boundary.Actual, 1, limit);
        // It would fit under compact accounting, but the old fitting decision
        // still omits it whole and allows the later short locator.
        var active = new[] { NavigationCard("BOUNDARY", boundary.Title, page: 7, end: 11), sample[1] };
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request, active,
            new Dictionary<string, HashSet<string>>(), active.Skip(1).Take(1).ToArray(), null, null, null)!;
        using var prompt = JsonDocument.Parse(NavigationPrompt(provider, request, projection));
        var nav = prompt.RootElement.GetProperty("sourceCatalogue").GetProperty("admittedCardLocators");
        Assert.Equal(2, nav.GetProperty("observedLocatorCount").GetInt32());
        Assert.Equal(1, nav.GetProperty("listedLocatorCount").GetInt32());
        Assert.Equal(1, nav.GetProperty("omittedLocatorCount").GetInt32());
        var retained = Assert.Single(nav.GetProperty("locators").EnumerateArray());
        Assert.Equal("SHORT METHOD", retained.GetProperty("exactTitle").GetString());
        Assert.Equal(19, retained.GetProperty("physicalPageStart").GetInt32());
        Assert.Equal(19, retained.GetProperty("physicalPageEnd").GetInt32());
        AssertNavigationWireBudget(prompt.RootElement, limit);
        Assert.Empty(factory.Requests);
    }

    private static JsonSerializerOptions NavigationLegacyInnerOptions()
        => new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static int NavigationCatalogueDelta(JsonObject full, JsonObject baseline,
        JsonSerializerOptions inner, JsonSerializerOptions outer)
        => JsonSerializer.Serialize(full.ToJsonString(inner), outer).Length
            - JsonSerializer.Serialize(baseline.ToJsonString(inner), outer).Length;
}
