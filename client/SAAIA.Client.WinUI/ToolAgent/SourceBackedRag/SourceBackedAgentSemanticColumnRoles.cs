using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticColumnRoleToolName = "submit_column_semantics";

    private sealed record SemanticColumnRoleOutcome(
        IReadOnlyDictionary<string, string> Roles,
        bool ProtocolValid,
        string FailureReason,
        int Attempts,
        int PromptTokens,
        int CompletionTokens);

    private sealed record SemanticColumnRolePreparation(
        SourceBackedIntake Intake,
        IReadOnlyDictionary<string, string> Roles,
        SemanticColumnRoleOutcome? Outcome);

    private async Task<SemanticColumnRolePreparation> PrepareSemanticColumnRolesAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        SemanticLayoutDimensions? dimensions,
        IReadOnlyDictionary<string, string> precomputedRoles,
        CancellationToken ct,
        bool allowPreObservationLlmReview = true)
    {
        var emptyRoles = (IReadOnlyDictionary<string, string>)
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var hasSemanticRoleBoundaries = precomputedRoles.Count > 0
            && precomputedRoles.Any(static pair => !string.Equals(
                pair.Key,
                pair.Value,
                StringComparison.OrdinalIgnoreCase));
        if (hasSemanticRoleBoundaries)
        {
            return new SemanticColumnRolePreparation(
                intake with { CanonicalColumnSemanticRoles = precomputedRoles },
                precomputedRoles,
                null);
        }
        if (!allowPreObservationLlmReview
            || !_options.SemanticColumnRoleReviewEnabled
            || dimensions is not { Columns: > 1 })
        {
            return new SemanticColumnRolePreparation(
                precomputedRoles.Count > 0
                    ? intake with
                    {
                        CanonicalColumnSemanticRoles = precomputedRoles
                    }
                    : intake,
                precomputedRoles.Count > 0 ? precomputedRoles : emptyRoles,
                null);
        }

        var outcome = await ReviewSemanticColumnRolesAsync(
                intake,
                semanticPlan,
                dimensions,
                precomputedRoles.Keys.ToArray(),
                ct)
            .ConfigureAwait(false);
        var effectiveRoles = outcome.Roles.Count > 0
            ? outcome.Roles
            : precomputedRoles;
        return new SemanticColumnRolePreparation(
            effectiveRoles.Count > 0
                ? intake with { CanonicalColumnSemanticRoles = effectiveRoles }
                : intake,
            effectiveRoles,
            outcome);
    }

    private void AddSemanticColumnRoleTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        SemanticColumnRoleOutcome? outcome)
    {
        if (outcome is null)
            return;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.semantic_column_roles.completed",
            ("protocol_valid", outcome.ProtocolValid),
            ("roles", outcome.Roles.Count),
            ("labels", outcome.Roles.Keys.ToArray()),
            ("role_boundaries", outcome.Roles
                .Select(static pair => pair.Key + "=" + pair.Value)
                .ToArray()),
            ("attempts", outcome.Attempts),
            ("prompt_tokens", outcome.PromptTokens),
            ("completion_tokens", outcome.CompletionTokens),
            ("failure_reason", outcome.FailureReason)));
    }

    private async Task<SemanticColumnRoleOutcome> ReviewSemanticColumnRolesAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        SemanticLayoutDimensions dimensions,
        IReadOnlyList<string> canonicalLabels,
        CancellationToken ct)
    {
        SourceBackedAgentCompletion? previousCompletion = null;
        var lastFailure = string.Empty;
        var promptTokens = 0;
        var completionTokens = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var completion = await _llm.CompleteAsync(
                    BuildSemanticColumnRoleMessages(
                        intake,
                        semanticPlan,
                        dimensions,
                        canonicalLabels,
                        attempt,
                        lastFailure,
                        previousCompletion),
                    new[]
                    {
                        BuildSemanticColumnRoleTool(dimensions.Columns)
                    },
                    _options.MaximumSemanticColumnRoleTokens,
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: true)
                .ConfigureAwait(false);
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();

            if (TryReadSemanticColumnRoles(
                    completion,
                    dimensions.Columns,
                    canonicalLabels,
                    out var roles,
                    out lastFailure))
            {
                return new SemanticColumnRoleOutcome(
                    roles,
                    ProtocolValid: true,
                    FailureReason: string.Empty,
                    Attempts: attempt,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens);
            }

            previousCompletion = completion;
        }

        return new SemanticColumnRoleOutcome(
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            ProtocolValid: false,
            FailureReason: lastFailure,
            Attempts: 2,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens);
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildSemanticColumnRoleMessages(
        SourceBackedIntake intake,
        string semanticPlan,
        SemanticLayoutDimensions dimensions,
        IReadOnlyList<string> canonicalLabels,
        int attempt,
        string lastFailure,
        SourceBackedAgentCompletion? previousCompletion)
    {
        var user = new StringBuilder();
        user.AppendLine("DEMANDE_ORIGINALE:");
        user.AppendLine(TrimPromptValue(intake.UserQuestion, 800));
        user.AppendLine("LANGUE: " + TrimPromptValue(intake.Language, 20));
        user.AppendLine("TYPE_DE_TACHE: " + TrimPromptValue(intake.TaskKind, 60));
        user.AppendLine(
            $"DISPOSITION: {dimensions.Rows} lignes et exactement {dimensions.Columns} colonnes.");
        if (canonicalLabels.Count > 0)
        {
            user.AppendLine(
                "COLONNES_CANONIQUES: " + string.Join(" | ", canonicalLabels));
        }
        user.AppendLine("PLAN_PREALABLE_DU_LLM:");
        user.AppendLine(TrimPromptValue(semanticPlan, 700));
        if (attempt > 1)
        {
            user.AppendLine("REPARATION_DU_PROTOCOLE:");
            user.AppendLine(
                "L'appel precedent est invalide (" + TrimPromptValue(lastFailure, 180) + ").");
            if (previousCompletion is not null)
            {
                user.AppendLine("SORTIE_PRECEDENTE:");
                user.AppendLine(TrimPromptValue(
                    RenderSemanticColumnRoleCompletion(previousCompletion),
                    500));
            }
        }
        user.AppendLine(
            $"Appelle {SemanticColumnRoleToolName} avec exactement {dimensions.Columns} colonnes.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu es l'interprete semantique du livrable avant toute selection de preuves.
                Le code ne choisit aucun sens: tu dois identifier les en-tetes visibles exacts
                des colonnes demandees et definir leur frontiere pratique les uns par rapport
                aux autres.

                Conserve les libelles explicites de la demande dans sa langue. Si un libelle
                n'est pas explicite, infere celui qui represente le plus fidelement la colonne
                distincte requise par le livrable. Chaque label doit etre l'en-tete concret
                d'une seule colonne. N'utilise jamais le nom abstrait de l'axe complet, une
                relation cle-valeur, un libelle de ligne ou une meta-categorie regroupant
                plusieurs colonnes.

                Pour chaque colonne, fournis separement une frontiere d'inclusion et une frontiere
                d'exclusion. L'inclusion decrit ce qui y appartient naturellement. L'exclusion
                nomme les classes de valeurs plausibles mais reservees aux autres colonnes, afin
                d'empecher les chevauchements faciles. Tiens compte du sens conventionnel des
                libelles, de la langue et du contexte culturel de la demande. Utilise de 3 a 24
                mots par frontiere.
                N'ajoute aucune preference personnelle ni contrainte absente de la demande.
                N'utilise aucun candidat ou document comme exemple et ne selectionne aucune valeur.

                Ne cherche et ne selectionne aucune preuve. Ne reponds pas a l'utilisateur.
                Appelle uniquement submit_column_semantics, sans texte ni autre outil.
                """),
            SourceBackedAgentMessage.User(user.ToString().Trim())
        };
    }

    private static SourceBackedAgentToolDefinition BuildSemanticColumnRoleTool(
        int expectedColumnCount)
        => new(
            SemanticColumnRoleToolName,
            "Soumet les en-tetes visibles et les frontieres semantiques des colonnes, "
            + "dans leur ordre final. Cet outil ne recherche et ne selectionne aucune preuve.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    columns = new
                    {
                        type = "array",
                        description =
                            "Colonnes visibles exactes, dans l'ordre final du livrable.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                label = new
                                {
                                    type = "string",
                                    minLength = 1,
                                    maxLength = 60
                                },
                                inclusion = new
                                {
                                    type = "string",
                                    minLength = 3,
                                    maxLength = 140
                                },
                                exclusion = new
                                {
                                    type = "string",
                                    minLength = 3,
                                    maxLength = 140
                                }
                            },
                            required = new[] { "label", "inclusion", "exclusion" },
                            additionalProperties = false
                        },
                        minItems = expectedColumnCount,
                        maxItems = expectedColumnCount
                    }
                },
                required = new[] { "columns" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static bool TryReadSemanticColumnRoles(
        SourceBackedAgentCompletion completion,
        int expectedColumnCount,
        IReadOnlyList<string> canonicalLabels,
        out IReadOnlyDictionary<string, string> roles,
        out string contractError)
    {
        roles = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        contractError = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SemanticColumnRoleToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            contractError = "column_semantics_single_tool_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(arguments, "columns", out var columns)
            || columns.ValueKind != JsonValueKind.Array)
        {
            contractError = "column_semantics_columns_missing";
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object)
            {
                contractError = "column_semantics_item_not_object";
                return false;
            }

            var label = GetString(column, "label")?.Trim();
            var inclusion = GetString(column, "inclusion")?.Trim();
            var exclusion = GetString(column, "exclusion")?.Trim();
            var inclusionWordCount = inclusion?.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length ?? 0;
            var exclusionWordCount = exclusion?.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length ?? 0;
            if (!IsValidLayoutLabel(label)
                || label!.Length > 60
                || string.IsNullOrWhiteSpace(inclusion)
                || inclusion!.Length > 140
                || inclusionWordCount is < 3 or > 24
                || string.IsNullOrWhiteSpace(exclusion)
                || exclusion!.Length > 140
                || exclusionWordCount is < 3 or > 24
                || !parsed.TryAdd(
                    label,
                    "IN_SCOPE: " + inclusion + " | OUT_OF_SCOPE: " + exclusion))
            {
                contractError = "column_semantics_item_invalid";
                return false;
            }
        }

        if (parsed.Count != expectedColumnCount)
        {
            contractError = "column_semantics_count_mismatch";
            return false;
        }
        if (canonicalLabels.Count > 0
            && !parsed.Keys.SequenceEqual(
                canonicalLabels,
                StringComparer.OrdinalIgnoreCase))
        {
            contractError = "column_semantics_labels_mismatch";
            return false;
        }

        roles = new ReadOnlyDictionary<string, string>(parsed);
        return true;
    }

    private static string RenderSemanticColumnRoleCompletion(
        SourceBackedAgentCompletion completion)
    {
        if (completion.ToolCalls.Count == 0)
            return completion.Content ?? string.Empty;
        return string.Join(
            " | ",
            completion.ToolCalls.Select(
                static call => call.Name + " " + call.Arguments.GetRawText()));
    }
}
