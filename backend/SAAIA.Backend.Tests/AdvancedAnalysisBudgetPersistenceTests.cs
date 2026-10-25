using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisBudgetPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "saaia-budget-persistence-" + Guid.NewGuid().ToString("N"));

    private AdvancedAnalysisOptions Options() => new()
    {
        ExternalBudgetAuthorizedUsd = 1m,
        ExternalBudgetHardLimitUsd = 1m,
        ExternalBudgetSoftLimitUsd = 0.9m,
        ExternalMaximumCostPerJobUsd = 1m,
        ExternalMaximumCallsPerJob = 1,
        ExternalInputUsdPerMillionTokens = 1m,
        ExternalCachedInputUsdPerMillionTokens = 0m,
        ExternalCacheWriteInputUsdPerMillionTokens = 1m,
        ExternalOutputUsdPerMillionTokens = 1m,
        ExternalUsageLedgerPath = Path.Combine(_directory, "usage.jsonl")
    };

    [Fact]
    public void Default_options_do_not_authorize_a_paid_campaign_or_assume_historical_prices()
    {
        var defaults = new AdvancedAnalysisOptions();
        Assert.Equal(0, defaults.ExternalBudgetAuthorizedUsd);
        Assert.Equal(0, defaults.ExternalInputUsdPerMillionTokens);
        Assert.Equal(0, defaults.ExternalOutputUsdPerMillionTokens);
        Assert.Throws<InvalidOperationException>(() => new AdvancedAnalysisExternalBudgetGuard(defaults, "openai-dev", "test"));
    }

    [Fact]
    public void Unsettled_call_survives_restart_and_keeps_the_job_call_slot()
    {
        var options = Options();
        var job = Guid.NewGuid();
        new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test")
            .Reserve(job, "writer", 100, 100);
        var restarted = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => restarted.Reserve(job, "writer", 100, 100));
        Assert.Equal("advanced_external_budget_job_call_limit", error.ErrorCode);
    }

    [Fact]
    public async Task Two_independent_guards_cannot_both_admit_the_same_last_job_slot()
    {
        var options = Options();
        var first = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var second = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var job = Guid.NewGuid();
        using var start = new ManualResetEventSlim();
        var admissions = await Task.WhenAll(new[] { first, second }.Select(guard => Task.Run(() =>
        {
            start.Wait();
            try { guard.Reserve(job, "writer", 100, 100); return true; }
            catch (AdvancedAnalysisProviderException error) when (error.ErrorCode == "advanced_external_budget_job_call_limit") { return false; }
        })).Append(Task.Run(() => { start.Set(); return false; })));
        Assert.Single(admissions, admitted => admitted);
    }

    [Fact]
    public void A_guard_created_before_another_settlement_observes_the_new_campaign_charge()
    {
        var options = Options();
        var first = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var stale = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var reservation = first.Reserve(Guid.NewGuid(), "writer", 100, 100);
        first.Complete(reservation, new(900_000, 0, 0));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => stale.Reserve(Guid.NewGuid(), "writer", 100, 200_000));
        Assert.Equal("advanced_external_budget_campaign_hard_limit", error.ErrorCode);
    }

    [Fact]
    public void Failed_terminal_append_keeps_reservation_recoverable_instead_of_refunding_it()
    {
        var options = Options();
        var guard = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var job = Guid.NewGuid();
        var reservation = guard.Reserve(job, "writer", 100, 100);
        Directory.CreateDirectory(options.ExternalUsageLedgerPath);
        Assert.ThrowsAny<Exception>(() => guard.Complete(reservation, new(100, 10, 0)));
        Directory.Delete(options.ExternalUsageLedgerPath);
        // The same owned reservation may be settled after repairing the ledger path.
        guard.Complete(reservation, new(100, 10, 0));
        guard.EndJob(job);
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal(reservation.RequestId, row.RootElement.GetProperty("requestId").GetString());
    }

    [Theory]
    [InlineData("{\"costUsd\":-1}")]
    [InlineData("{\"costUsd\":\"invalid\"}")]
    [InlineData("{}")]
    public void Invalid_cost_rows_fail_closed(string row)
    {
        var options = Options();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(options.ExternalUsageLedgerPath, row + Environment.NewLine);
        Assert.Throws<InvalidOperationException>(() => new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test"));
    }

    [Fact]
    public void Missing_usage_does_not_treat_four_characters_as_a_token_upper_bound()
    {
        var options = Options();
        var guard = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var reservation = guard.Reserve(Guid.NewGuid(), "writer", 4_000, 100);
        var charge = guard.Complete(reservation, new(null, null, null));
        Assert.True(charge.Usage.InputTokens >= 4_000, "A four-character average is not a conservative admission bound.");
    }

    [Fact]
    public void Unsettled_other_job_also_consumes_campaign_capacity_after_restart()
    {
        var options = Options();
        new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test")
            .Reserve(Guid.NewGuid(), "writer", 100, 800_000);
        var restarted = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => restarted.Reserve(Guid.NewGuid(), "writer", 100, 300_000));
        Assert.Equal("advanced_external_budget_campaign_hard_limit", error.ErrorCode);
    }

    [Fact]
    public void Legacy_settlements_and_new_starts_are_counted_once_each()
    {
        var options = Options();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(options.ExternalUsageLedgerPath, "{\"costUsd\":0.6}");
        var first = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var reservation = first.Reserve(Guid.NewGuid(), "writer", 100, 10);
        first.Complete(reservation, new(0, 200_000, 0));
        var restarted = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        // 0.6 legacy + 0.2 settled + 0.1 new is allowed; the settled WAL start is not charged twice.
        restarted.Reserve(Guid.NewGuid(), "writer", 100, 100_000);
        Assert.Equal(2, File.ReadAllLines(options.ExternalUsageLedgerPath).Length);
    }

    [Fact]
    public void Corrupt_reservation_journal_fails_closed_before_any_admission()
    {
        var options = Options();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(options.ExternalUsageLedgerPath + ".reservations.jsonl", "{\"schema\":\"advanced-budget-reservation.v1\"");
        Assert.Throws<InvalidOperationException>(() => new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test"));
    }

    [Fact]
    public void Serialized_payload_bound_covers_non_ascii_text_and_tool_definitions()
    {
        var options = Options();
        var payload = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = "漢字é😀" } }, tools = new[] { new { name = "search", description = new string('x', 1000) } } });
        var reservation = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test")
            .Reserve(Guid.NewGuid(), "writer", payload, 100);
        Assert.True(reservation.EstimatedInputTokens >= System.Text.Encoding.UTF8.GetByteCount(payload));
        Assert.True(reservation.EstimatedInputTokens > 1000, "Tool definitions must participate in admission, even with a tiny user question.");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(10, null)]
    [InlineData(-1, 10)]
    public void A_failed_response_without_complete_usage_is_explicitly_an_uncertain_reserved_charge(int? input, int? output)
    {
        var options = Options();
        var guard = new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test");
        var reservation = guard.Reserve(Guid.NewGuid(), "writer", 100, 100);
        guard.Fail(reservation, "synthetic_invalid_response", new AdvancedAnalysisLlmUsage(input, output, null));
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal("reserved_upper_bound", row.RootElement.GetProperty("usageSource").GetString());
        Assert.Equal(reservation.ReservedCostUsd, row.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.True(row.RootElement.GetProperty("inputTokens").GetInt32() > 0);
    }

    [Fact]
    public void Cached_pricing_cannot_exceed_the_rate_used_for_admission()
    {
        var options = Options();
        options.ExternalCachedInputUsdPerMillionTokens = 2m;
        Assert.Throws<InvalidOperationException>(() => new AdvancedAnalysisExternalBudgetGuard(options, "openai-dev", "test"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
