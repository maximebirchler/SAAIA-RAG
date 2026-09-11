using System.Diagnostics;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string FlatEvidenceSelectionToolName =
        "submit_flat_evidence_selection";
    private const string FlatEvidenceGapToolName =
        "submit_flat_evidence_gap";
    private const string FlatEvidenceContextGapToolName =
        "submit_flat_evidence_context_gap";
    private const string FlatEvidenceClarificationToolName =
        "submit_flat_evidence_clarification";

    private sealed record FlatEvidenceAdequacyOutcome(
        string Decision,
        string Reason,
        bool ProtocolValid,
        SourceBackedAgentCompletion? RoutedCompletion,
        SourceBackedAgentCompletion Completion,
        long ElapsedMilliseconds,
        IReadOnlyList<string>? TournamentSelectionEvidenceIds = null,
        int ReviewRounds = 1,
        int ReviewedCandidateCount = 0);

    private async Task<FlatEvidenceAdequacyOutcome>
        ReviewFlatEvidenceAdequacyRoundAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            EvidenceBundle bundle,
            IReadOnlyList<EvidenceItem> approvedItems,
            int provisionalTarget,
            int maximumSelectionItems,
            IReadOnlySet<string> incumbentEvidenceIds,
            string semanticFeedback,
            CancellationToken ct)
    {
        if (approvedItems.Count == 0)
        {
            var emptyCompletion = new SourceBackedAgentCompletion(
                string.Empty,
                Array.Empty<SourceBackedAgentToolCall>(),
                "no_evidence");
            return new FlatEvidenceAdequacyOutcome(
                "continue",
                "Aucune preuve approuvee n'est visible.",
                false,
                null,
                emptyCompletion,
                0);
        }

        var context = BuildFlatEvidenceAdequacyContext(
            intake,
            semanticPlan,
            approvedItems,
            provisionalTarget,
            incumbentEvidenceIds,
            semanticFeedback);

        var allowedEvidenceIds = approvedItems
            .Select(static item => item.EvidenceId)
            .ToArray();
        var selectionTool = new SourceBackedAgentToolDefinition(
            FlatEvidenceSelectionToolName,
            "Selectionne uniquement un sous-ensemble qui couvre completement toutes les composantes explicites de la demande originale. Une seule composante couverte ne suffit jamais si la demande en contient plusieurs.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    evidenceIds = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            @enum = allowedEvidenceIds
                        },
                        minItems = 1,
                        maxItems = Math.Min(
                            maximumSelectionItems,
                            allowedEvidenceIds.Length),
                        uniqueItems = true
                    },
                    reason = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 240
                    }
                },
                required = new[] { "evidenceIds", "reason" },
                additionalProperties = false
            }, ClientJson.CamelCase));
        var gapTool = new SourceBackedAgentToolDefinition(
            FlatEvidenceGapToolName,
            "Continue la recherche quand les preuves visibles ne couvrent pas encore toutes les composantes explicites de la demande originale. Conserve dans usefulEvidenceIds le meilleur pool partiel deja utile, puis nomme seulement les manques documentaires precis.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    usefulEvidenceIds = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            @enum = allowedEvidenceIds
                        },
                        minItems = 0,
                        maxItems = Math.Min(
                            maximumSelectionItems,
                            allowedEvidenceIds.Length),
                        uniqueItems = true
                    },
                    missingRequirements = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 160
                        },
                        minItems = 1,
                        maxItems = 6,
                        uniqueItems = true
                    },
                    reason = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 240
                    }
                },
                required = new[]
                {
                    "usefulEvidenceIds", "missingRequirements", "reason"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));
        var contextGapTool = new SourceBackedAgentToolDefinition(
            FlatEvidenceContextGapToolName,
            "Continue en lisant le contexte du meme item ou document qu'une preuve visible. Choisis semantiquement l'EvidenceId a approfondir. Utilise le gap global si les manques exigent un autre item ou document.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    evidenceId = new
                    {
                        type = "string",
                        @enum = allowedEvidenceIds
                    },
                    missingRequirements = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 160
                        },
                        minItems = 1,
                        maxItems = 6,
                        uniqueItems = true
                    },
                    reason = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 240
                    }
                },
                required = new[]
                {
                    "evidenceId", "missingRequirements", "reason"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));
        var clarificationTool = new SourceBackedAgentToolDefinition(
            FlatEvidenceClarificationToolName,
            "Demande une clarification uniquement lorsque plusieurs interpretations materielles restent possibles et que seul l'utilisateur peut trancher.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    understanding = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 300
                    },
                    options = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 180
                        },
                        minItems = 2,
                        maxItems = 4,
                        uniqueItems = true
                    },
                    executionImpact = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 240
                    },
                    ambiguityKind = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "scope", "constraints", "deliverable",
                            "source_strategy", "other"
                        }
                    },
                    reason = new
                    {
                        type = "string",
                        minLength = 2,
                        maxLength = 240
                    }
                },
                required = new[]
                {
                    "understanding", "options", "executionImpact",
                    "ambiguityKind", "reason"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));
        var messages = new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le juge d'adequation semantique. La demande originale prime sur "
                + "la cible provisoire du routeur. Utilise uniquement les preuves visibles. "
                + "Appelle exactement un seul des quatre outils proposes, sans autre texte."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
        var stopwatch = Stopwatch.StartNew();
        var completion = await _llm.CompleteAsync(
                messages,
                new[]
                {
                    selectionTool, contextGapTool, gapTool, clarificationTool
                },
                Math.Min(_options.MaximumActionTokens, 256),
                ct,
                temperatureOverride: 0,
                requireToolCall: true)
            .ConfigureAwait(false);
        stopwatch.Stop();
        var decisionCall = completion.ToolCalls.Count == 1
                           && completion.ToolCalls[0].Name is
                               FlatEvidenceSelectionToolName
                               or FlatEvidenceContextGapToolName
                               or FlatEvidenceGapToolName
                               or FlatEvidenceClarificationToolName
            ? completion.ToolCalls[0]
            : null;
        if (decisionCall is null
            || decisionCall.Arguments.ValueKind != JsonValueKind.Object)
        {
            return new FlatEvidenceAdequacyOutcome(
                "continue",
                "Le checkpoint n'a pas respecte son contrat.",
                false,
                null,
                completion,
                stopwatch.ElapsedMilliseconds);
        }

        var arguments = decisionCall.Arguments;
        var reason = GetString(arguments, "reason")?.Trim() ?? string.Empty;
        if (string.Equals(
                decisionCall.Name,
                FlatEvidenceSelectionToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            var evidenceIds = ReadFlatAdequacyStringArray(
                arguments,
                "evidenceIds",
                maximumSelectionItems);
            var allowedIds = allowedEvidenceIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var selectedIdsValid = evidenceIds.Length > 0
                                   && evidenceIds.Length <= maximumSelectionItems
                                   && evidenceIds.All(allowedIds.Contains)
                                   && evidenceIds.Distinct(
                                           StringComparer.OrdinalIgnoreCase)
                                       .Count() == evidenceIds.Length;
            if (!selectedIdsValid || reason.Length < 2)
            {
                return new FlatEvidenceAdequacyOutcome(
                    "continue",
                    "Le checkpoint de selection n'a pas respecte son contrat.",
                    false,
                    null,
                    completion,
                    stopwatch.ElapsedMilliseconds);
            }

            var routed = new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    new SourceBackedAgentToolCall(
                        "flat-adequacy-selection",
                        SemanticSelectionToolName,
                        JsonSerializer.SerializeToElement(new
                        {
                            evidenceIds
                        }, ClientJson.CamelCase))
                },
                "tool_calls",
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
                completion.ServerPredictedTokens,
                completion.ServerPredictedMilliseconds);
            return new FlatEvidenceAdequacyOutcome(
                "select",
                reason,
                true,
                routed,
                completion,
                stopwatch.ElapsedMilliseconds);
        }

        if (string.Equals(
                decisionCall.Name,
                FlatEvidenceContextGapToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            var evidenceId = GetString(arguments, "evidenceId")?.Trim()
                             ?? string.Empty;
            var contextMissingRequirements = ReadFlatAdequacyStringArray(
                arguments,
                "missingRequirements",
                6);
            var contextGapFieldsValid = allowedEvidenceIds.Contains(
                                            evidenceId,
                                            StringComparer.OrdinalIgnoreCase)
                                        && contextMissingRequirements.Length > 0
                                        && reason.Length >= 2;
            if (!contextGapFieldsValid
                || !TryBuildLeadContextAction(
                    bundle,
                    new[] { evidenceId },
                    out var contextCall))
            {
                return new FlatEvidenceAdequacyOutcome(
                    "continue",
                    "Le checkpoint de contexte n'a pas respecte son contrat.",
                    false,
                    null,
                    completion,
                    stopwatch.ElapsedMilliseconds);
            }

            var routed = new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    contextCall! with { Id = "flat-adequacy-context" }
                },
                "tool_calls",
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
                completion.ServerPredictedTokens,
                completion.ServerPredictedMilliseconds);
            return new FlatEvidenceAdequacyOutcome(
                "expand_context",
                reason,
                true,
                routed,
                completion,
                stopwatch.ElapsedMilliseconds);
        }

        if (string.Equals(
                decisionCall.Name,
                FlatEvidenceClarificationToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            var understanding = GetString(arguments, "understanding")?.Trim()
                                ?? string.Empty;
            var executionImpact = GetString(arguments, "executionImpact")?.Trim()
                                  ?? string.Empty;
            var ambiguityKind = GetString(arguments, "ambiguityKind")?.Trim()
                                ?? string.Empty;
            var options = ReadFlatAdequacyStringArray(arguments, "options", 4);
            var clarificationValid = understanding.Length is >= 2 and <= 300
                                     && executionImpact.Length is >= 2 and <= 240
                                     && options.Length is >= 2 and <= 4
                                     && ambiguityKind is
                                         "scope" or "constraints" or "deliverable"
                                         or "source_strategy" or "other"
                                     && reason.Length >= 2;
            if (!clarificationValid)
            {
                return new FlatEvidenceAdequacyOutcome(
                    "continue",
                    "Le checkpoint de clarification n'a pas respecte son contrat.",
                    false,
                    null,
                    completion,
                    stopwatch.ElapsedMilliseconds);
            }

            var routed = new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    new SourceBackedAgentToolCall(
                        "flat-adequacy-clarification",
                        RequestSourceBackedClarificationToolName,
                        JsonSerializer.SerializeToElement(new
                        {
                            understanding,
                            options,
                            executionImpact,
                            ambiguityKind
                        }, ClientJson.CamelCase))
                },
                "tool_calls",
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
                completion.ServerPredictedTokens,
                completion.ServerPredictedMilliseconds);
            return new FlatEvidenceAdequacyOutcome(
                "clarify",
                reason,
                true,
                routed,
                completion,
                stopwatch.ElapsedMilliseconds);
        }

        var missingRequirements = ReadFlatAdequacyStringArray(arguments, "missingRequirements", 6);
        var usefulEvidenceIds = ReadFlatAdequacyStringArray(
            arguments, "usefulEvidenceIds", maximumSelectionItems);
        var usefulEvidencePropertyValid = TryGetPropertyIgnoreCase(
            arguments, "usefulEvidenceIds", out var usefulEvidenceElement)
            && usefulEvidenceElement.ValueKind == JsonValueKind.Array;
        var allowedUsefulEvidenceIds = allowedEvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var continueValid = string.Equals(
                                decisionCall.Name,
                                FlatEvidenceGapToolName,
                                StringComparison.OrdinalIgnoreCase)
                            && usefulEvidencePropertyValid
                            && usefulEvidenceIds.All(allowedUsefulEvidenceIds.Contains)
                            && missingRequirements.Length > 0
                            && reason.Length >= 2;
        return new FlatEvidenceAdequacyOutcome(
            "continue",
            continueValid
                ? reason
                : "Le checkpoint de poursuite n'a pas respecte son contrat.",
            continueValid,
            null,
            completion,
            stopwatch.ElapsedMilliseconds,
            continueValid ? usefulEvidenceIds : null);
    }

    private static string[] ReadFlatAdequacyStringArray(
        JsonElement arguments,
        string propertyName,
        int maximumItems)
        => TryGetPropertyIgnoreCase(arguments, propertyName, out var element)
           && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim() ?? string.Empty)
                .Where(static value => value.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maximumItems))
                .ToArray()
            : Array.Empty<string>();
}
