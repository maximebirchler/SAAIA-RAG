using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LlmPayloadContentDiagnosticsTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("\"0\"")]
    [InlineData("{}")]
    [InlineData("1.5")]
    public void A_present_invalid_offset_is_not_reported_as_an_exact_repeat_of_the_default_window(string offsetJson)
    {
        var prompt = "{\"evidence\":[{\"sourceKey\":\"source-1\",\"physicalPageStart\":5,\"physicalPageEnd\":5,\"content\":\"PRIVATE_BODY\"},"
            + "{\"sourceKey\":\"source-1\",\"physicalPageStart\":5,\"physicalPageEnd\":5,\"content\":\"PRIVATE_BODY\",\"contentOffset\":" + offsetJson + "}]}";
        var logger = new DiagnosticRecordingLogger<LlmPayloadContentDiagnosticsTests>();
        using (var probe = BackendDiagnostics.Start("test.composition", logger))
        {
            LlmPayloadDiagnostics.AnnotateRequest(probe, prompt, "{}");
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var fields = JsonDocument.Parse(entry.Values["Measurements"]!.ToString()!);
        Assert.Equal(0, fields.RootElement.GetProperty("same_source_repeated_text_window_count").GetInt32());
        Assert.DoesNotContain("PRIVATE", entry.Text);
    }

    [Fact]
    public void Malformed_optional_diagnostics_do_not_change_the_request_outcome_or_publish_raw_values()
    {
        const string prompt = """{"evidence":[{"sourceKey":"PRIVATE unsafe handle","physicalPageStart":"PRIVATE_PAGE","physicalPageEnd":{},"contentOffset":null,"contentRole":{"private":"PRIVATE_ROLE"},"content":"PRIVATE_CONTENT"},null,17]}""";
        var logger = new DiagnosticRecordingLogger<LlmPayloadContentDiagnosticsTests>();
        using (var probe = BackendDiagnostics.Start("test.composition", logger))
        {
            LlmPayloadDiagnostics.AnnotateRequest(probe, prompt, "{}");
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var fields = JsonDocument.Parse(entry.Values["Measurements"]!.ToString()!);
        Assert.Equal(0, fields.RootElement.GetProperty("same_source_repeated_text_window_count").GetInt32());
        Assert.Equal(1, fields.RootElement.GetProperty("visible_unknown_role_count").GetInt32());
        Assert.Equal(2, fields.RootElement.GetProperty("malformed_evidence_item_count").GetInt32());
        Assert.DoesNotContain("PRIVATE", entry.Text);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("observations")]
    public void Documentary_composition_counts_exact_json_windows_without_logging_private_values(string collection)
    {
        var items = new object[]
        {
            new { evidenceId="E1", sourceKey="source-1", physicalPageStart=5, physicalPageEnd=5, contentOffset=0,
                content="PRIVATE ±0.125\n\"quoted\"", contentRole="content", supportingContentWindows=new[]{new{content="PRIVATE_EXTRA"}} },
            new { evidenceId="E2", sourceKey="source-1", physicalPageStart=5, physicalPageEnd=5, contentOffset=0,
                content="PRIVATE ±0.125\n\"quoted\"", contentRole="navigation" },
            new { evidenceId="E3", sourceKey="source-1", physicalPageStart=6, physicalPageEnd=6, contentOffset=0,
                content="PRIVATE ±0.125\n\"quoted\"", contentRole="mixed_navigation_content" },
            new { evidenceId="E4", sourceKey="source-2", physicalPageStart=5, physicalPageEnd=5, contentOffset=0,
                content="PRIVATE ±0.125\n\"quoted\"", contentRole="PRIVATE_UNKNOWN_ROLE" },
            new { evidenceId="E5", sourceKey="source-1", physicalPageStart=5, physicalPageEnd=5, contentOffset=20,
                content="PRIVATE ±0.125\n\"quoted\"", contentRole="content" }
        };
        var prompt = JsonSerializer.Serialize(new Dictionary<string, object>{[collection]=items});
        var logger = new DiagnosticRecordingLogger<LlmPayloadContentDiagnosticsTests>();
        using (var probe = BackendDiagnostics.Start("test.composition", logger))
        {
            LlmPayloadDiagnostics.AnnotateRequest(probe, prompt, "{}");
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var fields = JsonDocument.Parse(entry.Values["Measurements"]!.ToString()!);
        var values = fields.RootElement;
        Assert.Equal(2, values.GetProperty("visible_content_role_count").GetInt32());
        Assert.Equal(1, values.GetProperty("visible_navigation_role_count").GetInt32());
        Assert.Equal(1, values.GetProperty("visible_mixed_role_count").GetInt32());
        Assert.Equal(1, values.GetProperty("visible_unknown_role_count").GetInt32());
        Assert.Equal(0, values.GetProperty("malformed_evidence_item_count").GetInt32());
        Assert.Equal(1, values.GetProperty("same_source_repeated_text_window_count").GetInt32());
        using var parsed = JsonDocument.Parse(prompt);
        var array = parsed.RootElement.GetProperty(collection);
        var textCharacters = array.EnumerateArray().Sum(item => item.GetProperty("content").GetRawText().Length)
            + array[0].GetProperty("supportingContentWindows")[0].GetProperty("content").GetRawText().Length;
        Assert.Equal(textCharacters, values.GetProperty("evidence_serialized_content_characters").GetInt32());
        Assert.Equal(array.GetRawText().Length - textCharacters, values.GetProperty("evidence_nontext_json_characters").GetInt32());
        Assert.Equal(0, values.GetProperty("diagnostic_dropped_measurements").GetInt32());
        Assert.DoesNotContain("PRIVATE", entry.Text);
        Assert.DoesNotContain("0.125", entry.Text);
        Assert.DoesNotContain("quoted", entry.Text);
    }
}
