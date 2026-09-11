using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool TryReadNamedDocumentTransition(
        SourceBackedAgentCompletion completion,
        SourceBackedIntake intake,
        NamedDocumentInitialActionPreparation preparation,
        IReadOnlyList<SourceBackedAgentToolDefinition> transitionTools,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
        bool rejectImmediateInsufficiency,
        out SourceBackedIntake effectiveIntake,
        out SourceBackedAgentCompletion? terminalCompletion,
        out string decision,
        out bool retryCatalog,
        out string failureReason)
    {
        effectiveIntake = intake;
        terminalCompletion = null;
        decision = string.Empty;
        retryCatalog = false;
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1)
        {
            failureReason = "named_document_transition_single_call_required";
            return false;
        }

        var call = completion.ToolCalls[0];
        var matchedTool = transitionTools.FirstOrDefault(tool => string.Equals(
                tool.Name,
                call.Name,
                StringComparison.OrdinalIgnoreCase));
        if (matchedTool is null)
        {
            failureReason = "named_document_transition_not_available";
            return false;
        }
        decision = matchedTool.Name;
        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            failureReason = "named_document_transition_object_required";
            return false;
        }

        return decision switch
        {
            DeclareNamedDocumentInsufficiencyToolName
                => rejectImmediateInsufficiency
                    ? FailNamedDocumentTransition(
                        "named_document_immediate_insufficiency_requires_reference_reconsideration",
                        out failureReason)
                    : TryBuildNamedDocumentInsufficiency(
                        completion,
                        call.Arguments,
                        out terminalCompletion,
                        out failureReason),
            RequestNamedDocumentReferenceCorrectionToolName
                => TryBuildNamedDocumentReferenceCorrection(
                    completion,
                    call.Arguments,
                    out terminalCompletion,
                    out failureReason),
            ResearchNamedDocumentAlternativesToolName
                => TryBuildAlternativeResearchIntake(
                    completion,
                    call.Arguments,
                    intake,
                    availableTools,
                    out effectiveIntake,
                    out failureReason),
            RequestNamedDocumentCandidateSelectionToolName
                => TryBuildNamedDocumentCandidateSelection(
                    completion,
                    call.Arguments,
                    intake,
                    out terminalCompletion,
                    out failureReason),
            RetryNamedDocumentCatalogToolName
                => RequestNamedDocumentCatalogRetry(
                    call.Arguments,
                    out retryCatalog,
                    out failureReason),
            UseResolvedNamedDocumentToolName
                => TryUseResolvedNamedDocument(
                    call.Arguments,
                    intake,
                    preparation,
                    out effectiveIntake,
                    out failureReason),
            ReinterpretNamedReferenceAsSubjectToolName
                => TryReinterpretNamedReferenceAsSubject(
                    call.Arguments,
                    intake,
                    preparation,
                    availableTools,
                    out effectiveIntake,
                    out failureReason),
            _ => FailNamedDocumentTransition(
                "named_document_transition_unknown",
                out failureReason)
        };
    }

    private static bool TryBuildNamedDocumentReferenceCorrection(
        SourceBackedAgentCompletion sourceCompletion,
        JsonElement arguments,
        out SourceBackedAgentCompletion? terminalCompletion,
        out string failureReason)
    {
        terminalCompletion = null;
        var question = ReadCompactFollowUpString(arguments, "identityQuestion");
        var impact = ReadCompactFollowUpString(arguments, "executionImpact");
        if (!HasOnlyProperties(
                arguments,
                "identityQuestion",
                "identityCorrectionOptions",
                "executionImpact")
            || question.Length is < 4 or > 300
            || !question.EndsWith("?", StringComparison.Ordinal)
            || impact.Length is < 2 or > 240
            || !TryReadDecisionOptions(
                arguments,
                "identityCorrectionOptions",
                out var options))
        {
            failureReason = "named_document_reference_correction_invalid";
            return false;
        }

        terminalCompletion = BuildNamedDocumentClarificationCompletion(
            sourceCompletion,
            question,
            options,
            impact);
        failureReason = string.Empty;
        return true;
    }

    private static bool TryReinterpretNamedReferenceAsSubject(
        JsonElement arguments,
        SourceBackedIntake intake,
        NamedDocumentInitialActionPreparation preparation,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
        out SourceBackedIntake effectiveIntake,
        out string failureReason)
    {
        effectiveIntake = intake;
        var restoredActions = SelectAvailableQuarantinedActions(
            preparation,
            availableTools);
        var status = intake.RequestedDocumentResolution?.Status;
        if (!HasOnlyProperties(arguments)
            || !string.Equals(
                ReadExplicitNamedReferenceKind(intake),
                NamedReferenceDocument,
                StringComparison.Ordinal)
            || status is not (
                SourceBackedDocumentResolutionStatus.NotFound
                or SourceBackedDocumentResolutionStatus.Ambiguous)
            || restoredActions.Count == 0)
        {
            failureReason =
                "named_document_subject_reinterpretation_invalid";
            return false;
        }

        effectiveIntake = intake with
        {
            RequestedDocumentName = null,
            RequestedDocumentResolution = null,
            NamedReferenceKind = NamedReferenceSubject,
            InitialToolCalls = restoredActions,
            DocumentScope = SourceBackedDocumentScope.RequestedDocument,
            DocumentScopeDisclosure = null
        };
        failureReason = string.Empty;
        return true;
    }

    private static bool TryBuildNamedDocumentCandidateSelection(
        SourceBackedAgentCompletion sourceCompletion,
        JsonElement arguments,
        SourceBackedIntake intake,
        out SourceBackedAgentCompletion? terminalCompletion,
        out string failureReason)
    {
        terminalCompletion = null;
        var question = ReadCompactFollowUpString(arguments, "question");
        var impact = ReadCompactFollowUpString(arguments, "executionImpact");
        var observation = intake.RequestedDocumentResolution;
        if (!HasOnlyProperties(arguments, "question", "executionImpact")
            || question.Length is < 4 or > 300
            || !question.EndsWith("?", StringComparison.Ordinal)
            || impact.Length is < 2 or > 240
            || observation is null
            || observation.Status != SourceBackedDocumentResolutionStatus.Ambiguous
            || !TryBuildCandidateSelectionOptions(
                observation.Candidates,
                out var options))
        {
            failureReason = "named_document_candidate_selection_invalid";
            return false;
        }

        terminalCompletion = BuildNamedDocumentClarificationCompletion(
            sourceCompletion,
            question,
            options,
            impact);
        failureReason = string.Empty;
        return true;
    }

    private static SourceBackedAgentCompletion
        BuildNamedDocumentClarificationCompletion(
            SourceBackedAgentCompletion sourceCompletion,
            string question,
            IReadOnlyList<string> options,
            string impact)
        => sourceCompletion with
        {
            ToolCalls = new[]
            {
                new SourceBackedAgentToolCall(
                    sourceCompletion.ToolCalls[0].Id,
                    RequestSourceBackedClarificationToolName,
                    JsonSerializer.SerializeToElement(new
                    {
                        understanding = question,
                        options,
                        executionImpact = impact,
                        ambiguityKind = "source_identity"
                    }))
            }
        };

    private static bool TryBuildCandidateSelectionOptions(
        IReadOnlyList<SourceBackedDocumentResolutionCandidate> candidates,
        out string[] options)
    {
        options = candidates
            .Select(static candidate =>
                candidate.DocPath.Trim() is { Length: > 0 and <= 180 } path
                    ? path
                    : candidate.DocId.Trim())
            .Where(static identity => identity.Length is > 0 and <= 180)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return options.Length >= 2;
    }

    private static bool TryBuildNamedDocumentInsufficiency(
        SourceBackedAgentCompletion sourceCompletion,
        JsonElement arguments,
        out SourceBackedAgentCompletion? terminalCompletion,
        out string failureReason)
    {
        var reason = ReadCompactFollowUpString(arguments, "reason");
        if (!HasOnlyProperties(arguments, "reason")
            || reason.Length is < 10 or > 400)
        {
            terminalCompletion = null;
            failureReason = "named_document_transition_reason_invalid";
            return false;
        }
        terminalCompletion = sourceCompletion with
        {
            ToolCalls = new[]
            {
                new SourceBackedAgentToolCall(
                    sourceCompletion.ToolCalls[0].Id,
                    DeclareSourceInsufficiencyToolName,
                    JsonSerializer.SerializeToElement(new { reason }))
            }
        };
        failureReason = string.Empty;
        return true;
    }

    private static bool TryBuildAlternativeResearchIntake(
        SourceBackedAgentCompletion sourceCompletion,
        JsonElement arguments,
        SourceBackedIntake intake,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
        out SourceBackedIntake effectiveIntake,
        out string failureReason)
    {
        effectiveIntake = intake;
        var capability = ReadCompactFollowUpString(arguments, "capability");
        var query = ReadCompactFollowUpString(arguments, "query");
        var disclosure = ReadCompactFollowUpString(arguments, "disclosure");
        if (!HasOnlyProperties(
                arguments,
                "capability",
                "query",
                "limit",
                "disclosure")
            || capability is not (
                "rag_search" or "documents_navigation"
                or "documents_content_cards")
            || !availableTools.Any(tool => string.Equals(
                tool.Name,
                capability,
                StringComparison.OrdinalIgnoreCase))
            || query.Length is < 1 or > 600
            || disclosure.Length is < 10 or > 300)
        {
            failureReason = "named_document_alternative_research_invalid";
            return false;
        }

        var limit = ReadOptionalDecisionInt(arguments, "limit") ?? 10;
        if (limit is < 1 or > 100)
        {
            failureReason = "named_document_alternative_limit_invalid";
            return false;
        }
        var actionArguments = new JsonObject();
        if (string.Equals(capability, "rag_search", StringComparison.Ordinal))
        {
            actionArguments["query"] = query;
            actionArguments["topK"] = limit;
        }
        else
        {
            actionArguments["q"] = query;
            actionArguments["limit"] = limit;
            actionArguments["offset"] = 0;
        }
        effectiveIntake = intake with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    sourceCompletion.ToolCalls[0].Id + "-alternative",
                    capability,
                    JsonSerializer.SerializeToElement(actionArguments),
                    "llm_named_document_alternative_scope")
            },
            DocumentScope = SourceBackedDocumentScope.AlternativeSources,
            DocumentScopeDisclosure = disclosure
        };
        failureReason = string.Empty;
        return true;
    }

    private static bool TryUseResolvedNamedDocument(
        JsonElement arguments,
        SourceBackedIntake intake,
        NamedDocumentInitialActionPreparation preparation,
        out SourceBackedIntake effectiveIntake,
        out string failureReason)
    {
        effectiveIntake = intake;
        var observation = intake.RequestedDocumentResolution;
        if (!HasOnlyProperties(arguments)
            || observation is null
            || observation.Status != SourceBackedDocumentResolutionStatus.Resolved
            || !observation.CatalogObservationComplete
            || observation.Candidates.Count != 1
            || !string.Equals(
                preparation.ReasonCode,
                "resolved_identity_conflict",
                StringComparison.Ordinal)
            || preparation.QuarantinedActions.Count == 0)
        {
            failureReason = "named_document_resolved_transition_invalid";
            return false;
        }

        var candidate = observation.Candidates[0];
        var scopedActions = new List<SourceBackedInitialToolCall>();
        foreach (var action in preparation.QuarantinedActions)
        {
            if (!TryApplyResolvedIdentity(action, candidate, out var scopedAction))
            {
                failureReason = "named_document_resolved_action_invalid";
                return false;
            }
            scopedActions.Add(scopedAction);
        }
        effectiveIntake = intake with
        {
            InitialToolCalls = scopedActions,
            DocumentScope = SourceBackedDocumentScope.RequestedDocument,
            DocumentScopeDisclosure = null
        };
        failureReason = string.Empty;
        return true;
    }

    private static bool TryApplyResolvedIdentity(
        SourceBackedInitialToolCall action,
        SourceBackedDocumentResolutionCandidate candidate,
        out SourceBackedInitialToolCall scopedAction)
    {
        scopedAction = action;
        if (action.Arguments.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(candidate.DocId)
            || string.IsNullOrWhiteSpace(candidate.DocPath)
            || JsonNode.Parse(action.Arguments.GetRawText()) is not JsonObject arguments)
        {
            return false;
        }
        arguments["docId"] = candidate.DocId;
        arguments["docPath"] = candidate.DocPath;
        scopedAction = action with
        {
            Arguments = JsonSerializer.SerializeToElement(arguments)
        };
        return true;
    }

    private static bool TryReadDecisionOptions(
        JsonElement arguments,
        string propertyName,
        out string[] options)
    {
        options = Array.Empty<string>();
        if (!TryGetPropertyIgnoreCase(arguments, propertyName, out var node)
            || node.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var submitted = node.EnumerateArray().ToArray();
        options = submitted
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToArray();
        return submitted.Length is >= 2 and <= 4
               && options.Length == submitted.Length
               && options.All(static item => item.Length <= 180)
               && options.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               == options.Length;
    }

    private static int? ReadOptionalDecisionInt(
        JsonElement arguments,
        string propertyName)
        => TryGetPropertyIgnoreCase(arguments, propertyName, out var node)
           && node.ValueKind == JsonValueKind.Number
           && node.TryGetInt32(out var value)
            ? value
            : null;

    private static bool FailNamedDocumentTransition(
        string reason,
        out string failureReason)
    {
        failureReason = reason;
        return false;
    }

    private static bool RequestNamedDocumentCatalogRetry(
        JsonElement arguments,
        out bool retryCatalog,
        out string failureReason)
    {
        if (!HasOnlyProperties(arguments))
        {
            retryCatalog = false;
            failureReason = "named_document_catalog_retry_arguments_invalid";
            return false;
        }
        retryCatalog = true;
        failureReason = string.Empty;
        return true;
    }

    private static bool HasOnlyProperties(
        JsonElement arguments,
        params string[] allowedNames)
    {
        var names = arguments
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                   == names.Length
               && names.All(name => allowedNames.Contains(
                   name,
                   StringComparer.OrdinalIgnoreCase));
    }
}
