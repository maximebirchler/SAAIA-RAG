namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed record SourceBackedLlmBudgetAdmission(
    bool Admitted,
    string Reason,
    long ReservationId,
    int ReservedTokens,
    long RemainingMilliseconds,
    bool Terminal);

internal sealed record SourceBackedLlmBudgetSnapshot(
    int MaximumTokens,
    long MaximumElapsedMilliseconds,
    int TerminalReserveTokens,
    long TerminalReserveMilliseconds,
    int ChargedTokens,
    int ReservedTokens,
    int RemainingTokens,
    long RemainingMilliseconds,
    bool NormalBudgetClosed,
    int AdmittedCalls,
    int CompletedCalls,
    int FailedCalls,
    string LastUsageSource,
    string LastAdmissionReason,
    string LastCallClass);

internal sealed class SourceBackedLlmBudgetExceededException(
    string reason,
    SourceBackedLlmBudgetSnapshot snapshot)
    : InvalidOperationException(
        "Source-backed cumulative LLM budget stopped the request: " + reason)
{
    public string Reason { get; } = reason;

    public SourceBackedLlmBudgetSnapshot Snapshot { get; } = snapshot;
}

internal sealed class SourceBackedLlmCumulativeBudget
{
    private const string SharedScopeContract =
        "same_scope_writer_review_repair";

    private sealed record Reservation(
        long Id,
        int Tokens,
        bool Terminal);

    private readonly object _gate = new();
    private readonly int _maximumTokens;
    private readonly long _maximumElapsedMilliseconds;
    private readonly int _terminalReserveTokens;
    private readonly long _terminalReserveMilliseconds;
    private readonly Func<long> _elapsedMilliseconds;
    private readonly Dictionary<long, Reservation> _reservations = new();
    private long _nextReservationId;
    private int _chargedTokens;
    private int _admittedCalls;
    private int _completedCalls;
    private int _failedCalls;
    private string _lastUsageSource = "none";
    private string _lastAdmissionReason = "none";
    private string _lastCallClass = "none";

    internal SourceBackedLlmCumulativeBudget(
        int maximumTokens,
        long maximumElapsedMilliseconds,
        int terminalReserveTokens,
        long terminalReserveMilliseconds,
        Func<long> elapsedMilliseconds)
    {
        if (maximumTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        if (maximumElapsedMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumElapsedMilliseconds));
        }
        if (terminalReserveTokens < 0
            || terminalReserveTokens > maximumTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalReserveTokens));
        }
        if (terminalReserveMilliseconds < 0
            || terminalReserveMilliseconds > maximumElapsedMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalReserveMilliseconds));
        }

        _maximumTokens = maximumTokens;
        _maximumElapsedMilliseconds = maximumElapsedMilliseconds;
        _terminalReserveTokens = terminalReserveTokens;
        _terminalReserveMilliseconds = terminalReserveMilliseconds;
        _elapsedMilliseconds = elapsedMilliseconds
                               ?? throw new ArgumentNullException(
                                   nameof(elapsedMilliseconds));
    }

    internal SourceBackedLlmBudgetAdmission TryReserve(
        int inputTokens,
        int maximumOutputTokens,
        bool terminal)
    {
        if (inputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        if (maximumOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutputTokens));
        }

        var requestedTokens = checked(inputTokens + maximumOutputTokens);
        lock (_gate)
        {
            var elapsed = ReadElapsedMilliseconds();
            var remainingMilliseconds = Math.Max(
                0,
                _maximumElapsedMilliseconds - elapsed);
            if (remainingMilliseconds <= 0)
            {
                return Reject(
                    "time_budget_exhausted",
                    requestedTokens,
                    remainingMilliseconds,
                    terminal);
            }
            if (!terminal
                && remainingMilliseconds <= _terminalReserveMilliseconds)
            {
                return Reject(
                    "terminal_budget_only",
                    requestedTokens,
                    remainingMilliseconds,
                    terminal);
            }

            var occupiedTokens = checked(
                _chargedTokens + ReservedTokenCountUnsafe());
            var admissionLimit = terminal
                ? _maximumTokens
                : _maximumTokens - _terminalReserveTokens;
            if ((long)occupiedTokens + requestedTokens > admissionLimit)
            {
                var reason = !terminal
                             && (long)occupiedTokens + requestedTokens
                             <= _maximumTokens
                    ? "terminal_budget_only"
                    : "token_budget_exhausted";
                return Reject(
                    reason,
                    requestedTokens,
                    remainingMilliseconds,
                    terminal);
            }

            var reservationId = ++_nextReservationId;
            _reservations.Add(
                reservationId,
                new Reservation(
                    reservationId,
                    requestedTokens,
                    terminal));
            _admittedCalls++;
            _lastAdmissionReason = "admitted";
            return new SourceBackedLlmBudgetAdmission(
                true,
                "admitted",
                reservationId,
                requestedTokens,
                remainingMilliseconds,
                terminal);
        }
    }

    internal void Complete(
        long reservationId,
        int? promptTokens,
        int? completionTokens)
    {
        lock (_gate)
        {
            if (!_reservations.Remove(reservationId, out var reservation))
                throw new InvalidOperationException("Unknown LLM budget reservation.");

            var hasValidServerUsage = promptTokens is >= 0
                                      && completionTokens is >= 0;
            var serverUsage = hasValidServerUsage
                ? (long)promptTokens!.Value + completionTokens!.Value
                : -1;
            var charged = hasValidServerUsage
                ? (int)Math.Min(int.MaxValue, serverUsage)
                : reservation.Tokens;

            _chargedTokens = (int)Math.Min(
                int.MaxValue,
                (long)_chargedTokens + charged);
            _completedCalls++;
            _lastUsageSource = hasValidServerUsage
                ? serverUsage <= reservation.Tokens
                    ? "server_usage"
                    : "server_usage_inconsistent"
                : "exact_input_plus_reserved_output";
        }
    }

    internal void Fail(long reservationId)
    {
        lock (_gate)
        {
            if (!_reservations.Remove(reservationId, out var reservation))
                return;

            _chargedTokens = (int)Math.Min(
                int.MaxValue,
                (long)_chargedTokens + reservation.Tokens);
            _failedCalls++;
            _lastUsageSource = "exact_input_plus_reserved_output";
        }
    }

    internal SourceBackedLlmBudgetSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var reservedTokens = ReservedTokenCountUnsafe();
            var occupiedTokens = (int)Math.Min(
                int.MaxValue,
                (long)_chargedTokens + reservedTokens);
            var remainingMilliseconds = Math.Max(
                0,
                _maximumElapsedMilliseconds - ReadElapsedMilliseconds());
            return new SourceBackedLlmBudgetSnapshot(
                _maximumTokens,
                _maximumElapsedMilliseconds,
                _terminalReserveTokens,
                _terminalReserveMilliseconds,
                _chargedTokens,
                reservedTokens,
                Math.Max(0, _maximumTokens - occupiedTokens),
                remainingMilliseconds,
                occupiedTokens >= _maximumTokens - _terminalReserveTokens
                || remainingMilliseconds <= _terminalReserveMilliseconds,
                _admittedCalls,
                _completedCalls,
                _failedCalls,
                _lastUsageSource,
                _lastAdmissionReason,
                _lastCallClass);
        }
    }

    internal void SetLastCallClass(string callClass)
    {
        lock (_gate)
        {
            _lastCallClass = string.IsNullOrWhiteSpace(callClass)
                ? "unknown"
                : callClass;
        }
    }

    internal static string TraceContract =>
        "maximum_tokens charged_tokens reserved_tokens remaining_tokens "
        + "remaining_ms admission_reason call_class "
        + SharedScopeContract;

    private SourceBackedLlmBudgetAdmission Reject(
        string reason,
        int requestedTokens,
        long remainingMilliseconds,
        bool terminal)
    {
        _lastAdmissionReason = reason;
        return new SourceBackedLlmBudgetAdmission(
            false,
            reason,
            0,
            requestedTokens,
            remainingMilliseconds,
            terminal);
    }

    private int ReservedTokenCountUnsafe()
        => (int)Math.Min(
            int.MaxValue,
            _reservations.Values.Sum(static reservation =>
                (long)reservation.Tokens));

    private long ReadElapsedMilliseconds()
    {
        try
        {
            return Math.Max(0, _elapsedMilliseconds());
        }
        catch
        {
            return _maximumElapsedMilliseconds;
        }
    }
}
