using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string> WaitForAdminIngestionJobAsync(string? jobId, string documentLabel, string language, CancellationToken ct, Action<string>? onProgress)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            UpdateRecentAdminOperationStatus("queued", completed: false, success: false, error: null);
            return DeterministicAgentText.AdminReindexQueued(language, documentLabel, null);
        }

        var startedAtUtc = _mem.LastAdminOperation?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        string? lastStatus = null;

        for (var attempt = 0; attempt < 180; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await TryLoadAdminIngestionJobAsync(jobId, ct).ConfigureAwait(false);
            var status = (ReadAdminJobStatus(snapshot) ?? string.Empty).Trim().ToLowerInvariant();
            var elapsedSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));
            if (status.Length == 0)
                status = "running";

            if (status is "done" or "completed" or "succeeded" or "success")
            {
                UpdateRecentAdminOperationStatus("done", completed: true, success: true, error: null);
                return DeterministicAgentText.AdminReindexCompleted(language, documentLabel);
            }

            if (status is "failed" or "error" or "canceled" or "cancelled")
            {
                var error = ReadAdminJobLastError(snapshot);
                UpdateRecentAdminOperationStatus(status, completed: true, success: false, error: error);
                return DeterministicAgentText.AdminReindexFailed(language, documentLabel, error);
            }

            UpdateRecentAdminOperationStatus(status, completed: false, success: false, error: null);
            if (!string.Equals(lastStatus, status, StringComparison.OrdinalIgnoreCase) || attempt == 0 || attempt % 5 == 0)
            {
                onProgress?.Invoke(status switch
                {
                    "queued" => DeterministicAgentText.AdminJobQueued(language, documentLabel, elapsedSeconds),
                    "running" => DeterministicAgentText.AdminJobRunning(language, documentLabel, elapsedSeconds),
                    _ => DeterministicAgentText.AdminReindexRunning(language, documentLabel, elapsedSeconds)
                });
                lastStatus = status;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        var finalElapsed = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));
        UpdateRecentAdminOperationStatus("running", completed: false, success: false, error: null);
        return DeterministicAgentText.AdminReindexRunning(language, documentLabel, finalElapsed);
    }

    private async Task<JsonElement> TryLoadAdminIngestionJobAsync(string jobId, CancellationToken ct)
    {
        try
        {
        var json = await _api.AdminJobsListAsync("ingestion", 100, 0, null, null, null, null, ct).ConfigureAwait(false);
            if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var currentJobId = TryGetString(item, "jobId") ?? TryGetString(item, "JobId");
                    if (string.Equals(currentJobId, jobId, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
            }
        }
        catch
        {
        }

        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string? ReadAdminJobStatus(JsonElement snapshot)
    {
        return TryGetString(snapshot, "status")
               ?? TryGetString(snapshot, "Status");
    }

    private static string? ReadAdminJobLastError(JsonElement snapshot)
    {
        return TryGetString(snapshot, "lastError")
               ?? TryGetString(snapshot, "LastError");
    }

    private async Task<(bool handled, string finalAnswer, string? routerIntent, IReadOnlyList<string> toolNames)> TryHandleRecentAdminOperationStatusAsync(
        string effectiveUserMessage,
        string interactionLanguage,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!LooksLikeRecentAdminOperationStatusFollowUp(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        var op = _mem.LastAdminOperation;
        if (op is null || (DateTimeOffset.UtcNow - op.CreatedAtUtc) > TimeSpan.FromMinutes(30))
            return (false, string.Empty, null, Array.Empty<string>());

        onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));

        if (!op.IsCompleted && string.Equals(op.OperationKind, "document_reindex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(op.JobId))
        {
            onProgress?.Invoke(DeterministicAgentText.AdminJobRunning(interactionLanguage, op.DisplayLabel));
            var snapshot = await TryLoadAdminIngestionJobAsync(op.JobId, ct).ConfigureAwait(false);
            var status = (ReadAdminJobStatus(snapshot) ?? string.Empty).Trim().ToLowerInvariant();
            if (status.Length == 0)
                status = op.Status;

            if (status is "done" or "completed" or "succeeded" or "success")
                UpdateRecentAdminOperationStatus("done", completed: true, success: true, error: null);
            else if (status is "failed" or "error" or "canceled" or "cancelled")
                UpdateRecentAdminOperationStatus(status, completed: true, success: false, error: ReadAdminJobLastError(snapshot));
            else
                UpdateRecentAdminOperationStatus(string.IsNullOrWhiteSpace(status) ? "running" : status, completed: false, success: false, error: null);

            op = _mem.LastAdminOperation;
        }

        if (op is null)
            return (false, string.Empty, null, Array.Empty<string>());

        var answer = BuildRecentAdminOperationStatusAnswer(op, interactionLanguage);
        await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
        return (true, answer, "admin.operation.status", new[] { "admin.operation.status" });
    }

    private string BuildRecentAdminOperationStatusAnswer(ToolMemory.AdminOperationState operation, string language)
    {
        if (string.Equals(operation.OperationKind, "catalog_rescan", StringComparison.OrdinalIgnoreCase))
        {
            return operation.IsCompleted
                ? DeterministicAgentText.AdminRescanCompleted(language, operation.IndexedDocuments, operation.TotalCategories, operation.MaxDepth)
                : DeterministicAgentText.AdminRescanQueued(language, operation.JobId);
        }

        if (operation.IsCompleted)
        {
            return operation.IsSuccess
                ? DeterministicAgentText.AdminReindexCompleted(language, operation.DisplayLabel)
                : DeterministicAgentText.AdminReindexFailed(language, operation.DisplayLabel, operation.LastError);
        }

        var elapsedSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - operation.CreatedAtUtc).TotalSeconds));
        return string.Equals(operation.Status, "queued", StringComparison.OrdinalIgnoreCase)
            ? DeterministicAgentText.AdminJobQueued(language, operation.DisplayLabel, elapsedSeconds)
            : DeterministicAgentText.AdminReindexRunning(language, operation.DisplayLabel, elapsedSeconds);
    }

    private void UpdateRecentAdminOperationStatus(string status, bool completed, bool success, string? error)
    {
        if (_mem.LastAdminOperation is null)
            return;

        _mem.LastAdminOperation.Status = string.IsNullOrWhiteSpace(status) ? _mem.LastAdminOperation.Status : status;
        _mem.LastAdminOperation.IsCompleted = completed;
        _mem.LastAdminOperation.IsSuccess = success;
        _mem.LastAdminOperation.LastError = string.IsNullOrWhiteSpace(error) ? _mem.LastAdminOperation.LastError : error.Trim();
        _mem.LastAdminOperation.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private void RememberCompletedAdminRescan(JsonElement result)
    {
        int? totalDocuments = null;
        int? totalCategories = null;
        int? maxDepth = null;

        if (result.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
        {
            if (snapshot.TryGetProperty("tenants", out var tenants) && tenants.ValueKind == JsonValueKind.Array)
            {
                foreach (var tenant in tenants.EnumerateArray())
                {
                    totalDocuments = TryGetInt(tenant, "docs") ?? totalDocuments;
                    totalCategories = TryGetInt(tenant, "nodes") ?? totalCategories;
                    break;
                }
            }
        }

        _mem.LastAdminOperation = new ToolMemory.AdminOperationState
        {
            OperationKind = "catalog_rescan",
            DisplayLabel = "catalog_rescan",
            Status = "done",
            IsCompleted = true,
            IsSuccess = true,
            IndexedDocuments = totalDocuments,
            TotalCategories = totalCategories,
            MaxDepth = maxDepth,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private string BuildAdminRescanCompletedAnswer(JsonElement result, string language)
    {
        int? totalDocuments = null;
        int? totalCategories = null;
        int? maxDepth = null;

        if (result.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
        {
            if (snapshot.TryGetProperty("tenants", out var tenants) && tenants.ValueKind == JsonValueKind.Array)
            {
                foreach (var tenant in tenants.EnumerateArray())
                {
                    totalDocuments = TryGetInt(tenant, "docs") ?? totalDocuments;
                    totalCategories = TryGetInt(tenant, "nodes") ?? totalCategories;
                    break;
                }
            }
        }

        return DeterministicAgentText.AdminRescanCompleted(language, totalDocuments, totalCategories, maxDepth);
    }

    private void UpdateLastListedDocumentsFromSummaryStatusSnapshot()
    {
        if (_mem.LastSummaryStatusSnapshot is null || _mem.LastSummaryStatusSnapshot.Items.Count == 0)
            return;

        _mem.LastListedDocuments = _mem.LastSummaryStatusSnapshot.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.DocPath))
            .Select(x => new ToolMemory.DocumentItem
            {
                DocId = x.DocId,
                DocPath = x.DocPath,
                DocName = string.IsNullOrWhiteSpace(x.DocName) ? System.IO.Path.GetFileName(x.DocPath) : x.DocName,
                Category = x.Category,
                CategoryPath = x.Category,
                PdfRef = string.Empty
            })
            .ToList();

        _mem.LastListOffset = 0;
        _mem.LastListTotal = _mem.LastSummaryStatusSnapshot.Total > 0 ? _mem.LastSummaryStatusSnapshot.Total : _mem.LastListedDocuments.Count;
        _mem.LastListEndOfList = true;
        _mem.LastListCategoryPath = _mem.LastSummaryStatusSnapshot.CategoryPath;
        _mem.LastListQuery = null;
    }
}
