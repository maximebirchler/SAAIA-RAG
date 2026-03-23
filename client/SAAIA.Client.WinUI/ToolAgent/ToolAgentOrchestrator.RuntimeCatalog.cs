using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private delegate Task<JsonElement> ToolHandler(JsonElement args, CancellationToken ct);

    private static readonly string[] RuntimeToolNames =
    {
        "documents.list",
        "documents.search",
        "documents.get",
        "documents.count",
        "documents.categories",
        "documents.tree",
        "documents.stats",
        "documents.empty_count",
        "documents.empty_list",
        "rag.search",
        "rag.multi_search",
        "rag.summarize_live",
        "sources.resolve",
        "summary.get",
        "summary.exists",
        "summary.search",
        "summary.status.count",
        "summary.status.list",
        "summary.present.count",
        "summary.present.list",
        "admin.summary.missing",
        "admin.summary.request",
        "admin.summary.submit",
        "admin.summary.status",
        "admin.summary.delete",
        "admin.summary.generate",
        "admin.catalog.health",
        "admin.catalog.rescan_now",
        "admin.ingestion.reindex",
        "admin.jobs.list",
        "admin.jobs.cancel",
        "diagnostic.performance",
        "export.create",
        "support.bundle",
        "rag.debug.scroll",
        "admin.qdrant.health"
    };

    private IReadOnlyDictionary<string, ToolHandler>? _toolHandlers;

    internal static IReadOnlyList<string> GetExecutableToolNamesForTests()
        => RuntimeToolNames.ToArray();

    internal static void EnsureToolContractConsistency()
    {
        var report = ToolManifest.ValidateRuntimeCatalog(RuntimeToolNames);
        if (!report.IsValid)
            throw new InvalidOperationException(report.FormatErrorMessage());
    }

    private IReadOnlyDictionary<string, ToolHandler> GetOrCreateToolHandlers()
    {
        if (_toolHandlers is not null)
            return _toolHandlers;

        EnsureToolContractConsistency();

        var handlers = new Dictionary<string, ToolHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in RuntimeToolNames)
            handlers[toolName] = CreateToolHandler(toolName);

        _toolHandlers = handlers;
        return _toolHandlers;
    }

    private ToolHandler CreateToolHandler(string toolName)
        => toolName switch
        {
            "documents.list" => ExecDocumentsListAsync,
            "documents.search" => ExecDocumentsSearchAsync,
            "documents.get" => ExecDocumentsGetResolvedAsync,
            "documents.count" => ExecDocumentsCountAsync,
            "documents.categories" => ExecDocumentsCategoriesAsync,
            "documents.tree" => ExecDocumentsTreeAsync,
            "documents.stats" => ExecDocumentsStatsAsync,
            "documents.empty_count" => ExecDocumentsEmptyCountAsync,
            "documents.empty_list" => ExecDocumentsEmptyListAsync,
            "rag.search" => ExecRagSearchAsync,
            "rag.multi_search" => ExecRagMultiSearchAsync,
            "rag.summarize_live" => ExecRagSummarizeLiveAsync,
            "sources.resolve" => (args, _) => Task.FromResult(ExecSourcesResolveV2(args)),
            "summary.get" => ExecSummaryGetAsync,
            "summary.exists" => ExecSummaryExistsAsync,
            "summary.search" => ExecSummarySearchAsync,
            "summary.status.count" => ExecSummaryStatusCountAsync,
            "summary.status.list" => ExecSummaryStatusListAsync,
            "summary.present.count" => ExecSummaryPresentCountAsync,
            "summary.present.list" => ExecSummaryPresentListAsync,
            "admin.summary.missing" => ExecAdminSummaryMissingAsync,
            "admin.summary.request" => ExecAdminSummaryRequestAsync,
            "admin.summary.submit" => ExecAdminSummarySubmitAsync,
            "admin.summary.status" => ExecAdminSummaryStatusAsync,
            "admin.summary.delete" => ExecAdminSummaryDeleteAsync,
            "admin.summary.generate" => ExecAdminSummaryGenerateAsync,
            "admin.catalog.health" => (_, ct) => ExecAdminCatalogHealthAsync(ct),
            "admin.catalog.rescan_now" => (_, ct) => ExecAdminCatalogRescanNowAsync(ct),
            "admin.ingestion.reindex" => ExecAdminIngestionReindexAsync,
            "admin.jobs.list" => ExecAdminJobsListAsync,
            "admin.jobs.cancel" => ExecAdminJobsCancelAsync,
            "diagnostic.performance" => (args, _) => Task.FromResult(ExecDiagnosticPerformance(args)),
            "export.create" => (args, _) => Task.FromResult(ExecExportCreate(args)),
            "support.bundle" => ExecSupportBundleAsync,
            "rag.debug.scroll" => ExecRagDebugScrollAsync,
            "admin.qdrant.health" => (_, ct) => ExecAdminQdrantHealthAsync(ct),
            _ => throw new InvalidOperationException($"No runtime handler registered for tool '{toolName}'.")
        };
}
