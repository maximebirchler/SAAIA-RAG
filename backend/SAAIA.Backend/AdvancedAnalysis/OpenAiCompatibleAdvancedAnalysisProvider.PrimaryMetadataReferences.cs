using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string PrimaryMetadataReferenceContract = "sourceQualityReference and sourceOverviewReference inherit exactly the metadata object at the current collection's sourceEvidenceId, attested by metadataSha256. These references describe metadata only; they never establish documentary facts or make an omitted body citable.";
    internal sealed record PrimaryMetadataReferenceScope(string EvidenceId, string SourceKey,
        AdvancedAnalysisResultEvidence Source, bool IsPrimary, string? Quality, string? Overview);

    internal sealed record PrimaryMetadataReferenceMetrics(int QualityReferences, int OverviewReferences,
        int SavedWireCharacters);

    private static IReadOnlyList<PrimaryMetadataReferenceScope> PrimaryMetadataScopes(
        IReadOnlyList<PromptEvidenceItem> evidence, Dictionary<object, string>? cache = null)
    {
        cache ??= new(ReferenceEqualityComparer.Instance);
        string? Metadata(object? value)
        {
            if (value is null) return null;
            if (!cache.TryGetValue(value, out var text))
                cache[value] = text = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
            return text;
        }
        return evidence.Where(item => !string.IsNullOrWhiteSpace(item.EvidenceId))
            .Select(item => new PrimaryMetadataReferenceScope(item.EvidenceId!, item.SourceKey,
                item.SourceReference, item.SourceObservation is null && item.NativeObservation is null
                    && item.SourceTableFragment is null && item.TableReadWindow is null,
                Metadata(item.SourceQuality), Metadata(item.SourceOverview))).ToArray();
    }

    private static string SharePrimaryMetadataForPrompt(string prompt, IReadOnlyList<PromptEvidenceItem>? evidence,
        out PrimaryMetadataReferenceMetrics metrics, Dictionary<object, string>? cache = null)
    {
        metrics = new(0, 0, 0);
        return evidence is null || evidence.Count == 0 ? prompt
            : SharePrimaryMetadataForPrompt(prompt, PrimaryMetadataScopes(evidence, cache), out metrics);
    }

    // Only the current representation changes. Canonical objects, quality
    // warnings, source capabilities and citation identities remain complete.
    internal static string SharePrimaryMetadataForPrompt(string prompt,
        IReadOnlyList<PrimaryMetadataReferenceScope> scopes, out PrimaryMetadataReferenceMetrics metrics)
    {
        metrics = new(0, 0, 0);
        var root = JsonNode.Parse(prompt) as JsonObject;
        if (root is null) return prompt;
        var canonical = scopes.GroupBy(item => (item.EvidenceId, item.SourceKey))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var quality = 0;
        var overview = 0;
        var changed = false;
        foreach (var collection in new[] { "evidence", "observations" })
        {
            if (root[collection] is not JsonArray rows) continue;
            var objects = rows.OfType<JsonObject>().ToArray();
            var byId = objects.Where(item => Text(item, "evidenceId") is not null)
                .GroupBy(item => Text(item, "evidenceId")!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            PrimaryMetadataReferenceScope? Scope(JsonObject item)
                => Text(item, "evidenceId") is { } id && byId.ContainsKey(id)
                    && Text(item, "sourceKey") is { } source && canonical.TryGetValue((id, source), out var scope)
                    && scope.IsPrimary && ValidSource(scope.Source) ? scope : null;
            foreach (var field in new[] { "sourceQuality", "sourceOverview" })
            {
                var referenceField = field + "Reference";
                var referencePath = "current_user." + collection + "[evidenceId]." + field;
                string? Metadata(PrimaryMetadataReferenceScope scope) => field == "sourceQuality" ? scope.Quality : scope.Overview;
                bool EqualScope(PrimaryMetadataReferenceScope first, PrimaryMetadataReferenceScope second)
                    => first.SourceKey == second.SourceKey && SameSource(first.Source, second.Source)
                        && Metadata(first) is { } a && Metadata(second) is { } b
                        && string.Equals(a, b, StringComparison.Ordinal);

                // Reordering a previously compacted collection must not orphan
                // a reference or let it silently acquire different warnings.
                foreach (var item in objects.Where(item => item.ContainsKey(referenceField)))
                {
                    if (item[field] is not null || item[referenceField] is not JsonObject reference
                        || reference.Count != 3 || Text(reference, "reference") != referencePath
                        || Text(reference, "sourceEvidenceId") is not { } carrierId
                        || !byId.TryGetValue(carrierId, out var carrier) || carrier[field] is not JsonObject value
                        || carrier.ContainsKey(referenceField)
                        || Text(reference, "metadataSha256") != PrimaryMetadataValueHash(value)
                        || Scope(item) is not { } scope
                        || Scope(carrier) is not { } carrierScope || !EqualScope(scope, carrierScope))
                        throw new AdvancedAnalysisProviderException("advanced_primary_metadata_reference_invalid");
                    item[field] = value.DeepClone();
                    item.Remove(referenceField);
                    changed = true;
                }
                var carriers = new List<(JsonObject Item, PrimaryMetadataReferenceScope Scope, JsonObject Value)>();
                foreach (var item in objects)
                {
                    if (Scope(item) is not { } scope || item[field] is not JsonObject value) continue;
                    var carrier = carriers.FirstOrDefault(previous => EqualScope(scope, previous.Scope)
                        && JsonNode.DeepEquals(value, previous.Value));
                    if (carrier.Item is null)
                    {
                        carriers.Add((item, scope, (JsonObject)value.DeepClone()));
                        continue;
                    }
                    var reference = new JsonObject
                    {
                        ["sourceEvidenceId"] = carrier.Scope.EvidenceId,
                        ["reference"] = referencePath,
                        ["metadataSha256"] = PrimaryMetadataValueHash(carrier.Value)
                    };
                    var originalField = new JsonObject { [field] = value.DeepClone() };
                    var sharedField = new JsonObject { [referenceField] = reference.DeepClone() };
                    if (MeasureEscapedProviderWireTextCharacters(sharedField.ToJsonString(JsonOptions))
                        >= MeasureEscapedProviderWireTextCharacters(originalField.ToJsonString(JsonOptions))) continue;
                    item.Remove(field);
                    item[referenceField] = reference;
                    changed = true;
                    if (field == "sourceQuality") quality++; else overview++;
                }
            }
        }
        if (!changed) return prompt;
        var result = root.ToJsonString(JsonOptions);
        metrics = new(quality, overview, Math.Max(0,
            MeasureEscapedProviderWireTextCharacters(prompt) - MeasureEscapedProviderWireTextCharacters(result)));
        return result;
    }

    private static string? Text(JsonObject item, string name)
        => item[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string PrimaryMetadataValueHash(JsonNode value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions))))
            .ToLowerInvariant();

    private static bool ValidSource(AdvancedAnalysisResultEvidence source)
        => !string.IsNullOrWhiteSpace(source.DocId) && !string.IsNullOrWhiteSpace(source.RevisionId)
            && source.SourceHash is { Length: 64 } && source.SourceHash.All(Uri.IsHexDigit)
            && !string.IsNullOrWhiteSpace(source.DocPath);

    private static bool SameSource(AdvancedAnalysisResultEvidence first, AdvancedAnalysisResultEvidence second)
        => string.Equals(first.DocId, second.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.RevisionId, second.RevisionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.SourceHash, second.SourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AdvancedAnalysisToolGateway.NormalizePath(first.DocPath),
                AdvancedAnalysisToolGateway.NormalizePath(second.DocPath), StringComparison.Ordinal);
}
