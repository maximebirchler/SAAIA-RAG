using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<JsonElement> ExecDocumentsGetResolvedAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetStringArg(args, "docRef") ?? GetStringArg(args, "docId");
        if (string.IsNullOrWhiteSpace(docRef))
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef!, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.DocumentsGetAsync(resolved.DocId, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsCountAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var q = GetStringArg(args, "q");
        return await _api.DocumentsCountAsync(categoryPath, categoryRef, q, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsCategoriesAsync(JsonElement args, CancellationToken ct)
    {
        var (path, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.DocumentsCategoriesAsync(path, categoryRef, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsTreeAsync(JsonElement args, CancellationToken ct)
    {
        var (path, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var depth = GetIntArg(args, "depth") ?? 10;
        var format = GetStringArg(args, "format") ?? "markdown";
        return await _api.DocumentsTreeAsync(path, categoryRef, depth, format, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsStatsAsync(JsonElement args, CancellationToken ct)
    {
        var (path, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.DocumentsStatsAsync(path, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsEmptyCountAsync(JsonElement args, CancellationToken ct)
    {
        var (path, _) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.DocumentsEmptyFoldersCountAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsEmptyListAsync(JsonElement args, CancellationToken ct)
    {
        var (path, _) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = GetIntArg(args, "limit") ?? 200;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.DocumentsEmptyFoldersListAsync(path, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryGetAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.SummaryGetAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryExistsAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"exists\":false,\"error\":\"missing_doc_ref\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"exists\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.SummaryExistsAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummarySearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = GetStringArg(args, "q") ?? string.Empty;
        var limit = GetIntArg(args, "limit") ?? 20;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.SummarySearchAsync(q, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryStatusCountAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.AdminSummaryMissingCountAsync(categoryPath, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryStatusListAsync(JsonElement args, CancellationToken ct)
    {
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.AdminSummaryMissingAsync(limit, offset, categoryPath, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryPresentCountAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.AdminSummaryPresentCountAsync(categoryPath, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryPresentListAsync(JsonElement args, CancellationToken ct)
    {
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.AdminSummaryPresentAsync(limit, offset, categoryPath, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryMissingAsync(JsonElement args, CancellationToken ct)
    {
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        return await _api.AdminSummaryMissingAsync(limit, offset, categoryPath, categoryRef, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryRequestAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        return await _api.AdminSummaryRequestAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryGenerateAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var force = GetBoolArg(args, "force") ?? false;
        return await _api.AdminSummaryGenerateAsync(resolved.DocId, level, force, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummarySubmitAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var docLanguage = GetStringArg(args, "docLanguage") ?? GetStringArg(args, "language") ?? _mem.LastLanguage;
        var sourceHash = GetStringArg(args, "sourceHash") ?? string.Empty;
        var summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty;
        var jobId = GetStringArg(args, "jobId");
        JsonElement? meta = args.TryGetProperty("meta", out var metaEl) ? metaEl : null;

        return await _api.AdminSummarySubmitAsync(resolved.DocId, level, docLanguage, sourceHash, summaryText, jobId, meta, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryStatusAsync(JsonElement args, CancellationToken ct)
    {
        var jobId = GetStringArg(args, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonDocument.Parse("{\"error\":\"missing_job_id\"}").RootElement;

        return await _api.AdminSummaryStatusAsync(jobId!, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryDeleteAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        return await _api.AdminSummaryDeleteAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private Task<JsonElement> ExecAdminCatalogHealthAsync(CancellationToken ct)
        => _api.AdminCatalogHealthAsync(ct);

    private Task<JsonElement> ExecAdminCatalogRescanNowAsync(CancellationToken ct)
        => _api.AdminCatalogRescanNowAsync(ct);

    private async Task<JsonElement> ExecAdminIngestionReindexAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var strict = await ResolveExplicitDocumentReferenceAsync(docRef, ct).ConfigureAwait(false);
        if (!strict.IsResolved || strict.Document is null)
        {
            var error = strict.IsCategoryReference
                ? "doc_target_is_category"
                : strict.IsAmbiguous
                    ? "doc_ref_ambiguous"
                    : "doc_not_found";
            using var errorDoc = JsonDocument.Parse($"{{\"error\":\"{error}\"}}");
            return errorDoc.RootElement.Clone();
        }

        return await _api.AdminIngestionReindexAsync(strict.Document.DocPath, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminJobsListAsync(JsonElement args, CancellationToken ct)
    {
        var type = GetStringArg(args, "type");
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.AdminJobsListAsync(type, limit, offset, null, null, null, null, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminAuditAsync(JsonElement args, CancellationToken ct)
    {
        var action = GetStringArg(args, "action");
        var target = GetStringArg(args, "target");
        var since = GetStringArg(args, "since");
        var until = GetStringArg(args, "until");
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.AdminAuditListAsync(action, target, since, until, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminJobsCancelAsync(JsonElement args, CancellationToken ct)
    {
        var jobId = GetStringArg(args, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonDocument.Parse("{\"error\":\"missing_job_id\"}").RootElement;

        return await _api.AdminJobsCancelAsync(jobId!, ct).ConfigureAwait(false);
    }

    private JsonElement ExecDiagnosticPerformance(JsonElement args)
    {
        var payload = BuildAgentRuntimeSnapshot();
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSourcesResolveV2Async(JsonElement args, CancellationToken ct)
    {
        var rawRef = GetStringArg(args, "ref") ?? GetStringArg(args, "pdfRef");
        if (string.IsNullOrWhiteSpace(rawRef))
            return JsonDocument.Parse("{\"source\":null,\"error\":\"missing_source_ref\"}").RootElement;

        try
        {
            var backend = await _api.SourceResolveAsync(rawRef, rawRef, ct).ConfigureAwait(false);
            if (backend.ValueKind == JsonValueKind.Object
                && backend.TryGetProperty("source", out var sourceEl)
                && sourceEl.ValueKind == JsonValueKind.Object)
            {
                return backend;
            }
        }
        catch
        {
            // Keep the local resolver fallback for compatibility.
        }

        var source = ResolveSourceRef(rawRef!);
        if (source is null)
        {
            var notFoundPayload = new
            {
                source = (object?)null,
                error = "source_not_found",
                requestedRef = rawRef!.Trim()
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(notFoundPayload)).RootElement;
        }

        var payload = new
        {
            source = new
            {
                docPath = source.DocPath,
                pageStart = source.PageStart,
                pageEnd = source.PageEnd,
                label = source.Label
            }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

}
