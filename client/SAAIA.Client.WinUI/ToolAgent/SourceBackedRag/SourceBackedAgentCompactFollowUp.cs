using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitResearchActionToolName =
        "submit_research_action";

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildCompactSingleFollowUpTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> _,
            bool allowYieldResolution)
    {
        var tools = new List<SourceBackedAgentToolDefinition>
        {
            new SourceBackedAgentToolDefinition(
                SubmitResearchActionToolName,
                "Choisis une seule prochaine action documentaire.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        capability = new
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
                        query = CompactFollowUpString(0, 180),
                        scope = CompactFollowUpString(0, 120),
                        document = CompactFollowUpString(0, 220),
                        anchor = CompactFollowUpString(0, 160),
                        navigationKind = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                string.Empty,
                                "navigation_entry",
                                "title_anchor"
                            }
                        },
                        inventoryMode = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                string.Empty,
                                "ordered",
                                "representative"
                            },
                            description =
                                "Pour documents_content_cards sans query: representative explore des positions reparties; ordered suit l'ordre des sources."
                        },
                        pageStart = new
                        {
                            type = "integer",
                            minimum = 1
                        },
                        pageEnd = new
                        {
                            type = "integer",
                            minimum = 1
                        },
                        limit = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 40
                        },
                        offset = new
                        {
                            type = "integer",
                            minimum = 0,
                            maximum = 10000
                        },
                        mode = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "auto", "focused", "balanced", "broad"
                            }
                        }
                    },
                    required = new[]
                    {
                        "capability",
                        "query",
                        "scope",
                        "document",
                        "anchor",
                        "navigationKind",
                        "limit",
                        "offset"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase))
        };
        if (allowYieldResolution)
        {
            tools.Add(BuildSourceBackedClarificationTool());
            tools.Add(BuildSourceInsufficiencyTool());
        }

        return tools;
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildSemanticYieldTerminalDecisionTools()
        => new[] { BuildSemanticYieldTerminalDecisionTool() };

    private static object CompactFollowUpString(
        int minimumLength,
        int maximumLength,
        string? description = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "string",
            ["minLength"] = minimumLength,
            ["maxLength"] = maximumLength
        };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return schema;
    }

    private static bool TryValidateCompactResearchActionSubmission(
        SourceBackedAgentCompletion completion,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SubmitResearchActionToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "compact_research_action_single_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            failureReason = "compact_research_action_object_required";
            return false;
        }

        var invalidFields = new List<string>();
        var capability = ReadCompactFollowUpString(arguments, "capability");
        if (capability is not (
                "rag_search"
                or "documents_navigation"
                or "documents_content_cards"
                or "documents_context"))
        {
            invalidFields.Add("capability");
        }

        foreach (var field in new[]
                 {
                     (Name: "query", MaximumLength: 180),
                     (Name: "scope", MaximumLength: 120),
                     (Name: "document", MaximumLength: 220),
                     (Name: "anchor", MaximumLength: 160),
                     (Name: "navigationKind", MaximumLength: 32)
                 })
        {
            if (!TryGetPropertyIgnoreCase(
                    arguments,
                    field.Name,
                    out var value)
                || value.ValueKind != JsonValueKind.String
                || (value.GetString()?.Length ?? 0) > field.MaximumLength)
            {
                invalidFields.Add(field.Name);
            }
        }

        var navigationKind = ReadCompactFollowUpString(
            arguments,
            "navigationKind");
        if (navigationKind is not ("" or "navigation_entry" or "title_anchor"))
            invalidFields.Add("navigationKind");

        if (!TryGetPropertyIgnoreCase(arguments, "limit", out var limit)
            || limit.ValueKind != JsonValueKind.Number
            || !limit.TryGetInt32(out var limitValue)
            || limitValue is < 1 or > 40)
        {
            invalidFields.Add("limit");
        }
        if (!TryGetPropertyIgnoreCase(arguments, "offset", out var offset)
            || offset.ValueKind != JsonValueKind.Number
            || !offset.TryGetInt32(out var offsetValue)
            || offsetValue is < 0 or > 10000)
        {
            invalidFields.Add("offset");
        }
        if (TryGetPropertyIgnoreCase(arguments, "mode", out var mode)
            && (mode.ValueKind != JsonValueKind.String
                || mode.GetString() is not (
                    "auto" or "focused" or "balanced" or "broad")))
        {
            invalidFields.Add("mode");
        }
        if (TryGetPropertyIgnoreCase(
                arguments,
                "inventoryMode",
                out var inventoryMode)
            && (inventoryMode.ValueKind != JsonValueKind.String
                || inventoryMode.GetString() is not (
                    "" or "ordered" or "representative")))
        {
            invalidFields.Add("inventoryMode");
        }

        if (invalidFields.Count == 0)
            return true;

        failureReason =
            "compact_research_action_fields_missing_or_invalid:"
            + string.Join(",", invalidFields.Distinct(StringComparer.Ordinal));
        return false;
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCompactSingleFollowUpMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            EvidenceBundle bundle,
            IReadOnlyList<string> observedEvidenceIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds,
            IReadOnlyList<string> leadEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            string? semanticReviewFeedback)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 600));
        AppendQuestionFocusContext(context, intake);

        var atomicEvidenceExpectation = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "PREUVES_ATOMIQUES:",
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(atomicEvidenceExpectation))
        {
            context.Append("PREUVE_ATTENDUE: ")
                .AppendLine(TrimPromptValue(
                    atomicEvidenceExpectation[
                        (atomicEvidenceExpectation.IndexOf(':') + 1)..]
                        .Trim(),
                    180));
        }

        if (!string.IsNullOrWhiteSpace(semanticReviewFeedback))
        {
            context.Append("MANQUE_SELON_LE_JUGE: ")
                .AppendLine(TrimPromptValue(
                    semanticReviewFeedback,
                    220));
        }

        context.AppendLine("MEILLEURE_PISTE_A_APPROFONDIR:");
        foreach (var item in leadEvidenceIds
                     .Select(id => bundle.ById.TryGetValue(id, out var evidence)
                         ? evidence
                         : null)
                     .Where(static item => item is not null)
                     .Cast<EvidenceItem>())
        {
            AppendCompactEvidencePointer(context, item);
        }

        context.AppendLine("RECHERCHES_DEJA_EFFECTUEES:");
        foreach (var request in executedRequests.TakeLast(6))
        {
            context.Append("- ")
                .Append(request.ToolName)
                .Append(" | ")
                .Append(TrimPromptValue(request.Query, 150));
            if (!string.IsNullOrWhiteSpace(request.CategoryPath))
            {
                context.Append(" | categorie=")
                    .Append(TrimPromptValue(request.CategoryPath, 80));
            }
            if (!string.IsNullOrWhiteSpace(
                    request.DocPath ?? request.DocRef ?? request.DocId))
            {
                context.Append(" | document=")
                    .Append(TrimPromptValue(
                        request.DocPath ?? request.DocRef ?? request.DocId,
                        100));
            }
            if (request.PageStart is not null)
            {
                context.Append(" | page=")
                    .Append(request.PageStart.Value.ToString(
                        CultureInfo.InvariantCulture));
            }
            if (request.Limit is > 0)
                context.Append(" | limite=").Append(request.Limit.Value);
            if (request.Offset is >= 0)
                context.Append(" | offset=").Append(request.Offset.Value);
            if (request.NewEvidenceCount is >= 0)
            {
                context.Append(" | nouvelles_preuves=")
                    .Append(request.NewEvidenceCount.Value);
                if (request.NewEvidenceCount == 0)
                    context.Append(" | rendement=nul");
            }
            if (request.SemanticAuditApprovedCount is not null
                || request.SemanticAuditRejectedCount is not null)
            {
                context.Append(" | audit_llm_approuvees=")
                    .Append(request.SemanticAuditApprovedCount.GetValueOrDefault())
                    .Append(" | audit_llm_refusees=")
                    .Append(request.SemanticAuditRejectedCount.GetValueOrDefault());
            }
            if (request.NextOffset is >= 0)
            {
                context.Append(" | prochain_offset_exact=")
                    .Append(request.NextOffset.Value);
            }
            context.AppendLine();
        }

        context.AppendLine("PREUVES_REFUSEES_PAR_LE_JUGE:");
        foreach (var item in observedEvidenceIds
                     .Where(semanticallyRejectedEvidenceIds.Contains)
                     .Select(id => bundle.ById.TryGetValue(id, out var evidence)
                         ? evidence
                         : null)
                     .Where(static item => item is not null)
                     .Cast<EvidenceItem>()
                     .DistinctBy(
                         static item => item.VisibleSourceKey,
                         StringComparer.OrdinalIgnoreCase)
                     .TakeLast(8))
        {
            AppendCompactEvidencePointer(context, item);
        }

        context.AppendLine("POINTEURS_D_ORIENTATION_DISPONIBLES:");
        foreach (var item in observedEvidenceIds
                     .Select(id => bundle.ById.TryGetValue(id, out var evidence)
                         ? evidence
                         : null)
                     .Where(static item => item is not null
                         && item.RiskFlags.Contains(
                             "orientation_only",
                             StringComparer.OrdinalIgnoreCase))
                     .Cast<EvidenceItem>()
                     .DistinctBy(
                         static item => item.VisibleSourceKey,
                         StringComparer.OrdinalIgnoreCase)
                     .TakeLast(10))
        {
            context.Append("- ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 100))
                .Append(" | ")
                .Append(TrimPromptValue(
                    item.DocName ?? item.DocPath ?? item.DocId,
                    100));
            if (item.PageStart is not null)
            {
                context.Append(" page ")
                        .Append(item.PageStart.Value.ToString(
                            CultureInfo.InvariantCulture));
            }
            context.AppendLine();
        }

        return new[]
        {
            SourceBackedAgentMessage.System("""
                Tu pilotes la recherche documentaire. Le juge LLM a refuse le
                dernier lot. Choisis exactement un nouvel outil utile pour obtenir
                la preuve complete attendue. Ne reponds pas et ne selectionne pas
                une preuve refusee. Ne repete pas une recherche deja effectuee.
                Une paraphrase qui permute les memes termes reste la meme recherche.
                Une piste pertinente mais partielle peut etre approfondie avec
                documents_context en recopiant son document, sa page ou son ancre.
                La navigation retourne des pointeurs non citables: apres avoir choisi
                un pointeur, transforme-le en preuve avec une lecture ciblee. Les cartes
                canoniques sont des candidats citables; un audit semantique les jugera
                apres cette action. Pour decouvrir beaucoup de noms inconnus sans terme
                source fiable, tu peux choisir un inventaire de cartes avec query vide.
                Utilise des termes susceptibles d'apparaitre dans la source; si la
                recherche lexicale est saturee, change librement de capacite. Le
                champ document accepte uniquement un identifiant exact copie de
                la demande ou d'un pointeur affiche; laisse-le vide pour une
                recherche dans le corpus. N'y place jamais un sujet, une requete
                ou un nom de document imagine.
                """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSemanticYieldTerminalDecisionMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            IReadOnlyList<RetrievalRequest> executedRequests,
            string? semanticReviewFeedback,
            int continuationCount)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 500));
        var atomicEvidenceExpectation = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "PREUVES_ATOMIQUES:",
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(atomicEvidenceExpectation))
        {
            context.Append("PREUVE_ATTENDUE: ")
                .AppendLine(TrimPromptValue(
                    atomicEvidenceExpectation[
                        (atomicEvidenceExpectation.IndexOf(':') + 1)..]
                        .Trim(),
                    160));
        }

        var auditApproved = executedRequests.Sum(static request =>
            request.SemanticAuditApprovedCount.GetValueOrDefault());
        var auditRejected = executedRequests.Sum(static request =>
            request.SemanticAuditRejectedCount.GetValueOrDefault());
        context.Append("AUDIT_LLM: approuvees=")
            .Append(auditApproved.ToString(CultureInfo.InvariantCulture))
            .Append(" | refusees=")
            .AppendLine(auditRejected.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(semanticReviewFeedback))
        {
            context.Append("MANQUE_IDENTIFIE: ")
                .AppendLine(TrimPromptValue(semanticReviewFeedback, 180));
        }

        context.AppendLine("DERNIERES_ROUTES:");
        foreach (var request in executedRequests.TakeLast(4))
        {
            context.Append("- ")
                .Append(request.ToolName)
                .Append(" | ")
                .Append(TrimPromptValue(request.Query, 100))
                .Append(" | nouvelles_preuves=")
                .Append(request.NewEvidenceCount.GetValueOrDefault()
                    .ToString(CultureInfo.InvariantCulture))
                .Append(" | audit_approuvees=")
                .Append(request.SemanticAuditApprovedCount.GetValueOrDefault()
                    .ToString(CultureInfo.InvariantCulture))
                .Append(" | audit_refusees=")
                .AppendLine(request.SemanticAuditRejectedCount.GetValueOrDefault()
                    .ToString(CultureInfo.InvariantCulture));
        }
        context.AppendLine(BuildSemanticYieldTerminalDecisionMessage(
            continuationCount));

        return new[]
        {
            SourceBackedAgentMessage.System("""
                Tu rends une decision terminale de rendement documentaire. Appelle
                exactement un des outils exposes. Choisis la clarification seulement
                si une ambiguite materielle exige réellement le choix de l'utilisateur;
                sinon declare l'insuffisance precise des sources observees. N'appelle
                aucun outil documentaire, ne reponds pas en texte libre et n'invente rien.
                Pour une insuffisance, indique seulement quelle preuve n'a pas ete
                obtenue dans les recherches visibles. N'affirme pas qu'un element est
                absent du document ou du corpus et n'ajoute ni cause ni connaissance
                generale externe.
                """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static void AppendCompactEvidencePointer(
        StringBuilder context,
        EvidenceItem item)
    {
        context.Append("- [")
            .Append(item.EvidenceId)
            .Append("] ")
            .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 100))
            .Append(" | document=")
            .Append(TrimPromptValue(
                item.DocPath ?? item.DocName ?? item.DocId,
                120));
        if (item.PageStart is not null)
        {
            context.Append(" | page=")
                .Append(item.PageStart.Value.ToString(
                    CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(item.ChunkId))
        {
            context.Append(" | anchor=")
                .Append(TrimPromptValue(item.ChunkId, 80));
        }
        context.AppendLine();
    }

}
