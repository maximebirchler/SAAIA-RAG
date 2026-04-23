using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Non-sensitive client settings (per-user).
/// In packaged context, we can use ApplicationData.Current.LocalSettings.
/// In unpackaged context (no package identity), ApplicationData.Current may throw;
/// we fall back to a JSON file under %LOCALAPPDATA%\SAAIA\client\settings.json.
/// Secrets (API keys) remain in SecureLocalStore (DPAPI).
/// </summary>
internal sealed class AppSettings
{
    // Core
    private const string KBackendUrl = "backend.url";
    private const string KShowAdvancedUi = "ui.showAdvanced";
    private const string KAutoConnect = "ui.autoConnect";
    private const string KUiLanguage = "ui.language";
    private const string KUiTheme = "ui.theme";
    private const string KProvisioningHash = "provisioning.hash";
    private const string KLlmAutoInstallAttemptedHash = "llm.autoInstall.attemptedHash";
    private const string KLlmMode = "llm.mode"; // embedded|docker|external

    // Keys (LocalSettings compatibility)
    private const string KUseLocalLlm = "llm.useLocal";
    private const string KManageLocalLlmProcess = "llm.manageProcess";
    private const string KAutoStart = "llm.autoStartOnConnect";
    private const string KExePath = "llm.llamaExePath";
    private const string KModelPath = "llm.modelPath";
    private const string KHost = "llm.host";
    private const string KPort = "llm.port";
    private const string KModelId = "llm.modelId";
    private const string KExtraArgs = "llm.extraArgs";
    private const string KStartupTimeoutSec = "llm.startupTimeoutSec";

    // Safe tuning (must not break RAG)
    private const string KLlmTemperature = "llm.temperature";
    private const string KLlmMaxOutputTokens = "llm.maxOutputTokens";
    private const string KRagQualityPreset = "rag.qualityPreset";

    private const string KLastSessionId = "chat.lastSessionId";

    // Fine-grained LLM tuning (CDC v3.1 LLM-010 / LLM-011)
    private const string KUbatchSize   = "llm.ubatchSize";
    private const string KThreadsBatch = "llm.threadsBatch";
    private const string KFlashAttn    = "llm.flashAttn"; // "auto"|"on"|"off"

    public string BackendUrl { get; set; } = ClientDefaults.BackendBaseUrl;

    /// <summary>
    /// UI: when false, the app hides all advanced/provisioning-risky fields.
    /// This is the default for end users.
    /// </summary>
    public bool ShowAdvancedUi { get; set; } = false;

    /// <summary>
    /// UI: auto connect at startup when provisioning is present.
    /// </summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// UI language for the WinUI shell and built-in help commands.
    /// Supported: fr|en|es|pt|de|it
    /// </summary>
    public string UiLanguage { get; set; } = "fr";

    /// <summary>
    /// UI appearance mode for the WinUI shell.
    /// Supported: system|dark|light
    /// </summary>
    public string UiTheme { get; set; } = "dark";

    /// <summary>
    /// LLM enable/disable (safe). If false: app runs in degraded "search-only" mode.
    /// Note: historically named UseLocalLlm.
    /// </summary>
    public bool UseLocalLlm { get; set; } = true;

    /// <summary>
    /// How the client obtains the LLM endpoint:
    ///  - embedded: client downloads model and starts a local llama.cpp server (recommended)
    ///  - docker: endpoint is provided by Docker/script (dev/test)
    ///  - external: endpoint is managed externally (IT/service)
    /// </summary>
    public string LlmMode { get; set; } = "embedded";

    /// <summary>
    /// Advanced: whether the client should manage a local llama.cpp process.
    /// Default false (use an already-running OpenAI-compatible endpoint).
    /// </summary>
    public bool ManageLocalLlmProcess { get; set; } = true;

    public bool AutoStartOnConnect { get; set; } = true;

    public string LlamaExePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;

    /// <summary>OpenAI-compatible model id (what the client sends as "model").</summary>
    public string ModelId { get; set; } = ClientDefaults.LlmModel;

    public string ExtraArgs { get; set; } = "--ctx-size 3072"; // CDC v3.1 LLM-007: 3072 ctx default

    /// <summary>Micro-batch size for eval scheduling (CDC v3.1 LLM-010). Default 256.</summary>
    public int UbatchSize { get; set; } = 256;

    /// <summary>Thread count for batch processing (CDC v3.1 LLM-010). Default 6.</summary>
    public int ThreadsBatch { get; set; } = 6;

    /// <summary>
    /// Flash attention mode (CDC v3.1 LLM-011).
    /// null = auto (enabled automatically for CUDA builds), true = always on, false = disabled.
    /// </summary>
    public bool? FlashAttn { get; set; } = null;

    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Session operating mode. Not persisted.</summary>
    public string ActiveMode { get; set; } = "auto";

    /// <summary>Safe tuning: temperature (0..1). Higher => more creative.</summary>
    public double LlmTemperature { get; set; } = 0.2;

    /// <summary>Safe tuning: max tokens for the final answer.</summary>
    public int LlmMaxOutputTokens { get; set; } = 900;

    /// <summary>Safe tuning: controls retrieval depth (quick|balanced|deep).</summary>
    public string RagQualityPreset { get; set; } = "balanced";

    /// <summary>Provisioning file hash (to avoid re-applying the same provisioning every startup).</summary>
    public string? ProvisioningHash { get; set; }

    /// <summary>Internal: prevents re-running LLM auto-install on every startup.</summary>
    public string? LlmAutoInstallAttemptedHash { get; set; }

    /// <summary>Last active chat session id (chat-store).</summary>
    public string? LastSessionId { get; set; }

    public string LlmBaseUrl => $"http://{Host}:{Port}/v1";

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "client");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private sealed record FileDto(
        string BackendUrl,
        bool ShowAdvancedUi,
        bool AutoConnect,
        string UiLanguage,
        string UiTheme,
        string? ProvisioningHash,
        string? LlmAutoInstallAttemptedHash,
        string LlmMode,
        bool UseLocalLlm,
        bool ManageLocalLlmProcess,
        bool AutoStartOnConnect,
        string LlamaExePath,
        string ModelPath,
        string Host,
        int Port,
        string ModelId,
        string ExtraArgs,
        int UbatchSize,
        int ThreadsBatch,
        bool? FlashAttn,
        int StartupTimeoutSeconds,
        double LlmTemperature,
        int LlmMaxOutputTokens,
        string RagQualityPreset,
        string? LastSessionId);

    public static AppSettings Load()
    {
        var s = new AppSettings();

        // 1) Try packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            s.BackendUrl = (ls.Values[KBackendUrl] as string) ?? s.BackendUrl;
            s.ShowAdvancedUi = (ls.Values[KShowAdvancedUi] as bool?) ?? s.ShowAdvancedUi;
            s.AutoConnect = (ls.Values[KAutoConnect] as bool?) ?? s.AutoConnect;
            s.UiLanguage = (ls.Values[KUiLanguage] as string) ?? s.UiLanguage;
            s.UiTheme = NormalizeUiTheme(ls.Values[KUiTheme] as string);
            s.ProvisioningHash = (ls.Values[KProvisioningHash] as string) ?? s.ProvisioningHash;
            s.LlmAutoInstallAttemptedHash = (ls.Values[KLlmAutoInstallAttemptedHash] as string) ?? s.LlmAutoInstallAttemptedHash;
            s.LlmMode = (ls.Values[KLlmMode] as string) ?? s.LlmMode;

            s.UseLocalLlm = (ls.Values[KUseLocalLlm] as bool?) ?? s.UseLocalLlm;
            s.ManageLocalLlmProcess = (ls.Values[KManageLocalLlmProcess] as bool?) ?? s.ManageLocalLlmProcess;
            s.AutoStartOnConnect = (ls.Values[KAutoStart] as bool?) ?? s.AutoStartOnConnect;

            s.LlamaExePath = (ls.Values[KExePath] as string) ?? s.LlamaExePath;
            s.ModelPath = (ls.Values[KModelPath] as string) ?? s.ModelPath;

            s.Host = (ls.Values[KHost] as string) ?? s.Host;
            s.Port = (ls.Values[KPort] as int?) ?? s.Port;

            s.ModelId = (ls.Values[KModelId] as string) ?? s.ModelId;
            s.ExtraArgs = (ls.Values[KExtraArgs] as string) ?? s.ExtraArgs;
            s.StartupTimeoutSeconds = (ls.Values[KStartupTimeoutSec] as int?) ?? s.StartupTimeoutSeconds;

            s.UbatchSize   = (ls.Values[KUbatchSize]   as int?) ?? s.UbatchSize;
            s.ThreadsBatch = (ls.Values[KThreadsBatch] as int?) ?? s.ThreadsBatch;
            s.FlashAttn    = ParseFlashAttn(ls.Values[KFlashAttn] as string);

            s.LlmTemperature = (ls.Values[KLlmTemperature] as double?) ?? s.LlmTemperature;
            s.LlmMaxOutputTokens = (ls.Values[KLlmMaxOutputTokens] as int?) ?? s.LlmMaxOutputTokens;
            s.RagQualityPreset = (ls.Values[KRagQualityPreset] as string) ?? s.RagQualityPreset;

            s.LastSessionId = (ls.Values[KLastSessionId] as string) ?? s.LastSessionId;

            return s;
        }
        catch (Exception ex)
        {
            // Expected in unpackaged context.
            ClientLog.Warn($"AppSettings: LocalSettings unavailable, falling back to file. ({ex.GetType().Name}: {ex.Message})");
        }

        // 2) File fallback
        try
        {
            if (!File.Exists(SettingsPath)) return s;

            var json = File.ReadAllText(SettingsPath);
            var dto = JsonSerializer.Deserialize<FileDto>(json);
            if (dto is null) return s;

            bool Has(string name) => json.IndexOf('"' + name + '"', StringComparison.OrdinalIgnoreCase) >= 0;

            if (Has(nameof(FileDto.BackendUrl)))
                s.BackendUrl = string.IsNullOrWhiteSpace(dto.BackendUrl) ? s.BackendUrl : dto.BackendUrl;

            if (Has(nameof(FileDto.ShowAdvancedUi)))
                s.ShowAdvancedUi = dto.ShowAdvancedUi;

            if (Has(nameof(FileDto.AutoConnect)))
                s.AutoConnect = dto.AutoConnect;

            if (Has(nameof(FileDto.UiLanguage)))
                s.UiLanguage = string.IsNullOrWhiteSpace(dto.UiLanguage) ? s.UiLanguage : dto.UiLanguage;

            if (Has(nameof(FileDto.UiTheme)))
                s.UiTheme = NormalizeUiTheme(dto.UiTheme);

            if (Has(nameof(FileDto.ProvisioningHash)))
                s.ProvisioningHash = string.IsNullOrWhiteSpace(dto.ProvisioningHash) ? null : dto.ProvisioningHash;

            if (Has(nameof(FileDto.LlmAutoInstallAttemptedHash)))
                s.LlmAutoInstallAttemptedHash = string.IsNullOrWhiteSpace(dto.LlmAutoInstallAttemptedHash) ? null : dto.LlmAutoInstallAttemptedHash;

            if (Has(nameof(FileDto.LlmMode)))
                s.LlmMode = string.IsNullOrWhiteSpace(dto.LlmMode) ? "embedded" : dto.LlmMode;

            if (Has(nameof(FileDto.UseLocalLlm)))
                s.UseLocalLlm = dto.UseLocalLlm;

            if (Has(nameof(FileDto.ManageLocalLlmProcess)))
                s.ManageLocalLlmProcess = dto.ManageLocalLlmProcess;
            s.AutoStartOnConnect = dto.AutoStartOnConnect;
            s.LlamaExePath = dto.LlamaExePath ?? "";
            s.ModelPath = dto.ModelPath ?? "";
            s.Host = string.IsNullOrWhiteSpace(dto.Host) ? "127.0.0.1" : dto.Host;
            s.Port = dto.Port <= 0 ? 1234 : dto.Port;
            s.ModelId = string.IsNullOrWhiteSpace(dto.ModelId) ? ClientDefaults.LlmModel : dto.ModelId;
            s.ExtraArgs = dto.ExtraArgs ?? "";

            if (Has(nameof(FileDto.UbatchSize)))
                s.UbatchSize = dto.UbatchSize > 0 ? dto.UbatchSize : 256;
            if (Has(nameof(FileDto.ThreadsBatch)))
                s.ThreadsBatch = dto.ThreadsBatch > 0 ? dto.ThreadsBatch : 6;
            if (Has(nameof(FileDto.FlashAttn)))
                s.FlashAttn = dto.FlashAttn;

            s.StartupTimeoutSeconds = dto.StartupTimeoutSeconds <= 0 ? 60 : dto.StartupTimeoutSeconds;

            if (Has(nameof(FileDto.LlmTemperature)))
                s.LlmTemperature = dto.LlmTemperature;
            if (Has(nameof(FileDto.LlmMaxOutputTokens)))
                s.LlmMaxOutputTokens = dto.LlmMaxOutputTokens;
            if (Has(nameof(FileDto.RagQualityPreset)))
                s.RagQualityPreset = string.IsNullOrWhiteSpace(dto.RagQualityPreset) ? "balanced" : dto.RagQualityPreset;
            s.LastSessionId = string.IsNullOrWhiteSpace(dto.LastSessionId) ? null : dto.LastSessionId;

            return s;
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AppSettings.Load(file)", ex);
            return s;
        }
    }

    public static string NormalizeUiTheme(string? theme)
    {
        var value = (theme ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "system" => "system",
            "light" => "light",
            _ => "dark"
        };
    }

    public static string NormalizeActiveMode(string? mode)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "standard" => "standard",
            "strict" => "strict",
            _ => "auto"
        };
    }

    public void Save()
    {
        // 1) Try packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            ls.Values[KBackendUrl] = BackendUrl ?? ClientDefaults.BackendBaseUrl;
            ls.Values[KShowAdvancedUi] = ShowAdvancedUi;
            ls.Values[KAutoConnect] = AutoConnect;
            ls.Values[KUiLanguage] = string.IsNullOrWhiteSpace(UiLanguage) ? "fr" : UiLanguage;
            ls.Values[KUiTheme] = NormalizeUiTheme(UiTheme);
            if (string.IsNullOrWhiteSpace(ProvisioningHash)) ls.Values.Remove(KProvisioningHash);
            else ls.Values[KProvisioningHash] = ProvisioningHash;

            if (string.IsNullOrWhiteSpace(LlmAutoInstallAttemptedHash)) ls.Values.Remove(KLlmAutoInstallAttemptedHash);
            else ls.Values[KLlmAutoInstallAttemptedHash] = LlmAutoInstallAttemptedHash;

            ls.Values[KLlmMode] = string.IsNullOrWhiteSpace(LlmMode) ? "embedded" : LlmMode;

            ls.Values[KUseLocalLlm] = UseLocalLlm;
            ls.Values[KManageLocalLlmProcess] = ManageLocalLlmProcess;
            ls.Values[KAutoStart] = AutoStartOnConnect;

            ls.Values[KExePath] = LlamaExePath ?? "";
            ls.Values[KModelPath] = ModelPath ?? "";

            ls.Values[KHost] = Host ?? "127.0.0.1";
            ls.Values[KPort] = Port;

            ls.Values[KModelId] = ModelId ?? ClientDefaults.LlmModel;
            ls.Values[KExtraArgs] = ExtraArgs ?? "";
            ls.Values[KStartupTimeoutSec] = StartupTimeoutSeconds;

            ls.Values[KUbatchSize]   = UbatchSize;
            ls.Values[KThreadsBatch] = ThreadsBatch;
            if (FlashAttn is null) ls.Values.Remove(KFlashAttn);
            else ls.Values[KFlashAttn] = FlashAttn.Value ? "on" : "off";

            ls.Values.Remove("llm.strictMode");
            ls.Values[KLlmTemperature] = LlmTemperature;
            ls.Values[KLlmMaxOutputTokens] = LlmMaxOutputTokens;
            ls.Values[KRagQualityPreset] = RagQualityPreset ?? "balanced";

            if (string.IsNullOrWhiteSpace(LastSessionId))
                ls.Values.Remove(KLastSessionId);
            else
                ls.Values[KLastSessionId] = LastSessionId;

            return;
        }
        catch (Exception ex)
        {
            // Expected in unpackaged context.
            ClientLog.Warn($"AppSettings: LocalSettings unavailable, writing to file. ({ex.GetType().Name}: {ex.Message})");
        }

        // 2) File fallback
        try
        {
            Directory.CreateDirectory(SettingsDir);

            var dto = new FileDto(
                BackendUrl ?? ClientDefaults.BackendBaseUrl,
                ShowAdvancedUi,
                AutoConnect,
                string.IsNullOrWhiteSpace(UiLanguage) ? "fr" : UiLanguage,
                NormalizeUiTheme(UiTheme),
                string.IsNullOrWhiteSpace(ProvisioningHash) ? null : ProvisioningHash,
                string.IsNullOrWhiteSpace(LlmAutoInstallAttemptedHash) ? null : LlmAutoInstallAttemptedHash,
                string.IsNullOrWhiteSpace(LlmMode) ? "embedded" : LlmMode,
                UseLocalLlm,
                ManageLocalLlmProcess,
                AutoStartOnConnect,
                LlamaExePath ?? "",
                ModelPath ?? "",
                Host ?? "127.0.0.1",
                Port,
                ModelId ?? ClientDefaults.LlmModel,
                ExtraArgs ?? "",
                UbatchSize,
                ThreadsBatch,
                FlashAttn,
                StartupTimeoutSeconds,
                LlmTemperature,
                LlmMaxOutputTokens,
                RagQualityPreset ?? "balanced",
                string.IsNullOrWhiteSpace(LastSessionId) ? null : LastSessionId);

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AppSettings.Save(file)", ex);
        }
    }

    // --- Internal helpers ---

    /// <summary>
    /// Parses the persisted flash-attn string back to a nullable bool.
    /// Returns null (= auto) for any unrecognised value or when the key is absent.
    /// </summary>
    private static bool? ParseFlashAttn(string? val) => val switch
    {
        "on"  => true,
        "off" => false,
        _     => null   // absent or "auto"
    };

    // --- Compatibility helpers (UI / older patches) ---

    // Aliases used by some UI code (avoid breaking when property names evolve).
    public double Temperature { get => LlmTemperature; set => LlmTemperature = value; }
    public int MaxOutputTokens { get => LlmMaxOutputTokens; set => LlmMaxOutputTokens = value; }
    public string LlmHost { get => Host; set => Host = value; }
    public int LlmPort { get => Port; set => Port = value; }

    public AppSettings Clone() => new AppSettings
    {
        BackendUrl = this.BackendUrl,
        ShowAdvancedUi = this.ShowAdvancedUi,
        AutoConnect = this.AutoConnect,
        UiLanguage = this.UiLanguage,
        UiTheme = this.UiTheme,

        UseLocalLlm = this.UseLocalLlm,
        LlmMode = this.LlmMode,
        ManageLocalLlmProcess = this.ManageLocalLlmProcess,
        AutoStartOnConnect = this.AutoStartOnConnect,
        LlamaExePath = this.LlamaExePath,
        ModelPath = this.ModelPath,
        Host = this.Host,
        Port = this.Port,
        ModelId = this.ModelId,
        ExtraArgs = this.ExtraArgs,
        UbatchSize = this.UbatchSize,
        ThreadsBatch = this.ThreadsBatch,
        FlashAttn = this.FlashAttn,
        StartupTimeoutSeconds = this.StartupTimeoutSeconds,

        ActiveMode = this.ActiveMode,
        LlmTemperature = this.LlmTemperature,
        LlmMaxOutputTokens = this.LlmMaxOutputTokens,
        RagQualityPreset = this.RagQualityPreset,

        ProvisioningHash = this.ProvisioningHash,
        LlmAutoInstallAttemptedHash = this.LlmAutoInstallAttemptedHash,
        LastSessionId = this.LastSessionId
    };

    public void CopyFrom(AppSettings other)
    {
        if (other is null) return;

        BackendUrl = other.BackendUrl;
        ShowAdvancedUi = other.ShowAdvancedUi;
        AutoConnect = other.AutoConnect;
        UiLanguage = other.UiLanguage;
        UiTheme = other.UiTheme;

        UseLocalLlm = other.UseLocalLlm;
        LlmMode = other.LlmMode;
        ManageLocalLlmProcess = other.ManageLocalLlmProcess;
        AutoStartOnConnect = other.AutoStartOnConnect;
        LlamaExePath = other.LlamaExePath;
        ModelPath = other.ModelPath;
        Host = other.Host;
        Port = other.Port;
        ModelId = other.ModelId;
        ExtraArgs = other.ExtraArgs;
        UbatchSize = other.UbatchSize;
        ThreadsBatch = other.ThreadsBatch;
        FlashAttn = other.FlashAttn;
        StartupTimeoutSeconds = other.StartupTimeoutSeconds;

        ActiveMode = NormalizeActiveMode(other.ActiveMode);
        LlmTemperature = other.LlmTemperature;
        LlmMaxOutputTokens = other.LlmMaxOutputTokens;
        RagQualityPreset = other.RagQualityPreset;

        ProvisioningHash = other.ProvisioningHash;
        LlmAutoInstallAttemptedHash = other.LlmAutoInstallAttemptedHash;
        LastSessionId = other.LastSessionId;
    }

}
