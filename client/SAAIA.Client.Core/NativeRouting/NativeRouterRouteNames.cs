namespace SAAIA.Client.Core.NativeRouting;

/// <summary>Wire identifiers shared by native-router schemas and plan construction.</summary>
public static class NativeRouterRouteNames
{
    public const string SubmitSourceBackedRouteToolName =
        "submit_source_backed_route";

    public const string SubmitSourceBackedGridRouteToolName =
        "submit_source_backed_grid_route";

    public const string RequestUserClarificationToolName =
        "request_user_clarification";

    public const string SubmitOperationalRouteToolName =
        "submit_operational_route";

    public const string SubmitApplicationDecisionRouteToolName = "submit_application_decision_route";

    public const string RequestUnboundUserReferenceRouteToolName = "request_unbound_user_reference_route";

    public const string UnadvertisedNativeRouteFailure = "native_route_tool_not_advertised";

    public const string SubmitDocumentOverviewRouteToolName =
        "submit_document_overview_route";

    public const string RequestMissingUserInputToolName = "request_missing_user_input";

}
