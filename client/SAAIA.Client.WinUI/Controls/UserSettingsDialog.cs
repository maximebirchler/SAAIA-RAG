using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

/// <summary>
/// User-safe settings dialog (no dangerous configuration).
/// Built in C# to avoid WinUI XamlCompiler fragility.
/// </summary>
internal sealed class UserSettingsDialog : ContentDialog
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;

    private readonly Func<Task>? _repairAssistantAsync;

    private readonly DownloadManager _downloads = new();

    // Busy state (prevents double actions + blocks Apply/Close while running)
    private bool _busy;
    private string? _closeButtonTextBackup;


    private sealed record UiLanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    // UI controls (safe)
    private readonly ComboBox _uiLanguage = new();
    private readonly ToggleSwitch _assistantEnabled = new();
    private readonly ToggleSwitch _strictMode = new();
    private readonly ComboBox _ragQuality = new();
    private readonly ComboBox _style = new();
    private readonly ComboBox _length = new();

    private readonly TextBlock _assistantSectionTitle = new();
    private readonly TextBlock _repairSectionTitle = new();
    private readonly TextBlock _supportSectionTitle = new();
    private readonly TextBlock _interfaceSectionTitle = new();
    private readonly TextBlock _behaviorSectionTitle = new();
    private readonly TextBlock _heroTitle = new();
    private readonly TextBlock _heroSubtitle = new() { Opacity = 0.78, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _advancedSubtitle = new() { Opacity = 0.78, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _interfaceNote = new() { Opacity = 0.78, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _supportNote = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly Button _generalTabButton = new();
    private readonly Button _advancedTabButton = new();
    private readonly StackPanel _generalContent = new() { Spacing = 14 };
    private readonly StackPanel _advancedContent = new() { Spacing = 14 };
    private bool _showAdvanced;

    // Assistant install / repair
    private readonly TextBlock _assistantStatus = new() { Text = "", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _assistantProgress = new() { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed };
    private readonly Button _assistantRepairBtn = new() { HorizontalAlignment = HorizontalAlignment.Left };

    // Support bundle
    private readonly Button _exportBtn = new() { HorizontalAlignment = HorizontalAlignment.Left };

    private readonly ScrollViewer _layoutScroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    internal AppSettings UpdatedSettings { get; private set; }

    public UserSettingsDialog(AppSettings settings, Func<Task>? repairAssistantAsync = null)
    {
        _original = settings ?? throw new ArgumentNullException(nameof(settings));
        _working = _original.Clone();
        UpdatedSettings = _original;

        _repairAssistantAsync = repairAssistantAsync;

        DefaultButton = ContentDialogButton.Primary;

        // Handle apply
        PrimaryButtonClick += OnPrimaryClicked;
        CloseButtonClick += OnCloseClicked;
        Closing += OnClosing;

        _assistantEnabled.IsOn = _working.UseLocalLlm;
        _strictMode.IsOn = _working.StrictMode;

        foreach (var option in ClientUiText.GetLanguageOptions())
            _uiLanguage.Items.Add(new UiLanguageChoice(option.Code, option.Label));

        var selectedUiLanguage = ClientUiText.NormalizeLanguage(_working.UiLanguage);
        var selectedIndex = ClientUiText.GetLanguageOptions().ToList().FindIndex(x => string.Equals(x.Code, selectedUiLanguage, StringComparison.OrdinalIgnoreCase));
        _uiLanguage.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        _uiLanguage.SelectionChanged += (_, _) => RefreshUiTexts();

        // Apply current selections
        _ragQuality.SelectedIndex = MapRagQualityToIndex(_working.RagQualityPreset);
        _style.SelectedIndex = MapStyleToIndex(_working.Temperature);
        _length.SelectedIndex = MapLenToIndex(_working.MaxOutputTokens);

        _assistantRepairBtn.Click += async (_, _) => await RunRepairAsync().ConfigureAwait(true);
        _exportBtn.Click += async (_, _) => await ExportSupportBundleAsync().ConfigureAwait(true);

        RefreshUiTexts();
        Content = BuildUi();
    }


    // Overload used by some callers: pass XamlRoot explicitly.
    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot)
        : this(settings, xamlRoot, null)
    {
    }

    // Overload used by some callers: pass XamlRoot explicitly + optional repair delegate.
    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot, Func<Task>? repairAssistantAsync = null)
        : this(settings, repairAssistantAsync)
    {
        if (xamlRoot != null)
            XamlRoot = xamlRoot;
    }



    private UIElement BuildUi()
    {
        _uiLanguage.HorizontalAlignment = HorizontalAlignment.Stretch;
        _assistantEnabled.HorizontalAlignment = HorizontalAlignment.Stretch;
        _strictMode.HorizontalAlignment = HorizontalAlignment.Stretch;
        _ragQuality.HorizontalAlignment = HorizontalAlignment.Stretch;
        _style.HorizontalAlignment = HorizontalAlignment.Stretch;
        _length.HorizontalAlignment = HorizontalAlignment.Stretch;

        _assistantRepairBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _assistantRepairBtn.Padding = new Thickness(16, 10, 16, 10);
        _exportBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _exportBtn.Padding = new Thickness(16, 10, 16, 10);
        _assistantStatus.Opacity = 0.85;

        _layoutScroller.Padding = new Thickness(4, 2, 4, 2);

        var root = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 840
        };

        var hero = new Border
        {
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(20, 18, 20, 18),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x15, 0x15, 0x15)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _heroTitle,
                    _heroSubtitle
                }
            }
        };

        _generalTabButton.Click += (_, _) => SetSettingsView(showAdvanced: false);
        _advancedTabButton.Click += (_, _) => SetSettingsView(showAdvanced: true);

        var tabsHost = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x12, 0x12, 0x12)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x28)),
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

        _generalContent.Children.Add(interfaceCard);
        _generalContent.Children.Add(assistantCard);
        _generalContent.Children.Add(behaviorCard);

        var advancedIntro = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 16, 18, 16),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x12, 0x12, 0x12)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x28)),
            BorderThickness = new Thickness(1),
            Child = _advancedSubtitle
        };

        var section2 = SectionCard(_repairSectionTitle, new UIElement[]
        {
            _assistantRepairBtn,
            _assistantProgress,
            _assistantStatus
        });

        var section3 = SectionCard(_supportSectionTitle, new UIElement[]
        {
            _exportBtn,
            _supportNote
        });

        _advancedContent.Children.Add(advancedIntro);
        _advancedContent.Children.Add(section2);
        _advancedContent.Children.Add(section3);

        root.Children.Add(hero);
        root.Children.Add(tabsHost);
        root.Children.Add(_generalContent);
        root.Children.Add(_advancedContent);

        _layoutScroller.Content = root;
        SetSettingsView(showAdvanced: false);
        return _layoutScroller;
    }

    public void ApplyResponsiveLayout(double maxWidth, double maxHeight)
    {
        _layoutScroller.MaxWidth = maxWidth;
        _layoutScroller.MaxHeight = maxHeight;
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

    private static void ApplyTabButtonStyle(Button button, bool isSelected)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Padding = new Thickness(12, 10, 12, 10);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(14);
        button.Background = new SolidColorBrush(isSelected
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2D, 0x7D, 0xB8)
            : Microsoft.UI.ColorHelper.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static UIElement SectionCard(TextBlock titleBlock, UIElement[] body)
    {
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
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x10, 0x10, 0x10)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x27, 0x27, 0x27)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    private string UiLang => ClientUiText.NormalizeLanguage(GetSelectedLanguageCode());

    private string T(string key) => ClientUiText.Get(key, UiLang);

    private string GetSelectedLanguageCode()
        => _uiLanguage.SelectedItem is UiLanguageChoice choice ? choice.Code : ClientUiText.NormalizeLanguage(_working.UiLanguage);

    private void ResetComboItems(ComboBox combo, params string[] items)
    {
        var selectedIndex = combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex;
        combo.Items.Clear();
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Length - 1));
    }

    private void RefreshUiTexts()
    {
        Title = string.Empty;
        PrimaryButtonText = T("settings.apply");
        CloseButtonText = _busy ? string.Empty : ClientUiText.Get("dialog.close", UiLang);

        _heroTitle.Text = T("settings.title");
        _heroTitle.FontSize = 22;
        _heroTitle.FontWeight = FontWeights.SemiBold;
        _heroSubtitle.Text = T("settings.subtitle");
        _advancedSubtitle.Text = T("settings.advanced.subtitle");
        _interfaceNote.Text = T("settings.interface.note");

        _generalTabButton.Content = T("settings.tab.general");
        _advancedTabButton.Content = T("settings.tab.advanced");
        ApplyTabButtonStyle(_generalTabButton, !_showAdvanced);
        ApplyTabButtonStyle(_advancedTabButton, _showAdvanced);

        _interfaceSectionTitle.Text = T("settings.section.interface");
        _assistantSectionTitle.Text = T("settings.section.assistant");
        _behaviorSectionTitle.Text = T("settings.section.behavior");
        _repairSectionTitle.Text = T("settings.section.repair");
        _supportSectionTitle.Text = T("settings.section.support");
        _supportNote.Text = T("settings.support.note");

        _uiLanguage.Header = T("settings.language");
        _assistantEnabled.Header = T("settings.toggle.assistant");
        _assistantEnabled.OnContent = T("settings.toggle.assistant.on");
        _assistantEnabled.OffContent = T("settings.toggle.assistant.off");
        _strictMode.Header = T("settings.toggle.strict");
        _strictMode.OnContent = T("settings.toggle.strict.on");
        _strictMode.OffContent = T("settings.toggle.strict.off");
        _ragQuality.Header = T("settings.rag_quality");
        _style.Header = T("settings.style");
        _length.Header = T("settings.length");

        ResetComboItems(_ragQuality, T("settings.choice.quick"), T("settings.choice.balanced"), T("settings.choice.deep"));
        ResetComboItems(_style, T("settings.choice.precise"), T("settings.choice.balanced"), T("settings.choice.creative"));
        ResetComboItems(_length, T("settings.choice.short"), T("settings.choice.standard"), T("settings.choice.long"));

        _assistantRepairBtn.Content = T("settings.repair.button");
        _exportBtn.Content = T("settings.support.button");
    }



    private void OnPrimaryClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_busy)
        {
            args.Cancel = true;
            _assistantStatus.Text = T("help.subtitle.busy");
            return;
        }

        // Apply "safe" settings only
        _working.UseLocalLlm = _assistantEnabled.IsOn;
        _working.StrictMode = _strictMode.IsOn;

        _working.RagQualityPreset = MapIndexToRagQuality(_ragQuality.SelectedIndex);
        _working.Temperature = MapIndexToTemp(_style.SelectedIndex);
        _working.MaxOutputTokens = MapIndexToMaxTokens(_length.SelectedIndex);

        _working.UiLanguage = UiLang;

        _original.CopyFrom(_working);
        _original.Save();
        UpdatedSettings = _original;
    }


    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;

        // Disable dialog buttons
        IsPrimaryButtonEnabled = !busy;
        IsSecondaryButtonEnabled = !busy;

        // Hide the Close button while busy (WinUI3 has no IsCloseButtonEnabled)
        if (busy)
        {
            _closeButtonTextBackup ??= CloseButtonText;
            CloseButtonText = "";
        }
        else
        {
            if (_closeButtonTextBackup is not null)
            {
                CloseButtonText = _closeButtonTextBackup;
                _closeButtonTextBackup = null;
            }
        }

        // Disable interactive controls
        _uiLanguage.IsEnabled = !busy;
        _assistantEnabled.IsEnabled = !busy;
        _strictMode.IsEnabled = !busy;
        _ragQuality.IsEnabled = !busy;
        _style.IsEnabled = !busy;
        _length.IsEnabled = !busy;

        _assistantRepairBtn.IsEnabled = !busy;
        _exportBtn.IsEnabled = !busy;

        if (status is not null)
            _assistantStatus.Text = status;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_busy)
        {
            args.Cancel = true;
            _assistantStatus.Text = T("help.subtitle.busy");
        }
    }

    private void OnCloseClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_busy)
        {
            args.Cancel = true;
            _assistantStatus.Text = T("help.subtitle.busy");
        }
    }

    private static int MapRagQualityToIndex(string? preset)
        => (preset ?? "balanced").ToLowerInvariant() switch
        {
            "quick" => 0,
            "balanced" => 1,
            "deep" => 2,
            _ => 1
        };

    private static string MapIndexToRagQuality(int idx)
        => idx switch
        {
            0 => "quick",
            2 => "deep",
            _ => "balanced"
        };

    private static int MapStyleToIndex(double temp)
    {
        if (temp <= 0.15) return 0;
        if (temp <= 0.55) return 1;
        return 2;
    }

    private static double MapIndexToTemp(int idx)
        => idx switch
        {
            0 => 0.1,   // Précis
            2 => 0.8,   // Créatif
            _ => 0.3    // Équilibré
        };

    private static int MapLenToIndex(int maxTokens)
    {
        if (maxTokens <= 450) return 0;
        if (maxTokens <= 1100) return 1;
        return 2;
    }

    private static int MapIndexToMaxTokens(int idx)
        => idx switch
        {
            0 => 350,   // Court
            2 => 1600,  // Long
            _ => 900    // Standard
        };

    private async Task ExportSupportBundleAsync()
    {
        if (_busy) return;
        SetBusy(true, "Création du support-bundle…");
        try
        {
            var zipPath = await SupportBundleBuilder.BuildAsync(_original).ConfigureAwait(true);
            _assistantStatus.Text = "Diagnostic exporté :\n" + zipPath;
            TryOpenFolder(Path.GetDirectoryName(zipPath));
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = "Échec export diagnostic.\n" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }


    private async Task RunRepairAsync()
    {
        // Preferred path: caller-provided repair action (embedded bootstrap in MainWindow).
        if (_repairAssistantAsync is not null)
        {
            if (_busy) return;
            SetBusy(true, "Installation / réparation en cours…");
            _assistantRepairBtn.IsEnabled = false;
            _assistantProgress.Visibility = Visibility.Visible;
            _assistantProgress.IsIndeterminate = true;
            _assistantStatus.Text = "Installation / réparation en cours…";

            try
            {
                await _repairAssistantAsync().ConfigureAwait(true);

                // Post-check (Loading-aware)
                if (await WaitForModelsReadyAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(true))
                    _assistantStatus.Text = "Assistant prêt (LLM disponible).";
                else
                    _assistantStatus.Text = "Assistant démarré, mais le modèle n’est pas prêt. Réessaie dans 1–2 minutes.";
            }
            catch (Exception ex)
            {
                _assistantStatus.Text = "Échec : " + ex.Message;
            }
            finally
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantRepairBtn.IsEnabled = true;
                SetBusy(false);
            }

            return;
        }

        // Fallback: legacy/script-based repair (dev/test).
        await RepairAssistantAsync().ConfigureAwait(true);
    }

    private async Task RepairAssistantAsync()
    {
        if (_busy) return;
            SetBusy(true, "Installation / réparation en cours…");
            _assistantRepairBtn.IsEnabled = false;
        _assistantProgress.Visibility = Visibility.Visible;
        _assistantProgress.IsIndeterminate = true;
        _assistantStatus.Text = "Vérification de l'assistant…";

        try
        {
            // 1) If LLM endpoint already OK, nothing to do.
            if (await IsLlmReadyAsync().ConfigureAwait(true))
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = "Assistant prêt (LLM disponible).";
                return;
            }

            // 2) Prefer install-llm script if present in provisioning.
            var (scriptPath, hasAuto) = TryGetInstallScriptFromProvisioning();
            if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
            {
                var ok = await RunInstallScriptElevatedAndWaitAsync(scriptPath).ConfigureAwait(true);
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = ok
                    ? "Assistant prêt (LLM installé)."
                    : "Installation annulée ou échouée.";
                return;
            }

            // 3) Fallback: downloads plan (legacy / optional)
            if (Provisioning.TryGetDownloadAssets(out var assets, out var autoFirstRun) && assets.Count > 0)
            {
                _assistantStatus.Text = "Téléchargement des composants…";
                _assistantProgress.IsIndeterminate = false;
                _assistantProgress.Value = 0;

                var prog = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    if (p.TotalBytes is long tot && tot > 0)
                    {
                        var pct = (double)p.BytesDownloaded / tot * 100.0;
                        _assistantProgress.Value = Math.Clamp(pct, 0, 100);
                    }
                    else
                    {
                        _assistantProgress.IsIndeterminate = true;
                    }

                    _assistantStatus.Text = $"{p.Stage} ({p.AssetId})";
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await _downloads.EnsureAssetsAsync(assets, prog, cts.Token).ConfigureAwait(true);

                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = "Téléchargement terminé. Redémarrez l'assistant si nécessaire.";
                return;
            }

            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = "Aucun moyen automatique trouvé.\n"
                + "Vérifie que le script install-llm.ps1 est présent dans C:\\SAAIA\\deploy\\, ou contacte l'intégrateur.";
        }
        catch (Exception ex)
        {
            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = "Erreur pendant la réparation.\n" + ex.Message;
        }
        finally
        {
            _assistantRepairBtn.IsEnabled = true;
        }
    }

    private static (string? ScriptPath, bool AutoInstallOnFirstRun) TryGetInstallScriptFromProvisioning()
    {
        try
        {
            var path = Provisioning.FindProvisioningPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (null, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("llm", out var llm))
                return (null, false);

            string? script = null;
            bool auto = false;

            if (llm.TryGetProperty("installScriptPath", out var p) && p.ValueKind == JsonValueKind.String)
                script = p.GetString();

            if (llm.TryGetProperty("autoInstallOnFirstRun", out var a) && a.ValueKind == JsonValueKind.True)
                auto = true;

            return (script, auto);
        }
        catch
        {
            return (null, false);
        }
    }

    private async Task<bool> RunInstallScriptElevatedAndWaitAsync(string scriptPath)
    {
        _assistantStatus.Text = "Lancement de l'installation (UAC)…";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ""
            };

            Process.Start(psi);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            _assistantStatus.Text = "Installation annulée (UAC refusé).";
            return false;
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = "Impossible de lancer l'installation.\n" + ex.Message;
            return false;
        }

        // Wait for LLM readiness (models endpoint)
        _assistantStatus.Text = "Attente du démarrage du LLM…";
        _assistantProgress.IsIndeterminate = true;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsLlmReadyAsync().ConfigureAwait(true))
                return true;

            await Task.Delay(1500).ConfigureAwait(true);
        }

        _assistantStatus.Text = "Timeout : le LLM ne répond pas encore.\n"
            + "Vérifie Docker Desktop et le container saaia-llama.";
        return false;
    }


    private async Task<bool> WaitForModelsReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        _assistantProgress.Visibility = Visibility.Visible;
        _assistantProgress.IsIndeterminate = true;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var probe = await LlmEndpointProbe.GetModelsStatusAsync(
                _original.LlmBaseUrl,
                TimeSpan.FromSeconds(8),
                CancellationToken.None).ConfigureAwait(true);

            if (probe.Status == LlmModelsStatus.Ok)
                return true;

            if (probe.Status == LlmModelsStatus.Loading)
                _assistantStatus.Text = "Chargement du modèle en cours…";
            else
                _assistantStatus.Text = "Démarrage de l’assistant…";

            await Task.Delay(1000).ConfigureAwait(true);
        }

        return false;
    }

    private async Task<bool> IsLlmReadyAsync()
    {
        try
        {
            var url = $"http://{_original.LlmHost}:{_original.LlmPort}/v1/models";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync(url).ConfigureAwait(true);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOpenFolder(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
