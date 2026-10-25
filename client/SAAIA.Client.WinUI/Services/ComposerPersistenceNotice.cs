using System;

namespace SAAIA.Client.WinUI.Services;

/// <summary>Draft-scoped recovery feedback; a late response cannot warn another chat.</summary>
internal sealed class ComposerPersistenceNotice
{
    private string? _sessionId;
    private string? _draft;

    internal bool IsVisibleFor(string? sessionId) => _sessionId is not null
        && string.Equals(_sessionId, sessionId, StringComparison.OrdinalIgnoreCase);

    internal void ReportUnconfirmed(string failedSessionId, string? activeSessionId, string? draft)
    {
        if (!string.Equals(failedSessionId, activeSessionId, StringComparison.OrdinalIgnoreCase)) return;
        _sessionId = failedSessionId;
        _draft = Normalize(draft);
    }

    internal void ObserveDraft(string? sessionId, string? draft)
    {
        if (!IsVisibleFor(sessionId) || !string.Equals(_draft, Normalize(draft), StringComparison.Ordinal))
            Clear();
    }

    internal void ObserveSession(string? sessionId)
    {
        if (!IsVisibleFor(sessionId)) Clear();
    }

    internal void Confirmed(string sessionId)
    {
        if (IsVisibleFor(sessionId)) Clear();
    }

    private void Clear() { _sessionId = null; _draft = null; }
    private static string Normalize(string? text) => (text ?? string.Empty).Trim();
}
