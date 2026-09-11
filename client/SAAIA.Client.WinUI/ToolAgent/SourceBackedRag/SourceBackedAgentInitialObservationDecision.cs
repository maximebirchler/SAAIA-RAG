using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitInitialObservationDecisionToolName =
        "submit_initial_observation_decision";

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildInitialObservationDecisionTools(
            IReadOnlyList<string> permittedCategoryPaths)
    {
        var properties = new Dictionary<string, object?>
        {
            ["capability"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "documents_navigation_entries",
                    "documents_navigation_titles",
                    "documents_navigation_all",
                    "documents_content_cards_representative",
                    "documents_content_cards_ordered",
                    "rag_search"
                }
            },
            ["query"] = CompactFollowUpString(
                0,
                180,
                "Termes source reels. Chaine vide pour inventorier des noms inconnus; rag_search exige une valeur non vide."),
            ["coverageBudget"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "no_rejection_expected",
                    "some_rejections_expected",
                    "rejection_rate_unknown_or_high"
                },
                description =
                    "Prevision semantique du risque de rejet, pas une quantite. no_rejection_expected ne laisse aucune reserve; rejection_rate_unknown_or_high utilise toute la capacite disponible."
            }
        };
        var required = new List<string>
        {
            "capability",
            "query",
            "coverageBudget"
        };
        if (permittedCategoryPaths.Count > 1)
        {
            properties["scope"] = new
            {
                type = "string",
                @enum = new[] { string.Empty }
                    .Concat(permittedCategoryPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            required.Add("scope");
        }

        return
        [
            new SourceBackedAgentToolDefinition(
                SubmitInitialObservationDecisionToolName,
                "Choisis une seule premiere observation et sa marge de couverture.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties,
                    required,
                    additionalProperties = false
                }, ClientJson.CamelCase))
        ];
    }

    private static bool TryReadInitialObservationDecision(
        SourceBackedAgentCompletion completion,
        SourceBackedIntake intake,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
        int maximumEvidenceItems,
        int requiredAtomicEvidenceCount,
        IReadOnlyList<string> permittedCategoryPaths,
        out IReadOnlyList<SourceBackedInitialToolCall> actions,
        out string coverageBudget,
        out string failureReason)
    {
        actions = Array.Empty<SourceBackedInitialToolCall>();
        coverageBudget = string.Empty;
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SubmitInitialObservationDecisionToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "initial_observation_single_decision_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        var capability = ReadCompactFollowUpString(arguments, "capability");
        var query = ReadCompactFollowUpString(arguments, "query");
        coverageBudget = ReadCompactFollowUpString(
            arguments,
            "coverageBudget");
        if (query.Length > 180)
        {
            failureReason = "initial_observation_query_invalid";
            return false;
        }
        if (coverageBudget is not (
                "no_rejection_expected"
                or "some_rejections_expected"
                or "rejection_rate_unknown_or_high"))
        {
            failureReason = "initial_observation_coverage_budget_invalid";
            return false;
        }
        if (string.Equals(
                capability,
                "rag_search",
                StringComparison.Ordinal)
            && query.Length == 0)
        {
            failureReason = "initial_observation_rag_search_query_required";
            return false;
        }

        var submittedScope = ReadCompactFollowUpString(arguments, "scope");
        string scope;
        if (permittedCategoryPaths.Count == 1)
        {
            scope = permittedCategoryPaths[0];
        }
        else if (permittedCategoryPaths.Count > 1)
        {
            if (!TryGetPropertyIgnoreCase(arguments, "scope", out var scopeElement)
                || scopeElement.ValueKind != JsonValueKind.String
                || (submittedScope.Length > 0
                    && !permittedCategoryPaths.Contains(
                        submittedScope,
                        StringComparer.OrdinalIgnoreCase)))
            {
                failureReason = "initial_observation_scope_invalid";
                return false;
            }
            scope = submittedScope;
        }
        else
        {
            scope = string.Empty;
        }

        var (normalizedCapability, navigationKind, inventoryMode) = capability switch
        {
            "documents_navigation_entries" =>
                ("documents_navigation", "navigation_entry", string.Empty),
            "documents_navigation_titles" =>
                ("documents_navigation", "title_anchor", string.Empty),
            "documents_navigation_all" =>
                ("documents_navigation", string.Empty, string.Empty),
            "documents_content_cards_representative" =>
                ("documents_content_cards", string.Empty, "representative"),
            "documents_content_cards_ordered" =>
                ("documents_content_cards", string.Empty, "ordered"),
            "rag_search" =>
                ("rag_search", string.Empty, string.Empty),
            _ => (string.Empty, string.Empty, string.Empty)
        };
        if (normalizedCapability.Length == 0)
        {
            failureReason = "initial_observation_capability_invalid";
            return false;
        }

        var limit = ResolveInitialObservationLimit(
            coverageBudget,
            requiredAtomicEvidenceCount,
            maximumEvidenceItems);
        var normalizedInput = JsonSerializer.SerializeToElement(new
        {
            capability = normalizedCapability,
            query,
            scope,
            document = string.Empty,
            anchor = string.Empty,
            navigationKind,
            inventoryMode,
            limit,
            offset = 0
        }, ClientJson.CamelCase);
        if (!TryNormalizeResearchAction(
                "llm-initial-observation",
                normalizedInput,
                intake,
                EvidenceBundle.Empty(intake.UserQuestion),
                out var normalizedCall,
                out _))
        {
            failureReason = "initial_observation_normalization_failed";
            return false;
        }
        if (!availableTools.Any(tool => string.Equals(
                tool.Name,
                normalizedCall.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            failureReason = "initial_observation_tool_unavailable";
            return false;
        }

        actions =
        [
            new SourceBackedInitialToolCall(
                normalizedCall.Id,
                normalizedCall.Name,
                normalizedCall.Arguments,
                "llm_initial_observation")
        ];
        return true;
    }

    private static int ResolveInitialObservationLimit(
        string coverageBudget,
        int requiredAtomicEvidenceCount,
        int maximumEvidenceItems)
    {
        var maximum = Math.Clamp(maximumEvidenceItems, 1, 120);
        var minimum = Math.Clamp(requiredAtomicEvidenceCount, 1, maximum);
        return coverageBudget switch
        {
            "no_rejection_expected" => minimum,
            "some_rejections_expected" => Math.Min(
                maximum,
                Math.Max(minimum + 1, (int)Math.Ceiling(minimum * 1.5))),
            "rejection_rate_unknown_or_high" => maximum,
            _ => minimum
        };
    }
}
