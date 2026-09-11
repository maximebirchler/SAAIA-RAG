using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticSelectionToolName = "submit_evidence_selection";
    private sealed record SemanticSelectionLayout(
        string RowHeader,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> RowLabels);
    private sealed record SemanticLayoutDimensions(int Rows, int Columns);
    private sealed record DuplicateVisibleSourceSelection(
        string KeptEvidenceId,
        string DuplicateEvidenceId,
        string SourceLabel);
    private sealed record DuplicateDisplayValueSelection(
        string KeptEvidenceId,
        string DuplicateEvidenceId,
        string DisplayValue);

    private static SourceBackedAgentToolDefinition BuildSemanticSelectionTool(
        int requiredCount,
        SemanticLayoutDimensions? dimensions,
        IReadOnlyList<string>? allowedEvidenceIds,
        IReadOnlyList<string>? canonicalColumnLabels,
        string? canonicalRowHeader = null,
        IReadOnlyList<string>? canonicalRowLabels = null,
        int? minimumCount = null,
        bool provisionalFlatTarget = false)
    {
        var effectiveMinimumCount = Math.Clamp(
            minimumCount ?? requiredCount,
            1,
            requiredCount);
        var hasCanonicalLayout =
            dimensions is not null
            && !string.IsNullOrWhiteSpace(canonicalRowHeader)
            && canonicalColumnLabels is { Count: > 0 }
            && canonicalRowLabels is { Count: > 0 };
        var requiresLayout = !hasCanonicalLayout
                             && (dimensions is not null
                                 || canonicalColumnLabels is { Count: > 0 }
                                 || canonicalRowLabels is { Count: > 0 });
        var hasStructuredOrder = dimensions is not null;
        var properties = new Dictionary<string, object?>
        {
            ["evidenceIds"] = new
            {
                type = "array",
                description = hasStructuredOrder
                    ? "Selection finale ordonnee ligne par ligne, sans aucun doublon."
                    : "Preuves finales retenues semantiquement pour la redaction, sans doublon.",
                items = allowedEvidenceIds is { Count: > 0 }
                    ? (object)new
                    {
                        type = "string",
                        @enum = allowedEvidenceIds
                    }
                    : new
                    {
                        type = "string",
                        pattern = "^E[1-9][0-9]*$"
                    },
                minItems = effectiveMinimumCount,
                maxItems = requiredCount,
                uniqueItems = true
            }
        };
        if (requiresLayout)
        {
            properties["layout"] = new
            {
                type = "object",
                description = "Libelles de la disposition finale choisie par le LLM.",
                properties = new
                {
                    rowHeader = !string.IsNullOrWhiteSpace(canonicalRowHeader)
                        ? (object)new
                        {
                            type = "string",
                            @enum = new[] { canonicalRowHeader }
                        }
                        : new
                        {
                            type = "string",
                            maxLength = 60
                        },
                    columns = new
                    {
                        type = "array",
                        description = canonicalColumnLabels is { Count: > 0 }
                            ? "En-tetes exacts deja decides par le LLM, dans le meme ordre."
                            : "En-tetes visibles des colonnes, dans l'ordre final.",
                        items = canonicalColumnLabels is { Count: > 0 }
                            ? (object)new
                            {
                                type = "string",
                                @enum = canonicalColumnLabels
                            }
                            : new
                            {
                                type = "string",
                                maxLength = 60
                            },
                        minItems = dimensions?.Columns ?? 1,
                        maxItems = dimensions?.Columns ?? 12,
                        uniqueItems = true
                    },
                    rows = new
                    {
                        type = "array",
                        description = canonicalRowLabels is { Count: > 0 }
                            ? "Libelles exacts des lignes deja decides par le LLM, dans le meme ordre."
                            : "Libelles visibles des lignes, dans l'ordre final.",
                        items = canonicalRowLabels is { Count: > 0 }
                            ? (object)new
                            {
                                type = "string",
                                @enum = canonicalRowLabels
                            }
                            : new
                            {
                                type = "string",
                                maxLength = 80
                            },
                        minItems = dimensions?.Rows ?? 1,
                        maxItems = dimensions?.Rows ?? 80,
                        uniqueItems = true
                    }
                },
                required = new[] { "rowHeader", "columns", "rows" },
                additionalProperties = false
            };
        }

        return new SourceBackedAgentToolDefinition(
            SemanticSelectionToolName,
            (provisionalFlatTarget
                ? $"Soumet la selection semantique finale de {effectiveMinimumCount} a {requiredCount} preuves atomiques pour une cible plate provisoire. Le nombre retenu est ta decision semantique selon la demande et les preuves auditees. "
                : $"Soumet la selection semantique finale de exactement {requiredCount} preuves atomiques. ")
            + "Appelle cet outil seulement quand le livrable peut etre redige; il ne recherche rien. "
            + (hasStructuredOrder
                ? "L'ordre des EvidenceId est l'ordre des cellules, ligne par ligne. "
                : string.Empty)
            + "Cet appel doit etre la seule action du tour.",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = requiresLayout
                    ? new[] { "evidenceIds", "layout" }
                    : new[] { "evidenceIds" },
                ["additionalProperties"] = false
            }, ClientJson.CamelCase));
    }

    private static bool TryReadSemanticSelection(
        JsonElement arguments,
        IReadOnlyList<string>? canonicalColumnLabels,
        string? canonicalRowHeader,
        IReadOnlyList<string>? canonicalRowLabels,
        out IReadOnlyList<string> evidenceIds,
        out SemanticSelectionLayout? layout,
        out string contractError)
    {
        evidenceIds = Array.Empty<string>();
        layout = null;
        contractError = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            contractError = "selection_arguments_not_object";
            return false;
        }

        if (!TryReadLayoutEvidenceIds(arguments, out evidenceIds)
            || evidenceIds.Count == 0)
        {
            contractError = "selection_evidence_ids_invalid";
            return false;
        }
        if (evidenceIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != evidenceIds.Count)
        {
            var duplicates = evidenceIds
                .GroupBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToArray();
            contractError = "selection_evidence_ids_not_unique:"
                            + string.Join(",", duplicates);
            return false;
        }

        var hasCanonicalLayout =
            !string.IsNullOrWhiteSpace(canonicalRowHeader)
            && canonicalColumnLabels is { Count: > 0 }
            && canonicalRowLabels is { Count: > 0 };
        var hasDeclaredLayout =
            TryGetPropertyIgnoreCase(arguments, "layout", out _);
        if (hasCanonicalLayout && !hasDeclaredLayout)
        {
            layout = new SemanticSelectionLayout(
                canonicalRowHeader!,
                canonicalColumnLabels!,
                canonicalRowLabels!);
            return true;
        }

        var requiresLayout = !hasCanonicalLayout
                             && (!string.IsNullOrWhiteSpace(canonicalRowHeader)
                                 || canonicalColumnLabels is { Count: > 0 }
                                 || canonicalRowLabels is { Count: > 0 });
        if (!requiresLayout && !hasDeclaredLayout)
        {
            return true;
        }

        if (!TryReadSemanticSelectionLayout(
                arguments,
                out layout,
                out var layoutContractError))
        {
            contractError = layoutContractError;
            return false;
        }

        var parsedLayout = layout!;
        if (!string.IsNullOrWhiteSpace(canonicalRowHeader)
            && !string.Equals(
                parsedLayout.RowHeader,
                canonicalRowHeader,
                StringComparison.OrdinalIgnoreCase))
        {
            layout = null;
            contractError = "selection_layout_row_header_not_canonical";
            return false;
        }
        if (canonicalColumnLabels is { Count: > 0 }
            && !parsedLayout.Columns.SequenceEqual(
                canonicalColumnLabels,
                StringComparer.OrdinalIgnoreCase))
        {
            layout = null;
            contractError = "selection_layout_columns_not_canonical";
            return false;
        }
        if (canonicalRowLabels is { Count: > 0 }
            && !parsedLayout.RowLabels.SequenceEqual(
                canonicalRowLabels,
                StringComparer.OrdinalIgnoreCase))
        {
            layout = null;
            contractError = "selection_layout_rows_not_canonical";
            return false;
        }
        if (parsedLayout.RowLabels.Count * parsedLayout.Columns.Count
            != evidenceIds.Count)
        {
            layout = null;
            contractError = "selection_layout_cell_count_mismatch";
            return false;
        }

        return true;
    }

}
