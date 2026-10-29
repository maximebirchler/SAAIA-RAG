using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string QualityMissingMarker = """{"outcome":"answered","answerText":"PRIVATE_WRITER_TEXT","claims":[{"claimId":"C1","text":"PRIVATE_CLAIM_TEXT","evidenceIds":["E1"]}]}""";

    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task WriterQuality_actual_writer_and_critic_keep_source_conditions_and_useful_variety_without_a_distinct_quota(
        string style, string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"assembly","topK":12}]}"""),
            EarlyReply(protocol, NativeAnswered), EarlyReply(protocol, NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;
        const string canonical = "L'assemblage ne peut être utilisé qu'avec la bride X. Il est interdit au-delà de 17 bar.";
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        foreach (var call in factory.Requests.Skip(1))
        {
            var system = PayloadAuditSystem(call.Body);
            Assert.Contains("permission to reuse an item is not a request", system);
            Assert.Contains("Respect an explicitly requested repetition", system);
            Assert.Contains("not an extra\nmandatory distinct-item quota", system.Replace("\r\n", "\n"));
            Assert.Contains("Do not turn conditional support into an unconditional assertion", system);
            Assert.DoesNotContain("Do not add an\nextra diversity requirement", system.Replace("\r\n", "\n"));
            Assert.Contains(canonical, WorkspaceUser(call.Body).GetProperty("evidence")[0].GetProperty("content").GetString());
        }
        // Scripted replies prove prompt transport and contracts, not the model's
        // interpretation of these conditions or professional output quality.
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WriterQuality_repair_reports_exact_failure_and_preserves_private_text_only_in_model_payload(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""),
            EarlyReply(protocol, QualityMissingMarker), EarlyReply(protocol, NativeAnswered));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null, logger)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "PRIVATE_DOCUMENT_TEXT")), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        var payload = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal("advanced_writer_claim_markers_invalid", payload.GetProperty("validationErrorCode").GetString());
        Assert.Contains("PRIVATE_WRITER_TEXT", payload.GetProperty("originalWriterJson").GetString());
        Assert.Contains("Do not turn conditional support into an unconditional assertion", PayloadAuditSystem(factory.Requests[2].Body));
        var diagnostic = Assert.Single(logger.Events, e => Equals(e.Values.GetValueOrDefault("Operation"), "llm.writer_repair"));
        Assert.Equal("repaired", diagnostic.Values["Outcome"]);
        Assert.Equal("advanced_writer_claim_markers_invalid", diagnostic.Values["ReasonCode"]);
        using var measurements = JsonDocument.Parse(diagnostic.Values["Measurements"]!.ToString()!);
        Assert.Equal("advanced_writer_claim_markers_invalid", measurements.RootElement.GetProperty("validation_error_code").GetString());
        Assert.True(measurements.RootElement.GetProperty("repair_scheduled").GetBoolean());
        Assert.DoesNotContain("PRIVATE_", diagnostic.Text);
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task WriterQuality_unavailable_repair_preserves_critic_reservation_and_logs_without_another_call(string protocol, bool critic)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), EarlyReply(protocol, QualityMissingMarker));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = critic ? 3 : 2;
        options.SemanticCriticEnabled = critic;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "PRIVATE_DOCUMENT_TEXT")), CancellationToken.None));

        Assert.Equal("advanced_writer_claim_markers_invalid", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
        var diagnostic = Assert.Single(logger.Events, e => Equals(e.Values.GetValueOrDefault("Operation"), "llm.writer_repair"));
        Assert.Equal("not_scheduled", diagnostic.Values["Outcome"]);
        Assert.Equal("model_budget_unavailable", diagnostic.Values["ReasonCode"]);
        using var measurements = JsonDocument.Parse(diagnostic.Values["Measurements"]!.ToString()!);
        Assert.Equal(0, measurements.RootElement.GetProperty("remaining_model_calls").GetInt32());
        Assert.Equal(critic, measurements.RootElement.GetProperty("critic_call_reserved").GetBoolean());
        Assert.False(measurements.RootElement.GetProperty("repair_scheduled").GetBoolean());
        Assert.DoesNotContain("PRIVATE_", diagnostic.Text);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WriterQuality_invalid_repair_fails_once_and_records_failure_without_recursive_repair(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""),
            EarlyReply(protocol, QualityMissingMarker), EarlyReply(protocol, QualityMissingMarker));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null, logger)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "PRIVATE_DOCUMENT_TEXT")), CancellationToken.None));

        Assert.Equal("advanced_writer_claim_markers_invalid", error.ErrorCode);
        Assert.Equal(3, factory.Requests.Count);
        var diagnostic = Assert.Single(logger.Events, e => Equals(e.Values.GetValueOrDefault("Operation"), "llm.writer_repair"));
        Assert.Equal("failed", diagnostic.Values["Outcome"]);
        Assert.Equal("writer_repair_failed", diagnostic.Values["ReasonCode"]);
        Assert.DoesNotContain("PRIVATE_", diagnostic.Text);
    }
}
