using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record PromptSourceHandle(string SourceKey, string DisplayName,
        string CarrierEvidenceId, int? FirstIndexedPhysicalPage, int? LastIndexedPhysicalPage,
        int? CanonicalChunkCount, [property: JsonIgnore] AdvancedAnalysisResultEvidence SourceReference)
    {
        [JsonIgnore] public bool SecondaryTableReadingObserved { get; init; }
    }

    private static string ResolvePromptSourceKey(IDictionary<string, string> keys, string identity)
    {
        var existing = keys.Where(pair => string.Equals(pair.Key, identity, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (existing.Length > 1)
            throw new AdvancedAnalysisProviderException("advanced_source_catalogue_identity_conflict");
        if (existing.Length == 1) return existing[0];
        var next = 1;
        string key;
        do { key = $"internal-source-{next++}"; }
        while (keys.Values.Contains(key, StringComparer.Ordinal));
        keys.Add(identity, key);
        return key;
    }

    private IReadOnlyList<PromptSourceHandle> BuildPromptSourceCatalogue(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active, IDictionary<string, string> keys)
    {
        if (!_options.AdaptiveResearchEnabled && !_options.NativeResearchToolsEnabled) return [];
        var result = new List<PromptSourceHandle>();
        foreach (var group in active.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                     && !string.IsNullOrWhiteSpace(item.Reference.DocId)
                     && !string.IsNullOrWhiteSpace(item.Reference.RevisionId)
                     && !string.IsNullOrWhiteSpace(item.Reference.SourceHash)
                     && !string.IsNullOrWhiteSpace(item.Reference.DocPath))
                 .GroupBy(item => BuildCanonicalSourceIdentity(item, item.Reference.EvidenceId!), StringComparer.OrdinalIgnoreCase))
        {
            var carrier = group.First();
            if (group.Select(item => item.Reference.DocPath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                throw new AdvancedAnalysisProviderException("advanced_source_catalogue_identity_conflict");
            var key = ResolvePromptSourceKey(keys, group.Key);
            if (result.Any(item => item.SourceKey == key))
                throw new AdvancedAnalysisProviderException("advanced_source_catalogue_identity_conflict");
            var name = carrier.Reference.DocPath!.Replace('\\', '/').Split('/').Last();
            name = new string(name.Where(character => !char.IsControl(character)).Take(160).ToArray());
            var overview = group.Select(item => item.SourceOverview).FirstOrDefault(item => item is not null
                && item.FirstIndexedPhysicalPage > 0 && item.LastIndexedPhysicalPage >= item.FirstIndexedPhysicalPage
                && item.CanonicalChunkCount >= 0);
            result.Add(new(key, name, carrier.Reference.EvidenceId!, overview?.FirstIndexedPhysicalPage,
                overview?.LastIndexedPhysicalPage, overview?.CanonicalChunkCount, carrier.Reference)
            { SecondaryTableReadingObserved = group.Any(item => item.SourceOverview?.TableRegionObservations is not null
                || item.SourceObservation is not null) });
        }
        if (result.Count > Math.Clamp(_options.MaximumAccumulatedEvidenceItems, 1, 2_000))
            throw new AdvancedAnalysisProviderException("advanced_source_catalogue_active_limit_exceeded");
        return result;
    }

    private static object SourceCatalogueForPrompt(IReadOnlyList<PromptSourceHandle> sources) => new
    {
        sources,
        instruction = "Active revalidated source locators, independent of which excerpts fit this call. carrierEvidenceId identifies the admitted carrier; its absent body is not citable proof. displayName is a filename, not document contents. Indexed physical bounds summarize indexed chunks, not PDF completeness or an authorization extension. Use sourceKey with declared tools to inspect other bodies. A full working set may still admit whole results by replacing unprotected items; remainingEvidenceItems=0 does not itself forbid research. Tool, model, elapsed-time and documentary limits still apply."
    };

    private static string AddSourceCatalogueToPrompt(string userPrompt, IReadOnlyList<PromptEvidenceItem>? projection)
    {
        if (projection is not PromptEvidenceProjection { SourceCatalogueEnabled: true } current) return userPrompt;
        var prompt = JsonNode.Parse(userPrompt)!.AsObject();
        prompt["sourceCatalogue"] = JsonSerializer.SerializeToNode(SourceCatalogueForPrompt(current.SourceCatalogue), JsonOptions);
        return prompt.ToJsonString(JsonOptions);
    }

    private static string[] SourceKeysForNativeTools(JsonElement prompt)
        => (prompt.TryGetProperty("sourceCatalogue", out var catalogue)
                ? catalogue.GetProperty("sources").EnumerateArray()
                : prompt.GetProperty("evidence").EnumerateArray())
            .Select(item => ReadString(item, "sourceKey")).Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal).ToArray();

    private static bool HasObservedSecondaryTableSource(IReadOnlyList<PromptEvidenceItem>? observations, string key)
        => observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
            ? projection.SourceCatalogue.Any(source => source.SourceKey == key && source.SecondaryTableReadingObserved)
            : observations?.Any(item => item.SourceKey == key
                && (item.SourceOverview?.TableRegionObservations is not null || item.SourceObservation is not null)) == true;

    private static AdvancedAnalysisResultEvidence? ResolveObservedSource(
        IReadOnlyList<PromptEvidenceItem>? observations, string key)
    {
        if (observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection)
            return projection.SourceCatalogue.FirstOrDefault(source => source.SourceKey == key)?.SourceReference;
        var matches = observations?.Where(item => item.SourceKey == key).Select(item => item.SourceReference).ToArray() ?? [];
        if (matches.Select(item => string.Join("|", item.DocId, item.RevisionId, item.SourceHash, item.DocPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            throw new JsonException();
        return matches.FirstOrDefault();
    }
}
