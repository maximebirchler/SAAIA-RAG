namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
// =========================
    // Local LLM (llama.cpp) - M6.1
    // =========================

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


    private async Task<bool> EnsureLocalLlmStartedFromSettingsAsync(CancellationToken ct)
    {
        _appSettings = AppSettings.Load();

        if (string.IsNullOrWhiteSpace(_appSettings.LlamaExePath) || string.IsNullOrWhiteSpace(_appSettings.ModelPath))
            return false;

        TrySoftUi("EnsureLocalLlmStartedFromSettingsAsync.StatusText.Starting", () => LocalLlmStatusText.Text = "Starting llama.cpp…");

        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        TrySoftUi("EnsureLocalLlmStartedFromSettingsAsync.SyncUi", () =>
        {
            LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
            LocalLlmStatusText.Text = msg;
        });

        // reflect URL/model
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = _appSettings.ModelId;

        return ok;
    }

    private async Task<bool> EnsureLocalLlmAwakeForRequestAsync(ChatMessageItem? assistantMsg, CancellationToken ct)
    {
        _appSettings = AppSettings.Load();
        if (!_appSettings.UseLocalLlm || !_appSettings.ManageLocalLlmProcess)
            return true;

        if (_llmProc.IsRunning)
            return true;

        TrySoftUi("EnsureLocalLlmAwakeForRequestAsync.Progress", () =>
        {
            SetAssistantProgress(assistantMsg, "Chargement du modele en cours...");
            LocalLlmStatusText.Text = "Chargement du modele en cours...";
        });

        var ok = await EnsureLocalLlmStartedFromSettingsAsync(ct);
        TrySoftUi("EnsureLocalLlmAwakeForRequestAsync.Done", () =>
        {
            if (ok)
                SetAssistantProgress(assistantMsg, "Modele pret. Je prepare la reponse...");
        });

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
