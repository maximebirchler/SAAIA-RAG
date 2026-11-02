using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
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
    public async Task Native_history_wire_probes_match_actual_http_and_keep_legacy_rejection(
        string protocol, bool checkBoundary)
    {
        const string special = "<tag attr=\"quoted\"> & \\path\\ 漢字 😀 e\u0301";
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 65_536;
        options.NativeResearchMaximumHistoryCharacters = 65_536;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        var evidence = BuildEvidence("E1", "Whole canonical body " + special,
            new string('p', 3_000) + special + ".pdf");
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
            new[] { evidence }, new Dictionary<string, HashSet<string>>(), null, null, null, null)!;
        var reads = Enumerable.Range(0, 8).Select(index => new AdvancedAnalysisSearchRequest(
            "Literal " + index + " " + special, TopK: 3, DocId: evidence.Reference.DocId,
            RevisionId: evidence.Reference.RevisionId, DocPath: evidence.Reference.DocPath,
            Operation: "find_source_text", Offset: 0)).ToArray();
        var observations = reads.ToDictionary(read => (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", read)!,
            read => new AdvancedAnalysisSearchObservation(read.Query, [evidence], [], 0, 1));
        var arguments = reads.Select(read => JsonSerializer.Serialize(new
            { sourceKey = "internal-source-1", query = read.Query, offset = 0, topK = 3 })).ToArray();
        var calls = arguments.Select((argument, index) => new
        {
            id = "wire-history-" + index, type = "function",
            function = new { name = "find_source_text", arguments = argument }
        }).ToArray();
        var callsJson = JsonSerializer.Serialize(calls);
        var responseItems = protocol == "responses" ? JsonSerializer.Serialize(calls.Select(call => new
        {
            type = "function_call", id = "fc-" + call.id, call_id = call.id,
            name = call.function.name, arguments = call.function.arguments, status = "completed"
        })) : null;
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var turnType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", flags)!;
        var turn = Activator.CreateInstance(turnType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object?[] { callsJson, reads, observations, null, responseItems, false, false, false }, null)!;
        BindAdmittedNativeFixtureCalls(turn, calls.Select((call, index) => (call.id, reads[index])).ToArray());
        var history = (IReadOnlyList<object>)InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages",
            turn, projection, protocol == "responses")!;
        var inner = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        var prompt = JsonSerializer.Serialize(new { evidence = projection }, inner);
        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(), "writer",
            "Return JSON.", prompt, 128, CancellationToken.None, false, history, projection));
        var captured = Assert.Single(factory.Requests).Body;
        using var document = JsonDocument.Parse(captured);
        var messages = document.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray().ToArray();
        Assert.Equal(history.Count + 2, messages.Length);
        var rawHistory = messages.Skip(1).SkipLast(1).ToArray();
        var compact = new JsonSerializerOptions(inner) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var actualArray = JsonSerializer.Serialize(rawHistory, compact);
        var legacyArray = JsonSerializer.Serialize(rawHistory, inner);
        Assert.Contains(actualArray[1..^1], captured, StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(history, compact), actualArray);
        Assert.Equal(JsonSerializer.Serialize(history, inner), legacyArray);
        Assert.True(actualArray.Length < legacyArray.Length);
        var outputs = NativeCorrelationOutputs(captured, protocol);
        Assert.Equal(8, outputs.Length);
        Assert.All(outputs, output =>
        {
            Assert.Equal("executed", output.Body.GetProperty("status").GetString());
            Assert.Equal(evidence.Reference.DocPath, output.Body.GetProperty("resolvedSource").GetProperty("docPath").GetString());
            Assert.Equal("E1", Assert.Single(output.Body.GetProperty("visibleEvidenceIds").EnumerateArray()).GetString());
        });
        var nativeEvent = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "advanced.native_tool_results");
        var mainEvent = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "llm.call");
        foreach (var entry in new[] { nativeEvent, mainEvent })
        {
            using var metric = JsonDocument.Parse(entry.Values["Measurements"]!.ToString()!);
            Assert.Equal(actualArray.Length, metric.RootElement.GetProperty("history_characters").GetInt32());
            Assert.Equal(legacyArray.Length, metric.RootElement.GetProperty("history_fitting_characters").GetInt32());
            Assert.Equal(0, metric.RootElement.GetProperty("diagnostic_dropped_measurements").GetInt32());
            Assert.Equal(0, metric.RootElement.GetProperty("diagnostic_truncated_values").GetInt32());
            Assert.DoesNotContain(special, metric.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal("success", nativeEvent.Values["Outcome"]);
        var visible = Assert.Single(WorkspaceUser(captured).GetProperty("evidence").EnumerateArray());
        Assert.Equal(evidence.Content, visible.GetProperty("content").GetString());
        if (!checkBoundary) return;

        var limit = Math.Max(16_384, actualArray.Length + (legacyArray.Length - actualArray.Length) / 2);
        Assert.InRange(limit, 16_384, 65_536);
        Assert.True(actualArray.Length <= limit && legacyArray.Length > limit);
        options.NativeResearchMaximumHistoryCharacters = limit;
        // Reuse this exact admitted turn/projection. Cheaper compact transport
        // must still refuse the whole legacy-sized history before another HTTP.
        var before = JsonSerializer.Serialize(projection, inner);
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(provider,
            "BuildNativeToolTurnMessages", turn, projection, protocol == "responses"));
        Assert.Equal("advanced_native_tool_history_limit_exceeded", error.ErrorCode);
        Assert.Equal(legacyArray.Length, error.Data["historyCharacters"]);
        Assert.Equal(limit, error.Data["maximumHistoryCharacters"]);
        Assert.Single(factory.Requests);
        Assert.Equal(callsJson, turn.GetType().GetProperty("CallsJson")!.GetValue(turn));
        Assert.Equal(before, JsonSerializer.Serialize(projection, inner));
        var rejected = logger.Events.Last(entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "advanced.native_tool_results");
        Assert.Equal("incomplete", rejected.Values["Outcome"]);
        Assert.Equal("native_history_budget_exceeded", rejected.Values["ReasonCode"]);
        using var rejectedMetrics = JsonDocument.Parse(rejected.Values["Measurements"]!.ToString()!);
        Assert.Equal(actualArray.Length, rejectedMetrics.RootElement.GetProperty("history_characters").GetInt32());
        Assert.Equal(legacyArray.Length, rejectedMetrics.RootElement.GetProperty("history_fitting_characters").GetInt32());
        Assert.Equal(limit, rejectedMetrics.RootElement.GetProperty("history_maximum_characters").GetInt32());
    }
}
