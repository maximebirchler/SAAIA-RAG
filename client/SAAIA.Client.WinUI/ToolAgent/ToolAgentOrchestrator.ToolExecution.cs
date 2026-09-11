using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private async Task<JsonElement> ExecDocumentsNavigationAsync(JsonElement args, CancellationToken ct)
    {
        var (path, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var docRef = GetPreferredDocRef(args);
        string? docId = null;
        string? docPath = GetStringArg(args, "docPath");
        if (!string.IsNullOrWhiteSpace(docRef))
        {
            var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
            if (resolved is null)
                return JsonDocument.Parse("{\"found\":false,\"navigationOnly\":true,\"error\":\"doc_not_found\"}").RootElement.Clone();

            docId = resolved.DocId;
            docPath = resolved.DocPath;
        }

        if (!string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath))
        {
            path = null;
            categoryRef = null;
        }

        var q = GetStringArg(args, "q");
        var kind = GetStringArg(args, "kind");
        var limit = GetIntArg(args, "limit") ?? 120;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.DocumentsNavigationAsync(path, categoryRef, docId, docPath, q, kind, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsContentCardsAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var docRef = GetPreferredDocRef(args);
        string? docId = GetStringArg(args, "docId");
        string? docPath = GetStringArg(args, "docPath");
        if (!string.IsNullOrWhiteSpace(docRef)
            && string.IsNullOrWhiteSpace(docId)
            && string.IsNullOrWhiteSpace(docPath))
        {
            var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
            if (resolved is null)
                return JsonDocument.Parse("{\"found\":false,\"citable\":true,\"error\":\"doc_not_found\"}").RootElement.Clone();

            docId = resolved.DocId;
            docPath = resolved.DocPath;
        }

        if (!string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath))
        {
            categoryPath = null;
            categoryRef = null;
        }

        var q = GetStringArg(args, "q");
        var inventoryMode = GetStringArg(args, "inventoryMode");
        var limit = GetIntArg(args, "limit") ?? 60;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.DocumentsContentCardsAsync(
                categoryPath,
                categoryRef,
                docId,
                docPath,
                q,
                inventoryMode,
                limit,
                offset,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsContextAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        string? docId = GetStringArg(args, "docId");
        string? docPath = GetStringArg(args, "docPath");
        if (!string.IsNullOrWhiteSpace(docRef) && string.IsNullOrWhiteSpace(docId) && string.IsNullOrWhiteSpace(docPath))
        {
            var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
            if (resolved is null)
                return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement.Clone();

            docId = resolved.DocId;
            docPath = resolved.DocPath;
        }

        var chunkId = GetStringArg(args, "chunkId") ?? GetStringArg(args, "chunk_id");
        var pageStart = GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start");
        var pageEnd = GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end");
        var before = GetIntArg(args, "before") ?? 2;
        var after = GetIntArg(args, "after") ?? 4;
        var limit = GetIntArg(args, "limit") ?? 12;
        var offset = GetIntArg(args, "offset") ?? 0;

        var context = await _api.DocumentsContextAsync(
                docId,
                docPath,
                chunkId,
                pageStart,
                pageEnd,
                before,
                after,
                limit,
                offset,
                ct)
            .ConfigureAwait(false);
        if (context.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(chunkId))
        {
            return context;
        }

        var properties = context
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        properties["requestedAnchorChunkId"] = chunkId;
        return JsonSerializer.SerializeToElement(
            properties,
            ClientJson.CamelCase);
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

    private async Task<JsonElement> ExecDocumentsExtractionQualityAsync(JsonElement args, CancellationToken ct)
    {
        var (path, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = GetIntArg(args, "limit") ?? 200;
        return await _api.DocumentsExtractionQualityAsync(path, categoryRef, limit, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsExtractionPagesAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (string.IsNullOrWhiteSpace(docRef))
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement.Clone();

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement.Clone();

        return await _api.DocumentExtractionPagesAsync(resolved.DocId, ct).ConfigureAwait(false);
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
        var sourceMetadata = await ResolveLiveSummarySourceMetadataAsync(resolved, ct).ConfigureAwait(false);
        var resolvedDocLanguage = NormalizeDocumentLanguageTag(sourceMetadata?.DocLanguage);
        var docLanguage = !string.Equals(resolvedDocLanguage, "und", StringComparison.Ordinal)
            ? resolvedDocLanguage
            : ResolveLiveSummaryDocumentLanguage(ResolveAdminSummarySubmitDocLanguage(args), sourceMetadata);
        var sourceHash = sourceMetadata?.SourceHash ?? GetStringArg(args, "sourceHash") ?? string.Empty;
        var summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty;
        var jobId = GetStringArg(args, "jobId");
        var executionLeaseToken = GetStringArg(args, "executionLeaseToken") ?? GetStringArg(args, "leaseToken");
        JsonElement? meta = args.TryGetProperty("meta", out var metaEl) ? metaEl : null;

        return await _api.AdminSummarySubmitAsync(resolved.DocId, level, docLanguage, sourceHash, summaryText, jobId, executionLeaseToken, meta, ct).ConfigureAwait(false);
    }

    private static string ResolveAdminSummarySubmitDocLanguage(JsonElement args)
        => NormalizeAdminSummarySubmitDocLanguage(GetStringArg(args, "docLanguage"));

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
            var backendRef = rawRef.Trim();
            var resolvedRef = await ResolveDocRefAsync(backendRef, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolvedRef?.DocId))
                backendRef = resolvedRef.DocId;

            var backend = await _api.SourceResolveAsync(backendRef, rawRef, ct).ConfigureAwait(false);
            if (backend.ValueKind == JsonValueKind.Object
                && backend.TryGetProperty("source", out var sourceEl)
                && sourceEl.ValueKind == JsonValueKind.Object)
            {
                return EnrichSourceResolveResult(rawRef, backendRef, backend, sourceEl);
            }

            if (backend.ValueKind == JsonValueKind.Object
                && backend.TryGetProperty("error", out var errorEl)
                && errorEl.ValueKind == JsonValueKind.String)
            {
                var error = errorEl.GetString();
                if (string.Equals(error, "source_not_found", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(error, "missing_source_ref", StringComparison.OrdinalIgnoreCase))
                {
                    return backend;
                }
            }

            var invalidPayload = new
            {
                source = (object?)null,
                error = "sources_resolve_invalid_response",
                requestedRef = rawRef.Trim()
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(invalidPayload)).RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // Older backends may not expose /sources/resolve yet. In that one
            // compatibility case, fall back to the already-resolved conversation memory.
        }
        catch (HttpRequestException ex)
        {
            var failedPayload = new
            {
                source = (object?)null,
                error = "sources_resolve_failed",
                status = ex.StatusCode is null ? null : (int?)ex.StatusCode.Value,
                requestedRef = rawRef.Trim()
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(failedPayload)).RootElement.Clone();
        }
        catch (JsonException)
        {
            var failedPayload = new
            {
                source = (object?)null,
                error = "sources_resolve_invalid_response",
                requestedRef = rawRef.Trim()
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(failedPayload)).RootElement.Clone();
        }
        catch
        {
            var failedPayload = new
            {
                source = (object?)null,
                error = "sources_resolve_failed",
                requestedRef = rawRef.Trim()
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(failedPayload)).RootElement.Clone();
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
            requestedRef = rawRef!.Trim(),
                source = new
                {
                    docId = source.DocId,
                    docPath = source.DocPath,
                    docName = source.DocName,
                    pageStart = source.PageStart,
                    pageEnd = source.PageEnd,
                label = source.Label,
                sourceHash = source.SourceHash,
                docLanguage = source.DocLanguage,
                profileLanguage = source.ProfileLanguage,
                category = source.Category,
                categoryRef = source.CategoryRef,
                categoryPath = source.CategoryPath,
                chunkId = source.ChunkId,
                sectionTitle = source.SectionTitle,
                headingPath = source.HeadingPath,
                prevChunkId = source.PrevChunkId,
                nextChunkId = source.NextChunkId,
                sameSectionChunkId = source.SameSectionChunkId,
                originalChunkType = source.OriginalChunkType,
                provenanceInfo = BuildSourceProvenancePayload(source),
                contentSignals = BuildSourceContentSignalsPayload(source),
                extractionQuality = BuildSourceExtractionQualityPayload(source),
                matchedContentCards = BuildSourceContentCardsPayload(source),
                profileSignals = BuildSourceProfileSignalsPayload(source),
                selectionHints = BuildSourceSelectionHintsPayload(source)
            }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private JsonElement EnrichSourceResolveResult(string rawRef, string backendRef, JsonElement backend, JsonElement sourceEl)
    {
        var backendSource = TryBuildSourceRefFromJsonElement(sourceEl);
        if (backendSource is null)
            return backend;

        var memorySource =
            ResolveSourceRef(rawRef)
            ?? (string.Equals(rawRef, backendRef, StringComparison.OrdinalIgnoreCase) ? null : ResolveSourceRef(backendRef))
            ?? ResolveSourceRef(backendSource.DocPath);

        var preferPreciseMemory = memorySource is not null
            && ShouldPreferPreciseFallbackSource(backendSource, memorySource);
        var source = MergeSourceResolveMetadata(backendSource, memorySource);
        var root = JsonNode.Parse(backend.GetRawText()) as JsonObject ?? new JsonObject();
        var sourceNode = JsonNode.Parse(sourceEl.GetRawText()) as JsonObject ?? new JsonObject();

        SetStringIfMissing(sourceNode, "docId", source.DocId);
        SetStringIfMissing(sourceNode, "docPath", source.DocPath);
        SetStringIfMissing(sourceNode, "docName", source.DocName);
        SetNumberIfMissing(sourceNode, "pageStart", source.PageStart);
        SetNumberIfMissing(sourceNode, "pageEnd", source.PageEnd);
        SetStringIfMissing(sourceNode, "label", source.Label);
        SetStringIfMissing(sourceNode, "sourceHash", source.SourceHash);
        SetStringIfMissing(sourceNode, "docLanguage", source.DocLanguage);
        SetStringIfMissing(sourceNode, "profileLanguage", source.ProfileLanguage);
        SetStringIfMissing(sourceNode, "category", source.Category);
        SetStringIfMissing(sourceNode, "categoryRef", source.CategoryRef);
        SetStringIfMissing(sourceNode, "categoryPath", source.CategoryPath);
        SetStringIfMissing(sourceNode, "chunkId", source.ChunkId);
        SetStringIfMissing(sourceNode, "sectionTitle", source.SectionTitle);
        SetStringIfMissing(sourceNode, "headingPath", source.HeadingPath);
        SetStringIfMissing(sourceNode, "prevChunkId", source.PrevChunkId);
        SetStringIfMissing(sourceNode, "nextChunkId", source.NextChunkId);
        SetStringIfMissing(sourceNode, "sameSectionChunkId", source.SameSectionChunkId);
        SetStringIfMissing(sourceNode, "originalChunkType", source.OriginalChunkType);
        SetObjectIfMissingOrEmpty(sourceNode, "provenanceInfo", BuildSourceProvenancePayload(source));
        SetObjectIfMissingOrEmpty(sourceNode, "contentSignals", BuildSourceContentSignalsPayload(source));
        MergeObjectFieldsIfMissing(sourceNode, "extractionQuality", BuildSourceExtractionQualityPayload(source));
        SetObjectIfMissingOrEmpty(sourceNode, "matchedContentCards", BuildSourceContentCardsPayload(source));
        MergeObjectFieldsIfMissing(sourceNode, "profileSignals", BuildSourceProfileSignalsPayload(source));
        SetObjectIfMissingOrEmpty(sourceNode, "selectionHints", BuildSourceSelectionHintsPayload(source));
        if (preferPreciseMemory)
            ApplyPreciseSourceOverride(sourceNode, source);

        root["source"] = sourceNode;
        SetStringIfMissing(root, "requestedRef", rawRef.Trim());

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    private static void ApplyPreciseSourceOverride(JsonObject sourceNode, ToolMemory.SourceRef source)
    {
        SetNumberIfPresent(sourceNode, "pageStart", source.PageStart);
        SetNumberIfPresent(sourceNode, "pageEnd", source.PageEnd);
        SetStringIfPresent(sourceNode, "chunkId", source.ChunkId);
        SetStringIfPresent(sourceNode, "sectionTitle", source.SectionTitle);
        SetStringIfPresent(sourceNode, "headingPath", source.HeadingPath);
        SetStringIfPresent(sourceNode, "prevChunkId", source.PrevChunkId);
        SetStringIfPresent(sourceNode, "nextChunkId", source.NextChunkId);
        SetStringIfPresent(sourceNode, "sameSectionChunkId", source.SameSectionChunkId);
        SetStringIfPresent(sourceNode, "originalChunkType", source.OriginalChunkType);
        SetObjectIfPresent(sourceNode, "provenanceInfo", BuildSourceProvenancePayload(source));
        SetObjectIfPresent(sourceNode, "contentSignals", BuildSourceContentSignalsPayload(source));
        SetObjectIfPresent(sourceNode, "extractionQuality", BuildSourceExtractionQualityPayload(source));
        SetObjectIfPresent(sourceNode, "matchedContentCards", BuildSourceContentCardsPayload(source));
        SetObjectIfPresent(sourceNode, "profileSignals", BuildSourceProfileSignalsPayload(source));
        SetObjectIfPresent(sourceNode, "selectionHints", BuildSourceSelectionHintsPayload(source));
    }

    private static ToolMemory.SourceRef MergeSourceResolveMetadata(ToolMemory.SourceRef source, ToolMemory.SourceRef? fallback)
    {
        if (fallback is null)
            return source;

        var usePreciseFallback = ShouldPreferPreciseFallbackSource(source, fallback);
        var pageSource = usePreciseFallback ? fallback : source;
        var selectionHintSource = usePreciseFallback ? fallback : source;
        var pageQualitySource = usePreciseFallback ? fallback : source;
        var contentSignalSource = usePreciseFallback ? fallback : source;

        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(source.DocId) ?? NullIfWhiteSpace(fallback.DocId),
            DocPath = NullIfWhiteSpace(source.DocPath) ?? NullIfWhiteSpace(fallback.DocPath) ?? "",
            DocName = NullIfWhiteSpace(source.DocName) ?? NullIfWhiteSpace(fallback.DocName),
            PageStart = pageSource.PageStart <= 0 ? Math.Max(1, fallback.PageStart) : pageSource.PageStart,
            PageEnd = pageSource.PageEnd <= 0 ? Math.Max(Math.Max(1, fallback.PageStart), fallback.PageEnd) : pageSource.PageEnd,
            Label = NullIfWhiteSpace(source.Label) ?? NullIfWhiteSpace(fallback.Label) ?? NullIfWhiteSpace(source.DocPath) ?? NullIfWhiteSpace(fallback.DocPath) ?? "",
            SourceHash = NullIfWhiteSpace(source.SourceHash) ?? NullIfWhiteSpace(fallback.SourceHash),
            DocLanguage = NullIfWhiteSpace(source.DocLanguage) ?? NullIfWhiteSpace(fallback.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(source.ProfileLanguage) ?? NullIfWhiteSpace(fallback.ProfileLanguage),
            Category = NullIfWhiteSpace(source.Category) ?? NullIfWhiteSpace(fallback.Category),
            CategoryRef = NullIfWhiteSpace(source.CategoryRef) ?? NullIfWhiteSpace(fallback.CategoryRef),
            CategoryPath = NullIfWhiteSpace(source.CategoryPath) ?? NullIfWhiteSpace(fallback.CategoryPath),
            ChunkId = usePreciseFallback
                ? NullIfWhiteSpace(fallback.ChunkId) ?? NullIfWhiteSpace(source.ChunkId)
                : NullIfWhiteSpace(source.ChunkId) ?? NullIfWhiteSpace(fallback.ChunkId),
            SectionTitle = NullIfWhiteSpace(source.SectionTitle) ?? NullIfWhiteSpace(fallback.SectionTitle),
            HeadingPath = NullIfWhiteSpace(source.HeadingPath) ?? NullIfWhiteSpace(fallback.HeadingPath),
            PrevChunkId = NullIfWhiteSpace(source.PrevChunkId) ?? NullIfWhiteSpace(fallback.PrevChunkId),
            NextChunkId = NullIfWhiteSpace(source.NextChunkId) ?? NullIfWhiteSpace(fallback.NextChunkId),
            SameSectionChunkId = NullIfWhiteSpace(source.SameSectionChunkId) ?? NullIfWhiteSpace(fallback.SameSectionChunkId),
            OriginalChunkType = NullIfWhiteSpace(source.OriginalChunkType) ?? NullIfWhiteSpace(fallback.OriginalChunkType),
            OffsetStart = source.OffsetStart ?? fallback.OffsetStart,
            OffsetEnd = source.OffsetEnd ?? fallback.OffsetEnd,
            SourceUnitOrdinals = contentSignalSource.SourceUnitOrdinals.Count > 0
                ? contentSignalSource.SourceUnitOrdinals.Distinct().OrderBy(static ordinal => ordinal).ToList()
                : source.SourceUnitOrdinals.Count > 0
                    ? source.SourceUnitOrdinals.Distinct().OrderBy(static ordinal => ordinal).ToList()
                    : fallback.SourceUnitOrdinals.Distinct().OrderBy(static ordinal => ordinal).ToList(),
            SourceUnitStartOrdinal = contentSignalSource.SourceUnitStartOrdinal ?? source.SourceUnitStartOrdinal ?? fallback.SourceUnitStartOrdinal,
            SourceUnitEndOrdinal = contentSignalSource.SourceUnitEndOrdinal ?? source.SourceUnitEndOrdinal ?? fallback.SourceUnitEndOrdinal,
            SourceUnitCount = contentSignalSource.SourceUnitCount ?? source.SourceUnitCount ?? fallback.SourceUnitCount,
            ChunkComposition = NullIfWhiteSpace(contentSignalSource.ChunkComposition) ?? NullIfWhiteSpace(source.ChunkComposition) ?? NullIfWhiteSpace(fallback.ChunkComposition),
            ExtractionSource = NullIfWhiteSpace(source.ExtractionSource) ?? NullIfWhiteSpace(fallback.ExtractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(source.DocumentQualityStatus) ?? NullIfWhiteSpace(fallback.DocumentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(pageQualitySource.PageQualityStatus) ?? NullIfWhiteSpace(source.PageQualityStatus) ?? NullIfWhiteSpace(fallback.PageQualityStatus),
            TextStatus = NullIfWhiteSpace(pageQualitySource.TextStatus) ?? NullIfWhiteSpace(source.TextStatus) ?? NullIfWhiteSpace(fallback.TextStatus),
            ChunkTextStatus = NullIfWhiteSpace(pageQualitySource.ChunkTextStatus) ?? NullIfWhiteSpace(source.ChunkTextStatus) ?? NullIfWhiteSpace(fallback.ChunkTextStatus),
            ChunkTextSparse = pageQualitySource.ChunkTextSparse ?? source.ChunkTextSparse ?? fallback.ChunkTextSparse,
            ChunkOcrCandidate = pageQualitySource.ChunkOcrCandidate ?? source.ChunkOcrCandidate ?? fallback.ChunkOcrCandidate,
            QualityStatus = NullIfWhiteSpace(pageQualitySource.QualityStatus) ?? NullIfWhiteSpace(source.QualityStatus) ?? NullIfWhiteSpace(fallback.QualityStatus),
            ExtractionConfidence = source.ExtractionConfidence ?? fallback.ExtractionConfidence,
            DocumentExtractionConfidence = source.DocumentExtractionConfidence ?? fallback.DocumentExtractionConfidence,
            PageExtractionConfidence = pageQualitySource.PageExtractionConfidence ?? source.PageExtractionConfidence ?? fallback.PageExtractionConfidence,
            ManualReviewRecommended = source.ManualReviewRecommended || fallback.ManualReviewRecommended,
            DocumentManualReviewRecommended = source.DocumentManualReviewRecommended || fallback.DocumentManualReviewRecommended,
            PageManualReviewRecommended = pageQualitySource.PageManualReviewRecommended || source.PageManualReviewRecommended || fallback.PageManualReviewRecommended,
            OcrAttempted = source.OcrAttempted || fallback.OcrAttempted,
            OcrApplied = source.OcrApplied || fallback.OcrApplied,
            OcrRecommended = source.OcrRecommended || fallback.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(source.ExtractionDiagnosticSummary ?? fallback.ExtractionDiagnosticSummary),
            QualitySignals = source.QualitySignals
                .Concat(fallback.QualitySignals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            ChunkQualitySignals = source.ChunkQualitySignals
                .Concat(fallback.ChunkQualitySignals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            MatchedContentCards = MergeSourceContentCards(
                usePreciseFallback
                    ? new[] { fallback, source }
                    : new[] { source, fallback },
                maxCards: 5),
            ProfileSignals = MergeSourceProfileSignals(usePreciseFallback
                ? new[] { fallback, source }
                : new[] { source, fallback }),
            SelectionHintEvidenceRole = NullIfWhiteSpace(selectionHintSource.SelectionHintEvidenceRole) ?? NullIfWhiteSpace(source.SelectionHintEvidenceRole) ?? NullIfWhiteSpace(fallback.SelectionHintEvidenceRole),
            SelectionHintActionabilityScore = selectionHintSource.SelectionHintActionabilityScore ?? source.SelectionHintActionabilityScore ?? fallback.SelectionHintActionabilityScore,
            SelectionHintSupportScore = selectionHintSource.SelectionHintSupportScore ?? source.SelectionHintSupportScore ?? fallback.SelectionHintSupportScore,
            SelectionHintFragmentScore = selectionHintSource.SelectionHintFragmentScore ?? source.SelectionHintFragmentScore ?? fallback.SelectionHintFragmentScore,
            SelectionHintNavigationScore = selectionHintSource.SelectionHintNavigationScore ?? source.SelectionHintNavigationScore ?? fallback.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = selectionHintSource.SelectionHintQualityPenalty ?? source.SelectionHintQualityPenalty ?? fallback.SelectionHintQualityPenalty,
            ContentRole = NullIfWhiteSpace(contentSignalSource.ContentRole) ?? NullIfWhiteSpace(source.ContentRole) ?? NullIfWhiteSpace(fallback.ContentRole),
            NavigationReason = NullIfWhiteSpace(contentSignalSource.NavigationReason) ?? NullIfWhiteSpace(source.NavigationReason) ?? NullIfWhiteSpace(fallback.NavigationReason),
            RetrievalNavigationScore = contentSignalSource.RetrievalNavigationScore ?? source.RetrievalNavigationScore ?? fallback.RetrievalNavigationScore,
            ContentDensityScore = contentSignalSource.ContentDensityScore ?? source.ContentDensityScore ?? fallback.ContentDensityScore
        };
    }

    private static bool ShouldPreferPreciseFallbackSource(ToolMemory.SourceRef source, ToolMemory.SourceRef fallback)
    {
        if (!LooksLikeSameSourceDocument(source, fallback))
            return false;

        var sourceHash = NullIfWhiteSpace(source.SourceHash);
        var fallbackHash = NullIfWhiteSpace(fallback.SourceHash);
        if (sourceHash is not null
            && fallbackHash is not null
            && !string.Equals(sourceHash, fallbackHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceStart = Math.Max(1, source.PageStart);
        var sourceEnd = Math.Max(sourceStart, source.PageEnd);
        var fallbackStart = Math.Max(1, fallback.PageStart);
        var fallbackEnd = Math.Max(fallbackStart, fallback.PageEnd);
        var sourceSpan = sourceEnd - sourceStart;
        var fallbackSpan = fallbackEnd - fallbackStart;

        var fallbackHasPrecisePage = fallbackStart > sourceStart
            || fallbackEnd < sourceEnd
            || fallbackSpan < sourceSpan;
        var fallbackHasHitEvidence = !string.IsNullOrWhiteSpace(fallback.ChunkId)
            || fallback.MatchedContentCards.Count > 0
            || fallback.PageExtractionConfidence is not null
            || !string.IsNullOrWhiteSpace(fallback.PageQualityStatus);
        var sourceLooksDocumentWide = sourceStart <= 1
            && sourceSpan >= 2
            && string.IsNullOrWhiteSpace(source.ChunkId);

        return sourceLooksDocumentWide
            && fallbackHasHitEvidence
            && fallbackHasPrecisePage;
    }

    private static bool LooksLikeSameSourceDocument(ToolMemory.SourceRef source, ToolMemory.SourceRef fallback)
    {
        var sourceDocId = NullIfWhiteSpace(source.DocId);
        var fallbackDocId = NullIfWhiteSpace(fallback.DocId);
        if (sourceDocId is not null && fallbackDocId is not null)
            return string.Equals(sourceDocId, fallbackDocId, StringComparison.OrdinalIgnoreCase);

        var sourcePath = NullIfWhiteSpace(source.DocPath)?.Replace('\\', '/');
        var fallbackPath = NullIfWhiteSpace(fallback.DocPath)?.Replace('\\', '/');
        return sourcePath is not null
            && fallbackPath is not null
            && string.Equals(sourcePath, fallbackPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStringIfMissing(JsonObject obj, string propertyName, string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is null || !IsMissingOrEmpty(obj, propertyName))
            return;

        obj[propertyName] = value;
    }

    private static void SetNumberIfMissing(JsonObject obj, string propertyName, int value)
    {
        if (value <= 0 || !IsMissingOrEmpty(obj, propertyName))
            return;

        obj[propertyName] = value;
    }

    private static void SetStringIfPresent(JsonObject obj, string propertyName, string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is not null)
            obj[propertyName] = value;
    }

    private static void SetNumberIfPresent(JsonObject obj, string propertyName, int value)
    {
        if (value > 0)
            obj[propertyName] = value;
    }

    private static void SetObjectIfPresent(JsonObject obj, string propertyName, object? value)
    {
        if (value is not null)
            obj[propertyName] = JsonSerializer.SerializeToNode(value);
    }

    private static void SetObjectIfMissingOrEmpty(JsonObject obj, string propertyName, object? value)
    {
        if (value is null || !IsMissingOrEmpty(obj, propertyName))
            return;

        obj[propertyName] = JsonSerializer.SerializeToNode(value);
    }

    private static void MergeObjectFieldsIfMissing(JsonObject obj, string propertyName, object? value)
    {
        if (value is null)
            return;

        var fallback = JsonSerializer.SerializeToNode(value) as JsonObject;
        if (fallback is null || fallback.Count == 0)
            return;

        if (IsMissingOrEmpty(obj, propertyName))
        {
            obj[propertyName] = fallback;
            return;
        }

        if (!obj.TryGetPropertyValue(propertyName, out var existingNode)
            || existingNode is not JsonObject existing)
        {
            return;
        }

        foreach (var property in fallback)
        {
            if (IsMissingOrEmpty(existing, property.Key))
                existing[property.Key] = property.Value?.DeepClone();
        }
    }

    private static bool IsMissingOrEmpty(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
            return true;

        return node.GetValueKind() switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(node.GetValue<string>()),
            JsonValueKind.Array => node.AsArray().Count == 0,
            JsonValueKind.Object => !node.AsObject().Any(),
            _ => false
        };
    }

}
