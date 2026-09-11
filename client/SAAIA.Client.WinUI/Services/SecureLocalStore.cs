using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Stockage local + secrets chiffrés (DPAPI).
///
/// Objectifs (CDC v2.7 / M5.2) :
/// - userId : GUID stable (non secret) utilisé pour le chat-store (scoping multi-user)
/// - serverApiKey : secret chiffré DPAPI (CurrentUser)
///
/// Important:
/// - En mode *packaged*, on peut utiliser ApplicationData.Current.LocalSettings.
/// - En mode *unpackaged* (pas d'identité MSIX), ApplicationData.Current peut throw.
///   Dans ce cas, on utilise un fichier local: %LOCALAPPDATA%\SAAIA\client\secure.json
/// </summary>
public static class SecureLocalStore
{
    private const string UserIdKey = "userId";
    private const string LegacyServerApiKeyPlainKey = "apiKey";
    private const string ServerApiKeyProtectedKey = "serverApiKeyProtected";
    private const string OpenAiApiKeyProtectedKey = "openAiApiKeyProtected";
    private const string RunPodApiKeyProtectedKey = "runPodApiKeyProtected";

    private static readonly object _lock = new();

    // Entropy optionnelle pour DPAPI (liée à l'app)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SAAIA.Client.WinUI|CDC-v2.7");

    private static string StoreDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "client");

    private static string StorePath => Path.Combine(StoreDir, "secure.json");

    private sealed record FileDto(
        string? UserId = null,
        string? ServerApiKeyProtected = null,
        string? LegacyApiKeyPlain = null,
        string? OpenAiApiKeyProtected = null,
        string? RunPodApiKeyProtected = null);

    public static string GetOrCreateUserId()
    {
        // 1) Packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            if (ls.Values.TryGetValue(UserIdKey, out var existingObj) && existingObj is string existing)
            {
                if (Guid.TryParse(existing, out _))
                    return existing;
            }

            var uid = Guid.NewGuid().ToString();
            ls.Values[UserIdKey] = uid;
            return uid;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"SecureLocalStore: LocalSettings unavailable, using file store. ({ex.GetType().Name}: {ex.Message})");
        }

        // 2) File store
        lock (_lock)
        {
            var dto = LoadFileDto();
            if (dto.UserId is string existing && Guid.TryParse(existing, out _))
                return existing;

            var uid = Guid.NewGuid().ToString();
            dto = dto with { UserId = uid, LegacyApiKeyPlain = null };
            SaveFileDto(dto);
            return uid;
        }
    }

    public static string? GetServerApiKey()
    {
        // 1) Packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;

            // Migration : si un apiKey en clair existe, on le migre vers DPAPI une seule fois.
            if (ls.Values.TryGetValue(LegacyServerApiKeyPlainKey, out var legacyObj) && legacyObj is string legacy && !string.IsNullOrWhiteSpace(legacy))
            {
                SetServerApiKey(legacy);
                ls.Values.Remove(LegacyServerApiKeyPlainKey);
                return legacy;
            }

            var fromLocalSettings = UnprotectFromLocalSettings(ls, ServerApiKeyProtectedKey);
            if (!string.IsNullOrWhiteSpace(fromLocalSettings))
                return fromLocalSettings;

            var fromFileStore = GetServerApiKeyFromFileStore();
            if (!string.IsNullOrWhiteSpace(fromFileStore))
            {
                ProtectToLocalSettings(ls, ServerApiKeyProtectedKey, fromFileStore);
                return fromFileStore;
            }

            return null;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"SecureLocalStore: LocalSettings unavailable, using file store. ({ex.GetType().Name}: {ex.Message})");
        }

        // 2) File store
        lock (_lock)
        {
            var dto = LoadFileDto();

            // Migration depuis legacy plain si jamais (on l'efface après)
            if (!string.IsNullOrWhiteSpace(dto.LegacyApiKeyPlain))
            {
                var migrated = ProtectString(dto.LegacyApiKeyPlain);
                dto = dto with { ServerApiKeyProtected = migrated, LegacyApiKeyPlain = null };
                SaveFileDto(dto);
            }

            return UnprotectString(dto.ServerApiKeyProtected);
        }
    }

    public static void SetServerApiKey(string? apiKey)
    {
        // 1) Packaged LocalSettings
        try
        {
            var ls = ApplicationData.Current.LocalSettings;
            ProtectToLocalSettings(ls, ServerApiKeyProtectedKey, apiKey);
            SetServerApiKeyInFileStore(apiKey);
            return;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"SecureLocalStore: LocalSettings unavailable, using file store. ({ex.GetType().Name}: {ex.Message})");
        }

        // 2) File store
        lock (_lock)
        {
            var dto = LoadFileDto();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                dto = dto with { ServerApiKeyProtected = null, LegacyApiKeyPlain = null };
                SaveFileDto(dto);
                return;
            }

            var protectedB64 = ProtectString(apiKey);
            dto = dto with { ServerApiKeyProtected = protectedB64, LegacyApiKeyPlain = null };
            SaveFileDto(dto);
        }
    }

    /// <summary>
    /// Returns the OpenAI development key protected for the current Windows user.
    /// Environment-variable overrides remain the provider factory's first choice.
    /// </summary>
    public static string? GetOpenAiApiKey()
        => GetProtectedSecret(
            OpenAiApiKeyProtectedKey,
            static dto => dto.OpenAiApiKeyProtected,
            static (dto, value) => dto with { OpenAiApiKeyProtected = value });

    public static void SetOpenAiApiKey(string? apiKey)
        => SetProtectedSecret(
            OpenAiApiKeyProtectedKey,
            apiKey,
            static (dto, value) => dto with { OpenAiApiKeyProtected = value });

    /// <summary>
    /// Returns the RunPod benchmark key protected for the current Windows user.
    /// </summary>
    public static string? GetRunPodApiKey()
        => GetProtectedSecret(
            RunPodApiKeyProtectedKey,
            static dto => dto.RunPodApiKeyProtected,
            static (dto, value) => dto with { RunPodApiKeyProtected = value });

    public static void SetRunPodApiKey(string? apiKey)
        => SetProtectedSecret(
            RunPodApiKeyProtectedKey,
            apiKey,
            static (dto, value) => dto with { RunPodApiKeyProtected = value });

    private static string? GetProtectedSecret(
        string localSettingsKey,
        Func<FileDto, string?> selectProtectedValue,
        Func<FileDto, string?, FileDto> updateProtectedValue)
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var fromLocalSettings = UnprotectFromLocalSettings(localSettings, localSettingsKey);
            if (!string.IsNullOrWhiteSpace(fromLocalSettings))
                return fromLocalSettings;

            var fromFileStore = GetProtectedSecretFromFileStore(
                selectProtectedValue,
                updateProtectedValue);
            if (!string.IsNullOrWhiteSpace(fromFileStore))
            {
                ProtectToLocalSettings(localSettings, localSettingsKey, fromFileStore);
                return fromFileStore;
            }

            return null;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"SecureLocalStore: LocalSettings unavailable, using file store. ({ex.GetType().Name}: {ex.Message})");
        }

        return GetProtectedSecretFromFileStore(selectProtectedValue, updateProtectedValue);
    }

    private static void SetProtectedSecret(
        string localSettingsKey,
        string? plainValue,
        Func<FileDto, string?, FileDto> updateProtectedValue)
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            ProtectToLocalSettings(localSettings, localSettingsKey, plainValue);
            SetProtectedSecretInFileStore(plainValue, updateProtectedValue);
            return;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"SecureLocalStore: LocalSettings unavailable, using file store. ({ex.GetType().Name}: {ex.Message})");
        }

        SetProtectedSecretInFileStore(plainValue, updateProtectedValue);
    }

    private static string? GetProtectedSecretFromFileStore(
        Func<FileDto, string?> selectProtectedValue,
        Func<FileDto, string?, FileDto> updateProtectedValue)
    {
        lock (_lock)
        {
            var dto = LoadFileDto();
            var protectedValue = selectProtectedValue(dto);
            try
            {
                return UnprotectString(protectedValue);
            }
            catch
            {
                SaveFileDto(updateProtectedValue(dto, null));
                return null;
            }
        }
    }

    private static void SetProtectedSecretInFileStore(
        string? plainValue,
        Func<FileDto, string?, FileDto> updateProtectedValue)
    {
        lock (_lock)
        {
            var dto = LoadFileDto();
            var protectedValue = string.IsNullOrWhiteSpace(plainValue)
                ? null
                : ProtectString(plainValue.Trim());
            SaveFileDto(updateProtectedValue(dto, protectedValue));
        }
    }

    private static string? GetServerApiKeyFromFileStore()
    {
        lock (_lock)
        {
            var dto = LoadFileDto();

            if (!string.IsNullOrWhiteSpace(dto.LegacyApiKeyPlain))
            {
                var migrated = ProtectString(dto.LegacyApiKeyPlain);
                dto = dto with { ServerApiKeyProtected = migrated, LegacyApiKeyPlain = null };
                SaveFileDto(dto);
            }

            return UnprotectString(dto.ServerApiKeyProtected);
        }
    }

    private static void SetServerApiKeyInFileStore(string? apiKey)
    {
        lock (_lock)
        {
            var dto = LoadFileDto();
            dto = string.IsNullOrWhiteSpace(apiKey)
                ? dto with { ServerApiKeyProtected = null, LegacyApiKeyPlain = null }
                : dto with { ServerApiKeyProtected = ProtectString(apiKey), LegacyApiKeyPlain = null };
            SaveFileDto(dto);
        }
    }

    private static void ProtectToLocalSettings(ApplicationDataContainer ls, string key, string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            ls.Values.Remove(key);
            return;
        }

        var protectedB64 = ProtectString(plain);
        ls.Values[key] = protectedB64;
    }

    private static string? UnprotectFromLocalSettings(ApplicationDataContainer ls, string key)
    {
        if (!ls.Values.TryGetValue(key, out var obj) || obj is not string b64 || string.IsNullOrWhiteSpace(b64))
            return null;

        try
        {
            return UnprotectString(b64);
        }
        catch
        {
            // Corruption / changement de contexte utilisateur : on “oublie” le secret.
            ls.Values.Remove(key);
            return null;
        }
    }

    private static string ProtectString(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? UnprotectString(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        var protectedBytes = Convert.FromBase64String(b64);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static FileDto LoadFileDto()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new FileDto();

            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<FileDto>(json) ?? new FileDto();
        }
        catch (Exception ex)
        {
            ClientLog.Exception("SecureLocalStore.Load(file)", ex);
            return new FileDto();
        }
    }

    private static void SaveFileDto(FileDto dto)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
        }
        catch (Exception ex)
        {
            ClientLog.Exception("SecureLocalStore.Save(file)", ex);
        }
    }
}
