using System.IO;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldUseSourceBackedRagPipeline(
        RouterPlan routerPlan)
    {
        if (routerPlan.NeedClarification)
            return false;

        var calls = routerPlan.ToolCalls ?? new List<RouterPlan.ToolCall>();
        if (calls.Any(static call =>
                ToolManifest.IsAdminTool(NormalizeToolName(call.Name)))
            || routerPlan.Intent.StartsWith(
                "meta.",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (routerPlan.Origin == RouterPlanOrigin.Llm
            && routerPlan.SourceBackedMission is not null)
        {
            return calls.Count == 0
                   || calls.Any(static call =>
                       SourceBackedAgentToolCatalog.TryResolveExternalName(
                           NormalizeToolName(call.Name),
                           out _));
        }

        if (calls.Count == 0)
            return false;

        return calls.Any(static call =>
            NormalizeToolName(call.Name) is
                "rag.search" or "rag.multi_search");
    }

    internal static bool ShouldUseSourceBackedRagPipelineForTests(
        RouterPlan routerPlan)
        => ShouldUseSourceBackedRagPipeline(routerPlan);

    private static bool
        ShouldRouteStandaloneFallbackThroughSourceBackedRagPipeline(
            string effectiveUserMessage,
            RouterPlan routerPlan)
        => ShouldForceRagForStandaloneTopic(
            effectiveUserMessage,
            routerPlan);

    private static string? ResolveSourceBackedRequestedDocumentName(
        string effectiveUserMessage,
        RouterPlan routerPlan)
    {
        var mission = routerPlan.SourceBackedMission;
        if (routerPlan.Origin == RouterPlanOrigin.Llm
            && mission is not null)
        {
            var missionDocument = mission.RequestedDocumentName;
            if (mission.UsesFocusedDocument)
            {
                return !string.IsNullOrWhiteSpace(missionDocument)
                    ? Path.GetFileName(missionDocument.Trim())
                    : ResolveSourceBackedRequestedDocumentFromToolCalls(
                        routerPlan);
            }

            var missionKind = mission.NamedReferenceKind?.Trim();
            if (string.Equals(
                    missionKind,
                    "document",
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(missionDocument)
                    ? null
                    : Path.GetFileName(missionDocument.Trim());
            }

            return null;
        }

        var explicitDocument =
            ExtractExplicitDocumentFileReferenceQueries(effectiveUserMessage)
                .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(explicitDocument))
            return Path.GetFileName(explicitDocument.Trim());

        var namedReferenceKind = mission?.NamedReferenceKind;
        if (mission?.UsesFocusedDocument != true
            && (string.Equals(
                    namedReferenceKind,
                    "subject",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    namedReferenceKind,
                    "none",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var legacyMissionDocument =
            mission?.RequestedDocumentName;
        if (!string.IsNullOrWhiteSpace(legacyMissionDocument))
            return Path.GetFileName(legacyMissionDocument.Trim());

        return ResolveSourceBackedRequestedDocumentFromToolCalls(routerPlan);
    }

    private static string? ResolveSourceBackedRequestedDocumentFromToolCalls(
        RouterPlan routerPlan)
    {
        foreach (var call in
                 routerPlan.ToolCalls ?? new List<RouterPlan.ToolCall>())
        {
            if (call.Args.ValueKind
                != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            foreach (var propertyName in new[]
                     {
                         "docPath",
                         "docRef",
                         "document"
                     })
            {
                if (!call.Args.TryGetProperty(
                        propertyName,
                        out var property)
                    || property.ValueKind
                    != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.GetString()))
                {
                    continue;
                }

                return Path.GetFileName(property.GetString()!.Trim());
            }
        }

        return null;
    }

    private static string? ResolveSourceBackedNamedReferenceKind(
        string effectiveUserMessage,
        RouterPlan routerPlan)
    {
        var mission = routerPlan.SourceBackedMission;
        if (routerPlan.Origin == RouterPlanOrigin.Llm
            && mission is not null)
        {
            if (mission.UsesFocusedDocument)
                return "document";

            var missionKind = mission.NamedReferenceKind?
                .Trim()
                .ToLowerInvariant();
            return missionKind is "none" or "subject" or "document"
                ? missionKind
                : null;
        }

        if (ExtractExplicitDocumentFileReferenceQueries(effectiveUserMessage)
            .Any())
        {
            return "document";
        }

        if (mission?.UsesFocusedDocument == true)
            return "document";
        var kind = mission?.NamedReferenceKind?.Trim();
        if (kind is "none" or "subject" or "document")
            return kind;
        return string.IsNullOrWhiteSpace(mission?.RequestedDocumentName)
            ? null
            : "document";
    }

    private static string? NullIfBlankSourceBackedMissionValue(
        string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
