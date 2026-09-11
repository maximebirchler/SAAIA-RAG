using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string EvidenceWorkspaceToolName = "manage_evidence_workspace";
    private const string EvidenceWorkspaceContinueStatus = "continue_research";
    private const string EvidenceWorkspaceReadyStatus = "ready_to_write";

    private sealed class LlmEvidenceWorkspace
    {
        private readonly List<string> _retainedEvidenceIds = new();
        private readonly HashSet<string> _retainedEvidenceIdSet =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _rejectedEvidenceIds = new();
        private readonly HashSet<string> _rejectedEvidenceIdSet =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _decisionNotes =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> RetainedEvidenceIds => _retainedEvidenceIds;
        public IReadOnlySet<string> RetainedEvidenceIdSet =>
            _retainedEvidenceIdSet;
        public IReadOnlyList<string> RejectedEvidenceIds => _rejectedEvidenceIds;
        public IReadOnlySet<string> RejectedEvidenceIdSet =>
            _rejectedEvidenceIdSet;
        public IReadOnlyDictionary<string, string> DecisionNotes =>
            _decisionNotes;

        public bool WouldChange(EvidenceWorkspaceUpdate update)
        {
            foreach (var evidenceId in update.RetainEvidenceIds)
            {
                if (!_retainedEvidenceIdSet.Contains(evidenceId)
                    || !_decisionNotes.TryGetValue(evidenceId, out var note)
                    || !string.Equals(
                        note,
                        update.Note,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var evidenceId in update.RejectEvidenceIds)
            {
                if (!_rejectedEvidenceIdSet.Contains(evidenceId)
                    || !_decisionNotes.TryGetValue(evidenceId, out var note)
                    || !string.Equals(
                        note,
                        update.Note,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public void Retain(string evidenceId, string note)
        {
            if (_retainedEvidenceIdSet.Add(evidenceId))
                _retainedEvidenceIds.Add(evidenceId);
            _decisionNotes[evidenceId] = note;
        }

        public void Reject(string evidenceId, string note)
        {
            if (_rejectedEvidenceIdSet.Add(evidenceId))
                _rejectedEvidenceIds.Add(evidenceId);
            if (_retainedEvidenceIdSet.Remove(evidenceId))
            {
                _retainedEvidenceIds.RemoveAll(id => string.Equals(
                    id,
                    evidenceId,
                    StringComparison.OrdinalIgnoreCase));
            }
            _decisionNotes[evidenceId] = note;
        }
    }

    private sealed record EvidenceWorkspaceUpdate(
        IReadOnlyList<string> RetainEvidenceIds,
        IReadOnlyList<string> RejectEvidenceIds,
        string Note,
        string Status);

    private sealed record EvidenceWorkspaceApplication(
        SourceBackedAgentToolCall[] DocumentaryToolCalls,
        bool CompletedUsefulToolCall,
        int NoOpWorkspaceCallCount,
        IReadOnlyList<string> ReadyEvidenceIds);

    private static bool CanUseWorkspaceWriterHandoff(
        bool requireEvidenceSelection,
        SemanticLayoutDimensions? dimensions,
        int? requiredEvidenceCount)
        => requireEvidenceSelection
           && requiredEvidenceCount is > 1;

    private static SourceBackedAgentToolDefinition BuildEvidenceWorkspaceTool(
        bool enableWriterHandoff,
        int? requiredFinalEvidenceCount,
        bool orderedByCanonicalLayout,
        IReadOnlyList<string>? allowedEvidenceIds)
        => new(
            EvidenceWorkspaceToolName,
            enableWriterHandoff
                ? "Transmets ta selection semantique finale au redacteur. Utilise uniquement status=ready_to_write et exactement le nombre demande d'EvidenceId visibles, dans l'ordre canonique indique. Si les preuves ne suffisent pas encore, n'appelle pas cet outil et poursuis avec un outil documentaire."
                : allowedEvidenceIds is { Count: > 0 }
                ? "Nomme les candidats visibles qui meritent l'audit semantique independant, et refuse ceux que tu sais inadaptes. Seuls les EvidenceId explicitement conserves seront audites; cet outil ne recherche rien."
                : "Conserve ou refuse explicitement des EvidenceId deja montres selon ton propre jugement semantique. Les preuves conservees survivent aux compactages; les preuves refusees ne seront plus proposees. Cet outil ne recherche rien. Il est facultatif et peut etre appele seul ou dans le meme tour que des outils documentaires.",
            BuildEvidenceWorkspaceToolParameters(
                enableWriterHandoff,
                requiredFinalEvidenceCount,
                orderedByCanonicalLayout,
                allowedEvidenceIds));

    private static JsonElement BuildEvidenceWorkspaceToolParameters(
        bool enableWriterHandoff,
        int? requiredFinalEvidenceCount,
        bool orderedByCanonicalLayout,
        IReadOnlyList<string>? allowedEvidenceIds)
    {
        var maximumWorkspaceItems = enableWriterHandoff
            ? requiredFinalEvidenceCount.GetValueOrDefault()
            : allowedEvidenceIds?.Count ?? 24;
        var evidenceIdItems = allowedEvidenceIds is { Count: > 0 }
            ? (object)new
            {
                type = "string",
                @enum = allowedEvidenceIds
            }
            : new
            {
                type = "string",
                pattern = "^E[0-9]{1,4}$"
            };
        var properties = new Dictionary<string, object?>
        {
            ["status"] = new
            {
                type = "string",
                description = enableWriterHandoff
                    ? "ready_to_write uniquement lorsque les EvidenceId fournis constituent la selection finale suffisante."
                    : "Etat de poursuite de la recherche.",
                @enum = enableWriterHandoff
                    ? new[] { EvidenceWorkspaceReadyStatus }
                    : new[] { EvidenceWorkspaceContinueStatus }
            },
            ["retainEvidenceIds"] = new
            {
                type = "array",
                description =
                    enableWriterHandoff && orderedByCanonicalLayout
                        ? "EvidenceId visibles a garder. Avec ready_to_write, fournis exactement "
                          + requiredFinalEvidenceCount.GetValueOrDefault()
                          + " identifiants dans l'ordre des cellules du layout canonique, ligne par ligne."
                        : "EvidenceId deja visibles et prometteurs a garder dans l'espace de travail.",
                items = evidenceIdItems,
                uniqueItems = true,
                minItems = enableWriterHandoff
                    ? requiredFinalEvidenceCount.GetValueOrDefault()
                    : 0,
                maxItems = enableWriterHandoff
                    ? requiredFinalEvidenceCount.GetValueOrDefault()
                    : maximumWorkspaceItems
            },
            ["note"] = new
            {
                type = "string",
                description =
                    "Motif semantique concis de cette decision, sans raisonnement detaille.",
                maxLength = 240
            }
        };
        if (!enableWriterHandoff)
        {
            properties["rejectEvidenceIds"] = new
            {
                type = "array",
                description =
                    "EvidenceId a memoriser comme refuses pour une recherche future.",
                items = evidenceIdItems,
                uniqueItems = true,
                maxItems = maximumWorkspaceItems
            };
        }

        var required = enableWriterHandoff
            ? new[] { "status", "retainEvidenceIds", "note" }
            : new[]
            {
                "status",
                "retainEvidenceIds",
                "rejectEvidenceIds",
                "note"
            };
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static bool TryReadEvidenceWorkspaceUpdate(
        JsonElement arguments,
        IReadOnlySet<string> observedEvidenceIds,
        EvidenceBundle bundle,
        LlmEvidenceWorkspace workspace,
        out EvidenceWorkspaceUpdate update,
        out string error)
    {
        update = new EvidenceWorkspaceUpdate(
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Empty,
            EvidenceWorkspaceContinueStatus);
        error = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "workspace_arguments_must_be_an_object";
            return false;
        }

        var status = ReadWorkspaceStatus(arguments);
        var retain = ReadEvidenceIdArray(arguments, "retainEvidenceIds");
        var reject = ReadEvidenceIdArray(arguments, "rejectEvidenceIds")
                     ?? (string.Equals(
                             status,
                             EvidenceWorkspaceReadyStatus,
                             StringComparison.Ordinal)
                         ? Array.Empty<string>()
                         : null);
        var note = ReadWorkspaceNote(arguments);
        if (retain is null || reject is null || note is null)
        {
            error = "workspace_contract_invalid";
            return false;
        }
        if (retain.Count == 0 && reject.Count == 0)
        {
            error = "workspace_update_is_empty";
            return false;
        }

        var overlap = retain
            .Intersect(reject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (overlap.Length > 0)
        {
            error = "workspace_ids_cannot_be_retained_and_rejected:"
                    + string.Join(",", overlap);
            return false;
        }

        var unknown = retain
            .Concat(reject)
            .Where(id =>
                (!observedEvidenceIds.Contains(id)
                 && !workspace.RetainedEvidenceIdSet.Contains(id)
                 && !workspace.RejectedEvidenceIdSet.Contains(id))
                || !bundle.ById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            error = "workspace_unknown_evidence_ids:" + string.Join(",", unknown);
            return false;
        }

        var nonCitableRetained = retain
            .Where(id => bundle.ById[id].RiskFlags.Contains(
                "orientation_only",
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (nonCitableRetained.Length > 0)
        {
            error = "workspace_cannot_retain_orientation_only_ids:"
                    + string.Join(",", nonCitableRetained);
            return false;
        }

        var previouslyRejected = retain
            .Where(workspace.RejectedEvidenceIdSet.Contains)
            .ToArray();
        if (previouslyRejected.Length > 0)
        {
            error = "workspace_cannot_retain_previously_rejected_ids:"
                    + string.Join(",", previouslyRejected);
            return false;
        }

        update = new EvidenceWorkspaceUpdate(retain, reject, note, status);
        return true;
    }

    private static IReadOnlyList<string>? ReadEvidenceIdArray(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return null;
            }
            values.Add(item.GetString()!.Trim());
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
    }

    private static string? ReadWorkspaceNote(JsonElement root)
    {
        if (!root.TryGetProperty("note", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        return TrimPromptValue(value.GetString(), 240);
    }

    private static string ReadWorkspaceStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return EvidenceWorkspaceContinueStatus;
        }

        return value.GetString()!.Trim().ToLowerInvariant();
    }

    private static string BuildEvidenceWorkspaceResult(
        EvidenceWorkspaceUpdate update,
        LlmEvidenceWorkspace workspace)
        => JsonSerializer.Serialize(
            new
            {
                ok = true,
                status = update.Status,
                retainedEvidenceIds = update.RetainEvidenceIds,
                rejectedEvidenceIds = update.RejectEvidenceIds,
                retainedTotal = workspace.RetainedEvidenceIds.Count,
                rejectedTotal = workspace.RejectedEvidenceIds.Count,
                nextAction = string.Equals(
                    update.Status,
                    EvidenceWorkspaceReadyStatus,
                    StringComparison.Ordinal)
                    ? "writer"
                    : "orchestrator"
            },
            ClientJson.CamelCase);

}
