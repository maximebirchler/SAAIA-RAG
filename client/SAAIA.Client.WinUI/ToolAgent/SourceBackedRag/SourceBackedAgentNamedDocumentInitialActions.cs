using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record NamedDocumentInitialActionPreparation(
        SourceBackedIntake Intake,
        IReadOnlyList<SourceBackedInitialToolCall> QuarantinedActions,
        bool SuppressPlannedFirstAction,
        string ReasonCode);

    private static NamedDocumentInitialActionPreparation
        PrepareNamedDocumentInitialActions(SourceBackedIntake intake)
    {
        var observation = intake.RequestedDocumentResolution;
        if (observation is null)
        {
            return new NamedDocumentInitialActionPreparation(
                intake,
                Array.Empty<SourceBackedInitialToolCall>(),
                SuppressPlannedFirstAction: false,
                ReasonCode: "not_requested");
        }

        var originalActions = intake.InitialToolCalls
                              ?? Array.Empty<SourceBackedInitialToolCall>();
        if (observation.Status != SourceBackedDocumentResolutionStatus.Resolved
            || !observation.CatalogObservationComplete
            || observation.Candidates.Count != 1)
        {
            return new NamedDocumentInitialActionPreparation(
                intake with
                {
                    InitialToolCalls = Array.Empty<SourceBackedInitialToolCall>()
                },
                originalActions.ToArray(),
                SuppressPlannedFirstAction: true,
                ReasonCode: "catalog_" + observation.Status.ToString()
                    .ToLowerInvariant());
        }

        var candidate = observation.Candidates[0];
        var scopedActions = new List<SourceBackedInitialToolCall>(
            originalActions.Count);
        var rewroteUnanchoredContext = false;
        var appliedQueryHint = false;
        foreach (var action in originalActions)
        {
            if (!TryScopeInitialAction(
                    action,
                    candidate,
                    intake.UserQuestion,
                    out var scopedAction,
                    out var actionRewritten,
                    out var actionQueryHintApplied))
            {
                return new NamedDocumentInitialActionPreparation(
                    intake with
                    {
                        InitialToolCalls =
                            Array.Empty<SourceBackedInitialToolCall>()
                    },
                    originalActions.ToArray(),
                    SuppressPlannedFirstAction: true,
                    ReasonCode: "resolved_identity_conflict");
            }
            rewroteUnanchoredContext |= actionRewritten;
            appliedQueryHint |= actionQueryHintApplied;
            scopedActions.Add(scopedAction);
        }

        return new NamedDocumentInitialActionPreparation(
            intake with { InitialToolCalls = scopedActions },
            Array.Empty<SourceBackedInitialToolCall>(),
            SuppressPlannedFirstAction: false,
            ReasonCode: rewroteUnanchoredContext
                ? "resolved_unanchored_context_rewritten_to_search"
                : appliedQueryHint
                    ? "resolved_query_hint_applied"
                : "resolved_identity_applied");
    }

    private static bool TryScopeInitialAction(
        SourceBackedInitialToolCall action,
        SourceBackedDocumentResolutionCandidate candidate,
        string userQuestion,
        out SourceBackedInitialToolCall scopedAction,
        out bool rewrittenToSearch,
        out bool queryHintApplied)
    {
        scopedAction = action;
        rewrittenToSearch = false;
        queryHintApplied = false;
        if (action.Arguments.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(candidate.DocId)
            || string.IsNullOrWhiteSpace(candidate.DocPath))
        {
            return false;
        }

        var arguments = JsonNode.Parse(action.Arguments.GetRawText()) as JsonObject;
        if (arguments is null)
            return false;
        if (!ExistingIdentityIsCompatible(arguments, candidate))
            return false;

        arguments["docId"] = candidate.DocId;
        arguments["docPath"] = candidate.DocPath;
        if (IsUnanchoredDocumentContext(action.ToolName, arguments))
        {
            var query = string.IsNullOrWhiteSpace(action.QueryHint)
                ? userQuestion
                : action.QueryHint;
            if (string.IsNullOrWhiteSpace(query))
                return false;
            scopedAction = action with
            {
                ToolName = SourceBackedAgentToolCatalog.ToExternalName(
                    "rag.search"),
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    query = query.Trim(),
                    docId = candidate.DocId,
                    docPath = candidate.DocPath
                })
            };
            rewrittenToSearch = true;
            queryHintApplied = !string.IsNullOrWhiteSpace(action.QueryHint);
            return true;
        }

        if (IsDocumentNavigation(action.ToolName)
            && ReadNonBlankString(arguments, "q") is null
            && !string.IsNullOrWhiteSpace(action.QueryHint))
        {
            arguments["q"] = action.QueryHint.Trim();
            queryHintApplied = true;
        }
        scopedAction = action with
        {
            Arguments = JsonSerializer.SerializeToElement(arguments)
        };
        return true;
    }

    private static bool IsDocumentNavigation(string toolName)
        => string.Equals(
               toolName,
               "documents.navigation",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               toolName,
               "documents_navigation",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsUnanchoredDocumentContext(
        string toolName,
        JsonObject arguments)
    {
        var isDocumentContext = string.Equals(
                toolName,
                "documents.context",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                toolName,
                "documents_context",
                StringComparison.OrdinalIgnoreCase);
        if (!isDocumentContext)
            return false;

        return ReadNonBlankString(arguments, "chunkId") is null
               && !HasPositiveInteger(arguments, "pageStart")
               && !HasPositiveInteger(arguments, "pageEnd");
    }

    private static bool HasPositiveInteger(
        JsonObject arguments,
        string propertyName)
    {
        if (!arguments.TryGetPropertyValue(propertyName, out var node)
            || node is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<int>(out var number) && number > 0;
    }

    private static bool ExistingIdentityIsCompatible(
        JsonObject arguments,
        SourceBackedDocumentResolutionCandidate candidate)
    {
        var existingDocId = ReadNonBlankString(arguments, "docId");
        if (existingDocId is not null
            && !string.Equals(
                existingDocId,
                candidate.DocId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existingDocPath = ReadNonBlankString(arguments, "docPath");
        return existingDocPath is null
               || SourceBackedNamedDocumentResolver.IsExactMatch(
                   existingDocPath,
                   candidate);
    }

    private static string? ReadNonBlankString(
        JsonObject arguments,
        string propertyName)
    {
        if (!arguments.TryGetPropertyValue(propertyName, out var node)
            || node is null)
        {
            return null;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private void AddNamedDocumentInitialActionTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        NamedDocumentInitialActionPreparation preparation)
    {
        var observation = preparation.Intake.RequestedDocumentResolution;
        if (observation is null)
            return;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_named_document_initial_actions.prepared",
            ("status", observation.Status),
            ("complete", observation.CatalogObservationComplete),
            ("reason", preparation.ReasonCode),
            ("executable_actions",
                preparation.Intake.InitialToolCalls?.Count ?? 0),
            ("quarantined_actions", preparation.QuarantinedActions.Count),
            ("suppress_planned_action",
                preparation.SuppressPlannedFirstAction)));
    }
}
