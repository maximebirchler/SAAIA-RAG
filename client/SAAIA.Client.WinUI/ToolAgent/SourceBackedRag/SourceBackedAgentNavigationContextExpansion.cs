using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool IsResearchTransitionToolName(string? toolName)
        => toolName is ContinuePaginationToolName
            or ResolveNavigationAnchorsToolName
            or ExpandDocumentContextToolName
            or RefineFocusedDocumentSearchToolName
            or RefineNavigationToolName
            or StartContentCardResearchToolName
            or StartDocumentSearchToolName;

    private static bool TryExpandResearchTransitionCall(
        SourceBackedAgentToolCall call,
        EvidenceBundle bundle,
        ISet<string> resolvedNavigationEvidenceIds,
        IReadOnlyList<RetrievalRequest> executedRequests,
        int maximumWorkingEvidenceItems,
        out SourceBackedAgentToolCall expandedCall,
        out string decision,
        out int ignoredEvidenceIdCount,
        out string error)
    {
        expandedCall = call;
        decision = string.Empty;
        ignoredEvidenceIdCount = 0;
        error = string.Empty;
        switch (call.Name)
        {
            case ContinuePaginationToolName:
                decision = "continue_existing_pagination";
                if (!TryBuildPaginationContinuationCall(
                        NormalizeSemanticPlanLine(GetString(
                            call.Arguments,
                            "paginationRouteId")),
                        executedRequests,
                        Math.Min(5, Math.Max(1, maximumWorkingEvidenceItems)),
                        call.Id,
                        out expandedCall,
                        out error))
                {
                    return false;
                }
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                return true;
            case ResolveNavigationAnchorsToolName:
                decision = "resolve_selected_anchors";
                if (!TryGetPropertyIgnoreCase(
                        call.Arguments,
                        "evidenceIds",
                        out var evidenceIdsElement)
                    || evidenceIdsElement.ValueKind != JsonValueKind.Array)
                {
                    error = "research_transition_evidence_ids_invalid";
                    return false;
                }
                var rawEvidenceIds = evidenceIdsElement.EnumerateArray().ToArray();
                var evidenceIds = rawEvidenceIds
                    .Where(static value => value.ValueKind == JsonValueKind.String)
                    .Select(static value => value.GetString()?.Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray();
                if (evidenceIds.Length == 0
                    || evidenceIds.Length != rawEvidenceIds.Length
                    || evidenceIds
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != evidenceIds.Length)
                {
                    error = "research_transition_evidence_ids_invalid";
                    return false;
                }
                var allIdsResolvable = evidenceIds.All(id =>
                    bundle.ById.TryGetValue(id, out var item)
                    && IsMechanicallyResolvableNavigationLocator(item));
                if (!allIdsResolvable)
                {
                    error = "research_transition_evidence_not_resolvable";
                    return false;
                }
                expandedCall = call with
                {
                    Name = NavigationContextBatchToolName,
                    Arguments = JsonSerializer.SerializeToElement(
                        new { evidenceIds },
                        ClientJson.CamelCase)
                };
                return true;
            case ExpandDocumentContextToolName:
                decision = "expand_document_context";
                var requestedContextFocusEvidenceId =
                    NormalizeSemanticPlanLine(GetString(
                        call.Arguments,
                        "documentFocusEvidenceId"));
                if (!TryResolveDocumentFocus(
                        bundle,
                        maximumWorkingEvidenceItems,
                        requestedContextFocusEvidenceId,
                        out var contextFocusedEvidence,
                        out error)
                    || contextFocusedEvidence is null)
                {
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = "research_transition_document_focus_invalid";
                    }
                    return false;
                }
                if (!TryBuildLeadContextAction(
                        bundle,
                        new[] { contextFocusedEvidence.EvidenceId },
                        out var contextCall))
                {
                    error = "research_transition_document_context_unresolvable";
                    return false;
                }
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                expandedCall = contextCall with { Id = call.Id };
                return true;
            case RefineFocusedDocumentSearchToolName:
                decision = "search_focused_document";
                if (!TryBuildFocusedDocumentSearchCall(
                        call,
                        bundle,
                        maximumWorkingEvidenceItems,
                        out expandedCall,
                        out error))
                    return false;
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                return true;
            case RefineNavigationToolName:
                decision = "refine_navigation";
                if (!TryReadResearchTransitionLimit(call.Arguments, out var navigationLimit, out error))
                    return false;
                var requestedDocumentFocusEvidenceId =
                    NormalizeSemanticPlanLine(GetString(
                        call.Arguments,
                        "documentFocusEvidenceId"));
                if (!TryResolveDocumentFocus(
                        bundle,
                        maximumWorkingEvidenceItems,
                        requestedDocumentFocusEvidenceId,
                        out var focusedEvidence,
                        out error))
                {
                    return false;
                }
                var navigationQuery = NormalizeSemanticPlanLine(GetString(
                    call.Arguments,
                    "query"));
                var navigationKind = NormalizeSemanticPlanLine(GetString(
                    call.Arguments,
                    "navigationKind"));
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                expandedCall = call with
                {
                    Name = "documents_navigation",
                    Arguments = JsonSerializer.SerializeToElement(new
                    {
                        docId = focusedEvidence?.DocId,
                        docPath = focusedEvidence?.DocPath,
                        docRef = focusedEvidence?.DocName,
                        q = navigationQuery.Length > 0 ? navigationQuery : null,
                        kind = navigationKind == "all"
                            ? null
                            : navigationKind,
                        limit = navigationLimit,
                        offset = 0
                    }, ClientJson.CamelCase)
                };
                return true;
            case StartContentCardResearchToolName:
                decision = "switch_to_content_cards";
                if (!TryReadResearchTransitionLimit(call.Arguments, out var cardLimit, out error))
                    return false;
                var cardQuery = NormalizeSemanticPlanLine(GetString(
                    call.Arguments,
                    "query"));
                var inventoryMode = NormalizeSemanticPlanLine(GetString(
                    call.Arguments,
                    "inventoryMode"));
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                expandedCall = call with
                {
                    Name = "documents_content_cards",
                    Arguments = JsonSerializer.SerializeToElement(new
                    {
                        q = cardQuery.Length > 0 ? cardQuery : null,
                        inventoryMode = cardQuery.Length == 0
                            && inventoryMode is "ordered" or "representative"
                                ? inventoryMode
                                : null,
                        limit = cardLimit,
                        offset = 0
                    }, ClientJson.CamelCase)
                };
                return true;
            case StartDocumentSearchToolName:
                decision = "switch_to_search";
                if (!TryReadResearchTransitionLimit(call.Arguments, out var searchLimit, out error))
                    return false;
                var searchQuery = NormalizeSemanticPlanLine(GetString(
                    call.Arguments,
                    "query"));
                if (searchQuery.Length < 2)
                {
                    error = "research_transition_query_invalid";
                    return false;
                }
                ignoredEvidenceIdCount = RecordAbandonedNavigationEvidenceIds(
                    bundle,
                    resolvedNavigationEvidenceIds,
                    maximumWorkingEvidenceItems);
                expandedCall = call with
                {
                    Name = "rag_search",
                    Arguments = JsonSerializer.SerializeToElement(new
                    {
                        query = searchQuery,
                        topK = searchLimit
                    }, ClientJson.CamelCase)
                };
                return true;
            default:
                error = "research_transition_tool_invalid";
                return false;
        }
    }

    private static bool TryReadResearchTransitionLimit(
        JsonElement arguments,
        out int limit,
        out string error)
    {
        error = string.Empty;
        if (TryGetInteger(arguments, "limit", out limit) && limit >= 1)
            return true;

        error = "research_transition_limit_invalid";
        return false;
    }

    private static SourceBackedAgentToolDefinition?
        BuildNavigationContextBatchTool(
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            int maximumWorkingEvidenceItems)
    {
        var evidenceIds = SelectPendingNavigationEvidenceIds(
            bundle,
            resolvedNavigationEvidenceIds,
            maximumWorkingEvidenceItems,
            requireResolvableTarget: true);
        if (evidenceIds.Count == 0)
            return null;

        return new SourceBackedAgentToolDefinition(
            NavigationContextBatchToolName,
            "Resout en parallele les ancres de navigation que tu selectionnes. "
            + "Choisis seulement les EvidenceId dont le libelle nomme une instance "
            + "utile du type cible. Le code copie ensuite mecaniquement leurs "
            + "docPath et leur chunkId observe ou leurs pages observees; il ne "
            + "choisit aucune ancre et n'invente aucune coordonnee.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    evidenceIds = new
                    {
                        type = "array",
                        description =
                            "Ancres de navigation a transformer en passages citables.",
                        items = new
                        {
                            type = "string",
                            @enum = evidenceIds
                        },
                        minItems = 1,
                        maxItems = evidenceIds.Count,
                        uniqueItems = true
                    }
                },
                required = new[] { "evidenceIds" },
                additionalProperties = false
            }, ClientJson.CamelCase));
    }

    private static IReadOnlyList<string> SelectPendingNavigationEvidenceIds(
        EvidenceBundle bundle,
        IReadOnlySet<string> resolvedNavigationEvidenceIds,
        int maximumWorkingEvidenceItems,
        bool requireResolvableTarget)
        => bundle.Items
            .Where(item =>
                (string.Equals(
                    item.SourceKind,
                    "navigation_map",
                    StringComparison.OrdinalIgnoreCase)
                 || (SourceBackedContentCardEvidenceContract.IsProoflessCanonicalContentCard(item)
                     && IsMechanicallyResolvableNavigationLocator(item)))
                && !resolvedNavigationEvidenceIds.Contains(item.EvidenceId)
                && IsNavigationAnchorSemanticallyAvailable(item)
                && (!requireResolvableTarget
                    || IsMechanicallyResolvableNavigationLocator(item)))
            .Select(static item => item.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maximumWorkingEvidenceItems))
            .ToArray();

    private static int RecordAbandonedNavigationEvidenceIds(
        EvidenceBundle bundle,
        ISet<string> resolvedNavigationEvidenceIds,
        int maximumWorkingEvidenceItems)
    {
        var abandonedEvidenceIds = SelectPendingNavigationEvidenceIds(
            bundle,
            (IReadOnlySet<string>)resolvedNavigationEvidenceIds,
            maximumWorkingEvidenceItems,
            requireResolvableTarget: false);
        var added = 0;
        foreach (var evidenceId in abandonedEvidenceIds)
        {
            if (resolvedNavigationEvidenceIds.Add(evidenceId))
                added++;
        }
        return added;
    }

    private static bool TryExpandNavigationContextBatchArguments(
        JsonElement arguments,
        EvidenceBundle bundle,
        out JsonElement expandedArguments,
        out string error)
    {
        expandedArguments = default;
        error = string.Empty;
        if (!TryGetPropertyIgnoreCase(
                arguments,
                "evidenceIds",
                out var evidenceIdsElement)
            || evidenceIdsElement.ValueKind != JsonValueKind.Array)
        {
            error = "navigation_context_batch_evidence_ids_required";
            return false;
        }

        var evidenceIds = evidenceIdsElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
        if (evidenceIds.Length == 0
            || evidenceIds.Length != evidenceIdsElement.GetArrayLength()
            || evidenceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != evidenceIds.Length)
        {
            error = "navigation_context_batch_evidence_ids_invalid";
            return false;
        }

        var targets = new List<object>(evidenceIds.Length);
        foreach (var evidenceId in evidenceIds)
        {
            if (!bundle.ById.TryGetValue(evidenceId, out var item)
                || !IsMechanicallyResolvableNavigationLocator(item))
            {
                error =
                    "navigation_context_batch_evidence_not_resolvable:"
                    + evidenceId;
                return false;
            }

            targets.Add(new
            {
                evidenceId,
                sourceAnchorLabel = NormalizeBatchOptionalString(item.Excerpt),
                anchorId = NormalizeBatchOptionalString(item.AnchorId),
                docId = NormalizeBatchOptionalString(item.DocId),
                docPath = NormalizeBatchOptionalString(item.DocPath),
                docRef = NormalizeBatchOptionalString(item.DocName),
                chunkId = item.ChunkId,
                pageStart = item.PageStart,
                pageEnd = item.PageEnd,
                before = 0,
                after = 1,
                limit = 3,
                offset = 0
            });
        }

        expandedArguments = JsonSerializer.SerializeToElement(new
        {
            evidenceIds,
            targets
        }, ClientJson.CamelCase);
        return true;
    }

    private static string? NormalizeBatchOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int RecordResolvedNavigationEvidenceIds(
        JsonElement arguments,
        ToolResults results,
        ISet<string> resolvedNavigationEvidenceIds)
    {
        if (!TryGetPropertyIgnoreCase(
                arguments,
                "evidenceIds",
                out var evidenceIdsElement)
            || evidenceIdsElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var evidenceIds = evidenceIdsElement.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null)
            .ToArray();
        var added = 0;
        for (var index = 0;
             index < evidenceIds.Length && index < results.Items.Count;
             index++)
        {
            var evidenceId = evidenceIds[index];
            if (string.IsNullOrWhiteSpace(evidenceId)
                || !string.IsNullOrWhiteSpace(results.Items[index].Error)
                || !resolvedNavigationEvidenceIds.Add(evidenceId))
            {
                continue;
            }

            added++;
        }

        return added;
    }
}
