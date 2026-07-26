namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
// =========================
    // Local LLM (llama.cpp) - M6.1
    // =========================

    private readonly object _localGovernanceInitGate = new();
    private Task? _localGovernanceInitTask;
    private string? _lastLocalLlmStartupFailure;

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
        _lastLocalLlmStartupFailure = null;

        if (string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) || string.IsNullOrWhiteSpace(_appSettings.ModelPath))
        {
            _lastLocalLlmStartupFailure = LocalLlmText(
                "L'assistant local n'est pas configure : chemin du moteur ou du modele manquant.",
                "The local assistant is not configured: the engine or model path is missing.",
                "El asistente local no esta configurado: falta la ruta del motor o del modelo.",
                "O assistente local nao esta configurado: falta o caminho do motor ou do modelo.",
                "Der lokale Assistent ist nicht konfiguriert: Engine- oder Modellpfad fehlt.",
                "L'assistente locale non e configurato: manca il percorso del motore o del modello.",
                UiLang);
            return false;
        }

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.StatusText.Starting", () =>
            RunOnUiThreadAsync(() =>
                LocalLlmStatusText.Text = LocalLlmText("Demarrage de llama.cpp...", "Starting llama.cpp...", "Iniciando llama.cpp...", "A iniciar llama.cpp...", "llama.cpp wird gestartet...", "Avvio di llama.cpp...", UiLang)));

        var qualifiedProfileRefreshed = await RefreshLocalQualifiedProfileFromCurrentReferenceAsync(
            _appSettings,
            ct).ConfigureAwait(false);

        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);
        if (!ok)
        {
            _lastLocalLlmStartupFailure = LocalLlmText(
                "Le moteur local n'a pas pu demarrer. ",
                "The local engine could not start. ",
                "El motor local no pudo iniciarse. ",
                "O motor local nao conseguiu iniciar. ",
                "Die lokale Engine konnte nicht starten. ",
                "Il motore locale non e riuscito ad avviarsi. ",
                UiLang) + FormatLocalLlmStartFailureMessage(msg, UiLang);
        }

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.SyncUi", () =>
            RunOnUiThreadAsync(() =>
            {
                LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
                LocalLlmStatusText.Text = ok
                    ? LocalLlmText(
                        "Assistant local demarre.",
                        "Local assistant started.",
                        "Asistente local iniciado.",
                        "Assistente local iniciado.",
                        "Lokaler Assistent gestartet.",
                        "Assistente locale avviato.",
                        UiLang)
                    : (_lastLocalLlmStartupFailure ?? msg);
            }));

        if (ok
            && (qualifiedProfileRefreshed
                || await ShouldRunLocalLlmWarmupQualificationAsync(_appSettings, ct).ConfigureAwait(false)))
        {
            ok = await RunLocalLlmWarmupQualificationAsync(_appSettings, assistantMsg, ct).ConfigureAwait(false);
        }

        await TrySoftUiAsync("EnsureLocalLlmStartedFromSettingsAsync.ReflectEndpoint", () =>
            RunOnUiThreadAsync(() =>
            {
                LlmUrlBox.Text = _appSettings.LlmBaseUrl;
                LlmModelBox.Text = _appSettings.ModelId;
            }));

        return ok;
    }

    private static async Task<bool> RefreshLocalQualifiedProfileFromCurrentReferenceAsync(
        AppSettings settings,
        CancellationToken ct)
    {
        if (settings.QualifiedProfile is null)
            return false;

        var referenceItem = await WarmupProfileStore.FindProfileAsync(
            settings.QualifiedProfile.ProfileId,
            ct: ct).ConfigureAwait(false);
        var reference = referenceItem?.Candidate;
        if (reference is null)
            return false;

        if (!string.Equals(settings.QualifiedProfile.Runtime, reference.Runtime, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(settings.QualifiedProfile.ModelId, reference.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!RequalificationTriggerService.HasProfileConfigurationDrift(settings.QualifiedProfile, reference))
            return false;

        settings.QualifiedProfile = reference;
        settings.Save();
        return true;
    }

    private static async Task<bool> ShouldRunLocalLlmWarmupQualificationAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings.QualifiedProfile is null)
            return false;

        var registeredProfile = await WarmupProfileStore.FindProfileAsync(
            settings.QualifiedProfile.ProfileId,
            ct: ct).ConfigureAwait(false);
        if (registeredProfile is null)
            return false;

        var drift = RequalificationTriggerService.EvaluateProfileDrift(
            settings,
            registeredProfile.Candidate);
        if (drift.Required)
            return true;

        var read = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
            GovernanceArtifactStore.WarmupResultsFile,
            ct: ct).ConfigureAwait(false);
        if (read.Status != GovernanceArtifactReadStatus.Ok || read.Value is null)
            return true;

        var currentRuntime = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        var currentModelId = ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(settings.ModelPath is null ? null : System.IO.Path.GetFileName(settings.ModelPath))
            ?? settings.ModelId;

        var matching = read.Value.Items
            .Where(item =>
                string.Equals(item.ProfileId, settings.QualifiedProfile.ProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Runtime, currentRuntime, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ModelId, currentModelId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.At)
            .ToArray();

        if (matching.Any(static item => item.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded))
            return false;

        return matching.Length == 0;
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
                var loadingText = LocalLlmText("L'assistant local démarre le modèle...", "The local assistant is starting the model...", "El asistente local está iniciando el modelo...", "O assistente local está a iniciar o modelo...", "Der lokale Assistent startet das Modell...", "L'assistente locale sta avviando il modello...", UiLang);
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
                    var readyText = loadMs is > 0
                        ? LocalLlmText(
                            $"Assistant prêt en {loadMs.Value / 1000d:0.0}s. Je prépare la réponse...",
                            $"Assistant ready in {loadMs.Value / 1000d:0.0}s. Preparing the reply...",
                            $"Asistente listo en {loadMs.Value / 1000d:0.0}s. Preparando la respuesta...",
                            $"Assistente pronto em {loadMs.Value / 1000d:0.0}s. A preparar a resposta...",
                            $"Assistent bereit in {loadMs.Value / 1000d:0.0}s. Antwort wird vorbereitet...",
                            $"Assistente pronto in {loadMs.Value / 1000d:0.0}s. Preparazione della risposta...",
                            UiLang)
                        : LocalLlmText(
                            "Assistant prêt. Je prépare la réponse...",
                            "Assistant ready. Preparing the reply...",
                            "Asistente listo. Preparando la respuesta...",
                            "Assistente pronto. A preparar a resposta...",
                            "Assistent bereit. Antwort wird vorbereitet...",
                            "Assistente pronto. Preparazione della risposta...",
                            UiLang);
                    SetAssistantProgress(assistantMsg, readyText);
                }
                else if (!string.IsNullOrWhiteSpace(_lastLocalLlmStartupFailure))
                {
                    SetAssistantProgress(assistantMsg, _lastLocalLlmStartupFailure);
                }
            }));

        return ok;
    }

    private async Task<bool> WaitForExistingLocalLlmReadyAsync(ChatMessageItem? assistantMsg, CancellationToken ct)
    {
        await TrySoftUiAsync("WaitForExistingLocalLlmReadyAsync.Progress", () =>
            RunOnUiThreadAsync(() =>
            {
                var loadingText = LocalLlmText("L'assistant local charge le modèle...", "The local assistant is loading the model...", "El asistente local está cargando el modelo...", "O assistente local está a carregar o modelo...", "Der lokale Assistent lädt das Modell...", "L'assistente locale sta caricando il modello...", UiLang);
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

        if (await WarmupProfileStore.FindProfileAsync(
                settings.QualifiedProfile.ProfileId,
                ct: ct).ConfigureAwait(false) is null)
            return true;

        await TrySoftUiAsync("RunLocalLlmWarmupQualificationAsync.Progress", () =>
            RunOnUiThreadAsync(() =>
            {
                var checkingText = LocalLlmText("Vérification de l'assistant local avant réponse...", "Checking the local assistant before replying...", "Comprobando el asistente local antes de responder...", "A verificar o assistente local antes da resposta...", "Der lokale Assistent wird vor der Antwort geprüft...", "Verifica dell'assistente locale prima della risposta...", UiLang);
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
            if (await TryAllowSoftWarmupFallbackForActiveRequestAsync(result, settings, assistantMsg, ct).ConfigureAwait(false))
            {
                await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
                return true;
            }

            var failureText = BuildLocalLlmWarmupFailureText(result, settings, UiLang);
            _lastLocalLlmStartupFailure = failureText;
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
                            $"Verification echouee. Moteur precedent reactive ({rollbackBuild ?? "rollback"}).",
                            $"Check failed. Previous engine restored ({rollbackBuild ?? "rollback"}).",
                            $"La verificacion fallo. Motor anterior reactivado ({rollbackBuild ?? "rollback"}).",
                            $"A verificacao falhou. Motor anterior reativado ({rollbackBuild ?? "rollback"}).",
                            $"Pruefung fehlgeschlagen. Vorherige Engine wiederhergestellt ({rollbackBuild ?? "rollback"}).",
                            $"Verifica non riuscita. Motore precedente riattivato ({rollbackBuild ?? "rollback"}).",
                            UiLang);
                        SetAssistantProgress(assistantMsg, LocalLlmText(
                            "La verification de l'assistant local a echoue. Retour a la version precedente.",
                            "The local assistant check failed. Returning to the previous version.",
                            "La verificacion del asistente local fallo. Volviendo a la version anterior.",
                            "A verificacao do assistente local falhou. Regresso a versao anterior.",
                            "Die Pruefung des lokalen Assistenten ist fehlgeschlagen. Rueckkehr zur vorherigen Version.",
                            "La verifica dell'assistente locale non e riuscita. Ritorno alla versione precedente.",
                            UiLang));
                    }));

                ClientLog.Warn(
                    $"[RuntimeCompatibility] Qualification failed for '{runtimeId}'. "
                    + $"Rolled back active runtime to build '{rollbackBuild ?? "unknown"}'.");
            }
            else
            {
                await TrySoftUiAsync("RunLocalLlmWarmupQualificationAsync.FailureUi", () =>
                    RunOnUiThreadAsync(() =>
                    {
                        LocalLlmStatusText.Text = failureText;
                        SetAssistantProgress(assistantMsg, failureText);
                    }));
            }

            await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
            return false;
        }

        await RefreshLocalLlmGovernanceStatusAsync().ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryAllowSoftWarmupFallbackForActiveRequestAsync(
        Services.WarmupGateResult result,
        Services.AppSettings settings,
        ChatMessageItem? assistantMsg,
        CancellationToken ct)
    {
        if (result.Status != Services.WarmupGateStatus.FailFallback)
            return false;

        if (result.Reasons.Any(IsBlockingLocalLlmWarmupReason))
            return false;

        var probe = await Services.LlmEndpointProbe.GetModelsStatusAsync(
            settings.LlmBaseUrl,
            TimeSpan.FromSeconds(4),
            ct).ConfigureAwait(false);
        if (probe.Status != Services.LlmModelsStatus.Ok)
            return false;

        _lastLocalLlmStartupFailure = null;
        ClientLog.Warn(
            "[RuntimeCompatibility] Warmup qualification was below the strict target, "
            + "but the local model is running and responsive. Allowing this user request "
            + "and keeping the runtime available. Reasons: "
            + string.Join(";", result.Reasons));

        await TrySoftUiAsync("RunLocalLlmWarmupQualificationAsync.SoftFallbackUi", () =>
            RunOnUiThreadAsync(() =>
            {
                var readyText = LocalLlmText(
                    "Assistant prêt. Les performances sont surveillées, mais je prépare la réponse.",
                    "Assistant ready. Performance is being monitored, but the reply is being prepared.",
                    "Asistente listo. Se están vigilando las prestaciones, pero se prepara la respuesta.",
                    "Assistente pronto. O desempenho está a ser monitorizado, mas a resposta está a ser preparada.",
                    "Assistent bereit. Die Leistung wird überwacht, aber die Antwort wird vorbereitet.",
                    "Assistente pronto. Le prestazioni sono monitorate, ma la risposta è in preparazione.",
                    UiLang);
                LocalLlmStatusText.Text = readyText;
                SetAssistantProgress(assistantMsg, readyText);
            })).ConfigureAwait(false);

        return true;
    }

    private static bool IsBlockingLocalLlmWarmupReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        var normalized = reason.Trim().ToLowerInvariant();
        return normalized.StartsWith("blacklisted:", StringComparison.Ordinal)
            || normalized.StartsWith("hard_gate_", StringComparison.Ordinal)
            || normalized.StartsWith("warmup_run_failed", StringComparison.Ordinal)
            || normalized == "warmup_profile_missing"
            || normalized == "no_last_known_good";
    }

    private static string BuildLocalLlmWarmupFailureText(
        WarmupGateResult result,
        AppSettings settings,
        string? lang)
    {
        var details = BuildLocalLlmWarmupReasonSummary(result.Reasons, lang);
        var passText = $"{result.PassCount}";
        var profile = settings.QualifiedProfile?.ProfileId ?? settings.ModelId ?? "-";
        return LocalLlmText(
            $"Le modele local a demarre, mais il n'a pas passe la verification de stabilite ({passText} passage(s) valide(s)). Profil: {profile}. Detail: {details}. Essaie de relancer; si cela se repete, ouvre le diagnostic de l'assistant local ou choisis un profil plus leger.",
            $"The local model started, but it did not pass the stability check ({passText} valid pass(es)). Profile: {profile}. Detail: {details}. Try again; if it repeats, open local assistant diagnostics or choose a lighter profile.",
            $"El modelo local se inicio, pero no paso la comprobacion de estabilidad ({passText} paso(s) valido(s)). Perfil: {profile}. Detalle: {details}. Vuelve a intentarlo; si se repite, abre el diagnostico del asistente local o elige un perfil mas ligero.",
            $"O modelo local iniciou, mas nao passou a verificacao de estabilidade ({passText} passagem(ns) valida(s)). Perfil: {profile}. Detalhe: {details}. Tenta novamente; se se repetir, abre o diagnostico do assistente local ou escolhe um perfil mais leve.",
            $"Das lokale Modell wurde gestartet, hat die Stabilitaetspruefung aber nicht bestanden ({passText} gueltige(r) Lauf/Laeufe). Profil: {profile}. Detail: {details}. Versuche es erneut; wenn es wieder passiert, oeffne die Diagnose des lokalen Assistenten oder waehle ein leichteres Profil.",
            $"Il modello locale si e avviato, ma non ha superato il controllo di stabilita ({passText} passaggio/i valido/i). Profilo: {profile}. Dettaglio: {details}. Riprova; se succede ancora, apri la diagnostica dell'assistente locale o scegli un profilo piu leggero.",
            lang);
    }

    private static string BuildLocalLlmWarmupReasonSummary(IReadOnlyList<string> reasons, string? lang)
    {
        if (reasons.Count == 0)
        {
            return LocalLlmText(
                "aucune raison detaillee n'a ete fournie",
                "no detailed reason was provided",
                "no se proporciono ningun detalle",
                "nenhum detalhe foi fornecido",
                "kein Detailgrund wurde geliefert",
                "nessun dettaglio fornito",
                lang);
        }

        return string.Join("; ", reasons
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => TranslateLocalLlmWarmupReason(reason, lang))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4));
    }

    private static string TranslateLocalLlmWarmupReason(string reason, string? lang)
    {
        if (reason.StartsWith("warmup_tok_per_sec_below:", StringComparison.OrdinalIgnoreCase))
            return LocalLlmText("debit de generation trop bas (" + LastReasonSegment(reason) + " tok/s)", "generation speed too low (" + LastReasonSegment(reason) + " tok/s)", "velocidad de generacion demasiado baja (" + LastReasonSegment(reason) + " tok/s)", "velocidade de geracao demasiado baixa (" + LastReasonSegment(reason) + " tok/s)", "Generierung zu langsam (" + LastReasonSegment(reason) + " tok/s)", "velocita di generazione troppo bassa (" + LastReasonSegment(reason) + " tok/s)", lang);
        if (reason.StartsWith("warmup_ttft_ms_above:", StringComparison.OrdinalIgnoreCase))
            return LocalLlmText("premier token trop lent (" + LastReasonSegment(reason) + " ms)", "first token too slow (" + LastReasonSegment(reason) + " ms)", "primer token demasiado lento (" + LastReasonSegment(reason) + " ms)", "primeiro token demasiado lento (" + LastReasonSegment(reason) + " ms)", "erster Token zu langsam (" + LastReasonSegment(reason) + " ms)", "primo token troppo lento (" + LastReasonSegment(reason) + " ms)", lang);
        if (reason.StartsWith("warmup_load_ms_above:", StringComparison.OrdinalIgnoreCase))
            return LocalLlmText("chargement trop lent (" + LastReasonSegment(reason) + " ms)", "loading too slow (" + LastReasonSegment(reason) + " ms)", "carga demasiado lenta (" + LastReasonSegment(reason) + " ms)", "carregamento demasiado lento (" + LastReasonSegment(reason) + " ms)", "Laden zu langsam (" + LastReasonSegment(reason) + " ms)", "caricamento troppo lento (" + LastReasonSegment(reason) + " ms)", lang);
        if (reason.StartsWith("hard_gate_dxgi_budget_insufficient:", StringComparison.OrdinalIgnoreCase))
            return LocalLlmText("memoire GPU disponible insuffisante", "not enough available GPU memory", "memoria GPU disponible insuficiente", "memoria GPU disponivel insuficiente", "nicht genug verfuegbarer GPU-Speicher", "memoria GPU disponibile insufficiente", lang);
        if (reason.StartsWith("hard_gate_available_ram_insufficient:", StringComparison.OrdinalIgnoreCase))
            return LocalLlmText("RAM disponible insuffisante", "not enough available RAM", "RAM disponible insuficiente", "RAM disponivel insuficiente", "nicht genug verfuegbarer RAM", "RAM disponibile insufficiente", lang);

        return reason switch
        {
            "rollback_to_last_known_good" => LocalLlmText("retour au dernier profil connu comme stable", "returned to the last known stable profile", "vuelta al ultimo perfil estable conocido", "regresso ao ultimo perfil estavel conhecido", "Rueckkehr zum letzten bekannten stabilen Profil", "ritorno all'ultimo profilo stabile noto", lang),
            "no_last_known_good" => LocalLlmText("aucun profil de secours stable disponible", "no stable backup profile is available", "no hay perfil de respaldo estable", "nao ha perfil de contingencia estavel", "kein stabiles Ersatzprofil verfuegbar", "nessun profilo di ripiego stabile disponibile", lang),
            "insufficient_runs" => LocalLlmText("verification interrompue avant la fin", "check stopped before enough runs completed", "comprobacion interrumpida antes del final", "verificacao interrompida antes do fim", "Pruefung vor Abschluss unterbrochen", "verifica interrotta prima del completamento", lang),
            "warmup_run_failed" => LocalLlmText("un scenario de verification a echoue", "one check scenario failed", "un escenario de comprobacion fallo", "um cenario de verificacao falhou", "ein Pruefszenario ist fehlgeschlagen", "uno scenario di verifica non e riuscito", lang),
            _ => HumanizeRuntimeIdentifier(reason)
        };
    }

    private static string LastReasonSegment(string reason)
    {
        var parts = reason.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? reason : parts[^1];
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
        LocalLlmStatusText.Text = ok
            ? LocalLlmText(
                "Assistant local demarre.",
                "Local assistant started.",
                "Asistente local iniciado.",
                "Assistente local iniciado.",
                "Lokaler Assistent gestartet.",
                "Assistente locale avviato.",
                UiLang)
            : FormatLocalLlmStartFailureMessage(msg, UiLang);

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
            ClientLog.Exception("LocalLlm.Start.Click", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec du demarrage : ", "Start failed: ", "Error al iniciar: ", "Falha ao iniciar: ", "Start fehlgeschlagen: ", "Avvio non riuscito: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.Stop.Click", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'arret : ", "Stop failed: ", "Error al detener: ", "Falha ao parar: ", "Stop fehlgeschlagen: ", "Arresto non riuscito: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.Save.Click", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'enregistrement : ", "Save failed: ", "Error al guardar: ", "Falha ao guardar: ", "Speichern fehlgeschlagen: ", "Salvataggio non riuscito: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.BrowseExe", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec de la selection : ", "Browse failed: ", "Error al explorar: ", "Falha ao procurar: ", "Auswahl fehlgeschlagen: ", "Sfoglia non riuscita: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.BrowseModel", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec de la selection : ", "Browse failed: ", "Error al explorar: ", "Falha ao procurar: ", "Auswahl fehlgeschlagen: ", "Sfoglia non riuscita: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.ImportModel", ex);
            LocalLlmStatusText.Text = LocalLlmText("Echec de l'import : ", "Import failed: ", "Error de importacion: ", "Falha na importacao: ", "Import fehlgeschlagen: ", "Importazione non riuscita: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
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
            ClientLog.Exception("LocalLlm.OpenModelsFolder", ex);
            LocalLlmStatusText.Text = LocalLlmText("Impossible d'ouvrir le dossier : ", "Open folder failed: ", "Error al abrir la carpeta: ", "Falha ao abrir a pasta: ", "Ordner konnte nicht geoeffnet werden: ", "Impossibile aprire la cartella: ", UiLang) + FormatLocalLlmUserActionError(ex, UiLang);
        }
    }

    private static string FormatLocalLlmStartFailureMessage(string? message, string lang)
    {
        var normalized = (message ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return LocalLlmText("Aucun detail disponible.", "No detail available.", "Sin detalle disponible.", "Sem detalhe disponivel.", "Kein Detail verfuegbar.", "Nessun dettaglio disponibile.", lang);
        if (normalized.Contains("runtime not found") || normalized.Contains("runtime path") || normalized.Contains("server executable"))
            return LocalLlmText("Le moteur llama.cpp est introuvable. Verifie le chemin du moteur local.", "The llama.cpp engine was not found. Check the local engine path.", "No se encontro el motor llama.cpp. Revisa la ruta del motor local.", "O motor llama.cpp nao foi encontrado. Verifica o caminho do motor local.", "Die llama.cpp-Engine wurde nicht gefunden. Pruefe den Pfad zur lokalen Engine.", "Il motore llama.cpp non e stato trovato. Controlla il percorso del motore locale.", lang);
        if (normalized.Contains("model not found") || normalized.Contains("missing model") || normalized.Contains("model file not found"))
            return LocalLlmText("Le modele local est introuvable. Verifie le fichier .gguf selectionne.", "The local model was not found. Check the selected .gguf file.", "No se encontro el modelo local. Revisa el archivo .gguf seleccionado.", "O modelo local nao foi encontrado. Verifica o ficheiro .gguf selecionado.", "Das lokale Modell wurde nicht gefunden. Pruefe die ausgewaehlte .gguf-Datei.", "Il modello locale non e stato trovato. Controlla il file .gguf selezionato.", lang);
        if (normalized.Contains("blacklisted") || normalized.Contains("incompatible"))
            return LocalLlmText("Cette combinaison moteur/modele n'est pas validee. Ouvre le diagnostic de l'assistant local pour reverifier ou changer de modele.", "This engine/model combination is not approved. Open local assistant diagnostics to recheck or choose another model.", "Esta combinacion motor/modelo no esta validada. Abre el diagnostico del asistente local para revisar o cambiar de modelo.", "Esta combinacao motor/modelo nao esta validada. Abre o diagnostico do assistente local para reverificar ou trocar de modelo.", "Diese Engine/Modell-Kombination ist nicht freigegeben. Oeffne die Diagnose des lokalen Assistenten, um neu zu pruefen oder das Modell zu wechseln.", "Questa combinazione motore/modello non e validata. Apri la diagnostica dell'assistente locale per ricontrollare o cambiare modello.", lang);
        if (normalized.Contains("port") || normalized.Contains("already used"))
            return LocalLlmText("Le port de l'assistant local est déjà utilisé. Ferme l'autre processus ou change le port.", "The local assistant port is already in use. Close the other process or change the port.", "El puerto del asistente local ya está en uso. Cierra el otro proceso o cambia el puerto.", "A porta do assistente local já está em uso. Fecha o outro processo ou muda a porta.", "Der Port des lokalen Assistenten wird bereits verwendet. Beende den anderen Prozess oder aendere den Port.", "La porta dell'assistente locale è già in uso. Chiudi l'altro processo o cambia porta.", lang);
        if (normalized.Contains("did not become ready") || normalized.Contains("exited before readiness") || normalized.Contains("failed to start"))
            return LocalLlmText("Le moteur s'est lance mais n'a pas repondu correctement. Le log technique est conserve dans le dossier local SAAIA.", "The engine started but did not answer correctly. The technical log is kept in the local SAAIA folder.", "El motor arranco pero no respondio correctamente. El log tecnico queda en la carpeta local de SAAIA.", "O motor arrancou mas nao respondeu corretamente. O log tecnico fica na pasta local SAAIA.", "Die Engine wurde gestartet, hat aber nicht korrekt geantwortet. Das technische Log liegt im lokalen SAAIA-Ordner.", "Il motore si e avviato ma non ha risposto correttamente. Il log tecnico e nella cartella locale SAAIA.", lang);

        return LocalLlmText("Le moteur local a refuse de demarrer. Ouvre le diagnostic de l'assistant local pour voir le detail.", "The local engine refused to start. Open local assistant diagnostics for details.", "El motor local rechazo el arranque. Abre el diagnostico del asistente local para ver el detalle.", "O motor local recusou arrancar. Abre o diagnostico do assistente local para ver o detalhe.", "Die lokale Engine konnte nicht starten. Oeffne die Diagnose des lokalen Assistenten fuer Details.", "Il motore locale non si e avviato. Apri la diagnostica dell'assistente locale per i dettagli.", lang);
    }

}
