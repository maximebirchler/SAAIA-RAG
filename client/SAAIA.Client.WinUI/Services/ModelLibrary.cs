using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalModelInfo(
    string Id,
    string FileName,
    string FullPath,
    long SizeBytes,
    string Sha256,
    DateTime ImportedAtUtc);

/// <summary>
/// Simple local model library for GGUF files.
/// - Stores imported models under %LOCALAPPDATA%\SAAIA\Models
/// - Keeps a manifest (models.json) with sha256 + size for support/diagnostics
/// </summary>
internal static class ModelLibrary
{
    public static string ModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "Models");

    private static string ManifestPath => Path.Combine(ModelsDir, "models.json");

    private static readonly object _lock = new();
    private static List<LocalModelInfo>? _cache;

    private sealed record ManifestDto(List<LocalModelInfo> Models);

    private static string MT(string fr, string en, string es, string pt, string de, string it)
    {
        var lang = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);
        return lang switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
    }

    private static List<LocalModelInfo> LoadInternal()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new();

            var json = File.ReadAllText(ManifestPath);
            var dto = JsonSerializer.Deserialize<ManifestDto>(json);
            return dto?.Models ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void SaveInternal(List<LocalModelInfo> models)
    {
        Directory.CreateDirectory(ModelsDir);
        var dto = new ManifestDto(models);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ManifestPath, json);
    }

    private static List<LocalModelInfo> GetModels()
    {
        lock (_lock)
        {
            _cache ??= LoadInternal();
            return _cache;
        }
    }

    public static LocalModelInfo? TryGetByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        path = Path.GetFullPath(path);
        var models = GetModels();

        return models.FirstOrDefault(m =>
            string.Equals(Path.GetFullPath(m.FullPath), path, StringComparison.OrdinalIgnoreCase));
    }

    public static LocalModelInfo? TryGetBySha(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var models = GetModels();
        return models.FirstOrDefault(m => string.Equals(m.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<LocalModelInfo> ImportAsync(string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(MT("Chemin source manquant", "Missing sourcePath", "Falta sourcePath", "Falta sourcePath", "sourcePath fehlt", "sourcePath mancante"));

        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(MT("Fichier modele introuvable", "Model file not found", "Archivo de modelo no encontrado", "Ficheiro de modelo nao encontrado", "Modelldatei nicht gefunden", "File modello non trovato"), sourcePath);

        var ext = Path.GetExtension(sourcePath);
        if (!string.Equals(ext, ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(MT("Seuls les modeles .gguf sont pris en charge.", "Only .gguf models are supported.", "Solo se admiten modelos .gguf.", "Apenas modelos .gguf sao suportados.", "Nur .gguf-Modelle werden unterstuetzt.", "Sono supportati solo i modelli .gguf."));

        Directory.CreateDirectory(ModelsDir);

        // Copy to temp + compute sha256 in ONE pass (important for multi-GB files)
        var tmpPath = Path.Combine(ModelsDir, $"import_{Guid.NewGuid():N}.tmp");

        string sha;
        long bytes;

        try
        {
            using var sha256 = SHA256.Create();
            await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, useAsync: true);
            await using var dst = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 1024 * 1024, useAsync: true);

            var buffer = new byte[1024 * 1024];
            int read;
            bytes = 0;

            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                bytes += read;
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }

        // If we already imported the exact same model before, reuse it.
        var existing = TryGetBySha(sha);
        if (existing is not null)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return existing;
        }

        // Decide final name (avoid overwriting)
        var baseName = Path.GetFileName(sourcePath);
        var finalName = baseName;
        var finalPath = Path.Combine(ModelsDir, finalName);

        if (File.Exists(finalPath))
        {
            var stem = Path.GetFileNameWithoutExtension(baseName);
            var suffix = sha.Length >= 8 ? sha.Substring(0, 8) : sha;
            finalName = $"{stem}_{suffix}{ext}";
            finalPath = Path.Combine(ModelsDir, finalName);
        }

        File.Move(tmpPath, finalPath);

        var entry = new LocalModelInfo(
            Id: finalName,
            FileName: finalName,
            FullPath: finalPath,
            SizeBytes: bytes,
            Sha256: sha,
            ImportedAtUtc: DateTime.UtcNow);

        lock (_lock)
        {
            _cache ??= LoadInternal();
            _cache.Add(entry);
            SaveInternal(_cache);
        }

        return entry;
    }
}
