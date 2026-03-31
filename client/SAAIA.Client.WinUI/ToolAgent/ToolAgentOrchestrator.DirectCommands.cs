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
    internal async Task<DirectCommandExecutionResult> ExecuteDirectCommandAsync(DirectCommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureRuntimeCatalogContextAsync(ct).ConfigureAwait(false);

        var language = LocalizedStrings.NormalizeLanguage(string.IsNullOrWhiteSpace(request.Language) ? _mem.LastLanguage : request.Language);
        var displayText = (request.DisplayText ?? string.Empty).Trim();
        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgsJson) ? "{}" : request.ArgsJson);
        var args = argsDoc.RootElement.Clone();

        return await ExecuteDirectCommandCoreAsync(request.CommandId, args, displayText, language, ct).ConfigureAwait(false);
    }

    private async Task<DirectCommandExecutionResult> ExecuteDirectCommandCoreAsync(string commandId, JsonElement args, string displayText, string language, CancellationToken ct)
    {
        switch ((commandId ?? string.Empty).Trim())
        {
            case DirectCommandCatalog.CatalogCategoriesList:
            {
                var result = await _api.DocumentsCategoriesAsync(null, null, 100, 0, ct).ConfigureAwait(false);
                _mem.LastPresentedCategories = ParsePresentedCategories(result);
                _mem.LastResolvedCategory = null;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "categories";
                RememberDeterministicRenderFromJson("categories", result, "inventory.categories", displayText);
                var answer = RenderDeterministicInventoryFromData("categories", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.categories", new[] { "documents.categories", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.categories", "documents.categories", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogDocumentsListAll:
            {
                var pageSize = Math.Clamp(GetIntArg(args, "pageSize") ?? 500, 1, 500);
                var result = CreateCanonicalDocumentsListJson(await _api.DocumentsListAsync(null, null, null, pageSize, 0, ct).ConfigureAwait(false));
                _mem.LastResolvedCategory = null;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "list";
                ResetFocusedDocumentIfNoDocumentItems(result);
                RememberDeterministicRenderFromJson("list", result, "inventory.list", displayText);
                var answer = RenderDeterministicInventoryFromData("list", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.list", new[] { "documents.list", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.list", "documents.list", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogDocumentsListByCategory:
            {
                var category = ResolveCategoryFromArgs(args);
                if (category is null)
                    return BuildDirectCommandFailure(language, displayText, "inventory.list", DeterministicAgentText.ToolFailureToolFailed(language) + " Missing categoryRef.", "documents.list");

                var pageSize = Math.Clamp(GetIntArg(args, "pageSize") ?? 500, 1, 500);
                var raw = await _api.DocumentsListAsync(category.CategoryPath, category.CategoryRef, null, pageSize, 0, ct).ConfigureAwait(false);
                var result = CreateCanonicalDocumentsListJsonCore(raw, category.CategoryPath);
                _mem.LastResolvedCategory = category;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "list";
                ResetFocusedDocumentIfNoDocumentItems(result);
                RememberDeterministicRenderFromJson("list", result, "inventory.list", displayText);
                var answer = RenderDeterministicInventoryFromData("list", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.list", new[] { "documents.list", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.list", "documents.list", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogDocumentsSearch:
            {
                var query = (GetStringArg(args, "query") ?? GetStringArg(args, "q") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(query))
                    return BuildDirectCommandFailure(language, displayText, "inventory.find", LocalizedStrings.NoDocumentsFound(language), "documents.search");

                var pageSize = Math.Clamp(GetIntArg(args, "pageSize") ?? 100, 1, 500);
                var result = CreateCanonicalDocumentsListJsonCore(await _api.DocumentsSearchAsync(query, null, null, pageSize, 0, ct).ConfigureAwait(false), scopePath: null, searchQuery: query);
                _mem.LastResolvedCategory = null;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "search";
                ResetFocusedDocumentIfNoDocumentItems(result);
                RememberDeterministicRenderFromJson("list", result, "inventory.find", displayText);
                var answer = RenderDeterministicInventoryFromData("list", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.find", new[] { "documents.search", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.find", "documents.search", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogStatsView:
            {
                var category = ResolveCategoryFromArgs(args);
                var result = await _api.DocumentsStatsAsync(category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastResolvedCategory = category;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "stats";
                RememberDeterministicRenderFromJson("stats", result, "inventory.stats", displayText);
                var answer = RenderDeterministicInventoryFromData("stats", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.stats", new[] { "documents.stats", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.stats", "documents.stats", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogTreeView:
            {
                var depth = Math.Clamp(GetIntArg(args, "depth") ?? 12, 1, 24);
                var format = (GetStringArg(args, "format") ?? "markdown").Trim();
                var result = await _api.DocumentsTreeAsync(null, null, depth, format, ct).ConfigureAwait(false);
                _mem.LastResolvedCategory = null;
                _mem.LastSummaryStatusSnapshot = null;
                _mem.LastInventoryAction = "tree";
                RememberDeterministicRenderFromJson("tree", result, "inventory.tree", displayText);
                var answer = RenderDeterministicInventoryFromData("tree", result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.tree", new[] { "documents.tree", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.tree", "documents.tree", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogSummariesMissingCount:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "inventory.summary_status", DeterministicAgentText.ToolFailureAdminRequired(language), "summary.status.count");

                var category = ResolveCategoryFromArgs(args);
                var result = await _api.AdminSummaryMissingCountAsync(category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = "missing"
                };
                UpdateSummaryStatusSnapshotFromJson(result);
                var rendered = CreateSummaryStatusJson(_mem.LastSummaryStatusSnapshot!);
                RememberDeterministicRenderFromJson("summary_status_count", rendered, "inventory.summary_status", displayText);
                var answer = RenderDeterministicInventoryFromData("summary_status_count", rendered, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.summary_status", new[] { "summary.status.count", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.summary_status", "summary.status.count", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogSummariesMissingList:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "inventory.summary_status", DeterministicAgentText.ToolFailureAdminRequired(language), "summary.status.list");

                var category = ResolveCategoryFromArgs(args);
                var pageSize = Math.Clamp(GetIntArg(args, "pageSize") ?? 100, 1, 500);
                var result = await _api.AdminSummaryMissingAsync(pageSize, 0, category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = "missing"
                };
                UpdateSummaryStatusSnapshotFromJson(result);
                UpdateLastListedDocumentsFromSummaryStatusSnapshot();
                var rendered = CreateSummaryStatusJson(_mem.LastSummaryStatusSnapshot!);
                RememberDeterministicRenderFromJson("summary_status_list", rendered, "inventory.summary_status", displayText);
                var answer = RenderDeterministicInventoryFromData("summary_status_list", rendered, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.summary_status", new[] { "summary.status.list", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.summary_status", "summary.status.list", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogSummariesPresentCount:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "inventory.summary_status", DeterministicAgentText.ToolFailureAdminRequired(language), "summary.present.count");

                var category = ResolveCategoryFromArgs(args);
                var result = await _api.AdminSummaryPresentCountAsync(category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = "present"
                };
                UpdateSummaryStatusSnapshotFromJson(result);
                var rendered = CreateSummaryStatusJson(_mem.LastSummaryStatusSnapshot!);
                RememberDeterministicRenderFromJson("summary_status_count", rendered, "inventory.summary_status", displayText);
                var answer = RenderDeterministicInventoryFromData("summary_status_count", rendered, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.summary_status", new[] { "summary.present.count", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.summary_status", "summary.present.count", "inventory.rendered");
            }

            case DirectCommandCatalog.CatalogSummariesPresentList:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "inventory.summary_status", DeterministicAgentText.ToolFailureAdminRequired(language), "summary.present.list");

                var category = ResolveCategoryFromArgs(args);
                var pageSize = Math.Clamp(GetIntArg(args, "pageSize") ?? 100, 1, 500);
                var result = await _api.AdminSummaryPresentAsync(pageSize, 0, category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = "present"
                };
                UpdateSummaryStatusSnapshotFromJson(result);
                UpdateLastListedDocumentsFromSummaryStatusSnapshot();
                var rendered = CreateSummaryStatusJson(_mem.LastSummaryStatusSnapshot!);
                RememberDeterministicRenderFromJson("summary_status_list", rendered, "inventory.summary_status", displayText);
                var answer = RenderDeterministicInventoryFromData("summary_status_list", rendered, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "inventory.summary_status", new[] { "summary.present.list", "inventory.rendered" });
                return BuildDirectCommandResult(answer, "inventory.summary_status", "summary.present.list", "inventory.rendered");
            }

            case DirectCommandCatalog.AdminCatalogRescan:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "admin.catalog.rescan_now", DeterministicAgentText.ToolFailureAdminRequired(language), "admin.catalog.rescan_now");

                var result = await _api.AdminCatalogRescanNowAsync(ct).ConfigureAwait(false);
                RememberCompletedAdminRescan(result);
                var answer = BuildAdminRescanCompletedAnswer(result, language).Trim();
                RememberDirectCommandState(answer, language, displayText, "admin.catalog.rescan_now", new[] { "admin.catalog.rescan_now" });
                return BuildDirectCommandResult(answer, "admin.catalog.rescan_now", "admin.catalog.rescan_now");
            }

            case DirectCommandCatalog.AdminIngestionReindexDocument:
            {
                if (!_api.HasAdminKey)
                    return BuildDirectCommandFailure(language, displayText, "admin.ingestion.reindex", DeterministicAgentText.ToolFailureAdminRequired(language), "admin.ingestion.reindex");

                var documentRef = (GetStringArg(args, "documentRef") ?? string.Empty).Trim();
                var docPath = (GetStringArg(args, "docPath") ?? string.Empty).Trim();
                var docId = (GetStringArg(args, "docId") ?? string.Empty).Trim();
                var docName = (GetStringArg(args, "docName") ?? GetStringArg(args, "displayName") ?? string.Empty).Trim();
                var referenceLabel = !string.IsNullOrWhiteSpace(documentRef)
                    ? documentRef
                    : !string.IsNullOrWhiteSpace(docPath)
                        ? docPath
                        : !string.IsNullOrWhiteSpace(docName)
                            ? docName
                            : docId;

                if (string.IsNullOrWhiteSpace(referenceLabel))
                    return BuildDirectCommandFailure(language, displayText, "admin.ingestion.reindex", DeterministicAgentText.AdminReindexDocumentNotFound(language, referenceLabel), "admin.ingestion.reindex");

                if (!string.IsNullOrWhiteSpace(docPath) && !LooksLikeReindexableDocumentPath(docPath))
                    return BuildDirectCommandFailure(language, displayText, "admin.ingestion.reindex", DeterministicAgentText.AdminReindexDocumentTargetIsCategory(language, referenceLabel), "admin.ingestion.reindex");

                ExplicitDocumentResolution resolution;
                if (!string.IsNullOrWhiteSpace(docId))
                {
                    resolution = await ResolveExplicitDocumentReferenceStrictAsync(docId, ct).ConfigureAwait(false);
                    if (resolution.IsResolved && resolution.Document is not null && !string.IsNullOrWhiteSpace(docPath)
                        && !string.Equals(resolution.Document.DocPath, docPath, StringComparison.OrdinalIgnoreCase))
                    {
                        resolution = new ExplicitDocumentResolution();
                    }
                }
                else
                {
                    var strictReference = !string.IsNullOrWhiteSpace(docPath) ? docPath : referenceLabel;
                    resolution = await ResolveExplicitDocumentReferenceStrictAsync(strictReference, ct).ConfigureAwait(false);
                }

                if (!resolution.IsResolved || resolution.Document is null)
                {
                    ResetFocusedDocumentSelection();
                    var failure = resolution.IsCategoryReference
                        ? DeterministicAgentText.AdminReindexDocumentTargetIsCategory(language, referenceLabel)
                        : resolution.IsAmbiguous
                            ? DeterministicAgentText.AdminReindexDocumentAmbiguous(language, referenceLabel)
                            : DeterministicAgentText.AdminReindexDocumentNotFound(language, referenceLabel);
                    return BuildDirectCommandFailure(language, displayText, "admin.ingestion.reindex", failure, "admin.ingestion.reindex");
                }

                var resolved = resolution.Document;
                if (!LooksLikeReindexableDocumentPath(resolved.DocPath))
                    return BuildDirectCommandFailure(language, displayText, "admin.ingestion.reindex", DeterministicAgentText.AdminReindexDocumentTargetIsCategory(language, referenceLabel), "admin.ingestion.reindex");

                var result = await _api.AdminIngestionReindexAsync(resolved.DocPath, ct).ConfigureAwait(false);
                var label = Path.GetFileName((resolved.DocPath ?? string.Empty).Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(label))
                    label = string.IsNullOrWhiteSpace(resolved.DocName) ? referenceLabel : resolved.DocName;
                var jobId = TryGetString(result, "jobId") ?? TryGetString(result, "JobId");
                _mem.LastAdminOperation = new ToolMemory.AdminOperationState
                {
                    OperationKind = "document_reindex",
                    DisplayLabel = label,
                    JobId = jobId,
                    DocumentRef = referenceLabel,
                    DocPath = resolved.DocPath,
                    Status = string.IsNullOrWhiteSpace(jobId) ? "queued" : "running",
                    IsCompleted = false,
                    IsSuccess = false,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow
                };

                var initialStatus = string.IsNullOrWhiteSpace(jobId) ? "queued" : "running";
                var answer = initialStatus == "queued"
                    ? DeterministicAgentText.AdminReindexQueued(language, label, jobId)
                    : DeterministicAgentText.AdminReindexRunning(language, label);
                RememberDirectCommandState(answer, language, displayText, "admin.ingestion.reindex", new[] { "admin.ingestion.reindex" });
                return BuildDirectCommandResult(
                    answer,
                    "admin.ingestion.reindex",
                    new DirectCommandTrackedJob
                    {
                        JobId = jobId ?? string.Empty,
                        JobType = "ingestion",
                        DisplayLabel = label,
                        Status = initialStatus
                    },
                    "admin.ingestion.reindex");
            }

            default:
                return BuildDirectCommandFailure(language, displayText, "meta.help", $"Unknown direct command: {commandId}");
        }
    }

    private ToolMemory.CategorySnapshot? ResolveCategoryFromArgs(JsonElement args)
    {
        var categoryRef = GetStringArg(args, "categoryRef");
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetStringArg(args, "path");
        return ResolveCategorySnapshotFromReference(categoryRef, categoryPath);
    }

    private void ResetFocusedDocumentSelection()
    {
        _mem.LastFocusedDocument = null;
        _mem.LastRequestedDocumentRef = null;
    }

    private void ResetFocusedDocumentIfNoDocumentItems(JsonElement result)
    {
        if (result.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0)
        {
            return;
        }

        ResetFocusedDocumentSelection();
    }

    private DirectCommandExecutionResult BuildDirectCommandFailure(string language, string displayText, string routerIntent, string answer, params string[] toolNames)
    {
        RememberDirectCommandState(answer, language, displayText, routerIntent, toolNames);
        return new DirectCommandExecutionResult
        {
            FinalAnswer = answer,
            RouterIntent = routerIntent,
            ToolNames = toolNames
        };
    }

    private static DirectCommandExecutionResult BuildDirectCommandResult(string answer, string routerIntent, params string[] toolNames)
        => new()
        {
            FinalAnswer = answer,
            RouterIntent = routerIntent,
            ToolNames = toolNames
        };

    private static DirectCommandExecutionResult BuildDirectCommandResult(string answer, string routerIntent, DirectCommandTrackedJob trackedJob, params string[] toolNames)
        => new()
        {
            FinalAnswer = answer,
            RouterIntent = routerIntent,
            ToolNames = toolNames,
            TrackedJob = trackedJob
        };

    private void RememberDirectCommandState(string answer, string language, string displayText, string routerIntent, IEnumerable<string> toolNames)
    {
        _mem.LastLanguage = language;
        _mem.LastUserDetectedLanguage = language;
        _mem.LastAnswerLanguage = language;
        _mem.LastUserMessage = displayText;
        _mem.LastAssistantAnswer = answer;
        _mem.LastRouterIntent = routerIntent;
        _mem.LastToolNames = toolNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _mem.StagedDirectCommand = null;
    }
}
