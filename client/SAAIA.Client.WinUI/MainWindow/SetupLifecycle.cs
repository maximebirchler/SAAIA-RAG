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

            // Use the URL currently configured in AppSettings as default (falls back to ClientDefaults
            // only if AppSettings has nothing). This way the wizard reflects the real running config
            // (provisioning.json or previous user input), not a hardcoded localhost.
            var settingsForWizard = _appSettings ?? AppSettings.Load();
            var initialBackendUrl = !string.IsNullOrWhiteSpace(settingsForWizard.BackendUrl)
                ? settingsForWizard.BackendUrl
                : ClientDefaults.BackendBaseUrl;

            var dlg = new SetupWizardDialog(
                backendUrl: initialBackendUrl,
                userId: _userId,
                apiKeyInitial: ApiKeyBox.Password,
                settingsInitial: settingsForWizard,
                llmProc: _llmProc);

            // Wait for the XamlRoot before detaching content, otherwise the wizard's TextBoxes
            // (PasswordBox in particular) refuse focus inside the overlay.
            var xamlRoot = await GetDialogXamlRootAsync();
            if (xamlRoot is not null) dlg.XamlRoot = xamlRoot;
            dlg.RequestedTheme = GetElementTheme();

            // Show inside MainWindow's overlay system (smoke + presenter) — same pattern as
            // UserSettingsDialog. Avoids the leftover ContentDialog rectangle behind the card.
            OverlayDialogSession? overlay = null;
            var overlayContent = dlg.DetachContentForOverlay(() => overlay?.Close());
            overlay = ShowOverlayDialog(overlayContent);
            await overlay.Completion;

            if (dlg.Applied)
            {
                LoadSettings();
                LoadLocalLlmUiFromSettings();
                ApplyUserModeVisibility();
                Status(LocalRuntimeText("Configuration enregistrée.", "Setup saved.", "Configuración guardada.", "Configuração guardada.", "Einrichtung gespeichert.", "Configurazione salvata.", UiLang));

                if ((_appSettings?.AutoConnect ?? false) && _agent is null && !NeedsSetupWizard())
                    await ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Status(LocalRuntimeText("Assistant de configuration en échec : ", "Setup wizard failed: ", "Error del asistente de configuración: ", "Falha no assistente de configuração: ", "Setup-Assistent fehlgeschlagen: ", "Procedura guidata non riuscita: ", UiLang) + ex.Message);
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
                Status(LocalRuntimeText("Assistant IA : interface non prete (reessaie).", "Assistant: UI not ready (try again).", "Asistente: interfaz no lista (vuelve a intentarlo).", "Assistente: interface nao pronta (tenta novamente).", "Assistent: UI nicht bereit (erneut versuchen).", "Assistente: interfaccia non pronta (riprova).", UiLang));
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
                Status(LocalRuntimeText("Assistant IA : chargement du modele...", "Assistant: model loading...", "Asistente: cargando modelo...", "Assistente: a carregar modelo...", "Assistent: Modell wird geladen...", "Assistente: caricamento modello...", UiLang));
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
                        Text = LocalRuntimeText("Installation / reparation de l'assistant IA...", "Installing / repairing the assistant...", "Instalacion / reparacion del asistente...", "Instalacao / reparacao do assistente...", "Installation / Reparatur des Assistenten...", "Installazione / riparazione dell'assistente...", UiLang),
                        TextWrapping = TextWrapping.Wrap
                    };

                    var detail = new TextBlock
                    {
                        Text = LocalRuntimeText("Une fenetre Windows peut demander une autorisation (UAC).", "A Windows prompt may request permission (UAC).", "Una ventana de Windows puede solicitar autorizacion (UAC).", "Uma janela do Windows pode pedir autorizacao (UAC).", "Ein Windows-Fenster kann eine Berechtigung anfordern (UAC).", "Una finestra di Windows puo richiedere un'autorizzazione (UAC).", UiLang),
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
                        Title = LocalRuntimeText("Preparation", "Preparation", "Preparacion", "Preparacao", "Vorbereitung", "Preparazione", UiLang),
                        Content = panel,
                        CloseButtonText = ClientUiText.Get("dialog.close", UiLang),
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
                        detail.Text = LocalRuntimeText("Lancement de l'installation...", "Starting installation...", "Iniciando la instalacion...", "A iniciar a instalacao...", "Installation wird gestartet...", "Avvio dell'installazione...", UiLang);
                        var (ok, err) = await LlmInstallScriptRunner.RunElevatedAsync(scriptPath, cts.Token);

                        if (!ok)
                        {
                            detail.Text = LocalRuntimeText("Installation annulee ou en echec.", "Installation was cancelled or failed.", "La instalacion se cancelo o fallo.", "A instalacao foi cancelada ou falhou.", "Die Installation wurde abgebrochen oder ist fehlgeschlagen.", "L'installazione e stata annullata o non e riuscita.", UiLang)
                                          + (string.IsNullOrWhiteSpace(err) ? "" : ("\n" + err));
                            await Task.Delay(1200);
                            return;
                        }

                        detail.Text = LocalRuntimeText("Demarrage de l'assistant...", "Starting assistant...", "Iniciando el asistente...", "A iniciar o assistente...", "Assistent wird gestartet...", "Avvio dell'assistente...", UiLang);
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
                                detail.Text = LocalRuntimeText("Assistant pret.", "Assistant ready.", "Asistente listo.", "Assistente pronto.", "Assistent bereit.", "Assistente pronto.", UiLang);
                                _appSettings.LlmAutoInstallAttemptedHash = _appSettings.ProvisioningHash;
                                _appSettings.Save();
                                await Task.Delay(600);
                                return;
                            }

                            detail.Text = s2 == LlmModelsStatus.Loading
                                ? LocalRuntimeText("Chargement du modele...", "Model loading...", "Cargando modelo...", "A carregar modelo...", "Modell wird geladen...", "Caricamento modello...", UiLang)
                                : LocalRuntimeText("Attente de l'assistant...", "Waiting for assistant...", "Esperando al asistente...", "A aguardar o assistente...", "Warte auf den Assistenten...", "In attesa dell'assistente...", UiLang);

                            await Task.Delay(1500, cts.Token);
                        }

                        detail.Text = LocalRuntimeText("Delai depasse : l'assistant n'a pas repondu a temps.", "Timeout: the assistant did not respond in time.", "Tiempo agotado: el asistente no respondio a tiempo.", "Tempo esgotado: o assistente nao respondeu a tempo.", "Zeitueberschreitung: der Assistent hat nicht rechtzeitig geantwortet.", "Timeout: l'assistente non ha risposto in tempo.", UiLang);
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
                Text = LocalRuntimeText("Telechargement de l'assistant IA...", "Downloading the assistant...", "Descargando el asistente...", "A transferir o assistente...", "Assistent wird heruntergeladen...", "Download dell'assistente...", UiLang),
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
                Title = LocalRuntimeText("Preparation", "Preparation", "Preparacion", "Preparacao", "Vorbereitung", "Preparazione", UiLang),
                Content = panelDl,
                CloseButtonText = ClientUiText.Get("dialog.close", UiLang),
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
                        "verify" => LocalRuntimeText($"Verification : {p.Id}", $"Verification: {p.Id}", $"Verificacion: {p.Id}", $"Verificacao: {p.Id}", $"Pruefung: {p.Id}", $"Verifica: {p.Id}", UiLang),
                        "download" => LocalRuntimeText($"Telechargement : {p.Id}", $"Download: {p.Id}", $"Descarga: {p.Id}", $"Transferencia: {p.Id}", $"Download: {p.Id}", $"Download: {p.Id}", UiLang),
                        "done" => LocalRuntimeText($"Pret : {p.Id}", $"Done: {p.Id}", $"Listo: {p.Id}", $"Concluido: {p.Id}", $"Fertig: {p.Id}", $"Pronto: {p.Id}", UiLang),
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
            Status(LocalRuntimeText("Assistant IA : erreur au demarrage (voir logs).", "Assistant: startup error (see logs).", "Asistente: error al iniciar (ver logs).", "Assistente: erro ao iniciar (ver logs).", "Assistent: Startfehler (siehe Logs).", "Assistente: errore all'avvio (vedi log).", UiLang));
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
                Status(LocalRuntimeText("Assistant IA : chargement du modele en cours...", "Assistant: model is still loading...", "Asistente: el modelo sigue cargando...", "Assistente: o modelo ainda esta a carregar...", "Assistent: Modell wird noch geladen...", "Assistente: il modello e ancora in caricamento...", UiLang));
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
                        Status(LocalRuntimeText("Assistant IA : chargement du modele en cours...", "Assistant: model is still loading...", "Asistente: el modelo sigue cargando...", "Assistente: o modelo ainda esta a carregar...", "Assistent: Modell wird noch geladen...", "Assistente: il modello e ancora in caricamento...", UiLang));
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
                Status(LocalRuntimeText("Assistant IA : reparation requise (Parametres -> Installer / reparer).", "Assistant: repair required (Settings -> Install / repair).", "Asistente: reparacion necesaria (Configuracion -> Instalar / reparar).", "Assistente: reparacao necessaria (Definicoes -> Instalar / reparar).", "Assistent: Reparatur erforderlich (Einstellungen -> Installieren / reparieren).", "Assistente: riparazione richiesta (Impostazioni -> Installa / ripara).", UiLang));
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
            Status(LocalRuntimeText("Assistant IA : erreur au demarrage (voir logs).", "Assistant: startup error (see logs).", "Asistente: error al iniciar (ver logs).", "Assistente: erro ao iniciar (ver logs).", "Assistent: Startfehler (siehe Logs).", "Assistente: errore all'avvio (vedi log).", UiLang));
        }
    }

    private async Task EnsureEmbeddedAssistantAsync(bool force)
    {
        // No more separate "Préparation de l'assistant IA…" modal. The user already
        // sees the SAAIA startup overlay — we just refresh its status text in place.
        // (Bonus: no XamlRoot-not-ready race, no smoke-on-smoke stacking.)
        using var cts = new CancellationTokenSource();
        var subtitle = ClientUiText.Get("startup.subtitle", UiLang);

        void SetStartupStatus(string status)
            => ShowStartupOverlay(subtitle, status);

        try
        {
            var prog = new Progress<DownloadManager.ProgressInfo>(p =>
            {
                SetStartupStatus(p.Stage switch
                {
                    "verify" => LocalRuntimeText($"Vérification : {p.Id}", $"Verifying: {p.Id}", $"Verificación: {p.Id}", $"Verificação: {p.Id}", $"Pruefung: {p.Id}", $"Verifica: {p.Id}", UiLang),
                    "download" => LocalRuntimeText($"Téléchargement : {p.Id}", $"Download: {p.Id}", $"Descarga: {p.Id}", $"Transferência: {p.Id}", $"Download: {p.Id}", $"Download: {p.Id}", UiLang),
                    "done" => LocalRuntimeText($"Prêt : {p.Id}", $"Done: {p.Id}", $"Listo: {p.Id}", $"Concluído: {p.Id}", $"Fertig: {p.Id}", $"Pronto: {p.Id}", UiLang),
                    _ => p.Stage
                });
            });

            SetStartupStatus(LocalRuntimeText("Préparation des fichiers…", "Preparing files…", "Preparando archivos…", "A preparar ficheiros…", "Dateien werden vorbereitet…", "Preparazione dei file…", UiLang));
            var (ok, msg, _) = await _llmBootstrapper.EnsureAsync(_appSettings, force, prog, cts.Token);
            if (!ok)
            {
                SetStartupStatus(LocalRuntimeText("Échec : ", "Failed: ", "Error: ", "Falha: ", "Fehler: ", "Errore: ", UiLang) + msg);
                await Task.Delay(1200);
                return;
            }

            SetStartupStatus(LocalRuntimeText("Démarrage de l'assistant…", "Starting assistant…", "Iniciando el asistente…", "A iniciar o assistente…", "Assistent wird gestartet…", "Avvio dell'assistente…", UiLang));
            _appSettings = AppSettings.Load();
            _appSettings.ManageLocalLlmProcess = true;
            _appSettings.LlmMode = "embedded";
            _appSettings.Save();

            var (startedOk, startedMsg) = await _llmProc.StartAsync(_appSettings, cts.Token);
            if (!startedOk)
            {
                SetStartupStatus(LocalRuntimeText("Échec : ", "Failed: ", "Error: ", "Falha: ", "Fehler: ", "Errore: ", UiLang) + startedMsg);
                await Task.Delay(1200);
                return;
            }

            // Wait until /v1/models is really ready (handles 503 "Loading model").
            var readyDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < readyDeadline && !cts.IsCancellationRequested)
            {
                var probe2 = await LlmEndpointProbe.GetModelsStatusAsync(
                    _appSettings.LlmBaseUrl,
                    TimeSpan.FromSeconds(8),
                    cts.Token);

                if (probe2.Status == LlmModelsStatus.Ok)
                    break;

                SetStartupStatus(probe2.Status == LlmModelsStatus.Loading
                    ? LocalRuntimeText("Chargement du modèle…", "Model loading…", "Cargando modelo…", "A carregar modelo…", "Modell wird geladen…", "Caricamento modello…", UiLang)
                    : LocalRuntimeText("Démarrage de l'assistant…", "Starting assistant…", "Iniciando el asistente…", "A iniciar o assistente…", "Assistent wird gestartet…", "Avvio dell'assistente…", UiLang));

                await Task.Delay(1000, cts.Token);
            }

            var finalProbe = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(8),
                cts.Token);

            if (finalProbe.Status != LlmModelsStatus.Ok)
            {
                SetStartupStatus(LocalRuntimeText("Assistant démarré, mais le modèle n'est pas prêt. Réessaie dans 1-2 minutes.", "Assistant started, but the model is not ready yet. Try again in 1-2 minutes.", "El asistente se inició, pero el modelo aún no está listo. Vuelve a intentarlo en 1-2 minutos.", "O assistente iniciou, mas o modelo ainda não está pronto. Tenta novamente em 1-2 minutos.", "Der Assistent wurde gestartet, aber das Modell ist noch nicht bereit. Versuche es in 1-2 Minuten erneut.", "L'assistente è stato avviato, ma il modello non è ancora pronto. Riprova tra 1-2 minuti.", UiLang));
                await Task.Delay(1600);
                return;
            }

            SetStartupStatus(LocalRuntimeText("Assistant prêt.", "Assistant ready.", "Asistente listo.", "Assistente pronto.", "Assistent bereit.", "Assistente pronto.", UiLang));

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
            Status(LocalRuntimeText("Assistant IA : erreur au démarrage (voir logs).", "Assistant: startup error (see logs).", "Asistente: error al iniciar (ver logs).", "Assistente: erro ao iniciar (ver logs).", "Assistent: Startfehler (siehe Logs).", "Assistente: errore all'avvio (vedi log).", UiLang));
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
            Status(LocalRuntimeText("Assistant IA : erreur au demarrage (voir logs).", "Assistant: startup error (see logs).", "Asistente: error al iniciar (ver logs).", "Assistente: erro ao iniciar (ver logs).", "Assistent: Startfehler (siehe Logs).", "Assistente: errore all'avvio (vedi log).", UiLang));
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
        _lastConnectErrorMessage = null;
        // Declared outside the try so the catch can include "(192.168.1.75:5122)" in the
        // localized error message — context that's invaluable for debugging which URL failed.
        string backendUrl = "";
        try
        {
            _userId = SecureLocalStore.GetOrCreateUserId();

            _appSettings = AppSettings.Load();

            // Pick the first reachable backend URL among (BackendUrl + alternates). This is
            // what makes "same wizard config" work both on Wifi (LAN URL) and on VPN
            // (Tailscale URL): whichever responds within the timeout wins.
            var candidates = _appSettings.AllBackendUrlCandidates();
            if (candidates.Count <= 1)
            {
                backendUrl = string.IsNullOrWhiteSpace(_appSettings.BackendUrl) ? ClientDefaults.BackendBaseUrl : _appSettings.BackendUrl;
            }
            else
            {
                ShowStartupOverlay(
                    ClientUiText.Get("startup.subtitle", UiLang),
                    LocalRuntimeText("Recherche du serveur joignable…", "Probing reachable backend…", "Buscando servidor accesible…", "À procura do servidor acessível…", "Erreichbares Backend wird gesucht…", "Ricerca del backend raggiungibile…", UiLang));

                var pick = await BackendUrlPicker.PickAsync(candidates, TimeSpan.FromSeconds(3), CancellationToken.None);
                backendUrl = pick.Url ?? candidates[0];

                if (pick.Url is null)
                {
                    // None responded — keep the primary URL configured anyway so the rest of
                    // the connect flow surfaces a clean failure message via _lastConnectErrorMessage.
                    _lastConnectErrorMessage = pick.FailureSummary ?? "no backend reachable";
                }
                else if (pick.ChangedFromPrimary)
                {
                    // Persist the successful URL only as the *active* one for this run; we do
                    // NOT mutate AppSettings.BackendUrl so the user's preferred order stays
                    // intact across networks (next launch will probe again from the same list).
                    ClientLog.Info($"BackendUrlPicker: roamed to {pick.Url} (primary was {candidates[0]}).");
                }
            }

            _api.Configure(backendUrl, ApiKeyBox.Password, _userId);

            // LLM endpoint (usually already running via Docker/service). In user mode we do NOT manage a process.
            // Advanced mode can manage llama.cpp if ManageLocalLlmProcess is true.
            if (_appSettings.ManageLocalLlmProcess && _appSettings.UseLocalLlm && _appSettings.EagerLoad)
            {
                var started = _appSettings.ShowAdvancedUi
                    ? await EnsureLocalLlmStartedAsync(CancellationToken.None)
                    : await EnsureLocalLlmStartedFromSettingsAsync(CancellationToken.None);
                if (!started)
                    Status(LocalRuntimeText("Demarrage du LLM local en echec. Mode degrade possible.", "Local LLM start failed. Fallback mode is possible.", "Error al iniciar el LLM local. Es posible un modo degradado.", "Falha ao iniciar o LLM local. E possivel um modo degradado.", "Lokaler LLM-Start fehlgeschlagen. Ein degradierter Modus ist moeglich.", "Avvio del LLM locale non riuscito. E possibile una modalita degradata.", UiLang));
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

            Status(LocalRuntimeText("Connexion...", "Connecting...", "Conectando...", "A ligar...", "Verbinden...", "Connessione...", UiLang));

            await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);

            var selectedSession = FindSessionById(_sessionId);
            if (selectedSession is not null)
                await LoadSessionAsync(selectedSession, CancellationToken.None);

            Status(LocalRuntimeText($"Connecte. Session : {_sessionId}", $"Connected. Session: {_sessionId}", $"Conectado. Sesion: {_sessionId}", $"Ligado. Sessao: {_sessionId}", $"Verbunden. Sitzung: {_sessionId}", $"Connesso. Sessione: {_sessionId}", UiLang));
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);

        }
        catch (Exception ex)
        {
            // Reset _agent so InitializeUserModeAsync detects the failure and shows the
            // "Pas connecté au serveur" red-cross overlay. Without this, the agent stays
            // wired to the previously-configured (unreachable) URL and the UI looks fine
            // even though no backend call ever succeeds.
            _agent = null;
            _lastConnectErrorMessage = ClassifyConnectError(ex, backendUrl, UiLang);
            Status(LocalRuntimeText("Connexion en échec : ", "Connect failed: ", "Error de conexión: ", "Falha na ligação: ", "Verbindung fehlgeschlagen: ", "Connessione non riuscita: ", UiLang) + _lastConnectErrorMessage);
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);
        }
    }

    // Turn a raw .NET network exception into a localized, actionable one-liner.
    // Recognises the most common Windows socket / HTTP error patterns and falls
    // back to the original message (with URL appended) when nothing matches.
    private static string ClassifyConnectError(Exception ex, string url, string lang)
    {
        var raw = ex?.Message ?? "";
        var lower = raw.ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(url) ? "" : $" ({url})";

        bool Has(params string[] needles)
        {
            foreach (var n in needles)
                if (lower.Contains(n, StringComparison.Ordinal)) return true;
            return false;
        }

        // Timeout (TaskCanceledException + Windows socket "did not properly respond after a period of time").
        if (ex is TaskCanceledException || Has("timed out", "did not properly respond after a period of time", "operation has timed out"))
        {
            return lang switch
            {
                "en" => $"The server did not respond within the timeout{host}.",
                "es" => $"El servidor no respondió dentro del tiempo límite{host}.",
                "pt" => $"O servidor não respondeu dentro do tempo limite{host}.",
                "de" => $"Der Server hat innerhalb des Timeouts nicht geantwortet{host}.",
                "it" => $"Il server non ha risposto entro il timeout{host}.",
                _    => $"Le serveur n'a pas répondu dans les temps{host}."
            };
        }

        // Connection refused (active rejection by the server).
        if (Has("actively refused", "connection refused", "no connection could be made"))
        {
            return lang switch
            {
                "en" => $"Connection refused by the server{host}.",
                "es" => $"Conexión rechazada por el servidor{host}.",
                "pt" => $"Ligação recusada pelo servidor{host}.",
                "de" => $"Verbindung vom Server abgelehnt{host}.",
                "it" => $"Connessione rifiutata dal server{host}.",
                _    => $"Connexion refusée par le serveur{host}."
            };
        }

        // DNS resolution failure.
        if (Has("no such host", "name or service not known", "name resolution"))
        {
            return lang switch
            {
                "en" => $"Hostname could not be resolved{host}.",
                "es" => $"No se pudo resolver el nombre del host{host}.",
                "pt" => $"Não foi possível resolver o nome do host{host}.",
                "de" => $"Hostname konnte nicht aufgelöst werden{host}.",
                "it" => $"Impossibile risolvere il nome host{host}.",
                _    => $"Nom d'hôte introuvable{host}."
            };
        }

        // Network unreachable (often: VPN down, route missing).
        if (Has("network is unreachable", "no route to host", "network unreachable"))
        {
            return lang switch
            {
                "en" => $"Network unreachable — check your VPN/Wifi{host}.",
                "es" => $"Red inaccesible — comprueba tu VPN/Wifi{host}.",
                "pt" => $"Rede inacessível — verifica a tua VPN/Wifi{host}.",
                "de" => $"Netzwerk nicht erreichbar — VPN/Wifi prüfen{host}.",
                "it" => $"Rete non raggiungibile — controlla la tua VPN/Wifi{host}.",
                _    => $"Réseau injoignable — vérifie ton VPN/Wifi{host}."
            };
        }

        // TLS/SSL issues.
        if (Has("ssl", "tls", "certificate"))
        {
            return lang switch
            {
                "en" => $"TLS/certificate problem{host}: {raw}",
                "es" => $"Problema TLS/certificado{host}: {raw}",
                "pt" => $"Problema TLS/certificado{host}: {raw}",
                "de" => $"TLS-/Zertifikatsproblem{host}: {raw}",
                "it" => $"Problema TLS/certificato{host}: {raw}",
                _    => $"Problème TLS/certificat{host} : {raw}"
            };
        }

        // Fallback: keep raw message (still in English) but at least append URL for context.
        return string.IsNullOrEmpty(host) ? raw : $"{raw}{host}";
    }

private async Task RefreshSessionsAsync(string? preferSessionId, CancellationToken ct)
    {
        var list = await _api.ListSessionsAsync(ct, limit: 200, offset: 0);

        // Si aucune session: on en crÃ©e une
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
