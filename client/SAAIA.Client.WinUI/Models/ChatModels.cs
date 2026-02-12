using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SAAIA.Client.WinUI.Models;

public sealed class ChatMessageItem : INotifyPropertyChanged
{
    private string _role = "user";
    private string _content = "";
    private string? _sourcesJson;
    private DateTime _createdAt = DateTime.UtcNow;

    // ✅ Petit label gris sous le message (ex: “Génération interrompue.”)
    private string? _statusNote;

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

    public string? StatusNote
    {
        get => _statusNote;
        set { if (_statusNote != value) { _statusNote = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
