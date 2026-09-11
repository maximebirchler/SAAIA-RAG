using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<ToolResults> ExecuteToolsAsync(
        RouterPlan plan,
        string userMessage,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onProgress,
        bool skipInitialSourceBackedCategoryPlanning = false)
    {
        var results = new ToolResults();

        foreach (var call in plan.ToolCalls)
        {
            if (plan.Origin == RouterPlanOrigin.Llm
                && (string.Equals(NormalizeToolName(call.Name), "rag.search", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeToolName(call.Name), "rag.multi_search", StringComparison.OrdinalIgnoreCase)))
            {
                call.Args = TrustResolvedLlmRagCategoryScopeArg(call.Args);
            }

            if (!skipInitialSourceBackedCategoryPlanning
                && plan.Origin == RouterPlanOrigin.Llm
                && string.Equals(NormalizeToolName(call.Name), "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                call.Args = await TryApplyInitialLlmSourceBackedCategoryScopeArgAsync(
                        plan,
                        call.Args,
                        userMessage,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
            }

            onPhase?.Invoke(PhaseLabelForTool(call.Name, plan.Language));
            onProgress?.Invoke(DescribeToolAction(call.Name, userMessage, plan.Language, call.Args));

            var sw = Stopwatch.StartNew();
            EmitRagTrace(
                "tool.start",
                ("name", call.Name),
                ("args", call.Args.GetRawText()));
            ClientLog.Info(
                "ToolAgent tool start: " +
                $"name={call.Name}|args={TruncateForPrompt(call.Args.GetRawText(), 420)}");
            try
            {
                if (!ToolManifest.IsKnownTool(call.Name))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=unknown_tool|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "unknown_tool"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }

                if (ToolManifest.IsAdminTool(call.Name) && !_api.HasAdminKey)
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "admin_required",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"admin_required\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=admin_required|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "admin_required"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }

                var handlers = GetOrCreateToolHandlers();
                if (!handlers.TryGetValue(call.Name, out var handler))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=unknown_tool_handler|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "unknown_tool_handler"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }

                JsonElement res = await handler(call.Args, ct).ConfigureAwait(false);

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Result = res,
                    DurationMs = sw.ElapsedMilliseconds
                });
                ClientLog.Info(
                    "ToolAgent tool end: " +
                    $"name={call.Name}|ok=true|ms={sw.ElapsedMilliseconds}|resultKind={res.ValueKind}|preview={TruncateForPrompt(res.GetRawText(), 520)}");
                EmitRagTrace(
                    "tool.end",
                    ("name", call.Name),
                    ("ok", true),
                    ("result_kind", res.ValueKind.ToString()),
                    ("result_chars", res.GetRawText().Length),
                    ("ms", sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                var structuredError = ClassifyToolExecutionError(ex, ToolManifest.IsAdminTool(call.Name));
                var effectiveError = string.IsNullOrWhiteSpace(structuredError) ? ex.Message : structuredError;
                var resultError = string.IsNullOrWhiteSpace(structuredError) ? "tool_failed" : structuredError;
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Error = effectiveError,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = JsonDocument.Parse($"{{\"error\":\"{resultError}\"}}").RootElement
                });
                ClientLog.Info(
                    "ToolAgent tool end: " +
                    $"name={call.Name}|ok=false|error={TruncateForPrompt(effectiveError, 260)}|ms={sw.ElapsedMilliseconds}");
                EmitRagTrace(
                    "tool.end",
                    ("name", call.Name),
                    ("ok", false),
                    ("error", effectiveError),
                    ("ms", sw.ElapsedMilliseconds));
            }
        }

        return results;
    }
}
