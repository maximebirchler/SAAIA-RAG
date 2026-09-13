using System.Text;
using System.Text.Json;

namespace SAAIA.Client.ToolAgent.Tests;

internal static class LiveAdvancedValidationOwnership
{
    internal const string ManifestEnvironmentVariable = "SAAIA_ADVANCED_VALIDATION_OWNER_IDS_PATH";

    // Persist the real owner before any HTTP request can create a durable job.
    // Each context retains a fresh user identity for case/memory isolation.
    internal static string CreateUserId(string? manifestPath, bool advancedServer)
    {
        var userId = Guid.NewGuid().ToString("D");
        if (!advancedServer) return userId;
        if (string.IsNullOrWhiteSpace(manifestPath) || !Path.IsPathFullyQualified(manifestPath))
            throw new InvalidOperationException("Advanced validation requires an absolute owner manifest path.");
        // Refuse a missing destination instead of losing ownership on interruption.
        using var file = new FileStream(manifestPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Seek(0, SeekOrigin.End);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(userId) + "\n");
        file.Write(bytes);
        file.Flush(flushToDisk: true);
        return userId;
    }
}
