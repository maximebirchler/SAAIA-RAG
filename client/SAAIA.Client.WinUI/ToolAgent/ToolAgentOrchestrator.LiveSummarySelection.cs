using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static List<SummaryChunk> SelectRepresentativeSummaryChunks(IReadOnlyList<SummaryChunk> orderedChunks, string strategy, int maxChunks)
    {
        if (orderedChunks.Count == 0 || maxChunks <= 0)
            return new List<SummaryChunk>();

        var unique = orderedChunks
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .GroupBy(x => x.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.PageStart)
            .ThenBy(x => x.ChunkIndex)
            .ToList();

        if (unique.Count <= maxChunks)
            return unique;

        var divisor = unique.Count <= 15 ? 3 : unique.Count <= 32 ? 4 : 5;
        var desired = (int)Math.Ceiling(unique.Count / (double)divisor);
        var minTarget = strategy == "about" ? 3 : Math.Min(6, maxChunks);
        var target = Math.Clamp(desired, Math.Min(minTarget, unique.Count), maxChunks);
        target = Math.Min(target, unique.Count);

        var selected = new List<SummaryChunk>();
        var seen = new HashSet<int>();
        var prioritized = unique
            .Select((chunk, index) => new { chunk, index, score = ScoreSummaryChunkForSelection(chunk) })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.chunk.PageStart)
            .ThenBy(item => item.chunk.ChunkIndex)
            .Take(Math.Max(1, target / 2))
            .ToArray();

        foreach (var item in prioritized)
        {
            if (selected.Count >= target)
                break;
            if (seen.Add(item.index))
                selected.Add(item.chunk);
        }

        for (var i = 0; i < target; i++)
        {
            var idx = target == 1
                ? 0
                : (int)Math.Round(i * (unique.Count - 1d) / (target - 1d));
            idx = Math.Clamp(idx, 0, unique.Count - 1);
            if (seen.Add(idx))
                selected.Add(unique[idx]);
        }

        if (selected.Count < target)
        {
            foreach (var item in unique.Select((chunk, index) => new { chunk, index }))
            {
                if (selected.Count >= target)
                    break;
                if (seen.Add(item.index))
                    selected.Add(item.chunk);
            }
        }

        return selected
            .OrderBy(x => x.PageStart)
            .ThenBy(x => x.ChunkIndex)
            .ToList();
    }

    private static int ScoreSummaryChunkForSelection(SummaryChunk chunk)
    {
        var score = 0;
        if (chunk.MatchedContentCards is { Count: > 0 })
            score += Math.Min(6, chunk.MatchedContentCards.Count * 2);
        if (!string.IsNullOrWhiteSpace(chunk.SelectionHintEvidenceRole))
            score += chunk.SelectionHintEvidenceRole.Equals("navigation", StringComparison.OrdinalIgnoreCase) ? -2 : 2;
        score += Math.Clamp((chunk.SelectionHintSupportScore ?? 0) / 20, 0, 5);
        score += Math.Clamp((chunk.SelectionHintActionabilityScore ?? 0) / 25, 0, 4);
        score -= Math.Clamp((chunk.SelectionHintQualityPenalty ?? 0) / 2, 0, 4);
        if (chunk.OcrRecommended || chunk.ManualReviewRecommended)
            score -= 2;
        if (chunk.OcrApplied)
            score += 1;
        if (!string.IsNullOrWhiteSpace(chunk.QualityStatus)
            && chunk.QualityStatus.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static object BuildSamplingMeta(int totalChunks, int selectedChunks)
    {
        var divisor = totalChunks <= 15 ? 3 : totalChunks <= 32 ? 4 : 5;
        return new
        {
            totalChunks,
            selectedChunks,
            divisor
        };
    }
}
