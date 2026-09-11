using System.Diagnostics;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandlePostToolRagResultsSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        ToolResults toolResults,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        string entryPoint = "post_tools_rag_results")
    {
        if (!ShouldRoutePostToolRagResultsThroughSourceBackedPipeline(routerPlan, toolResults))
            return (false, string.Empty, null);

        EmitRagTrace(
            "source_backed_agent_v2.post_tools_handoff",
            ("intent", routerPlan.Intent),
            ("entry", entryPoint),
            ("reason", "legacy_tool_results_are_not_canonical_evidence"),
            ("discarded_rag_items", toolResults.Items.Count(static item =>
                item.ToolName is "rag.search" or "rag.multi_search")));
        var pipelineIntent = string.Equals(routerPlan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
            ? "rag.answer"
            : routerPlan.Intent;
        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            pipelineIntent,
            entryPoint,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleDocumentVersionTraceabilityFallbackSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage))
            return (false, string.Empty, null);

        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            "rag.answer",
            "document_version_traceability_fallback",
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleExactItemFallbackSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldRouteExactItemFallbackThroughSourceBackedRagPipeline(effectiveUserMessage))
            return (false, string.Empty, null);

        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            "rag.answer",
            "exact_item_fallback",
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private static bool ShouldRouteExactItemFallbackThroughSourceBackedRagPipeline(string effectiveUserMessage)
    {
        if (ShouldSkipExactItemPreRouterShortcut(effectiveUserMessage)
            || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage))
            return false;

        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            return false;

        if (LooksLikeStructuredItemCardRequest(effectiveUserMessage)
            || LooksLikeItemLocationLookupRequest(effectiveUserMessage))
            return true;

        var exactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var requestedExplicitDocument = ExtractExplicitDocumentFileReferenceQueries(effectiveUserMessage).FirstOrDefault();
        return LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            && (!string.IsNullOrWhiteSpace(exactItemTitle)
                || !string.IsNullOrWhiteSpace(requestedExplicitDocument));
    }

    private static bool ShouldRoutePostToolRagResultsThroughSourceBackedPipeline(
        RouterPlan routerPlan,
        ToolResults toolResults)
    {
        if (routerPlan.Intent.StartsWith("meta.", StringComparison.OrdinalIgnoreCase)
            || IsInventoryIntent(routerPlan.Intent))
            return false;

        return toolResults.Items.Any(static item =>
            string.IsNullOrWhiteSpace(item.Error)
            && item.ToolName is "rag.search" or "rag.multi_search");
    }
}
