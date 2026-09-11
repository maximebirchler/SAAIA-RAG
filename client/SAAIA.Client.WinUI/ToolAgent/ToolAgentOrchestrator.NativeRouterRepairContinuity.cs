using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static void PreserveExplicitDocumentAcrossNativeRouterRepair(
        string sourceRequest,
        SourceBackedAgentCompletion invalidCompletion,
        RouterPlan repairedPlan)
    {
        var mission = repairedPlan.SourceBackedMission;
        if (mission is null
            || !string.IsNullOrWhiteSpace(mission.RequestedDocumentName)
            || string.Equals(
                mission.NamedReferenceKind,
                "subject",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var explicitDocument =
            ExtractExplicitDocumentFromInvalidNativeRoute(
                sourceRequest,
                invalidCompletion);
        if (!string.IsNullOrWhiteSpace(explicitDocument))
        {
            mission.RequestedDocumentName = explicitDocument;
            mission.NamedReferenceKind = "document";
        }
    }

    private static string? ExtractExplicitDocumentFromInvalidNativeRoute(
        string sourceRequest,
        SourceBackedAgentCompletion invalidCompletion)
    {
        if (invalidCompletion.ToolCalls.Count != 1)
            return null;

        var call = invalidCompletion.ToolCalls[0];
        if (call.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object
            || (!string.Equals(
                    call.Name,
                    SubmitSourceBackedRouteToolName,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    call.Name,
                    SubmitSourceBackedGridRouteToolName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var document = NormalizeNativeRouterOptionalReference(
            ReadNativeRouterString(call.Arguments, "document"));
        var namedReferenceKind = ReadNativeRouterString(
            call.Arguments,
            "namedReferenceKind");
        if (namedReferenceKind.Length > 0
            && !string.Equals(
                namedReferenceKind,
                "document",
                StringComparison.Ordinal))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(document)
            || !sourceRequest.Contains(
                document,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scope = NormalizeNativeRouterOptionalReference(
            ReadNativeRouterString(call.Arguments, "scope"));
        if (string.Equals(
                document,
                scope,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFileName(document);
    }
}
