using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int MaximumOmittedEvidenceReadHandles = 64;

    // A locator disclosed by the current native read result, never an excerpt
    // or a new citation. Keep no historical text and do not retain retired IDs.
    private sealed record OmittedEvidenceReadHandle(string EvidenceId, string SourceKey,
        int PhysicalPageStart, int PhysicalPageEnd, int OriginalContentLength,
        [property: JsonIgnore] AdvancedAnalysisResultEvidence SourceReference);

    private static Dictionary<string, AdvancedAnalysisResolvedEvidence> CurrentCanonicalEvidenceById(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
        => active.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId) && !string.IsNullOrEmpty(item.Content))
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .Where(group => group.All(item => SameCanonicalReadReference(group.First().Reference, item.Reference)
                && string.Equals(group.First().Content, item.Content, StringComparison.Ordinal)
                && item.SourceObservation is null && item.NativeObservation is null))
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static IReadOnlyList<OmittedEvidenceReadHandle> BuildOmittedEvidenceReadHandles(
        NativeResearchTurn? turn, IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
        => BuildOmittedEvidenceReadHandlesCore(turn, observations, active,
            observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
                ? projection.SourceCatalogue : null);

    private static IReadOnlyList<OmittedEvidenceReadHandle> BuildOmittedEvidenceReadHandlesCore(
        NativeResearchTurn? turn, IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active,
        IReadOnlyList<PromptSourceHandle>? sourceCatalogue)
    {
        if (turn is null || turn.Rejection is not null || sourceCatalogue is null)
            return [];
        var current = CurrentCanonicalEvidenceById(active);
        var visible = observations.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var handles = new List<OmittedEvidenceReadHandle>();
        using var calls = JsonDocument.Parse(turn.CallsJson);
        foreach (var call in calls.RootElement.EnumerateArray())
        {
            var callId = ReadString(call, "id");
            if (turn.DeferredCallIds.Contains(callId)
                || ReadString(call.GetProperty("function"), "name") != "read_source"
                || !turn.CallRequests.TryGetValue(callId, out var read) || read.Operation != "read_source"
                || read.NativeObservationView is not null || read.TableObservationView is not null
                || !turn.CallSourceScopes.TryGetValue(callId, out var scope)
                || !turn.Results.TryGetValue(BuildSearchIdentity(read), out var result)
                || result.ResourceLimit is not null || result.NotExecutedAfterResourceLimit
                || result.AlternativeNativeReadings?.View is not null
                || result.AlternativeTableReadings?.View is not null
                || result.Evidence.Any(item => !MatchesBoundReadSource(item.Reference, scope)))
                continue;

            // Match the renderer's already disclosed omittedEvidenceIds.Take(8),
            // before filtering. Never silently disclose a later, unlisted ID.
            var disclosed = result.Evidence.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                    && !visible.Contains(item.Reference.EvidenceId))
                .DistinctBy(item => item.Reference.EvidenceId, StringComparer.Ordinal).Take(8);
            foreach (var item in disclosed)
            {
                var id = item.Reference.EvidenceId!;
                if (!current.TryGetValue(id, out var canonical)
                    || item.SourceObservation is not null || item.NativeObservation is not null
                    || canonical.SourceObservation is not null || canonical.NativeObservation is not null
                    || !SameCanonicalReadReference(item.Reference, canonical.Reference)
                    || string.IsNullOrEmpty(canonical.Content)
                    || !string.Equals(item.Content, canonical.Content, StringComparison.Ordinal)
                    || CurrentNativeSourceKey(sourceCatalogue, canonical.Reference) is not { } key
                    || handles.Any(handle => handle.EvidenceId == id))
                    continue;
                handles.Add(new(id, key, canonical.Reference.PageStart, canonical.Reference.PageEnd,
                    canonical.Content.Length, canonical.Reference));
                if (handles.Count == MaximumOmittedEvidenceReadHandles) return handles;
            }
        }
        return handles;
    }

    private static string AddOmittedEvidenceReadHandlesToPrompt(string userPrompt,
        IReadOnlyList<PromptEvidenceItem> observations, SynthesisResearchContext context)
    {
        context.OmittedEvidenceReadTurn = context.NativeTurn;
        context.OmittedEvidenceReadHandles = BuildOmittedEvidenceReadHandles(
            context.NativeTurn, observations, context.Tools.Evidence);
        var prompt = JsonNode.Parse(userPrompt)!.AsObject();
        prompt.Remove("omittedEvidenceReadHandles");
        if (context.OmittedEvidenceReadHandles.Count > 0)
            prompt["omittedEvidenceReadHandles"] = JsonSerializer.SerializeToNode(
                OmittedEvidenceReadHandlesForPrompt(context.OmittedEvidenceReadHandles), JsonOptions);
        return prompt.ToJsonString(JsonOptions);
    }

    private static object OmittedEvidenceReadHandlesForPrompt(IReadOnlyList<OmittedEvidenceReadHandle> handles)
        => new
        {
            navigationOnly = true, citable = false, items = handles,
            instruction = "Current primary-read results disclosed these active canonical IDs, but their bodies do not fit the current evidence projection. These handles are locators, never proof. If still useful, read_evidence at offset 0 (or another valid character offset) opens an exact window without repeating read_source or a corpus retrieval. Cite an ID only after its canonical body is actually present in current user.evidence. Handles expire when this current read result or source is no longer active."
        };

    private static JsonElement FindOmittedEvidenceReadHandle(JsonElement prompt, string id)
    {
        if (!prompt.TryGetProperty("omittedEvidenceReadHandles", out var handles)
            || handles.ValueKind != JsonValueKind.Object
            || !handles.TryGetProperty("navigationOnly", out var navigation) || navigation.ValueKind != JsonValueKind.True
            || !handles.TryGetProperty("citable", out var citable) || citable.ValueKind != JsonValueKind.False
            || !handles.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > MaximumOmittedEvidenceReadHandles)
            return default;
        var matches = items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
            && ReadString(item, "evidenceId") == id).ToArray();
        return matches.Length == 1 ? matches[0] : default;
    }

    private static string[] ReadableEvidenceIdsForNativeTools(JsonElement prompt)
    {
        var visible = prompt.GetProperty("evidence").EnumerateArray()
            .Where(item => !item.TryGetProperty("sourceObservation", out var secondary) || secondary.ValueKind == JsonValueKind.Null)
            .Where(item => !item.TryGetProperty("nativeObservation", out var native) || native.ValueKind == JsonValueKind.Null)
            .Where(item => item.TryGetProperty("originalContentLength", out var size)
                           && size.TryGetInt32(out var length) && length > 0)
            .Select(item => ReadString(item, "evidenceId")).Where(id => !string.IsNullOrWhiteSpace(id));
        var omitted = prompt.TryGetProperty("omittedEvidenceReadHandles", out var handles)
            && handles.ValueKind == JsonValueKind.Object
            && handles.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Take(MaximumOmittedEvidenceReadHandles)
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => ReadString(item, "evidenceId"))
                .Where(id => !string.IsNullOrWhiteSpace(id) && FindOmittedEvidenceReadHandle(prompt, id).ValueKind == JsonValueKind.Object)
            : [];
        return visible.Concat(omitted).Distinct(StringComparer.Ordinal).ToArray();
    }
}
