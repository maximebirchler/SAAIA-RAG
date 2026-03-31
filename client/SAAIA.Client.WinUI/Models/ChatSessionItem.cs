using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SAAIA.Client.WinUI.Models;

public sealed class ChatSessionItem : INotifyPropertyChanged
{
    private string _sessionId = "";
    private string? _title;
    private string? _clientUser;
    private DateTime _createdAtUtc;
    private DateTime _updatedAtUtc;
    private DateTime? _lastMessageAtUtc;
    private bool _isCurrent;
    private Brush _cardBackgroundBrush = TransparentBrush();
    private Brush _cardBorderBrush = TransparentBrush();
    private Thickness _cardBorderThickness = new(1);
    private double _selectionAccentOpacity;

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

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;

            _isCurrent = value;
            OnPropertyChanged();
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

    public string DisplaySubtitle => DisplayWhen;

    public Brush CardBackgroundBrush
    {
        get => _cardBackgroundBrush;
        private set => SetField(ref _cardBackgroundBrush, value);
    }

    public Brush CardBorderBrush
    {
        get => _cardBorderBrush;
        private set => SetField(ref _cardBorderBrush, value);
    }

    public Thickness CardBorderThickness
    {
        get => _cardBorderThickness;
        private set => SetField(ref _cardBorderThickness, value);
    }

    public double SelectionAccentOpacity
    {
        get => _selectionAccentOpacity;
        private set => SetField(ref _selectionAccentOpacity, value);
    }

    public void ApplySelectionVisualState(bool isCurrent, bool useLightPalette)
    {
        IsCurrent = isCurrent;
        CardBackgroundBrush = MakeBrush(isCurrent
            ? (useLightPalette ? ((byte)0xD6, (byte)0xE3, (byte)0xEF, (byte)0xFF) : ((byte)0x1B, (byte)0x31, (byte)0x4A, (byte)0xFF))
            : (useLightPalette ? ((byte)0xEA, (byte)0xF0, (byte)0xF6, (byte)0xFF) : ((byte)0x1A, (byte)0x22, (byte)0x2D, (byte)0xFF)));
        CardBorderBrush = MakeBrush(isCurrent
            ? (useLightPalette ? ((byte)0x58, (byte)0x72, (byte)0x8C, (byte)0xFF) : ((byte)0x3A, (byte)0x84, (byte)0xD8, (byte)0xFF))
            : (useLightPalette ? ((byte)0xB8, (byte)0xC4, (byte)0xD0, (byte)0xFF) : ((byte)0x24, (byte)0x30, (byte)0x3C, (byte)0xFF)));
        CardBorderThickness = isCurrent ? new Thickness(2) : new Thickness(1);
        SelectionAccentOpacity = isCurrent ? 1d : 0d;
    }

    public void RefreshSelectionVisualProperties(bool useLightPalette)
        => ApplySelectionVisualState(IsCurrent, useLightPalette);

    public event PropertyChangedEventHandler? PropertyChanged;

    private static SolidColorBrush MakeBrush((byte r, byte g, byte b, byte a) c)
        => new(Microsoft.UI.ColorHelper.FromArgb(c.a, c.r, c.g, c.b));

    private static SolidColorBrush TransparentBrush()
        => new(Microsoft.UI.Colors.Transparent);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
