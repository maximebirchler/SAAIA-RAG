using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public static class ToolManifest
{
    // IMPORTANT: outil “manuel” (= toolbook) : guide l’IA sans phrases figées.
    // C’est volontairement descriptif et stable.
    public static string BuildManifestJson()
    {
        var manifest = new
        {
            tools = new object[]
            {
                new {
                    name = "documents.list",
                    access = "user",
                    description = "Lister les documents indexés (documents uniques, pas des chunks). Paginé.",
                    args_schema = new { category = "string|null", q = "string|null", limit = "int", offset = "int" },
                    result_schema = new { items = "DocumentItem[]", nextOffset = "int|null", total = "int|null" }
                },
                new {
                    name = "documents.search",
                    access = "user",
                    description = "Rechercher un document par nom/chemin (paginé).",
                    args_schema = new { q = "string", category = "string|null", limit = "int", offset = "int" },
                    result_schema = new { items = "DocumentItem[]", nextOffset = "int|null", total = "int|null" }
                },
                new {
                    name = "documents.get",
                    access = "user",
                    description = "Obtenir les métadonnées d’un document (pages/dates/catégories).",
                    args_schema = new { docId = "string" },
                    result_schema = new { doc = "DocumentItem" }
                },
                new {
                    name = "rag.categories",
                    access = "user",
                    description = "Lister les catégories dynamiques (dossiers sous documents/).",
                    args_schema = new { },
                    result_schema = new { categories = "string[]" }
                },
                new {
                    name = "rag.search",
                    access = "user",
                    description = "Recherche documentaire sourcée (doc/page/extrait). Utiliser pour questions techniques/factuelles.",
                    args_schema = new { query = "string", topK = "int", category = "string|null" },
                    result_schema = new { hits = "Hit[]" }
                },
                new {
                    name = "sources.resolve",
                    access = "user",
                    description = "Résoudre une demande du type 'source du PDF34' en doc/page (p.1 si demande après inventaire).",
                    args_schema = new { pdfRef = "string" },
                    result_schema = new { source = "SourceRef|null" }
                }
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    public static string ToolbookText => @"
RÈGLES (Toolbook) — à suivre sans dépendre de mots-clés exacts :
- Inventaire (documents/catégories) => tools documents.list/documents.search ou rag.categories. PAS de rag.search.
- Question technique/factuelle basée sur corpus interne (normes, exigences, chiffres) => rag.search (mode strict recommandé).
- Conversationnel (qui es-tu, aide générale) => pas d’outils, pas de sources.
- Quand une liste est longue : paginer et proposer de continuer; l’utilisateur peut dire 'reprends à partir de PDF34'.
- Langue : répondre dans la langue de la question.
- Ne pas écrire de phrases figées dans le code : le texte final est rédigé par l’IA.
";
}