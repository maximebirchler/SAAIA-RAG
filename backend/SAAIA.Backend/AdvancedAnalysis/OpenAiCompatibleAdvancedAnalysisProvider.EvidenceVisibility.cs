using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record ProjectionBodyIdentity(string DocId, string RevisionId, string SourceHash,
        string DocPath, int PageStart, int PageEnd, string Content, string? Quality, string? Overview,
        string? ChunkType, string? ChunkComposition, bool UnassignedContext);

    private static ProjectionBodyIdentity? ProjectionBodyKey(AdvancedAnalysisResolvedEvidence item,
        Dictionary<object, string> metadata)
    {
        var reference = item.Reference;
        // These are separate observation/window capabilities, even when their
        // currently visible words happen to match a primary excerpt.
        if (item.SourceObservation is not null || item.NativeObservation is not null
            || reference.SourceObservation is not null || reference.NativeObservation is not null
            || item.TableFragment is not null || item.TableReadWindow is not null
            || string.IsNullOrWhiteSpace(reference.DocId) || string.IsNullOrWhiteSpace(reference.RevisionId)
            || string.IsNullOrWhiteSpace(reference.SourceHash) || string.IsNullOrEmpty(item.Content)
            || reference.PageStart < 1 || reference.PageEnd < reference.PageStart)
            return null;

        string? Metadata(object? value)
        {
            if (value is null) return null;
            if (!metadata.TryGetValue(value, out var json))
                metadata[value] = json = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
            return json;
        }
        return new(reference.DocId, reference.RevisionId, reference.SourceHash, reference.DocPath,
            reference.PageStart, reference.PageEnd, item.Content, Metadata(item.SourceQuality), Metadata(item.SourceOverview),
            item.SourceStructure?.ChunkType, item.SourceStructure?.ChunkComposition,
            item.SourceStructure?.HasUnassignedPhysicalContext ?? false);
    }

    internal static bool CanDeferExactProjectionCopy(AdvancedAnalysisResolvedEvidence first,
        AdvancedAnalysisResolvedEvidence second)
    {
        var metadata = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var firstKey = ProjectionBodyKey(first, metadata);
        return firstKey is not null && firstKey == ProjectionBodyKey(second, metadata)
            && (string.IsNullOrWhiteSpace(first.ExactTitle) || string.IsNullOrWhiteSpace(second.ExactTitle)
                || string.Equals(first.ExactTitle, second.ExactTitle, StringComparison.Ordinal));
    }

    private static int[] ProjectionCopyRepresentatives(IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId)
    {
        var representatives = Enumerable.Range(0, evidence.Count).ToArray();
        var metadata = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var groups = Enumerable.Range(0, evidence.Count)
            .Select(index => (Index: index, Key: ProjectionBodyKey(evidence[index], metadata)))
            .Where(item => item.Key is not null).GroupBy(item => item.Key!);
        foreach (var group in groups)
        {
            var indexes = group.Select(item => item.Index).ToArray();
            // Two explicit different titles can identify different alternatives.
            // A titleless chunk and its exact titled card remain separate objects;
            // only their position in this bounded prompt changes.
            if (indexes.Select(index => evidence[index].ExactTitle).Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.Ordinal).Skip(1).Any()) continue;
            HashSet<string> Queries(int index) => evidence[index].Reference.EvidenceId is { } id
                && retrievalQueriesByEvidenceId.TryGetValue(id, out var queries)
                ? queries.Where(query => !string.IsNullOrWhiteSpace(query)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            var allQueries = indexes.SelectMany(index => Queries(index)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasTitle = indexes.Any(index => !string.IsNullOrWhiteSpace(evidence[index].ExactTitle));
            // Never synthesize a union on a different evidence ID. If no original
            // carries all observed query roles and the known title, keep the
            // original rows separate in the fair turns.
            var eligible = indexes.Where(index => Queries(index).IsSupersetOf(allQueries)
                && (!hasTitle || !string.IsNullOrWhiteSpace(evidence[index].ExactTitle))).ToArray();
            if (eligible.Length == 0) continue;
            var first = eligible.OrderByDescending(index => !string.IsNullOrWhiteSpace(evidence[index].ExactTitle))
                .ThenBy(index => index).First();
            foreach (var index in indexes) representatives[index] = first;
        }
        return representatives;
    }

    private static bool HasUnprojectedCanonicalContent(PromptEvidenceItem evidence)
    {
        if (evidence.OriginalContentLength <= 0) return false;
        var ranges = evidence.SupportingContentWindows.Select(window =>
                (Start: window.ContentOffset, Length: window.Content.Length))
            .Append((Start: evidence.ContentOffset, Length: evidence.Content.Length))
            .Where(range => range.Length > 0).OrderBy(range => range.Start);
        long covered = 0;
        foreach (var range in ranges)
        {
            if (range.Start > covered) return true;
            covered = Math.Max(covered, (long)range.Start + range.Length);
            if (covered >= evidence.OriginalContentLength) return false;
        }
        return covered < evidence.OriginalContentLength;
    }
}
