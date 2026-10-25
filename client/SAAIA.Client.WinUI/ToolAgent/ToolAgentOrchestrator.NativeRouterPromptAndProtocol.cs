using static SAAIA.Client.Core.NativeRouting.NativeRouterResponseReader;
using static SAAIA.Client.Core.NativeRouting.NativeRouterRouteNames;
using System.IO;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

// Native router plan construction using the independent prompt, schema and response-reader components.
namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private string BuildNativeRouterRuntimePolicy()
    {
        var mode = AppSettings.NormalizeActiveMode(_settings?.ActiveMode);
        return "\n\nActive mode: " + mode + "."
            + (mode == "strict"
                ? " In this mode, factual questions and recommendations require corpus evidence even without a named document. Social chat and app operations remain operational."
                : string.Empty);
    }

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
        var scopePolicy = ReadNativeRouterString(arguments, "scopePolicy");
        var scopeUserAnchor = ReadNativeRouterString(arguments, "scopeUserAnchor");
        if (scopePolicy.Length == 0) scopePolicy = "hypothesis"; // Older native replies carry a research hint only.
        if (scopePolicy is not ("hypothesis" or "user_restriction")
            || scopePolicy == "user_restriction" && requestedCategoryScope.Length == 0)
        {
            failureReason = "native_source_scope_policy_invalid";
            return false;
        }
        if (scopePolicy == "user_restriction" && (scopeUserAnchor.Length == 0
            || !sourceRequest.Contains(scopeUserAnchor, StringComparison.OrdinalIgnoreCase)))
        {
            failureReason = "native_source_scope_user_anchor_invalid";
            return false;
        }
        if (!TryResolveNativeRouterCategoryScope(
                sourceRequest,
                requestedCategoryScope,
                categoryHintsIncluded,
                prevalidatedCategoryScope,
                out var categoryPath))
        {
            if (scopePolicy == "user_restriction")
            {
                failureReason = "mandatory_category_scope_unavailable";
                return false;
            }
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
        EmitRagTrace("router.native.document_identity.input",
            ("source_request_chars", sourceRequest.Length),
            ("announced_reference_chars", requestedDocumentName.Length),
            ("announced_reference_has_directory", requestedDocumentName.IndexOfAny(['/', '\\']) >= 0 ? 1 : 0),
            ("uses_focused_document", useFocusedDocument ? 1 : 0));
        var completeDocumentReference = NativeRouting.ExplicitDocumentPath.PreserveUniquePath(sourceRequest, requestedDocumentName);
        if (!string.Equals(completeDocumentReference, requestedDocumentName, StringComparison.Ordinal))
        {
            EmitRagTrace("router.native.document_identity.explicit_path_preserved",
                ("announced_reference_chars", requestedDocumentName.Length),
                ("explicit_reference_chars", completeDocumentReference.Length));
            requestedDocumentName = completeDocumentReference;
        }
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
        var selectionPolicy = ReadNativeRouterString(
            arguments,
            "selectionPolicy");
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
            selectionPolicy = string.Equals(
                compactKind,
                "grid",
                StringComparison.Ordinal)
                ? "structured_layout"
                : atomicEvidenceCount == 1
                    ? "single_item"
                    : "explicit_set";
        }
        var declaresNamedStructuredEntry = string.Equals(
                compactKind,
                "grid",
                StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(
                atomicEvidenceType,
                @"(?:^|[_\s-])(?:entry|item|option|instance)(?:$|[_\s-])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (declaresNamedStructuredEntry
            && string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.Ordinal))
        {
            EmitRagTrace(
                "router.native.mechanical_adjustment",
                ("adjustment", "named_grid_entry_contract_canonicalized"),
                ("atomic_evidence_type", atomicEvidenceType),
                ("provided_mode", atomicEvidenceMode),
                ("derived_mode", "named_item"),
                ("provided_selection", selectionPolicy),
                ("derived_selection", "distinct_structured_layout"));
            atomicEvidenceMode = "named_item";
            selectionPolicy = "distinct_structured_layout";
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
                or "structured_layout" or "distinct_structured_layout")
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
            || (selectionPolicy == "distinct_structured_layout"
                && (compactKind != "grid"
                    || atomicEvidenceMode != "named_item"))
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
                ? requestedDocumentName
                : docPath.Length > 0
                    ? docPath
                    : string.Empty,
            BoundedNamedDocumentExtraction =
                normalizedBoundedNamedValueExtraction,
            CandidateScopePaths = categoryPath.Length > 0
                ? new List<string> { categoryPath }
                : new List<string>(),
            CategoryScopePolicy = scopePolicy,
            CategoryScopeUserAnchor = scopeUserAnchor,
            RequiredCategoryPaths = scopePolicy == "user_restriction"
                ? new List<string> { categoryPath } : new List<string>(),
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
}
