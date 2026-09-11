using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static SourceBackedRetrievalAttempt BuildRetrievalAttempt(
        ToolResults.Item toolItem,
        int sequence,
        int evidenceItemCount)
    {
        var result = toolItem.Result;
        var errorCode = FirstNonBlank(
            toolItem.Error,
            result.ValueKind == JsonValueKind.Object ? GetString(result, "error", "code") : null);
        var busy = result.ValueKind == JsonValueKind.Object && GetBool(result, "busy") == true;
        var meta = result.ValueKind == JsonValueKind.Object
                   && TryGetProperty(result, "meta", out var metaElement)
                   && metaElement.ValueKind == JsonValueKind.Object
            ? metaElement
            : default;
        var queries = ReadStringArray(meta, "queries");
        if (queries.Count == 0 && result.ValueKind == JsonValueKind.Object)
        {
            var query = GetString(result, "query", "queryUsed");
            if (!string.IsNullOrWhiteSpace(query))
                queries = new[] { query };
        }

        var degraded = ReadStringArray(meta, "degradedRetrievers");
        if (degraded.Count == 0)
            degraded = ReadStringArray(result, "degradedRetrievers");
        if (degraded.Count == 0 && TryGetProperty(meta, "metrics", out var metrics))
            degraded = ReadStringArray(metrics, "degradedRetrievers");
        if (degraded.Count == 0 && TryGetProperty(result, "metrics", out var resultMetrics))
            degraded = ReadStringArray(resultMetrics, "degradedRetrievers");
        var outcome = !string.IsNullOrWhiteSpace(errorCode) || busy
            ? "failed"
            : degraded.Count > 0
                ? "degraded"
                : "completed";

        return new SourceBackedRetrievalAttempt(
            sequence,
            toolItem.ToolName,
            queries,
            meta.ValueKind == JsonValueKind.Object ? GetString(meta, "categoryPath", "category") : null,
            outcome,
            errorCode,
            busy,
            degraded,
            toolItem.DurationMs,
            evidenceItemCount);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return values.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(static value => value.GetString()!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }
}
