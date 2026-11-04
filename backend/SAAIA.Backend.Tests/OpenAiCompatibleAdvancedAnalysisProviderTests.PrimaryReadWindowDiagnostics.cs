using System.Collections;
using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(4_000, false)]
    [InlineData(1_200, false)]
    [InlineData(0, true)]
    public void Primary_read_probe_counts_main_and_supporting_window_text_without_changing_the_tool_wire(int offset, bool nullContent)
    {
        var body = PrimaryReadBody("READ", nullContent ? null! : new string('a', 6_000));
        var request = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, [body], body.Reference);
        turn.GetType().GetProperty("CallsJson")!.SetValue(turn,
            """[{"id":"read","type":"function","function":{"name":"read_source","arguments":"{}"}}]""");
        object? windows = null;
        if (!nullContent)
        {
            var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
            var map = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
            map.Add("READ", Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [offset, 200, null], null)!);
            windows = map;
        }
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 30_000;
        options.NativeObservationReadEnabled = true;
        var logger = new NativeCorrelationLogger();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            new[] { body }, new Dictionary<string, HashSet<string>>(), new[] { body }, new Dictionary<string, string>(), windows, turn)!;
        var item = Assert.Single(((IEnumerable)projection).Cast<object>());
        var main = ((string)item.GetType().GetProperty("Content")!.GetValue(item)!).Length;
        var support = ((IEnumerable)item.GetType().GetProperty("SupportingContentWindows")!.GetValue(item)!).Cast<object>()
            .Sum(window => ((string)window.GetType().GetProperty("Content")!.GetValue(window)!).Length);
        Assert.Equal(nullContent ? 0 : 200, main);
        Assert.Equal(nullContent ? 0 : 2_400, support);
        var catalogue = projection.GetType().GetProperty("SourceCatalogue")!.GetValue(projection);
        var unobserved = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessagesCore", turn, projection, false, false, catalogue);
        Assert.Empty(logger.Entries);
        var observed = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, false);
        Assert.Equal(JsonSerializer.Serialize(unobserved), JsonSerializer.Serialize(observed));
        var probe = Assert.Single(logger.Entries, entry => entry.Contains("operation=advanced.native_tool_results ", StringComparison.Ordinal));
        Assert.Contains("\"primary_read_visible_content_characters\":" + (main + support), probe);
        Assert.Contains("\"primary_read_main_content_characters\":" + main, probe);
        Assert.Contains("\"primary_read_supporting_content_characters\":" + support, probe);
        // At offset1200 the main window lies inside the prefix: 2600 displayed
        // characters cover only2400 unique canonical characters. The probe
        // deliberately counts serialized text, not a union of source ranges.
        if (offset == 1_200) Assert.Equal(2_600, main + support);
        Assert.Empty(factory.Requests);
    }
}
