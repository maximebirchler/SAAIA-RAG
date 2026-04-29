namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
// =========================
    // Local LLM (llama.cpp) - M6.1
    // =========================

    private readonly object _localGovernanceInitGate = new();
    private Task? _localGovernanceInitTask;

    private void LoadLocalLlmUiFromSettings()
    {
        try
        {
            _appSettings = AppSettings.Load();

            LocalLlmEnabledCheck.IsChecked = _appSettings.UseLocalLlm;
            LocalLlmAutoStartCheck.IsChecked = _appSettings.EagerLoad;

            LocalLlmExePathBox.Text = _appSettings.LlamaExePath;
            LocalLlmModelPathBox.Text = _appSettings.ModelPath;

            LocalLlmHostBox.Text = _appSettings.Host;
            LocalLlmPortBox.Text = _appSettings.Port.ToString();

            LocalLlmModelIdBox.Text = _appSettings.ModelId;
            LocalLlmExtraArgsBox.Text = _appSettings.ExtraArgs;

            LocalLlmStatusText.Text = _llmProc.IsRunning
                ? LocalLlmText("En cours.", "Running.", "En ejecucion.", "Em execucao.", "Laeuft.", "In esecuzione.", UiLang)
                : "";
            LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
            RefreshLocalLlmModelInfoText();
            _ = EnsureLocalGovernanceUiInitializedAsync();
        }
        catch
        {
            // ignore UI init failures
        }
    }

    private Task EnsureLocalGovernanceUiInitializedAsync()
    {
        lock (_localGovernanceInitGate)
        {
            if (_localGovernanceInitTask is { IsCompleted: false })
                return _localGovernanceInitTask;

            var initTask = InitializeLocalGovernanceUiCoreAsync();
            _localGovernanceInitTask = initTask;
            _ = initTask.ContinueWith(
                static (completedTask, state) =>
                {
                    var window = (MainWindow)state!;
                    lock (window._localGovernanceInitGate)
                    {
                        if (ReferenceEquals(window._localGovernanceInitTask, completedTask))
                            window._localGovernanceInitTask = null;
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return initTask;
        }
    }

    private async Task InitializeLocalGovernanceUiCoreAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            var hadQualifiedProfile = settings.QualifiedProfile is not null;
            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings).ConfigureAwait(false);

            if (!hadQualifiedProfile && settings.QualifiedProfile is not null)
                settings.Save();

            await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"Local governance init skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private AppSettings ReadLocalLlmSettingsFromUi()
    {
        var s = AppSettings.Load();

        s.UseLocalLlm = LocalLlmEnabledCheck.IsChecked == true;
        s.AutoStartOnConnect = LocalLlmAutoStartCheck.IsChecked == true;
        s.EagerLoad = LocalLlmAutoStartCheck.IsChecked == true;

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


    private async Task<bool> EnsureLocalLlmStartedFromSettingsAsync(CancellationToken ct, ChatMessageItem? assistantMsg = null)
    {
        _appSettings = AppSettings.Load();

        if (string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) || string.IsNullOrWhiteSpace(_appSettings.ModelPath))
            return false;

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.StatusText.Starting", () =>
            RunOnUiThreadAsync(() =>
                LocalLlmStatusText.Text = LocalLlmText("Demarrage de llama.cpp...", "Starting llama.cpp...", "Iniciando llama.cpp...", "A iniciar llama.cpp...", "llama.cpp wird gestartet...", "Avvio di llama.cpp...", UiLang)));

        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.SyncUi", () =>
            RunOnUiThreadAsync(() =>
            {
                LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
                LocalLlmStatusText.Text = msg;
            }));

        if (ok)
            ok = await RunLocalLlmWarmupQualificationAsync(_appSettings, assistantMsg, ct).ConfigureAwait(false);

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.ReflectEndpoint", () =>
            RunOnUiThreadAsync(() =>
            {
                LlmUrlBox.Text = _appSettings.LlmBaseUrl;
                LlmModelBox.Text = _appSettings.ModelId;
            }));

        return ok;
    }

    private async Task<bool> EnsureLocalLlmAwakeForRequestAsync(ChatMessageItem? assistantMsg, CancellationToken ct)
    {
        _appSettings = AppSettings.Load();
        if (!_appSettings.UseLocalLlm || !_appSettings.ManageLocalLlmProcess)
            return true;

        if (_llmProc.IsRunning)
        {
            var probe = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(4),
                ct).ConfigureAwait(false);

            if (probe.Status == LlmModelsStatus.Ok)
                return true;

            if (probe.Status == LlmModelsStatus.Loading)
            {
                var ready = await WaitForExistingLocalLlmReadyAsync(assistantMsg, ct).ConfigureAwait(false);
                if (ready)
                    return true;
            }

            ClientLog.Warn(
                $"[LlamaCpp] Managed runtime state was stale before request "
                + $"(status={probe.Status}, http={probe.HttpStatus}, error={probe.ErrorMessage}). Restarting.");
        }

        await TrySoftUiAsync("EnsureLocalLlmAwakeForRequestAsync.Progress", () =>
            RunOnUiThreadAsync(() =>
            {
                var loadingText = LocalLlmText("Chargement du modele en cours...", "Loading model...", "Cargando modelo...", "A carregar o modelo...", "Modell wird geladen...", "Caricamento modello...", UiLang);
                SetAssistantProgress(assistantMsg, loadingText);
                LocalLlmStatusText.Text = loadingText;
            }));

        var ok = await EnsureLocalLlmStartedFromSettingsAsync(ct, assistantMsg);
        await TrySoftUiAsync("EnsureLocalLlmAwakeForRequestAsync.Done", () =>
            RunOnUiThreadAsync(() =>
            {
                if (ok)
                {
                    var loadMs = _llmProc.LastStartupLoadMs;
                    SetAssistantProgress(
                        assistantMsg,
                        loadMs is > 0
                            ? $"Modele pret en {loadMs.Value / 1000d:0.0}s. Je prepare la reponse..."
                            : "Modele pret. Je prepare la reponse...");
                }
            }));

        return ok;
    }

    private async Task<bool> WaitForExistingLocalLlmReadyAsync(ChatMessageItem? assistantMsg, CancellationToken ct)
    {
        await TrySoftUiAsync("WaitForExistingLocalLlmReadyAsync.Progress", () =>
            RunOnUiThreadAsync(() =>
            {
                var loadingText = LocalLlmText("Chargement du modele en cours...", "Loading model...", "Cargando modelo...", "A carregar o modelo...", "Modell wird geladen...", "Caricamento modello...", UiLang);
                SetAssistantProgress(assistantMsg, loadingText);
                LocalLlmStatusText.Text = loadingText;
            }));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var probe = await LlmEndpointProbe.GetModelsStatusAsync(
                _appSettings.LlmBaseUrl,
                TimeSpan.FromSeconds(6),
                ct).ConfigureAwait(false);

            if (probe.Status == LlmModelsStatus.Ok)
                return true;

            if (probe.Status != LlmModelsStatus.Loading)
                return false;

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> RunLocalLlmWarmupQualificationAsync(
        AppSettings settings,
        ChatMessageItem? assistantMsg,
        CancellationToken ct)
    {
        if (settings.QualifiedProfile is null)
            return true;

        if (WarmupProfileStore.FindProfile(settings.QualifiedProfile.ProfileId) is null)
            return true;

        await TrySoftUiAsync("RunLocalLlmWarmupQualificationAsync.Progress", () =>
            RunOnUiThreadAsync(() =>
            {
                var checkingText = LocalLlmText("Verification de compatibilite en cours...", "Checking compatibility...", "Comprobando compatibilidad...", "A verificar a compatibilidade...", "Kompatibilitaet wird geprueft...", "Verifica compatibilita in corso...", UiLang);
                SetAssistantProgress(assistantMsg, checkingText);
                LocalLlmStatusText.Text = checkingText;
            }));

        var result = await WarmupGate.RunQualificationAsync(
            settings.QualifiedProfile,
            settings.LlmBaseUrl,
            settings.ModelId,
            root: null,
            observedLoadMs: _llmProc.LastStartupLoadMs,
            trigger: "client_runtime_start",
            ct: ct).ConfigureAwait(false);

        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        _ = LlamaCppReleaseDownloader.EnsureRuntimeTrackedForQualification(runtimeId, settings.LlamaExePath);
        if (result.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded)
            _ = LlamaCppReleaseDownloader.TryMarkRuntimeQualified(runtimeId);

        if (result.SelectedProfile is not null)
        {
            settings.QualifiedProfile = result.SelectedProfile;
            settings.Save();
        }

        if (result.Status is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback)
        {
            TrySoftUi("RunLocalLlmWarmupQualificationAsync.Stop", _llmProc.Stop);

            if (LlamaCppReleaseDownloader.TryRollbackPendingRuntime(runtimeId, out var rollbackExe, out var rollbackBuild)
                && !string.IsNullOrWhiteSpace(rollbackExe))
            {
                settings.LlamaExePath = rollbackExe;
                settings.Save();

                await TrySoftUiAsync("RunLocalLlmWarmupQualificationAsync.RollbackUi", () =>
                    RunOnUiThreadAsync(() =>
                    {
                        LocalLlmExePathBox.Text = rollbackExe;
                        LocalLlmStatusText.Text = LocalLlmText(
                            $"Qualification echouee. Runtime precedent reactive ({rollbackBuild ?? "rollback"}).",
                            $"Qualification failed. Previous runtime restored ({rollbackBuild ?? "rollback"}).",
                            $"La cualificacion fallo. Runtime anterior reactivado ({rollbackBuild ?? "rollback"}).",
                            $"A qualificacao falhou. Runtime anterior reativado ({rollbackBuild ?? "rollback"}).",
                            $"Qualifizierung fehlgeschlagen. Vorherige Runtime wiederhergestellt ({rollbackBuild ?? "rollback"}).",
                            $"Qualificazione non riuscita. Runtime precedente riattivato ({rollbackBuild ?? "rollback"}).",
                            UiLang);
                        SetAssistantProgress(assistantMsg, LocalLlmText(
                            "Compatibilite non validee. Retour au runtime precedent.",
                            "Compatibility not validated. Returning to the previous runtime.",
                            "Compatibilidad no validada. Volviendo al runtime anterior.",
                            "Compatibilidade nao validada. Regresso ao runtime anterior.",
                            "Kompatibilitaet nicht bestaetigt. Rueckkehr zur vorherigen Runtime.",
                            "Compatibilita non validata. Ritorno al runtime precedente.",
                            UiLang));
                    }));

                ClientLog.Warn(
                    $"[RuntimeCompatibility] Qualification failed for '{runtimeId}'. "
                    + $"Rolled back active runtime to build '{rollbackBuild ?? "unknown"}'.");
            }

            await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
            return false;
        }

        await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
        return true;
    }

    private async Task RefreshLocalLlmGovernanceStatusAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings).ConfigureAwait(false);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    LocalLlmStatusText.Text = LocalLlmRuntimeStatusService.ResolveDisplayMessage(
                        status,
                        _llmProc.IsRunning,
                        LocalLlmStatusText.Text) ?? "";
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                return;
            }

            await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            // non bloquant
        }
    }

    private async Task<bool> EnsureLocalLlmStartedAsync(CancellationToken ct)
    {
        _appSettings = ReadLocalLlmSettingsFromUi();
        _appSettings.Save();

        LocalLlmStatusText.Text = LocalLlmText("Demarrage de llama.cpp...", "Starting llama.cpp...", "Iniciando llama.cpp...", "A iniciar llama.cpp...", "llama.cpp wird gestartet...", "Avvio di llama.cpp...", UiLang);
        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
        LocalLlmStatusText.Text = msg;

        if (ok)
            ok = await RunLocalLlmWarmupQualificationAsync(_appSettings, null, ct).ConfigureAwait(false);

        await TrySoftUiAsync("EnsureLocalLlmStartedAsync.ReflectEndpoint", () =>
            RunOnUiThreadAsync(() =>
            {
                LlmUrlBox.Text = _appSettings.LlmBaseUrl;
                LlmModelBox.Text = _appSettings.ModelId;
            }));
        await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);

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
            LocalLlmStatusText.Text = LocalLlmText("Echec du demarrage : ", "Start failed: ", "Error al iniciar: ", "Falha ao iniciar: ", "Start fehlgeschlagen: ", "Avvio non riuscito: ", UiLang) + ex.Message;
        }
    }

    private void LocalLlmStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LocalLlmStatusText.Text = LocalLlmText("Arrete.", "Stopped.", "Detenido.", "Parado.", "Gestoppt.", "Fermato.", UiLang);
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'arret : ", "Stop failed: ", "Error al detener: ", "Falha ao parar: ", "Stop fehlgeschlagen: ", "Arresto non riuscito: ", UiLang) + ex.Message;
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

            LocalLlmStatusText.Text = LocalLlmText("Enregistre.", "Saved.", "Guardado.", "Guardado.", "Gespeichert.", "Salvato.", UiLang);
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'enregistrement : ", "Save failed: ", "Error al guardar: ", "Falha ao guardar: ", "Speichern fehlgeschlagen: ", "Salvataggio non riuscito: ", UiLang) + ex.Message;
        }
    }


    // =========================
    // Model library helpers (import + sha256)
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
                    LocalLlmModelInfoText.Text = LocalLlmText(
                        $"Taille : {sizeMb:0.0} MB (hors bibliotheque)",
                        $"Size: {sizeMb:0.0} MB (not in library)",
                        $"Tamano: {sizeMb:0.0} MB (fuera de la biblioteca)",
                        $"Tamanho: {sizeMb:0.0} MB (fora da biblioteca)",
                        $"Groesse: {sizeMb:0.0} MB (nicht in der Bibliothek)",
                        $"Dimensione: {sizeMb:0.0} MB (non nella libreria)",
                        UiLang);
                }
                else
                {
                    LocalLlmModelInfoText.Text = LocalLlmText("Fichier introuvable.", "File not found.", "Archivo no encontrado.", "Ficheiro nao encontrado.", "Datei nicht gefunden.", "File non trovato.", UiLang);
                }
                return;
            }

            var libSizeMb = info.SizeBytes / 1024d / 1024d;
            var shaShort = info.Sha256.Length > 12 ? info.Sha256.Substring(0, 12) : info.Sha256;
            LocalLlmModelInfoText.Text = LocalLlmText($"Bibliotheque : {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", $"Library: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", $"Biblioteca: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", $"Biblioteca: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", $"Bibliothek: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", $"Libreria: {info.Id} | {libSizeMb:0.0} MB | sha256 {shaShort}...", UiLang);
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
            LocalLlmStatusText.Text = LocalLlmText("Echec de la selection : ", "Browse failed: ", "Error al explorar: ", "Falha ao procurar: ", "Auswahl fehlgeschlagen: ", "Sfoglia non riuscita: ", UiLang) + ex.Message;
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
            LocalLlmStatusText.Text = LocalLlmText("Echec de la selection : ", "Browse failed: ", "Error al explorar: ", "Falha ao procurar: ", "Auswahl fehlgeschlagen: ", "Sfoglia non riuscita: ", UiLang) + ex.Message;
        }
    }

    private async void LocalLlmImportModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var src = (LocalLlmModelPathBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
            {
                LocalLlmStatusText.Text = LocalLlmText("Selectionne d'abord un fichier .gguf.", "Select a .gguf file first.", "Selecciona primero un archivo .gguf.", "Seleciona primeiro um ficheiro .gguf.", "Waehle zuerst eine .gguf-Datei aus.", "Seleziona prima un file .gguf.", UiLang);
                return;
            }

            LocalLlmStatusText.Text = LocalLlmText("Import du modele...", "Importing model...", "Importando modelo...", "A importar o modelo...", "Modell wird importiert...", "Importazione modello...", UiLang);
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
            LocalLlmStatusText.Text = LocalLlmText(
                $"Importe vers {ModelLibrary.ModelsDir}",
                $"Imported to {ModelLibrary.ModelsDir}",
                $"Importado en {ModelLibrary.ModelsDir}",
                $"Importado para {ModelLibrary.ModelsDir}",
                $"Importiert nach {ModelLibrary.ModelsDir}",
                $"Importato in {ModelLibrary.ModelsDir}",
                UiLang);
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'import : ", "Import failed: ", "Error de importacion: ", "Falha na importacao: ", "Import fehlgeschlagen: ", "Importazione non riuscita: ", UiLang) + ex.Message;
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
            LocalLlmStatusText.Text = LocalLlmText("Impossible d'ouvrir le dossier : ", "Open folder failed: ", "Error al abrir la carpeta: ", "Falha ao abrir a pasta: ", "Ordner konnte nicht geoeffnet werden: ", "Impossibile aprire la cartella: ", UiLang) + ex.Message;
        }
    }

}
