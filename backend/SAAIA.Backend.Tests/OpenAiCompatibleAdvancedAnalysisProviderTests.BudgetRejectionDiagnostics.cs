using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("campaign", "advanced_external_budget_campaign_hard_limit")]
    [InlineData("job_cost", "advanced_external_budget_job_cost_limit")]
    [InlineData("job_calls", "advanced_external_budget_job_call_limit")]
    public async Task Budget_rejection_reports_the_exact_attempt_without_http_or_new_ledger_rows(
        string limit,
        string expectedCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-budget-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var request = BuildDirectRequest();
            // Obtain the serializer's exact request using the same fake provider
            // with available funds. No request is ever sent outside this process.
            var controlOptions = BudgetDiagnosticOptions(Path.Combine(directory, "control.jsonl"));
            using var controlFactory = new QueuedHttpClientFactory(
                Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            await new OpenAiCompatibleAdvancedAnalysisProvider(controlFactory, controlOptions, "fake-control-key")
                .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
            var payload = controlFactory.Requests[0].Body;
            var inputBound = Encoding.UTF8.GetByteCount(payload) + 256;
            var outputMaximum = Math.Clamp(controlOptions.PlannerMaxTokens, 256, 4_096);
            var attempted = AdvancedAnalysisExternalBudgetGuard.CalculateCost(
                inputBound, outputMaximum, 0, controlOptions, cacheWriteRateOverride: 1m);

            var options = BudgetDiagnosticOptions(Path.Combine(directory, "rejected.jsonl"));
            options.ExternalMaximumCallsPerJob = limit == "job_calls" ? 1 : 10;
            var charged = limit == "job_calls" ? .001m : .999m;
            var historyJob = limit == "campaign" ? Guid.NewGuid() : request.JobId;
            File.WriteAllText(options.ExternalUsageLedgerPath,
                JsonSerializer.Serialize(new { jobId = historyJob, costUsd = charged }) + Environment.NewLine);
            var reserved = 0m;
            if (limit == "campaign")
                reserved = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "gpt-5.6-luna")
                    .Reserve(Guid.NewGuid(), "other-job", "", 100).ReservedCostUsd;
            var usageBefore = File.ReadAllBytes(options.ExternalUsageLedgerPath);
            var journal = options.ExternalUsageLedgerPath + ".reservations.jsonl";
            var reservationsBefore = File.Exists(journal) ? File.ReadAllBytes(journal) : null;
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            using var rejectedFactory = new QueuedHttpClientFactory();
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(rejectedFactory, options, "fake-secret-budget-marker", logger)
                    .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None));

            Assert.Equal(expectedCode, error.ErrorCode);
            Assert.Empty(rejectedFactory.Requests);
            Assert.Equal(usageBefore, File.ReadAllBytes(options.ExternalUsageLedgerPath));
            if (reservationsBefore is null) Assert.False(File.Exists(journal));
            else Assert.Equal(reservationsBefore, File.ReadAllBytes(journal));
            Assert.Equal(inputBound, Assert.IsType<int>(error.Data["budget_attempted_input_token_bound"]));
            Assert.Equal(outputMaximum, Assert.IsType<int>(error.Data["budget_attempted_maximum_output_tokens"]));
            Assert.Equal(attempted, Assert.IsType<decimal>(error.Data["budget_attempted_reserve_usd"]));
            Assert.False(Assert.IsType<bool>(error.Data["budget_reservation_created"]));

            var call = Assert.Single(logger.Events, entry =>
                entry.Values.TryGetValue("Operation", out var operation) && Equals(operation, "llm.call"));
            Assert.Equal("failed", call.Values["Outcome"]);
            using var measurements = JsonDocument.Parse((string)call.Values["Measurements"]!);
            var values = measurements.RootElement;
            Assert.Equal(inputBound, values.GetProperty("budget_attempted_input_token_bound").GetInt32());
            Assert.Equal(outputMaximum, values.GetProperty("budget_attempted_maximum_output_tokens").GetInt32());
            Assert.Equal(attempted, values.GetProperty("budget_attempted_reserve_usd").GetDecimal());
            Assert.Equal(limit == "campaign" ? 0m : charged, values.GetProperty("budget_job_charged_usd").GetDecimal());
            Assert.Equal(0m, values.GetProperty("budget_job_reserved_usd").GetDecimal());
            Assert.Equal(1m, values.GetProperty("budget_job_cap_usd").GetDecimal());
            Assert.Equal(limit == "campaign" ? 1m : 1m - charged, values.GetProperty("budget_job_remaining_usd").GetDecimal());
            Assert.Equal(charged, values.GetProperty("budget_campaign_charged_usd").GetDecimal());
            Assert.Equal(reserved, values.GetProperty("budget_campaign_reserved_usd").GetDecimal());
            Assert.Equal(1m, values.GetProperty("budget_campaign_cap_usd").GetDecimal());
            Assert.Equal(1m - charged - reserved, values.GetProperty("budget_campaign_remaining_usd").GetDecimal());
            Assert.Equal(limit == "campaign" ? 0 : 1, values.GetProperty("budget_job_calls").GetInt32());
            Assert.Equal(options.ExternalMaximumCallsPerJob, values.GetProperty("budget_job_call_limit").GetInt32());
            Assert.False(values.GetProperty("budget_reservation_created").GetBoolean());
            Assert.Equal(0, values.GetProperty("http_attempt_count").GetInt32());
            Assert.Equal(0, values.GetProperty("diagnostic_dropped_measurements").GetInt32());
            Assert.Equal(0, values.GetProperty("diagnostic_truncated_values").GetInt32());
            Assert.False(values.TryGetProperty("budget_request_id", out _));
            Assert.False(values.TryGetProperty("budget_reserved_cost_usd", out _));
            Assert.DoesNotContain(payload, call.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Handoff.RequestText, call.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-secret-budget-marker", call.Text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Budget_rejection_diagnostics_copy_only_the_numeric_whitelist_and_false_reservation_marker()
    {
        var error = new AdvancedAnalysisProviderException("advanced_external_budget_campaign_hard_limit");
        error.Data["budget_attempted_input_token_bound"] = "private-payload-marker";
        error.Data["budget_attempted_reserve_usd"] = true;
        error.Data["budget_campaign_cap_usd"] = .50m;
        error.Data["budget_reservation_created"] = false;
        error.Data["unknown_numeric"] = 123;
        error.Data["request_body"] = "private-payload-marker";
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var method = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod(
            "AnnotateRejectedBudget", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (var probe = BackendDiagnostics.Start("test.budget_rejection", logger))
        {
            method.Invoke(null, [probe, error]);
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var measurements = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        var values = measurements.RootElement;
        Assert.Equal(.50m, values.GetProperty("budget_campaign_cap_usd").GetDecimal());
        Assert.False(values.GetProperty("budget_reservation_created").GetBoolean());
        Assert.False(values.TryGetProperty("budget_attempted_input_token_bound", out _));
        Assert.False(values.TryGetProperty("budget_attempted_reserve_usd", out _));
        Assert.False(values.TryGetProperty("unknown_numeric", out _));
        Assert.False(values.TryGetProperty("request_body", out _));
        Assert.DoesNotContain("private-payload-marker", entry.Text, StringComparison.Ordinal);
    }

    private static AdvancedAnalysisOptions BudgetDiagnosticOptions(string ledger)
    {
        var options = CreateOptions();
        options.Provider = options.ProviderKey = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://budget-fixture.invalid/v1";
        options.LlmModel = "gpt-5.6-luna";
        options.ExternalBudgetAuthorizedUsd = options.ExternalBudgetHardLimitUsd = options.ExternalMaximumCostPerJobUsd = 1m;
        options.ExternalBudgetSoftLimitUsd = .9m;
        options.ExternalMaximumCallsPerJob = 10;
        options.ExternalInputUsdPerMillionTokens = options.ExternalOutputUsdPerMillionTokens = 1m;
        options.ExternalCacheWriteInputUsdPerMillionTokens = 1m;
        options.ExternalCachedInputUsdPerMillionTokens = 0m;
        options.ExternalUsageLedgerPath = ledger;
        return options;
    }
}
