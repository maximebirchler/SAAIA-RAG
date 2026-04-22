using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private sealed record CapabilityAOffsetPreviewResult(
        string Message,
        bool Positive);

    private async Task<CapabilityAOffsetPreviewResult> LoadCapabilityAOffsetBackfillPreviewAsync(string lang, CancellationToken ct)
    {
        var json = await _api.AdminRuntimeCapabilityAEnqueueAsync(
            CapabilityAOffsetBackfillReasons,
            dryRun: true,
            allowUnsafeCandidates: false,
            maxCandidates: null,
            ct).ConfigureAwait(true);

        var candidateCount = TryGetInt(json, "candidateCount") ?? 0;
        var plannedCount = TryGetInt(json, "plannedCount") ?? 0;
        var skippedCount = TryGetInt(json, "skippedCount") ?? 0;
        var previewDocs = new List<string>();

        if (TryGetPropertyIgnoreCase(json, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var previewItem in itemsElement.EnumerateArray())
            {
                var reason = TryGetString(previewItem, "reason");
                var docPath = TryGetString(previewItem, "docPath");
                if (!string.Equals(reason, "dry_run_preview", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(docPath))
                {
                    continue;
                }

                previewDocs.Add(docPath!);
            }
        }

        var examples = previewDocs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        var message = plannedCount > 0
            ? examples.Length > 0
                ? ClientUiText.Format(
                    "admin.runtime.action.preview_offsets.result",
                    lang,
                    plannedCount,
                    candidateCount,
                    skippedCount,
                    string.Join(", ", examples))
                : ClientUiText.Format(
                    "admin.runtime.action.preview_offsets.result_short",
                    lang,
                    plannedCount,
                    candidateCount,
                    skippedCount)
            : ClientUiText.Format(
                "admin.runtime.action.preview_offsets.empty",
                lang,
                candidateCount,
                skippedCount);

        return new CapabilityAOffsetPreviewResult(
            Message: message,
            Positive: plannedCount > 0);
    }
}
