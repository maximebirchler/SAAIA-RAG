using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitInitialResearchBatchToolName =
        "submit_initial_research_batch";
    private const int MaximumInitialResearchActions = 8;

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildInitialResearchBatchTools(
            int maximumEvidenceItems,
            IReadOnlyList<string> permittedCategoryPaths,
            string candidatePoolRelation)
    {
        var maximumLimit = Math.Clamp(maximumEvidenceItems, 8, 120);
        var usesSharedPool = string.Equals(
            candidatePoolRelation,
            SharedCandidatePoolRelation,
            StringComparison.Ordinal);
        var scopeValues = new[] { string.Empty }
            .Concat(permittedCategoryPaths)
            .Where(static path => path.Length <= 120)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        object scopeSchema = permittedCategoryPaths.Count > 0
            ? new
            {
                type = "string",
                @enum = scopeValues,
                description =
                    "Chemin de catalogue exact choisi parmi l'enum. La chaine vide signifie corpus complet; un scope exact enumere peut affiner ce corpus."
            }
            : CompactFollowUpString(
                0,
                120,
                "Chemin de catalogue minimal. La chaine vide signifie corpus complet quand aucun catalogue exact n'est disponible.");
        var actionProperties = new Dictionary<string, object?>
        {
            ["capability"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "rag_search",
                    "documents_navigation",
                    "documents_content_cards",
                    "documents_context"
                }
            },
            ["query"] = CompactFollowUpString(
                0,
                180,
                "Termes susceptibles d'apparaitre dans la preuve atomique elle-meme; jamais le nom, les axes ou la forme du livrable a construire. Laisser vide pour inventorier des noms inconnus."),
            ["document"] = CompactFollowUpString(0, 220),
            ["anchor"] = CompactFollowUpString(0, 160),
            ["pageStart"] = new
            {
                type = "integer",
                minimum = 1
            },
            ["pageEnd"] = new
            {
                type = "integer",
                minimum = 1
            },
            ["limit"] = new
            {
                type = "integer",
                minimum = 1,
                maximum = maximumLimit,
                description =
                    "Budget de candidats, pas quota final. Pour un inventaire qui exige N instances et peut contenir des rejets, N ne constitue aucune marge: choisir librement une valeur superieure dans la limite autorisee."
            },
            ["offset"] = new
            {
                type = "integer",
                minimum = 0,
                maximum = 10000
            },
            ["mode"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "auto", "focused", "balanced", "broad"
                }
            },
            ["inventoryMode"] = new
            {
                type = "string",
                @enum = new[]
                {
                    string.Empty, "ordered", "representative"
                },
                description =
                    "Pour documents_content_cards sans query seulement: ordered suit l'ordre des sources; representative expose des positions reparties quand aucun terme source fiable n'est encore connu. Chaine vide pour les autres capacites."
            },
            ["navigationKind"] = new
            {
                type = "string",
                description =
                    "Pour documents_navigation seulement: navigation_entry cible les entrees structurees de sommaire/index; title_anchor cible les autres titres; chaine vide conserve les deux.",
                @enum = new[]
                {
                    string.Empty, "navigation_entry", "title_anchor"
                }
            }
        };
        var actionRequired = new List<string>
        {
            "capability",
            "query",
            "document",
            "anchor",
            "inventoryMode",
            "limit",
            "offset"
        };
        if (!usesSharedPool)
        {
            actionProperties["scope"] = scopeSchema;
            actionRequired.Insert(2, "scope");
        }
        var rootProperties = new Dictionary<string, object?>
        {
            ["actions"] = new
            {
                type = "array",
                description =
                    "Lot sous budget global: la somme des limit de toutes les actions ne peut pas depasser le maximum autorise pour une action.",
                minItems = 1,
                maxItems = MaximumInitialResearchActions,
                items = new
                {
                    type = "object",
                    properties = actionProperties,
                    required = actionRequired,
                    additionalProperties = false
                }
            }
        };
        var rootRequired = new List<string> { "actions" };
        if (usesSharedPool)
        {
            rootProperties["sharedScope"] = scopeSchema;
            rootRequired.Insert(0, "sharedScope");
        }
        return
        [
            new SourceBackedAgentToolDefinition(
                SubmitInitialResearchBatchToolName,
                "Soumets le plus petit lot utile d'actions documentaires independantes.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = rootProperties,
                    required = rootRequired,
                    additionalProperties = false
                }, ClientJson.CamelCase))
        ];
    }

    private static bool TryReadInitialResearchActions(
        SourceBackedAgentCompletion completion,
        SourceBackedIntake intake,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
        int maximumEvidenceItems,
        string candidatePoolRelation,
        IReadOnlyList<string> candidateScopePaths,
        out IReadOnlyList<SourceBackedInitialToolCall> actions,
        out string failureReason)
    {
        actions = Array.Empty<SourceBackedInitialToolCall>();
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SubmitInitialResearchBatchToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "initial_research_batch_single_submission_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        var usesSharedPool = string.Equals(
            candidatePoolRelation,
            SharedCandidatePoolRelation,
            StringComparison.Ordinal);
        var sharedScope = string.Empty;
        var sharedScopeElement = default(JsonElement);
        if (usesSharedPool
            && (!TryGetPropertyIgnoreCase(
                    arguments,
                    "sharedScope",
                    out sharedScopeElement)
                || sharedScopeElement.ValueKind != JsonValueKind.String
                || (sharedScopeElement.GetString()?.Length ?? 0) > 120))
        {
            failureReason = "initial_research_shared_scope_required";
            return false;
        }
        if (usesSharedPool)
            sharedScope = sharedScopeElement.GetString()?.Trim() ?? string.Empty;
        if (!TryGetPropertyIgnoreCase(arguments, "actions", out var submittedActions)
            || submittedActions.ValueKind != JsonValueKind.Array)
        {
            failureReason = "initial_research_batch_actions_array_required";
            return false;
        }

        var submitted = submittedActions.EnumerateArray().ToArray();
        if (submitted.Length is < 1 or > MaximumInitialResearchActions)
        {
            failureReason = "initial_research_batch_action_count_invalid";
            return false;
        }

        var normalized = new List<SourceBackedInitialToolCall>(submitted.Length);
        var rejectedActions = new List<string>();
        for (var index = 0; index < submitted.Length; index++)
        {
            var submittedAction = usesSharedPool
                ? ApplySharedInitialResearchScope(
                    submitted[index],
                    sharedScope)
                : submitted[index];
            if (!TryValidateInitialResearchActionArguments(
                    submittedAction,
                    out var actionFailureReason))
            {
                rejectedActions.Add(
                    $"initial_research_batch_action_{index + 1}:"
                    + actionFailureReason);
                continue;
            }

            var callId = $"llm-initial-research-{index + 1}";
            if (!TryNormalizeResearchAction(
                    callId,
                    submittedAction,
                    intake,
                    EvidenceBundle.Empty(intake.UserQuestion),
                    out var normalizedCall,
                    out _))
            {
                rejectedActions.Add(
                    $"initial_research_batch_action_{index + 1}:"
                    + "normalization_failed");
                continue;
            }
            if (string.Equals(
                    normalizedCall.Name,
                    "documents_context",
                    StringComparison.OrdinalIgnoreCase)
                && !HasGroundedInitialDocumentContextPointer(
                    normalizedCall.Arguments))
            {
                rejectedActions.Add(
                    $"initial_research_batch_action_{index + 1}:"
                    + "documents_context_requires_grounded_pointer");
                continue;
            }
            if (!availableTools.Any(tool => string.Equals(
                    tool.Name,
                    normalizedCall.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                rejectedActions.Add(
                    $"initial_research_batch_action_{index + 1}:"
                    + "tool_unavailable");
                continue;
            }

            normalized.Add(new SourceBackedInitialToolCall(
                normalizedCall.Id,
                normalizedCall.Name,
                normalizedCall.Arguments,
                "llm_initial_research_batch"));
        }

        var maximumAggregateLimit = Math.Clamp(maximumEvidenceItems, 8, 120);
        var aggregateLimit = normalized.Sum(static action =>
            TryGetInteger(action.Arguments, "limit", out var limit)
                ? limit
                : TryGetInteger(action.Arguments, "topK", out var topK)
                    ? topK
                    : 0);
        var mechanicalAdjustments = new List<string>();
        if (aggregateLimit > maximumAggregateLimit)
        {
            normalized = NormalizeInitialResearchAggregateLimits(
                normalized,
                maximumAggregateLimit);
            mechanicalAdjustments.Add(
                "initial_research_batch_limits_scaled:"
                + aggregateLimit
                + ">"
                + maximumAggregateLimit);
        }
        var distinctCategoryScopes = normalized
            .Select(static action =>
                GetString(action.Arguments, "categoryPath")?.Trim()
                ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var availableCategoryPaths =
            SourceBackedRetrievalScope.GetAvailableCategoryPaths(intake);
        var unavailableScopes = availableCategoryPaths.Count == 0
            ? Array.Empty<string>()
            : distinctCategoryScopes
                .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                .Where(scope => !availableCategoryPaths.Contains(
                    scope,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
        if (unavailableScopes.Length > 0)
        {
            failureReason = "initial_research_scope_not_in_catalog:"
                            + string.Join("|", unavailableScopes);
            return false;
        }
        if (availableCategoryPaths.Count > 0
            && candidateScopePaths.Count > 0
            && !candidateScopePaths.Any(static path =>
                string.IsNullOrWhiteSpace(path)))
        {
            var allowedScopes = candidateScopePaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var disallowedScopes = distinctCategoryScopes
                .Where(scope => !allowedScopes.Contains(scope))
                .ToArray();
            if (disallowedScopes.Length > 0)
            {
                failureReason = "initial_research_scope_outside_llm_strategy:"
                                + string.Join("|", disallowedScopes);
                return false;
            }
        }
        if (string.Equals(
                candidatePoolRelation,
                SharedCandidatePoolRelation,
                StringComparison.Ordinal)
            && distinctCategoryScopes.Length > 1)
        {
            failureReason = "initial_research_shared_pool_multiple_scopes:"
                            + string.Join("|", distinctCategoryScopes);
            return false;
        }

        var normalizedCount = normalized.Count;
        normalized = CoalesceIdenticalInitialResearchRoutes(normalized);
        if (normalized.Count < normalizedCount)
        {
            mechanicalAdjustments.Add(
                "initial_research_batch_duplicate_routes_coalesced:"
                + normalizedCount
                + ">"
                + normalized.Count);
        }

        actions = normalized;
        var adjustmentSummary = string.Join(";", mechanicalAdjustments);
        failureReason = rejectedActions.Count == 0
            ? adjustmentSummary
            : normalized.Count == 0
                ? string.Join(";", rejectedActions)
                : string.Join(
                    ";",
                    new[]
                    {
                        "partial_invalid_actions:"
                        + string.Join(";", rejectedActions),
                        adjustmentSummary
                    }.Where(static value => value.Length > 0));
        return normalized.Count > 0;
    }

}
