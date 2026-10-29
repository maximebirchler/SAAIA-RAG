using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", "evidence")]
    [InlineData("responses", "evidence")]
    [InlineData("chat-completions", "observations")]
    [InlineData("responses", "observations")]
    public async Task DocumentaryFinalGuard_primary_only_oversize_is_rejected_before_http_and_budget_reservation(
        string protocol, string collection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-documentary-final-guard-" + Guid.NewGuid().ToString("N"));
        var ledger = Path.Combine(directory, "usage.jsonl");
        try
        {
            using var factory = new QueuedHttpClientFactory();
            var options = WorkspaceOptions(protocol);
            options.Provider = "openai-dev";
            options.LlmLocation = "external-service";
            options.LlmBaseUrl = "https://api.example/v1";
            options.NativeObservationReadEnabled = false;
            options.VisualSourceCitationsEnabled = false;
            options.MaximumEvidencePromptCharacters = 8_000;
            options.ExternalMaximumCostPerJobUsd = 2m;
            options.ExternalUsageLedgerPath = ledger;
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key", logger);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider);
            var json = (JsonSerializerOptions)type.GetField("JsonOptions", flags)!.GetValue(null)!;
            var canonicalContent = string.Concat(Enumerable.Repeat("PRIVATE_DOCUMENT ±0.125 \"quoted\"\n", 400));
            var source = BuildEvidence("E1", canonicalContent);
            var projection = type.GetMethod("BuildPromptEvidence", flags)!.Invoke(provider,
                new object?[] { BuildDirectRequest(), new[] { source }, new Dictionary<string, HashSet<string>>(), null })!;
            var user = new JsonObject
            {
                ["request"] = "Return grounded JSON.",
                [collection] = JsonSerializer.SerializeToNode(projection, json)
            };
            var primary = Assert.Single(user[collection]!.AsArray())!.AsObject();
            Assert.InRange(primary["content"]!.GetValue<string>().Length, 1, canonicalContent.Length - 1);

            // Restore the complete canonical body after bounded selection to
            // exercise the final envelope gate independently of that selection.
            // No table/native/visual data or tool history can trigger a feature gate.
            primary["content"] = canonicalContent;
            primary["contentTruncated"] = false;
            primary["contentOffset"] = 0;
            primary["nextContentOffset"] = null;
            var prompt = user.ToJsonString(json);
            Assert.True(prompt.Length > options.MaximumEvidencePromptCharacters);
            Assert.Null(source.SourceOverview);
            Assert.Null(source.SourceObservation);
            Assert.Null(source.NativeObservation);
            Assert.False(File.Exists(ledger));
            Assert.False(File.Exists(ledger + ".reservations.jsonl"));

            var task = (Task)type.GetMethod("CompleteJsonAsync", flags)!.Invoke(provider,
                new object?[] { Guid.NewGuid(), "writer", "Return JSON.", prompt, 128,
                    CancellationToken.None, false, null, projection })!;
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => task);

            Assert.Equal("advanced_documentary_wire_budget_exceeded", error.ErrorCode);
            Assert.Empty(factory.Requests);
            Assert.False(File.Exists(ledger));
            Assert.False(File.Exists(ledger + ".reservations.jsonl"));
            var entry = Assert.Single(logger.Events, item => Equals(item.Values["Operation"], "llm.call"));
            Assert.Equal("failed", entry.Values["Outcome"]);
            Assert.Equal(error.ErrorCode, entry.Values["ReasonCode"]);
            using var diagnostic = JsonDocument.Parse((string)entry.Values["Measurements"]!);
            var measurements = diagnostic.RootElement;
            Assert.Equal(8_000, measurements.GetProperty("documentary_wire_maximum_characters").GetInt32());
            Assert.True(measurements.GetProperty("documentary_wire_characters").GetInt32() > 8_000);
            Assert.Equal(error.Data["documentaryWireCharacters"], measurements.GetProperty("documentary_wire_characters").GetInt32());
            Assert.DoesNotContain("PRIVATE_DOCUMENT", entry.Text, StringComparison.Ordinal);
        }
        finally
        {
            // Remove only this test's explicitly named files, then its empty directory.
            foreach (var path in new[] { ledger, ledger + ".lock", ledger + ".reservations.jsonl" })
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
