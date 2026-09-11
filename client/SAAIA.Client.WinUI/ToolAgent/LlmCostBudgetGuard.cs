using System.Globalization;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal sealed record LlmPricingMetadata(
    decimal InputUsdPerMillionTokens,
    decimal CachedInputUsdPerMillionTokens,
    decimal CacheWriteUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens);

internal sealed record LlmBudgetOptions(
    decimal AuthorizedBudgetUsd,
    decimal SoftLimitUsd,
    decimal HardLimitUsd,
    decimal MaximumCostPerTurnUsd,
    int MaximumCallsPerTurn,
    string LedgerPath);

internal sealed record LlmBudgetSnapshot(
    decimal AuthorizedBudgetUsd,
    decimal SoftLimitUsd,
    decimal HardLimitUsd,
    decimal RecordedCostUsd,
    decimal ReservedCostUsd,
    decimal RemainingBeforeHardLimitUsd,
    bool SoftLimitReached,
    int CurrentTurnCalls,
    decimal CurrentTurnCostUsd);

internal sealed class LlmBudgetExceededException(string reason, LlmBudgetSnapshot snapshot)
    : InvalidOperationException("External LLM budget stopped the request: " + reason)
{
    internal string Reason { get; } = reason;
    internal LlmBudgetSnapshot Snapshot { get; } = snapshot;
}

internal sealed class LlmCostBudgetGuard
{
    private static readonly JsonSerializerOptions LedgerJson = new(JsonSerializerDefaults.Web);
    internal sealed class TurnState
    {
        internal required string CorrelationId { get; init; }
        internal int Calls;
        internal decimal CostUsd;
        internal decimal ReservedUsd;
    }

    internal sealed class Reservation
    {
        internal required long Id { get; init; }
        internal required string RequestId { get; init; }
        internal required string TraceId { get; init; }
        internal required LlmLogicalRole Role { get; init; }
        internal required int EstimatedInputTokens { get; init; }
        internal required int MaximumOutputTokens { get; init; }
        internal required decimal EstimatedCostUsd { get; init; }
        internal TurnState? Turn { get; init; }
    }

    private sealed record LedgerEntry(
        DateTimeOffset Timestamp,
        string RequestId,
        string TraceId,
        string Provider,
        string ModelId,
        string Role,
        int? InputTokens,
        int? OutputTokens,
        int? CachedInputTokens,
        int? CacheWriteTokens,
        int? ReasoningTokens,
        decimal CostUsd,
        bool Success,
        string UsageSource,
        string? ErrorCode);

    private sealed class Scope(AsyncLocal<TurnState?> slot, TurnState? previous) : IDisposable
    {
        private AsyncLocal<TurnState?>? _slot = slot;
        private readonly TurnState? _previous = previous;

        public void Dispose()
        {
            var slot = Interlocked.Exchange(ref _slot, null);
            if (slot is not null)
                slot.Value = _previous;
        }
    }

    private readonly object _gate = new();
    private readonly LlmProviderDescriptor _descriptor;
    private readonly LlmPricingMetadata _pricing;
    private readonly LlmBudgetOptions _options;
    private readonly AsyncLocal<TurnState?> _currentTurn = new();
    private readonly Dictionary<long, Reservation> _reservations = new();
    private long _nextReservationId;
    private decimal _recordedCostUsd;
    private decimal _reservedCostUsd;

    internal LlmCostBudgetGuard(
        LlmProviderDescriptor descriptor,
        LlmPricingMetadata pricing,
        LlmBudgetOptions options)
    {
        _descriptor = descriptor;
        _pricing = pricing;
        _options = Validate(options);
        _recordedCostUsd = ReadRecordedCost(options.LedgerPath);
    }

    internal IDisposable BeginTurn(string? correlationId = null)
    {
        var previous = _currentTurn.Value;
        _currentTurn.Value = new TurnState
        {
            CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? "turn-" + Guid.NewGuid().ToString("N")[..12]
                : correlationId.Trim()
        };
        return new Scope(_currentTurn, previous);
    }

    internal Reservation Reserve(
        LlmLogicalRole role,
        int estimatedInputTokens,
        int maximumOutputTokens)
    {
        estimatedInputTokens = Math.Max(0, estimatedInputTokens);
        maximumOutputTokens = Math.Max(0, maximumOutputTokens);
        var estimatedCost = CalculateCost(
            new LlmTokenUsage(estimatedInputTokens, maximumOutputTokens),
            _pricing);

        lock (_gate)
        {
            var turn = _currentTurn.Value;
            if (turn is not null && turn.Calls >= _options.MaximumCallsPerTurn)
                throw Exceeded("maximum_calls_per_turn", turn);
            if (turn is not null
                && turn.CostUsd + turn.ReservedUsd + estimatedCost
                > _options.MaximumCostPerTurnUsd)
            {
                throw Exceeded("maximum_cost_per_turn", turn);
            }
            if (_recordedCostUsd + _reservedCostUsd + estimatedCost
                > _options.HardLimitUsd)
            {
                throw Exceeded("campaign_hard_limit", turn);
            }

            var id = ++_nextReservationId;
            var traceId = SourceBackedRag.SourceBackedTelemetryContext.TraceId
                          ?? turn?.CorrelationId
                          ?? "unscoped";
            var reservation = new Reservation
            {
                Id = id,
                RequestId = "llm-" + Guid.NewGuid().ToString("N")[..12],
                TraceId = traceId,
                Role = role,
                EstimatedInputTokens = estimatedInputTokens,
                MaximumOutputTokens = maximumOutputTokens,
                EstimatedCostUsd = estimatedCost,
                Turn = turn
            };
            _reservations.Add(id, reservation);
            _reservedCostUsd += estimatedCost;
            if (turn is not null)
            {
                turn.Calls++;
                turn.ReservedUsd += estimatedCost;
            }
            return reservation;
        }
    }

    internal decimal Complete(Reservation reservation, LlmTokenUsage usage)
        => Close(reservation, usage, success: true, errorCode: null);

    internal decimal Fail(Reservation reservation, string? errorCode)
        => Close(
            reservation,
            new LlmTokenUsage(
                reservation.EstimatedInputTokens,
                reservation.MaximumOutputTokens),
            success: false,
            errorCode);

    internal LlmBudgetSnapshot GetSnapshot()
    {
        lock (_gate)
            return Snapshot(_currentTurn.Value);
    }

    internal static decimal CalculateCost(
        LlmTokenUsage usage,
        LlmPricingMetadata pricing)
    {
        var input = Math.Max(0, usage.InputTokens ?? 0);
        var cached = Math.Clamp(usage.CachedInputTokens ?? 0, 0, input);
        var cacheWrite = Math.Clamp(
            usage.CacheWriteTokens ?? 0,
            0,
            Math.Max(0, input - cached));
        var uncached = Math.Max(0, input - cached - cacheWrite);
        var output = Math.Max(0, usage.OutputTokens ?? 0);
        const decimal million = 1_000_000m;
        return decimal.Round(
            (uncached * pricing.InputUsdPerMillionTokens
             + cached * pricing.CachedInputUsdPerMillionTokens
             + cacheWrite * pricing.CacheWriteUsdPerMillionTokens
             + output * pricing.OutputUsdPerMillionTokens) / million,
            8,
            MidpointRounding.AwayFromZero);
    }

    internal static int EstimateInputTokens(
        IReadOnlyList<(string role, string content)> messages)
        => Math.Max(
            1,
            (int)Math.Ceiling(messages.Sum(static message =>
                (message.role?.Length ?? 0) + (message.content?.Length ?? 0)) / 4d));

    internal static int EstimateInputTokens(
        IReadOnlyList<SourceBackedRag.SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedRag.SourceBackedAgentToolDefinition> tools)
    {
        var messageCharacters = messages.Sum(static message =>
            (message.Role?.Length ?? 0)
            + (message.Content?.Length ?? 0)
            + (message.Name?.Length ?? 0)
            + (message.ToolCallId?.Length ?? 0)
            + (message.ToolCalls?.Sum(static call =>
                call.Id.Length + call.Name.Length + call.Arguments.GetRawText().Length) ?? 0));
        var toolCharacters = tools.Sum(static tool =>
            tool.Name.Length + tool.Description.Length + tool.Parameters.GetRawText().Length);
        return Math.Max(1, (int)Math.Ceiling((messageCharacters + toolCharacters) / 4d));
    }

    private decimal Close(
        Reservation reservation,
        LlmTokenUsage usage,
        bool success,
        string? errorCode)
    {
        lock (_gate)
        {
            if (!_reservations.ContainsKey(reservation.Id))
                throw new InvalidOperationException("Unknown external LLM budget reservation.");

            var hasServerUsage = usage.InputTokens is >= 0 && usage.OutputTokens is >= 0;
            var billedUsage = hasServerUsage
                ? usage
                : new LlmTokenUsage(
                    reservation.EstimatedInputTokens,
                    reservation.MaximumOutputTokens);
            var cost = CalculateCost(billedUsage, _pricing);
            AppendLedger(new LedgerEntry(
                DateTimeOffset.UtcNow,
                reservation.RequestId,
                reservation.TraceId,
                _descriptor.Provider,
                _descriptor.ModelId,
                reservation.Role.ToString(),
                billedUsage.InputTokens,
                billedUsage.OutputTokens,
                billedUsage.CachedInputTokens,
                billedUsage.CacheWriteTokens,
                billedUsage.ReasoningTokens,
                cost,
                success,
                hasServerUsage ? "provider_usage" : "reserved_upper_bound",
                errorCode));

            _reservations.Remove(reservation.Id);
            _reservedCostUsd -= reservation.EstimatedCostUsd;
            _recordedCostUsd += cost;
            if (reservation.Turn is not null)
            {
                reservation.Turn.ReservedUsd -= reservation.EstimatedCostUsd;
                reservation.Turn.CostUsd += cost;
            }
            return cost;
        }
    }

    private LlmBudgetExceededException Exceeded(string reason, TurnState? turn)
        => new(reason, Snapshot(turn));

    private LlmBudgetSnapshot Snapshot(TurnState? turn)
        => new(
            _options.AuthorizedBudgetUsd,
            _options.SoftLimitUsd,
            _options.HardLimitUsd,
            _recordedCostUsd,
            _reservedCostUsd,
            Math.Max(0, _options.HardLimitUsd - _recordedCostUsd - _reservedCostUsd),
            _recordedCostUsd + _reservedCostUsd >= _options.SoftLimitUsd,
            turn?.Calls ?? 0,
            (turn?.CostUsd ?? 0) + (turn?.ReservedUsd ?? 0));

    private void AppendLedger(LedgerEntry entry)
    {
        var directory = Path.GetDirectoryName(_options.LedgerPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.AppendAllText(
            _options.LedgerPath,
            JsonSerializer.Serialize(entry, LedgerJson) + Environment.NewLine);
    }

    private static decimal ReadRecordedCost(string path)
    {
        if (!File.Exists(path))
            return 0;
        decimal total = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if ((document.RootElement.TryGetProperty("costUsd", out var value)
                     || document.RootElement.TryGetProperty("CostUsd", out value))
                    && value.TryGetDecimal(out var cost)
                    && cost >= 0)
                {
                    total += cost;
                }
            }
            catch (JsonException)
            {
                // A damaged historical line must not disable the local hard stop.
                throw new InvalidOperationException(
                    "The external LLM usage ledger is invalid and must be reviewed before paid calls resume.");
            }
        }
        return total;
    }

    private static LlmBudgetOptions Validate(LlmBudgetOptions options)
    {
        if (options.AuthorizedBudgetUsd <= 0
            || options.SoftLimitUsd <= 0
            || options.HardLimitUsd <= 0
            || options.SoftLimitUsd > options.HardLimitUsd
            || options.HardLimitUsd > options.AuthorizedBudgetUsd
            || options.MaximumCostPerTurnUsd <= 0
            || options.MaximumCostPerTurnUsd > options.HardLimitUsd
            || options.MaximumCallsPerTurn <= 0
            || string.IsNullOrWhiteSpace(options.LedgerPath))
        {
            throw new InvalidOperationException("External LLM budget configuration is invalid.");
        }
        return options;
    }
}
