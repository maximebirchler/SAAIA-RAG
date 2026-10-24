using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LlmPayloadDiagnosticsTests
{
    [Fact]
    public void Wire_sections_tools_and_visible_evidence_are_counted_without_copying_content()
    {
        const string prompt = """{"request":"private question","evidence":[{"evidenceId":"E1","sourceKey":"internal-source-1","content":"alpha","contentTruncated":true,"supportingContentWindows":[{"content":"xyz"}]}]}""";
        const string wire = """{"input":[],"tools":[{"type":"function","name":"read_source","description":"é"}]}""";
        var logger = new DiagnosticRecordingLogger<LlmPayloadDiagnosticsTests>();
        using (var probe = BackendDiagnostics.Start("test.payload", logger))
        {
            LlmPayloadDiagnostics.AnnotateRequest(probe, prompt, wire);
            LlmPayloadDiagnostics.AnnotateSelectedTools(probe, """[{"function":{"name":"read_source","arguments":"private tool arguments"}}]""");
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var fields = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        Assert.Equal(1, fields.RootElement.GetProperty("tools_count").GetInt32());
        using var parsedWire = JsonDocument.Parse(wire);
        Assert.Equal(parsedWire.RootElement.GetProperty("tools").GetRawText().Length, fields.RootElement.GetProperty("tools_characters").GetInt32());
        Assert.Equal(8, fields.RootElement.GetProperty("visible_evidence_characters").GetInt32());
        Assert.Equal(1, fields.RootElement.GetProperty("truncated_evidence_count").GetInt32());
        Assert.Equal("E1", fields.RootElement.GetProperty("visible_evidence_ids")[0].GetString());
        Assert.Equal("read_source", fields.RootElement.GetProperty("selected_tools")[0].GetString());
        Assert.DoesNotContain("private question", entry.Text);
        Assert.DoesNotContain("private tool arguments", entry.Text);
        Assert.DoesNotContain("alpha", entry.Text);
    }

    [Fact]
    public void A_large_evidence_identity_sample_declares_its_bound()
    {
        var prompt = JsonSerializer.Serialize(new { evidence = Enumerable.Range(0, 50).Select(index => new { evidenceId = "E" + index, sourceKey = "source-" + index, content = "redacted" }) });
        var logger = new DiagnosticRecordingLogger<LlmPayloadDiagnosticsTests>();
        using (var probe = BackendDiagnostics.Start("test.payload", logger))
        {
            LlmPayloadDiagnostics.AnnotateRequest(probe, prompt, "{}");
            probe.Complete();
        }
        using var fields = JsonDocument.Parse((string)Assert.Single(logger.Events).Values["Measurements"]!);
        Assert.Equal(50, fields.RootElement.GetProperty("visible_evidence_count").GetInt32());
        Assert.Equal(32, fields.RootElement.GetProperty("visible_evidence_ids").GetArrayLength());
        Assert.True(fields.RootElement.GetProperty("evidence_ids_sampled").GetBoolean());
    }
}
