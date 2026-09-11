using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string ClassifyGridPropertiesToolName =
        "classify_grid_values_as_properties_of_row_subjects";
    private const string ClassifyGridSlotsToolName =
        "classify_grid_values_as_named_items_for_slots";
    private const string GroundedGridContentClaimType =
        "source-backed fact about the row subject";

    private async Task<string?> CompleteGroundedGridEvidenceModeAsync(
        ISourceBackedAgentLlmClient llm,
        string question,
        GroundedGridShape shape,
        int maximumOutputTokens,
        CancellationToken ct)
    {
        var tools = BuildGroundedGridFamilyTools();
        var completion = await llm.CompleteAsync(
                [
                    SourceBackedAgentMessage.System(
                        "Classify the value-bearing cells of the supplied grid. "
                        + "Do not answer or retrieve. Inspect the already extracted row and column labels, "
                        + "then call exactly one available function. The value type is determined by what a "
                        + "completed cell contains, not by the corpus or table title."),
                    SourceBackedAgentMessage.User(
                        "CURRENT_REQUEST: " + question
                        + "\nROW_LABELS: " + JsonSerializer.Serialize(shape.Rows)
                        + "\nCOLUMN_LABELS: " + JsonSerializer.Serialize(shape.Columns))
                ],
                tools,
                maximumOutputTokens,
                ct,
                temperatureOverride: 0,
                requireToolCall: true)
            .ConfigureAwait(false);

        var accepted = TryReadGroundedGridEvidenceMode(
            completion,
            out var evidenceMode);
        EmitRagTrace(
            "router.grid_evidence_mode.completed",
            ("accepted", accepted),
            ("evidence_mode", evidenceMode),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("finish_reason", completion.FinishReason));
        if (!accepted)
        {
            EmitRagTrace(
                "router.grid_evidence_mode.rejected",
                ("protocol_error", completion.ProtocolError),
                ("tool_count", completion.ToolCalls.Count),
                ("raw_output", TruncateForPrompt(
                    completion.ProtocolRawOutput ?? completion.Content,
                    500)));
        }

        return accepted ? evidenceMode : null;
    }

    private static bool TryReadGroundedGridEvidenceMode(
        SourceBackedAgentCompletion completion,
        out string? evidenceMode)
    {
        evidenceMode = null;
        if (!string.Equals(
                completion.FinishReason,
                "tool_calls",
                StringComparison.OrdinalIgnoreCase)
            || completion.ToolCalls.Count != 1
            || !string.IsNullOrWhiteSpace(completion.Content)
            || !string.IsNullOrEmpty(completion.ProtocolError))
        {
            return false;
        }

        var call = completion.ToolCalls[0];
        if (call.Arguments.ValueKind != JsonValueKind.Object
            || call.Arguments.EnumerateObject().Any())
        {
            return false;
        }

        evidenceMode = call.Name switch
        {
            ClassifyGridPropertiesToolName => "content_claim",
            ClassifyGridSlotsToolName => "named_item",
            _ => null
        };
        return evidenceMode is not null;
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BindGroundedGridEvidenceMode(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            string evidenceMode)
        => tools.Select(tool =>
        {
            if (!string.Equals(
                    tool.Name,
                    SubmitSourceBackedGridRouteToolName,
                    StringComparison.Ordinal))
            {
                return tool;
            }

            var schema = JsonNode.Parse(tool.Parameters.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            properties["sourceItemMode"] = JsonSerializer.SerializeToNode(new
            {
                type = "string",
                @enum = new[] { evidenceMode }
            });
            if (string.Equals(
                    evidenceMode,
                    "content_claim",
                    StringComparison.Ordinal))
            {
                properties["sourceItemType"] = JsonSerializer.SerializeToNode(new
                {
                    type = "string",
                    @enum = new[] { GroundedGridContentClaimType }
                });
            }

            var required = schema["required"]!.AsArray();
            if (!required.Any(node => string.Equals(
                    node?.GetValue<string>(),
                    "sourceItemMode",
                    StringComparison.Ordinal)))
            {
                required.Add("sourceItemMode");
            }
            return tool with
            {
                Parameters = JsonSerializer.SerializeToElement(schema)
            };
        }).ToArray();

    private static string DescribeGroundedGridEvidenceMode(string evidenceMode)
        => "\n\nGRID_EVIDENCE_MODE already selected by a separate semantic function: "
           + JsonSerializer.Serialize(evidenceMode)
           + ". Set sourceItemMode to this exact value. "
           + (string.Equals(evidenceMode, "content_claim", StringComparison.Ordinal)
               ? "Set sourceItemType to "
                 + JsonSerializer.Serialize(GroundedGridContentClaimType)
                 + "."
               : "Choose sourceItemType as the named object class placed in each cell.");
}
