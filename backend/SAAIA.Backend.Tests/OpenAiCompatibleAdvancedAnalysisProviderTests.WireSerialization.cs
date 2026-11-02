using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Wire_serialization_preserves_decoded_prompts_and_evidence_and_reserves_the_smaller_actual_utf8(
        string protocol,
        bool nativeTurn)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-wire-serialization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string special = "<tag attr=\"quoted\"> & 'apostrophe' \\path\\ 漢字 😀 e\u0301";
            var original = BuildDirectRequest();
            var handoff = original.Handoff;
            var request = original with
            {
                Handoff = new AdvancedAnalysisHandoffEnvelope
                {
                    HandoffId = handoff.HandoffId, CreatedAtUtc = handoff.CreatedAtUtc,
                    RequestText = handoff.RequestText + " " + special,
                    Language = handoff.Language, OriginIntent = handoff.OriginIntent,
                    ReasonCode = handoff.ReasonCode, TransferStage = handoff.TransferStage,
                    Load = handoff.Load, ResearchState = handoff.ResearchState
                }
            };
            var content = "Le seuil est 17 bar. " + special;
            var options = BudgetDiagnosticOptions(Path.Combine(directory, "usage.jsonl"));
            options.NativeResearchToolsEnabled = protocol == "responses";
            options.NativeResearchApiProtocol = protocol;
            options.NativeResearchTopology = "reviewed";
            options.AdaptiveResearchEnabled = nativeTurn;
            options.DevelopmentTraceDirectory = Path.Combine(directory, "traces");
            options.DevelopmentTraceStrict = true;
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            var nativeArguments = JsonSerializer.Serialize(new
            {
                query = "17 bar " + special, sourceKey = "internal-source-1",
                category = "", documentHint = "", topK = 20
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            using var factory = nativeTurn
                ? new QueuedHttpClientFactory(
                    Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
                    Completion("""{"decision":"ready","queries":[]}"""),
                    NativeResponseFunction("search_corpus", nativeArguments), NativeResponseAnswer(DiagnosticAnswer))
                : new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "fake-wire-key", logger)
                .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", content)), CancellationToken.None);
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(nativeTurn ? 4 : 2, factory.Requests.Count);
            var starts = File.ReadAllLines(options.ExternalUsageLedgerPath + ".reservations.jsonl")
                .Select(line => JsonDocument.Parse(line)).ToArray();
            var traces = Directory.GetFiles(options.DevelopmentTraceDirectory, "saaia-exact-v1-*.json")
                .Select(path => JsonDocument.Parse(File.ReadAllText(path))).ToArray();
            var probes = logger.Events.Where(entry => entry.Values.TryGetValue("Operation", out var operation)
                    && Equals(operation, "llm.call"))
                .Select(entry => JsonDocument.Parse((string)entry.Values["Measurements"]!)).ToArray();
            try
            {
                Assert.Equal(factory.Requests.Count, starts.Length);
                Assert.Equal(factory.Requests.Count, traces.Length);
                Assert.Equal(factory.Requests.Count, probes.Length);
                var legacyOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                };
                for (var index = 0; index < factory.Requests.Count; index++)
                {
                    var actual = factory.Requests[index].Body;
                    using var wire = JsonDocument.Parse(actual);
                    // Reserializing the actual JSON tree with the previous outer
                    // encoder changes only wire escapes, never decoded messages.
                    var legacy = JsonSerializer.Serialize(wire.RootElement, legacyOptions);
                    using var legacyWire = JsonDocument.Parse(legacy);
                    Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                        System.Text.Json.Nodes.JsonNode.Parse(actual), System.Text.Json.Nodes.JsonNode.Parse(legacy)));
                    var collection = protocol == "responses" ? "input" : "messages";
                    var messages = wire.RootElement.GetProperty(collection);
                    var legacyMessages = legacyWire.RootElement.GetProperty(collection);
                    for (var message = 0; message < messages.GetArrayLength(); message++)
                        if (messages[message].TryGetProperty("content", out var messageContent))
                            Assert.Equal(legacyMessages[message].GetProperty("content").GetString(), messageContent.GetString());
                    var userText = messages.EnumerateArray().Last(item => item.TryGetProperty("role", out var role) && role.GetString() == "user")
                        .GetProperty("content").GetString()!;
                    using var user = JsonDocument.Parse(userText);
                    Assert.Equal(request.Handoff.RequestText, user.RootElement.GetProperty("request").GetString());
                    if (index == factory.Requests.Count - 1)
                    {
                        var evidence = Assert.Single(user.RootElement.GetProperty("evidence").EnumerateArray());
                        Assert.Equal("E1", evidence.GetProperty("evidenceId").GetString());
                        Assert.Equal(content, evidence.GetProperty("content").GetString());
                        if (nativeTurn)
                        {
                            var call = Assert.Single(messages.EnumerateArray(), item =>
                                item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
                            Assert.Equal(nativeArguments, call.GetProperty("arguments").GetString());
                            var output = Assert.Single(messages.EnumerateArray(), item =>
                                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
                            Assert.Equal(call.GetProperty("call_id").GetString(), output.GetProperty("call_id").GetString());
                            var legacyOutput = Assert.Single(legacyMessages.EnumerateArray(), item =>
                                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
                            Assert.Equal(legacyOutput.GetProperty("output").GetString(), output.GetProperty("output").GetString());
                            var reasoning = Assert.Single(messages.EnumerateArray(), item =>
                                item.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
                            Assert.Equal("synthetic-sealed-reasoning", reasoning.GetProperty("encrypted_content").GetString());
                            Assert.Contains(wire.RootElement.GetProperty("tools").EnumerateArray(), tool =>
                                tool.GetProperty("name").GetString() == "search_corpus");
                        }
                    }
                    var actualBytes = Encoding.UTF8.GetByteCount(actual);
                    var legacyBytes = Encoding.UTF8.GetByteCount(legacy);
                    Assert.True(actualBytes < legacyBytes, "The outer encoder must save bytes without changing decoded prompt strings.");
                    var start = starts[index].RootElement;
                    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
                    var trace = Assert.Single(traces, item => item.RootElement.GetProperty("requestJson").GetString() == actual)
                        .RootElement;
                    Assert.Equal(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(trace.GetProperty("requestJson").GetString()!));
                    Assert.Equal(digest, trace.GetProperty("requestSha256").GetString(), ignoreCase: true);
                    var probe = Assert.Single(probes, item => item.RootElement.GetProperty("request_sha256").GetString() == digest)
                        .RootElement;
                    Assert.Equal(actual.Length, probe.GetProperty("request_characters").GetInt32());
                    Assert.Equal(actualBytes + 256, probe.GetProperty("budget_input_token_bound").GetInt32());
                    Assert.Equal(start.GetProperty("RequestId").GetString(), probe.GetProperty("budget_request_id").GetString());
                    Assert.Equal(actualBytes + 256, start.GetProperty("EstimatedInputTokens").GetInt32());
                    var outputMaximum = start.GetProperty("MaximumOutputTokens").GetInt32();
                    var actualReserve = start.GetProperty("ReservedCostUsd").GetDecimal();
                    Assert.Equal(AdvancedAnalysisExternalBudgetGuard.CalculateCost(
                        actualBytes + 256, outputMaximum, 0, options, cacheWriteRateOverride: 1m), actualReserve);
                    Assert.True(actualReserve < AdvancedAnalysisExternalBudgetGuard.CalculateCost(
                        legacyBytes + 256, outputMaximum, 0, options, cacheWriteRateOverride: 1m));
                }
            }
            finally
            {
                foreach (var start in starts) start.Dispose();
                foreach (var trace in traces) trace.Dispose();
                foreach (var probe in probes) probe.Dispose();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public void Wire_serialization_documentary_measurement_uses_one_compact_outer_encoding_for_full_and_control(
        string protocol)
    {
        const string sample = "<tag attr=\"v\"> & \\quoted\\ 😀 e\u0301";
        var control = string.Concat(Enumerable.Repeat(sample, 200));
        var inner = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        var outer = new JsonSerializerOptions(inner) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var baselineUser = new Dictionary<string, object?> { ["request"] = sample, ["control"] = control };
        var fullUser = new Dictionary<string, object?>(baselineUser)
        {
            ["evidence"] = new[] { new { evidenceId = "E1", content = sample + control } },
            ["structuredTables"] = new[] { new { rows = new[] { sample } } },
            ["sourceCatalogue"] = new { sources = new[] { new { sourceKey = "internal-source-1", displayName = sample } } },
            ["alternativeTableReadings"] = new { instruction = sample },
            ["visualSources"] = new[] { new { assetId = "A1", title = sample } },
            ["visualSourceCapabilities"] = new { modelPixelsProvided = false },
            ["alternativeNativeReadings"] = new { lines = new[] { sample } },
            ["nativeObservationCapabilities"] = new { nativeObservationReadAvailable = true }
        };
        Dictionary<string, object?> Payload(Dictionary<string, object?> user, bool includeDocumentaryHistory)
        {
            var messages = new List<object> { new { role = "system", content = control } };
            if (protocol == "responses")
            {
                messages.Add(new { type = "reasoning", encrypted_content = control });
                if (includeDocumentaryHistory)
                {
                    messages.Add(new { type = "function_call", call_id = "call_1", name = "search_corpus", arguments = sample });
                    messages.Add(new { type = "function_call_output", call_id = "call_1", output = sample });
                }
            }
            else if (includeDocumentaryHistory)
            {
                messages.Add(new { role = "assistant", tool_calls = new[] { new { id = "call_1", arguments = sample } } });
                messages.Add(new { role = "tool", tool_call_id = "call_1", content = sample });
            }
            messages.Add(new { role = "user", content = JsonSerializer.Serialize(user, inner) });
            return new Dictionary<string, object?>
            {
                [protocol == "responses" ? "input" : "messages"] = messages,
                ["tools"] = new[] { new { name = "search_corpus", description = control } }
            };
        }
        // Independent typed controls: do not call the product's projection or
        // strip fields from its result to obtain the expected contribution.
        var full = JsonSerializer.Serialize(Payload(fullUser, true), outer);
        var baseline = JsonSerializer.Serialize(Payload(baselineUser, false), outer);
        var expected = full.Length - baseline.Length;
        Assert.True(expected > 0);
        Assert.Equal(expected, OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(full));
        // Lexical outer escaping cannot change this documentary measurement.
        var oldEncodedFull = JsonSerializer.Serialize(Payload(fullUser, true), inner);
        Assert.Equal(expected, OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(oldEncodedFull));
    }
}
