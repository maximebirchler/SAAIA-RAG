using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("EnvironmentVariables")]
public sealed class SourceTraceMetadataTests
{
    [Fact]
    public void Production_probe_and_log_keep_safe_source_identity_and_counts_without_document_content()
    {
        var previousRoot = Environment.GetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable);
        var previousContent = Environment.GetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT");
        var root = Path.Combine(Path.GetTempPath(), "saaia-safe-source-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, root);
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", "false");
            var trace = SourceBackedTraceEvent.Create("source-backed-agent-v2-synthetic", 7, SourceBackedPipelineStep.Planner,
                "source_backed_named_document_resolution.completed", new Dictionary<string, string>
                {
                    ["requested_reference"] = "PrivateFolder/SecretFile.pdf", ["requested_reference_length"] = "28",
                    ["status"] = "Ambiguous", ["reason"] = "multiple_exact_current_catalog_matches",
                    ["candidate_count"] = "2", ["exact_match_count"] = "2", ["private_text"] = "PRIVATE_PROMPT_SECRET"
                });
            var projection = typeof(ToolAgentOrchestrator).GetMethod("SourceBackedTraceMetadata", BindingFlags.NonPublic | BindingFlags.Static)!;
            var fields = ((string Key, object? Value)[])projection.Invoke(null, new object[] { trace })!;
            var memory = new ToolMemory();
            var orchestrator = new ToolAgentOrchestrator(new ApiClient(), new QueueLlmClient(Array.Empty<string>()), memory);
            var emit = typeof(ToolAgentOrchestrator).GetMethod("EmitRagTrace", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string turnId;
            using (var turn = ClientDiagnostics.Journal.BeginTurn())
            {
                turnId = turn.TurnId;
                emit.Invoke(orchestrator, new object[] { "source_backed_agent_v2.step", fields });
                turn.Complete();
            }
            var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TurnId == turnId);
            var probe = Assert.Single(record.Events, item => item.Operation == "agent.source_backed_agent_v2.step");
            Assert.Equal(trace.EventName, probe.Identifiers["source_event"]);
            Assert.Equal("Planner", probe.Identifiers["source_step"]);
            Assert.Equal("Ambiguous", probe.Identifiers["source_status"]);
            Assert.Equal(2, probe.Measurements["source_candidate_count"]);
            Assert.Equal(1, probe.Measurements["source_reference_has_directory"]);
            var exported = JsonSerializer.Serialize(record) + File.ReadAllText(ClientLog.StartupLogPath);
            Assert.Contains("source_event", exported);
            Assert.DoesNotContain("PrivateFolder", exported);
            Assert.DoesNotContain("SecretFile", exported);
            Assert.DoesNotContain("PRIVATE_PROMPT_SECRET", exported);
            Assert.DoesNotContain("source_fields=", exported);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, previousRoot);
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", previousContent);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Free_text_in_a_code_field_is_rejected_in_metadata_mode()
    {
        var previous = Environment.GetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT");
        try
        {
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", "false");
            var build = typeof(ToolAgentOrchestrator).GetMethod("BuildRagTraceLine", BindingFlags.NonPublic | BindingFlags.Static)!;
            var line = (string)build.Invoke(null, new object[] { "step", "synthetic", 1, 0L,
                new (string Key, object? Value)[] { ("source_event", "PRIVATE event content"),
                    ("source_fields", "private_document=SecretFile.pdf"), ("query", "PRIVATE prompt") } })!;
            Assert.DoesNotContain("PRIVATE", line);
            Assert.DoesNotContain("SecretFile", line);
            Assert.DoesNotContain("source_event=", line);
        }
        finally { Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", previous); }
    }
}
