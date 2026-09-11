using System.IO;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

// Native router prompt, tool schema and response protocol.
namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string SubmitSourceBackedRouteToolName =
        "submit_source_backed_route";
    private const string SubmitSourceBackedGridRouteToolName =
        "submit_source_backed_grid_route";
    private const string RequestUserClarificationToolName =
        "request_user_clarification";
    private const string SubmitOperationalRouteToolName =
        "submit_operational_route";
    private string BuildNativeRouterRuntimePolicy()
    {
        var mode = AppSettings.NormalizeActiveMode(_settings?.ActiveMode);
        return "\n\nActive mode: " + mode + "."
            + (mode == "strict"
                ? " In this mode, factual questions and recommendations require corpus evidence even without a named document. Social chat and app operations remain operational."
                : string.Empty);
    }

    private static string BuildNativeRouterClassifierSystemPrompt()
        => """
            Classify SAAIA requests by contract family. Never answer, retrieve or
            fill route arguments. Call exactly one function.

            Use submit_document_overview_route only for one explicitly named
            whole-document overview with 2+ cumulative content facets. Use
            submit_source_backed_grid_route only when sourced values fill repeated
            row-by-column positions in a plan, schedule or table. Use
            submit_source_backed_route for other corpus-backed work. An N-point list
            or whole-document summary has one axis and is never a grid. Alternatives whose
            truth, status, applicability or value evidence must establish are
            answer candidates, not user choices: choose source-backed. If a goal,
            scope, constraint or deliverable preference is missing and
            sources or conversation cannot supply it, still choose the probable
            resume work family; the next stage may clarify. Acceptable evidence forms
            joined by "or" are retrieval targets. A named document with requested
            passages is source-backed. Use submit_operational_route for social chat,
            settings, inventory, export or diagnostics.
            """;

    internal static string BuildNativeRouterClassifierSystemPromptForTests()
        => BuildNativeRouterClassifierSystemPrompt();

    internal static string BuildNativeRouterSpecializedSystemPromptForTests(
        string routeToolName)
        => BuildNativeRouterSpecializedSystemPrompt(
            routeToolName,
            "fr",
            disallowMetaSetLanguage: false);

    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterClassifierToolsForTests()
        => BuildNativeRouterClassifierTools();

    internal static string[] BuildNativeRouterSecondStageToolNamesForTests(
        string selectedRouteToolName,
        bool classifierAccepted)
    {
        var allTools = BuildNativeRouterTools();
        return (classifierAccepted
                ? BuildNativeRouterSecondStageTools(
                    allTools,
                    selectedRouteToolName)
                : allTools)
            .Select(static tool => tool.Name)
            .ToArray();
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterSecondStageTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            string selectedRouteToolName)
    {
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

    internal static bool TryResolveNativeRouterClassifierSelectionForTests(
        string toolName,
        out string selectedToolName)
        => TryResolveNativeRouterClassifierSelection(
            toolName,
            out selectedToolName);

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
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
                emptyParameters)
        };
    }

    private static bool TryResolveNativeRouterClassifierSelection(
        string toolName,
        out string selectedToolName)
    {
        selectedToolName = toolName?.Trim() ?? string.Empty;
        if (selectedToolName is SubmitDocumentOverviewRouteToolName
            or SubmitSourceBackedRouteToolName
            or SubmitSourceBackedGridRouteToolName
            or SubmitOperationalRouteToolName)
            return true;

        selectedToolName = string.Empty;
        return false;
    }

    private static string BuildNativeRouterSpecializedSystemPrompt(
        string routeToolName,
        string detectedLanguage,
        bool disallowMetaSetLanguage)
    {
        var clarificationPolicy = string.Equals(
            routeToolName,
            SubmitOperationalRouteToolName,
            StringComparison.OrdinalIgnoreCase)
            ? """
                Call exactly one available route function. Use the operational
                family when context can resolve the request. Use
                request_user_clarification only for missing user input that
                sources or conversation cannot supply.
                """
            : "Call exactly one available route function. Ask request_missing_user_input for unnamed user-designated documents; facts and recommendations stay evidence-first.";
        var common = $"""
            You are SAAIA's specialized semantic router. Never answer.
            The classifier's proposed work family is {routeToolName}.
            {clarificationPolicy}
            Language: {NormalizeLanguageCode(detectedLanguage)}.
            meta.set_language forbidden: {(disallowMetaSetLanguage ? "true" : "false")}.
            """;
        var family = routeToolName switch
        {
            SubmitDocumentOverviewRouteToolName => """
                The first-stage classifier has already decided that the user
                requests one overview of one explicit document. Preserve the
                explicit document reference exactly. Identify every cumulative
                content facet requested by the user and keep those facets in
                their original order. A facet is a requested semantic question,
                not an answer, source, keyword expansion or invented topic. Do
                not merge distinct facets. requestedPointCount is the facet
                count. sampleCount is requestedPointCount plus one.
                questionFocus must be content.
                """,
            SubmitSourceBackedGridRouteToolName => """
                This request needs sourced values in repeated row-by-column
                positions. Expand finite ranges. count = rows.length *
                columns.length. rowHeader names only the row axis. Axis labels are
                exact plain visible text without JSON punctuation.

                sourceItemMode describes completed cells: content_claim for facts
                about the named row subject; named_item for a new object selected
                into a row-column slot.

                sourceItemType is the answer-bearing item class inside source
                documents, read and cited before cell placement; never a document,
                deliverable, axis-qualified value or guessed answer. For
                content_claim it is a fact about the row subject and must cover all
                columns. Swapping axes must not change it.

                scope is one exact CATEGORY_HINTS category when clear. Choose cards
                to discover unknown items or navigate to inspect a named document
                or structure. Grid intake never formulates a search query.
                Evidence-grounded search can happen after this observation. pool is
                the smallest safe budget. namedReferenceKind: subject for a
                product/model/entity (not a document); document for an explicit
                artifact title, filename, path or formal standard/specification/
                regulation identifier, with or without an extension; none otherwise.
                Only document permits it.
                questionFocus is
                document_family_or_type only when the
                document's own family/type is asked; otherwise content.
                """,
            SubmitSourceBackedRouteToolName => """
                Preserve subjective wording; no proxies. answerUnitType is the answer-bearing source
                unit, never query, target document, deliverable or guessed answer.
                answerUnitMode describes it. For internal information about a named
                object, including a request that first asks to find that object, use
                content_claim; the target document can be singular while the
                answer-bearing content claims are plural. Use named_item with
                single_item only when the output unit is the named object itself.

                selectionPolicy counts answer units: explicit_set is an explicit
                quantity. An unnumbered plural or open collective uses
                a small comparison set of 2 or 3 through open_set. For a broad
                whole-document overview without explicit facets or count, use overview
                with open_set and count 2 or 3; do not invent facets. query is short;
                count counts answer units; pool is a candidate budget.

                Use overview + summary_doc for a named whole-document summary/about;
                otherwise search, cards, navigate or context. scope is one exact
                category only when clear. namedReferenceKind: subject for a
                product/model/entity (not a document); document for an explicit
                artifact title, filename, path or formal standard/specification/
                regulation identifier, with or without an extension; none otherwise.
                Only document permits it.
                useFocusedDocument requires an
                explicit FOCUSED_DOCUMENT_MEMORY reference. questionFocus is
                document_family_or_type only when the document's own type is asked;
                otherwise content.
                """,
            RequestUserClarificationToolName => """
                Ask only when 2+ readings materially change route, scope,
                constraints or deliverable and only the user can resolve them.
                Alternatives whose truth, status, applicability or value evidence
                must establish are answer candidates, not user choices. Clarify only
                for a missing preference that sources or conversation cannot supply.
                If one reading dominates, proceed. understanding preserves the
                request but omits alternatives. Put 2-4 choices only in options. For
                an explicit alternative, userTextAnchor copies its shortest complete
                exact span from the request; an inferred choice uses null.
                executionImpact says what changes.
                """,
            SubmitOperationalRouteToolName => """
                Route social chat, settings, inventory, export and diagnostics.
                Operational mappings: inventory.count=documents.count;
                inventory.list/changed_since=documents.list;
                inventory.find=documents.search; inventory.tree=documents.tree;
                inventory.categories=documents.categories;
                inventory.stats=documents.stats. Other operational tool names
                normally equal their intent; social/meta chat may use none.
                """,
            _ => string.Empty
        };
        return common + Environment.NewLine + Environment.NewLine + family;
    }

    private static string BuildNativeRouterSystemPrompt(
        string detectedLanguage,
        bool disallowMetaSetLanguage)
        => $"""
            You are SAAIA's semantic router. Never answer. Call exactly one
            available route function.

            Evidence decides truth, status, applicability or value: answer candidates, not user choices;
            route source-backed. Clarify only if 2+
            readings need the user. Do not ask about uncertainty source tools can resolve.
            Copy exact explicit userTextAnchor; inferred choices use null.
            Evidence forms joined by "or" are retrieval targets. A named reference
            with passages is source-backed. Never turn nested fragments into
            clarification options.

            Source-back facts, instructions, recipes, recommendations, comparisons,
            selections and plans when documents can help. Social chat, fiction,
            rewriting, translation, settings, inventory, export and diagnostics are
            operational. Recipes and practical recommendations are source-backed.

            Use submit_source_backed_grid_route for repeated sourced row-by-column
            values. Expand finite ranges. count = rows.length * columns.length.
            sourceItemType names the answer-bearing item class inside source documents,
            read and cited before assignment to a cell; never a document or guessed
            value. Swapping axis labels must not change it.
            Axis labels are plain visible text: never include JSON punctuation,
            brackets or quote marks inside a label.
            scope is one exact CATEGORY_HINTS category when clear. First observation:
            navigate a named document/structure or use cards for unknown items.
            Grid intake never formulates a search
            query; evidence-grounded search can happen after this observation.
            pool is the smallest safe budget. rowHeader names only the row axis.

            For non-grid corpus work, answerUnitType is the final answer unit; never
            query, target document, heading or guessed answer. answerUnitMode describes
            that final answer unit, not the object to locate. For internal information
            about a named object, including a request that first asks to find that object,
            use content_claim; the target document can be singular while the answer-bearing
            content claims are plural. Use named_item with single_item only when the output
            unit is the named object itself. selectionPolicy counts
            answer units, not target objects; explicit_set is an explicit quantity. An
            unnumbered plural or open collective uses a small comparison set of 2 or 3
            through open_set. For a broad whole-document overview without explicit
            facets or count, use overview with open_set and count 2 or 3; do not invent
            facets. query is short source terms; count is answer units; pool is a
            candidate budget. Preserve subjective wording without proxies.

            Use submit_document_overview_route for 2+ facets of one named
            document; keep their order.

            First source tool: choose overview + summary_doc for an explicitly named
            whole-document summary/about. Otherwise use search, cards, navigate or
            context. scope is one exact category only when clear.

            namedReferenceKind: subject for product/model/entity (not a document);
            document for an explicit artifact title, filename, path or formal standard/
            specification/regulation identifier, with or without an extension; none
            otherwise. Only document permits document; otherwise null.
            useFocusedDocument is true only when the request refers to FOCUSED_DOCUMENT_MEMORY; memory
            guides retrieval but is never proof. questionFocus is
            document_family_or_type only when a clause asks the type or family of
            the DOCUMENT itself; otherwise it is content. For non-grid source work
            always provide tool, intent, query, answerUnitType, answerUnitMode,
            selectionPolicy, useFocusedDocument, questionFocus, namedReferenceKind,
            document, pool and
            count. Use null for document when no document is named. Omit an empty scope.

            Operational mappings: inventory.count=documents.count;
            inventory.list/changed_since=documents.list;
            inventory.find=documents.search; inventory.tree=documents.tree;
            inventory.categories=documents.categories;
            inventory.stats=documents.stats. Other operational tool names normally
            equal their intent; social/meta chat may use none.

            Language: {NormalizeLanguageCode(detectedLanguage)}.
            meta.set_language forbidden: {(disallowMetaSetLanguage ? "true" : "false")}.
            """;

    internal static string BuildNativeRouterSystemPromptForTests()
        => BuildNativeRouterSystemPrompt("fr", disallowMetaSetLanguage: false);

    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterToolsForTests()
        => BuildNativeRouterTools();

    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterRepairToolsForTests(string routeToolName)
        => BuildNativeRouterRepairTools(
            BuildNativeRouterTools(),
            routeToolName);

    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterAlternativesAfterInvalidClarificationForTests()
        => BuildNativeRouterAlternativesAfterInvalidClarification(
            BuildNativeRouterTools());

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
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

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
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

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
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
                        "tool", "pool", "sourceItemType", "sourceItemMode", "intent", "count",
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

    private static object BoundedNativeRouterString(
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

    private bool TryBuildNativeRouterPlan(
        SourceBackedAgentCompletion completion,
        string sourceRequest,
        string detectedLanguage,
        bool disallowMetaSetLanguage,
        bool categoryHintsIncluded,
        string? prevalidatedCategoryScope,
        out RouterPlan plan,
        out string failureReason)
    {
        plan = new RouterPlan();
        failureReason = string.Empty;
        SourceBackedAgentToolCall call;
        if (completion.ToolCalls.Count == 1
            && completion.ToolCalls[0].Arguments.ValueKind
            == JsonValueKind.Object)
        {
            call = completion.ToolCalls[0];
        }
        else if (TryCoalesceHomogeneousNativeOverviewCalls(
                     completion.ToolCalls,
                     out call,
                     out var coalescedCallCount,
                     out var coalescedPointCount))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "homogeneous_overview_calls_coalesced"),
                ("provided_call_count", coalescedCallCount),
                ("derived_point_count", coalescedPointCount),
                ("preserved_facet_count", ReadNativeRouterStringArray(
                    call.Arguments,
                    "overviewFacets").Count));
        }
        else
        {
            failureReason = "native_router_single_call_required";
            return false;
        }

        if (string.Equals(
                call.Name,
                SubmitDocumentOverviewRouteToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildNativeDocumentOverviewRouterPlan(
                call.Arguments,
                sourceRequest,
                detectedLanguage,
                disallowMetaSetLanguage,
                categoryHintsIncluded,
                out plan,
                out failureReason);
        }
        if (string.Equals(
                call.Name,
                SubmitSourceBackedRouteToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildNativeSourceBackedRouterPlan(
                call.Arguments,
                sourceRequest,
                detectedLanguage,
                disallowMetaSetLanguage,
                categoryHintsIncluded,
                prevalidatedCategoryScope,
                forcedKind: null,
                out plan,
                out failureReason);
        }
        if (string.Equals(
                call.Name,
                SubmitSourceBackedGridRouteToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildNativeSourceBackedRouterPlan(
                call.Arguments,
                sourceRequest,
                detectedLanguage,
                disallowMetaSetLanguage,
                categoryHintsIncluded,
                prevalidatedCategoryScope,
                forcedKind: "grid",
                out plan,
                out failureReason);
        }
        if (string.Equals(call.Name, RequestMissingUserInputToolName, StringComparison.OrdinalIgnoreCase))
            return TryBuildNativeMissingUserInputPlan(call.Arguments, sourceRequest,
                detectedLanguage, out plan, out failureReason);
        if (string.Equals(
                call.Name,
                RequestUserClarificationToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildNativeClarificationRouterPlan(
                call.Arguments,
                sourceRequest,
                detectedLanguage,
                out plan,
                out failureReason);
        }
        if (string.Equals(
                call.Name,
                SubmitOperationalRouteToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildNativeOperationalRouterPlan(
                call.Arguments,
                detectedLanguage,
                disallowMetaSetLanguage,
                out plan,
                out failureReason);
        }

        failureReason = "native_router_unknown_route_tool";
        return false;
    }

    private static bool TryCoalesceHomogeneousNativeOverviewCalls(
        IReadOnlyList<SourceBackedAgentToolCall> calls,
        out SourceBackedAgentToolCall coalescedCall,
        out int coalescedCallCount,
        out int coalescedPointCount)
    {
        coalescedCall = null!;
        coalescedCallCount = calls.Count;
        coalescedPointCount = 0;
        if (calls.Count is < 2 or > MaximumEvidenceOverviewPointCount)
            return false;

        string? document = null;
        string? scope = null;
        string? questionFocus = null;
        var overviewFacets = new List<string>();
        var maximumPool = 0;
        foreach (var call in calls)
        {
            if (!string.Equals(
                    call.Name,
                    SubmitSourceBackedRouteToolName,
                    StringComparison.OrdinalIgnoreCase)
                || call.Arguments.ValueKind != JsonValueKind.Object
                || !string.Equals(
                    ReadNativeRouterString(call.Arguments, "tool"),
                    "overview",
                    StringComparison.Ordinal)
                || !string.Equals(
                    ReadNativeRouterString(call.Arguments, "intent"),
                    "summary_doc",
                    StringComparison.Ordinal)
                || !TryReadNativeRouterBoolean(
                    call.Arguments,
                    "useFocusedDocument",
                    out var useFocusedDocument)
                || useFocusedDocument
                || !TryReadNativeRouterInteger(
                    call.Arguments,
                    "count",
                    out var pointCount)
                || pointCount is < 1 or > MaximumEvidenceOverviewPointCount
                || !TryReadNativeRouterInteger(
                    call.Arguments,
                    "pool",
                    out var pool)
                || pool is < 1 or > 120)
            {
                return false;
            }

            var currentDocument = NormalizeNativeRouterOptionalReference(
                ReadNativeRouterString(call.Arguments, "document"));
            var currentScope = ReadNativeRouterString(
                call.Arguments,
                "scope");
            var currentQuestionFocus = ReadNativeRouterString(
                call.Arguments,
                "questionFocus");
            if (currentQuestionFocus.Length == 0)
                currentQuestionFocus = "content";
            if (currentDocument.Length == 0
                || currentQuestionFocus != "content"
                || document is not null && !string.Equals(
                    document,
                    currentDocument,
                    StringComparison.OrdinalIgnoreCase)
                || scope is not null && !string.Equals(
                    scope,
                    currentScope,
                    StringComparison.OrdinalIgnoreCase)
                || questionFocus is not null && !string.Equals(
                    questionFocus,
                    currentQuestionFocus,
                    StringComparison.Ordinal))
            {
                return false;
            }

            document ??= currentDocument;
            scope ??= currentScope;
            questionFocus ??= currentQuestionFocus;
            var currentFacet = ReadNativeRouterString(
                call.Arguments,
                "query");
            if (currentFacet.Length > 180)
                return false;
            if (currentFacet.Length > 0
                && !overviewFacets.Contains(
                    currentFacet,
                    StringComparer.OrdinalIgnoreCase))
            {
                overviewFacets.Add(currentFacet);
            }
            coalescedPointCount += pointCount;
            maximumPool = Math.Max(maximumPool, pool);
            if (coalescedPointCount > MaximumEvidenceOverviewPointCount)
                return false;
        }

        var arguments = JsonSerializer.SerializeToElement(
            new
            {
                tool = "overview",
                intent = "summary_doc",
                query = string.Empty,
                sourceItemType = "document_overview_claim",
                sourceItemMode = "content_claim",
                selectionPolicy = coalescedPointCount == 1
                    ? "single_item"
                    : "explicit_set",
                scope = string.IsNullOrWhiteSpace(scope) ? null : scope,
                document,
                pool = maximumPool,
                count = coalescedPointCount,
                useFocusedDocument = false,
                questionFocus = "content",
                overviewFacets
            },
            ClientJson.CamelCase);
        coalescedCall = new SourceBackedAgentToolCall(
            "native-route-coalesced-overview",
            SubmitSourceBackedRouteToolName,
            arguments);
        return true;
    }

    private bool TryBuildNativeSourceBackedRouterPlan(
        JsonElement arguments,
        string sourceRequest,
        string detectedLanguage,
        bool disallowMetaSetLanguage,
        bool categoryHintsIncluded,
        string? prevalidatedCategoryScope,
        string? forcedKind,
        out RouterPlan plan,
        out string failureReason)
    {
        plan = new RouterPlan();
        failureReason = string.Empty;
        var compactTool =
            ReadNativeRouterString(arguments, "tool");
        var compactIntent = ReadNativeRouterString(arguments, "intent");
        if (string.Equals(
                compactTool,
                "overview",
                StringComparison.Ordinal)
            && !string.Equals(
                compactIntent,
                "summary_doc",
                StringComparison.Ordinal))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "overview_intent_derived_from_capability"),
                ("provided_intent", compactIntent),
                ("derived_intent", "summary_doc"));
            compactIntent = "summary_doc";
        }
        else if (compactIntent.Length == 0)
            compactIntent = "answer";
        var initialQuery =
            ReadNativeRouterString(arguments, "query");
        var overviewFacets = ReadNativeRouterStringArray(
                arguments,
                "overviewFacets")
            .Where(static facet =>
                facet.Length is > 0 and <= 180)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumEvidenceOverviewPointCount)
            .ToList();
        var requestedCategoryScope =
            ReadNativeRouterString(arguments, "scope");
        if (!TryResolveNativeRouterCategoryScope(
                sourceRequest,
                requestedCategoryScope,
                categoryHintsIncluded,
                prevalidatedCategoryScope,
                out var categoryPath))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "unsupported_category_scope_removed"),
                ("requested_scope", requestedCategoryScope),
                ("category_hints_included", categoryHintsIncluded),
                ("reason", "not_an_exact_published_category_identity"));
            categoryPath = string.Empty;
        }
        else if (requestedCategoryScope.Length > 0)
        {
            EmitRagTrace(
                "router.native.category_scope_resolved",
                ("requested_scope", requestedCategoryScope),
                ("resolved_category_path", categoryPath));
        }
        var hasFocusedDocumentDecision = TryReadNativeRouterBoolean(
            arguments,
            "useFocusedDocument",
            out var useFocusedDocument);
        if (!hasFocusedDocumentDecision)
            useFocusedDocument = false;
        var questionFocus =
            ReadNativeRouterString(arguments, "questionFocus");
        if (questionFocus.Length == 0)
            questionFocus = "content";
        var requestedDocumentName = NormalizeNativeRouterOptionalReference(
            ReadNativeRouterString(arguments, "document"));
        var namedReferenceKind = ReadNativeRouterString(
            arguments,
            "namedReferenceKind");
        if (namedReferenceKind.Length == 0)
        {
            namedReferenceKind = requestedDocumentName.Length > 0
                ? "document"
                : "none";
        }
        else if (namedReferenceKind is not ("none" or "subject" or "document"))
        {
            failureReason = "native_source_route_named_reference_kind_invalid";
            return false;
        }
        if ((string.Equals(
                 namedReferenceKind,
                 "document",
                 StringComparison.Ordinal)
             && requestedDocumentName.Length == 0)
            || (!string.Equals(
                    namedReferenceKind,
                    "document",
                    StringComparison.Ordinal)
                && requestedDocumentName.Length > 0))
        {
            failureReason = "native_source_route_named_reference_inconsistent";
            return false;
        }
        if (requestedDocumentName.Length > 0
            && !sourceRequest.Contains(
                requestedDocumentName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "native_source_route_document_not_explicit";
            return false;
        }
        if (requestedDocumentName.Length > 0
            && (string.Equals(
                    requestedDocumentName,
                    requestedCategoryScope,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    requestedDocumentName,
                    categoryPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "category_identity_removed_from_document"),
                ("value", requestedDocumentName));
            requestedDocumentName = string.Empty;
            namedReferenceKind = "none";
        }
        var (docId, docPath) =
            ResolveFocusedDocumentRouteIdentity(useFocusedDocument);
        var intent = compactIntent switch
        {
            "followup" => "rag.followup",
            "summary_doc" => "rag.summarize_doc",
            "summary_topic" => "rag.summarize_topic",
            "compare" => "rag.compare",
            _ => "rag.answer"
        };
        var hasAtomicEvidenceCount = TryReadNativeRouterInteger(
            arguments,
            "count",
            out var atomicEvidenceCount);
        var explicitOverviewPointCountFromRequest = false;
        if (string.Equals(
                compactTool,
                "overview",
                StringComparison.Ordinal)
            && TryExtractEvidenceOverviewPointCount(sourceRequest)
                is { } explicitOverviewPointCount)
        {
            explicitOverviewPointCountFromRequest = true;
            explicitOverviewPointCount = Math.Clamp(
                explicitOverviewPointCount,
                2,
                MaximumEvidenceOverviewPointCount);
            if (!hasAtomicEvidenceCount
                || atomicEvidenceCount != explicitOverviewPointCount)
            {
                EmitRagTrace(
                    "router.native.mechanical_adjustment",
                    ("adjustment", "overview_count_derived_from_explicit_request"),
                    ("provided_count", hasAtomicEvidenceCount
                        ? atomicEvidenceCount
                        : null),
                    ("derived_count", explicitOverviewPointCount));
            }
            atomicEvidenceCount = explicitOverviewPointCount;
            hasAtomicEvidenceCount = true;
        }
        var compactKind = forcedKind
                          ?? (atomicEvidenceCount == 1
                              ? "one"
                              : "many");
        if (string.Equals(compactKind, "grid", StringComparison.Ordinal)
            && compactTool.Length > 0
            && compactTool is not ("cards" or "navigate"))
        {
            failureReason = "native_source_route_contract_invalid";
            return false;
        }
        if (string.Equals(compactKind, "grid", StringComparison.Ordinal))
            initialQuery = string.Empty;
        var planKind = compactKind switch
        {
            "many" => "multi_item",
            "grid" => "structured_layout",
            _ => "single_item"
        };
        var initialCapability = compactTool switch
        {
            "cards" => "documents_content_cards",
            "navigate" => "documents_navigation",
            "context" => "documents_context",
            "overview" => "documents_overview",
            "search" => "rag_search",
            _ => string.Empty
        };
        var initialLimit = 5;
        var hasInitialLimit = TryReadNativeRouterInteger(
            arguments,
            "pool",
            out var parsedInitialLimit);
        if (hasInitialLimit)
            initialLimit = parsedInitialLimit;
        var deliverable = sourceRequest.Trim();
        var atomicEvidenceType = FirstNonBlank(
            ReadNativeRouterString(arguments, "answerUnitType"),
            ReadNativeRouterString(arguments, "sourceItemType"));
        var atomicEvidenceMode = FirstNonBlank(
            ReadNativeRouterString(arguments, "answerUnitMode"),
            ReadNativeRouterString(arguments, "sourceItemMode"));
        var selectionPolicy = string.Equals(
            compactKind,
            "grid",
            StringComparison.Ordinal)
            ? "structured_layout"
            : ReadNativeRouterString(arguments, "selectionPolicy");
        if (atomicEvidenceType.Length == 0
            && !string.Equals(
                compactKind,
                "grid",
                StringComparison.Ordinal))
        {
            // Backward compatibility for already persisted pre-contract router
            // payloads. New LLM calls cannot omit answerUnitType because the tool
            // schema requires it.
            atomicEvidenceType = initialQuery;
        }
        if (atomicEvidenceMode.Length == 0)
        {
            // Persisted plans created before the semantic-mode contract named
            // source items. New LLM calls must choose an explicit mode.
            atomicEvidenceMode = "named_item";
        }
        if (selectionPolicy.Length == 0)
        {
            // Compatibility with persisted router payloads created before the
            // explicit output-selection policy. New tool calls require it.
            selectionPolicy = atomicEvidenceCount == 1
                ? "single_item"
                : "explicit_set";
        }
        var boundedExtractionFromNamedDocument =
            string.Equals(compactKind, "many", StringComparison.Ordinal)
            && string.Equals(
                selectionPolicy,
                "explicit_set",
                StringComparison.Ordinal)
            && (requestedDocumentName.Length > 0
                || docId.Length > 0
                || docPath.Length > 0);
        var normalizedBoundedNamedValueExtraction =
            boundedExtractionFromNamedDocument
            && !string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.Ordinal);
        if (normalizedBoundedNamedValueExtraction)
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "named_document_bounded_extraction_uses_content_claims"),
                ("provided_mode", atomicEvidenceMode),
                ("derived_mode", "content_claim"),
                ("answer_unit_count", atomicEvidenceCount));
            atomicEvidenceMode = "content_claim";
        }
        if (string.Equals(
                compactKind,
                "one",
                StringComparison.Ordinal)
            && atomicEvidenceCount == 1
            && string.Equals(
                selectionPolicy,
                "explicit_set",
                StringComparison.Ordinal))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "single_answer_unit_selection_policy_canonicalized"),
                ("provided_selection", selectionPolicy),
                ("derived_selection", "single_item"),
                ("answer_unit_count", atomicEvidenceCount));
            selectionPolicy = "single_item";
        }
        if (string.Equals(
                compactTool,
                "overview",
                StringComparison.Ordinal))
        {
            var preservesOpenOverviewSelection =
                !explicitOverviewPointCountFromRequest
                && overviewFacets.Count == 0
                && atomicEvidenceCount is 2 or 3
                && string.Equals(
                    selectionPolicy,
                    "open_set",
                    StringComparison.Ordinal);
            var canonicalSelectionPolicy = preservesOpenOverviewSelection
                ? "open_set"
                : atomicEvidenceCount == 1
                    ? "single_item"
                    : "explicit_set";
            if (!string.Equals(
                    atomicEvidenceMode,
                    "content_claim",
                    StringComparison.Ordinal)
                || !string.Equals(
                    selectionPolicy,
                    canonicalSelectionPolicy,
                    StringComparison.Ordinal))
            {
                EmitRagTrace(
                    "router.native.mechanical_adjustment",
                    ("adjustment", "overview_claim_contract_derived_from_capability"),
                    ("provided_mode", atomicEvidenceMode),
                    ("derived_mode", "content_claim"),
                    ("provided_selection", selectionPolicy),
                    ("derived_selection", canonicalSelectionPolicy));
            }
            atomicEvidenceMode = "content_claim";
            selectionPolicy = canonicalSelectionPolicy;
        }
        var hasValidLayout = TryReadNativeRouterLayout(
            arguments,
            compactKind,
            atomicEvidenceCount,
            out var rowHeader,
            out var rowLabels,
            out var columns);
        if (string.Equals(compactKind, "grid", StringComparison.Ordinal))
        {
            var derivedCellCount = (long)rowLabels.Count * columns.Count;
            if (derivedCellCount is >= 1 and <= 80
                && TryReadNativeRouterLayout(
                    arguments,
                    compactKind,
                    (int)derivedCellCount,
                    out rowHeader,
                    out rowLabels,
                    out columns))
            {
                if (!hasAtomicEvidenceCount
                    || atomicEvidenceCount != derivedCellCount)
                {
                    EmitRagTrace(
                        "router.native.mechanical_adjustment",
                        ("adjustment", "grid_cell_count_derived_from_axes"),
                        ("provided_count", hasAtomicEvidenceCount
                            ? atomicEvidenceCount
                            : null),
                        ("derived_count", derivedCellCount),
                        ("rows", rowLabels.Count),
                        ("columns", columns.Count));
                }

                atomicEvidenceCount = (int)derivedCellCount;
                hasAtomicEvidenceCount = true;
                hasValidLayout = true;
            }
        }
        if (string.Equals(compactKind, "grid", StringComparison.Ordinal)
            && hasValidLayout
            && !AreNativeRouterGridAxesGrounded(
                sourceRequest,
                rowLabels,
                columns))
        {
            failureReason = "native_source_route_axes_not_grounded";
            return false;
        }
        var language = NormalizeLanguageCode(detectedLanguage);
        if (deliverable.Length < 2
            || atomicEvidenceType.Length < 2
            || atomicEvidenceMode is not ("named_item" or "content_claim")
            || selectionPolicy is not (
                "single_item" or "explicit_set" or "open_set"
                or "structured_layout")
            || !hasAtomicEvidenceCount
            || compactIntent is not (
                "answer"
                or "followup"
                or "summary_doc"
                or "summary_topic"
                or "compare")
            || compactKind is not ("one" or "many" or "grid")
            || (compactKind == "grid"
                ? compactTool.Length > 0
                  && compactTool is not ("cards" or "navigate")
                : compactTool is not ("search" or "cards" or "navigate" or "context" or "overview"))
            || questionFocus is not ("content" or "document_family_or_type")
            || atomicEvidenceCount is < 1 or > 80
            || (compactKind == "one" && atomicEvidenceCount != 1)
            || (compactKind == "many" && atomicEvidenceCount < 2)
            || (selectionPolicy == "single_item"
                && (compactKind != "one" || atomicEvidenceCount != 1))
            || (selectionPolicy == "explicit_set"
                && (compactKind != "many" || atomicEvidenceCount < 2))
            || (selectionPolicy == "open_set"
                && (compactKind != "many"
                    || atomicEvidenceCount is < 2 or > 3))
            || (selectionPolicy == "structured_layout"
                && compactKind != "grid")
            || !hasValidLayout
            || (initialCapability.Length > 0
                && (!hasInitialLimit
                    || initialLimit < 1
                    || initialLimit > (compactKind == "grid" ? 40 : 120))))
        {
            failureReason = "native_source_route_contract_invalid";
            return false;
        }

        RouterPlan.ToolCall? initialToolCall = null;
        if (initialCapability.Length == 0)
        {
        }
        else if (string.Equals(
                initialCapability,
                "rag_search",
                StringComparison.Ordinal))
        {
            if (initialQuery.Length == 0)
            {
                failureReason = "native_source_route_query_missing";
                return false;
            }

            initialToolCall = new RouterPlan.ToolCall
            {
                Name = "rag.search",
                Args = JsonSerializer.SerializeToElement(new
                {
                    query = initialQuery,
                    topK = initialLimit,
                    categoryPath =
                        categoryPath.Length > 0 ? categoryPath : null,
                    docId = docId.Length > 0 ? docId : null,
                    docPath = docPath.Length > 0 ? docPath : null
                }, ClientJson.CamelCase)
            };
        }
        else if (string.Equals(
                     initialCapability,
                     "documents_overview",
                     StringComparison.Ordinal))
        {
            var documentReference = docPath.Length > 0
                ? docPath
                : requestedDocumentName;
            if (docId.Length == 0 && documentReference.Length == 0)
            {
                failureReason = "native_source_route_document_missing";
                return false;
            }
            if (!string.Equals(
                    compactIntent,
                    "summary_doc",
                    StringComparison.Ordinal))
            {
                failureReason = "native_source_route_overview_intent_invalid";
                return false;
            }

            initialToolCall = new RouterPlan.ToolCall
            {
                Name = "rag.summarize_live",
                Args = JsonSerializer.SerializeToElement(new
                {
                    docRef = documentReference.Length > 0
                        ? documentReference
                        : docId,
                    level = "medium",
                    strategy = "evidence_overview",
                    responseLanguage = NormalizeLanguageCode(detectedLanguage),
                    userRequest = sourceRequest,
                    overviewFacets,
                    requestedPointCount = atomicEvidenceCount,
                    sampleCount = Math.Clamp(
                        atomicEvidenceCount + 1,
                        3,
                        MaximumEvidenceOverviewPointCount + 1)
                }, ClientJson.CamelCase)
            };
        }
        else if (string.Equals(
                     initialCapability,
                     "documents_context",
                     StringComparison.Ordinal))
        {
            var documentReference = docPath.Length > 0
                ? docPath
                : requestedDocumentName;
            if (docId.Length == 0 && documentReference.Length == 0)
            {
                failureReason = "native_source_route_document_missing";
                return false;
            }

            initialToolCall = new RouterPlan.ToolCall
            {
                Name = "documents.context",
                QueryHint = initialQuery.Length > 0 ? initialQuery : null,
                Args = JsonSerializer.SerializeToElement(new
                {
                    docRef = documentReference.Length > 0
                        ? documentReference
                        : null,
                    docId = docId.Length > 0 ? docId : null,
                    docPath = docPath.Length > 0 ? docPath : null,
                    limit = initialLimit,
                    offset = 0
                }, ClientJson.CamelCase)
            };
        }
        else
        {
            var documentReference = docPath.Length > 0
                ? docPath
                : requestedDocumentName;
            var isExactDocumentNavigation =
                initialCapability == "documents_navigation"
                && documentReference.Length > 0;
            if (string.Equals(
                    initialCapability,
                    "documents_content_cards",
                    StringComparison.Ordinal)
                && string.Equals(
                    compactKind,
                    "grid",
                    StringComparison.Ordinal))
            {
                initialToolCall = new RouterPlan.ToolCall
                {
                    Name = "documents.content_cards",
                    Args = JsonSerializer.SerializeToElement(new
                    {
                        categoryPath =
                            categoryPath.Length > 0 ? categoryPath : null,
                        docRef = documentReference.Length > 0
                            ? documentReference
                            : null,
                        docId = docId.Length > 0 ? docId : null,
                        docPath = docPath.Length > 0 ? docPath : null,
                        q = (string?)null,
                        inventoryMode = "representative",
                        limit = initialLimit,
                        offset = 0
                    }, ClientJson.CamelCase)
                };
            }
            else
            {
                initialToolCall = new RouterPlan.ToolCall
                {
                    Name = initialCapability == "documents_content_cards"
                        ? "documents.content_cards"
                        : "documents.navigation",
                    QueryHint = initialQuery.Length > 0 ? initialQuery : null,
                    Args = JsonSerializer.SerializeToElement(new
                    {
                        categoryPath =
                            categoryPath.Length > 0 ? categoryPath : null,
                        docRef = documentReference.Length > 0
                            ? documentReference
                            : null,
                        docId = docId.Length > 0 ? docId : null,
                        docPath = docPath.Length > 0 ? docPath : null,
                        q = !isExactDocumentNavigation && initialQuery.Length > 0
                            ? initialQuery
                            : null,
                        limit = initialLimit,
                        offset = 0
                    }, ClientJson.CamelCase)
                };
            }
        }

        var mission = new RouterPlan.SourceBackedMissionPlan
        {
            PlanKind = planKind,
            Deliverable = deliverable,
            AtomicEvidenceType = atomicEvidenceType,
            AtomicEvidenceMode = atomicEvidenceMode,
            SelectionPolicy = selectionPolicy,
            AtomicEvidenceTypeStatus = string.Equals(
                compactKind,
                "grid",
                StringComparison.Ordinal)
                ? "provisional_pre_observation"
                : "llm_routed",
            InitialCapability = initialCapability,
            QuestionFocus = questionFocus,
            UsesFocusedDocument = useFocusedDocument,
            NamedReferenceKind = namedReferenceKind,
            RequestedDocumentName = requestedDocumentName.Length > 0
                ? Path.GetFileName(requestedDocumentName)
                : docPath.Length > 0
                    ? Path.GetFileName(docPath)
                    : string.Empty,
            BoundedNamedDocumentExtraction =
                normalizedBoundedNamedValueExtraction,
            CandidateScopePaths = categoryPath.Length > 0
                ? new List<string> { categoryPath }
                : new List<string>(),
            RowHeader = rowHeader,
            RowLabels = rowLabels,
            Columns = columns,
            RowCount = rowLabels.Count > 0 ? rowLabels.Count : 1,
            ColumnCount = columns.Count > 0 ? columns.Count : 1
        };
        mission.AtomicEvidenceCount = atomicEvidenceCount;
        mission.StructuredLayout =
            string.Equals(
                planKind,
                "structured_layout",
                StringComparison.Ordinal);

        plan = SanitizeRouterPlan(
            new RouterPlan
            {
                Intent = intent,
                Language = language,
                ToolCalls = initialToolCall is null
                    ? new List<RouterPlan.ToolCall>()
                    : new List<RouterPlan.ToolCall> { initialToolCall },
                SourceBackedMission = mission,
                Origin = RouterPlanOrigin.Llm
            },
            detectedLanguage,
            disallowMetaSetLanguage);
        plan.Origin = RouterPlanOrigin.Llm;
        return true;
    }

    private bool TryBuildNativeClarificationRouterPlan(
        JsonElement arguments,
        string sourceRequest,
        string detectedLanguage,
        out RouterPlan plan,
        out string failureReason)
    {
        plan = new RouterPlan();
        failureReason = string.Empty;
        var understanding =
            ReadNativeRouterString(arguments, "understanding");
        var optionLabels = new List<string>();
        var optionPresentations = new List<string>();
        var optionsValid = TryReadNativeRouterProperty(
                arguments,
                "options",
                out var optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Array;
        if (optionsValid)
        {
            foreach (var optionElement in optionsElement.EnumerateArray())
            {
                if (optionElement.ValueKind != JsonValueKind.Object)
                {
                    optionsValid = false;
                    break;
                }
                var label = ReadNativeRouterString(optionElement, "label");
                var anchor = ReadNativeRouterString(
                    optionElement,
                    "userTextAnchor");
                if (label.Length is < 1 or > 180)
                {
                    optionsValid = false;
                    break;
                }
                if (anchor.Length > 0)
                {
                    var anchorIndex = sourceRequest.IndexOf(
                        anchor,
                        StringComparison.OrdinalIgnoreCase);
                    if (anchor.Length > 180 || anchorIndex < 0)
                    {
                        optionsValid = false;
                        break;
                    }
                    optionPresentations.Add(sourceRequest.Substring(
                        anchorIndex,
                        anchor.Length));
                }
                else
                {
                    optionPresentations.Add(label);
                }
                optionLabels.Add(label);
            }
        }
        var options = optionPresentations
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == optionPresentations.Count
            ? optionPresentations
            : optionLabels;
        var executionImpact =
            ReadNativeRouterString(arguments, "executionImpact");
        var resumeRoute = ReadNativeRouterString(arguments, "resumeRoute");
        var ambiguityKind =
            ReadNativeRouterString(arguments, "ambiguityKind");

        if (understanding.Length is < 2 or > 300
            || !optionsValid
            || options.Count is < 2 or > 4
            || options.Any(static option => option.Length is < 1 or > 180)
            || options.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != options.Count
            || executionImpact.Length is < 2 or > 240
            || resumeRoute is not (
                "source_backed" or "source_backed_grid" or "operational")
            || ambiguityKind is not (
                "goal" or "scope" or "constraints" or "deliverable"
                or "route" or "other"))
        {
            failureReason = "native_clarification_route_contract_invalid";
            return false;
        }

        var message = ClarificationPresentation.BuildChoiceMessage(
            understanding,
            options,
            detectedLanguage);

        plan = SanitizeRouterPlan(
            new RouterPlan
            {
                Intent = "clarification",
                Language = NormalizeLanguageCode(detectedLanguage),
                NeedClarification = true,
                ClarificationQuestions = new List<string> { message },
                Clarification = new RouterPlan.ClarificationDecisionPlan
                {
                    Message = message,
                    Options = options,
                    ExecutionImpact = executionImpact,
                    ResumeRoute = resumeRoute,
                    AmbiguityKind = ambiguityKind
                },
                Origin = RouterPlanOrigin.Llm
            },
            detectedLanguage,
            disallowMetaSetLanguage: false);
        plan.Origin = RouterPlanOrigin.Llm;
        return true;
    }

    private bool TryResolveNativeRouterCategoryScope(
        string sourceRequest,
        string requestedScope,
        bool categoryHintsIncluded,
        string? prevalidatedCategoryScope,
        out string categoryPath)
    {
        categoryPath = string.Empty;
        var normalizedScope = CollapseWhitespace(requestedScope);
        if (normalizedScope.Length == 0)
            return true;
        var normalizedPrevalidatedScope = CollapseWhitespace(
            prevalidatedCategoryScope ?? string.Empty);
        if (normalizedPrevalidatedScope.Length > 0
            && string.Equals(
                normalizedScope,
                normalizedPrevalidatedScope,
                StringComparison.OrdinalIgnoreCase))
        {
            categoryPath = normalizedPrevalidatedScope;
            return true;
        }
        if (!categoryHintsIncluded)
            return false;

        var matches = SelectSourceBackedLlmCategoryHints(
                sourceRequest,
                CompactSourceBackedRouterCategoryHintLimit)
            .Select(static candidate => candidate.Category)
            .Where(category => new[]
            {
                CollapseWhitespace(category.CategoryRef),
                CollapseWhitespace(category.CategoryPath),
                CollapseWhitespace(category.DisplayName)
            }.Any(identity => identity.Length > 0
                              && string.Equals(
                                  identity,
                                  normalizedScope,
                                  StringComparison.OrdinalIgnoreCase)))
            .Select(category => CollapseWhitespace(
                string.IsNullOrWhiteSpace(category.CategoryPath)
                    ? category.DisplayName
                    : category.CategoryPath))
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        if (matches.Length != 1)
            return false;

        categoryPath = matches[0];
        return true;
    }

    private (string DocId, string DocPath)
        ResolveFocusedDocumentRouteIdentity(bool useFocusedDocument)
    {
        var focused = _mem.LastFocusedDocument;
        if (!useFocusedDocument || focused is null)
            return (string.Empty, string.Empty);

        var resolvedDocId = focused.DocId?.Trim() ?? string.Empty;
        var resolvedDocPath =
            (focused.DocPath ?? focused.DocName)?.Trim() ?? string.Empty;
        ClientLog.Info(
            "ToolAgent focused document route identity restored: "
            + $"docId={resolvedDocId}|docPath={resolvedDocPath}");
        return (resolvedDocId, resolvedDocPath);
    }

    private bool TryBuildNativeOperationalRouterPlan(
        JsonElement arguments,
        string detectedLanguage,
        bool disallowMetaSetLanguage,
        out RouterPlan plan,
        out string failureReason)
    {
        failureReason = string.Empty;
        var intent = ReadNativeRouterString(arguments, "intent");
        var toolName = ReadNativeRouterString(arguments, "toolName");
        var language = NormalizeLanguageCode(detectedLanguage);
        var toolCalls = new List<RouterPlan.ToolCall>();
        if (toolName.Length > 0
            && TryReadNativeRouterProperty(
                arguments,
                "toolArgs",
                out var toolArgs)
            && toolArgs.ValueKind == JsonValueKind.Object)
        {
            toolCalls.Add(new RouterPlan.ToolCall
            {
                Name = toolName,
                Args = toolArgs.Clone()
            });
        }

        plan = SanitizeRouterPlan(
            new RouterPlan
            {
                Intent = intent,
                Language = language,
                ToolCalls = toolCalls,
                Origin = RouterPlanOrigin.Llm
            },
            detectedLanguage,
            disallowMetaSetLanguage);
        plan.Origin = RouterPlanOrigin.Llm;
        return true;
    }

    private static string ReadNativeRouterString(
        JsonElement root,
        string propertyName)
        => TryReadNativeRouterProperty(root, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string NormalizeNativeRouterOptionalReference(string value)
        => value.Trim() switch
        {
            "-" or "none" or "null" or "n/a" => string.Empty,
            var normalized => normalized
        };

    private static bool TryReadNativeRouterLayout(
        JsonElement root,
        string compactKind,
        int atomicEvidenceCount,
        out string rowHeader,
        out List<string> rowLabels,
        out List<string> columns)
    {
        rowHeader = ReadNativeRouterString(root, "rowHeader");
        rowLabels = ReadNativeRouterStringArray(root, "rows");
        columns = ReadNativeRouterStringArray(root, "columns");

        if (!string.Equals(compactKind, "grid", StringComparison.Ordinal))
        {
            return rowHeader.Length == 0
                   && rowLabels.Count == 0
                   && columns.Count == 0;
        }

        var normalizedRowHeader = rowHeader;
        return rowHeader.Length is >= 1 and <= 60
               && IsStructurallyValidNativeRouterLabel(rowHeader)
               && rowLabels.Count is >= 1 and <= 80
               && columns.Count is >= 1 and <= 40
               && rowLabels.All(static label => label.Length is >= 1 and <= 60)
               && columns.All(static label => label.Length is >= 1 and <= 60)
               && rowLabels.All(IsStructurallyValidNativeRouterLabel)
               && columns.All(IsStructurallyValidNativeRouterLabel)
               && rowLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               == rowLabels.Count
               && columns.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               == columns.Count
               && columns.All(label => !string.Equals(
                   label,
                   normalizedRowHeader,
                   StringComparison.OrdinalIgnoreCase))
               && (long)rowLabels.Count * columns.Count
                == atomicEvidenceCount;
    }

    private static bool IsStructurallyValidNativeRouterLabel(string label)
        => label.Any(char.IsLetterOrDigit)
           && !label.Any(char.IsControl)
           && HasBalancedNativeRouterDelimiters(label, '(', ')')
           && HasBalancedNativeRouterDelimiters(label, '[', ']')
           && HasBalancedNativeRouterDelimiters(label, '{', '}');

    private static bool AreNativeRouterGridAxesGrounded(
        string sourceRequest,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columns)
    {
        if (rowLabels.Count == 0 || columns.Count == 0)
            return false;
        var normalizedRequest = NormalizeShortcutToken(sourceRequest);
        bool IsGrounded(string value)
        {
            var normalized = NormalizeShortcutToken(value);
            return normalized.Length > 0
                   && normalizedRequest.Contains(
                       normalized,
                       StringComparison.Ordinal);
        }

        return IsGrounded(rowLabels[0])
               && IsGrounded(rowLabels[^1])
               && columns.All(IsGrounded);
    }

    private static bool HasBalancedNativeRouterDelimiters(
        string value,
        char opening,
        char closing)
    {
        var depth = 0;
        foreach (var character in value)
        {
            if (character == opening)
            {
                depth++;
                continue;
            }
            if (character != closing || --depth >= 0)
                continue;

            return false;
        }

        return depth == 0;
    }

    private static string? FindEmbeddedNativeLayoutCoordinate(
        IEnumerable<string> values,
        string rowHeader,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columns)
    {
        var coordinates = new[] { rowHeader }
            .Concat(rowLabels)
            .Concat(columns)
            .Where(static coordinate => coordinate.Length >= 2)
            .ToArray();
        foreach (var value in values)
        {
            foreach (var coordinate in coordinates)
            {
                var searchStart = 0;
                while (searchStart < value.Length)
                {
                    var index = value.IndexOf(
                        coordinate,
                        searchStart,
                        StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                        break;

                    var beforeIsBoundary = index == 0
                                           || !char.IsLetterOrDigit(value[index - 1]);
                    var end = index + coordinate.Length;
                    var afterIsBoundary = end == value.Length
                                          || !char.IsLetterOrDigit(value[end]);
                    if (beforeIsBoundary && afterIsBoundary)
                        return coordinate;

                    searchStart = index + 1;
                }
            }
        }

        return null;
    }

    private static List<string> ReadNativeRouterStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!TryReadNativeRouterProperty(root, propertyName, out var property))
            return new List<string>();
        if (property.ValueKind != JsonValueKind.Array)
            return new List<string> { string.Empty };

        return property
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim() ?? string.Empty
                : string.Empty)
            .ToList();
    }

    internal static bool TryReadNativeRouterLayoutForTests(
        string json,
        string compactKind,
        int atomicEvidenceCount,
        out string rowHeader,
        out IReadOnlyList<string> rowLabels,
        out IReadOnlyList<string> columns)
    {
        using var document = JsonDocument.Parse(json);
        var valid = TryReadNativeRouterLayout(
            document.RootElement,
            compactKind,
            atomicEvidenceCount,
            out rowHeader,
            out var parsedRows,
            out var parsedColumns);
        rowLabels = parsedRows;
        columns = parsedColumns;
        return valid;
    }

    private static bool TryReadNativeRouterInteger(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return TryReadNativeRouterProperty(
                   root,
                   propertyName,
                   out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool TryReadNativeRouterBoolean(
        JsonElement root,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!TryReadNativeRouterProperty(
                root,
                propertyName,
                out var property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadNativeRouterProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
