using System.Text;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisLlmUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    int? CacheWriteTokens = null);

internal sealed record AdvancedAnalysisRateLimitTelemetry(
    string? ProviderRequestId,
    long? RequestLimit,
    long? RequestRemaining,
    string? RequestReset,
    long? TokenLimit,
    long? TokenRemaining,
    string? TokenReset,
    long? RetryAfterMilliseconds);

internal sealed record AdvancedAnalysisBudgetCharge(
    AdvancedAnalysisLlmUsage Usage,
    decimal CostUsd,
    string UsageSource);

/// <summary>
/// Local, persistent stop for any token-priced external LLM provider.
/// The ledger deliberately contains no prompt, evidence, endpoint or secret.
/// </summary>
internal sealed class AdvancedAnalysisExternalBudgetGuard
{
    internal sealed record Reservation(
        long Id,
        string RequestId,
        Guid JobId,
        string Role,
        int EstimatedInputTokens,
        int MaximumOutputTokens,
        decimal ReservedCostUsd);

    private readonly object _gate = new();
    private readonly AdvancedAnalysisOptions _options;
    private readonly string _providerKey;
    private readonly string _modelId;
    private readonly decimal _cacheWriteRateUsdPerMillionTokens;
    private readonly AdvancedAnalysisBudgetLedger _ledger;
    private readonly Dictionary<long, Reservation> _reservations = new();
    private long _nextReservationId;

    internal AdvancedAnalysisExternalBudgetGuard(
        AdvancedAnalysisOptions options,
        string providerKey,
        string modelId)
    {
        _options = Validate(options);
        _providerKey = providerKey;
        _modelId = modelId;
        _cacheWriteRateUsdPerMillionTokens = options.ExternalCacheWriteInputUsdPerMillionTokens
            ?? (string.Equals(providerKey, "openai-dev", StringComparison.OrdinalIgnoreCase)
                ? options.ExternalInputUsdPerMillionTokens * 1.25m
                : options.ExternalInputUsdPerMillionTokens);
        _ledger = new AdvancedAnalysisBudgetLedger(options.ExternalUsageLedgerPath);
        _ledger.Locked(snapshot => snapshot.Charged); // Fail closed before the first call on corrupt history.
    }

    internal Reservation Reserve(
        Guid jobId,
        string role,
        int inputCharacters,
        int maximumOutputTokens)
    {
        // Legacy callers only know UTF-16 length: at most three UTF-8 bytes per
        // code unit, plus protocol overhead. Never use chars/4 as an upper bound.
        return ReserveTokens(jobId, role, checked(Math.Max(0, inputCharacters) * 3 + 256), maximumOutputTokens);
    }

    internal Reservation Reserve(Guid jobId, string role, string serializedPayload, int maximumOutputTokens)
        => ReserveTokens(jobId, role, checked(Encoding.UTF8.GetByteCount(serializedPayload) + 256), maximumOutputTokens);

    private Reservation ReserveTokens(Guid jobId, string role, int estimatedInputTokens, int maximumOutputTokens)
    {
        maximumOutputTokens = Math.Max(1, maximumOutputTokens);
        var reservedCost = CalculateCost(
            estimatedInputTokens,
            maximumOutputTokens,
            cachedInputTokens: 0,
            _options,
            cacheWriteRateOverride: _cacheWriteRateUsdPerMillionTokens);

        lock (_gate)
        {
            return _ledger.Locked(snapshot =>
            {
                var job = snapshot.Job(jobId);
                if (job.Calls >= _options.ExternalMaximumCallsPerJob)
                    throw Exceeded("advanced_external_budget_job_call_limit");
                if (job.Charged + job.Reserved + reservedCost
                    > _options.ExternalMaximumCostPerJobUsd)
                {
                    throw Exceeded("advanced_external_budget_job_cost_limit");
                }
                if (snapshot.Charged + snapshot.Reserved + reservedCost
                    > _options.ExternalBudgetHardLimitUsd)
                {
                    throw Exceeded("advanced_external_budget_campaign_hard_limit");
                }

                var reservation = new Reservation(
                    ++_nextReservationId,
                    Guid.NewGuid().ToString("N"),
                    jobId,
                    role,
                    estimatedInputTokens,
                    maximumOutputTokens,
                    reservedCost);
                _ledger.AppendStart(reservation);
                _reservations.Add(reservation.Id, reservation);
                return reservation;
            });
        }
    }

    internal AdvancedAnalysisBudgetCharge Complete(
        Reservation reservation,
        AdvancedAnalysisLlmUsage usage,
        long elapsedMilliseconds = 0,
        int httpAttemptCount = 1,
        string? observedModelId = null,
        AdvancedAnalysisRateLimitTelemetry? rateLimit = null)
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
            errorCode: null,
            elapsedMilliseconds,
            httpAttemptCount,
            observedModelId,
            rateLimit);
    }

    internal void Fail(
        Reservation reservation,
        string errorCode,
        long elapsedMilliseconds = 0,
        int httpAttemptCount = 1)
        => Close(
            reservation,
            new AdvancedAnalysisLlmUsage(
                reservation.EstimatedInputTokens,
                reservation.MaximumOutputTokens,
                0),
            "reserved_upper_bound",
            success: false,
            errorCode,
            elapsedMilliseconds,
            httpAttemptCount);

    internal void Fail(
        Reservation reservation,
        string errorCode,
        AdvancedAnalysisLlmUsage usage,
        long elapsedMilliseconds = 0,
        int httpAttemptCount = 1)
    {
        var hasProviderUsage = usage.InputTokens is >= 0 && usage.OutputTokens is >= 0;
        Close(
            reservation,
            hasProviderUsage ? usage : new(reservation.EstimatedInputTokens, reservation.MaximumOutputTokens, 0),
            hasProviderUsage ? "provider_usage" : "reserved_upper_bound",
            success: false,
            errorCode,
            elapsedMilliseconds,
            httpAttemptCount);
    }

    internal void Reject(
        Reservation reservation,
        string errorCode,
        long elapsedMilliseconds = 0,
        int httpAttemptCount = 1,
        AdvancedAnalysisRateLimitTelemetry? rateLimit = null)
        => Close(
            reservation,
            new AdvancedAnalysisLlmUsage(0, 0, 0),
            "provider_http_rejected",
            success: false,
            errorCode,
            elapsedMilliseconds,
            httpAttemptCount,
            rateLimit: rateLimit);

    internal void EndJob(Guid jobId)
    {
        lock (_gate)
        {
            if (_reservations.Values.Any(item => item.JobId == jobId))
                throw new InvalidOperationException(
                    "Advanced-analysis budget job ended with an open reservation.");
            _ledger.Locked(snapshot =>
            {
                if (snapshot.Job(jobId).Reserved > 0)
                    throw new InvalidOperationException("Advanced-analysis budget job ended with an unresolved durable reservation.");
                return true;
            });
        }
    }

    private AdvancedAnalysisBudgetCharge Close(
        Reservation reservation,
        AdvancedAnalysisLlmUsage usage,
        string usageSource,
        bool success,
        string? errorCode,
        long elapsedMilliseconds,
        int httpAttemptCount,
        string? observedModelId = null,
        AdvancedAnalysisRateLimitTelemetry? rateLimit = null)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservation.Id, out var owned) || owned != reservation)
            {
                throw new InvalidOperationException(
                    "Unknown advanced-analysis budget reservation.");
            }
            var cost = CalculateCost(
                usage.InputTokens ?? reservation.EstimatedInputTokens,
                usage.OutputTokens ?? reservation.MaximumOutputTokens,
                usage.CachedInputTokens ?? 0,
                _options,
                usage.CacheWriteTokens,
                _cacheWriteRateUsdPerMillionTokens);
            if (usageSource == "provider_usage"
                && !usage.CacheWriteTokens.HasValue
                && _cacheWriteRateUsdPerMillionTokens > _options.ExternalInputUsdPerMillionTokens)
                usageSource = "provider_usage_cache_write_upper_bound";
            _ledger.Locked(snapshot =>
            {
                if (!snapshot.OpenRequests.Contains(reservation.RequestId))
                    throw new InvalidOperationException("Advanced-analysis budget reservation is not open in the durable ledger.");
                _ledger.AppendCharge(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    requestId = reservation.RequestId,
                    traceId = reservation.JobId,
                    jobId = reservation.JobId,
                    provider = _providerKey,
                    modelId = _modelId,
                    observedModelId = NormalizeObservedModelId(observedModelId),
                    role = reservation.Role,
                    inputTokens = usage.InputTokens,
                    outputTokens = usage.OutputTokens,
                    cachedInputTokens = usage.CachedInputTokens,
                    cacheWriteTokens = usage.CacheWriteTokens,
                    cacheWriteInputUsdPerMillionTokens = _cacheWriteRateUsdPerMillionTokens,
                    costUsd = cost,
                    softLimitReached = snapshot.Charged + cost
                        >= _options.ExternalBudgetSoftLimitUsd,
                    success,
                    usageSource,
                    errorCode,
                    elapsedMilliseconds = Math.Max(0, elapsedMilliseconds),
                    timeToFirstTokenMilliseconds = (long?)null,
                    httpAttemptCount = Math.Max(0, httpAttemptCount),
                    retryCount = Math.Max(0, httpAttemptCount - 1),
                    providerRequestId = rateLimit?.ProviderRequestId,
                    rateLimitRequestLimit = rateLimit?.RequestLimit,
                    rateLimitRequestRemaining = rateLimit?.RequestRemaining,
                    rateLimitRequestReset = rateLimit?.RequestReset,
                    rateLimitTokenLimit = rateLimit?.TokenLimit,
                    rateLimitTokenRemaining = rateLimit?.TokenRemaining,
                    rateLimitTokenReset = rateLimit?.TokenReset,
                    retryAfterMilliseconds = rateLimit?.RetryAfterMilliseconds
                });
                return true;
            });
            // A failed append must leave the durable and in-memory reservation open.
            _reservations.Remove(reservation.Id);
            return new AdvancedAnalysisBudgetCharge(usage, cost, usageSource);
        }
    }

    private static string? NormalizeObservedModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 256 ? normalized : null;
    }

    internal static decimal CalculateCost(
        int inputTokens,
        int outputTokens,
        int cachedInputTokens,
        AdvancedAnalysisOptions options,
        int? cacheWriteTokens = null,
        decimal? cacheWriteRateOverride = null)
    {
        inputTokens = Math.Max(0, inputTokens);
        outputTokens = Math.Max(0, outputTokens);
        cachedInputTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var uncachedInput = inputTokens - cachedInputTokens;
        var cacheWriteRate = cacheWriteRateOverride
            ?? options.ExternalCacheWriteInputUsdPerMillionTokens
            ?? options.ExternalInputUsdPerMillionTokens;
        var writes = Math.Clamp(cacheWriteTokens
            ?? (cacheWriteRate > options.ExternalInputUsdPerMillionTokens ? uncachedInput : 0), 0, uncachedInput);
        return decimal.Round(
            ((uncachedInput - writes) * options.ExternalInputUsdPerMillionTokens
             + cachedInputTokens * options.ExternalCachedInputUsdPerMillionTokens
             + writes * cacheWriteRate
             + outputTokens * options.ExternalOutputUsdPerMillionTokens)
            / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
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
            || options.ExternalInputUsdPerMillionTokens <= 0
            || options.ExternalCachedInputUsdPerMillionTokens < 0
            || options.ExternalCachedInputUsdPerMillionTokens > Math.Max(
                options.ExternalInputUsdPerMillionTokens,
                options.ExternalCacheWriteInputUsdPerMillionTokens ?? options.ExternalInputUsdPerMillionTokens)
            || options.ExternalCacheWriteInputUsdPerMillionTokens is < 0
            || options.ExternalOutputUsdPerMillionTokens <= 0
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
