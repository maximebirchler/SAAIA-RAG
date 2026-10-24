using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionStageDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_ingestion_counts_and_the_terminal_phase_survive_failure_and_cancellation(bool cancelled)
    {
        var logger = new DiagnosticRecordingLogger<IngestionStageDiagnosticsTests>();
        var job = new IngestionJob(Guid.NewGuid(), Guid.NewGuid(), "upsert", "private-source-path", null, Guid.NewGuid(), 3, null);
        using (var diagnostics = new IngestionAttemptDiagnostics(job, "worker-17", logger))
        {
            diagnostics.Set("pages", 4);
            diagnostics.Begin("embedding_and_index_write");
            diagnostics.Fail(cancelled ? new JobCanceledException("private marker") : new IOException("private marker"));
        }
        var completed = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var operation) && Equals(operation, "ingestion.upsert"));
        Assert.Equal(cancelled ? "cancelled" : "failed", completed.Values["Outcome"]);
        using var fields = JsonDocument.Parse((string)completed.Values["Measurements"]!);
        Assert.Equal(4, fields.RootElement.GetProperty("pages").GetInt32());
        Assert.Equal("embedding_and_index_write", fields.RootElement.GetProperty("terminal_phase").GetString());
        Assert.Equal(1, fields.RootElement.GetProperty("completed_phases").GetInt32());
        Assert.DoesNotContain("private-source-path", completed.Text);
        Assert.DoesNotContain("private marker", completed.Text);
    }

    [Fact]
    public async Task Extraction_stage_reports_failure_from_the_actual_conversion_port()
    {
        var logger = new DiagnosticRecordingLogger<IngestionStageDiagnosticsTests>();
        var options = new DocumentIntelligenceOptions
        {
            Enabled = true, Provider = "docling", NativeTextCoverageReconciliationEnabled = false,
            NativePdfImageInventoryEnabled = false
        };
        await Assert.ThrowsAsync<IOException>(() => IngestionExtractionStage.ExtractAsync("unused-synthetic.pdf", options,
            (_, _) => throw new IOException("private document marker"), logger, CancellationToken.None));
        var entry = Assert.Single(logger.Events, item => item.Values.TryGetValue("Operation", out var operation) && Equals(operation, "ingestion.source_extraction"));
        Assert.Equal("failed", entry.Values["Outcome"]);
        Assert.DoesNotContain("private document marker", entry.Text);
    }
}
