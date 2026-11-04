using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.Core.Sources;

namespace SAAIA.Client.Core.AdvancedAnalysis;

public sealed record AdvancedAnalysisCitationLink(string Label, SourceLocator Locator);

/// <summary>Resolves persisted claim markers only through that message's exact source identities.</summary>
public static class AdvancedAnalysisCitationMap
{
    private static readonly Regex ClaimId = new(@"^C[1-9][0-9]{0,2}$", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, IReadOnlyList<AdvancedAnalysisCitationLink>> Parse(string? sourcesJson)
    {
        var empty = new Dictionary<string, IReadOnlyList<AdvancedAnalysisCitationLink>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(sourcesJson) || sourcesJson.Length > 4_194_304) return empty;
        try
        {
            using var document = JsonDocument.Parse(sourcesJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "intent") != "advanced_analysis.answer"
                || !root.TryGetProperty("advancedAnalysis", out var state) || state.ValueKind != JsonValueKind.Object
                || Text(state, "schemaVersion") != "saaia.advanced-analysis-client-state.v1"
                || Text(state, "status") != "succeeded"
                || !state.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array
                || claims.GetArrayLength() > 512
                || !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array
                || sources.GetArrayLength() > 256) return empty;
            var origin = Text(state, "originTraceId");
            var displayedSources = sources;
            if (state.TryGetProperty("citationSources", out var completeSources))
            {
                if (completeSources.ValueKind != JsonValueKind.Array || completeSources.GetArrayLength() > 256) return empty;
                sources = completeSources;
            }
            var references = new Dictionary<string, AdvancedAnalysisCitationLink>(StringComparer.Ordinal);
            foreach (var source in sources.EnumerateArray())
            {
                var id = Text(source, "evidenceId");
                var path = Text(source, "docPath");
                var hash = Text(source, "sourceHash");
                var revision = Text(source, "revisionId");
                if (id.Length is < 1 or > 200 || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hash)
                    || !Guid.TryParse(Text(source, "docId"), out var doc) || doc == Guid.Empty
                    || !Guid.TryParse(revision, out var rev) || rev == Guid.Empty
                    || !Integer(source, "pageStart", out var first) || first < 1
                    || !Integer(source, "pageEnd", out var last) || last < first) return empty;
                if (!displayedSources.EnumerateArray().Any(card =>
                    string.Equals(Text(card, "docId"), Text(source, "docId"), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Text(card, "revisionId"), revision, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Text(card, "sourceHash"), hash, StringComparison.OrdinalIgnoreCase)
                    && Text(card, "docPath") == path && Integer(card, "pageStart", out var start) && start == first
                    && Integer(card, "pageEnd", out var end) && end == last)) return empty;
                var name = Text(source, "docName");
                if (string.IsNullOrWhiteSpace(name)) name = path.Replace('\\', '/').Split('/').Last();
                var label = first == last ? $"{name} — p. {first}" : $"{name} — pp. {first}-{last}";
                var locator = new SourceLocator(path, first, hash, revision,
                    NullText(source, "chunkId"), NullText(source, "anchorId"), NullText(source, "contentCardId"),
                    RequireExactSourceHash: true, OriginTraceId: string.IsNullOrWhiteSpace(origin) ? null : origin,
                    PageEnd: last);
                if (!references.TryAdd(id, new(label, locator))) return empty;
            }
            var result = new Dictionary<string, IReadOnlyList<AdvancedAnalysisCitationLink>>(StringComparer.Ordinal);
            foreach (var claim in claims.EnumerateArray())
            {
                var id = Text(claim, "claimId");
                if (!ClaimId.IsMatch(id) || !int.TryParse(id.AsSpan(1), out var ordinal) || ordinal > 512
                    || !claim.TryGetProperty("evidenceIds", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array
                    || identifiers.GetArrayLength() is < 1 or > 64 || result.ContainsKey(id)) return empty;
                var used = new HashSet<string>(StringComparer.Ordinal);
                var links = new List<AdvancedAnalysisCitationLink>();
                foreach (var identifier in identifiers.EnumerateArray())
                {
                    if (identifier.ValueKind != JsonValueKind.String || !used.Add(identifier.GetString()!)
                        || !references.TryGetValue(identifier.GetString()!, out var link)) return empty;
                    links.Add(link);
                }
                result.Add(id, links.DistinctBy(link => link.Locator).ToArray());
            }
            return result;
        }
        catch (JsonException) { return empty; }
        catch (InvalidOperationException) { return empty; }
    }

    private static string Text(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string? NullText(JsonElement item, string name)
        => Text(item, name) is { Length: > 0 } text ? text : null;
    private static bool Integer(JsonElement item, string name, out int number)
    {
        number = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
