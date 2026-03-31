using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
        set { if (_sourcesJson != value) { _sourcesJson = value; OnPropertyChanged(); } }
    }

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
        set { if (_statusNote != value) { _statusNote = value; OnPropertyChanged(); } }
    }

    public string? ProgressText
    {
        get => _progressText;
        set { if (_progressText != value) { _progressText = value; OnPropertyChanged(); } }
    }

    public ChatTrackingMeta? TrackingMeta
    {
        get => _trackingMeta;
        set { if (!ReferenceEquals(_trackingMeta, value)) { _trackingMeta = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
