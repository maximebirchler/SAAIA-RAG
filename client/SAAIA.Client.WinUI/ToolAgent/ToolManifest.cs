using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public static class ToolManifest
{
    public static string BuildManifestJson()
    {
        var manifest = new
        {
            version = "v2.8.1",
            tools = new object[]
            {
                new { name = "documents.list", access = "user", description = "List indexed documents with pagination and optional filters.", args_schema = new { q = "string|null", categoryPath = "string|null", limit = "int", offset = "int" } },
                new { name = "documents.search", access = "user", description = "Search documents by name/path. Paginated.", args_schema = new { q = "string", limit = "int", offset = "int" } },
                new { name = "documents.get", access = "user", description = "Get document metadata for a document reference (PDFxx, index, docId, docPath or exact file name).", args_schema = new { docRef = "string" } },
                new { name = "documents.count", access = "user", description = "Count indexed documents globally or with filters.", args_schema = new { categoryPath = "string|null", q = "string|null" } },
                new { name = "documents.tree", access = "user", description = "Get the multi-level category tree. Prefer this for arborescence/tree requests.", args_schema = new { path = "string|null", depth = "int|null", format = "json|markdown", limit = "int|null", offset = "int|null" } },
                new { name = "documents.stats", access = "user", description = "Get inventory stats (total docs, depth counts, top categories).", args_schema = new { path = "string|null" } },
                new { name = "sources.resolve", access = "user", description = "Resolve an explicit source reference (e.g. PDF34, source of document 2). Use ONLY when the user explicitly asks for a source/open/link.", args_schema = new { @ref = "string" } },
                new { name = "rag.search", access = "user", description = "RAG retrieval for factual/technical questions. Returns snippets with document/page references.", args_schema = new { query = "string", topK = "int|null", filters = "object|null", mode = "auto|strict|standard" } },
                new { name = "rag.multi_search", access = "user", description = "Multi-query retrieval with dedup/diversity for synthesis across several documents.", args_schema = new { queries = "string[]", topK = "int|null", filters = "object|null", diversity = "double|null" } },
                new { name = "rag.summarize_live", access = "user", description = "Live non-stored summary for a single document.", args_schema = new { docRef = "string", level = "short|medium", strategy = "skim", language = "auto|fr|en", maxWords = "int|null" } },
                new { name = "summary.get", access = "user", description = "Read a stored admin summary for a document.", args_schema = new { docRef = "string", level = "medium" } },
                new { name = "summary.exists", access = "user", description = "Check whether a stored admin summary exists and is fresh.", args_schema = new { docRef = "string", level = "medium" } },
                new { name = "summary.search", access = "user", description = "Search inside stored summaries.", args_schema = new { q = "string", limit = "int", offset = "int" } },
                new { name = "export.create", access = "user", description = "Create an export artifact from provided content.", args_schema = new { format = "txt|md|csv|docx", title = "string|null", content = "string" } },
                new { name = "support.bundle", access = "user", description = "Create a support bundle with logs and redacted config.", args_schema = new { include = "string[]|null" } },
                new { name = "diagnostic.performance", access = "user", description = "Return router/tools/writer timing diagnostics.", args_schema = new { lastN = "int|null" } },
                new { name = "admin.summary.missing", access = "admin", description = "List indexed documents without a fresh stored summary.", args_schema = new { limit = "int", offset = "int", categoryPath = "string|null" } },
                new { name = "admin.summary.request", access = "admin", description = "Create a summary request job for a document.", args_schema = new { docRef = "string", level = "medium" } },
                new { name = "admin.summary.submit", access = "admin", description = "Submit a generated admin summary for storage.", args_schema = new { jobId = "string|null", docRef = "string", level = "medium", docLanguage = "string", sourceHash = "string", summaryText = "string", meta = "object|null" } },
                new { name = "admin.summary.status", access = "admin", description = "Get status of an admin summary job.", args_schema = new { jobId = "string" } },
                new { name = "admin.summary.delete", access = "admin", description = "Delete a stored summary.", args_schema = new { docRef = "string", level = "medium" } },
                new { name = "admin.catalog.rescan_now", access = "admin", description = "Force a catalog rescan/reconcile.", args_schema = new { } },
                new { name = "admin.catalog.health", access = "admin", description = "Get catalog/index health information.", args_schema = new { } },
                new { name = "admin.ingestion.reindex", access = "admin", description = "Re-trigger ingestion for one document.", args_schema = new { docRef = "string" } },
                new { name = "admin.jobs.list", access = "admin", description = "List admin jobs with status and errors.", args_schema = new { type = "string|null", limit = "int", offset = "int" } },
                new { name = "admin.jobs.cancel", access = "admin", description = "Cancel an admin job.", args_schema = new { jobId = "string" } },
                new { name = "admin.summary.generate", access = "admin", description = "Generate or queue an admin summary generation for a document.", args_schema = new { docRef = "string", level = "medium", force = "bool|null" } },
                new { name = "rag.debug.scroll", access = "admin", description = "Scroll/paginate raw chunks for debugging RAG ingestion.", args_schema = new { docRef = "string", limit = "int", offset = "int" } },
                new { name = "admin.qdrant.health", access = "admin", description = "Qdrant health checks (if available on deployment).", args_schema = new { } }
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    public static string ToolbookText => @"
RÈGLES OUTILS SAAIA v2.8.1
- L'objectif est d'être libre mais cadré : aucun texte métier ne doit être codé en dur dans l'orchestrateur.
- Quand l'utilisateur demande de quoi parle un document, préfère documents.get + summary.get / summary.exists / rag.summarize_live selon le cas.
- N'utilise sources.resolve QUE si l'utilisateur demande explicitement une source, un lien, l'ouverture d'un PDF ou une référence précise.
- Pour l'inventaire :
  - combien => documents.count
  - liste / trouve / recherche => documents.list ou documents.search
  - arborescence / tree / structure => documents.tree
  - statistiques => documents.stats
- Pour une question technique factuelle basée sur le corpus => rag.search.
- Pour une synthèse multi-documents => rag.multi_search.
- Pour un résumé stocké => summary.get / summary.exists.
- Pour l'administration des résumés => admin.summary.*.
- Répondre dans la langue de l'utilisateur et ne pas inventer de docPath/docName absents des toolResults.
";
}
