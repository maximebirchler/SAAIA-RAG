using System.Text.Json;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static object BuildResearchStallFeedback(
        IReadOnlyList<AdvancedAnalysisSearchRequest> searches,
        IReadOnlyDictionary<string, EvidenceReadWindow>? reads,
        IReadOnlyList<PromptEvidenceItem> observations, bool finalSynthesisRequired)
    {
        var windows = (reads ?? new Dictionary<string, EvidenceReadWindow>())
            .Select(pair => (pair, excerpt: observations.FirstOrDefault(item => item.EvidenceId == pair.Key)))
            .Where(item => item.excerpt is not null)
            .Select(item => new
            {
                evidenceId = item.pair.Key,
                offset = item.pair.Value.Offset,
                requestedLength = item.pair.Value.Length,
                originalContentLength = item.excerpt!.OriginalContentLength,
                requestedWindowVisible = IsRequestedEvidenceWindowVisible(item.excerpt,
                    JsonSerializer.SerializeToElement(new { offset = item.pair.Value.Offset, length = item.pair.Value.Length })),
                wholeExcerptVisible = IsRequestedEvidenceWindowVisible(item.excerpt,
                    JsonSerializer.SerializeToElement(new { offset = 0, length = item.excerpt.OriginalContentLength })),
                nextContentOffset = item.excerpt.NextContentOffset
            }).ToArray();
        return new
        {
            reasonCode = searches.Count > 0 ? "search_already_executed"
                : reads?.Count > 0 ? "evidence_window_already_available" : "research_state_unchanged",
            searches = BuildPriorSearchesForPrompt(searches, observations),
            evidenceReads = windows,
            finalSynthesisRequired,
            instruction = finalSynthesisRequired
                ? "Repeated operations made no documentary progress. This is the final synthesis turn for this phase; no research or state-update tools are available. Return a terminal JSON result using only currently visible, revalidated evidence. Preserve useful supported claims in an explicitly partial insufficient_documentation answer and explain the unresolved gaps and bounded research. Do not infer corpus absence or invent missing choices. Use answered only if the full request is supported. The normal citation, identity and review checks still apply."
                : "This batch did not change the available research state. Listed searches already ran; listed cached windows were already stored. Check requestedWindowVisible before citing. wholeExcerptVisible means the entire canonical excerpt is already present: requesting a larger length cannot reveal another page or document. Do not repeat these operations. For a different fact choose a different declared research route, source location or genuinely missing window within budget, or return a supported terminal result, including a useful explicitly partial answer. Lack of a visible passage does not prove corpus absence."
        };
    }
}
