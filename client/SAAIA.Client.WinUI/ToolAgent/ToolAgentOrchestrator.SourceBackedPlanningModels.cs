using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int ResolveSourceBackedPlanningTargetItemCount(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var requestedDays = DetectRequestedDayAxisLabels(query, "en");
            var requestedAxes = DetectRequestedPlanningSlotAxisLabels(query, "en")
                .Concat(ExtractPlanningSlotRetrievalTerms(query))
                .Select(NormalizeLexicalLookup)
                .Where(static term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedDays.Count > 0 && requestedAxes.Length > 0)
                return Math.Clamp(requestedDays.Count * requestedAxes.Length, 1, 20);

            var explicitCount = Regex.Match(
                normalized,
                @"\b(?<n>\d{1,2})\s+(?:jours?|days?|items?|elements?|options?|etapes?|steps?)\b",
                RegexOptions.CultureInvariant);
            if (explicitCount.Success
                && int.TryParse(explicitCount.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && count is >= 1 and <= 20)
            {
                return count;
            }
        }

        return LooksLikeWeeklyPlanningRequest(query) ? 7 : 5;
    }

    private sealed record SourceBackedOptionCandidate(RagHitSummary Hit, string Title, int Score, int? VisibleMinutes);

    private sealed record SourceBackedPlanningDraft(
        string Answer,
        IReadOnlyList<SourceBackedOptionCandidate> Items,
        IReadOnlyList<ToolMemory.SourceRef> Sources)
    {
        public static SourceBackedPlanningDraft Empty { get; } = new(
            string.Empty,
            Array.Empty<SourceBackedOptionCandidate>(),
            Array.Empty<ToolMemory.SourceRef>());
    }

    private sealed record SourceBackedOptionAnswerSelection(
        IReadOnlyList<SourceBackedOptionCandidate> Items,
        bool WantsTotalPairing,
        bool HasCertifiedTotalPair,
        int? RequestedMaxMinutes,
        int? VisibleTotalMinutes);

    private sealed record SourceBackedCountdownCandidate(RagHitSummary Hit, string Title, int Score, int? VisibleMinutes);
}
