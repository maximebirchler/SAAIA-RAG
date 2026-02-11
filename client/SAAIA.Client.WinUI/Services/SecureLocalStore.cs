using System;
using System.Security.Cryptography;
using System.Text;

using Windows.Storage;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Stockage local + secrets chiffrés (DPAPI).
/// 
/// Objectifs (CDC v2.7 / M5.2) :
/// - userId : GUID stable (non secret) utilisé pour le chat-store (scoping multi-user)
/// - serverApiKey : secret chiffré DPAPI (CurrentUser)
/// 
/// Note :
/// - On garde ApplicationData.Current.LocalSettings comme “registry” local.
/// - Les secrets sont stockés sous forme base64(chiffrement DPAPI).
/// </summary>
public static class SecureLocalStore
{
    private const string UserIdKey = "userId";
    private const string LegacyServerApiKeyPlainKey = "apiKey";
    private const string ServerApiKeyProtectedKey = "serverApiKeyProtected";

    // Entropy optionnelle pour DPAPI (liée à l'app)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SAAIA.Client.WinUI|CDC-v2.7");

    public static string GetOrCreateUserId()
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

    public static string? GetServerApiKey()
    {
        var ls = ApplicationData.Current.LocalSettings;

        // Migration : si un apiKey en clair existe, on le migre vers DPAPI une seule fois.
        if (ls.Values.TryGetValue(LegacyServerApiKeyPlainKey, out var legacyObj) && legacyObj is string legacy && !string.IsNullOrWhiteSpace(legacy))
        {
            SetServerApiKey(legacy);
            ls.Values.Remove(LegacyServerApiKeyPlainKey);
        }

        return UnprotectStringFromLocalSettings(ServerApiKeyProtectedKey);
    }

    public static void SetServerApiKey(string? apiKey)
    {
        ProtectStringToLocalSettings(ServerApiKeyProtectedKey, apiKey);
    }

    private static void ProtectStringToLocalSettings(string key, string? plain)
    {
        var ls = ApplicationData.Current.LocalSettings;

        if (string.IsNullOrWhiteSpace(plain))
        {
            ls.Values.Remove(key);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        ls.Values[key] = Convert.ToBase64String(protectedBytes);
    }

    private static string? UnprotectStringFromLocalSettings(string key)
    {
        var ls = ApplicationData.Current.LocalSettings;
        if (!ls.Values.TryGetValue(key, out var obj) || obj is not string b64 || string.IsNullOrWhiteSpace(b64))
            return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(b64);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Corruption / changement de contexte utilisateur : on “oublie” le secret.
            ls.Values.Remove(key);
            return null;
        }
    }
}
