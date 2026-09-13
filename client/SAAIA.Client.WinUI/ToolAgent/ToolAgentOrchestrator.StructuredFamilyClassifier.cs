using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildStructuredRouterFamilyPrompt()
        => """
           Classify the user's goal in a private-document assistant. Active mode: strict. Do not answer the user or plan retrieval. Return only a JSON object with one field, family.

           family="answer": the user wants information, an explanation, advice, a recommendation, a list or specific passages. In this app those answers use the private corpus even when no document is mentioned.
           family="overview": the user explicitly wants an overview of one named whole document covering at least two cumulative content facets. A specific fact, passage or procedure in a named document is answer, not overview.
           family="grid": sourced content must occupy repeated row-by-column positions in a table, schedule or plan. A one-axis list is answer.
           family="operation": social conversation or operating the app itself: greetings, thanks, settings, catalog inventory, export or diagnostics.
           family="missing_instance_facts": a decision about the user's own setup needs actual configuration, state or project phase, entirely absent from request and conversation. Sources cannot establish those facts.
           family="missing_user_binding": an essential user-designated reference or mandatory choice is unbound and conversation cannot identify it. Searching the corpus, PDF collections, document contents and availability are work, not missing bindings. Optional preferences permit documented defaults.

           Missing instance facts take priority. Facts, values and alternatives that corpus evidence can establish remain answer candidates. Optional refinements do not block a request that permits documented candidates or defaults. Never choose operation merely because no document is mentioned.
           """;

    private static LlmStructuredOutputContract BuildStructuredRouterFamilyContract()
        => new("saaia_work_family_v5", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                family = new { type = "string", @enum = new[] { "answer", "overview", "grid", "operation", "missing_instance_facts", "missing_user_binding" } }
            },
            required = new[] { "family" },
            additionalProperties = false
        }));

    private static bool TryResolveStructuredRouterFamily(
        SourceBackedAgentCompletion completion,
        out string selectedToolName)
    {
        selectedToolName = string.Empty;
        if (completion.ToolCalls.Count != 0
            || completion.ProtocolError is { Length: > 0 }
            || !string.Equals(completion.FinishReason, "stop", StringComparison.Ordinal))
            return false;
        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("family", out var family)
                || family.ValueKind != JsonValueKind.String)
                return false;
            selectedToolName = family.GetString() switch
            {
                "answer" => SubmitSourceBackedRouteToolName,
                "overview" => SubmitDocumentOverviewRouteToolName,
                "grid" => SubmitSourceBackedGridRouteToolName,
                "operation" => SubmitOperationalRouteToolName,
                "missing_instance_facts" => RequestMissingUserInputToolName,
                "missing_user_binding" => RequestUnboundUserReferenceRouteToolName,
                _ => string.Empty
            };
            return selectedToolName.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
