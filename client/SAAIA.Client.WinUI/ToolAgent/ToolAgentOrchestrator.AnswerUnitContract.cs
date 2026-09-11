using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record GroundedAnswerUnits(string RequestAnchor, string UnitType,
        string Mode, string SelectionPolicy, int Count,
        IReadOnlyList<string> RequestedParts);

    private bool CanUseStandaloneAnswerUnits(IReadOnlyList<(string role, string content)> history)
        => string.IsNullOrWhiteSpace(_mem.LastAssistantAnswer)
           && _mem.LastFocusedDocument is null
           && !history.Any(m => !string.IsNullOrWhiteSpace(m.content)
               && (string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(m.role, "assistant", StringComparison.OrdinalIgnoreCase)));

    private async Task<GroundedAnswerUnits?> CompleteGroundedAnswerUnitsAsync(
        ISourceBackedAgentStructuredLlmClient llm, string question, int maximumOutputTokens, CancellationToken ct)
    {
        var completion = await llm.CompleteStructuredAsync(
            [SourceBackedAgentMessage.System(BuildGroundedAnswerUnitPrompt()),
             SourceBackedAgentMessage.User("CURRENT_REQUEST: " + question)],
            new LlmStructuredOutputContract("saaia_answer_units_v2", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    requestAnchor = new { type = "string", minLength = 1, maxLength = 200 },
                    requestedParts = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 12,
                        items = new { type = "string", minLength = 2, maxLength = 120 }
                    },
                    unitType = new { type = "string", minLength = 2, maxLength = 100 },
                    outputKind = new { type = "string", @enum = new[] { "object", "facts" } },
                    quantityKind = new { type = "string", @enum = new[] { "one", "exact", "open" } },
                    count = new { type = "integer", minimum = 1, maximum = 80 }
                },
                required = new[] { "requestAnchor", "requestedParts", "unitType", "outputKind", "quantityKind", "count" },
                additionalProperties = false
            })), maximumOutputTokens, ct, temperatureOverride: 0).ConfigureAwait(false);
        var accepted = TryReadGroundedAnswerUnits(completion, question, out var units);
        EmitRagTrace("router.answer_units.completed", ("accepted", accepted),
            ("request_anchor", units?.RequestAnchor), ("unit_type", units?.UnitType),
            ("requested_parts", units is null
                ? null
                : string.Join(" | ", units.RequestedParts)),
            ("mode", units?.Mode), ("selection_policy", units?.SelectionPolicy),
            ("count", units?.Count), ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens), ("finish_reason", completion.FinishReason));
        if (!accepted)
        {
            EmitRagTrace(
                "router.answer_units.rejected",
                ("protocol_error", completion.ProtocolError),
                ("raw_output", TruncateForPrompt(
                    completion.ProtocolRawOutput ?? completion.Content,
                    900)));
        }
        return accepted ? units : null;
    }

    private static bool TryReadGroundedAnswerUnits(SourceBackedAgentCompletion completion,
        string question, out GroundedAnswerUnits? units)
    {
        units = null;
        if (completion.FinishReason != "stop" || completion.ToolCalls.Count != 0
            || !string.IsNullOrEmpty(completion.ProtocolError) || string.IsNullOrWhiteSpace(completion.Content))
            return false;
        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6)
                return false;
            string? Field(string name) => root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var anchor = Field("requestAnchor");
            var type = Field("unitType");
            var kind = Field("outputKind");
            var quantity = Field("quantityKind");
            if (!TryReadRequestedParts(root, question, out var requestedParts))
                return false;
            if (anchor is not { Length: >= 1 and <= 200 } || !question.Contains(anchor, StringComparison.Ordinal)
                || type is not { Length: >= 2 and <= 100 } || string.IsNullOrWhiteSpace(type)
                || kind is not ("object" or "facts") || quantity is not ("one" or "exact" or "open")
                || !root.TryGetProperty("count", out var countValue) || countValue.ValueKind != JsonValueKind.Number
                || !countValue.TryGetInt32(out var count) || count is < 1 or > 80
                || (quantity == "one" && count != 1) || (quantity == "exact" && count <= 1))
                return false;
            if (kind == "facts" && requestedParts.Count > count)
            {
                count = requestedParts.Count;
                if (quantity == "one")
                    quantity = "exact";
            }
            else if (kind == "object" && quantity == "exact" && count > 1
                     && requestedParts.Count != count)
            {
                requestedParts = new[] { anchor };
            }
            units = new(anchor, type.Trim(), kind == "object" ? "named_item" : "content_claim",
                quantity switch { "one" => "single_item", "exact" => "explicit_set", _ => "open_set" }, count,
                requestedParts);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadRequestedParts(
        JsonElement root,
        string question,
        out IReadOnlyList<string> requestedParts)
    {
        requestedParts = Array.Empty<string>();
        if (!root.TryGetProperty("requestedParts", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var part = item.GetString()?.Trim() ?? string.Empty;
            if (part.Length is < 2 or > 120
                || !question.Contains(part, StringComparison.Ordinal)
                || !unique.Add(part))
            {
                return false;
            }
            parts.Add(part);
        }

        if (parts.Count is < 1 or > 12)
            return false;
        requestedParts = parts;
        return true;
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition> BindGroundedAnswerUnits(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools, GroundedAnswerUnits units)
        => tools.Select(tool =>
        {
            if (tool.Name != SubmitSourceBackedRouteToolName) return tool;
            var schema = JsonNode.Parse(tool.Parameters.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            properties["answerUnitType"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = new[] { units.UnitType } });
            properties["answerUnitMode"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = new[] { units.Mode } });
            properties["selectionPolicy"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = new[] { units.SelectionPolicy } });
            properties["count"] = JsonSerializer.SerializeToNode(new { type = "integer", minimum = units.Count, maximum = units.Count });
            return tool with { Parameters = JsonSerializer.SerializeToElement(schema) };
        }).ToArray();

    private static string DescribeGroundedAnswerUnits(GroundedAnswerUnits units)
        => "\n\nOUTPUT_UNITS already established by the model from CURRENT_REQUEST: "
           + JsonSerializer.Serialize(new { units.RequestAnchor, answerUnitType = units.UnitType,
               requestedParts = units.RequestedParts, answerUnitMode = units.Mode,
               selectionPolicy = units.SelectionPolicy, count = units.Count })
           + ". Preserve these four route fields exactly. Choose the retrieval arguments without reinterpreting the output units.";

    private static bool ValidateGroundedAnswerUnits(GroundedAnswerUnits? units, RouterPlan plan, ref string failureReason)
    {
        if (units is null) return true;
        if (plan.NeedClarification && plan.Origin == RouterPlanOrigin.Llm
            && plan.ClarificationQuestions.Count > 0 && plan.ToolCalls.Count == 0
            && plan.SourceBackedMission is null)
        {
            if (units.Count >= AdvancedMultiItemAnswerUnitThreshold)
            {
                failureReason = "native_clarification_route_contract_invalid";
                return false;
            }
            return true;
        }
        var mission = plan.SourceBackedMission;
        var boundedNamedDocumentModeNormalization =
            mission is not null
            && string.Equals(
                units.Mode,
                "named_item",
                StringComparison.Ordinal)
            && string.Equals(
                mission.AtomicEvidenceMode,
                "content_claim",
                StringComparison.Ordinal)
            && string.Equals(
                mission.PlanKind,
                "multi_item",
                StringComparison.Ordinal)
            && string.Equals(
                mission.SelectionPolicy,
                "explicit_set",
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(mission.RequestedDocumentName);
        if (mission is not null && mission.AtomicEvidenceType == units.UnitType
            && (mission.AtomicEvidenceMode == units.Mode
                || boundedNamedDocumentModeNormalization)
            && mission.SelectionPolicy == units.SelectionPolicy
            && mission.AtomicEvidenceCount == units.Count) return true;
        failureReason = "native_source_route_answer_units_changed";
        return false;
    }

    private static string BuildGroundedAnswerUnitPrompt()
        => """
           Identify only the output units requested by the user. Do not answer, retrieve, choose example values, or plan tools. Return the required JSON object.
           First copy into requestAnchor the exact short span from the current request that names the output units and any quantity. Then fill requestedParts with short exact substrings copied from CURRENT_REQUEST: one element for each distinct answer facet explicitly named by the user. For an explicit count of unnamed homogeneous members, copy the group span once, preserve the number separately in count, and never invent member labels. Keep supporting constraints with their result. Do not combine distinct coordinated facets into one element. Do not split one result into its descriptive attributes. A comparison may be one comparison result or its two explicitly named sides; never split it into unstated factual answers. These fields observe the request and are not an answer.

           outputKind="object" when the user asks to receive complete examples, recommendations or selections from a class. Each output object is identified in the sources; the user need not already know its name. For counted objects with descriptive constraints, copy one exact group span that keeps the object and its constraints together; do not split adjectives, audience, cost, use or other constraints into requestedParts. Supporting details belong to that object and do not become additional output objects.
           outputKind="facts" when the user asks for information inside or about a target: facts, properties, explanations, specific passages or procedural steps. Finding the target first does not change this into choosing the target as an output object.

           quantityKind="one" for one singular output unit, with count=1. This includes an indefinite singular request for an example or recommendation.
           quantityKind="exact" for an explicit number of output units greater than one; copy that number into count.
           quantityKind="open" for an unnumbered plural or open collective; count is a small provisional number of answer units, not a new user requirement.

           unitType names the semantic class of the output units, not a mode label, a search query, a target document, a section heading or a guessed answer. Judge outputKind and quantityKind independently. Preserve every explicit quantity and do not split one selected object into its supporting details.
           """;
}
