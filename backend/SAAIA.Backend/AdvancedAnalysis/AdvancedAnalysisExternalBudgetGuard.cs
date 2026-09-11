using System.Globalization;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisLlmUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens);

internal sealed record AdvancedAnalysisBudgetCharge(
    AdvancedAnalysisLlmUsage Usage,
    decimal CostUsd,
    string UsageSource);

/// <summary>
/// Local, persistent stop for the temporary token-priced OpenAI baseline.
/// The ledger deliberately contains no prompt, evidence, endpoint or secret.
/// </summary>
internal sealed class AdvancedAnalysisExternalBudgetGuard
{
    internal sealed record Reservation(
        long Id,
        Guid JobId,
        string Role,
        int EstimatedInputTokens,
        int MaximumOutputTokens,
        decimal ReservedCostUsd);

    private sealed class JobState
    {
        public int Calls;
        public decimal ChargedUsd;
        public decimal ReservedUsd;
    }

    private readonly object _gate = new();
    private readonly AdvancedAnalysisOptions _options;
    private readonly string _providerKey;
    private readonly string _modelId;
    private readonly Dictionary<Guid, JobState> _jobs = new();
    private readonly Dictionary<long, Reservation> _reservations = new();
    private long _nextReservationId;
    private decimal _recordedCostUsd;
    private decimal _reservedCostUsd;

    internal AdvancedAnalysisExternalBudgetGuard(
        AdvancedAnalysisOptions options,
        string providerKey,
        string modelId)
    {
        _options = Validate(options);
        _providerKey = providerKey;
        _modelId = modelId;
        _recordedCostUsd = ReadRecordedCost(options.ExternalUsageLedgerPath);
    }

    internal Reservation Reserve(
        Guid jobId,
        string role,
        int inputCharacters,
        int maximumOutputTokens)
    {
        var estimatedInputTokens = Math.Max(1,
            (int)Math.Ceiling(Math.Max(0, inputCharacters) / 4d));
        maximumOutputTokens = Math.Max(1, maximumOutputTokens);
        var reservedCost = CalculateCost(
            estimatedInputTokens,
            maximumOutputTokens,
            cachedInputTokens: 0,
            _options);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                job = ReadRecordedJobState(
                    _options.ExternalUsageLedgerPath,
                    jobId);
                _jobs.Add(jobId, job);
            }
            if (job.Calls >= _options.ExternalMaximumCallsPerJob)
                throw Exceeded("advanced_external_budget_job_call_limit");
            if (job.ChargedUsd + job.ReservedUsd + reservedCost
                > _options.ExternalMaximumCostPerJobUsd)
            {
                throw Exceeded("advanced_external_budget_job_cost_limit");
            }
            if (_recordedCostUsd + _reservedCostUsd + reservedCost
                > _options.ExternalBudgetHardLimitUsd)
            {
                throw Exceeded("advanced_external_budget_campaign_hard_limit");
            }

            var reservation = new Reservation(
                ++_nextReservationId,
                jobId,
                role,
                estimatedInputTokens,
                maximumOutputTokens,
                reservedCost);
            _reservations.Add(reservation.Id, reservation);
            _reservedCostUsd += reservedCost;
            job.Calls++;
            job.ReservedUsd += reservedCost;
            return reservation;
        }
    }

    internal AdvancedAnalysisBudgetCharge Complete(
        Reservation reservation,
        AdvancedAnalysisLlmUsage usage)
    {
        var hasProviderUsage = usage.InputTokens is >= 0
                               && usage.OutputTokens is >= 0;
        var billed = hasProviderUsage
            ? usage
            : new AdvancedAnalysisLlmUsage(
                reservation.EstimatedInputTokens,
                reservation.MaximumOutputTokens,
                0);
        return Close(
            reservation,
            billed,
            hasProviderUsage ? "provider_usage" : "reserved_upper_bound",
            success: true,
            errorCode: null);
    }

    internal void Fail(Reservation reservation, string errorCode)
        => Close(
            reservation,
            new AdvancedAnalysisLlmUsage(
                reservation.EstimatedInputTokens,
                reservation.MaximumOutputTokens,
                0),
            "reserved_upper_bound",
            success: false,
            errorCode);

    internal void Fail(
        Reservation reservation,
        string errorCode,
        AdvancedAnalysisLlmUsage usage)
        => Close(
            reservation,
            usage,
            "provider_usage",
            success: false,
            errorCode);

    internal void Reject(Reservation reservation, string errorCode)
        => Close(
            reservation,
            new AdvancedAnalysisLlmUsage(0, 0, 0),
            "provider_http_rejected",
            success: false,
            errorCode);

    internal void EndJob(Guid jobId)
    {
        lock (_gate)
        {
            if (_reservations.Values.Any(item => item.JobId == jobId))
                throw new InvalidOperationException(
                    "Advanced-analysis budget job ended with an open reservation.");
            _jobs.Remove(jobId);
        }
    }

    private AdvancedAnalysisBudgetCharge Close(
        Reservation reservation,
        AdvancedAnalysisLlmUsage usage,
        string usageSource,
        bool success,
        string? errorCode)
    {
        lock (_gate)
        {
            if (!_reservations.Remove(reservation.Id))
            {
                throw new InvalidOperationException(
                    "Unknown advanced-analysis budget reservation.");
            }
            var job = _jobs[reservation.JobId];
            var cost = CalculateCost(
                usage.InputTokens ?? reservation.EstimatedInputTokens,
                usage.OutputTokens ?? reservation.MaximumOutputTokens,
                usage.CachedInputTokens ?? 0,
                _options);
            _reservedCostUsd -= reservation.ReservedCostUsd;
            job.ReservedUsd -= reservation.ReservedCostUsd;
            job.ChargedUsd += cost;
            _recordedCostUsd += cost;
            AppendLedger(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                jobId = reservation.JobId,
                provider = _providerKey,
                modelId = _modelId,
                role = reservation.Role,
                inputTokens = usage.InputTokens,
                outputTokens = usage.OutputTokens,
                cachedInputTokens = usage.CachedInputTokens,
                costUsd = cost,
                softLimitReached = _recordedCostUsd
                    >= _options.ExternalBudgetSoftLimitUsd,
                success,
                usageSource,
                errorCode
            });
            return new AdvancedAnalysisBudgetCharge(usage, cost, usageSource);
        }
    }

    internal static decimal CalculateCost(
        int inputTokens,
        int outputTokens,
        int cachedInputTokens,
        AdvancedAnalysisOptions options)
    {
        inputTokens = Math.Max(0, inputTokens);
        outputTokens = Math.Max(0, outputTokens);
        cachedInputTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var uncachedInput = inputTokens - cachedInputTokens;
        return decimal.Round(
            (uncachedInput * options.ExternalInputUsdPerMillionTokens
             + cachedInputTokens * options.ExternalCachedInputUsdPerMillionTokens
             + outputTokens * options.ExternalOutputUsdPerMillionTokens)
            / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
    }

    private void AppendLedger(object entry)
    {
        var path = Path.GetFullPath(_options.ExternalUsageLedgerPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.AppendAllText(
            path,
            JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    private static decimal ReadRecordedCost(string rawPath)
    {
        var path = Path.GetFullPath(rawPath);
        if (!File.Exists(path))
            return 0m;
        decimal total = 0m;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("costUsd", out var value)
                    && value.TryGetDecimal(out var cost)
                    && cost >= 0)
                {
                    total += cost;
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    "The advanced-analysis usage ledger is invalid and must be reviewed before paid calls resume.");
            }
        }
        return total;
    }

    private static JobState ReadRecordedJobState(string rawPath, Guid jobId)
    {
        var state = new JobState();
        var path = Path.GetFullPath(rawPath);
        if (!File.Exists(path))
            return state;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("jobId", out var rawJobId)
                    || rawJobId.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(rawJobId.GetString(), out var parsedJobId)
                    || parsedJobId != jobId)
                {
                    continue;
                }
                state.Calls++;
                if (root.TryGetProperty("costUsd", out var rawCost)
                    && rawCost.TryGetDecimal(out var cost)
                    && cost >= 0)
                {
                    state.ChargedUsd += cost;
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    "The advanced-analysis usage ledger is invalid and must be reviewed before paid calls resume.");
            }
        }
        return state;
    }

    private static AdvancedAnalysisOptions Validate(AdvancedAnalysisOptions options)
    {
        if (options.ExternalBudgetAuthorizedUsd <= 0
            || options.ExternalBudgetSoftLimitUsd <= 0
            || options.ExternalBudgetHardLimitUsd <= 0
            || options.ExternalBudgetSoftLimitUsd
                > options.ExternalBudgetHardLimitUsd
            || options.ExternalBudgetHardLimitUsd
                > options.ExternalBudgetAuthorizedUsd
            || options.ExternalMaximumCostPerJobUsd <= 0
            || options.ExternalMaximumCostPerJobUsd
                > options.ExternalBudgetHardLimitUsd
            || options.ExternalMaximumCallsPerJob <= 0
            || options.ExternalInputUsdPerMillionTokens < 0
            || options.ExternalCachedInputUsdPerMillionTokens < 0
            || options.ExternalOutputUsdPerMillionTokens < 0
            || string.IsNullOrWhiteSpace(options.ExternalUsageLedgerPath))
        {
            throw new InvalidOperationException(
                "Advanced-analysis external budget configuration is invalid.");
        }
        return options;
    }

    private static AdvancedAnalysisProviderException Exceeded(string code)
        => new(code);
}
