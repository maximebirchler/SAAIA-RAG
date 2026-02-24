using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly LocalLlmBootstrapper _llmBootstrapper = new();
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
    private bool _sourcesCollapsedByWidth;

    private bool _setupAutoPrompted;

    public MainWindow()
    {
        InitializeComponent();

        // Option B provisioning: installer/IT can drop a provisioning.json.
        // Apply it before loading settings so the user has nothing to configure.
        if (Provisioning.TryApplyIfPresent(out var provMsg))
        {
            ClientLog.Info(provMsg);
        }

        Root.Loaded += async (_, __) =>
        {
            UpdateMessagesClip();
            await InitializeUserModeAsync();
        };

        TryResize(1400, 820);

        MessagesList.ItemsSource = _messages;
        SessionsList.ItemsSource = _sessions;

        LoadSettings();
        LoadLocalLlmUiFromSettings();
        UpdateUiState(isGenerating: false);
        Status("Ready.");
    }

    private async Task InitializeUserModeAsync()
    {
        try
        {
            ApplyUserModeVisibility();
            await ShowSetupWizardIfNeededAsync();

            // Ensure assistant is usable (embedded by default).
            await EnsureAssistantReadyIfNeededAsync(force: false);

            // Auto-connect (default) when apiKey exists.
            if (_appSettings.AutoConnect && _agent is null && !NeedsSetupWizard())
            {
                await ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Status("Init failed: " + ex.Message);
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

    private void Status(string s) => StatusText.Text = s;

    private bool IsConnected => _agent is not null;

    private void UpdateUiState(bool isGenerating)
    {
        _isGenerating = isGenerating;

        SendButton.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);
        CancelButton.IsEnabled = IsConnected && _isGenerating;
        InputBox.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);

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

            dlg.XamlRoot = Root.XamlRoot;

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

            var (st, _, _) = await LlmEndpointProbe.GetModelsStatusAsync(_appSettings.LlmBaseUrl, TimeSpan.FromSeconds(2), CancellationToken.None);
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
                        XamlRoot = Root.XamlRoot
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
                            var (s2, _, _) = await LlmEndpointProbe.GetModelsStatusAsync(_appSettings.LlmBaseUrl, TimeSpan.FromSeconds(3), CancellationToken.None);
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
                XamlRoot = Root.XamlRoot
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
        catch
        {
            // ignore
        }
    }

    private async Task EnsureAssistantReadyIfNeededAsync(bool force)
    {
        try
        {
            _appSettings = AppSettings.Load();
            if (!_appSettings.UseLocalLlm) return;

            var (st, http, msg) = await LlmEndpointProbe.GetModelsStatusAsync(_appSettings.LlmBaseUrl, TimeSpan.FromSeconds(2), CancellationToken.None);
            if (st == LlmModelsStatus.Ok) return;

            // Important: if the model is already loading, do NOT attempt any install/repair.
            // Just let the running llama-server finish loading (prevents loops / double-start).
            if (st == LlmModelsStatus.Loading)
            {
                ClientLog.Info($"LLM endpoint reports Loading (http={http}). Skipping repair.");
                Status("Assistant IA : chargement du modèle en cours…");
                return;
            }
            else
            {
                ClientLog.Info($"LLM endpoint not ready (status={http}, msg={msg}). Proceeding with bootstrap.");
            }

            // Avoid re-running every startup when provisioning didn't change.
            if (!force && !string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash) &&
                string.Equals(_appSettings.ProvisioningHash, _appSettings.LlmAutoInstallAttemptedHash, StringComparison.OrdinalIgnoreCase))
            {
                Status("Assistant IA : réparation requise (Paramètres → Installer / réparer). ");
                return;
            }

            var mode = (_appSettings.LlmMode ?? "embedded").Trim().ToLowerInvariant();

            if (mode == "docker")
            {
                // Dev/test only: Option B via script (UAC + PowerShell).
                await TryAutoInstallIfConfiguredAsync();
                return;
            }

            await EnsureEmbeddedAssistantAsync(force);
        }
        catch
        {
            // ignore
        }
    }

    private async Task EnsureEmbeddedAssistantAsync(bool force)
    {
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
            XamlRoot = Root.XamlRoot
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

            detail.Text = "Assistant prêt.";
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
        catch
        {
            // ignore
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
        catch
        {
            // ignore
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
        dlg.XamlRoot = Root.XamlRoot;

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            _appSettings = dlg.UpdatedSettings;
            _appSettings.Save();

            // Apply live to the running agent
            _agent?.ApplySettings(_appSettings);

            Status("Settings applied.");
        }
    }
    catch (Exception ex)
    {
        Status("Settings failed: " + ex.Message);
    }
}

private async Task RefreshSessionsAsync(string? preferSessionId, CancellationToken ct)
    {
        var list = await _api.ListSessionsAsync(ct, limit: 200, offset: 0);

        // Si aucune session: on en crée une
        if (list.Count == 0)
        {
            var created = await _api.CreateSessionAsync("New chat", Environment.UserName, ct);
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

            Status("Loading chat…");

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


            Status($"Loaded. Session: {_sessionId}");
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
            Status("Click Connect first.");
            return;
        }

        if (_isGenerating) return;

        try
        {
            Status("Creating new chat…");

            var res = await _api.CreateSessionAsync("New chat", Environment.UserName, CancellationToken.None);

            // refresh pour ordre correct (backend ORDER BY updated_at desc)
            await RefreshSessionsAsync(preferSessionId: res.SessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status($"New session: {_sessionId}");
        }
        catch (Exception ex)
        {
            Status("New chat failed: " + ex.Message);
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
                PlaceholderText = "Titre…"
            };

            var dlg = new ContentDialog
            {
                Title = "Rename chat",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = box,
                XamlRoot = xamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var newTitle = (box.Text ?? "").Trim();
            if (newTitle.Length == 0) newTitle = "New chat";
            if (newTitle.Length > 120) newTitle = newTitle[..120];

            await _api.UpdateSessionTitleAsync(s.SessionId, newTitle, CancellationToken.None);

            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
            Status("Renamed.");
        }
        catch (Exception ex)
        {
            Status("Rename failed: " + ex.Message);
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
                Title = "Delete chat?",
                Content = $"Supprimer définitivement: \"{s.DisplayTitle}\" ?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
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

            Status("Deleted.");
        }
        catch (Exception ex)
        {
            Status("Delete failed: " + ex.Message);
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown && !e.KeyStatus.IsKeyReleased)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isGenerating) return;

        Status("Cancelling…");
        CancelButton.IsEnabled = false;
        try { _cts?.Cancel(); } catch { }
    }

    private static void MarkInterrupted(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null) return;

        if (string.IsNullOrWhiteSpace(assistantMsg.Content))
        {
            assistantMsg.Content = ""; // on laisse vide (ou tu peux mettre "…")
            assistantMsg.StatusNote = "Génération interrompue.";
            return;
        }

        if (string.IsNullOrWhiteSpace(assistantMsg.StatusNote))
            assistantMsg.StatusNote = "Génération interrompue.";
    }

    private async Task MaybeAutoTitleAsync(string userText)
    {
        // Si le titre est "New chat" (ou vide), on met un titre basé sur la 1ère question
        if (SessionsList.SelectedItem is not ChatSessionItem s) return;

        var currentTitle = (s.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(currentTitle) && !string.Equals(currentTitle, "New chat", StringComparison.OrdinalIgnoreCase))
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
        TypingText.Visibility = isTyping ? Visibility.Visible : Visibility.Collapsed;
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

        try
        {
            _isProgrammaticScroll = true;
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, true);
        }
        catch { }
        finally
        {
            _isProgrammaticScroll = false;
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
        // seuils simples et efficaces
        // - >= 1200 : on montre Sources à droite
        // - < 1200 : on cache Sources pour laisser respirer le chat
        var shouldCollapseSources = width < 1200;

        if (shouldCollapseSources == _sourcesCollapsedByWidth)
            return;

        _sourcesCollapsedByWidth = shouldCollapseSources;

        if (_sourcesCollapsedByWidth)
        {
            // cache la colonne Sources
            SourcesCol.Width = new GridLength(0);
            SourcesPanel.Visibility = Visibility.Collapsed;

            // affiche bouton "Sources" en haut (ouvre un popup)
            SourcesToggleButton.Visibility = Visibility.Visible;
        }
        else
        {
            // restore
            SourcesCol.Width = new GridLength(360);
            SourcesPanel.Visibility = Visibility.Visible;

            SourcesToggleButton.Visibility = Visibility.Collapsed;
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

        var text = msg.Content ?? "";
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

        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        ChatMessageItem? assistantMsg = null;

        try
        {
            UpdateUiState(isGenerating: true);

            InputBox.Text = "";

            var tailBefore = _messages.ToList();

            var userMsg = new ChatMessageItem { Role = "user", Content = text, CreatedAt = DateTime.UtcNow };
            _messages.Add(userMsg);
            await _api.AddMessageAsync(_sessionId!, "user", text, null, CancellationToken.None);

            await MaybeAutoTitleAsync(text);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = "",
                CreatedAt = DateTime.UtcNow,
                StatusNote = null
            };
            _messages.Add(assistantMsg);
            ScrollToBottom(force: true);

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            Status("Thinking…");
            
            SetTyping(true);
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: ClientDefaults.DefaultCategory,
                conversationTail: tailBefore,
                onDelta: token =>
                {
                   DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMsg.Content += token;

                        // Auto-follow seulement si on est en mode follow et que l'user n'a pas scroll up
                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom();
                    });
                },
                ct: _cts.Token);

            var wasCancelled = _cts.Token.IsCancellationRequested;

            if (!wasCancelled)
            {
                assistantMsg.Content = string.IsNullOrWhiteSpace(finalAnswer)
                    ? "⚠️ Réponse vide côté LLM. Voir les sources à droite."
                    : finalAnswer;

                assistantMsg.StatusNote = null;
            }
            else
            {
                MarkInterrupted(assistantMsg);
                if (string.IsNullOrWhiteSpace(assistantMsg.Content) && !string.IsNullOrWhiteSpace(finalAnswer))
                    assistantMsg.Content = finalAnswer;
            }

            var pretty = System.Text.Json.JsonSerializer.Serialize(
                sourcesObj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;

            SourcesCards.Items = SourceCardParser.Parse(pretty);
            SourcesBox.Text = pretty;

            await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, sourcesObj, CancellationToken.None);

            // refresh sidebar order after activity
            await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            Status(wasCancelled ? "Cancelled." : "Done.");
        }
        catch (OperationCanceledException)
        {
            SetTyping(false);
            UpdateJumpButton();
            MarkInterrupted(assistantMsg);

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            Status("Cancelled.");
        }
        catch (Exception ex)
        {
            SetTyping(false);
            UpdateJumpButton();
            Status("Send failed: " + ex.Message);
        }
        finally
        {
            SetTyping(false);
            UpdateJumpButton();
            UpdateUiState(isGenerating: false);
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
