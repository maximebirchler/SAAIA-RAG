using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string NavigationContextBatchToolName =
        "documents_context_batch";
    private const string ContinuePaginationToolName =
        "continue_document_pagination";
    private const string ResolveNavigationAnchorsToolName =
        "resolve_navigation_anchors";
    private const string ExpandDocumentContextToolName =
        "expand_document_context";
    private const string RefineNavigationToolName =
        "refine_document_navigation";
    private const string RefineFocusedDocumentSearchToolName =
        "refine_focused_document_search";
    private const string StartContentCardResearchToolName =
        "start_content_card_research";
    private const string StartDocumentSearchToolName =
        "start_document_search";

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildResearchTransitionTools(
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            int maximumWorkingEvidenceItems)
        => BuildResearchTransitionActionTools(
            bundle,
            resolvedNavigationEvidenceIds,
            executedRequests,
            maximumWorkingEvidenceItems);

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildResearchTransitionActionTools(
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            int maximumWorkingEvidenceItems)
    {
        var batchTool = BuildNavigationContextBatchTool(
            bundle,
            resolvedNavigationEvidenceIds,
            maximumWorkingEvidenceItems);
        var evidenceIds = batchTool is null
            ? Array.Empty<string>()
            : batchTool.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        var maximum = Math.Max(1, maximumWorkingEvidenceItems);
        var maximumPaginationRoutes = Math.Min(5, maximum);
        var paginationRoutes = BuildPaginationContinuationOptions(
            executedRequests,
            maximumPaginationRoutes);
        var documentFocusOptions = BuildDocumentFocusOptions(
            bundle,
            maximumWorkingEvidenceItems);
        var documentFocusEvidenceIds = new[]
            {
                GlobalDocumentFocusEvidenceId
            }
            .Concat(documentFocusOptions.Select(static option =>
                option.EvidenceId))
            .ToArray();
        var tools = new List<SourceBackedAgentToolDefinition>(7);
        if (paginationRoutes.Count > 0)
        {
            tools.Add(new SourceBackedAgentToolDefinition(
                ContinuePaginationToolName,
                "Atteint la prochaine page inedite d'une route deja ouverte que tu choisis. "
                + "Si la meme query, le meme inventaire et les memes filtres restent utiles, "
                + "c'est cette action qui les poursuit; les rouvrir avec un outil de nouvelle "
                + "route recommencerait a offset 0 et repeterait une page consommee. Compare "
                + "librement son rendement mesure aux autres actions; le code ne change que "
                + "l'offset en recopiant nextOffset. "
                + DescribePaginationContinuationOptions(
                    executedRequests,
                    maximumPaginationRoutes),
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        paginationRouteId = new
                        {
                            type = "string",
                            description =
                                "Identifiant de la route exacte a poursuivre.",
                            @enum = paginationRoutes.Select(static route =>
                                route.RouteId).ToArray()
                        }
                    },
                    required = new[] { "paginationRouteId" },
                    additionalProperties = false
                }, ClientJson.CamelCase)));
        }

        if (evidenceIds.Length > 0)
        {
            tools.Add(new SourceBackedAgentToolDefinition(
                ResolveNavigationAnchorsToolName,
                "Transforme en passages citables uniquement les ancres visibles dont le libelle "
                + "nomme reellement une instance utile du type recherche. Tu peux resoudre le "
                + "sous-ensemble utile maintenant puis completer le vivier avec une autre action; "
                + "il n'est pas necessaire que ce seul lot satisfasse tout le besoin final. Cette "
                + "action conserve exactement les identites visibles selectionnees; ouvrir une "
                + "nouvelle route ne les materialise pas.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        evidenceIds = new
                        {
                            type = "array",
                            description =
                                "Ancres qui nomment reellement des instances du type cible.",
                            items = new
                            {
                                type = "string",
                                @enum = evidenceIds
                            },
                            minItems = 1,
                            maxItems = evidenceIds.Length,
                            uniqueItems = true
                        }
                    },
                    required = new[] { "evidenceIds" },
                    additionalProperties = false
                }, ClientJson.CamelCase)));
        }
        if (documentFocusOptions.Count > 0)
        {
            tools.Add(new SourceBackedAgentToolDefinition(
                ExpandDocumentContextToolName,
                "Lit des passages complementaires autour d'une preuve citable deja visible, "
                + "dans exactement le meme document et autour du meme chunk ou des memes pages. "
                + "Choisis cette action lorsque le besoin restant appartient au meme item ou "
                + "document deja localise. Le code copie mecaniquement l'identite de la preuve; "
                + "il ne choisit ni le document ni la pertinence semantique. Pour rechercher un "
                + "autre document ou item, choisis plutot une action de recherche globale.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        documentFocusEvidenceId = new
                        {
                            type = "string",
                            description =
                                "EvidenceId visible dont il faut lire le contexte documentaire complementaire.",
                            @enum = documentFocusOptions
                                .Select(static option => option.EvidenceId)
                                .ToArray()
                        }
                    },
                    required = new[] { "documentFocusEvidenceId" },
                    additionalProperties = false
                }, ClientJson.CamelCase)));
            tools.Add(new SourceBackedAgentToolDefinition(
                RefineFocusedDocumentSearchToolName,
                "Recherche des passages semantiquement pertinents dans tout le corps du "
                + "document d'une preuve visible. Choisis cette action quand le document est "
                + "deja le bon mais que la page du contenu manquant est inconnue. Le code copie "
                + "mecaniquement docId et docPath depuis l'EvidenceId que tu choisis; il ne "
                + "choisit ni le document, ni la query, ni la pertinence. Pour lire le voisinage "
                + "d'un passage connu, choisis expand_document_context. Pour cartographier "
                + "titres, sommaire ou index, choisis refine_document_navigation. Pour quitter "
                + "le document, choisis start_document_search.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        documentFocusEvidenceId = new
                        {
                            type = "string",
                            description =
                                "EvidenceId visible dont le document exact doit etre conserve.",
                            @enum = documentFocusOptions
                                .Select(static option => option.EvidenceId)
                                .ToArray()
                        },
                        query = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 120,
                            description =
                                "Termes semantiques susceptibles d'apparaitre dans le corps de la source, dans sa langue si utile."
                        },
                        limit = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum
                        }
                    },
                    required = new[]
                    {
                        "documentFocusEvidenceId",
                        "query",
                        "limit"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase)));
        }
        tools.Add(new SourceBackedAgentToolDefinition(
            RefineNavigationToolName,
            "Laisse les ancres visibles non resolues et explore de nouvelles ancres. Les "
            + "preuves deja acquises restent disponibles. Utilise une query "
            + "seulement si ses termes ont une chance d'apparaitre dans les sources; laisse-la "
            + "vide pour une nouvelle vue large. Un role deficient peut guider des noms "
            + "d'instances, mais ne concatene pas les axes ou la forme du livrable. "
            + "Choisis explicitement si cette navigation preserve le document d'une preuve "
            + "visible ou repart globalement. "
            + DescribeDocumentFocusOptions(documentFocusOptions),
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    documentFocusEvidenceId = new
                    {
                        type = "string",
                        description =
                            "GLOBAL pour explorer d'autres documents, ou EvidenceId publie pour conserver exactement son document.",
                        @enum = documentFocusEvidenceIds
                    },
                    query = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 120,
                        description =
                            "Termes source, ou chaine vide pour une exploration large."
                    },
                    navigationKind = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "navigation_entry",
                            "title_anchor",
                            "all"
                        }
                    },
                    limit = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum
                    }
                },
                required = new[]
                {
                    "documentFocusEvidenceId",
                    "query",
                    "navigationKind",
                    "limit"
                },
                additionalProperties = false
            }, ClientJson.CamelCase)));
        tools.Add(new SourceBackedAgentToolDefinition(
            StartContentCardResearchToolName,
            "Laisse les ancres visibles non resolues et ouvre une nouvelle route de cartes "
            + "canoniques. Les preuves deja acquises restent disponibles. "
            + "Avec query vide, l'inventaire decouvre des noms inconnus; avec query, le filtre "
            + "est lexical. Cet outil recommence a offset 0: ne l'utilise pas pour reproduire "
            + "une route paginee deja ouverte avec la meme query et le meme inventoryMode; "
            + "continue_document_pagination atteint alors sa page inedite. Ne recopie jamais "
            + "la forme du livrable dans query; vise plutot des noms d'instances plausibles "
            + "du role deficient, sans ajouter de contrainte au besoin.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 120,
                        description =
                            "Noms d'instances susceptibles d'etre des titres de preuves individuelles; chaine vide pour un inventaire large. Ne concatene pas les axes ou la forme du livrable."
                    },
                    inventoryMode = new
                    {
                        type = "string",
                        description =
                            "Avec query vide: representative explore des positions reparties; ordered suit l'ordre stable des sources.",
                        @enum = new[] { "ordered", "representative" }
                    },
                    limit = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum
                    }
                },
                required = new[] { "query", "inventoryMode", "limit" },
                additionalProperties = false
            }, ClientJson.CamelCase)));
        tools.Add(new SourceBackedAgentToolDefinition(
            StartDocumentSearchToolName,
            "Laisse les ancres visibles non resolues et ouvre une nouvelle recherche ciblee. "
            + "Les preuves deja acquises restent disponibles. La query "
            + "doit contenir des termes susceptibles d'apparaitre dans la source et ne doit "
            + "pas decrire seulement le futur livrable ou ses axes.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 120,
                        description =
                            "Noms ou termes susceptibles d'apparaitre dans une preuve source individuelle; ne concatene pas les axes ou la forme du livrable."
                    },
                    limit = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum
                    }
                },
                required = new[] { "query", "limit" },
                additionalProperties = false
            }, ClientJson.CamelCase)));
        return tools;
    }

}
