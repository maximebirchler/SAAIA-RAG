using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<bool> TryExpandBackendGuidanceClarificationRetrievalAsync(
        ToolResults toolResults,
        RouterPlan plan,
        string effectiveUserMessage,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onProgress)
    {
        if (!HasBackendGuidanceClarification(toolResults)
            || !ShouldExpandDocumentaryProbeRetrieval(toolResults, effectiveUserMessage, plan.Language))
        {
            EmitRagTrace(
                "backend_guidance.expansion.skipped",
                ("has_guidance", HasBackendGuidanceClarification(toolResults)),
                ("query", effectiveUserMessage));
            return false;
        }

        var queries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage);
        if (queries.Length == 0)
        {
            EmitRagTrace(
                "backend_guidance.expansion.skipped",
                ("reason", "no_queries"),
                ("query", effectiveUserMessage));
            return false;
        }

        var args = CreateJsonArgs(new
        {
            queries,
            topK = ResolveDocumentaryProbeTopK(effectiveUserMessage),
            category = ResolveRagCategoryScope(effectiveUserMessage),
            mode = "broad",
            researchMode = "source_exploration",
            includeResearchSurfaces = true
        });

        var sw = Stopwatch.StartNew();
        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressSearchSourceBackedCandidates(plan.Language));
            EmitRagTrace(
                "backend_guidance.expansion.start",
                ("queries", queries),
                ("query", effectiveUserMessage),
                ("top_k", ResolveDocumentaryProbeTopK(effectiveUserMessage)),
                ("category", ResolveRagCategoryScope(effectiveUserMessage)));
            var expandedResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
            sw.Stop();
            if (!HasRagHits(expandedResult))
            {
                EmitRagTrace(
                    "backend_guidance.expansion.end",
                    ("accepted", false),
                    ("reason", "no_hits"),
                    ("hits", 0),
                    ("ms", sw.ElapsedMilliseconds));
                return false;
            }

            var candidate = new ToolResults();
            candidate.Items.AddRange(toolResults.Items);
            candidate.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });

            if (!IsBetterDocumentaryProbeCoverage(toolResults, candidate, effectiveUserMessage, plan.Language))
            {
                EmitRagTrace(
                    "backend_guidance.expansion.end",
                    ("accepted", false),
                    ("reason", "no_coverage_gain"),
                    ("hits", CountRagHits(expandedResult)),
                    ("ms", sw.ElapsedMilliseconds));
                return false;
            }

            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });
            _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
            if (!_mem.LastToolNames.Contains("rag.multi_search", StringComparer.OrdinalIgnoreCase))
                _mem.LastToolNames.Add("rag.multi_search");
            EmitRagTrace(
                "backend_guidance.expansion.end",
                ("accepted", true),
                ("hits", CountRagHits(expandedResult)),
                ("ms", sw.ElapsedMilliseconds));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "backend_guidance.expansion.end",
                ("accepted", false),
                ("reason", "error"),
                ("ms", sw.ElapsedMilliseconds));
            return false;
        }
    }

    private static bool HasBackendGuidanceClarification(ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(static x => x.ToolName is "rag.search" or "rag.multi_search"))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("guidance", out var guidance)
                || guidance.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var behavior = TryGetString(guidance, "behavior") ?? TryGetString(guidance, "Behavior");
            var responseShape = TryGetString(guidance, "responseShape") ?? TryGetString(guidance, "ResponseShape");
            if (string.Equals(behavior, "ask_clarification", StringComparison.OrdinalIgnoreCase)
                || string.Equals(responseShape, "clarify", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
