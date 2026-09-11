using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string RepairNamedReferencePairToolName =
        "repair_named_reference_pair";
    private const int NativeRouterNamedReferenceRepairMaximumOutputTokens = 96;

    private static bool ShouldUseNativeRouterNamedReferencePairRepair(
        string failureReason)
        => string.Equals(
            failureReason,
            "native_source_route_named_reference_inconsistent",
            StringComparison.Ordinal);

    internal static bool ShouldUseNativeRouterNamedReferencePairRepairForTests(
        string failureReason)
        => ShouldUseNativeRouterNamedReferencePairRepair(failureReason);

    internal static int
        GetNativeRouterNamedReferenceRepairMaximumOutputTokensForTests()
        => NativeRouterNamedReferenceRepairMaximumOutputTokens;

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterNamedReferenceRepairTools()
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                RepairNamedReferencePairToolName,
                "Resolve only the semantic named-reference kind and its mechanically paired document transport value; preserve the rest of the route.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        namedReferenceKind = new
                        {
                            type = "string",
                            @enum = new[] { "none", "subject", "document" },
                            description =
                                "Qwen's semantic classification: document only for an explicit artifact title, filename or path; subject for a named entity being researched; none otherwise."
                        },
                        document = new
                        {
                            type = new[] { "string", "null" },
                            minLength = 2,
                            maxLength = 180,
                            description =
                                "Exact artifact title, filename or path span copied from the user when kind=document; null for subject or none."
                        }
                    },
                    required = new[] { "namedReferenceKind", "document" },
                    additionalProperties = false
                }, ClientJson.CamelCase))
        };

    internal static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNativeRouterNamedReferenceRepairToolsForTests()
        => BuildNativeRouterNamedReferenceRepairTools();

    private async Task<SourceBackedAgentCompletion?>
        CompleteNativeRouterNamedReferencePairRepairAsync(
            ISourceBackedAgentLlmClient llm,
            string userMessage,
            SourceBackedAgentCompletion invalidCompletion,
            CancellationToken ct)
    {
        var invalidRoute = invalidCompletion.ToolCalls.Count == 1
            ? invalidCompletion.ToolCalls[0].Arguments.GetRawText()
            : "{}";
        var messages = new[]
        {
            SourceBackedAgentMessage.System(
                """
                You are repairing one contradictory transport pair in a route that
                Qwen already authored. Decide the semantic named-reference class
                from USER_MESSAGE. Use document only for an explicit artifact title,
                filename or path, including a title without an extension, and copy
                that exact span. Use subject for a named product, model or entity
                being researched; use none when neither applies. subject and none
                require document=null. Do not reconsider or output any other route
                field. Call the only function exactly once.
                """),
            SourceBackedAgentMessage.User(
                "USER_MESSAGE:\n"
                + userMessage
                + "\n\nPREVIOUS_INVALID_ROUTE:\n"
                + TruncateForPrompt(invalidRoute, 1_200))
        };
        var stopwatch = Stopwatch.StartNew();
        EmitRagTrace(
            "router.native.named_reference_pair_repair.start",
            ("repair_strategy", "focused_named_reference_pair"),
            ("failure_reason",
                "native_source_route_named_reference_inconsistent"),
            ("maximum_output_tokens",
                NativeRouterNamedReferenceRepairMaximumOutputTokens),
            ("prompt_chars", messages.Sum(static message =>
                message.Content?.Length ?? 0)));
        var repairCompletion = await llm.CompleteAsync(
                messages,
                BuildNativeRouterNamedReferenceRepairTools(),
                NativeRouterNamedReferenceRepairMaximumOutputTokens,
                ct,
                temperatureOverride: 0,
                requireToolCall: true)
            .ConfigureAwait(false);
        var repaired = ApplyNativeRouterNamedReferencePairRepair(
            invalidCompletion,
            repairCompletion);
        EmitRagTrace(
            "router.native.named_reference_pair_repair.end",
            ("repair_strategy", "focused_named_reference_pair"),
            ("protocol_valid", repaired is not null),
            ("prompt_tokens", repairCompletion.PromptTokens),
            ("completion_tokens", repairCompletion.CompletionTokens),
            ("ms", stopwatch.ElapsedMilliseconds));
        return repaired;
    }

    private static SourceBackedAgentCompletion?
        ApplyNativeRouterNamedReferencePairRepair(
            SourceBackedAgentCompletion invalidCompletion,
            SourceBackedAgentCompletion repairCompletion)
    {
        if (invalidCompletion.ToolCalls.Count != 1
            || repairCompletion.ToolCalls.Count != 1)
        {
            return null;
        }

        var repairCall = repairCompletion.ToolCalls[0];
        if (!string.Equals(
                repairCall.Name,
                RepairNamedReferencePairToolName,
                StringComparison.Ordinal)
            || repairCall.Arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var properties = repairCall.Arguments
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (properties.Length != 2
            || !properties.Contains(
                "namedReferenceKind",
                StringComparer.Ordinal)
            || !properties.Contains("document", StringComparer.Ordinal))
        {
            return null;
        }

        var kindElement = repairCall.Arguments.GetProperty(
            "namedReferenceKind");
        var documentElement = repairCall.Arguments.GetProperty("document");
        if (kindElement.ValueKind != JsonValueKind.String)
            return null;
        var kind = kindElement.GetString()?.Trim().ToLowerInvariant()
                   ?? string.Empty;
        if (kind is not ("none" or "subject" or "document"))
            return null;

        string? document = null;
        if (kind == "document")
        {
            if (documentElement.ValueKind != JsonValueKind.String)
                return null;
            document = documentElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(document)
                || document.Length is < 2 or > 180)
            {
                return null;
            }
        }
        else if (documentElement.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        var originalCall = invalidCompletion.ToolCalls[0];
        if (JsonNode.Parse(originalCall.Arguments.GetRawText())
            is not JsonObject repairedArguments)
        {
            return null;
        }
        repairedArguments["namedReferenceKind"] = kind;
        repairedArguments["document"] = document is null
            ? null
            : JsonValue.Create(document);
        var repairedCall = originalCall with
        {
            Arguments = JsonSerializer.SerializeToElement(repairedArguments)
        };
        return repairCompletion with
        {
            ToolCalls = new[] { repairedCall }
        };
    }

    internal static SourceBackedAgentCompletion?
        ApplyNativeRouterNamedReferencePairRepairForTests(
            SourceBackedAgentCompletion invalidCompletion,
            SourceBackedAgentCompletion repairCompletion)
        => ApplyNativeRouterNamedReferencePairRepair(
            invalidCompletion,
            repairCompletion);
}
