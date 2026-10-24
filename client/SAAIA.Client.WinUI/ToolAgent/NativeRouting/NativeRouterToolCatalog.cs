using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using static SAAIA.Client.Core.NativeRouting.NativeRouterRouteNames;

namespace SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting;

/// <summary>Native-router tool schemas, advertised routes and stage selection. No application state is read.</summary>
internal static class NativeRouterToolCatalog
{


    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterSecondStageTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            string selectedRouteToolName)
    {
        selectedRouteToolName = ResolveClassifierPlanningToolName(selectedRouteToolName);
        var selected = tools.FirstOrDefault(tool => string.Equals(
            tool.Name,
            selectedRouteToolName,
            StringComparison.OrdinalIgnoreCase));
        var clarification = tools.FirstOrDefault(tool => string.Equals(
            tool.Name,
            RequestUserClarificationToolName,
            StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            return Array.Empty<SourceBackedAgentToolDefinition>();

        if (selectedRouteToolName == RequestMissingUserInputToolName)
            return new[] { selected };
        if (selectedRouteToolName == SubmitSourceBackedRouteToolName)
            return new[] { selected };

        if (!string.Equals(
                selectedRouteToolName,
                SubmitOperationalRouteToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            var missingInput = tools.FirstOrDefault(tool => string.Equals(
                tool.Name, RequestMissingUserInputToolName, StringComparison.OrdinalIgnoreCase));
            return missingInput is null ? new[] { selected } : new[] { selected, missingInput };
        }

        return clarification is null
            ? Array.Empty<SourceBackedAgentToolDefinition>()
            : new[] { selected, clarification };
    }


    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterClassifierTools()
    {
        var emptyParameters = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>(),
            additionalProperties = false
        }, ClientJson.CamelCase);
        return new[]
        {
            new SourceBackedAgentToolDefinition(
                SubmitDocumentOverviewRouteToolName,
                "One explicitly named whole-document overview with two or more cumulative content facets.",
                emptyParameters),
            new SourceBackedAgentToolDefinition(
                SubmitSourceBackedRouteToolName,
                "Corpus-backed answer or list, including answer candidates resolved by evidence, without repeated row-by-column placement.",
                emptyParameters),
            new SourceBackedAgentToolDefinition(
                SubmitSourceBackedGridRouteToolName,
                "Corpus-backed plan, schedule or table with sourced values in repeated row-by-column positions.",
                emptyParameters),
            new SourceBackedAgentToolDefinition(
                SubmitOperationalRouteToolName,
                "Social, settings, inventory, export, diagnostics or other non-source-backed operation.",
                emptyParameters),
            new SourceBackedAgentToolDefinition(
                RequestMissingUserInputToolName,
                "Missing actual configuration, state or project phase of the user's own setup essential for an actionable decision. Not optional preferences or document availability.",
                emptyParameters),
            new SourceBackedAgentToolDefinition(
                RequestUnboundUserReferenceRouteToolName,
                "An essential user-designated reference or mandatory choice is unbound and conversation cannot identify it. Corpus/PDF collection searches and optional preferences are ordinary work.",
                emptyParameters)
        };
    }


    internal static bool TryResolveNativeRouterClassifierSelection(
        string toolName,
        out string selectedToolName)
    {
        selectedToolName = toolName?.Trim() ?? string.Empty;
        if (selectedToolName is SubmitDocumentOverviewRouteToolName
            or SubmitSourceBackedRouteToolName
            or SubmitSourceBackedGridRouteToolName
            or SubmitOperationalRouteToolName
            or RequestMissingUserInputToolName
            or RequestUnboundUserReferenceRouteToolName)
            return true;

        selectedToolName = string.Empty;
        return false;
    }


    internal static string ResolveClassifierPlanningToolName(string selectedName)
        => selectedName switch
        {
            SubmitApplicationDecisionRouteToolName => SubmitSourceBackedRouteToolName,
            RequestUnboundUserReferenceRouteToolName => RequestMissingUserInputToolName,
            _ => selectedName
        };


    internal static bool ValidateAdvertisedNativeRoute(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<SourceBackedAgentToolDefinition> advertisedTools,
        ref string failureReason)
    {
        if (completion.ToolCalls.All(call => advertisedTools.Any(tool =>
                string.Equals(tool.Name, call.Name, StringComparison.Ordinal))))
            return true;
        failureReason = UnadvertisedNativeRouteFailure;
        return false;
    }


    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterRepairTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            string routeToolName)
    {
        var matchingTool = tools.FirstOrDefault(tool => string.Equals(
            tool.Name,
            routeToolName,
            StringComparison.OrdinalIgnoreCase));
        return matchingTool is null
            ? tools
            : new[] { matchingTool };
    }


    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterAlternativesAfterInvalidClarification(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => tools
            .Where(tool => !string.Equals(
                               tool.Name,
                               RequestUserClarificationToolName,
                               StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(
                               tool.Name,
                               RequestMissingUserInputToolName,
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();


    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterTools()
        => new[]
        {
            BuildNativeDocumentOverviewRouteTool(),
            new SourceBackedAgentToolDefinition(
                SubmitSourceBackedRouteToolName,
                "Corpus-backed answer or list without repeated row-by-column placement. Never use for a schedule, plan or table whose sourced values fill visible rows and columns.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        tool = new
                        {
                            type = "string",
                            @enum = new[] { "search", "cards", "navigate", "context", "overview" }
                        },
                        intent = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "answer", "followup", "summary_doc",
                                "summary_topic", "compare"
                            }
                        },
                        query = BoundedNativeRouterString(
                            0,
                            180,
                            "Compact answer-bearing source query; empty only when unused."),
                        answerUnitType = BoundedNativeRouterString(
                            2,
                            140,
                            "Type of final answer unit expected in each output position; never the query, target document, deliverable, heading or guessed answer."),
                        answerUnitMode = new
                        {
                            type = "string",
                            @enum = new[] { "named_item", "content_claim" },
                            description =
                                "Mode of the final answer unit, not the target document: named_item only when each output unit is the named object itself; content_claim for facts, properties, passages, instructions or other internal information about a target object, including when the request first asks to find it."
                        },
                        selectionPolicy = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "single_item", "explicit_set", "open_set"
                            }
                        },
                        scope = BoundedNativeRouterString(
                            2,
                            120,
                            "Exact category. Omit when open."),
                        useFocusedDocument = new
                        {
                            type = "boolean",
                            description =
                                "True only when the request refers to FOCUSED_DOCUMENT_MEMORY."
                        },
                        questionFocus = new
                        {
                            type = "string",
                            @enum = new[] { "content", "document_family_or_type" }
                        },
                        namedReferenceKind = new
                        {
                            type = "string",
                            @enum = new[] { "none", "subject", "document" },
                            description =
                                "Classify the named reference: subject for a product, model or entity being researched; document for an explicitly named document artifact, including a formally identified standard, specification or regulation without an extension; none when neither. A subject is not a document."
                        },
                        document = new
                        {
                            type = new[] { "string", "null" },
                            minLength = 2,
                            maxLength = 180,
                            description =
                                "Exact document title or filename span copied from the user; a title without an extension is valid and must not be completed."
                        },
                        pool = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 120
                        },
                        count = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 80
                        }
                    },
                    required = new[]
                    {
                        "tool", "intent", "query", "answerUnitType",
                        "answerUnitMode", "selectionPolicy",
                        "useFocusedDocument", "questionFocus",
                        "namedReferenceKind", "document",
                        "pool", "count"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase)),
            new SourceBackedAgentToolDefinition(
                SubmitSourceBackedGridRouteToolName,
                "Corpus-backed plan, schedule or table where concrete sourced values fill every visible row-by-column cell. The initial action is discovery or navigation only; search waits for observed evidence.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        tool = new
                        {
                            type = "string",
                            @enum = new[] { "cards", "navigate" }
                        },
                        pool = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 40
                        },
                        sourceItemType = BoundedNativeRouterString(
                            2,
                            140,
                            "Provisional source-object class to verify after observation; never executable as a query, document, deliverable, cell role, axis-qualified value, example or guessed answer."),
                        sourceItemMode = new
                        {
                            type = "string",
                            @enum = new[] { "named_item", "content_claim" },
                            description =
                                "Mode of a completed cell: content_claim for facts or properties about a subject already named by the row; named_item for a new object selected to fill a row-by-column slot."
                        },
                        selectionPolicy = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "structured_layout",
                                "distinct_structured_layout"
                            },
                            description =
                                "Whether named items selected into cells may repeat. Use distinct_structured_layout when every cell must contain a different item; otherwise structured_layout."
                        },
                        intent = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "answer", "followup", "summary_doc",
                                "summary_topic", "compare"
                            }
                        },
                        useFocusedDocument = new { type = "boolean" },
                        questionFocus = new
                        {
                            type = "string",
                            @enum = new[] { "content", "document_family_or_type" }
                        },
                        namedReferenceKind = new
                        {
                            type = "string",
                            @enum = new[] { "none", "subject", "document" },
                            description =
                                "Classify the named reference: subject for a product, model or entity being researched; document for an explicitly named document artifact, including a formally identified standard, specification or regulation without an extension; none when neither. A subject is not a document."
                        },
                        document = new
                        {
                            type = new[] { "string", "null" },
                            minLength = 2,
                            maxLength = 180,
                            description =
                                "Exact document title or filename span copied from the user; a title without an extension is valid and must not be completed."
                        },
                        scope = BoundedNativeRouterString(
                            2,
                            120,
                            "One exact CATEGORY_HINTS category when semantically clear; omit when genuinely open."),
                        count = new
                        {
                            type = "integer",
                            description =
                                "Exact value-bearing cell count: rows.length multiplied by columns.length.",
                            minimum = 1,
                            maximum = 80
                        },
                        rowHeader = BoundedNativeRouterString(
                            1,
                            60,
                            "Exact visible row-axis header; never repeat it in columns."),
                        rows = new
                        {
                            type = "array",
                            items = BoundedNativeRouterString(1, 60),
                            minItems = 1,
                            maxItems = 80
                        },
                        columns = new
                        {
                            type = "array",
                            items = BoundedNativeRouterString(1, 60),
                            minItems = 1,
                            maxItems = 40
                        }
                    },
                    required = new[]
                    {
                        "tool", "pool", "sourceItemType", "sourceItemMode", "selectionPolicy", "intent", "count",
                        "rowHeader", "rows", "columns", "questionFocus",
                        "useFocusedDocument", "namedReferenceKind", "document"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase)),
            BuildNativeMissingUserInputTool(),
            new SourceBackedAgentToolDefinition(
                RequestUserClarificationToolName,
                "Pause only when user input must resolve materially distinct readings. Never use when alternatives are candidate answers whose value sources should establish, for acceptable evidence forms joined by or, named-document passage search, or nested fragments of one request.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        understanding = BoundedNativeRouterString(
                            2,
                            300,
                            "One user-facing sentence preserving the request. Do not mention alternatives, options or a question."),
                        options = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    label = BoundedNativeRouterString(
                                        1,
                                        180,
                                        "Concise concrete option in the user's language."),
                                    userTextAnchor = new
                                    {
                                        type = new[] { "string", "null" },
                                        minLength = 2,
                                        maxLength = 180,
                                        description =
                                            "Exact complete span copied from the request when this alternative was explicit; null only when inferred."
                                    }
                                },
                                required = new[] { "label", "userTextAnchor" },
                                additionalProperties = false
                            },
                            minItems = 2,
                            maxItems = 4
                        },
                        executionImpact = BoundedNativeRouterString(
                            2,
                            240,
                            "Briefly state which route, scope, constraints or deliverable the answer will determine."),
                        resumeRoute = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "source_backed", "source_backed_grid", "operational"
                            }
                        },
                        ambiguityKind = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "goal", "scope", "constraints", "deliverable",
                                "route", "other"
                            }
                        }
                    },
                    required = new[]
                    {
                        "understanding", "options", "executionImpact",
                        "resumeRoute", "ambiguityKind"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase)),
            new SourceBackedAgentToolDefinition(
                SubmitOperationalRouteToolName,
                "Chat, text transformation, settings, inventory, export or diagnostics.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        intent = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "chat.general", "meta.set_language",
                                "meta.set_style", "meta.set_mode",
                                "meta.rewrite_last", "meta.help",
                                "meta.translate_last_answer", "inventory.count",
                                "inventory.list", "inventory.find",
                                "inventory.changed_since", "inventory.tree",
                                "inventory.categories", "inventory.stats",
                                "summary.exists", "export.create",
                                "diagnostic.performance"
                            }
                        },
                        toolName = BoundedNativeRouterString(0, 80),
                        toolArgs = new
                        {
                            type = "object",
                            additionalProperties = true
                        }
                    },
                    required = new[]
                    {
                        "intent", "toolName", "toolArgs"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase))
        };


    internal static object BoundedNativeRouterString(
        int minimumLength,
        int maximumLength,
        string? description = null)
        => string.IsNullOrWhiteSpace(description)
            ? new
            {
                type = "string",
                minLength = minimumLength,
                maxLength = maximumLength
            }
            : new
            {
                type = "string",
                description,
                minLength = minimumLength,
                maxLength = maximumLength
            };


    internal static SourceBackedAgentToolDefinition
        BuildNativeDocumentOverviewRouteTool()
        => new(
            SubmitDocumentOverviewRouteToolName,
            "One explicitly named document overview with two to five cumulative content facets preserved in user order.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    document = new
                    {
                        type = "string",
                        minLength = 1,
                        maxLength = 260
                    },
                    facets = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 2,
                        maxItems = 5
                    },
                    requestedPointCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 10
                    },
                    sampleCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 11
                    },
                    questionFocus = new
                    {
                        type = "string",
                        @enum = new[] { "content" }
                    }
                },
                required = new[]
                {
                    "document", "facets", "requestedPointCount",
                    "sampleCount", "questionFocus"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));


    internal static SourceBackedAgentToolDefinition BuildNativeMissingUserInputTool()
        => new(RequestMissingUserInputToolName,
            "Ask one open question for essential user input absent from request and conversation. A decision about the user's actual configuration, state or project phase needs instance facts that general rules cannot establish. Optional preferences do not block a request permitting documented candidates or defaults. Document contents and indexed availability require investigation, not user input. Do not ask users to choose answers evidence can establish. Do not invent options.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    question = BoundedNativeRouterString(2, 300,
                        "One direct question in the user language. Ask for the missing information without suggesting invented values."),
                    userTextAnchor = BoundedNativeRouterString(2, 180,
                        "Exact span copied from the current request containing the unbound reference or the need for the missing input."),
                    missingInformation = BoundedNativeRouterString(2, 240,
                        "What essential user-provided information is missing and why the corpus cannot supply it."),
                    resumeRoute = new
                    {
                        type = "string",
                        @enum = new[] { "source_backed", "source_backed_grid", "operational" }
                    }
                },
                required = new[] { "question", "userTextAnchor", "missingInformation", "resumeRoute" },
                additionalProperties = false
            }, ClientJson.CamelCase));

}
