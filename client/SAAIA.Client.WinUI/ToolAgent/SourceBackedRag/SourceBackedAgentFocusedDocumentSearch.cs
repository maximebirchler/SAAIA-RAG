using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool TryBuildFocusedDocumentSearchCall(
        SourceBackedAgentToolCall call,
        EvidenceBundle bundle,
        int maximumWorkingEvidenceItems,
        out SourceBackedAgentToolCall expandedCall,
        out string error)
    {
        expandedCall = call;
        if (!TryReadResearchTransitionLimit(
                call.Arguments,
                out var limit,
                out error))
        {
            return false;
        }

        var requestedEvidenceId = NormalizeSemanticPlanLine(GetString(
            call.Arguments,
            "documentFocusEvidenceId"));
        if (!TryResolveDocumentFocus(
                bundle,
                maximumWorkingEvidenceItems,
                requestedEvidenceId,
                out var focusedEvidence,
                out error)
            || focusedEvidence is null)
        {
            if (string.IsNullOrWhiteSpace(error))
                error = "research_transition_document_focus_invalid";
            return false;
        }

        var query = NormalizeSemanticPlanLine(GetString(
            call.Arguments,
            "query"));
        if (query.Length < 2)
        {
            error = "research_transition_query_invalid";
            return false;
        }

        expandedCall = call with
        {
            Name = "rag_search",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                query,
                topK = limit,
                docId = focusedEvidence.DocId,
                docPath = focusedEvidence.DocPath
            }, ClientJson.CamelCase)
        };
        return true;
    }
}
