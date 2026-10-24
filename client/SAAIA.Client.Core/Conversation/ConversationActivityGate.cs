namespace SAAIA.Client.Core.Conversation;

/// <summary>
/// Owns admission to one conversation surface. Cancellation requests do not release an
/// active turn: its owner must finish before memory can be changed for another session.
/// </summary>
public sealed class ConversationActivityGate
{
    private readonly object _gate = new();
    private ActivityLease? _active;

    public bool IsChangingSession { get { lock (_gate) return _active?.Kind == ConversationActivityKind.SessionChange; } }
    public bool IsRunningTurn { get { lock (_gate) return _active?.Kind == ConversationActivityKind.Turn; } }
    public bool IsTurnCancellationRequested { get { lock (_gate) return _active?.CancellationToken.IsCancellationRequested == true; } }

    public void CancelActiveTurn()
    {
        ActivityLease? turn;
        lock (_gate) turn = _active?.Kind == ConversationActivityKind.Turn ? _active : null;
        turn?.Cancel();
    }

    public ActivityLease? TryBeginSessionChange(string? sessionId = null)
        => TryBegin(ConversationActivityKind.SessionChange, sessionId);

    public ActivityLease? TryBeginTurn(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return TryBegin(ConversationActivityKind.Turn, sessionId);
    }

    private ActivityLease? TryBegin(ConversationActivityKind kind, string? sessionId)
    {
        lock (_gate)
        {
            if (_active is not null) return null;
            return _active = new ActivityLease(this, kind, sessionId);
        }
    }

    public sealed class ActivityLease : IDisposable
    {
        private readonly ConversationActivityGate _owner;
        private readonly CancellationTokenSource? _cancellation;
        private int _disposed;

        internal ActivityLease(ConversationActivityGate owner, ConversationActivityKind kind, string? sessionId)
        {
            _owner = owner; Kind = kind; SessionId = sessionId;
            if (kind == ConversationActivityKind.Turn)
            {
                _cancellation = new CancellationTokenSource();
                CancellationToken = _cancellation.Token;
            }
        }

        public ConversationActivityKind Kind { get; }
        public string? SessionId { get; }
        public CancellationToken CancellationToken { get; }
        public bool IsCurrent { get { lock (_owner._gate) return ReferenceEquals(_owner._active, this); } }

        internal void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _cancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* A finishing owner already released this source. */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_owner._gate)
                if (ReferenceEquals(_owner._active, this)) _owner._active = null;
            _cancellation?.Dispose();
        }
    }
}

public enum ConversationActivityKind { SessionChange, Turn }
