using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SAAIA.Client.WinUI.Models;

public sealed class ChatSessionItem : INotifyPropertyChanged
{
    private string _sessionId = "";
    private string? _title;
    private string? _clientUser;
    private DateTime _createdAtUtc;
    private DateTime _updatedAtUtc;
    private DateTime? _lastMessageAtUtc;

    public string SessionId
    {
        get => _sessionId;
        set { if (_sessionId != value) { _sessionId = value; OnPropertyChanged(); } }
    }

    public string? Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string? ClientUser
    {
        get => _clientUser;
        set { if (_clientUser != value) { _clientUser = value; OnPropertyChanged(); } }
    }

    public DateTime CreatedAtUtc
    {
        get => _createdAtUtc;
        set
        {
            if (_createdAtUtc != value)
            {
                _createdAtUtc = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayWhen));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }

    public DateTime UpdatedAtUtc
    {
        get => _updatedAtUtc;
        set
        {
            if (_updatedAtUtc != value)
            {
                _updatedAtUtc = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayWhen));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }

    public DateTime? LastMessageAtUtc
    {
        get => _lastMessageAtUtc;
        set
        {
            if (_lastMessageAtUtc != value)
            {
                _lastMessageAtUtc = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayWhen));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "New chat" : Title!.Trim();

    public string DisplayWhen
    {
        get
        {
            var dt = LastMessageAtUtc ?? UpdatedAtUtc;
            if (dt == default) dt = CreatedAtUtc;
            if (dt == default) return "";

            try
            {
                var local = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                return local.ToString("dd.MM HH:mm");
            }
            catch
            {
                return "";
            }
        }
    }

    // ✅ Pour matcher ton XAML : DisplayTitle + DisplaySubtitle
    public string DisplaySubtitle => DisplayWhen;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
