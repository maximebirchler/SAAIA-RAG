using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, IReadOnlyList<string> toolNames)> TryHandleSourcePolicyShortcutAsync(
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!LooksLikeSourceBypassOrUnsupportedInventionRequest(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (ShouldSkipExactItemPreRouterShortcut(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
            && !LooksLikeDocumentInstructionPolicyRequest(effectiveUserMessage)
            && !LooksLikeHardSourceBypassOrUnsupportedInventionRequest(effectiveUserMessage))
        {
            return (false, string.Empty, null, Array.Empty<string>());
        }

        language = NormalizeLanguageCode(language);
        onPhase?.Invoke(DeterministicAgentText.PhaseRag(language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));

        if (LooksLikeDocumentInstructionPolicyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildDocumentInstructionPolicyAnswer(language);
            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        if (LooksLikeSourceAbsentAssertionPolicyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildSourceAbsentAssertionPolicyAnswer(language);
            if (ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequest(effectiveUserMessage))
            {
                try
                {
                    var anchorArgs = CreateJsonArgs(new
                    {
                        query = NormalizeRagQueryForRetrieval(effectiveUserMessage),
                        topK = 2,
                        category = ResolveRagCategoryScope(effectiveUserMessage),
                        mode = "focused"
                    });
                    var anchorResult = await ExecRagSearchAsync(anchorArgs, ct).ConfigureAwait(false);
                    if (HasRagHits(anchorResult))
                    {
                        var anchorToolResults = new ToolResults();
                        anchorToolResults.Items.Add(new ToolResults.Item
                        {
                            ToolName = "rag.search",
                            Result = anchorResult
                        });

                        var anchorSources = DeriveSourcesFromRagHits(anchorToolResults).Take(3).ToList();
                        if (anchorSources.Count > 0)
                        {
                            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(anchorSources);
                            _mem.LastToolNames = new List<string> { "rag.search" };
                            deterministicAnswer = InjectInlineSources(deterministicAnswer, anchorSources, language);
                            var anchorSourcesPayload = BuildSourcesPayload(anchorSources);
                            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
                            onProgress?.Invoke(string.Empty);
                            return (true, deterministicAnswer, anchorSourcesPayload, new[] { "rag.search" });
                        }
                    }
                }
                catch
                {
                    // The policy answer is still valid without a source anchor.
                }
            }

            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        if (LooksLikeBinaryAnswerWithSourceUncertaintyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildBinaryAnswerWithSourceUncertaintyPolicyAnswer(language);
            try
            {
                var anchorArgs = CreateJsonArgs(new
                {
                    query = NormalizeRagQueryForRetrieval(effectiveUserMessage),
                    topK = 3,
                    category = ResolveRagCategoryScope(effectiveUserMessage),
                    mode = "focused"
                });
                var anchorResult = await ExecRagSearchAsync(anchorArgs, ct).ConfigureAwait(false);
                if (HasRagHits(anchorResult))
                {
                    var anchorToolResults = new ToolResults();
                    anchorToolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "rag.search",
                        Result = anchorResult
                    });

                    var anchorSources = DeriveSourcesFromRagHits(anchorToolResults).Take(4).ToList();
                    if (anchorSources.Count > 0)
                    {
                        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(anchorSources);
                        _mem.LastToolNames = new List<string> { "rag.search" };
                        deterministicAnswer = InjectInlineSources(deterministicAnswer, anchorSources, language);
                        var anchorSourcesPayload = BuildSourcesPayload(anchorSources);
                        await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
                        onProgress?.Invoke(string.Empty);
                        return (true, deterministicAnswer, anchorSourcesPayload, new[] { "rag.search" });
                    }
                }
            }
            catch
            {
                // The policy answer is still valid without a source anchor.
            }

            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        var answer = BuildSourcePolicyGuardPrefix(language);
        await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        return (true, answer, null, Array.Empty<string>());
    }


    private static bool ShouldSkipExactItemPreRouterShortcut(string effectiveUserMessage)
        => LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           || ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);

    private static bool ShouldTryPreciseMultiSearchForExactItem(
        JsonElement ragResult,
        string effectiveUserMessage,
        string exactItemTitle,
        string? requestedExplicitDocument,
        bool isCompactTechnicalExactItem,
        bool isDocumentVersionTraceabilityRequest)
    {
        var hasInitialHits = HasRagHits(ragResult);
        if (!hasInitialHits)
            return true;

        if (isDocumentVersionTraceabilityRequest)
            return false;

        var explicitDocumentHasInitialHits = !string.IsNullOrWhiteSpace(requestedExplicitDocument)
            && RagResultContainsExplicitDocumentHit(ragResult, requestedExplicitDocument!);
        if (!string.IsNullOrWhiteSpace(requestedExplicitDocument)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            && !explicitDocumentHasInitialHits)
        {
            return true;
        }

        var initialHasUsableRequestedTitle = !string.IsNullOrWhiteSpace(exactItemTitle)
            && RagResultContainsUsableRequestedTitle(ragResult, exactItemTitle);
        var isStructuredItemCardRequest = LooksLikeStructuredItemCardRequest(effectiveUserMessage);
        if (isStructuredItemCardRequest)
            return !initialHasUsableRequestedTitle;

        return !isCompactTechnicalExactItem && !initialHasUsableRequestedTitle;
    }


}
