using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Models;

public sealed class ChatTrackingMeta
{
    public string? Kind { get; set; }
    public string? JobId { get; set; }
    public string? JobType { get; set; }
    public string? DisplayLabel { get; set; }
    public string? DocId { get; set; }
    public string? DocPath { get; set; }
    public bool IsTerminal { get; set; }

    // Durable progress snapshot used to restore long-running admin job tracking
    // after session reloads or application restarts.
    public string? LastKnownStatus { get; set; }
    public string? LastKnownProgressPhase { get; set; }
    public int? LastKnownProgressCurrent { get; set; }
    public int? LastKnownProgressTotal { get; set; }
    public int? LastKnownProgressPercent { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? LastSnapshotAtUtc { get; set; }
}

public sealed class ChatMessageItem : INotifyPropertyChanged
{
    private string _role = "user";
    private string _content = "";
    private string? _sourcesJson;
    private DateTime _createdAt = DateTime.UtcNow;
    private string? _messageId;
    private string? _statusNote;
    private string? _progressText;
    private bool _isStreaming;
    private ChatTrackingMeta? _trackingMeta;

    // UI-only smoothing state for progress/status updates.
    public DateTime ProgressLastUpdatedUtc { get; set; } = DateTime.MinValue;

    public string Role
    {
        get => _role;
        set { if (_role != value) { _role = value; OnPropertyChanged(); } }
    }

    public string Content
    {
        get => _content;
        set { if (_content != value) { _content = value; OnPropertyChanged(); } }
    }

    public string? SourcesJson
    {
        get => _sourcesJson;
        set
        {
            if (_sourcesJson != value)
            {
                _sourcesJson = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParsedSources));
            }
        }
    }

    public IList<SourceCard> ParsedSources => SourceCardParser.Parse(_sourcesJson);

    public DateTime CreatedAt
    {
        get => _createdAt;
        set { if (_createdAt != value) { _createdAt = value; OnPropertyChanged(); } }
    }

    public string? MessageId
    {
        get => _messageId;
        set { if (_messageId != value) { _messageId = value; OnPropertyChanged(); } }
    }

    public string? StatusNote
    {
        get => _statusNote;
        set
        {
            if (_statusNote != value)
            {
                _statusNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusNoteSet));
                OnPropertyChanged(nameof(StatusNoteEmpty));
                OnPropertyChanged(nameof(StatusNoteVisibleWhenIdle));
            }
        }
    }

    public string? ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressTextVisibleWhenIdle));
                OnPropertyChanged(nameof(StreamingShowProgressText));
                OnPropertyChanged(nameof(StreamingShowDefault));
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming != value)
            {
                _isStreaming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusNoteVisibleWhenIdle));
                OnPropertyChanged(nameof(ProgressTextVisibleWhenIdle));
                OnPropertyChanged(nameof(StreamingShowProgressText));
                OnPropertyChanged(nameof(StreamingShowDefault));
            }
        }
    }

    // Streaming-row layout: only ONE label visible at a time. Priority order:
    //   1. StatusNote        (most specific)
    //   2. ProgressText      (next)
    //   3. "Génération en cours…" default
    public bool StatusNoteSet => !string.IsNullOrWhiteSpace(_statusNote);
    public bool StatusNoteEmpty => string.IsNullOrWhiteSpace(_statusNote);
    public bool StreamingShowProgressText
        => string.IsNullOrWhiteSpace(_statusNote) && !string.IsNullOrWhiteSpace(_progressText);
    public bool StreamingShowDefault
        => string.IsNullOrWhiteSpace(_statusNote) && string.IsNullOrWhiteSpace(_progressText);

    // The standalone ProgressText spinner (above the bubble) is only shown when NOT
    // streaming. While streaming, the row inside the bubble already covers it — without
    // this guard the user sees TWO spinners stacked ("J'interprète…" + "Génération…").
    public bool ProgressTextVisibleWhenIdle
        => !_isStreaming && !string.IsNullOrWhiteSpace(_progressText);

    // Idle row: render StatusNote (e.g. "Génération interrompue.") only after streaming.
    public bool StatusNoteVisibleWhenIdle
        => !_isStreaming && !string.IsNullOrWhiteSpace(_statusNote);

    public ChatTrackingMeta? TrackingMeta
    {
        get => _trackingMeta;
        set { if (!ReferenceEquals(_trackingMeta, value)) { _trackingMeta = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
