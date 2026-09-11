using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CandidateLabelResolutionResult(
        IReadOnlyList<SourceBackedAgentCompletion> Completions,
        CandidateCollectionAuditDecision Decision);

    private async Task<CandidateLabelResolutionResult>
        CompleteCandidateLabelResolutionAsync(
            IReadOnlyList<EvidenceItem> candidates,
            CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return new CandidateLabelResolutionResult(
                Array.Empty<SourceBackedAgentCompletion>(),
                new CandidateCollectionAuditDecision(
                    true,
                    Array.Empty<CandidateCollectionApproval>(),
                    Array.Empty<string>(),
                    Array.Empty<CandidateCollectionClassification>(),
                    null));
        }

        var messages = BuildCandidateLabelResolutionMessages(candidates);
        var completions = new List<SourceBackedAgentCompletion>(2);
        CandidateCollectionAuditDecision? decision = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptMessages = messages.ToList();
            if (attempt > 1)
            {
                attemptMessages.Add(SourceBackedAgentMessage.User(
                    "REPARATION DU CONTRAT: recopie exactement un libelle autorise "
                    + "pour chaque cle cN, sans commentaire."));
            }

            var completion = _llm is ISourceBackedAgentStructuredLlmClient structuredLlm
                ? await structuredLlm.CompleteStructuredAsync(
                        attemptMessages,
                        BuildCandidateLabelResolutionContract(candidates),
                        ResolveCandidateLabelResolutionOutputTokens(candidates.Count),
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        attemptMessages,
                        BuildCandidateLabelResolutionTool(candidates),
                        ResolveCandidateLabelResolutionOutputTokens(candidates.Count),
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            completions.Add(completion);
            decision = ReadCandidateLabelResolutionDecision(
                completion,
                candidates);
            if (decision.ProtocolValid)
                break;
        }

        return new CandidateLabelResolutionResult(completions, decision!);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateLabelResolutionMessages(
            IReadOnlyList<EvidenceItem> candidates)
    {
        var prompt = new StringBuilder()
            .AppendLine("CANDIDATS ET LIBELLES SOURCES POSSIBLES:");
        for (var candidateIndex = 0;
             candidateIndex < candidates.Count;
             candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var options = BuildCandidateBatchDisplayValueOptions(candidate);
            prompt.Append(CandidateAuditKey(candidateIndex))
                .Append(" [")
                .Append(candidate.EvidenceId)
                .Append("] | options=");
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                if (optionIndex > 0)
                    prompt.Append(" ; ");
                prompt.Append(optionIndex + 1)
                    .Append("=\"")
                    .Append(TrimPromptValue(options[optionIndex], 100))
                    .Append('"');
            }
            if (candidate.SelectionHints.TryGetValue("kind", out var kind))
            {
                prompt.Append(" | structure=")
                    .Append(TrimPromptValue(kind, 32));
            }
            if (candidate.SelectionHints.TryGetValue(
                    "headingPath",
                    out var headingPath))
            {
                prompt.Append(" | chemin=")
                    .Append(TrimPromptValue(headingPath, 120));
            }
            prompt.Append(" | extrait=")
                .AppendLine(TrimPromptValue(candidate.Excerpt, 160));
        }
        prompt.AppendLine()
            .Append("DECISION: pour chaque cle cN, recopie exactement l'option "
                + "qui nomme le plus precisement l'element source complet.");
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu resous uniquement l'identite nommee de chaque element source; tu ne "
                + "decides PAS encore s'il convient a la demande finale. Lis les options "
                + "dans l'ordre. Commence toujours par l'option 1: si elle nomme deja un "
                + "element precis et autonome, choisis-la immediatement et ne monte jamais "
                + "vers une categorie plus large. Monte a l'option suivante seulement si "
                + "l'option courante est une etape, une quantite, un role ou un fragment; "
                + "arrete-toi au premier parent qui nomme le sujet complet de l'extrait. "
                + "Exemple structurel: options 'Modele AX-17' puis 'Equipements' implique "
                + "'Modele AX-17'; options 'Etape 2' puis 'Modele AX-17' puis "
                + "'Equipements' implique 'Modele AX-17'. Si l'element source "
                + "est reellement une rubrique ou une collection, choisis son libelle exact; "
                + "un juge semantique distinct decidera ensuite de son admissibilite. Ne "
                + "transforme pas une rubrique en instance et n'invente aucun nom. Parmi "
                + "plusieurs options exactes, choisis la plus proche et la plus precise. "
                + "Retourne uniquement l'objet JSON."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static LlmStructuredOutputContract
        BuildCandidateLabelResolutionContract(
            IReadOnlyList<EvidenceItem> candidates)
        => new(
            "source_backed_candidate_label_resolution_v3",
            BuildCandidateLabelResolutionSchema(candidates));

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildCandidateLabelResolutionTool(
            IReadOnlyList<EvidenceItem> candidates)
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                "submit_candidate_label_resolution",
                "Resout un libelle source exact pour chaque candidat.",
                BuildCandidateLabelResolutionSchema(candidates))
        };

    private static JsonElement BuildCandidateLabelResolutionSchema(
        IReadOnlyList<EvidenceItem> candidates)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            properties[CandidateAuditKey(index)] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] =
                    "Premiere option autonome et precise; monter au parent seulement pour reparer un fragment direct.",
                ["enum"] = BuildCandidateBatchDisplayValueOptions(
                    candidates[index]).ToArray()
            };
        }
        var keys = Enumerable.Range(0, candidates.Count)
            .Select(CandidateAuditKey)
            .ToArray();
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["resolvedLabelsByCandidateKey"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = properties,
                            ["required"] = keys,
                            ["additionalProperties"] = false
                        }
                },
                ["required"] = new[] { "resolvedLabelsByCandidateKey" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static CandidateCollectionAuditDecision
        ReadCandidateLabelResolutionDecision(
            SourceBackedAgentCompletion completion,
            IReadOnlyList<EvidenceItem> candidates)
    {
        JsonElement arguments;
        JsonDocument? document = null;
        if (completion.ToolCalls.Count == 1
            && string.Equals(
                completion.ToolCalls[0].Name,
                "submit_candidate_label_resolution",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments = completion.ToolCalls[0].Arguments;
        }
        else if (completion.ToolCalls.Count == 0
                 && TryParseParentReviewContent(completion.Content, out document))
        {
            arguments = document!.RootElement;
        }
        else
        {
            return InvalidCandidateBatchDecision(
                "candidate_label_resolution_structured_object_required");
        }

        try
        {
            if (!TryGetPropertyIgnoreCase(
                    arguments,
                    "resolvedLabelsByCandidateKey",
                    out var labels)
                || labels.ValueKind != JsonValueKind.Object)
            {
                return InvalidCandidateBatchDecision(
                    "candidate_label_resolution_labels_missing");
            }
            var expectedKeys = Enumerable.Range(0, candidates.Count)
                .Select(CandidateAuditKey)
                .ToHashSet(StringComparer.Ordinal);
            var returnedKeys = labels.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            if (returnedKeys.Length != expectedKeys.Count
                || returnedKeys.Any(key => !expectedKeys.Contains(key)))
            {
                return InvalidCandidateBatchDecision(
                    "candidate_label_resolution_key_set_mismatch");
            }

            var approvals = new List<CandidateCollectionApproval>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var options = BuildCandidateBatchDisplayValueOptions(candidate);
                if (!TryGetPropertyIgnoreCase(
                        labels,
                        CandidateAuditKey(index),
                        out var labelElement)
                    || labelElement.ValueKind != JsonValueKind.String)
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_label_resolution_label_invalid:"
                        + candidate.EvidenceId);
                }
                var label = labelElement.GetString() ?? string.Empty;
                if (!options.Contains(label, StringComparer.Ordinal))
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_label_resolution_label_invalid:"
                        + candidate.EvidenceId);
                }
                approvals.Add(new CandidateCollectionApproval(
                    candidate.EvidenceId,
                    label));
            }
            return new CandidateCollectionAuditDecision(
                true,
                approvals,
                Array.Empty<string>(),
                approvals.Select(static approval =>
                    new CandidateCollectionClassification(
                        approval.EvidenceId,
                        "source_label_resolved",
                        approval.DisplayValue))
                    .ToArray(),
                null);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static int ResolveCandidateLabelResolutionOutputTokens(
        int candidateCount)
        // Exact titles preserve semantic grounding better than numeric aliases.
        // Reserve enough generation room for real OCR labels without truncation.
        => Math.Clamp(96 + Math.Max(1, candidateCount) * 24, 192, 1280);

    private static IReadOnlyList<EvidenceItem> ApplyCandidateLabelResolutions(
        IReadOnlyList<EvidenceItem> candidates,
        IReadOnlyList<CandidateCollectionApproval> resolutions)
    {
        var labelByEvidenceId = resolutions.ToDictionary(
            static resolution => resolution.EvidenceId,
            static resolution => resolution.DisplayValue,
            StringComparer.OrdinalIgnoreCase);
        return candidates
            .Select(candidate =>
            {
                if (!labelByEvidenceId.TryGetValue(
                        candidate.EvidenceId,
                        out var label))
                {
                    return candidate;
                }
                var hints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAnchorLabel"] = label
                };
                return candidate with { SelectionHints = hints };
            })
            .ToArray();
    }
}
