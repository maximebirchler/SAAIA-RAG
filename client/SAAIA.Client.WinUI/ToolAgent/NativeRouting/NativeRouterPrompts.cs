using SAAIA.Client.WinUI.Localization;
using static SAAIA.Client.Core.NativeRouting.NativeRouterRouteNames;
using static SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting.NativeRouterToolCatalog;

namespace SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting;

/// <summary>Pure native-router prompt construction from explicit language and policy inputs.</summary>
internal static class NativeRouterPrompts
{

    internal static string BuildNativeRouterClassifierSystemPrompt()
        => """
            Classify SAAIA requests. Never answer, retrieve or fill arguments.
            Call exactly one function according to its description.
            A list or summary has one axis and is never a grid. A named document
            with requested passages is source-backed. Alternatives whose truth,
            status, applicability or value evidence must establish are answer candidates, not user choices.
            Acceptable evidence forms joined by "or" are retrieval targets.
            request_missing_user_input is only for actual instance facts entirely
            absent from conversation. Sources cannot supply it. With actual
            context supplied, choose corpus work; a separate context check preserves it.
            Essential unbound user references use request_unbound_user_reference_route.
            Corpus/PDF collections, optional preferences and document availability
            stay in the work family. General rules and hypothetical cases are source-backed.
            """;


    internal static string BuildNativeRouterSpecializedSystemPrompt(
        string routeToolName,
        string detectedLanguage,
        bool disallowMetaSetLanguage)
    {
        routeToolName = ResolveClassifierPlanningToolName(routeToolName);
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
            : routeToolName == SubmitSourceBackedRouteToolName
            ? "Call exactly one available route function. Begin evidence-first corpus work; selection of PDF files is not a prerequisite."
            : """
                Call exactly one available route function. Ask
                request_missing_user_input for unnamed user-designated documents
                or missing user state; rules cannot supply it. Facts stay evidence-first.
                """;
        var common = $"""
            Route this request. Never answer.
            The classifier's proposed work family is {routeToolName}.
            {clarificationPolicy}
            Language: {LocalizedStrings.NormalizeLanguage(detectedLanguage)}.
            meta.set_language forbidden: {(disallowMetaSetLanguage ? "true" : "false")}.
            """;
        var family = routeToolName switch
        {
            RequestMissingUserInputToolName => """
                Ask one open question for essential missing user input. Copy its
                exact user-text anchor and explain why sources cannot supply the
                information. Do not invent choices, instance facts or answers.
                """,
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

                Distinct-item grid: sourceItemMode=named_item;
                selectionPolicy=distinct_structured_layout unless repeats allowed.

                sourceItemType is the answer-bearing item class inside source
                documents, read and cited before cell placement; never a document,
                deliverable, axis-qualified value or guessed answer. For
                content_claim it is a fact about the row subject and must cover all
                columns. Swapping axes must not change it.

                scope is one exact CATEGORY_HINTS category when clear. Choose cards
                to discover unknown items or navigate to inspect a named document
                or structure. Grid intake never formulates a search query.
                Search follows this observation. pool is
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
                Optional preferences do not block a request permitting documented candidates.
                Preserve subjective wording; no proxies. answerUnitType is the answer-bearing source
                unit, never query, target document, deliverable or guessed answer.
                answerUnitMode describes it. For internal information about a named
                object, including a request that first asks to find that object, use
                content_claim; the target document can be singular while the
                answer-bearing content claims are plural. Use named_item with
                single_item only when the output unit is the named object itself.

                selectionPolicy counts answer units: explicit_set is an explicit
                quantity. An unnumbered plural or open collective uses
                a small comparison set of 2 or 3 through open_set. Without overview
                facets or count, use overview with open_set and count 2 or 3;
                do not invent facets. pool is a candidate budget.

                Use overview + summary_doc for a named whole-document summary/about;
                otherwise search, cards, navigate or context. scope is one exact
                category only when clear. namedReferenceKind: subject for a
                product/model/entity (not a document); document for an explicit title, filename,
                path or formal standard/specification/regulation identifier,
                with or without an extension; none otherwise.
                Only document permits it.
                useFocusedDocument requires an
                explicit FOCUSED_DOCUMENT_MEMORY reference. questionFocus is
                document_family_or_type only when the document's own type is asked;
                otherwise content.
                """,
            RequestUserClarificationToolName => """
                Ask only when 2+ readings materially change route, scope,
                constraints or deliverable and only the user can resolve them,
                or essential facts about the user's own setup prevent an
                actionable decision and sources or conversation cannot supply them.
                Alternatives whose truth, status, applicability or value evidence
                must establish are answer candidates, not user choices. Clarify only
                for a missing preference or such user-specific application facts.
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


    internal static string BuildNativeRouterSystemPrompt(
        string detectedLanguage,
        bool disallowMetaSetLanguage)
        => $"""
            You are SAAIA's semantic router. Never answer. Call exactly one
            available route function.

            Evidence decides truth, status, applicability or value: answer candidates, not user choices;
            route source-backed. Clarify only for user ambiguity or essential
            missing instance facts (setup, state, project phase); general rules
            do not supply them. Do not ask about uncertainty source tools can resolve.
            Copy exact explicit userTextAnchor; inferred choices use null.
            Evidence forms joined by "or" are retrieval targets. A named reference
            with passages is source-backed. Never turn nested fragments into
            clarification options.

            Source-back facts, instructions, recipes, recommendations, comparisons,
            selections and plans when documents can help. Social chat, fiction,
            rewriting, translation, settings, inventory, export and diagnostics are
            operational.

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
            document, pool and count. Null document when unnamed; omit empty scope.

            Operational mappings: inventory.count=documents.count;
            inventory.list/changed_since=documents.list;
            inventory.find=documents.search; inventory.tree=documents.tree;
            inventory.categories=documents.categories;
            inventory.stats=documents.stats. Other operational tool names normally
            equal their intent; social/meta chat may use none.

            Language: {LocalizedStrings.NormalizeLanguage(detectedLanguage)}.
            meta.set_language forbidden: {(disallowMetaSetLanguage ? "true" : "false")}.
            """;

}
