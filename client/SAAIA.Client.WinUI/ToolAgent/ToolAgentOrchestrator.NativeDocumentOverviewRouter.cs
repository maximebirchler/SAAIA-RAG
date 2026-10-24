using static SAAIA.Client.Core.NativeRouting.NativeRouterResponseReader;
using static SAAIA.Client.Core.NativeRouting.NativeRouterRouteNames;
using static SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting.NativeRouterToolCatalog;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int NativeDocumentOverviewRouteMaximumOutputTokens = 160;

    private bool TryBuildNativeDocumentOverviewRouterPlan(
        JsonElement arguments,
        string sourceRequest,
        string detectedLanguage,
        bool disallowMetaSetLanguage,
        bool categoryHintsIncluded,
        out RouterPlan plan,
        out string failureReason)
    {
        plan = new RouterPlan();
        failureReason = string.Empty;

        var document = NormalizeNativeRouterOptionalReference(
            ReadNativeRouterString(arguments, "document"));
        var facets = ReadNativeRouterStringArray(arguments, "facets");
        var rawFacetCount = TryReadNativeRouterProperty(
                arguments,
                "facets",
                out var rawFacets)
            && rawFacets.ValueKind == JsonValueKind.Array
                ? rawFacets.GetArrayLength()
                : 0;
        var hasRequestedPointCount = TryReadNativeRouterInteger(
            arguments,
            "requestedPointCount",
            out var requestedPointCount);
        var hasSampleCount = TryReadNativeRouterInteger(
            arguments,
            "sampleCount",
            out var sampleCount);
        var questionFocus = ReadNativeRouterString(
            arguments,
            "questionFocus");

        if (document.Length == 0
            || !sourceRequest.Contains(
                document,
                StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "native_document_overview_document_not_explicit";
            return false;
        }

        if (rawFacetCount is < 2 or > 5
            || facets.Count != rawFacetCount
            || facets.Any(static facet => facet.Length is < 1 or > 180)
            || facets.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               != facets.Count
            || !hasRequestedPointCount
            || requestedPointCount != facets.Count
            || !hasSampleCount
            || sampleCount != requestedPointCount + 1
            || !string.Equals(
                questionFocus,
                "content",
                StringComparison.Ordinal))
        {
            failureReason = "native_document_overview_contract_invalid";
            return false;
        }

        var canonicalArguments = JsonSerializer.SerializeToElement(
            new
            {
                tool = "overview",
                intent = "summary_doc",
                query = string.Empty,
                sourceItemType = "document_overview_claim",
                sourceItemMode = "content_claim",
                selectionPolicy = "explicit_set",
                scope = (string?)null,
                document,
                pool = sampleCount,
                count = requestedPointCount,
                useFocusedDocument = false,
                questionFocus = "content",
                overviewFacets = facets
            },
            ClientJson.CamelCase);

        return TryBuildNativeSourceBackedRouterPlan(
            canonicalArguments,
            sourceRequest,
            detectedLanguage,
            disallowMetaSetLanguage,
            categoryHintsIncluded,
            prevalidatedCategoryScope: null,
            forcedKind: "many",
            out plan,
            out failureReason);
    }

    private static string ResolveNativeRouterRepairRouteToolName(
        string classifierSelectedRouteToolName,
        string completionRouteToolName)
        => string.Equals(
            classifierSelectedRouteToolName,
            SubmitDocumentOverviewRouteToolName,
            StringComparison.Ordinal)
            ? SubmitSourceBackedRouteToolName
            : completionRouteToolName;

    internal static SourceBackedAgentToolDefinition
        BuildNativeDocumentOverviewRouteToolForTests()
        => BuildNativeDocumentOverviewRouteTool();

    internal static string ResolveNativeRouterRepairRouteToolNameForTests(
        string classifierSelectedRouteToolName,
        string completionRouteToolName)
        => ResolveNativeRouterRepairRouteToolName(
            classifierSelectedRouteToolName,
            completionRouteToolName);
}
