using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Controls;

using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private readonly OpenAiLlmClient _llm = new();
    private AppSettings _appSettings = AppSettings.Load();
    private readonly LlamaCppProcessManager _llmProc = new();
    private readonly DownloadManager _downloads = new();
    private readonly Services.LocalLlmBootstrapper _llmBootstrapper = new();
    private RagChatAgent? _agent;

    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly ObservableCollection<ChatSessionItem> _sessions = new();

    private string? _sessionId;
    private string _userId = "";
    private CancellationTokenSource? _cts;
    private bool _isGenerating;
    private bool _isLoadingSession;
    private bool _autoFollow = true;          // si true: on suit le bas pendant streaming
    private bool _userScrolledUp = false;     // si true: on ne force plus le scroll
    private DateTime _lastAutoScroll = DateTime.MinValue;
    private bool _isProgrammaticScroll = false;

    // Windows title bar colors (edit these values if you want another header color)
    private static readonly global::Windows.UI.Color TitleBarBackgroundColor = global::Windows.UI.Color.FromArgb(255, 18, 18, 18);
    private static readonly global::Windows.UI.Color TitleBarInactiveBackgroundColor = global::Windows.UI.Color.FromArgb(255, 18, 18, 18);
    private static readonly global::Windows.UI.Color TitleBarButtonHoverColor = global::Windows.UI.Color.FromArgb(255, 40, 40, 40);
    private static readonly global::Windows.UI.Color TitleBarButtonPressedColor = global::Windows.UI.Color.FromArgb(255, 55, 55, 55);
    private static readonly global::Windows.UI.Color TitleBarForegroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
    private static readonly global::Windows.UI.Color TitleBarTransparentColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusHideTimer;
    private bool _typingPinned;

    private bool _setupAutoPrompted;
    private string? _pendingOutboundWireText;
    private string? _pendingOutboundDisplayText;
    private ContentDialog? _activeHelpDialog;
    private Grid? _startupSplashOverlay;
    private Border? _startupSplashCard;
    private TextBlock? _startupSplashTitleText;
    private TextBlock? _startupSplashSubtitleText;
    private TextBlock? _startupSplashStatusText;
    private WindowSizeConstraintsController? _windowSizeConstraints;
    private int _secretAdminClickCount;
    private DateTimeOffset _secretAdminFirstClickUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan SecretAdminClickWindow = TimeSpan.FromMilliseconds(1500);

    public MainWindow()
    {
        InitializeComponent();

        // Option B provisioning: installer/IT can drop a provisioning.json.
        // Apply it before loading settings so the user has nothing to configure.
        if (Provisioning.TryApplyIfPresent(out var provMsg))
        {
            ClientLog.Info(provMsg);
        }

        EnsureWindowSizingConstraints();
        EnsureStartupSplashOverlay();
        ShowStartupSplash(ClientUiText.Get("startup.status.initializing", _appSettings.UiLanguage));

        Root.Loaded += async (_, __) =>
        {
            UpdateMessagesClip();
            await InitializeUserModeAsync();
        };

        TryResize(1400, 820);
        ApplyWindowChrome();

        MessagesList.ItemsSource = _messages;
        SessionsList.ItemsSource = _sessions;

        Closed += (_, __) => { CloseTransientDialogs(); try { _windowSizeConstraints?.Dispose(); } catch { } _windowSizeConstraints = null; };

        LoadSettings();
        LoadLocalLlmUiFromSettings();
        UpdateUiState(isGenerating: false);
        Status(ClientUiText.Get("status.ready", _appSettings.UiLanguage));
    }

    private string GetDefaultSessionTitle()
        => ClientUiText.Get("chat.default_title", _appSettings.UiLanguage);

    private static bool IsDefaultSessionTitle(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length == 0)
            return true;

        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            if (string.Equals(value, ClientUiText.Get("chat.default_title", language), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(value, "New chat", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyLocalizedDefaultSessionTitles()
    {
        var localizedDefault = GetDefaultSessionTitle();
        foreach (var session in _sessions)
        {
            if (IsDefaultSessionTitle(session.Title))
                session.Title = localizedDefault;
        }
    }

    private void SessionMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem rename)
            rename.Text = ClientUiText.Get("session.menu.rename", _appSettings.UiLanguage);
        if (flyout.Items.Count > 1 && flyout.Items[1] is MenuFlyoutItem delete)
            delete.Text = ClientUiText.Get("session.menu.delete", _appSettings.UiLanguage);
    }

    private async Task InitializeUserModeAsync()
    {
        try
        {
            ShowStartupSplash(ClientUiText.Get("startup.status.initializing", _appSettings.UiLanguage));
            ApplyUserModeVisibility();

            ShowStartupSplash(ClientUiText.Get("startup.status.checking_setup", _appSettings.UiLanguage));
            await ShowSetupWizardIfNeededAsync();

            ShowStartupSplash(ClientUiText.Get("startup.status.starting_assistant", _appSettings.UiLanguage));
            await EnsureAssistantReadyIfNeededAsync(force: false);

            if (_appSettings.AutoConnect && _agent is null && !NeedsSetupWizard())
            {
                ShowStartupSplash(ClientUiText.Get("startup.status.connecting", _appSettings.UiLanguage));
                await ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.init_failed", _appSettings.UiLanguage) + ex.Message);
        }
        finally
        {
            HideStartupSplash();
        }
    }

    private void TryResize(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch
        {
            Activated += (_, __) =>
            {
                try { AppWindow.Resize(new SizeInt32(width, height)); } catch { }
            };
        }
    }

    private void ApplyWindowChrome()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var tb = AppWindow.TitleBar;
            tb.BackgroundColor = TitleBarBackgroundColor;
            tb.ForegroundColor = TitleBarForegroundColor;
            tb.InactiveBackgroundColor = TitleBarInactiveBackgroundColor;
            tb.InactiveForegroundColor = TitleBarForegroundColor;

            // Keep the caption buttons visually on the exact same background as the custom title bar.
            // Using Transparent here lets the AppTitleBar background show through.
            tb.ButtonBackgroundColor = TitleBarTransparentColor;
            tb.ButtonForegroundColor = TitleBarForegroundColor;
            tb.ButtonHoverBackgroundColor = TitleBarButtonHoverColor;
            tb.ButtonHoverForegroundColor = TitleBarForegroundColor;
            tb.ButtonPressedBackgroundColor = TitleBarButtonPressedColor;
            tb.ButtonPressedForegroundColor = TitleBarForegroundColor;
            tb.ButtonInactiveBackgroundColor = TitleBarTransparentColor;
            tb.ButtonInactiveForegroundColor = TitleBarForegroundColor;
        }
        catch
        {
            // non bloquant
        }
    }

    private void EnsureWindowSizingConstraints()
    {
        try
        {
            _windowSizeConstraints ??= WindowSizeConstraintsController.TryAttach(this, 1120, 760, 1800, 1220);
            if (_windowSizeConstraints is not null)
                return;

            Activated += (_, __) =>
            {
                try { _windowSizeConstraints ??= WindowSizeConstraintsController.TryAttach(this, 1120, 760, 1800, 1220); } catch { }
            };
        }
        catch
        {
        }
    }

    private void EnsureStartupSplashOverlay()
    {
        if (_startupSplashOverlay is not null || Root is null)
            return;

        var lang = ClientUiText.NormalizeLanguage(_appSettings.UiLanguage);

        _startupSplashTitleText = new TextBlock
        {
            Text = ClientUiText.Get("startup.title", lang),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _startupSplashSubtitleText = new TextBlock
        {
            Text = ClientUiText.Get("startup.subtitle", lang),
            Opacity = 0.85,
            TextWrapping = TextWrapping.WrapWholeWords,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _startupSplashStatusText = new TextBlock
        {
            Text = ClientUiText.Get("startup.status.initializing", lang),
            Opacity = 0.88,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380
        };

        var ringRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ringRow.Children.Add(new ProgressRing { Width = 20, Height = 20, IsActive = true });
        ringRow.Children.Add(_startupSplashStatusText);

        var content = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/SAAIA_Main.png")),
            Height = 72,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(_startupSplashTitleText);
        content.Children.Add(_startupSplashSubtitleText);
        content.Children.Add(ringRow);

        _startupSplashCard = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 21, 21, 21)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 45, 45, 45)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28),
            MinWidth = 360,
            MaxWidth = 560,
            Child = content
        };

        _startupSplashOverlay = new Grid
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(204, 15, 15, 15)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _startupSplashOverlay.Children.Add(_startupSplashCard);
        Grid.SetRowSpan(_startupSplashOverlay, 3);
        Canvas.SetZIndex(_startupSplashOverlay, 1000);
        Root.Children.Add(_startupSplashOverlay);
    }

    private void ShowStartupSplash(string? statusText = null)
    {
        try
        {
            EnsureStartupSplashOverlay();
            if (_startupSplashOverlay is null)
                return;

            UpdateStartupSplashText(statusText);
            _startupSplashOverlay.Visibility = Visibility.Visible;
            _startupSplashOverlay.IsHitTestVisible = true;
        }
        catch
        {
        }
    }

    private void HideStartupSplash()
    {
        try
        {
            if (_startupSplashOverlay is null)
                return;

            _startupSplashOverlay.Visibility = Visibility.Collapsed;
            _startupSplashOverlay.IsHitTestVisible = false;
        }
        catch
        {
        }
    }

    private void UpdateStartupSplashText(string? statusText = null)
    {
        try
        {
            var lang = ClientUiText.NormalizeLanguage(_appSettings.UiLanguage);
            if (_startupSplashTitleText is not null)
                _startupSplashTitleText.Text = ClientUiText.Get("startup.title", lang);
            if (_startupSplashSubtitleText is not null)
                _startupSplashSubtitleText.Text = ClientUiText.Get("startup.subtitle", lang);
            if (_startupSplashStatusText is not null)
                _startupSplashStatusText.Text = string.IsNullOrWhiteSpace(statusText)
                    ? ClientUiText.Get("startup.status.initializing", lang)
                    : statusText;
        }
        catch
        {
        }
    }

    private void Status(string s)
    {
        try
        {
            if (StatusText is not null)
                StatusText.Text = s;

            _statusHideTimer?.Stop();
            if (_isGenerating)
                return;

            _statusHideTimer = DispatcherQueue.CreateTimer();
            _statusHideTimer.Interval = TimeSpan.FromSeconds(1.8);
            _statusHideTimer.Tick += (_, __) =>
            {
                _statusHideTimer?.Stop();
                if (!_isGenerating && StatusText is not null)
                    StatusText.Text = string.Empty;
            };
            _statusHideTimer.Start();
        }
        catch
        {
        }
    }

    private void StageOutboundMessage(string wireText, string? displayText = null)
    {
        _pendingOutboundWireText = string.IsNullOrWhiteSpace(wireText) ? null : wireText.Trim();
        _pendingOutboundDisplayText = string.IsNullOrWhiteSpace(displayText) ? null : displayText.Trim();
        if (InputBox is not null)
            InputBox.Text = _pendingOutboundDisplayText ?? _pendingOutboundWireText ?? string.Empty;
    }

    private void CloseTransientDialogs()
    {
        try
        {
            _activeHelpDialog?.Hide();
        }
        catch
        {
        }
        finally
        {
            _activeHelpDialog = null;
        }
    }

    private void ClearStatus()
    {
        try
        {
            _statusHideTimer?.Stop();
            _statusHideTimer = null;

            if (StatusText is not null)
                StatusText.Text = string.Empty;

            if (TypingText is not null)
            {
                TypingText.Text = string.Empty;
                TypingText.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
        }
    }

    private bool IsConnected => _agent is not null;

    private void UpdateUiState(bool isGenerating)
    {
        _isGenerating = isGenerating;

        InputBox.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);

        // Single button (Option B): Send when idle, Cancel when generating.
        SendCancelButton.IsEnabled = IsConnected && !string.IsNullOrWhiteSpace(_sessionId);
        SendCancelIcon.Glyph = _isGenerating ? "\uE71A" : "\uE724"; // Stop / Send
        ToolTipService.SetToolTip(SendCancelButton, _isGenerating ? ClientUiText.Get("button.cancel", _appSettings.UiLanguage) : ClientUiText.Get("button.send", _appSettings.UiLanguage));
        if (HeaderHelpButton is not null)
            HeaderHelpButton.IsEnabled = !_isGenerating;

        SessionsList.IsEnabled = IsConnected && !_isGenerating;
        NewChatButton.IsEnabled = IsConnected && !_isGenerating;

        ConnectButton.IsEnabled = !_isGenerating;
    }

    private void LoadSettings()
    {
        _appSettings = AppSettings.Load();
        ServerUrlBox.Text = string.IsNullOrWhiteSpace(_appSettings.BackendUrl) ? ClientDefaults.BackendBaseUrl : _appSettings.BackendUrl;

        _userId = SecureLocalStore.GetOrCreateUserId();
        ApiKeyBox.Password = SecureLocalStore.GetServerApiKey() ?? "";

        // LLM endpoint is configured in settings (usually 127.0.0.1:1234/v1). Hidden in user mode.
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = string.IsNullOrWhiteSpace(_appSettings.ModelId) ? ClientDefaults.LlmModel : _appSettings.ModelId;

        _sessionId = string.IsNullOrWhiteSpace(_appSettings.LastSessionId) ? null : _appSettings.LastSessionId;
        ApplyUiLanguage();
    }

    private void SaveSettings()
    {
        SecureLocalStore.SetServerApiKey(ApiKeyBox.Password.Trim());

        // Persist last session id in AppSettings (works both packaged and unpackaged)
        _appSettings ??= AppSettings.Load();
        _appSettings.LastSessionId = _sessionId;
        _appSettings.Save();
    }

    private bool NeedsSetupWizard()
    {
        var apiKey = (ApiKeyBox.Password ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) return true;
        return false;
    }


    private async Task<Microsoft.UI.Xaml.XamlRoot?> GetDialogXamlRootAsync()
    {
        // WinUI 3: ContentDialog requires a non-null XamlRoot.
        // In early startup it can briefly be null.
        Microsoft.UI.Xaml.XamlRoot? xamlRoot = null;
        try { xamlRoot = Root?.XamlRoot ?? Content?.XamlRoot; } catch { }
        for (var i = 0; xamlRoot is null && i < 40; i++)
        {
            await Task.Delay(50);
            try { xamlRoot = Root?.XamlRoot ?? Content?.XamlRoot; } catch { }
        }
        return xamlRoot;
    }

    private async Task ShowSetupWizardIfNeededAsync()
    {
        if (_setupAutoPrompted) return;
        _setupAutoPrompted = true;

        if (!NeedsSetupWizard()) return;

        await ShowSetupWizardAsync();
    }

    private async Task ShowSetupWizardAsync()
    {
        try
        {
            _userId = SecureLocalStore.GetOrCreateUserId();

            var dlg = new SetupWizardDialog(
                backendUrl: ClientDefaults.BackendBaseUrl,
                userId: _userId,
                apiKeyInitial: ApiKeyBox.Password,
                settingsInitial: _appSettings,
                llmProc: _llmProc);

            var xamlRoot = await GetDialogXamlRootAsync();
            if (xamlRoot is not null) dlg.XamlRoot = xamlRoot;

            await dlg.ShowAsync();

            if (dlg.Applied)
            {
                LoadSettings();
                LoadLocalLlmUiFromSettings();
                ApplyUserModeVisibility();
                Status("Setup saved.");

                if (_appSettings.AutoConnect && _agent is null && !NeedsSetupWizard())
                    await ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Status("Setup wizard failed: " + ex.Message);
        }
    }


    private async Task TryAutoInstallIfConfiguredAsync()
    {
        try
        {
            _appSettings = AppSettings.Load();

            // Only if assistant is enabled.
            if (!_appSettings.UseLocalLlm) return;

            var xamlRoot = await GetDialogXamlRootAsync();
            if (xamlRoot is null)
            {
                ClientLog.Error("LLM bootstrap UI: XamlRoot is null; cannot show progress dialog.");
                Status("Assistant IA : UI non prête (réessayez).");
                return;
            }

            // Probe /v1/models (OpenAI-compatible). Avoid tuple deconstruction here to keep compilation
            // resilient across minor signature changes.
            var probe0 = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(6),
                CancellationToken.None);
            var st = probe0.Status;
            if (st == LlmModelsStatus.Ok) return;

            // If model is already loading, do not attempt an install (wait for IT/docker).
            if (st == LlmModelsStatus.Loading)
            {
                Status("Assistant IA : chargement du modèle…");
                return;
            }

            // Preferred: Option B (docker + model) via elevated installer script.
            if (Provisioning.TryGetLlmAutoInstall(out var autoLlm, out var scriptPath) && autoLlm)
            {
                if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
                {
                    // Prevent re-running every startup.
                    if (!string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash) &&
                        string.Equals(_appSettings.ProvisioningHash, _appSettings.LlmAutoInstallAttemptedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var title = new TextBlock
                    {
                        Text = "Installation / réparation de l’assistant IA…",
                        TextWrapping = TextWrapping.Wrap
                    };

                    var detail = new TextBlock
                    {
                        Text = "Une fenêtre Windows peut demander une autorisation (UAC).",
                        Opacity = 0.85,
                        TextWrapping = TextWrapping.Wrap
                    };

                    var bar = new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 6,
                        Minimum = 0,
                        Maximum = 1
                    };

                    var panel = new StackPanel { Spacing = 12 };
                    panel.Children.Add(title);
                    panel.Children.Add(bar);
                    panel.Children.Add(detail);

                    using var cts = new CancellationTokenSource();

                    var dlg = new ContentDialog
                    {
                        Title = "Préparation",
                        Content = panel,
                        CloseButtonText = "Annuler",
                        XamlRoot = xamlRoot
                    };

                    dlg.CloseButtonClick += (_, __) =>
                    {
                        try { cts.Cancel(); } catch { }
                    };

                    var showTask = dlg.ShowAsync().AsTask();

                    try
                    {
                        detail.Text = "Lancement de l’installation…";
                        var (ok, err) = await LlmInstallScriptRunner.RunElevatedAsync(scriptPath, cts.Token);

                        if (!ok)
                        {
                            detail.Text = "Installation annulée ou échouée."
                                          + (string.IsNullOrWhiteSpace(err) ? "" : ("\n" + err));
                            await Task.Delay(1200);
                            return;
                        }

                        detail.Text = "Démarrage de l’assistant…";
                        var deadline = DateTime.UtcNow.AddMinutes(10);

                        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
                        {
                            var probe2 = await LlmEndpointProbe.GetModelsStatusAsync(
                                _appSettings.LlmBaseUrl,
                                TimeSpan.FromSeconds(6),
                                CancellationToken.None);
                            var s2 = probe2.Status;
                            if (s2 == LlmModelsStatus.Ok)
                            {
                                detail.Text = "Assistant prêt.";
                                _appSettings.LlmAutoInstallAttemptedHash = _appSettings.ProvisioningHash;
                                _appSettings.Save();
                                await Task.Delay(600);
                                return;
                            }

                            detail.Text = s2 == LlmModelsStatus.Loading
                                ? "Chargement du modèle…"
                                : "Attente de l’assistant…";

                            await Task.Delay(1500, cts.Token);
                        }

                        detail.Text = "Timeout : l’assistant n’a pas répondu à temps.";
                        await Task.Delay(1200);
                    }
                    catch
                    {
                        // ignore
                    }
                    finally
                    {
                        try { dlg.Hide(); } catch { }
                        try { await showTask; } catch { }
                    }

                    return;
                }
            }

            // Fallback (legacy): installer/IT may provide a download plan.
            if (!Provisioning.TryGetDownloadAssets(out var assets, out var auto) || !auto)
                return;

            var titleDl = new TextBlock
            {
                Text = "Téléchargement de l’assistant IA…",
                TextWrapping = TextWrapping.Wrap
            };

            var detailDl = new TextBlock
            {
                Text = "",
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap
            };

            var barDl = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 6,
                Minimum = 0,
                Maximum = 1
            };

            var panelDl = new StackPanel { Spacing = 12 };
            panelDl.Children.Add(titleDl);
            panelDl.Children.Add(barDl);
            panelDl.Children.Add(detailDl);

            using var ctsDl = new CancellationTokenSource();

            var dlgDl = new ContentDialog
            {
                Title = "Préparation",
                Content = panelDl,
                CloseButtonText = "Annuler",
                XamlRoot = xamlRoot
            };

            dlgDl.CloseButtonClick += (_, __) =>
            {
                try { ctsDl.Cancel(); } catch { }
            };

            var showTaskDl = dlgDl.ShowAsync().AsTask();

            try
            {
                var prog = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    if (p.TotalBytes is long tot && tot > 0)
                    {
                        barDl.IsIndeterminate = false;
                        barDl.Maximum = tot;
                        barDl.Value = Math.Min(tot, Math.Max(0, p.DownloadedBytes));
                    }
                    else
                    {
                        barDl.IsIndeterminate = true;
                    }

                    detailDl.Text = p.Stage switch
                    {
                        "verify" => $"Vérification : {p.Id}",
                        "download" => $"Téléchargement : {p.Id}",
                        "done" => $"OK : {p.Id}",
                        _ => p.Stage
                    };
                });

                var mgr = new DownloadManager();
                var installed = await mgr.InstallAsync(assets, prog, ctsDl.Token);
                ApplyInstalledAssetsToSettings(installed);
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { dlgDl.Hide(); } catch { }
                try { await showTaskDl; } catch { }
            }
        }
        catch (Exception ex)
        {
            ClientLog.Exception("EnsureAssistantReadyIfNeededAsync", ex);
            Status("Assistant IA : erreur au démarrage (voir logs)");
        }
    }

    private async Task EnsureAssistantReadyIfNeededAsync(bool force)
    {
        try
        {
            _appSettings = AppSettings.Load();
            if (!_appSettings.UseLocalLlm) return;

            var probe = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(6),
                CancellationToken.None);
            var st3 = probe.Status;
            var http = probe.HttpStatus;
            var msg = probe.ErrorMessage;
            if (st3 == LlmModelsStatus.Ok) return;

            // Important: if the model is already loading, do NOT attempt any install/repair.
            // Just let the running llama-server finish loading (prevents loops / double-start).
            if (st3 == LlmModelsStatus.Loading)
            {
                ClientLog.Info($"LLM endpoint reports Loading (http={http}). Skipping repair.");
                Status("Assistant IA : chargement du modèle en cours…");
                return;
            }
            else
            {
                ClientLog.Info($"LLM endpoint not ready (status={http}, msg={msg}). Proceeding with bootstrap.");
            }
            var mode = (_appSettings.LlmMode ?? "embedded").Trim().ToLowerInvariant();

            // GPU upgrade path: if NVIDIA is available and we are still configured with a CPU runtime,
            // do not perform a "cheap start" and do not skip bootstrap due to attempted-hash.
            var hasNvidiaGpuForUpgrade = await GpuDetector.HasNvidiaGpuAsync(CancellationToken.None).ConfigureAwait(false);
            var exePathNow = _appSettings.LlamaExePath ?? "";
            var isCpuRuntimeNow = exePathNow.Contains(System.IO.Path.Combine("llm", "runtime", "win-cpu-x64"), StringComparison.OrdinalIgnoreCase);


            // Embedded: if we already have runtime+model, try a cheap start before any heavy bootstrap.
            // This fixes the case where the model exists on disk but the llama-server process is not running.
            if (mode == "embedded" && _appSettings.ManageLocalLlmProcess &&
                !(hasNvidiaGpuForUpgrade && isCpuRuntimeNow) &&
                !string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) && File.Exists(_appSettings.LlamaExePath) &&
                !string.IsNullOrWhiteSpace(_appSettings.ModelPath) && File.Exists(_appSettings.ModelPath))
            {
                var (startedOk2, startedMsg2) = await _llmProc.StartAsync(_appSettings, CancellationToken.None);
                if (startedOk2)
                {
                    var probe2 = await LlmEndpointProbe.GetModelsStatusAsync(
                        _appSettings.LlmBaseUrl,
                        TimeSpan.FromSeconds(6),
                        CancellationToken.None);

                    if (probe2.Status == LlmModelsStatus.Ok) return;

                    if (probe2.Status == LlmModelsStatus.Loading)
                    {
                        Status("Assistant IA : chargement du modèle en cours…");
                        return;
                    }
                }
                else
                {
                    ClientLog.Info($"LLM start attempt failed: {startedMsg2}");
                }
            }

            // Avoid re-running heavy bootstrap every startup when provisioning didn't change.
            
            // If required assets are missing, we MUST run bootstrap automatically (no manual "repair" gate).
            var exeMissing = string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) || !File.Exists(_appSettings.LlamaExePath);
            var modelMissing = string.IsNullOrWhiteSpace(_appSettings.ModelPath) || !File.Exists(_appSettings.ModelPath);
            var missingAssets = exeMissing || modelMissing;
            if (missingAssets)
            {
                ClientLog.Info($"LLM missing assets (exeMissing={exeMissing}, modelMissing={modelMissing}). Forcing bootstrap.");
            }
if (!missingAssets && !force && !string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash) &&
                string.Equals(_appSettings.ProvisioningHash, _appSettings.LlmAutoInstallAttemptedHash, StringComparison.OrdinalIgnoreCase) &&
                !(hasNvidiaGpuForUpgrade && isCpuRuntimeNow))
            {
                Status("Assistant IA : réparation requise (Paramètres → Installer / réparer). ");
                return;
            }
            if (mode == "docker")
            {
                // Dev/test only: Option B via script (UAC + PowerShell).
                await TryAutoInstallIfConfiguredAsync();
                return;
            }

            await EnsureEmbeddedAssistantAsync(force);
        }
        catch (Exception ex)
        {
            ClientLog.Exception("EnsureAssistantReadyIfNeededAsync", ex);
            Status("Assistant IA : erreur au démarrage (voir logs)");
        }
    }

    private async Task EnsureEmbeddedAssistantAsync(bool force)
    {
        var xamlRoot = await GetDialogXamlRootAsync();
        if (xamlRoot is null)
        {
            ClientLog.Error("LLM bootstrap UI: XamlRoot is null; cannot show embedded progress dialog.");
            Status("Assistant IA : UI non prête (réessayez).");
            return;
        }

        // UI dialog with progress; no PowerShell/UAC needed.
        var title = new TextBlock { Text = "Préparation de l’assistant IA…", TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { Text = "Vérification…", Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Minimum = 0, Maximum = 1 };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(title);
        panel.Children.Add(bar);
        panel.Children.Add(detail);

        using var cts = new CancellationTokenSource();

        var dlg = new ContentDialog
        {
            Title = "Assistant",
            Content = panel,
            CloseButtonText = "Annuler",
            XamlRoot = xamlRoot
        };

        dlg.CloseButtonClick += (_, __) =>
        {
            try { cts.Cancel(); } catch { }
        };

        var showTask = dlg.ShowAsync().AsTask();

        try
        {
            var prog = new Progress<DownloadManager.ProgressInfo>(p =>
            {
                if (p.TotalBytes is long tot && tot > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Maximum = tot;
                    bar.Value = Math.Min(tot, Math.Max(0, p.DownloadedBytes));
                }
                else
                {
                    bar.IsIndeterminate = true;
                }

                detail.Text = p.Stage switch
                {
                    "verify" => $"Vérification : {p.Id}",
                    "download" => $"Téléchargement : {p.Id}",
                    "done" => $"OK : {p.Id}",
                    _ => p.Stage
                };
            });

            detail.Text = "Préparation des fichiers…";
            var (ok, msg, _) = await _llmBootstrapper.EnsureAsync(_appSettings, force, prog, cts.Token);
            if (!ok)
            {
                detail.Text = "Échec : " + msg;
                await Task.Delay(1200);
                return;
            }

            detail.Text = "Démarrage de l’assistant…";
            _appSettings = AppSettings.Load();
            _appSettings.ManageLocalLlmProcess = true;
            _appSettings.LlmMode = "embedded";
            _appSettings.Save();

            var (startedOk, startedMsg) = await _llmProc.StartAsync(_appSettings, cts.Token);
            if (!startedOk)
            {
                detail.Text = "Échec : " + startedMsg;
                await Task.Delay(1200);
                return;
            }
            // Wait until /v1/models is really ready (handles 503 "Loading model")
            var readyDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < readyDeadline && !cts.IsCancellationRequested)
            {
                var probe2 = await LlmEndpointProbe.GetModelsStatusAsync(
                    _appSettings.LlmBaseUrl,
                    TimeSpan.FromSeconds(8),
                    cts.Token);

                if (probe2.Status == LlmModelsStatus.Ok)
                    break;

                detail.Text = probe2.Status == LlmModelsStatus.Loading
                    ? "Chargement du modèle…"
                    : "Démarrage de l’assistant…";

                await Task.Delay(1000, cts.Token);
            }

            var finalProbe = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(8),
                cts.Token);

            if (finalProbe.Status != LlmModelsStatus.Ok)
            {
                detail.Text = "Assistant démarré, mais le modèle n’est pas prêt. Réessaie dans 1–2 minutes.";
                await Task.Delay(1600);
                return;
            }

            detail.Text = "Assistant prêt.";

            // Mark provisioning as successfully applied ONLY when models are ready.
            if (!string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash))
            {
                _appSettings.LlmAutoInstallAttemptedHash = _appSettings.ProvisioningHash;
                _appSettings.Save();
            }

            await Task.Delay(600);
}
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            ClientLog.Exception("EnsureAssistantReadyIfNeededAsync", ex);
            Status("Assistant IA : erreur au démarrage (voir logs)");
        }
        finally
        {
            try { dlg.Hide(); } catch { }
            try { await showTask; } catch { }
        }
    }

    private void ApplyInstalledAssetsToSettings(IReadOnlyList<string> installed)
    {
        try
        {
            _appSettings = AppSettings.Load();

            var exe = installed.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var gguf = installed.FirstOrDefault(p => p.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(exe))
                _appSettings.LlamaExePath = exe;

            if (!string.IsNullOrWhiteSpace(gguf))
            {
                _appSettings.ModelPath = gguf;
                _appSettings.ModelId = Path.GetFileName(gguf);
            }

            if (!string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(gguf))
            {
                _appSettings.ManageLocalLlmProcess = true;
                _appSettings.AutoStartOnConnect = true;
                _appSettings.UseLocalLlm = true;

                _appSettings.Host = "127.0.0.1";
                _appSettings.Port = 1234;
            }

            _appSettings.Save();

            // Refresh UI (even in user mode)
            LoadSettings();
            LoadLocalLlmUiFromSettings();
            ApplyUserModeVisibility();
        }
        catch (Exception ex)
        {
            ClientLog.Exception("EnsureAssistantReadyIfNeededAsync", ex);
            Status("Assistant IA : erreur au démarrage (voir logs)");
        }
    }
    private async void SetupWizard_Click(object sender, RoutedEventArgs e)
    {
        await ShowSetupWizardAsync();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            _userId = SecureLocalStore.GetOrCreateUserId();

            _appSettings = AppSettings.Load();
            var backendUrl = string.IsNullOrWhiteSpace(_appSettings.BackendUrl) ? ClientDefaults.BackendBaseUrl : _appSettings.BackendUrl;
            _api.Configure(backendUrl, ApiKeyBox.Password, _userId);

            // LLM endpoint (usually already running via Docker/service). In user mode we do NOT manage a process.
            // Advanced mode can manage llama.cpp if ManageLocalLlmProcess is true.
            if (_appSettings.ManageLocalLlmProcess && _appSettings.UseLocalLlm && _appSettings.AutoStartOnConnect)
            {
                var started = _appSettings.ShowAdvancedUi
                    ? await EnsureLocalLlmStartedAsync(CancellationToken.None)
                    : await EnsureLocalLlmStartedFromSettingsAsync(CancellationToken.None);
                if (!started)
                    Status("Local LLM start failed. Mode dégradé possible.");
            }

            // Configure LLM (even if disabled; agent will handle degraded mode)
            var llmBaseUrl = _appSettings.LlmBaseUrl;
            var llmModelId = string.IsNullOrWhiteSpace(_appSettings.ModelId) ? ClientDefaults.LlmModel : _appSettings.ModelId;

            // If modelId is invalid, auto-fallback to the first /v1/models (safe, prevents breaking).
            try
            {
                _llm.Configure(llmBaseUrl, llmModelId);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var models = await _llm.ListModelsAsync(cts.Token);
                if (models.Count > 0 && !models.Any(m => string.Equals(m, llmModelId, StringComparison.OrdinalIgnoreCase)))
                {
                    _appSettings.ModelId = models[0];
                    _appSettings.Save();
                    llmModelId = models[0];
                    _llm.Configure(llmBaseUrl, llmModelId);
                }
            }
            catch
            {
                // LLM might be down; keep config and continue (degraded mode supported).
                _llm.Configure(llmBaseUrl, llmModelId);
            }

            _agent = new RagChatAgent(_api, _llm);
            _agent.ApplySettings(_appSettings);

            SaveSettings();

            Status("Connecting…");

            await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status($"Connected. Session: {_sessionId}");
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);

        }
        catch (Exception ex)
        {
            Status("Connect failed: " + ex.Message);
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);
        }
    }

    private async void ChatsSecretHotzone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_secretAdminFirstClickUtc == DateTimeOffset.MinValue || (now - _secretAdminFirstClickUtc) > SecretAdminClickWindow)
            {
                _secretAdminFirstClickUtc = now;
                _secretAdminClickCount = 1;
            }
            else
            {
                _secretAdminClickCount++;
            }

            if (_secretAdminClickCount < 5)
                return;

            _secretAdminClickCount = 0;
            _secretAdminFirstClickUtc = DateTimeOffset.MinValue;
            await ShowAdminSessionDialogAsync();
        }
        catch (Exception ex)
        {
            Status("Admin popup failed: " + ex.Message);
        }
    }

    private async Task ShowAdminSessionDialogAsync()
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = _api.HasAdminKey ? "Remplacer la clé admin de session" : "Entrer la clé admin",
            MinWidth = 360
        };

        var info = new TextBlock
        {
            Text = _api.HasAdminKey
                ? "Session admin active sur cette ouverture de l'application."
                : "Aucune session admin active.",
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(info);
        stack.Children.Add(passwordBox);

        var dlg = new ContentDialog
        {
            Title = "Administration",
            Content = stack,
            PrimaryButtonText = _api.HasAdminKey ? "Mettre à jour" : "Connecter",
            SecondaryButtonText = _api.HasAdminKey ? "Déconnecter" : string.Empty,
            CloseButtonText = "Fermer",
            DefaultButton = ContentDialogButton.Primary
        };

        var xamlRoot = await GetDialogXamlRootAsync();
        if (xamlRoot is not null) dlg.XamlRoot = xamlRoot;

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            var key = (passwordBox.Password ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                Status("Clé admin vide.");
                return;
            }

            _api.SetAdminSessionKey(key);
            ClientLog.Info("Admin session key set for current app session.");
            Status("Session admin activée.");
        }
        else if (res == ContentDialogResult.Secondary)
        {
            _api.ClearAdminSessionKey();
            ClientLog.Info("Admin session key cleared.");
            Status("Session admin désactivée.");
        }
    }

    private void ApplyUserModeVisibility()
    {
        _appSettings = AppSettings.Load();

        var showAdv = _appSettings.ShowAdvancedUi;
        SetupButton.Visibility = (showAdv || NeedsSetupWizard()) ? Visibility.Visible : Visibility.Collapsed;
        LlmSettingsButton.Visibility = showAdv ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Visibility = showAdv ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RepairAssistantAsync()
    {
        await EnsureAssistantReadyIfNeededAsync(force: true);
    }

    private async void UserSettings_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // Reload settings (they might have been provisioned or edited externally)
        _appSettings = AppSettings.Load();

        var dlg = new UserSettingsDialog(_appSettings, RepairAssistantAsync);
        var xamlRoot = await GetDialogXamlRootAsync();
        if (xamlRoot is not null) dlg.XamlRoot = xamlRoot;

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            _appSettings = dlg.UpdatedSettings;
            _appSettings.Save();

            // Apply live to the running agent
            _agent?.ApplySettings(_appSettings);

            ApplyUiLanguage();
            Status(ClientUiText.Get("status.settings_applied", _appSettings.UiLanguage));
        }
    }
    catch (Exception ex)
    {
        Status(ClientUiText.Get("status.settings_failed", _appSettings.UiLanguage) + ex.Message);
    }
}

private async Task RefreshSessionsAsync(string? preferSessionId, CancellationToken ct)
    {
        var list = await _api.ListSessionsAsync(ct, limit: 200, offset: 0);

        // Si aucune session: on en crée une
        if (list.Count == 0)
        {
            var created = await _api.CreateSessionAsync(GetDefaultSessionTitle(), Environment.UserName, ct);
            list.Insert(0, new ChatSessionItem
            {
                SessionId = created.SessionId,
                Title = created.Title,
                ClientUser = created.ClientUser,
                CreatedAtUtc = created.CreatedAtUtc.UtcDateTime,
                UpdatedAtUtc = created.CreatedAtUtc.UtcDateTime,
                LastMessageAtUtc = null
            });
        }

        _sessions.Clear();
        foreach (var s in list) _sessions.Add(s);
        ApplyLocalizedDefaultSessionTitles();

        ChatSessionItem? toSelect = null;

        if (!string.IsNullOrWhiteSpace(preferSessionId))
            toSelect = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, preferSessionId, StringComparison.OrdinalIgnoreCase));

        toSelect ??= _sessions.FirstOrDefault();

        if (toSelect is not null)
            SessionsList.SelectedItem = toSelect;
    }

    private async Task LoadSessionAsync(ChatSessionItem session, CancellationToken ct)
    {
        if (_isLoadingSession) return;

        _isLoadingSession = true;
        try
        {
            _sessionId = session.SessionId;
            SaveSettings();

            Status(ClientUiText.Get("status.loading_chat", _appSettings.UiLanguage));

            _messages.Clear();
            var msgs = await _api.ListMessagesAsync(_sessionId!, ct);
            foreach (var m in msgs) _messages.Add(m);

            // reset
            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            // ✅ toujours reset auto-follow au chargement
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            if (_messages.Count > 0)
            {
                var lastWithSources = _messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.SourcesJson));
                MessagesList.SelectedItem = lastWithSources ?? _messages[^1];
                ScrollToBottom(force: true);
            }
            else
            {
                ScrollToBottom(force: true);
            }


            Status(ClientUiText.Format("status.loaded_session", _appSettings.UiLanguage, _sessionId ?? string.Empty));
            UpdateUiState(_isGenerating);
        }
        finally
        {
            _isLoadingSession = false;
        }
    }


    private async void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSession) return;
        if (_isGenerating) return;

        if (SessionsList.SelectedItem is ChatSessionItem sel)
        {
            if (!string.Equals(_sessionId, sel.SessionId, StringComparison.OrdinalIgnoreCase))
                await LoadSessionAsync(sel, CancellationToken.None);
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            Status(ClientUiText.Get("status.connect_first", _appSettings.UiLanguage));
            return;
        }

        if (_isGenerating) return;

        try
        {
            Status(ClientUiText.Get("status.creating_chat", _appSettings.UiLanguage));

            var res = await _api.CreateSessionAsync(GetDefaultSessionTitle(), Environment.UserName, CancellationToken.None);

            // refresh pour ordre correct (backend ORDER BY updated_at desc)
            await RefreshSessionsAsync(preferSessionId: res.SessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status(ClientUiText.Format("status.new_session", _appSettings.UiLanguage, _sessionId ?? string.Empty));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.new_chat_failed", _appSettings.UiLanguage) + ex.Message);
        }
        finally
        {
            UpdateUiState(isGenerating: false);
        }
    }

    private async void SessionRename_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating) return;

        if (sender is not MenuFlyoutItem mi) return;
        if (mi.CommandParameter is not ChatSessionItem s) return;

        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            var box = new TextBox
            {
                Text = s.DisplayTitle,
                PlaceholderText = ClientUiText.Get("session.rename.placeholder", _appSettings.UiLanguage)
            };

            var dlg = new ContentDialog
            {
                Title = ClientUiText.Get("session.rename.title", _appSettings.UiLanguage),
                PrimaryButtonText = ClientUiText.Get("session.rename.save", _appSettings.UiLanguage),
                CloseButtonText = ClientUiText.Get("dialog.close", _appSettings.UiLanguage),
                DefaultButton = ContentDialogButton.Primary,
                Content = box,
                XamlRoot = xamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var newTitle = (box.Text ?? "").Trim();
            if (newTitle.Length == 0) newTitle = GetDefaultSessionTitle();
            if (newTitle.Length > 120) newTitle = newTitle[..120];

            await _api.UpdateSessionTitleAsync(s.SessionId, newTitle, CancellationToken.None);

            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
            Status(ClientUiText.Get("session.rename.done", _appSettings.UiLanguage));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("session.rename.failed", _appSettings.UiLanguage) + ex.Message);
        }
    }

    private async void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating) return;

        if (sender is not MenuFlyoutItem mi) return;
        if (mi.CommandParameter is not ChatSessionItem s) return;

        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            var dlg = new ContentDialog
            {
                Title = ClientUiText.Get("session.delete.title", _appSettings.UiLanguage),
                Content = ClientUiText.Format("session.delete.confirm", _appSettings.UiLanguage, s.DisplayTitle),
                PrimaryButtonText = ClientUiText.Get("session.delete.confirm_button", _appSettings.UiLanguage),
                CloseButtonText = ClientUiText.Get("dialog.close", _appSettings.UiLanguage),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            await _api.DeleteSessionAsync(s.SessionId, CancellationToken.None);

            var next = _sessions.FirstOrDefault(x => !string.Equals(x.SessionId, s.SessionId, StringComparison.OrdinalIgnoreCase));
            await RefreshSessionsAsync(preferSessionId: next?.SessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status(ClientUiText.Get("session.delete.done", _appSettings.UiLanguage));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("session.delete.failed", _appSettings.UiLanguage) + ex.Message);
        }
    }

    private async void SendCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating)
        {
            CancelGeneration();
            return;
        }

        await SendAsync();
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || e.KeyStatus.IsMenuKeyDown)
            return;

        if (sender is not TextBox tb)
            return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var shiftDown = (shift & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        e.Handled = true;

        if (shiftDown)
        {
            InsertNewLineAtCaret(tb);
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await SendAsync();
            }
            catch
            {
                // non bloquant
            }
        });
    }

    private static void InsertNewLineAtCaret(TextBox tb)
    {
        // WinUI TextBox normalizes line breaks internally. Using Environment.NewLine here can
        // desynchronize SelectionStart vs the actual stored text on repeated Shift+Enter presses.
        // A single CR keeps caret math stable and avoids the regression where a second Shift+Enter
        // appears to remove the previous line break.
        const string newline = "\r";

        var current = tb.Text ?? string.Empty;
        var selectionStart = Math.Clamp(tb.SelectionStart, 0, current.Length);
        var selectionLength = Math.Clamp(tb.SelectionLength, 0, current.Length - selectionStart);

        var updated = current.Remove(selectionStart, selectionLength).Insert(selectionStart, newline);
        tb.Text = updated;

        var caret = Math.Clamp(selectionStart + newline.Length, 0, tb.Text?.Length ?? 0);
        tb.SelectionStart = caret;
        tb.SelectionLength = 0;
    }

    private void CancelGeneration()
    {
        if (!_isGenerating) return;

        Status("Cancelling…");
        try { _cts?.Cancel(); } catch { }
    }

    private static void MarkInterrupted(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null) return;

        assistantMsg.ProgressText = null;

        if (string.IsNullOrWhiteSpace(assistantMsg.Content))
        {
            assistantMsg.Content = "";
            assistantMsg.StatusNote = "Génération interrompue.";
            return;
        }

        if (string.IsNullOrWhiteSpace(assistantMsg.StatusNote))
            assistantMsg.StatusNote = "Génération interrompue.";
    }

    private static void SetAssistantProgress(ChatMessageItem? assistantMsg, string? progress)
    {
        if (assistantMsg is null) return;
        assistantMsg.ProgressText = string.IsNullOrWhiteSpace(progress) ? null : progress.Trim();
    }

    private static void ClearAssistantProgress(ChatMessageItem? assistantMsg)
        => SetAssistantProgress(assistantMsg, null);

    private static void StampAssistantMessageStart(ChatMessageItem? assistantMsg, ref int replyStarted)
    {
        if (assistantMsg is null)
            return;

        if (System.Threading.Interlocked.CompareExchange(ref replyStarted, 1, 0) != 0)
            return;

        assistantMsg.CreatedAt = DateTime.UtcNow;
    }

    private static void EnsureAssistantMessageHasFailureText(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null)
            return;

        ClearAssistantProgress(assistantMsg);
        assistantMsg.StatusNote = null;

        if (string.IsNullOrWhiteSpace(assistantMsg.Content))
            assistantMsg.Content = "⚠️ La réponse n'a pas pu être générée. Réessaie.";
    }

    private async Task MaybeAutoTitleAsync(string userText)
    {
        // Si le titre est "New chat" (ou vide), on met un titre basé sur la 1ère question
        if (SessionsList.SelectedItem is not ChatSessionItem s) return;

        var currentTitle = (s.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(currentTitle) && !IsDefaultSessionTitle(currentTitle))
            return;

        var title = (userText ?? "").Trim();
        if (title.Length == 0) return;

        // petit nettoyage + coupe
        title = title.Replace("\r", " ").Replace("\n", " ");
        if (title.Length > 60) title = title[..60];

        try
        {
            await _api.UpdateSessionTitleAsync(s.SessionId, title, CancellationToken.None);
            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
        }
        catch { /* non bloquant */ }
    }

    private void SetTyping(bool isTyping)
    {
        _typingPinned = isTyping;
        if (TypingText is not null)
        {
            TypingText.Text = string.Empty;
            TypingText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateJumpButton()
    {
        JumpBottomButton.Visibility = (_userScrolledUp && _messages.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottom(bool force = false)
    {
        // throttle léger pour éviter un spam de ChangeView pendant streaming
        if (!force)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastAutoScroll).TotalMilliseconds < 120) return;
            _lastAutoScroll = now;
        }

        void ScrollNow()
        {
            try
            {
                _isProgrammaticScroll = true;
                MessagesList?.UpdateLayout();
                MessagesScroll?.UpdateLayout();
                MessagesScroll?.ChangeView(null, MessagesScroll.ScrollableHeight, null, true);
            }
            catch
            {
                // non bloquant
            }
            finally
            {
                _isProgrammaticScroll = false;
            }
        }

        ScrollNow();

        try
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollNow);
        }
        catch
        {
            // non bloquant
        }
    }

    private void MessagesScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isProgrammaticScroll) return;

        // Si le user n'est pas à ~20px du bas => il a scroll up
        var distanceFromBottom = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset;

        var nearBottom = distanceFromBottom < 20;
        _userScrolledUp = !nearBottom;

        // si il revient en bas manuellement, on réactive l'autofollow
        if (nearBottom)
            _autoFollow = true;

        UpdateJumpButton();
    }

    private void JumpBottom_Click(object sender, RoutedEventArgs e)
    {
        _autoFollow = true;
        _userScrolledUp = false;
        UpdateJumpButton();
        ScrollToBottom(force: true);
    }

    private void CopyAssistant_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b) return;
            if (b.CommandParameter is not ChatMessageItem m) return;

            var text = (m.Content ?? "").Trim();
            if (text.Length == 0) return;

            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);

            Status("Copied.");
        }
        catch (Exception ex)
        {
            Status("Copy failed: " + ex.Message);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        // UI change: Sources panel is deprecated (sources are now inline in the chat).
        // Keep it permanently hidden to avoid wasting space.
        try
        {
            SourcesCol.Width = new GridLength(0);
            SourcesPanel.Visibility = Visibility.Collapsed;
            SourcesToggleButton.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // non bloquant
        }
    }

    private void UpdateMessagesClip()
    {
        try
        {
            if (MessagesPanelBorder is null) return;

            var w = MessagesPanelBorder.ActualWidth;
            var h = MessagesPanelBorder.ActualHeight;

            if (w <= 0 || h <= 0) return;

            MessagesPanelBorder.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, w, h)
            };
        }
        catch
        {
            // non bloquant
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        UpdateMessagesClip();
    }

    private bool _chatsCollapsed;

    private void ChatsToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _chatsCollapsed = !_chatsCollapsed;

            ChatsCol.Width = _chatsCollapsed ? new GridLength(0) : new GridLength(280);
            ChatsPanel.Visibility = _chatsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    MessagesPanelBorder?.UpdateLayout();
                    UpdateMessagesClip();
                }
                catch
                {
                    // non bloquant
                }
            });
        }
        catch
        {
            // non bloquant
        }
    }

    private async void SourcesToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            // On prend les sources du message sélectionné.
            // Si rien n’est sélectionné, on tente le dernier message qui a des sources.
            string? json = null;

            if (MessagesList.SelectedItem is ChatMessageItem sel)
                json = sel.SourcesJson;

            if (string.IsNullOrWhiteSpace(json))
                json = _messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.SourcesJson))?.SourcesJson;

            var cards = SourceCardParser.Parse(json);

            var ctrl = new SAAIA.Client.WinUI.Controls.SourcesCardsControl
            {
                Items = cards
            };

            var dlg = new ContentDialog
            {
                Title = "Sources",
                Content = ctrl,
                CloseButtonText = "Close",
                XamlRoot = xamlRoot
            };

            await dlg.ShowAsync();
        }
        catch
        {
            // non bloquant
        }
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;

        // CommandParameter="{Binding}" => ChatMessageItem
        if (b.CommandParameter is not ChatMessageItem msg) return;

        var text = LinkifiedTextBlock.ToPlainText(msg.Content);
        if (text.Length == 0) return;

        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);

        Status("Copied.");
    }

    private void Bubble_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        if (fe.FindName("CopyBtn") is Button b)
        {
            b.Opacity = 1;
            b.IsHitTestVisible = true;
        }
    }

    private void Bubble_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        if (fe.FindName("CopyBtn") is Button b)
        {
            b.Opacity = 0;
            b.IsHitTestVisible = false;
        }
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            Status("Select a chat (or New).");
            return;
        }
        if (_agent is null)
        {
            Status("Click Connect first (agent not ready).");
            return;
        }

        var wireText = _pendingOutboundWireText;
        var displayText = _pendingOutboundDisplayText;
        var text = string.IsNullOrWhiteSpace(wireText)
            ? (InputBox.Text ?? "").Trim()
            : wireText.Trim();
        var shownText = string.IsNullOrWhiteSpace(displayText)
            ? text
            : displayText!.Trim();
        if (text.Length == 0) return;

        ChatMessageItem? assistantMsg = null;

        try
        {
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            UpdateUiState(isGenerating: true);
            InputBox.Text = string.Empty;
            _pendingOutboundWireText = null;
            _pendingOutboundDisplayText = null;

            var tailBefore = _messages.ToList();

            var userMsg = new ChatMessageItem { Role = "user", Content = shownText, CreatedAt = DateTime.UtcNow };
            _messages.Add(userMsg);
            ScrollToBottom(force: true);
            await _api.AddMessageAsync(_sessionId!, "user", shownText, null, CancellationToken.None);

            await MaybeAutoTitleAsync(shownText);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                CreatedAt = DateTime.UtcNow,
                StatusNote = null,
                ProgressText = "Je prépare la réponse…"
            };
            _messages.Add(assistantMsg);
            ScrollToBottom(force: true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollToBottom(force: true));

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = string.Empty;

            SetTyping(true);
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            var finalAnswerCommitted = 0;
            var replyStarted = 0;

            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: ClientDefaults.DefaultCategory,
                conversationTail: tailBefore,
                onDelta: token =>
                {
                    if (string.IsNullOrEmpty(token) || System.Threading.Volatile.Read(ref finalAnswerCommitted) == 1)
                        return;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (System.Threading.Volatile.Read(ref finalAnswerCommitted) == 1)
                            return;

                        StampAssistantMessageStart(assistantMsg, ref replyStarted);
                        assistantMsg.StatusNote = null;
                        ClearAssistantProgress(assistantMsg);
                        assistantMsg.Content += token;

                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom(force: true);
                    });
                },
                onPhase: _ => { },
                onProgress: progress =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SetAssistantProgress(assistantMsg, progress);
                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom(force: true);
                    });
                },
                ct: _cts.Token);

            var wasCancelled = _cts.Token.IsCancellationRequested;

            if (!wasCancelled)
            {
                ClearAssistantProgress(assistantMsg);

                if (!string.IsNullOrWhiteSpace(finalAnswer))
                {
                    StampAssistantMessageStart(assistantMsg, ref replyStarted);
                    System.Threading.Interlocked.Exchange(ref finalAnswerCommitted, 1);
                    assistantMsg.Content = finalAnswer;
                }
                else if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                {
                    assistantMsg.Content = "⚠️ Réponse vide côté LLM. Voir les sources à droite.";
                }

                assistantMsg.StatusNote = null;
            }
            else
            {
                MarkInterrupted(assistantMsg);
                if (string.IsNullOrWhiteSpace(assistantMsg.Content) && !string.IsNullOrWhiteSpace(finalAnswer))
                {
                    StampAssistantMessageStart(assistantMsg, ref replyStarted);
                    System.Threading.Interlocked.Exchange(ref finalAnswerCommitted, 1);
                    assistantMsg.Content = finalAnswer;
                }
            }

            var pretty = sourcesObj is null
                ? ""
                : System.Text.Json.JsonSerializer.Serialize(
                    sourcesObj,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;
            SourcesCards.Items = SourceCardParser.Parse(pretty);
            SourcesBox.Text = pretty;

            await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, sourcesObj, CancellationToken.None, assistantMsg.StatusNote);

            try
            {
                await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);
            }
            catch
            {
            }

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            ClearStatus();
        }
        catch (OperationCanceledException)
        {
            SetTyping(false);
            UpdateJumpButton();
            MarkInterrupted(assistantMsg);

            try
            {
                if (!string.IsNullOrWhiteSpace(_sessionId) && assistantMsg is not null)
                {
                    await _api.AddMessageAsync(
                        _sessionId!,
                        "assistant",
                        assistantMsg.Content ?? "",
                        assistantMsg.SourcesJson,
                        CancellationToken.None,
                        assistantMsg.StatusNote);

                    try { await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None); } catch { }
                }
            }
            catch
            {
            }

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            ClearStatus();
        }
        catch (Exception ex)
        {
            EnsureAssistantMessageHasFailureText(assistantMsg);
            SetTyping(false);
            UpdateJumpButton();
            Status("Send failed: " + ex.Message);
        }
        finally
        {
            UpdateUiState(isGenerating: false);
            SetTyping(false);
            UpdateJumpButton();
        }
    }

    private void MessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesList.SelectedItem is ChatMessageItem m)
        {
            SourcesCards.Items = SourceCardParser.Parse(m.SourcesJson);
            SourcesBox.Text = m.SourcesJson ?? "";
        }
    }

    // =========================
    // Local LLM (llama.cpp) - M6.1
    // =========================

    private void LoadLocalLlmUiFromSettings()
    {
        try
        {
            _appSettings = AppSettings.Load();

            LocalLlmEnabledCheck.IsChecked = _appSettings.UseLocalLlm;
            LocalLlmAutoStartCheck.IsChecked = _appSettings.AutoStartOnConnect;

            LocalLlmExePathBox.Text = _appSettings.LlamaExePath;
            LocalLlmModelPathBox.Text = _appSettings.ModelPath;

            LocalLlmHostBox.Text = _appSettings.Host;
            LocalLlmPortBox.Text = _appSettings.Port.ToString();

            LocalLlmModelIdBox.Text = _appSettings.ModelId;
            LocalLlmExtraArgsBox.Text = _appSettings.ExtraArgs;

            LocalLlmStatusText.Text = _llmProc.IsRunning ? "Running." : "";
            LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
            RefreshLocalLlmModelInfoText();
        }
        catch
        {
            // ignore UI init failures
        }
    }

    private AppSettings ReadLocalLlmSettingsFromUi()
    {
        var s = AppSettings.Load();

        s.UseLocalLlm = LocalLlmEnabledCheck.IsChecked == true;
        s.AutoStartOnConnect = LocalLlmAutoStartCheck.IsChecked == true;

        s.LlamaExePath = (LocalLlmExePathBox.Text ?? "").Trim();
        s.ModelPath = (LocalLlmModelPathBox.Text ?? "").Trim();

        // If exe+model are provided, assume integrator wants process management.
        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && !string.IsNullOrWhiteSpace(s.ModelPath))
            s.ManageLocalLlmProcess = true;

        s.Host = string.IsNullOrWhiteSpace(LocalLlmHostBox.Text) ? "127.0.0.1" : LocalLlmHostBox.Text.Trim();

        if (int.TryParse((LocalLlmPortBox.Text ?? "").Trim(), out var p) && p > 0) s.Port = p;
        else s.Port = 1234;

        s.ModelId = string.IsNullOrWhiteSpace(LocalLlmModelIdBox.Text) ? ClientDefaults.LlmModel : LocalLlmModelIdBox.Text.Trim();
        s.ExtraArgs = (LocalLlmExtraArgsBox.Text ?? "").Trim();

        return s;
    }


    private async Task<bool> EnsureLocalLlmStartedFromSettingsAsync(CancellationToken ct)
    {
        _appSettings = AppSettings.Load();

        if (string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) || string.IsNullOrWhiteSpace(_appSettings.ModelPath))
            return false;

        try { LocalLlmStatusText.Text = "Starting llama.cpp…"; } catch { }

        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        try
        {
            LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
            LocalLlmStatusText.Text = msg;
        }
        catch { }

        // reflect URL/model
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = _appSettings.ModelId;

        return ok;
    }
    private async Task<bool> EnsureLocalLlmStartedAsync(CancellationToken ct)
    {
        _appSettings = ReadLocalLlmSettingsFromUi();
        _appSettings.Save();

        LocalLlmStatusText.Text = "Starting llama.cpp…";
        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
        LocalLlmStatusText.Text = msg;

        // reflect URL/model
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = _appSettings.ModelId;

        return ok;
    }

    private async void LocalLlmStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureLocalLlmStartedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Start failed: " + ex.Message;
        }
    }

    private void LocalLlmStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LocalLlmStatusText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Stop failed: " + ex.Message;
        }
    }

    private void LocalLlmSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _appSettings = ReadLocalLlmSettingsFromUi();
            _appSettings.Save();

            // reflect URL/model in top bar
            LlmUrlBox.Text = _appSettings.UseLocalLlm ? _appSettings.LlmBaseUrl : ClientDefaults.LlmBaseUrl;
            LlmModelBox.Text = _appSettings.ModelId;

            LocalLlmStatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Save failed: " + ex.Message;
        }
    }


    // =========================
    // M6.2 - Model library (import + sha256)
    // =========================

    private void RefreshLocalLlmModelInfoText()
    {
        try
        {
            var path = (LocalLlmModelPathBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                LocalLlmModelInfoText.Text = "";
                return;
            }

            var info = ModelLibrary.TryGetByPath(path);
            if (info is null)
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    var sizeMb = fi.Length / 1024d / 1024d;
                    LocalLlmModelInfoText.Text = $"Size: {sizeMb:0.0} MB (not in library)";
                }
                else
                {
                    LocalLlmModelInfoText.Text = "File not found.";
                }
                return;
            }

            var libSizeMb = info.SizeBytes / 1024d / 1024d;
            var shaShort = info.Sha256.Length > 12 ? info.Sha256.Substring(0, 12) : info.Sha256;
            LocalLlmModelInfoText.Text = $"Library: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}…";
        }
        catch
        {
            LocalLlmModelInfoText.Text = "";
        }
    }

    private async Task<string?> PickFilePathAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void LocalLlmBrowseExe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickFilePathAsync(".exe");
            if (!string.IsNullOrWhiteSpace(path))
                LocalLlmExePathBox.Text = path;
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Browse failed: " + ex.Message;
        }
    }

    private async void LocalLlmBrowseModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickFilePathAsync(".gguf");
            if (!string.IsNullOrWhiteSpace(path))
            {
                LocalLlmModelPathBox.Text = path;
                if (string.IsNullOrWhiteSpace(LocalLlmModelIdBox.Text))
                    LocalLlmModelIdBox.Text = Path.GetFileName(path);

                RefreshLocalLlmModelInfoText();
            }
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Browse failed: " + ex.Message;
        }
    }

    private async void LocalLlmImportModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var src = (LocalLlmModelPathBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
            {
                LocalLlmStatusText.Text = "Select a .gguf file first.";
                return;
            }

            LocalLlmStatusText.Text = "Importing model…";
            var entry = await ModelLibrary.ImportAsync(src, CancellationToken.None);

            LocalLlmModelPathBox.Text = entry.FullPath;
            LocalLlmModelIdBox.Text = entry.Id;

            _appSettings = ReadLocalLlmSettingsFromUi();
            _appSettings.ModelPath = entry.FullPath;
            _appSettings.ModelId = entry.Id;
            _appSettings.Save();

            // reflect URL/model in top bar
            LlmUrlBox.Text = _appSettings.UseLocalLlm ? _appSettings.LlmBaseUrl : ClientDefaults.LlmBaseUrl;
            LlmModelBox.Text = _appSettings.ModelId;

            RefreshLocalLlmModelInfoText();
            LocalLlmStatusText.Text = $"Imported to {ModelLibrary.ModelsDir}";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Import failed: " + ex.Message;
        }
    }

    private void LocalLlmOpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ModelLibrary.ModelsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = ModelLibrary.ModelsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Open folder failed: " + ex.Message;
        }
    }
}


internal sealed class WindowSizeConstraintsController : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private static readonly Dictionary<IntPtr, WindowSizeConstraintsController> Instances = new();

    private readonly IntPtr _hwnd;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private readonly int _maxWidthDip;
    private readonly int _maxHeightDip;
    private readonly WndProc _wndProcDelegate;
    private readonly IntPtr _wndProcPtr;
    private IntPtr _previousWndProc;
    private bool _disposed;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private WindowSizeConstraintsController(IntPtr hwnd, int minWidthDip, int minHeightDip, int maxWidthDip, int maxHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _maxWidthDip = maxWidthDip;
        _maxHeightDip = maxHeightDip;
        _wndProcDelegate = WindowProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _previousWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, _wndProcPtr);
    }

    public static WindowSizeConstraintsController? TryAttach(Window window, int minWidthDip, int minHeightDip, int maxWidthDip, int maxHeightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return null;

        if (Instances.TryGetValue(hwnd, out var existing))
            return existing;

        var controller = new WindowSizeConstraintsController(hwnd, minWidthDip, minHeightDip, maxWidthDip, maxHeightDip);
        Instances[hwnd] = controller;
        return controller;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (_hwnd != IntPtr.Zero && _previousWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GwlpWndProc, _previousWndProc);
        }
        catch
        {
        }

        Instances.Remove(_hwnd);
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var dpi = GetSafeDpi(hWnd);
            info.ptMinTrackSize.x = DipToPixels(_minWidthDip, dpi);
            info.ptMinTrackSize.y = DipToPixels(_minHeightDip, dpi);
            if (_maxWidthDip > 0)
                info.ptMaxTrackSize.x = DipToPixels(_maxWidthDip, dpi);
            if (_maxHeightDip > 0)
                info.ptMaxTrackSize.y = DipToPixels(_maxHeightDip, dpi);
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
    }

    private static uint GetSafeDpi(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96u;
        }
    }

    private static int DipToPixels(int dip, uint dpi)
        => (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, newLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, newLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
