using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeRepeatableCollectionEvidenceForPrompt(
        AdvancedAnalysisLoadDescriptor load, IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId)
    {
        if (evidence.Count < 2 || !load.StructuredLayout || load.AnswerUnitCount < 2
            || ResolveSelectionMode(load) != "repeatable_named_items" || load.BoundedNamedDocumentExtraction
            || HasFixedSubjectFactFamily(load)) return evidence;

        var representatives = ProjectionCopyRepresentatives(evidence, retrievalQueriesByEvidenceId);
        var queries = evidence.SelectMany(item => item.Reference.EvidenceId is { } id
                && retrievalQueriesByEvidenceId.TryGetValue(id, out var found) ? found.AsEnumerable() : Enumerable.Empty<string>())
            .Where(query => !string.IsNullOrWhiteSpace(query)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        Queue<int> QueueFor(string query)
        {
            var tokens = ExtractEvidenceRankingTokens(query, null);
            return new(Enumerable.Range(0, evidence.Count).Where(index => evidence[index].Reference.EvidenceId is { } id
                    && retrievalQueriesByEvidenceId.TryGetValue(id, out var found) && found.Contains(query, StringComparer.OrdinalIgnoreCase))
                .OrderBy(index => RetrievalContentClassifier.AnalyzeEvidenceContent(evidence[index].Content,
                    evidence[index].ExactTitle).ContentRole switch
                {
                    RetrievalContentClassifier.ContentRole => 0,
                    RetrievalContentClassifier.MixedNavigationContentRole => 1,
                    _ => 2
                })
                .ThenByDescending(index => ScoreEvidenceText(evidence[index].Content, tokens))
                .ThenByDescending(index => !string.IsNullOrWhiteSpace(evidence[index].ExactTitle))
                .ThenBy(index => index));
        }
        var targets = queries.ToDictionary(query => query,
            query => ResolveTargetColumnsForPrompt(load, [query]), StringComparer.OrdinalIgnoreCase);
        var groups = load.Columns.Select(column => queries.Where(query => targets[query]
                .Contains(column, StringComparer.OrdinalIgnoreCase)).Select(QueueFor).ToArray())
            .Where(group => group.Length > 0).ToList();
        // A query without a declared target still gets its own turn. No document
        // diversity or domain-specific suitability is inferred from this order.
        groups.AddRange(queries.Where(query => targets[query].Count == 0).Select(query => new[] { QueueFor(query) }));
        var cursors = new int[groups.Count];
        var selected = new List<AdvancedAnalysisResolvedEvidence>(evidence.Count);
        var seen = new HashSet<int>();
        while (true)
        {
            var progress = false;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                for (var attempt = 0; attempt < group.Length; attempt++)
                {
                    var queryIndex = (cursors[groupIndex] + attempt) % group.Length;
                    var queue = group[queryIndex];
                    while (queue.Count > 0 && seen.Contains(representatives[queue.Peek()])) queue.Dequeue();
                    if (queue.Count == 0) continue;
                    var representative = representatives[queue.Dequeue()];
                    seen.Add(representative); selected.Add(evidence[representative]);
                    cursors[groupIndex] = (queryIndex + 1) % group.Length; progress = true; break;
                }
            }
            if (!progress) break;
        }
        foreach (var index in Enumerable.Range(0, evidence.Count))
            if (representatives[index] == index && seen.Add(index)) selected.Add(evidence[index]);
        // Retain every original ID, title, structural locator and complete text.
        // Copies follow the alternatives; the existing budget decides visibility.
        // Later explicit/window/RAW priorities are still applied by the caller.
        foreach (var index in Enumerable.Range(0, evidence.Count))
            if (seen.Add(index)) selected.Add(evidence[index]);
        return selected;
    }

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
