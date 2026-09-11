using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string SubmitDocumentOverviewRouteToolName =
        "submit_document_overview_route";
    private const int NativeDocumentOverviewRouteMaximumOutputTokens = 160;

    private static SourceBackedAgentToolDefinition
        BuildNativeDocumentOverviewRouteTool()
        => new(
            SubmitDocumentOverviewRouteToolName,
            "One explicitly named document overview with two to five cumulative content facets preserved in user order.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    document = new
                    {
                        type = "string",
                        minLength = 1,
                        maxLength = 260
                    },
                    facets = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 2,
                        maxItems = 5
                    },
                    requestedPointCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 10
                    },
                    sampleCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 11
                    },
                    questionFocus = new
                    {
                        type = "string",
                        @enum = new[] { "content" }
                    }
                },
                required = new[]
                {
                    "document", "facets", "requestedPointCount",
                    "sampleCount", "questionFocus"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

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
