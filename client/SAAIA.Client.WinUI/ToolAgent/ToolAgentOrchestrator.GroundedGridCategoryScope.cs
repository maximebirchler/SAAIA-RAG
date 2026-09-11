using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string?> CompleteGroundedGridCategoryScopeAsync(
        ISourceBackedAgentStructuredLlmClient llm,
        string question,
        GroundedGridShape shape,
        IReadOnlyList<SourceBackedCatalogHint> catalogHints,
        int maximumOutputTokens,
        CancellationToken ct)
    {
        var categories = catalogHints
            .Where(static hint => !string.IsNullOrWhiteSpace(hint.CategoryPath))
            .GroupBy(static hint => hint.CategoryPath.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(SourceBackedCatalogHintLimit)
            .ToArray();
        if (categories.Length == 0)
            return null;

        if (TryResolveExplicitGroundedGridCategoryScope(
                question,
                categories,
                out var explicitCategoryPath))
        {
            EmitRagTrace(
                "router.grid_category_scope.completed",
                ("accepted", true),
                ("decision", "use_scope"),
                ("decision_source", "literal_exact_category"),
                ("category_path", explicitCategoryPath),
                ("category_count", categories.Length),
                ("prompt_tokens", 0),
                ("completion_tokens", 0),
                ("finish_reason", "mechanical_exact_match"));
            return explicitCategoryPath;
        }

        var completion = await llm.CompleteStructuredAsync(
                [
                    SourceBackedAgentMessage.System(
                        "Classify each supplied ROW_LABEL independently by the one published corpus category "
                        + "that directly describes where documents about that exact row subject belong. Use an "
                        + "empty string for a day, time slot, phase, mixed or ambiguous label, or any subject "
                        + "without one clear category. Do not let one row influence another. Never force the "
                        + "nearest category, invent one or translate one. Return the same number of rowCategories "
                        + "in the same order. Do not answer or retrieve."),
                    SourceBackedAgentMessage.User(
                        "ROW_LABELS: " + JsonSerializer.Serialize(shape.Rows)
                        + "\nPUBLISHED_CATEGORIES: "
                        + JsonSerializer.Serialize(categories.Select(static hint => new
                        {
                            categoryPath = hint.CategoryPath,
                            displayName = hint.DisplayName,
                            aliases = hint.Aliases
                        })))
                ],
                BuildGroundedGridRowCategoryScopeContract(
                    categories,
                    shape.RowCount),
                maximumOutputTokens,
                ct,
                temperatureOverride: 0)
            .ConfigureAwait(false);

        var accepted = TryReadGroundedGridRowCategoryScope(
            completion,
            categories,
            shape.RowCount,
            out var rowCategoryPaths,
            out var categoryPath);
        EmitRagTrace(
            "router.grid_category_scope.completed",
            ("accepted", accepted),
            ("decision", string.IsNullOrWhiteSpace(categoryPath)
                ? "open"
                : "use_scope"),
            ("decision_source", "llm_per_row_consensus"),
            ("row_category_paths", rowCategoryPaths),
            ("category_path", categoryPath),
            ("category_count", categories.Length),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("finish_reason", completion.FinishReason));
        if (!accepted)
        {
            EmitRagTrace(
                "router.grid_category_scope.rejected",
                ("protocol_error", completion.ProtocolError),
                ("raw_output", TruncateForPrompt(
                    completion.ProtocolRawOutput ?? completion.Content,
                    500)));
        }

        return accepted ? categoryPath : null;
    }

    private static bool TryResolveExplicitGroundedGridCategoryScope(
        string question,
        IReadOnlyList<SourceBackedCatalogHint> categories,
        out string? categoryPath)
    {
        categoryPath = null;
        var matches = categories
            .Where(category => EnumerateGroundedGridCategoryLabels(category)
                .Any(label => ContainsGroundedGridCategoryLabel(question, label)))
            .Select(static category => category.CategoryPath.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1)
            return false;
        categoryPath = matches[0];
        return true;
    }

    private static IEnumerable<string> EnumerateGroundedGridCategoryLabels(
        SourceBackedCatalogHint category)
    {
        yield return category.CategoryPath;
        if (!string.IsNullOrWhiteSpace(category.DisplayName))
            yield return category.DisplayName;
        foreach (var alias in category.Aliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }

    private static bool ContainsGroundedGridCategoryLabel(
        string question,
        string label)
    {
        var trimmed = label.Trim();
        if (trimmed.Length < 2)
            return false;
        return Regex.IsMatch(
            question,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(trimmed)
                                   + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
    }

    private static LlmStructuredOutputContract
        BuildGroundedGridRowCategoryScopeContract(
            IReadOnlyList<SourceBackedCatalogHint> categories,
            int rowCount)
        => new(
            "saaia_grounded_grid_row_category_scope_v2",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    rowCategories = new
                    {
                        type = "array",
                        minItems = rowCount,
                        maxItems = rowCount,
                        items = new
                        {
                            type = "string",
                            @enum = new[] { string.Empty }
                                .Concat(categories.Select(static hint =>
                                    hint.CategoryPath.Trim()))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                        }
                    }
                },
                required = new[] { "rowCategories" },
                additionalProperties = false
            }));

    private static bool TryReadGroundedGridRowCategoryScope(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<SourceBackedCatalogHint> categories,
        int expectedRowCount,
        out IReadOnlyList<string> rowCategoryPaths,
        out string? categoryPath)
    {
        rowCategoryPaths = Array.Empty<string>();
        categoryPath = null;
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
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("rowCategories", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() != expectedRowCount)
            {
                return false;
            }

            var exactCategories = categories.ToDictionary(
                static hint => hint.CategoryPath.Trim(),
                static hint => hint.CategoryPath.Trim(),
                StringComparer.OrdinalIgnoreCase);
            var rows = new List<string>(expectedRowCount);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    return false;
                var proposed = value.GetString()?.Trim() ?? string.Empty;
                if (proposed.Length == 0)
                {
                    rows.Add(string.Empty);
                    continue;
                }
                if (!exactCategories.TryGetValue(proposed, out var exact))
                    return false;
                rows.Add(exact);
            }

            rowCategoryPaths = rows;
            var first = rows.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)
                && rows.All(row => string.Equals(
                    row,
                    first,
                    StringComparison.OrdinalIgnoreCase)))
            {
                categoryPath = first;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BindGroundedGridCategoryScope(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            string categoryPath)
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
            properties["scope"] = JsonSerializer.SerializeToNode(new
            {
                type = "string",
                @enum = new[] { categoryPath }
            });
            var required = schema["required"]!.AsArray();
            if (!required.Any(node => string.Equals(
                    node?.GetValue<string>(),
                    "scope",
                    StringComparison.Ordinal)))
            {
                required.Add("scope");
            }
            return tool with
            {
                Parameters = JsonSerializer.SerializeToElement(schema)
            };
        }).ToArray();

    private static string DescribeGroundedGridCategoryScope(string categoryPath)
        => "\n\nGRID_CATEGORY_SCOPE already selected from PUBLISHED_CATEGORIES: "
           + JsonSerializer.Serialize(categoryPath)
           + ". Set scope to this exact value.";
}
