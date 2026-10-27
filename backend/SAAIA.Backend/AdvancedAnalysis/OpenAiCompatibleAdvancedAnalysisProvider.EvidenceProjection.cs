using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed class PromptEvidenceProjection(IList<PromptEvidenceItem> items, int inputCount)
        : ReadOnlyCollection<PromptEvidenceItem>(items)
    {
        public int InputCount { get; } = inputCount;
    }

    // This runs after the source-specific enrichments, against the actual user
    // collection that will be serialized for this call. The original projection
    // travels explicitly so that role selection and remembered evidence cannot
    // be mistaken for source absence or for a smaller canonical input.
    private static string AddEvidenceProjectionToPrompt(string userPrompt,
        IReadOnlyList<PromptEvidenceItem>? canonicalProjection)
    {
        var prompt = JsonNode.Parse(userPrompt)!.AsObject();
        var collection = prompt["evidence"] is JsonArray ? "evidence"
            : prompt["observations"] is JsonArray ? "observations" : null;
        if (collection is null)
        {
            prompt["evidenceProjection"] = JsonSerializer.SerializeToNode(new
            {
                status = "not_applicable", instruction = "This call has no documentary collection. No corpus count is asserted."
            }, JsonOptions);
            return prompt.ToJsonString(JsonOptions);
        }
        if (canonicalProjection is not PromptEvidenceProjection projection)
            throw new AdvancedAnalysisProviderException("advanced_evidence_projection_context_missing");

        var visible = prompt[collection]!.AsArray().Select(item => item!.AsObject()).ToArray();
        static (string? EvidenceId, string SourceKey) Identity(JsonObject item)
            => (item["evidenceId"]?.GetValue<string>(), item["sourceKey"]?.GetValue<string>() ?? string.Empty);
        var available = projection.GroupBy(item => (item.EvidenceId, item.SourceKey))
            .ToDictionary(group => group.Key, group => group.Count());
        var matched = 0;
        foreach (var item in visible)
        {
            var identity = Identity(item);
            if (available.TryGetValue(identity, out var count) && count > 0)
            {
                matched++;
                available[identity] = count - 1;
            }
        }
        prompt["evidenceProjection"] = JsonSerializer.SerializeToNode(new
        {
            status = "observed",
            canonicalProjection = new
            {
                inputCount = projection.InputCount,
                projectedCount = projection.Count,
                omittedByPromptBudgetCount = projection.InputCount - projection.Count
            },
            currentCall = new
            {
                collection,
                visibleCount = visible.Length,
                fromCanonicalProjectionCount = matched,
                outsideCanonicalProjectionCount = visible.Length - matched,
                canonicalProjectedNotSelectedCount = projection.Count - matched,
                truncatedContentCount = visible.Count(item => item["contentTruncated"]?.GetValue<bool>() == true),
                supportingWindowsOmittedCount = visible.Sum(item => item["supportingContentWindowsOmittedCount"]?.GetValue<int>() ?? 0)
            },
            instruction = "Counts describe this bounded prompt, not corpus absence. canonicalProjection is the current revalidated input after document scoping and its prompt-budget projection. currentCall describes the actual documentary collection for this role; membership uses evidenceId plus sourceKey, not a text comparison. Role selection and remembered items outside that projection are separate from budget omissions. contentTruncated and window offsets separately identify shortened text; unseen items and gaps establish no facts."
        }, JsonOptions);
        return prompt.ToJsonString(JsonOptions);
    }
}
