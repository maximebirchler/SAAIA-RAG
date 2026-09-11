using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool TryReadStructuredSemanticPlan(
        SourceBackedAgentCompletion completion,
        out string plan,
        out string rowHeader,
        out IReadOnlyList<string> rowLabels,
        out IReadOnlyDictionary<string, string> columnRoles,
        out string contractError)
    {
        plan = string.Empty;
        rowHeader = string.Empty;
        rowLabels = Array.Empty<string>();
        columnRoles = EmptySemanticColumnRoles();
        contractError = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SemanticPlanToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            contractError = "semantic_plan_single_tool_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            contractError = "semantic_plan_dimensions_invalid";
            return false;
        }

        var planKind = NormalizeSemanticPlanLine(
            GetString(arguments, "planKind"));
        var structuredLayout = false;
        var rowCount = 1;
        var columnCount = 1;
        var atomicEvidenceCount = 1;
        switch (planKind)
        {
            case "single_item":
                break;
            case "multi_item":
                if (!TryGetInteger(
                        arguments,
                        "atomicEvidenceCount",
                        out atomicEvidenceCount))
                {
                    contractError =
                        "semantic_plan_multi_item_count_missing";
                    return false;
                }
                break;
            case "structured_layout":
                structuredLayout = true;
                if (!TryGetInteger(arguments, "rowCount", out rowCount)
                    || !TryGetInteger(
                        arguments,
                        "columnCount",
                        out columnCount)
                    || !TryGetInteger(
                        arguments,
                        "atomicEvidenceCount",
                        out atomicEvidenceCount))
                {
                    contractError =
                        "semantic_plan_structured_dimensions_missing";
                    return false;
                }
                break;
            case "":
                if (!TryGetBoolean(
                        arguments,
                        "structuredLayout",
                        out structuredLayout)
                    || !TryGetInteger(arguments, "rowCount", out rowCount)
                    || !TryGetInteger(
                        arguments,
                        "columnCount",
                        out columnCount)
                    || !TryGetInteger(
                        arguments,
                        "atomicEvidenceCount",
                        out atomicEvidenceCount))
                {
                    contractError = "semantic_plan_dimensions_invalid";
                    return false;
                }
                break;
            default:
                contractError = "semantic_plan_kind_invalid";
                return false;
        }

        if (rowCount is < 1 or > 80
            || columnCount is < 1 or > 40
            || atomicEvidenceCount is < 1 or > 80)
        {
            contractError = "semantic_plan_dimensions_invalid";
            return false;
        }

        if (structuredLayout)
        {
            if ((long)rowCount * columnCount != atomicEvidenceCount)
            {
                contractError = "semantic_plan_atomic_count_not_layout_product";
                return false;
            }
        }
        else if (rowCount != 1 || columnCount != 1)
        {
            contractError = "semantic_plan_unstructured_dimensions_must_be_one";
            return false;
        }

        // The native router deliberately preserves the user's exact requested
        // deliverable. It can therefore be longer than the compact text a
        // standalone planner would normally generate. Keep the bound finite,
        // but do not invalidate an otherwise exact semantic mission merely
        // because the original request exceeds the old 220-character prompt
        // optimization.
        if (!TryReadBoundedPlanString(arguments, "deliverable", 1000, out var deliverable)
            || !TryReadBoundedPlanString(
                arguments,
                "atomicEvidenceType",
                180,
                out var atomicEvidenceType))
        {
            contractError = "semantic_plan_text_fields_invalid";
            return false;
        }
        var atomicEvidenceMode = NormalizeSemanticPlanLine(
            GetString(arguments, "atomicEvidenceMode"));
        if (atomicEvidenceMode.Length == 0)
        {
            // Compatibility with router missions persisted before this semantic
            // distinction existed. New planner contracts require the field.
            atomicEvidenceMode = "named_item";
        }
        if (atomicEvidenceMode is not ("named_item" or "content_claim"))
        {
            contractError = "semantic_plan_atomic_evidence_mode_invalid";
            return false;
        }
        var selectionPolicy = NormalizeSemanticPlanLine(
            GetString(arguments, "selectionPolicy"));
        if (selectionPolicy.Length == 0)
        {
            // Compatibility with missions persisted before selectionPolicy.
            selectionPolicy = structuredLayout
                ? "structured_layout"
                : atomicEvidenceCount == 1
                    ? "single_item"
                    : "explicit_set";
        }
        var resolvedPlanKind = planKind.Length > 0
            ? planKind
            : structuredLayout
                ? "structured_layout"
                : atomicEvidenceCount == 1
                    ? "single_item"
                    : "multi_item";
        var selectionPolicyValid = selectionPolicy switch
        {
            "single_item" =>
                resolvedPlanKind == "single_item" && atomicEvidenceCount == 1,
            "explicit_set" =>
                resolvedPlanKind == "multi_item" && atomicEvidenceCount >= 2,
            "open_set" =>
                resolvedPlanKind == "multi_item"
                && atomicEvidenceCount is >= 2 and <= 3,
            "structured_layout" => resolvedPlanKind == "structured_layout",
            _ => false
        };
        if (!selectionPolicyValid)
        {
            contractError = "semantic_plan_selection_policy_invalid";
            return false;
        }
        var initialCapability = NormalizeSemanticPlanLine(
            GetString(arguments, "initialCapability"));
        if (initialCapability.Length > 40
            || (initialCapability.Length > 0
                && initialCapability is not (
                    "documents_content_cards"
                    or "documents_navigation"
                    or "rag_search"
                    or "documents_context")))
        {
            contractError = "semantic_plan_initial_capability_invalid";
            return false;
        }

        var compactUnstructuredPlan =
            planKind is "single_item" or "multi_item";
        if (compactUnstructuredPlan)
        {
            if (HasConflictingCompactSemanticFields(
                    arguments,
                    planKind))
            {
                contractError =
                    "semantic_plan_unstructured_axes_must_be_empty";
                return false;
            }
        }
        else if (!TryReadSemanticPlanAxes(
                     arguments,
                     structuredLayout,
                     rowCount,
                     columnCount,
                     out rowHeader,
                     out rowLabels,
                     out columnRoles,
                     out contractError))
        {
            return false;
        }
        var toolApproach = initialCapability.Length > 0
            ? "commencer par " + initialCapability
              + ", puis comparer librement toutes les capacites selon les observations"
            : "choisir librement la premiere capacite apres exposition des outils, "
              + "puis adapter la suite selon les observations";
        plan = string.Join(
            Environment.NewLine,
            "LIVRABLE: " + deliverable,
            "DIMENSIONS: " + (structuredLayout
                ? rowCount + " x " + columnCount
                : "aucune grille imposee"),
            "EN_TETE_LIGNES: " + (rowHeader.Length > 0 ? rowHeader : "aucun"),
            "LIGNES_VISIBLES: " + (rowLabels.Count > 0
                ? string.Join(" | ", rowLabels)
                : "aucune"),
            "PREUVES_ATOMIQUES: " + atomicEvidenceCount + " " + atomicEvidenceType,
            "MODE_PREUVES_ATOMIQUES: " + atomicEvidenceMode,
            "POLITIQUE_SELECTION: " + selectionPolicy,
            "INTENTIONS_RECHERCHE: a decider dynamiquement depuis la demande originale et les observations",
            "APPROCHE_OUTILS: " + toolApproach,
            "PREMIERE_ACTION: choix libre apres exposition des outils disponibles",
            "ACCEPTER_SI: toutes les preuves atomiques requises sont distinctes, utilisables et sourcees",
            "INSUFFISANT_SEULEMENT_SI: l'orchestrateur conclut apres exploration utile que les preuves necessaires restent introuvables");
        return true;
    }

    private static bool HasConflictingCompactSemanticFields(
        JsonElement arguments,
        string planKind)
    {
        if (TryGetBoolean(
                arguments,
                "structuredLayout",
                out var structuredLayout)
            && structuredLayout)
        {
            return true;
        }
        if (TryGetInteger(arguments, "rowCount", out var rowCount)
            && rowCount != 1)
        {
            return true;
        }
        if (TryGetInteger(arguments, "columnCount", out var columnCount)
            && columnCount != 1)
        {
            return true;
        }
        if (string.Equals(
                planKind,
                "single_item",
                StringComparison.Ordinal)
            && TryGetInteger(
                arguments,
                "atomicEvidenceCount",
                out var atomicEvidenceCount)
            && atomicEvidenceCount != 1)
        {
            return true;
        }
        if (NormalizeSemanticPlanLine(
                GetString(arguments, "rowHeader")).Length > 0)
        {
            return true;
        }

        foreach (var propertyName in new[] { "rowLabels", "columns" })
        {
            if (TryGetPropertyIgnoreCase(
                    arguments,
                    propertyName,
                    out var property)
                && (property.ValueKind != JsonValueKind.Array
                    || property.GetArrayLength() > 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBoundedPlanString(
        JsonElement arguments,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = NormalizeSemanticPlanLine(GetString(arguments, propertyName));
        return value.Length is > 1 && value.Length <= maximumLength;
    }

    private static bool TryGetBoolean(
        JsonElement arguments,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!TryGetPropertyIgnoreCase(arguments, propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetInteger(
        JsonElement arguments,
        string propertyName,
        out int value)
    {
        value = 0;
        return TryGetPropertyIgnoreCase(arguments, propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static string NormalizeSemanticPlanLine(string? value)
        => string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string RenderSemanticPlanCompletion(SourceBackedAgentCompletion completion)
    {
        if (completion.ToolCalls.Count == 0)
            return completion.Content ?? string.Empty;
        return string.Join(
            " | ",
            completion.ToolCalls.Select(
                static call => call.Name + " " + call.Arguments.GetRawText()));
    }

    private static string BuildUnavailableSemanticPlan()
        => string.Join(
            Environment.NewLine,
            "LIVRABLE: reponse sourcee conforme a la demande originale",
            "DIMENSIONS: aucune grille imposee",
            "PREUVES_ATOMIQUES: 1 instance complete demandee",
            "MODE_PREUVES_ATOMIQUES: named_item",
            "INTENTIONS_RECHERCHE: contenu explicitement demande",
            "APPROCHE_OUTILS: choisir librement les outils selon les observations",
            "PREMIERE_ACTION: choix libre apres exposition des outils disponibles",
            "ACCEPTER_SI: les affirmations concretes sont prouvees et citees",
            "INSUFFISANT_SEULEMENT_SI: aucune preuve utile ne peut etre obtenue");
}
