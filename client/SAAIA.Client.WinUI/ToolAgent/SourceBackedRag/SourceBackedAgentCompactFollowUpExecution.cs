using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private SourceBackedAgentCompletion ApplyCompactSingleFollowUpAdjustment(
        SourceBackedAgentCompletion completion,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        completion = NormalizeCompactSingleFollowUpCompletion(
            completion,
            intake,
            bundle,
            out var adjustment);
        if (adjustment.Length > 0)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.compact_follow_up.mechanical_adjustment",
                ("turn", turn),
                ("adjustment", adjustment)));
        }

        return completion;
    }

    private static SourceBackedAgentCompletion
        NormalizeCompactSingleFollowUpCompletion(
            SourceBackedAgentCompletion completion,
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            out string mechanicalAdjustment)
    {
        mechanicalAdjustment = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SubmitResearchActionToolName,
                StringComparison.OrdinalIgnoreCase)
            || completion.ToolCalls[0].Arguments.ValueKind
            != JsonValueKind.Object)
        {
            return completion;
        }

        var call = completion.ToolCalls[0];
        if (!TryNormalizeResearchAction(
                call.Id,
                call.Arguments,
                intake,
                bundle,
                out var normalizedCall,
                out mechanicalAdjustment))
        {
            return completion;
        }

        return completion with
        {
            ToolCalls = new[] { normalizedCall }
        };
    }

    private static bool TryNormalizeResearchAction(
        string callId,
        JsonElement submittedArguments,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        out SourceBackedAgentToolCall normalizedCall,
        out string mechanicalAdjustment)
    {
        normalizedCall = default!;
        mechanicalAdjustment = string.Empty;
        var capability = ReadCompactFollowUpString(
            submittedArguments,
            "capability");
        var query = ReadCompactFollowUpString(
            submittedArguments,
            "query");
        var scope = ReadCompactFollowUpString(
            submittedArguments,
            "scope");
        var submittedDocument = ReadCompactFollowUpString(
            submittedArguments,
            "document");
        var document = ResolveGroundedCompactDocumentReference(
            submittedDocument,
            intake,
            bundle);
        if (submittedDocument.Length > 0
            && document.Length == 0)
        {
            mechanicalAdjustment =
                "ungrounded_document_reference_removed:"
                + submittedDocument;
        }
        var anchor = ReadCompactFollowUpString(
            submittedArguments,
            "anchor");
        var mode = ReadCompactFollowUpString(
            submittedArguments,
            "mode");
        var inventoryMode = ReadCompactFollowUpString(
            submittedArguments,
            "inventoryMode");
        var navigationKind = ReadCompactFollowUpString(
            submittedArguments,
            "navigationKind");
        var limit = ReadCompactFollowUpInteger(
            submittedArguments,
            "limit",
            8);
        var offset = ReadCompactFollowUpInteger(
            submittedArguments,
            "offset",
            0);
        var pageStart = ReadCompactFollowUpNullableInteger(
            submittedArguments,
            "pageStart");
        var pageEnd = ReadCompactFollowUpNullableInteger(
            submittedArguments,
            "pageEnd");

        string toolName;
        JsonElement arguments;
        switch (capability)
        {
            case "rag_search" when query.Length > 0:
                toolName = "rag_search";
                arguments = BuildCompactFollowUpArguments(
                    ("query", query),
                    ("topK", limit),
                    ("categoryPath", EmptyToNull(scope)),
                    ("docPath", EmptyToNull(document)),
                    ("pageStart", pageStart),
                    ("pageEnd", pageEnd),
                    ("mode", mode));
                break;
            case "documents_navigation":
                toolName = "documents_navigation";
                arguments = BuildCompactFollowUpArguments(
                    ("q", EmptyToNull(query)),
                    ("categoryPath", EmptyToNull(scope)),
                    ("docRef", EmptyToNull(document)),
                    ("kind", EmptyToNull(navigationKind)),
                    ("limit", limit),
                    ("offset", offset));
                break;
            case "documents_content_cards":
                toolName = "documents_content_cards";
                arguments = BuildCompactFollowUpArguments(
                    ("q", EmptyToNull(query)),
                    ("categoryPath", EmptyToNull(scope)),
                    ("docRef", EmptyToNull(document)),
                    ("inventoryMode", EmptyToNull(inventoryMode)),
                    ("limit", limit),
                    ("offset", offset));
                break;
            case "documents_context":
                toolName = "documents_context";
                arguments = BuildCompactFollowUpArguments(
                    ("docRef", EmptyToNull(document)),
                    ("chunkId", EmptyToNull(anchor)),
                    ("pageStart", pageStart),
                    ("pageEnd", pageEnd),
                    ("limit", limit),
                    ("offset", offset));
                break;
            default:
                return false;
        }

        normalizedCall = new SourceBackedAgentToolCall(
            callId,
            toolName,
            arguments);
        return true;
    }

    private static string ResolveGroundedCompactDocumentReference(
        string submittedDocument,
        SourceBackedIntake intake,
        EvidenceBundle bundle)
    {
        var candidate = submittedDocument.Trim();
        if (candidate.Length == 0)
            return string.Empty;

        if (intake.UserQuestion.Contains(
                candidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        foreach (var item in bundle.Items)
        {
            var references = new[]
            {
                item.DocPath,
                item.DocName,
                item.DocId
            };
            if (!references
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Any(value => string.Equals(
                    value!.Trim(),
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return item.DocPath?.Trim()
                   ?? item.DocName?.Trim()
                   ?? item.DocId?.Trim()
                   ?? candidate;
        }

        return string.Empty;
    }

    private static string? EmptyToNull(string value)
        => value.Length == 0 ? null : value;

    private static bool TryBuildLeadContextAction(
        EvidenceBundle bundle,
        IReadOnlyList<string> leadEvidenceIds,
        out SourceBackedAgentToolCall call)
    {
        var lead = leadEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .FirstOrDefault(static item => item is not null);
        if (lead is null)
        {
            call = default!;
            return false;
        }

        var docId = lead.DocId?.Trim() ?? string.Empty;
        var docPath = lead.DocPath?.Trim() ?? string.Empty;
        var docName = lead.DocName?.Trim() ?? string.Empty;
        var document = docPath.Length > 0
            ? docPath
            : docName.Length > 0
                ? docName
                : docId;
        var chunkId = lead.ChunkId?.Trim() ?? string.Empty;
        if (document.Length == 0 && chunkId.Length == 0)
        {
            call = default!;
            return false;
        }

        call = new SourceBackedAgentToolCall(
            "evidence-judge-context-" + Guid.NewGuid().ToString("N")[..10],
            "documents_context",
            BuildCompactFollowUpArguments(
                ("docId", EmptyToNull(docId)),
                ("docPath", EmptyToNull(docPath)),
                ("docRef", EmptyToNull(document)),
                ("chunkId", EmptyToNull(chunkId)),
                ("pageStart", lead.PageStart),
                ("pageEnd", lead.PageEnd),
                ("before", 2),
                ("after", 2),
                ("limit", 8),
                ("offset", 0)));
        return true;
    }

    private static JsonElement BuildCompactFollowUpArguments(
        params (string Name, object? Value)[] values)
    {
        var arguments = new Dictionary<string, object?>(
            StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (value is null
                || value is string text
                && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            arguments[name] = value;
        }

        return JsonSerializer.SerializeToElement(
            arguments,
            ClientJson.CamelCase);
    }

    private static string ReadCompactFollowUpString(
        JsonElement root,
        string propertyName)
        => TryGetPropertyIgnoreCase(root, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int ReadCompactFollowUpInteger(
        JsonElement root,
        string propertyName,
        int fallback)
        => TryGetPropertyIgnoreCase(root, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var value)
            ? value
            : fallback;

    private static int? ReadCompactFollowUpNullableInteger(
        JsonElement root,
        string propertyName)
        => TryGetPropertyIgnoreCase(root, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var value)
            ? value
            : null;
}
