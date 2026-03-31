namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
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
                    ConfigureDialogChrome(dlg);

                    dlg.CloseButtonClick += (_, __) =>
                    {
                        TrySoftUi("TryAutoInstallIfConfiguredAsync.CancelInstall", () => cts.Cancel());
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
                        TrySoftUi("TryAutoInstallIfConfiguredAsync.HideInstallDialog", dlg.Hide);
                        await TrySoftUiAsync("TryAutoInstallIfConfiguredAsync.AwaitInstallDialogClose", () => showTask);
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
            ConfigureDialogChrome(dlgDl);

            dlgDl.CloseButtonClick += (_, __) =>
            {
                TrySoftUi("TryAutoInstallIfConfiguredAsync.CancelDownloadDialog", () => ctsDl.Cancel());
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
                TrySoftUi("TryAutoInstallIfConfiguredAsync.HideDownloadDialog", dlgDl.Hide);
                await TrySoftUiAsync("TryAutoInstallIfConfiguredAsync.AwaitDownloadDialogClose", () => showTaskDl);
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
        ConfigureDialogChrome(dlg);

        dlg.CloseButtonClick += (_, __) =>
        {
            TrySoftUi("EnsureEmbeddedAssistantAsync.CancelDialog", () => cts.Cancel());
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
            TrySoftUi("EnsureEmbeddedAssistantAsync.HideDialog", dlg.Hide);
            await TrySoftUiAsync("EnsureEmbeddedAssistantAsync.AwaitDialogClose", () => showTask);
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

            var selectedSession = FindSessionById(_sessionId);
            if (selectedSession is not null)
                await LoadSessionAsync(selectedSession, CancellationToken.None);

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
        _sessionId = toSelect?.SessionId;

        TrySoftUi("RefreshSessionsAsync.RebindItemsSource", () =>
        {
            SessionsList.ItemsSource = null;
            SessionsList.ItemsSource = _sessions;
        });

        if (toSelect is not null)
            SyncSessionSelectionVisual(toSelect.SessionId);
        else
            SyncSessionSelectionVisual(null);

        TrySoftUi("RefreshSessionsAsync.DispatcherQueue", () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TrySoftUi("RefreshSessionsAsync.DeferredSyncSelection", () => SyncSessionSelectionVisual(toSelect?.SessionId));
            });
        });
    }


}
