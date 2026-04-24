using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public static class ToolManifest
{
    public sealed record ToolDefinition(
        string Name,
        string Access,
        string Description,
        IReadOnlyDictionary<string, string> ArgsSchema);

    public sealed record ToolContractValidationResult(
        IReadOnlyList<string> MissingInManifest,
        IReadOnlyList<string> MissingInRuntime)
    {
        public bool IsValid => MissingInManifest.Count == 0 && MissingInRuntime.Count == 0;

        public string FormatErrorMessage()
        {
            if (IsValid)
                return "Tool manifest/runtime contract is consistent.";

            var parts = new List<string>();
            if (MissingInManifest.Count > 0)
                parts.Add("runtime-only tools: " + string.Join(", ", MissingInManifest));
            if (MissingInRuntime.Count > 0)
                parts.Add("manifest-only tools: " + string.Join(", ", MissingInRuntime));
            return "Tool manifest/runtime contract mismatch — " + string.Join("; ", parts);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptySchema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly ToolDefinition[] _definitions =
    {
        new("documents.list", "user", "List indexed documents with pagination and optional filters.", Schema(("q", "string|null"), ("categoryPath", "string|null"), ("categoryRef", "string|null"), ("changedSince", "string|null"), ("limit", "int"), ("offset", "int"))),
        new("documents.search", "user", "Search documents by name/path. Paginated.", Schema(("q", "string"), ("categoryPath", "string|null"), ("categoryRef", "string|null"), ("limit", "int"), ("offset", "int"))),
        new("documents.get", "user", "Get document metadata for a document reference (PDFxx, index, docId, docPath or exact file name).", Schema(("docRef", "string"))),
        new("documents.count", "user", "Count indexed documents globally or with filters.", Schema(("categoryPath", "string|null"), ("categoryRef", "string|null"), ("q", "string|null"))),
        new("documents.categories", "user", "List categories from the catalog snapshot. Top-level categories are returned in stable display order and may include explicit multilingual aliases.", Schema(("path", "string|null"), ("categoryRef", "string|null"), ("limit", "int|null"), ("offset", "int|null"))),
        new("documents.tree", "user", "Get the multi-level category tree for catalog structure exploration.", Schema(("path", "string|null"), ("categoryRef", "string|null"), ("depth", "int|null"), ("format", "json|markdown"), ("limit", "int|null"), ("offset", "int|null"))),
        new("documents.stats", "user", "Get inventory statistics for the indexed catalog.", Schema(("path", "string|null"), ("categoryRef", "string|null"))),
        new("documents.empty_count", "admin", "Count empty folders on the server filesystem for admin/health diagnostics.", Schema(("path", "string|null"))),
        new("documents.empty_list", "admin", "List empty folders on the server filesystem for admin/health diagnostics.", Schema(("path", "string|null"), ("limit", "int|null"), ("offset", "int|null"))),
        new("sources.resolve", "user", "Resolve an explicit source reference (e.g. PDF34, source of document 2). Use only when the user explicitly asks for a source, link, opening action or PDF reference.", Schema(("ref", "string|null"), ("pdfRef", "string|null"))),
        new("rag.search", "user", "RAG retrieval for factual or technical questions. Returns snippets with document/page references.", Schema(("query", "string"), ("topK", "int|null"), ("categoryPath", "string|null"), ("category", "string|null"), ("filters", "object|null"), ("mode", "auto|strict|standard"))),
        new("rag.multi_search", "user", "Multi-query retrieval with dedup/diversity for synthesis across several documents.", Schema(("queries", "string[]"), ("topK", "int|null"), ("categoryPath", "string|null"), ("category", "string|null"), ("filters", "object|null"), ("diversity", "double|null"), ("mode", "auto|strict|standard|null"))),
        new("rag.summarize_live", "user", "Live non-stored summary for a single document.", Schema(("docRef", "string"), ("level", "short|medium|long"), ("strategy", "about|summary|store"), ("language", "auto|fr|en"), ("maxWords", "int|null"), ("maxChunks", "int|null"), ("maxBatches", "int|null"), ("maxCharsPerBatch", "int|null"))),
        new("summary.get", "user", "Read a stored admin summary for a document.", Schema(("docRef", "string"), ("level", "medium"))),
        new("summary.exists", "user", "Check whether a stored admin summary exists and is fresh.", Schema(("docRef", "string"), ("level", "medium"))),
        new("summary.search", "user", "Search inside stored summaries.", Schema(("q", "string"), ("limit", "int"), ("offset", "int"))),
        new("summary.status.count", "admin", "Count indexed documents without a fresh stored summary (missing or stale).", Schema(("categoryPath", "string|null"), ("categoryRef", "string|null"))),
        new("summary.status.list", "admin", "List indexed documents without a fresh stored summary (missing or stale).", Schema(("limit", "int"), ("offset", "int"), ("categoryPath", "string|null"), ("categoryRef", "string|null"))),
        new("summary.present.count", "admin", "Count indexed documents with a fresh stored summary.", Schema(("categoryPath", "string|null"), ("categoryRef", "string|null"))),
        new("summary.present.list", "admin", "List indexed documents with a fresh stored summary.", Schema(("limit", "int"), ("offset", "int"), ("categoryPath", "string|null"), ("categoryRef", "string|null"))),
        new("export.create", "user", "Create an export artifact from provided content.", Schema(("format", "txt|md|csv|docx"), ("title", "string|null"), ("fileName", "string|null"), ("content", "string"))),
        new("support.bundle", "user", "Create a support bundle with logs and redacted config.", Schema(("include", "string[]|null"))),
        new("diagnostic.performance", "user", "Return router/tools/writer timing diagnostics.", Schema(("lastN", "int|null"))),
        new("admin.summary.missing", "admin", "List indexed documents without a fresh stored summary.", Schema(("limit", "int"), ("offset", "int"), ("categoryPath", "string|null"), ("categoryRef", "string|null"))),
        new("admin.summary.request", "admin", "Create a summary request job for a document.", Schema(("docRef", "string"), ("level", "medium"))),
        new("admin.summary.submit", "admin", "Submit a generated admin summary for storage.", Schema(("jobId", "string|null"), ("docRef", "string"), ("level", "medium"), ("docLanguage", "string"), ("sourceHash", "string"), ("summaryText", "string"), ("meta", "object|null"))),
        new("admin.summary.status", "admin", "Get status of an admin summary job.", Schema(("jobId", "string"))),
        new("admin.summary.delete", "admin", "Delete a stored summary.", Schema(("docRef", "string"), ("level", "medium"))),
        new("admin.catalog.rescan_now", "admin", "Force a catalog rescan/reconcile.", EmptySchema),
        new("admin.catalog.health", "admin", "Get catalog/index health information.", EmptySchema),
        new("admin.jobs.list", "admin", "List admin jobs with status and errors.", Schema(("type", "string|null"), ("limit", "int"), ("offset", "int"))),
        new("admin.jobs.cancel", "admin", "Cancel an admin job.", Schema(("jobId", "string"))),
        new("admin.audit", "admin", "List recent audit events for admin diagnostics.", Schema(("action", "string|null"), ("target", "string|null"), ("since", "string|null"), ("until", "string|null"), ("limit", "int"), ("offset", "int"))),
        new("admin.summary.generate", "admin", "Generate or queue an admin summary generation for a document.", Schema(("docRef", "string"), ("level", "medium"), ("force", "bool|null"))),
        new("rag.debug.scroll", "admin", "Scroll/paginate raw chunks for debugging RAG ingestion.", Schema(("docRef", "string|null"), ("docPath", "string|null"), ("cursor", "string|null"), ("limit", "int|null"))),
        new("admin.qdrant.health", "admin", "Qdrant health checks (if available on deployment).", EmptySchema)
    };

    private static readonly ToolDefinition[] _conversationDefinitions = _definitions
        .Where(x => string.Equals(x.Access, "user", StringComparison.OrdinalIgnoreCase))
        .ToArray();


    private static readonly IReadOnlySet<string> _knownToolNames = new HashSet<string>(_definitions.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> _adminToolNames = new HashSet<string>(_definitions.Where(x => string.Equals(x.Access, "admin", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ToolDefinition> Definitions => _definitions;
    public static IReadOnlySet<string> KnownToolNames => _knownToolNames;
    public static IReadOnlySet<string> AdminToolNames => _adminToolNames;

    public static string BuildManifestJson()
        => BuildManifestJson(_definitions, "v3.1");

    public static string BuildConversationManifestJson()
        => BuildManifestJson(_conversationDefinitions, "v3.1");

    private static string BuildManifestJson(IEnumerable<ToolDefinition> definitions, string version)
    {
        var manifest = new
        {
            version,
            tools = definitions.Select(x => new
            {
                name = x.Name,
                access = x.Access,
                description = x.Description,
                args_schema = x.ArgsSchema
            }).ToArray()
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    public static ToolContractValidationResult ValidateRuntimeCatalog(IEnumerable<string> runtimeToolNames)
    {
        var runtime = new HashSet<string>(runtimeToolNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var missingInManifest = runtime.Where(x => !_knownToolNames.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var missingInRuntime = _knownToolNames.Where(x => !runtime.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ToolContractValidationResult(missingInManifest, missingInRuntime);
    }

    public static bool IsKnownTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && _knownToolNames.Contains(toolName);

    public static bool IsAdminTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && _adminToolNames.Contains(toolName);

    public static string ToolbookText => string.Join("\n", new[]
    {
        "SAAIA tools rules v3.1",
        "- Be flexible but grounded: use tools to obtain data, do not invent document metadata or source links.",
        "- The session mode is controlled by meta.set_mode; use it only for explicit requests to switch auto/standard/strict behavior.",
        "- For one-document content questions, documents.get only resolves metadata. Prefer rag.summarize_live (or summary.get if a stored summary is explicitly needed).",
        "- If the user asks to verify whether a summary is already stored, use summary.exists and summary.get only. Do not regenerate.",
        "- If the user explicitly asks to store or refresh a reusable summary, use admin.summary.* with admin access.",
        "- Use sources.resolve only for explicit source requests, PDF references, open-file actions or 'where is document N' style questions.",
        "- Inventory requests (count/categories/list/find/tree/stats/changed-since) are answered from indexed documents/catalog data, not from RAG chunks. Empty-folder checks are separate admin/server diagnostics.",
        "- For one-document content questions ('de quoi parle le document 2 ?', 'résume le document 2'), do not stop at sources.resolve/documents.get. Use summary.exists/summary.get or rag.summarize_live.",
        "- For explicit content questions or factual questions inside documents, use rag.search or rag.multi_search.",
        "- For stored summary administration, use admin.summary.*.",
        "- diagnostic.performance returns router/tools/writer timings; use it only for explicit diagnostics/performance questions.",
        "- support.bundle creates a support zip; use it only when the user explicitly asks for a support bundle/export for troubleshooting.",
        "- Empty-folder tools are admin/health filesystem diagnostics, not nominal user inventory tools.",
        "- Never call admin tools without an active admin session.",
        "- Do not invent tools: stay strictly inside the manifest."
    });

    public static string ConversationToolbookText => string.Join("\n", new[]
    {
        "SAAIA conversation tools rules v3.1",
        "- The free conversation rail is strictly user-only, even if an admin session exists.",
        "- Never plan or call admin tools in free conversation. Redirect to guided/admin surfaces instead.",
        "- Use tools ONLY from the manifest.",
        "- Use meta.set_mode only for explicit session-mode changes (auto/standard/strict).",
        "- Use sources.resolve only for explicit source, link, opening or PDF-reference requests.",
        "- Inventory requests stay on count/list/find/tree/categories/stats tools, never on RAG tools.",
        "- Do not invent tools or admin-only alternatives."
    });

    private static IReadOnlyDictionary<string, string> Schema(params (string Name, string Type)[] entries)
    {
        if (entries is null || entries.Length == 0)
            return EmptySchema;

        return entries.ToDictionary(x => x.Name, x => x.Type, StringComparer.OrdinalIgnoreCase);
    }
}
