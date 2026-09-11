using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record GroundedGridShape(
        string RowCountAnchor,
        int RowCount,
        IReadOnlyList<string> Rows,
        string ColumnCountAnchor,
        int ColumnCount,
        IReadOnlyList<string> Columns)
    {
        public int CellCount => RowCount * ColumnCount;

        public bool IsComplete => RowCount >= 2
                                  && ColumnCount >= 2
                                  && CellCount <= 80;
    }

    private async Task<GroundedGridShape?> CompleteGroundedGridShapeAsync(
        ISourceBackedAgentStructuredLlmClient llm,
        string question,
        int maximumOutputTokens,
        CancellationToken ct)
    {
        var completion = await llm.CompleteStructuredAsync(
                [
                    SourceBackedAgentMessage.System(BuildGroundedGridShapePrompt()),
                    SourceBackedAgentMessage.User("CURRENT_REQUEST: " + question)
                ],
                BuildGroundedGridShapeContract(),
                maximumOutputTokens,
                ct,
                temperatureOverride: 0)
            .ConfigureAwait(false);
        var accepted = TryReadGroundedGridShape(completion, question, out var shape);
        EmitRagTrace(
            "router.grid_shape.completed",
            ("accepted", accepted),
            ("complete", shape?.IsComplete),
            ("row_count_anchor", shape?.RowCountAnchor),
            ("row_count", shape?.RowCount),
            ("rows", shape?.Rows),
            ("column_count_anchor", shape?.ColumnCountAnchor),
            ("column_count", shape?.ColumnCount),
            ("columns", shape?.Columns),
            ("cell_count", shape?.CellCount),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("finish_reason", completion.FinishReason));
        if (!accepted)
        {
            EmitRagTrace(
                "router.grid_shape.rejected",
                ("protocol_error", completion.ProtocolError),
                ("raw_output", TruncateForPrompt(
                    completion.ProtocolRawOutput ?? completion.Content,
                    900)));
        }

        return accepted ? shape : null;
    }

    private static LlmStructuredOutputContract BuildGroundedGridShapeContract()
        => new(
            "saaia_explicit_grid_axes_v1",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    rowCountAnchor = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 80
                    },
                    rowCount = new
                    {
                        type = "integer",
                        minimum = 0,
                        maximum = 12
                    },
                    rowAnchors = new
                    {
                        type = "array",
                        minItems = 0,
                        maxItems = 12,
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 120
                        }
                    },
                    columnCountAnchor = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 80
                    },
                    columnCount = new
                    {
                        type = "integer",
                        minimum = 0,
                        maximum = 12
                    },
                    columnAnchors = new
                    {
                        type = "array",
                        minItems = 0,
                        maxItems = 12,
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 120
                        }
                    }
                },
                required = new[]
                {
                    "rowCountAnchor", "rowCount", "rowAnchors",
                    "columnCountAnchor", "columnCount", "columnAnchors"
                },
                additionalProperties = false
            }));

    private static bool TryReadGroundedGridShape(
        SourceBackedAgentCompletion completion,
        string question,
        out GroundedGridShape? shape)
    {
        shape = null;
        if (!string.Equals(completion.FinishReason, "stop", StringComparison.Ordinal)
            || completion.ToolCalls.Count != 0
            || !string.IsNullOrEmpty(completion.ProtocolError)
            || string.IsNullOrWhiteSpace(completion.Content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 6
                || !TryReadGroundedGridAxis(
                    root,
                    question,
                    "rowCountAnchor",
                    "rowCount",
                    "rowAnchors",
                    out var rowCountAnchor,
                    out var rowCount,
                    out var rows)
                || !TryReadGroundedGridAxis(
                    root,
                    question,
                    "columnCountAnchor",
                    "columnCount",
                    "columnAnchors",
                    out var columnCountAnchor,
                    out var columnCount,
                    out var columns))
            {
                return false;
            }

            shape = new GroundedGridShape(
                rowCountAnchor,
                rowCount,
                rows,
                columnCountAnchor,
                columnCount,
                columns);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadGroundedGridAxis(
        JsonElement root,
        string question,
        string countAnchorProperty,
        string countProperty,
        string memberAnchorsProperty,
        out string countAnchor,
        out int count,
        out IReadOnlyList<string> memberAnchors)
    {
        countAnchor = string.Empty;
        count = 0;
        memberAnchors = Array.Empty<string>();
        if (!root.TryGetProperty(countAnchorProperty, out var countAnchorValue)
            || countAnchorValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty(countProperty, out var countValue)
            || countValue.ValueKind != JsonValueKind.Number
            || !countValue.TryGetInt32(out count)
            || count is < 0 or > 12
            || !root.TryGetProperty(memberAnchorsProperty, out var anchorsValue)
            || anchorsValue.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        countAnchor = countAnchorValue.GetString()?.Trim() ?? string.Empty;
        var anchors = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in anchorsValue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var anchor = item.GetString()?.Trim() ?? string.Empty;
            if (anchor.Length is < 1 or > 120
                || !question.Contains(anchor, StringComparison.Ordinal)
                || !unique.Add(anchor))
            {
                return false;
            }
            anchors.Add(anchor);
        }

        if (anchors.Count > 12
            || (count == 0
                ? countAnchor.Length != 0 || anchors.Count != 0
                : countAnchor.Length is < 1 or > 80
                  || !question.Contains(countAnchor, StringComparison.Ordinal)
                  || ConvertGroundedGridSmallCount(countAnchor) != count
                  || anchors.Count != count))
        {
            return false;
        }

        memberAnchors = anchors;
        return true;
    }

    private static int ConvertGroundedGridSmallCount(string countAnchor)
    {
        if (string.IsNullOrWhiteSpace(countAnchor))
            return 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["one"] = 1,
            ["un"] = 1,
            ["une"] = 1,
            ["uno"] = 1,
            ["una"] = 1,
            ["um"] = 1,
            ["uma"] = 1,
            ["ein"] = 1,
            ["eine"] = 1,
            ["two"] = 2,
            ["deux"] = 2,
            ["dos"] = 2,
            ["dois"] = 2,
            ["duas"] = 2,
            ["zwei"] = 2,
            ["due"] = 2,
            ["three"] = 3,
            ["trois"] = 3,
            ["tres"] = 3,
            ["drei"] = 3,
            ["tre"] = 3,
            ["four"] = 4,
            ["quatre"] = 4,
            ["cuatro"] = 4,
            ["quatro"] = 4,
            ["vier"] = 4,
            ["quattro"] = 4,
            ["five"] = 5,
            ["cinq"] = 5,
            ["cinco"] = 5,
            ["funf"] = 5,
            ["fuenf"] = 5,
            ["cinque"] = 5,
            ["six"] = 6,
            ["seis"] = 6,
            ["sechs"] = 6,
            ["sei"] = 6,
            ["seven"] = 7,
            ["sept"] = 7,
            ["siete"] = 7,
            ["sete"] = 7,
            ["sieben"] = 7,
            ["sette"] = 7,
            ["eight"] = 8,
            ["huit"] = 8,
            ["ocho"] = 8,
            ["oito"] = 8,
            ["acht"] = 8,
            ["otto"] = 8,
            ["nine"] = 9,
            ["neuf"] = 9,
            ["nueve"] = 9,
            ["nove"] = 9,
            ["neun"] = 9,
            ["ten"] = 10,
            ["dix"] = 10,
            ["diez"] = 10,
            ["dez"] = 10,
            ["zehn"] = 10,
            ["dieci"] = 10,
            ["eleven"] = 11,
            ["onze"] = 11,
            ["once"] = 11,
            ["elf"] = 11,
            ["undici"] = 11,
            ["twelve"] = 12,
            ["douze"] = 12,
            ["doce"] = 12,
            ["zwolf"] = 12,
            ["zwoelf"] = 12,
            ["dodici"] = 12
        };
        foreach (Match match in Regex.Matches(
                     countAnchor,
                     @"[\p{L}\p{N}]+",
                     RegexOptions.CultureInvariant))
        {
            var token = RemoveGroundedGridDiacritics(match.Value)
                .ToLowerInvariant();
            if (int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                && number is >= 0 and <= 12)
            {
                return number;
            }
            if (counts.TryGetValue(token, out number))
                return number;
        }

        return -1;
    }

    private static string RemoveGroundedGridDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BindGroundedGridShape(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            GroundedGridShape shape)
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
            properties["count"] = JsonSerializer.SerializeToNode(new
            {
                type = "integer",
                minimum = shape.CellCount,
                maximum = shape.CellCount
            });
            properties["rows"] = JsonSerializer.SerializeToNode(new
            {
                type = "array",
                minItems = shape.RowCount,
                maxItems = shape.RowCount,
                items = new
                {
                    type = "string",
                    @enum = shape.Rows
                }
            });
            properties["columns"] = JsonSerializer.SerializeToNode(new
            {
                type = "array",
                minItems = shape.ColumnCount,
                maxItems = shape.ColumnCount,
                items = new
                {
                    type = "string",
                    @enum = shape.Columns
                }
            });
            return tool with
            {
                Parameters = JsonSerializer.SerializeToElement(schema)
            };
        }).ToArray();

    private static string DescribeGroundedGridShape(GroundedGridShape shape)
        => "\n\nGRID_SHAPE already established from literal spans in CURRENT_REQUEST: "
           + JsonSerializer.Serialize(new
           {
               rows = shape.Rows,
               columns = shape.Columns,
               count = shape.CellCount
           })
           + ". Preserve rows, columns and count exactly and in this order. "
           + "Choose only the evidence-discovery fields without reinterpreting either axis.";

    private static bool ValidateGroundedGridShape(
        GroundedGridShape? shape,
        RouterPlan plan,
        ref string failureReason)
    {
        if (shape is null || !shape.IsComplete)
            return true;
        var mission = plan.SourceBackedMission;
        if (mission is not null
            && mission.StructuredLayout
            && mission.RowCount == shape.RowCount
            && mission.ColumnCount == shape.ColumnCount
            && mission.AtomicEvidenceCount == shape.CellCount
            && mission.RowLabels.SequenceEqual(shape.Rows, StringComparer.Ordinal)
            && mission.Columns.SequenceEqual(shape.Columns, StringComparer.Ordinal))
        {
            return true;
        }

        failureReason = "native_source_route_grid_shape_changed";
        return false;
    }

    private static string BuildGroundedGridShapePrompt()
        => """
           Extract only explicitly counted grid axes from CURRENT_REQUEST. Do not answer, retrieve, infer missing labels, translate, or create examples.

           For each axis, copy into its countAnchor one exact short substring containing the explicit quantity and axis class, copy the quantity into count, and copy one exact substring for each individually named member into anchors. The quantity phrase is not a member. Rows are the items whose fields will be displayed. Columns are the repeated fields. Counts and member-array lengths must match.

           If an axis has no explicit quantity or lacks individually named members, return an empty countAnchor, count=0 and an empty anchors array for that axis. An absent other axis does not erase a fully explicit axis.
           """;
}
