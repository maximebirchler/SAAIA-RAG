using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

internal static class UserPrefsStore
{
    // CDC v3.0: only language and style are persisted between conversations.
    // Mode remains on the runtime side and always falls back to auto.
    internal sealed record UserPrefs(int Version, string Language, string Style, string Mode);

    private sealed record UserPrefsDto(int Version, string? Language, string? Style, string? Mode);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "user-prefs.bin");

    public static UserPrefs Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return Default();

            var encrypted = File.ReadAllBytes(FilePath);
            if (encrypted.Length == 0)
                return Default();

            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            var dto = JsonSerializer.Deserialize<UserPrefsDto>(plain);
            if (dto is null)
                return Default();

            return Normalize(dto.Language, dto.Style, dto.Mode);
        }
        catch
        {
            return Default();
        }
    }

    public static void Save(UserPrefs prefs)
    {
        var normalized = Normalize(prefs?.Language, prefs?.Style, prefs?.Mode);
        var dto = new UserPrefsDto(normalized.Version, normalized.Language, normalized.Style, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, new JsonSerializerOptions { WriteIndented = false });
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static UserPrefs SaveLanguage(string? language)
    {
        var current = Load();
        var updated = Normalize(language, current.Style, current.Mode);
        Save(updated);
        return updated;
    }

    public static UserPrefs SaveStyle(string? style)
    {
        var current = Load();
        var updated = Normalize(current.Language, style, current.Mode);
        Save(updated);
        return updated;
    }

    public static UserPrefs SaveMode(string? mode)
    {
        var current = Load();
        var updated = Normalize(current.Language, current.Style, mode);
        Save(updated);
        return updated;
    }

    private static UserPrefs Normalize(string? language, string? style, string? mode)
        => new(3, NormalizeLanguage(language), NormalizeStyle(style), "auto");

    private static UserPrefs Default() => new(3, "fr", "auto", "auto");

    private static string NormalizeLanguage(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "en" => "en",
            "es" => "es",
            "pt" => "pt",
            "de" => "de",
            "it" => "it",
            _ => "fr"
        };
    }

    private static string NormalizeStyle(string? style)
    {
        var value = (style ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "plain" => "plain",
            "technical" => "technical",
            "executive" => "executive",
            _ => "auto"
        };
    }

    private static string NormalizeMode(string? mode)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "standard" => "standard",
            "strict" => "strict",
            _ => "auto"
        };
    }
}
