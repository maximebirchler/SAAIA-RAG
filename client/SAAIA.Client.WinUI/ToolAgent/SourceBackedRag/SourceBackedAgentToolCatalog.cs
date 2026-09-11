using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedAgentToolCatalog
{
    private static readonly IReadOnlyDictionary<string, string> ExternalToInternal =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rag_search"] = "rag.search",
            ["documents_navigation"] = "documents.navigation",
            ["documents_content_cards"] = "documents.content_cards",
            ["documents_context"] = "documents.context",
            ["documents_context_batch"] = "documents.context_batch"
        };

    private static readonly IReadOnlyDictionary<string, string> InternalToExternal =
        ExternalToInternal.ToDictionary(
            static pair => pair.Value,
            static pair => pair.Key,
            StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ExposedArguments =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["documents.navigation"] = Names(
                "path", "categoryPath", "docRef", "docPath", "q", "kind", "limit", "offset"),
            ["documents.content_cards"] = Names(
                "categoryPath", "categoryRef", "docRef", "docId", "docPath", "q", "inventoryMode", "limit", "offset"),
            ["documents.context"] = Names(
                "docRef", "docId", "docPath", "chunkId", "pageStart", "pageEnd",
                "before", "after", "limit", "offset"),
            ["rag.search"] = Names(
                "query", "queries", "topK", "categoryPath", "docId", "docPath", "pageStart",
                "pageEnd", "maxPerDoc", "maxPerPage", "diversity", "mode")
        };

    public static IReadOnlyList<SourceBackedAgentToolDefinition> Build(
        bool useConstrainedContextDescriptions = false,
        IReadOnlyList<string>? allowedCategoryPaths = null,
        bool includeCategoryPathEnums = true)
        => ToolManifest.Definitions
            .Where(static definition => InternalToExternal.ContainsKey(definition.Name))
            .Select(definition =>
            {
                var allowed = ExposedArguments[definition.Name];
                var schema = definition.ArgsSchema
                    .Where(pair => allowed.Contains(pair.Key))
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal);
                if (string.Equals(definition.Name, "rag.search", StringComparison.OrdinalIgnoreCase))
                {
                    var multiSearch = ToolManifest.Definitions.Single(static item =>
                        string.Equals(item.Name, "rag.multi_search", StringComparison.OrdinalIgnoreCase));
                    foreach (var pair in multiSearch.ArgsSchema.Where(pair => allowed.Contains(pair.Key)))
                        schema.TryAdd(pair.Key, pair.Value);
                }
                var schemaCategoryPaths = includeCategoryPathEnums
                    ? allowedCategoryPaths
                    : null;
                return new SourceBackedAgentToolDefinition(
                InternalToExternal[definition.Name],
                useConstrainedContextDescriptions
                    ? BuildConstrainedContextDescription(definition.Name)
                    : BuildCompactDescription(definition.Name),
                string.Equals(definition.Name, "rag.search", StringComparison.OrdinalIgnoreCase)
                    ? BuildUnifiedSearchJsonSchema(schema, schemaCategoryPaths)
                    : BuildJsonSchema(schema, schemaCategoryPaths));
            })
            .ToArray();

    public static bool TryResolveInternalName(
        string? externalName,
        JsonElement arguments,
        out string internalName)
    {
        if (!string.IsNullOrWhiteSpace(externalName)
            && ExternalToInternal.TryGetValue(externalName.Trim(), out var resolved))
        {
            internalName = string.Equals(resolved, "rag.search", StringComparison.OrdinalIgnoreCase)
                           && HasNonEmptyQueryArray(arguments)
                ? "rag.multi_search"
                : resolved;
            return true;
        }

        internalName = string.Empty;
        return false;
    }

    public static string ToExternalName(string internalName)
        => InternalToExternal.TryGetValue(internalName, out var externalName)
            ? externalName
            : internalName.Replace('.', '_');

    public static bool TryResolveExternalName(
        string? internalName,
        out string externalName)
    {
        var normalized = internalName?.Trim() ?? string.Empty;
        if (string.Equals(
                normalized,
                "rag.multi_search",
                StringComparison.OrdinalIgnoreCase))
        {
            externalName = "rag_search";
            return true;
        }

        return InternalToExternal.TryGetValue(normalized, out externalName!);
    }

    public static JsonElement NormalizeRouterArguments(
        string internalName,
        JsonElement arguments)
    {
        var normalizedName = string.Equals(
            internalName,
            "rag.multi_search",
            StringComparison.OrdinalIgnoreCase)
            ? "rag.search"
            : internalName;
        if (arguments.ValueKind != JsonValueKind.Object
            || !ExposedArguments.TryGetValue(
                normalizedName,
                out var exposedArguments))
        {
            return arguments.Clone();
        }

        var normalized = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (exposedArguments.Contains(property.Name)
                && property.Value.ValueKind is not (
                    JsonValueKind.Null or JsonValueKind.Undefined))
                normalized[property.Name] = property.Value.Clone();
        }

        var hasCategoryPath = normalized.Keys.Any(static key =>
            string.Equals(
                key,
                "categoryPath",
                StringComparison.OrdinalIgnoreCase));
        if (!hasCategoryPath
            && TryGetStringProperty(arguments, "category", out var category)
            && !string.IsNullOrWhiteSpace(category))
        {
            normalized["categoryPath"] =
                JsonSerializer.SerializeToElement(category.Trim());
        }

        return JsonSerializer.SerializeToElement(
            normalized,
            ClientJson.CamelCase);
    }

    private static IReadOnlySet<string> Names(params string[] values)
        => values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string BuildCompactDescription(string internalName)
        => internalName switch
        {
            "documents.navigation" =>
                "Cartographie rapidement documents, titres, sommaires, index et plages de pages. kind=navigation_entry limite aux entrees structurees de sommaire/index; kind=title_anchor explore les autres titres; omets kind pour comparer les deux. Utile pour decouvrir beaucoup de noms inconnus avant une recherche groupee ou une lecture ciblee. Les resultats sont uniquement des pointeurs non citables et exigent un autre outil pour prouver la reponse. Tu decides librement du categoryPath, de la requete, du type d'ancre, de la pagination et de la reutilisation des docPath/pages trouves.",
            "documents.content_cards" =>
                "Parcourt un inventaire pagine de candidats canoniques ancres a un fichier et une page. Une carte peut etre un simple en-tete structurel: elle n'est jamais preclassee comme instance semantiquement adaptee. Juge le titre avec kind, headingPath et evidence. Avec q, filtre lexical sur des termes susceptibles d'apparaitre dans la source. Sans q, choisis inventoryMode=ordered pour suivre le debut et l'ordre des documents, ou representative pour observer des positions reparties dans chaque document quand les noms sources sont encore inconnus. nextOffset poursuit exactement le meme ordre stable. Tu decides librement de l'outil, du corpus et des candidats a retenir.",
            "documents.context" =>
                "Lit le texte indexe autour d'un docPath, de pages ou d'un chunk deja identifie par navigation ou recherche. Copie seulement un chunkId observe; si l'ancre n'en fournit pas, reutilise ses pageStart/pageEnd et n'invente jamais de chunkId.",
            "rag.search" =>
                "Recherche une cible lexicale dans la base et retourne des preuves citables avec document, page et chunk. Les termes doivent pouvoir apparaitre dans la source: evite les meta-requetes comme \"nom de l'element\" ou \"liste d'elements\". La requete decrit le contenu attendu, pas sa seule position dans le livrable: omets les axes et libelles purement visuels, mais conserve une contrainte qui change reellement la nature ou l'usage du contenu recherche. Utilise categoryPath exact si connu, docPath pour raffiner, query pour une cible ou queries pour plusieurs intentions complementaires. Tu ajustes librement topK et la diversite.",
            _ => string.Empty
        };

    private static string BuildConstrainedContextDescription(string internalName)
        => internalName switch
        {
            "documents.navigation" =>
                "Explore titres, sommaires, index et pages pour decouvrir des noms inconnus. kind=navigation_entry cible les entrees de sommaire/index; kind=title_anchor cible les autres titres; omettre kind conserve les deux. Retourne des pointeurs non citables a reutiliser ensuite dans une recherche groupee ou une lecture ciblee.",
            "documents.content_cards" =>
                "Parcourt des candidats canoniques ancres par fichier/page. Ils ne sont pas preclasses semantiquement: juge titre, kind, headingPath et evidence. Avec q, filtre lexical. Sans q, inventoryMode=ordered suit l'ordre des sources; representative echantillonne des positions reparties dans chaque document. nextOffset poursuit la pagination stable.",
            "documents.context" =>
                "Lit le passage indexe autour d'un document, de pages ou d'un chunk deja localise. Copie un chunkId observe; sinon utilise les pages observees, sans synthetiser d'identifiant.",
            "rag.search" =>
                "Recherche des termes susceptibles d'apparaitre dans la source et retourne des preuves citables. Plusieurs EvidenceId d'une meme page partagent le meme groupe_source. query cible une intention; queries regroupe plusieurs intentions. docPath/categoryPath affinent le corpus.",
            _ => string.Empty
        };

    private static JsonElement BuildUnifiedSearchJsonSchema(
        IReadOnlyDictionary<string, string> argsSchema,
        IReadOnlyList<string>? allowedCategoryPaths)
    {
        var properties = argsSchema.ToDictionary(
            pair => pair.Key,
            pair => BuildPropertySchema(
                pair.Key,
                pair.Value,
                allowedCategoryPaths),
            StringComparer.Ordinal);
        if (properties.ContainsKey("query"))
        {
            properties["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] =
                    "Termes compacts attendus dans la source. Exclure les jours, cases et libelles de mise en page sauf s'ils doivent apparaitre litteralement dans le document."
            };
        }
        if (properties.ContainsKey("queries"))
        {
            properties["queries"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] =
                    "Intentions source complementaires. Chaque intention decrit un contenu a trouver, pas sa future case dans la reponse.",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["minItems"] = 1
            };
        }
        var required = argsSchema
            .Where(static pair =>
                pair.Key is not ("query" or "queries")
                && !pair.Value.Contains("null", StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key)
            .ToArray();
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
            ["anyOf"] = new object[]
            {
                new Dictionary<string, object?> { ["required"] = new[] { "query" } },
                new Dictionary<string, object?> { ["required"] = new[] { "queries" } }
            }
        };
        if (required.Length > 0)
            schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema, ClientJson.CamelCase);
    }

    private static bool HasNonEmptyQueryArray(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (string.Equals(property.Name, "queries", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Array
                && property.Value.EnumerateArray().Any(static item =>
                    item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString())))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStringProperty(
        JsonElement root,
        string propertyName,
        out string value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static JsonElement BuildJsonSchema(
        IReadOnlyDictionary<string, string> argsSchema,
        IReadOnlyList<string>? allowedCategoryPaths)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var pair in argsSchema)
        {
            properties[pair.Key] = BuildPropertySchema(
                pair.Key,
                pair.Value,
                allowedCategoryPaths);
            if (!pair.Value.Contains("null", StringComparison.OrdinalIgnoreCase))
                required.Add(pair.Key);
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
            schema["required"] = required;

        return JsonSerializer.SerializeToElement(schema, ClientJson.CamelCase);
    }

    private static object BuildPropertySchema(
        string name,
        string compactType,
        IReadOnlyList<string>? allowedCategoryPaths)
    {
        var alternatives = compactType
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Dictionary<string, object?> schema;
        if (alternatives.Length > 1
            && alternatives.All(static value => value is not ("string" or "int" or "double" or "bool" or "object" or "string[]")))
        {
            schema = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = alternatives
            };
        }
        else
        {
            schema = alternatives.FirstOrDefault() switch
            {
                "int" => new Dictionary<string, object?> { ["type"] = "integer" },
                "double" => new Dictionary<string, object?> { ["type"] = "number" },
                "bool" => new Dictionary<string, object?> { ["type"] = "boolean" },
                "object" => new Dictionary<string, object?> { ["type"] = "object" },
                "string[]" => new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" }
                },
                _ => new Dictionary<string, object?> { ["type"] = "string" }
            };
        }

        if (string.Equals(name, "docId", StringComparison.OrdinalIgnoreCase))
        {
            schema["description"] =
                "Identifiant backend opaque retourne explicitement comme docId. Ne jamais y placer un docPath, un nom de fichier ou un alias D1/D2.";
        }
        else if (string.Equals(name, "docPath", StringComparison.OrdinalIgnoreCase))
        {
            schema["description"] =
                "Chemin documentaire exact retourne comme docPath. Copier le chemin complet, jamais un alias compact D1/D2.";
        }
        else if (string.Equals(name, "chunkId", StringComparison.OrdinalIgnoreCase))
        {
            schema["description"] =
                "Identifiant exact retourne comme chunkId par une observation. Ne jamais l'inventer. Si aucun chunkId n'est observe, omettre ce champ et reutiliser pageStart/pageEnd observes.";
        }
        else if (string.Equals(
                     name,
                     "categoryPath",
                     StringComparison.OrdinalIgnoreCase))
        {
            var exactPaths = (allowedCategoryPaths ?? Array.Empty<string>())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
            if (exactPaths.Length > 0)
                schema["enum"] = exactPaths;
            schema["description"] = exactPaths.Length > 0
                ? "Chemin exact du catalogue. Choisir une valeur enum ou omettre ce filtre."
                : "Chemin exact deja retourne par le catalogue ou un outil; sinon omettre ce filtre.";
        }
        else if (string.Equals(name, "offset", StringComparison.OrdinalIgnoreCase))
        {
            schema["description"] =
                "Position dans la pagination du meme outil avec exactement la meme requete et les memes filtres. Utiliser le nextOffset retourne pour cette route; une nouvelle requete commence normalement a 0.";
        }

        return schema;
    }
}
