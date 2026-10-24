using System.Collections.ObjectModel;
using System.Diagnostics;

namespace SAAIA.Client.Core.Diagnostics;

public sealed record ClientProbeEvent(string SchemaVersion, string TraceId, string TurnId,
    string? SessionId, long Sequence, DateTimeOffset AtUtc, long ElapsedMilliseconds,
    string Operation, string Outcome, string? Reason, IReadOnlyDictionary<string, long?> Measurements,
    string SpanId, string? ParentSpanId, IReadOnlyDictionary<string, string> Identifiers);

public sealed record TurnDiagnosticRecord(string SchemaVersion, string TraceId, string TurnId,
    string? SessionId, DateTimeOffset StartedAtUtc, long TotalMilliseconds, string Outcome,
    string? Reason, IReadOnlyDictionary<string, long?> Phases, int EventCount, int DroppedEvents,
    IReadOnlyList<ClientProbeEvent> Events, IReadOnlyDictionary<string, string> Configuration, string Operation,
    int OmittedMetadataFields, int TruncatedMetadataFields);

/// <summary>Bounded, content-free production diagnostics. Completed records never change.</summary>
public sealed class ClientDiagnosticJournal
{
    public const int MaximumMetadataFields = 32;
    public const int MaximumMeasurements = 48;
    public const int MaximumKeyCharacters = 64;
    public const int MaximumMetadataCharacters = 256;
    private readonly object _gate = new();
    private readonly Queue<TurnDiagnosticRecord> _completed = new();
    private readonly AsyncLocal<TurnState?> _current = new();
    private readonly int _capacity;
    private readonly int _eventCapacity;
    private long _collectorFailures;
    public Action<ClientProbeEvent>? EventSink { get; set; }
    public long CollectorFailures => Interlocked.Read(ref _collectorFailures);
    public void RecordCollectorFailure() => Interlocked.Increment(ref _collectorFailures);
    public string? CurrentTraceId => _current.Value?.TraceId;
    public string? CurrentTurnId => _current.Value?.TurnId;
    public string? CurrentSessionId => _current.Value?.SessionId;
    public string? CurrentTraceParent => _current.Value?.TraceParent;
    public string? ConfigurationValue(string key)
    {
        var state = _current.Value;
        if (state is null) return null;
        lock (state.Gate) return state.Configuration.TryGetValue(key, out var value) ? value : null;
    }

    public ClientDiagnosticJournal(int capacity = 64, int eventCapacity = 240)
    {
        _capacity = Math.Clamp(capacity, 1, 1_024);
        _eventCapacity = Math.Clamp(eventCapacity, 1, 4_096);
    }

    public TurnScope BeginTurn(string? sessionId = null, string operation = "chat_turn", bool independent = false)
    {
        if (!independent && _current.Value is { Finalized: false } current)
            return new TurnScope(this, current, ownsTurn: false);
        var previous = _current.Value;
        var previousActivity = Activity.Current;
        var activity = new Activity("saaia.client.turn").SetIdFormat(ActivityIdFormat.W3C);
        try { activity.Start(); }
        catch
        {
            Interlocked.Increment(ref _collectorFailures);
            try { Activity.Current = previousActivity; }
            catch { Interlocked.Increment(ref _collectorFailures); }
        }
        var state = new TurnState(activity, sessionId, operation, previous, previousActivity);
        state.SessionId = BoundValue(sessionId, 64, state);
        state.Operation = BoundToken(operation, "unknown_operation", state);
        _current.Value = state;
        Emit("turn.begin", "success");
        return new TurnScope(this, state, ownsTurn: true);
    }

    public IReadOnlyList<TurnDiagnosticRecord> Last(int lastN = 1, string? operation = null)
    {
        lock (_gate)
            return Array.AsReadOnly(_completed.Where(record => operation is null || record.Operation == operation)
                .TakeLast(Math.Clamp(lastN, 1, _capacity)).ToArray());
    }

    public void Phase(string phase, long milliseconds)
    {
        var state = _current.Value;
        if (state is null) return;
        lock (state.Gate)
        {
            if (state.Finalized) return;
            if (!ValidKey(phase) || (!state.Phases.ContainsKey(phase) && state.Phases.Count >= MaximumMetadataFields))
            { state.OmittedMetadata++; return; }
            state.Phases[phase] = milliseconds < 0 ? null : milliseconds;
            if (milliseconds < 0) state.OmittedMetadata++;
        }
    }

    public void Configuration(IReadOnlyDictionary<string, string> metadata)
    {
        var state = _current.Value;
        if (state is null) return;
        lock (state.Gate)
            if (!state.Finalized)
            {
                foreach (var entry in metadata.Take(MaximumMetadataFields))
                {
                    if (!ValidKey(entry.Key) || (!state.Configuration.ContainsKey(entry.Key) && state.Configuration.Count >= MaximumMetadataFields))
                    { state.OmittedMetadata++; continue; }
                    state.Configuration[entry.Key] = BoundValue(entry.Value, MaximumMetadataCharacters, state) ?? "unavailable";
                }
                state.OmittedMetadata += Math.Max(0, metadata.Count - MaximumMetadataFields);
            }
    }

    public void Emit(string operation, string outcome, string? reason = null,
        IReadOnlyDictionary<string, long?>? measurements = null,
        IReadOnlyDictionary<string, string>? identifiers = null)
    {
        var state = _current.Value;
        if (state is null) return;
        EmitForState(state, operation, outcome, reason, measurements, identifiers);
    }

    private void EmitForState(TurnState state, string operation, string outcome, string? reason = null,
        IReadOnlyDictionary<string, long?>? measurements = null,
        IReadOnlyDictionary<string, string>? identifiers = null)
    {
        ClientProbeEvent entry;
        lock (state.Gate)
        {
            if (state.Finalized) return;
            entry = new("saaia.client-probe.v1", state.TraceId, state.TurnId, state.SessionId,
                ++state.Sequence, DateTimeOffset.UtcNow, state.Clock.ElapsedMilliseconds,
                BoundToken(operation, "unknown_operation", state), NormalizeOutcome(outcome, state),
                reason is null ? null : BoundToken(reason, "unstructured_reason_omitted", state), FreezeMeasurements(measurements, state), state.SpanId,
                state.Activity.ParentSpanId == default ? null : state.Activity.ParentSpanId.ToHexString(),
                FreezeIdentifiers(identifiers, state));
            if (state.Events.Count == _eventCapacity) { state.Events.Dequeue(); state.Dropped++; }
            state.Events.Enqueue(entry);
        }
        try { EventSink?.Invoke(entry); }
        catch { Interlocked.Increment(ref _collectorFailures); }
    }

    private void Finish(TurnState state, string outcome, string? reason)
    {
        EmitForState(state, "turn.end", outcome, reason);
        lock (state.Gate)
        {
            if (state.Finalized) return;
            state.Finalized = true;
            state.Clock.Stop();
            var record = new TurnDiagnosticRecord("saaia.client-turn.v1", state.TraceId,
                state.TurnId, state.SessionId, state.StartedAtUtc, state.Clock.ElapsedMilliseconds,
                NormalizeOutcome(outcome, state), reason is null ? null : BoundToken(reason, "unstructured_reason_omitted", state),
                Freeze(state.Phases), state.Sequence, state.Dropped,
                Array.AsReadOnly(state.Events.ToArray()), new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(state.Configuration)), state.Operation,
                state.OmittedMetadata, state.TruncatedMetadata);
            lock (_gate)
            {
                if (_completed.Count == _capacity) _completed.Dequeue();
                _completed.Enqueue(record);
            }
        }
        try { state.Activity.Stop(); }
        catch { Interlocked.Increment(ref _collectorFailures); }
        finally
        {
            if (ReferenceEquals(Activity.Current, state.Activity) || Activity.Current?.IsStopped == true)
            {
                var restoreActivity = state.PreviousActivity;
                while (restoreActivity?.IsStopped == true) restoreActivity = restoreActivity.Parent;
                try { Activity.Current = restoreActivity; }
                catch { Interlocked.Increment(ref _collectorFailures); }
            }
            if (ReferenceEquals(_current.Value, state))
                _current.Value = state.Parent is { Finalized: false } parent ? parent : null;
        }
    }

    private static IReadOnlyDictionary<string, long?> Freeze(IReadOnlyDictionary<string, long?>? values)
        => new ReadOnlyDictionary<string, long?>(values is null ? new() : values.ToDictionary(x => x.Key, x => x.Value));

    private static IReadOnlyDictionary<string, long?> FreezeMeasurements(IReadOnlyDictionary<string, long?>? values, TurnState state)
    {
        var result = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var entry in (values ?? new Dictionary<string, long?>()).Take(MaximumMeasurements))
        {
            if (!ValidKey(entry.Key) || result.Count >= MaximumMeasurements) { state.OmittedMetadata++; continue; }
            result[entry.Key] = entry.Value;
        }
        state.OmittedMetadata += Math.Max(0, (values?.Count ?? 0) - MaximumMeasurements);
        return new ReadOnlyDictionary<string, long?>(result);
    }

    private static IReadOnlyDictionary<string, string> FreezeIdentifiers(IReadOnlyDictionary<string, string>? values, TurnState state)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in (values ?? new Dictionary<string, string>()).Take(MaximumMetadataFields))
        {
            if (!ValidKey(entry.Key) || result.Count >= MaximumMetadataFields) { state.OmittedMetadata++; continue; }
            result[entry.Key] = BoundValue(entry.Value, MaximumMetadataCharacters, state) ?? "unavailable";
        }
        state.OmittedMetadata += Math.Max(0, (values?.Count ?? 0) - MaximumMetadataFields);
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static bool ValidKey(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumKeyCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
        && value.ToLowerInvariant() is not ("apikey" or "api_key" or "password" or "secret" or "authorization" or "bearertoken" or "hftoken"
            or "prompt" or "query" or "question" or "answertext" or "documentpath" or "docpath" or "content");

    private static string BoundToken(string? value, string fallback, TurnState state)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'))
        { state.OmittedMetadata++; return fallback; }
        return BoundValue(value, 160, state)!;
    }

    private static string? BoundValue(string? value, int maximum, TurnState state)
    {
        if (value is null) return null;
        if (value.Length <= maximum) return value;
        state.TruncatedMetadata++;
        return value[..maximum];
    }

    private static string NormalizeOutcome(string? outcome, TurnState state)
    {
        if (outcome is "success" or "failed" or "cancelled" or "degraded" or "skipped" or "unavailable") return outcome;
        if (outcome is "canceled") return "cancelled";
        if (outcome is "succeeded") return "success";
        state.OmittedMetadata++;
        return "unavailable";
    }

    internal sealed class TurnState(Activity activity, string? sessionId, string operation, TurnState? parent, Activity? previousActivity)
    {
        internal readonly object Gate = new();
        internal readonly Activity Activity = activity;
        internal readonly string TraceId = (activity.TraceId == default ? ActivityTraceId.CreateRandom() : activity.TraceId).ToHexString();
        internal readonly string SpanId = (activity.SpanId == default ? ActivitySpanId.CreateRandom() : activity.SpanId).ToHexString();
        internal string TraceParent => Activity.Id ?? $"00-{TraceId}-{SpanId}-00";
        internal readonly string TurnId = Guid.NewGuid().ToString("N");
        internal string? SessionId = sessionId;
        internal string Operation = operation;
        internal readonly TurnState? Parent = parent;
        internal readonly Activity? PreviousActivity = previousActivity;
        internal readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly Dictionary<string, long?> Phases = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Configuration = new(StringComparer.Ordinal);
        internal readonly Queue<ClientProbeEvent> Events = new();
        internal int Sequence;
        internal int Dropped;
        internal int OmittedMetadata;
        internal int TruncatedMetadata;
        internal volatile bool Finalized;
    }

    public sealed class TurnScope : IDisposable
    {
        private readonly ClientDiagnosticJournal _journal;
        private readonly TurnState _state;
        private readonly bool _ownsTurn;
        private string _outcome = "failed";
        private string? _reason = "scope_exited_without_completion";
        private int _disposed;
        internal TurnScope(ClientDiagnosticJournal journal, TurnState state, bool ownsTurn)
            => (_journal, _state, _ownsTurn) = (journal, state, ownsTurn);
        public string TraceId => _state.TraceId;
        public string TurnId => _state.TurnId;
        // Async callbacks target their owner explicitly, even when delivered outside its execution context.
        public void Emit(string operation, string outcome, string? reason = null,
            IReadOnlyDictionary<string, long?>? measurements = null,
            IReadOnlyDictionary<string, string>? identifiers = null)
            => _journal.EmitForState(_state, operation, outcome, reason, measurements, identifiers);
        public void Complete(string outcome = "success", string? reason = null)
            => (_outcome, _reason) = (outcome, reason);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsTurn)
                _journal.Finish(_state, _outcome, _reason);
        }
    }
}
