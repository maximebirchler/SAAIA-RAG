using System;
using System.Linq;
using Microsoft.UI.Text;
using SAAIA.Client.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SAAIA.Client.WinUI.Controls;

internal sealed partial class UserSettingsDialog
{
    // Theme changes are applied after validation to avoid mixed-theme preview glitches in WinUI dialogs.

    private static void TryApplyStyle(Control control, string resourceKey)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true && value is Style style)
                control.Style = style;
        }
        catch
        {
        }
    }

    private void PopulateAppearanceOptions()
    {
        var selected = AppSettings.NormalizeUiTheme(_working.UiTheme);
        if (_appearance.SelectedItem is UiThemeChoice current)
            selected = current.Code;

        _appearance.Items.Clear();
        _appearance.Items.Add(new UiThemeChoice("dark", T("settings.theme.dark")));
        _appearance.Items.Add(new UiThemeChoice("light", T("settings.theme.light")));
        _appearance.Items.Add(new UiThemeChoice("system", T("settings.theme.system")));

        var index = _appearance.Items
            .OfType<UiThemeChoice>()
            .ToList()
            .FindIndex(x => string.Equals(x.Code, selected, StringComparison.OrdinalIgnoreCase));

        _appearance.SelectedIndex = index >= 0 ? index : 0;
    }

    private void ResetComboItems(ComboBox combo, params string[] items)
    {
        var selectedIndex = combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex;
        combo.Items.Clear();
        foreach (var item in items)
            combo.Items.Add(item);
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Length - 1));
    }

    private void RefreshUiTexts()
    {
        Title = string.Empty;
        PrimaryButtonText = string.Empty;
        SecondaryButtonText = string.Empty;
        CloseButtonText = string.Empty;
        _applyButton.Content = T("settings.apply");
        _closeButton.Content = ClientUiText.Get("dialog.close", UiLang);

        _heroTitle.Text = T("settings.title");
        _heroTitle.FontSize = 22;
        _heroTitle.FontWeight = FontWeights.SemiBold;
        _heroSubtitle.Text = T("settings.subtitle");
        _advancedSubtitle.Text = T("settings.advanced.subtitle");
        _interfaceNote.Text = T("settings.interface.note");
        _supportNote.Text = T("settings.support.note");

        _generalTabButton.Content = T("settings.tab.general");
        _advancedTabButton.Content = T("settings.tab.advanced");
        ApplyTabButtonStyle(_generalTabButton, !_showAdvanced);
        ApplyTabButtonStyle(_advancedTabButton, _showAdvanced);

        _interfaceSectionTitle.Text = T("settings.section.interface");
        _assistantSectionTitle.Text = T("settings.section.assistant");
        _behaviorSectionTitle.Text = T("settings.section.behavior");
        _repairSectionTitle.Text = T("settings.section.repair");
        _supportSectionTitle.Text = T("settings.section.support");

        _uiLanguage.Header = T("settings.language");
        _appearance.Header = T("settings.appearance");
        _assistantEnabled.Header = T("settings.toggle.assistant");
        _assistantEnabled.OnContent = T("settings.toggle.assistant.on");
        _assistantEnabled.OffContent = T("settings.toggle.assistant.off");
        _strictMode.Header = T("settings.toggle.strict");
        _strictMode.OnContent = T("settings.toggle.strict.on");
        _strictMode.OffContent = T("settings.toggle.strict.off");
        _ragQuality.Header = T("settings.rag_quality");
        _style.Header = T("settings.style");
        _length.Header = T("settings.length");

        PopulateAppearanceOptions();
        ResetComboItems(_ragQuality, T("settings.choice.quick"), T("settings.choice.balanced"), T("settings.choice.deep"));
        ResetComboItems(_style, T("settings.choice.precise"), T("settings.choice.balanced"), T("settings.choice.creative"));
        ResetComboItems(_length, T("settings.choice.short"), T("settings.choice.standard"), T("settings.choice.long"));

        _assistantRepairBtn.Content = T("settings.repair.button");
        _exportBtn.Content = T("settings.support.button");
    }

    private UIElement BuildUi()
    {
        var light = UseLightPalette();
        RequestedTheme = ToElementTheme(GetSelectedThemeCode());

        foreach (var combo in new[] { _uiLanguage, _appearance, _ragQuality, _style, _length })
        {
            combo.HorizontalAlignment = HorizontalAlignment.Stretch;
            TryApplyStyle(combo, "AppComboStyle");
        }

        _assistantEnabled.HorizontalAlignment = HorizontalAlignment.Stretch;
        _strictMode.HorizontalAlignment = HorizontalAlignment.Stretch;
        _assistantRepairBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _assistantRepairBtn.Padding = new Thickness(16, 10, 16, 10);
        _exportBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _exportBtn.Padding = new Thickness(16, 10, 16, 10);
        _assistantStatus.Opacity = 0.92;
        _layoutScroller.Padding = new Thickness(0);

        TryApplyStyle(_assistantRepairBtn, "AppSecondaryButtonStyle");
        TryApplyStyle(_exportBtn, "AppSecondaryButtonStyle");
        TryApplyStyle(_applyButton, "AppPrimaryButtonStyle");
        TryApplyStyle(_closeButton, "AppSecondaryButtonStyle");
        _applyButton.MinWidth = 148;
        _closeButton.MinWidth = 148;
        _applyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _closeButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        var root = new StackPanel { Spacing = 14, MaxWidth = 760 };

        var hero = new Border
        {
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(20, 18, 20, 18),
            Background = light ? Brush(0xE7, 0xED, 0xF4) : Brush(0x14, 0x1A, 0x22),
            BorderBrush = light ? Brush(0xBE, 0xCB, 0xD9) : Brush(0x2A, 0x33, 0x3D),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { _heroTitle, _heroSubtitle }
            }
        };

        var tabsHost = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(6),
            Background = light ? Brush(0xEC, 0xF1, 0xF6) : Brush(0x12, 0x16, 0x1C),
            BorderBrush = light ? Brush(0xB8, 0xC5, 0xD3) : Brush(0x35, 0x38, 0x40),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children =
                {
                    _generalTabButton,
                    CreateTabbedButtonHost(_advancedTabButton, 1)
                }
            }
        };

        var interfaceCard = SectionCard(_interfaceSectionTitle, new UIElement[]
        {
            _uiLanguage,
            _appearance,
            _interfaceNote
        });

        var assistantCard = SectionCard(_assistantSectionTitle, new UIElement[]
        {
            _assistantEnabled,
            _ragQuality,
            _style,
            _length
        });

        var behaviorCard = SectionCard(_behaviorSectionTitle, new UIElement[]
        {
            _strictMode
        });

        _generalContent.Children.Clear();
        _generalContent.Children.Add(interfaceCard);
        _generalContent.Children.Add(assistantCard);
        _generalContent.Children.Add(behaviorCard);

        var advancedIntro = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 16, 18, 16),
            Background = light ? Brush(0xEC, 0xF1, 0xF6) : Brush(0x12, 0x16, 0x1C),
            BorderBrush = light ? Brush(0xB8, 0xC5, 0xD3) : Brush(0x35, 0x38, 0x40),
            BorderThickness = new Thickness(1),
            Child = _advancedSubtitle
        };

        var repairCard = SectionCard(_repairSectionTitle, new UIElement[]
        {
            _assistantRepairBtn,
            _assistantProgress,
            _assistantStatus
        });

        var supportCard = SectionCard(_supportSectionTitle, new UIElement[]
        {
            _exportBtn,
            _supportNote
        });

        _advancedContent.Children.Clear();
        _advancedContent.Children.Add(advancedIntro);
        _advancedContent.Children.Add(repairCard);
        _advancedContent.Children.Add(supportCard);

        var footer = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_applyButton, 0);
        Grid.SetColumn(_closeButton, 1);
        footer.Children.Add(_applyButton);
        footer.Children.Add(_closeButton);

        root.Children.Add(hero);
        root.Children.Add(tabsHost);
        root.Children.Add(_generalContent);
        root.Children.Add(_advancedContent);

        _layoutScroller.Content = root;

        var shellLayout = new Grid();
        shellLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shellLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_layoutScroller, 0);
        Grid.SetRow(footer, 1);
        shellLayout.Children.Add(_layoutScroller);
        shellLayout.Children.Add(footer);

        _shellHost.CornerRadius = new CornerRadius(24);
        _shellHost.MaxWidth = 760;
        _shellHost.Padding = new Thickness(20, 18, 20, 18);
        _shellHost.HorizontalAlignment = HorizontalAlignment.Center;
        _shellHost.VerticalAlignment = VerticalAlignment.Center;
        _shellHost.Background = light ? Brush(0xEC, 0xF1, 0xF6) : Brush(0x14, 0x19, 0x21);
        _shellHost.BorderBrush = light ? Brush(0xB8, 0xC5, 0xD3) : Brush(0x2E, 0x36, 0x42);
        _shellHost.BorderThickness = new Thickness(1);
        _shellHost.Child = shellLayout;

        SetSettingsView(showAdvanced: _showAdvanced);
        return _shellHost;
    }

    private static UIElement CreateTabbedButtonHost(Button button, int column)
    {
        Grid.SetColumn(button, column);
        return button;
    }

    private void SetSettingsView(bool showAdvanced)
    {
        _showAdvanced = showAdvanced;
        _generalContent.Visibility = showAdvanced ? Visibility.Collapsed : Visibility.Visible;
        _advancedContent.Visibility = showAdvanced ? Visibility.Visible : Visibility.Collapsed;
        ApplyTabButtonStyle(_generalTabButton, !showAdvanced);
        ApplyTabButtonStyle(_advancedTabButton, showAdvanced);
    }

    private void ApplyTabButtonStyle(Button button, bool isSelected)
    {
        var light = UseLightPalette();
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Padding = new Thickness(12, 10, 12, 10);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(14);
        button.Background = isSelected
            ? (light ? Brush(0x5E, 0x7A, 0x97) : Brush(0x3A, 0x84, 0xD8))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Foreground = isSelected
            ? Brush(0xFF, 0xFF, 0xFF)
            : (light ? Brush(0x46, 0x56, 0x6B) : Brush(0xD3, 0xDB, 0xE8));
        button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private Border SectionCard(TextBlock titleBlock, UIElement[] body)
    {
        var light = UseLightPalette();
        titleBlock.FontWeight = FontWeights.SemiBold;
        titleBlock.FontSize = 15;

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(titleBlock);
        foreach (var el in body)
            panel.Children.Add(el);

        return new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 16, 18, 16),
            Background = light ? Brush(0xEC, 0xF1, 0xF6) : Brush(0x10, 0x14, 0x1C),
            BorderBrush = light ? Brush(0xBE, 0xCB, 0xD9) : Brush(0x27, 0x27, 0x27),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }
}
