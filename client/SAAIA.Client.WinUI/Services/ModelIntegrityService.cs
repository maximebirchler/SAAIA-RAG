using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record ModelIntegrityVerificationResult(
    bool Blocked,
    string Reason,
    string? UserMessage = null,
    string? QuarantinedPath = null,
    string? ExpectedSha256 = null,
    string? ActualSha256 = null);

internal sealed record AcquisitionLogArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<AcquisitionLogItem> Items);

internal sealed record AcquisitionLogItem(
    DateTimeOffset Timestamp,
    string Component,
    string? Source,
    string? ExpectedSha256,
    string? ActualSha256,
    bool Verified,
    string Action,
    string? Reason,
    string? OriginalPath,
    string? QuarantinedPath);

internal static class ModelIntegrityService
{
    public static string GetQuarantineUserMessage(string? uiLanguage = null)
        => ClientUiText.Get("status.model_quarantined", uiLanguage);

    public static async Task<ModelIntegrityVerificationResult> VerifyModelAsync(
        AppSettings settings,
        string? root = null,
        CancellationToken ct = default)
    {
        var modelPath = (settings.ModelPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new ModelIntegrityVerificationResult(false, "model_missing_or_not_set");

        var expectedSha = await ResolveExpectedChecksumAsync(settings, root, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedSha))
            return new ModelIntegrityVerificationResult(false, "checksum_unavailable");

        var actualSha = await ComputeSha256HexAsync(modelPath, ct).ConfigureAwait(false);
        if (string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            return new ModelIntegrityVerificationResult(
                false,
                "checksum_verified",
                ExpectedSha256: expectedSha,
                ActualSha256: actualSha);
        }

        var quarantinedPath = QuarantinePath(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(quarantinedPath)!);
        if (File.Exists(quarantinedPath))
            File.Delete(quarantinedPath);
        File.Move(modelPath, quarantinedPath);

        await AppendAcquisitionLogAsync(
            new AcquisitionLogItem(
                DateTimeOffset.UtcNow,
                Component: Path.GetFileName(modelPath),
                Source: settings.ModelId,
                ExpectedSha256: expectedSha,
                ActualSha256: actualSha,
                Verified: false,
                Action: "quarantined",
                Reason: "checksum_mismatch",
                OriginalPath: modelPath,
                QuarantinedPath: quarantinedPath),
            root,
            ct).ConfigureAwait(false);

        ClientLog.Warn(
            $"[Governance] Model checksum mismatch detected for '{modelPath}' - quarantined as '{quarantinedPath}'.");

        return new ModelIntegrityVerificationResult(
            true,
            "checksum_mismatch_quarantined",
            GetQuarantineUserMessage(settings.UiLanguage),
            quarantinedPath,
            expectedSha,
            actualSha);
    }

    public static bool IsModelQuarantined(AppSettings settings)
    {
        var modelPath = (settings.ModelPath ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(modelPath) && File.Exists(QuarantinePath(modelPath));
    }

    internal static string QuarantinePath(string modelPath)
        => modelPath + ".quarantine";

    private static async Task<string?> ResolveExpectedChecksumAsync(
        AppSettings settings,
        string? root,
        CancellationToken ct)
    {
        var modelPath = (settings.ModelPath ?? string.Empty).Trim();
        var fileName = Path.GetFileName(modelPath);
        var canonicalModelId = ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(fileName)
            ?? settings.ModelId;

        var catalogRead = await GovernanceArtifactStore.ReadAsync<ModelCatalogArtifact>(
            GovernanceArtifactStore.ModelCatalogFile,
            root,
            ct).ConfigureAwait(false);
        if (catalogRead.Status == GovernanceArtifactReadStatus.Ok && catalogRead.Value is not null)
        {
            var catalogMatch = catalogRead.Value.Items.FirstOrDefault(item =>
                string.Equals(item.ModelId, canonicalModelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(catalogMatch?.ChecksumSha256))
                return catalogMatch.ChecksumSha256;
        }

        return ModelLibrary.TryGetByPath(modelPath)?.Sha256;
    }

    private static async Task AppendAcquisitionLogAsync(
        AcquisitionLogItem item,
        string? root,
        CancellationToken ct)
    {
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root, ct).ConfigureAwait(false);

        var read = await GovernanceArtifactStore.ReadAsync<AcquisitionLogArtifact>(
            GovernanceArtifactStore.AcquisitionLogFile,
            root,
            ct).ConfigureAwait(false);

        var items = read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value.Items.ToList()
            : new List<AcquisitionLogItem>();

        items.Insert(0, item);

        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.AcquisitionLogFile,
            new AcquisitionLogArtifact(
                GovernanceArtifactStore.AcquisitionLogFile,
                "v3.1",
                items.Take(200).ToArray()),
            root,
            ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[1024 * 128];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0)
                break;

            hasher.AppendData(buffer, 0, n);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
