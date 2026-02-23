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
    // Keys (LocalSettings compatibility)
    private const string KUseLocalLlm = "llm.useLocal";
    private const string KAutoStart = "llm.autoStartOnConnect";
    private const string KExePath = "llm.llamaExePath";
    private const string KModelPath = "llm.modelPath";
    private const string KHost = "llm.host";
    private const string KPort = "llm.port";
    private const string KModelId = "llm.modelId";
    private const string KExtraArgs = "llm.extraArgs";
    private const string KStartupTimeoutSec = "llm.startupTimeoutSec";
    private const string KLastSessionId = "chat.lastSessionId";

    public bool UseLocalLlm { get; set; } = false;
    public bool AutoStartOnConnect { get; set; } = true;

    public string LlamaExePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;

    /// <summary>OpenAI-compatible model id (what the client sends as "model").</summary>
    public string ModelId { get; set; } = ClientDefaults.LlmModel;

    public string ExtraArgs { get; set; } = "--ctx-size 4096";

    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Last active chat session id (chat-store).</summary>
    public string? LastSessionId { get; set; }

    public string LlmBaseUrl => $"http://{Host}:{Port}/v1";

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "client");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private sealed record FileDto(
        bool UseLocalLlm,
        bool AutoStartOnConnect,
        string LlamaExePath,
        string ModelPath,
        string Host,
        int Port,
        string ModelId,
        string ExtraArgs,
        int StartupTimeoutSeconds,
        string? LastSessionId);

    public static AppSettings Load()
    {
        var s = new AppSettings();

        // 1) Try packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            s.UseLocalLlm = (ls.Values[KUseLocalLlm] as bool?) ?? s.UseLocalLlm;
            s.AutoStartOnConnect = (ls.Values[KAutoStart] as bool?) ?? s.AutoStartOnConnect;

            s.LlamaExePath = (ls.Values[KExePath] as string) ?? s.LlamaExePath;
            s.ModelPath = (ls.Values[KModelPath] as string) ?? s.ModelPath;

            s.Host = (ls.Values[KHost] as string) ?? s.Host;
            s.Port = (ls.Values[KPort] as int?) ?? s.Port;

            s.ModelId = (ls.Values[KModelId] as string) ?? s.ModelId;
            s.ExtraArgs = (ls.Values[KExtraArgs] as string) ?? s.ExtraArgs;
            s.StartupTimeoutSeconds = (ls.Values[KStartupTimeoutSec] as int?) ?? s.StartupTimeoutSeconds;

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

            s.UseLocalLlm = dto.UseLocalLlm;
            s.AutoStartOnConnect = dto.AutoStartOnConnect;
            s.LlamaExePath = dto.LlamaExePath ?? "";
            s.ModelPath = dto.ModelPath ?? "";
            s.Host = string.IsNullOrWhiteSpace(dto.Host) ? "127.0.0.1" : dto.Host;
            s.Port = dto.Port <= 0 ? 1234 : dto.Port;
            s.ModelId = string.IsNullOrWhiteSpace(dto.ModelId) ? ClientDefaults.LlmModel : dto.ModelId;
            s.ExtraArgs = dto.ExtraArgs ?? "";
            s.StartupTimeoutSeconds = dto.StartupTimeoutSeconds <= 0 ? 60 : dto.StartupTimeoutSeconds;
            s.LastSessionId = string.IsNullOrWhiteSpace(dto.LastSessionId) ? null : dto.LastSessionId;

            return s;
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AppSettings.Load(file)", ex);
            return s;
        }
    }

    public void Save()
    {
        // 1) Try packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            ls.Values[KUseLocalLlm] = UseLocalLlm;
            ls.Values[KAutoStart] = AutoStartOnConnect;

            ls.Values[KExePath] = LlamaExePath ?? "";
            ls.Values[KModelPath] = ModelPath ?? "";

            ls.Values[KHost] = Host ?? "127.0.0.1";
            ls.Values[KPort] = Port;

            ls.Values[KModelId] = ModelId ?? ClientDefaults.LlmModel;
            ls.Values[KExtraArgs] = ExtraArgs ?? "";
            ls.Values[KStartupTimeoutSec] = StartupTimeoutSeconds;

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
                UseLocalLlm,
                AutoStartOnConnect,
                LlamaExePath ?? "",
                ModelPath ?? "",
                Host ?? "127.0.0.1",
                Port,
                ModelId ?? ClientDefaults.LlmModel,
                ExtraArgs ?? "",
                StartupTimeoutSeconds,
                string.IsNullOrWhiteSpace(LastSessionId) ? null : LastSessionId);

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AppSettings.Save(file)", ex);
        }
    }
}
