using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int MaximumOmittedEvidenceReadHandles = 64;

    // A locator disclosed by the current native read result, never an excerpt
    // or a new citation. Keep no historical text and do not retain retired IDs.
    private sealed record OmittedEvidenceReadHandle(string EvidenceId, string SourceKey,
        int PhysicalPageStart, int PhysicalPageEnd, int OriginalContentLength,
        [property: JsonIgnore] AdvancedAnalysisResultEvidence SourceReference,
        [property: JsonIgnore] string CanonicalContentSha256);

    private sealed record OmittedEvidenceReadSnapshot(
        IReadOnlyList<OmittedEvidenceReadHandle> Retained,
        IReadOnlyList<OmittedEvidenceReadHandle> Offered);

    private static Dictionary<string, AdvancedAnalysisResolvedEvidence> CurrentCanonicalEvidenceById(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
        => active.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId))
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .Where(group => group.All(item => !string.IsNullOrEmpty(item.Content)
                && SameCanonicalReadReference(group.First().Reference, item.Reference)
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
        => ResolveOmittedEvidenceReadSnapshot(turn, observations, active, sourceCatalogue, []).Offered;

    private static string CanonicalBodyHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static AdvancedAnalysisResultEvidence SnapshotCanonicalReference(AdvancedAnalysisResultEvidence source)
        => new()
        {
            EvidenceId = source.EvidenceId, DocId = source.DocId, RevisionId = source.RevisionId,
            SourceHash = source.SourceHash, DocPath = source.DocPath, FileName = source.FileName,
            PageStart = source.PageStart, PageEnd = source.PageEnd,
            ChunkId = source.ChunkId, AnchorId = source.AnchorId, ContentCardId = source.ContentCardId
        };

    private static IReadOnlyList<OmittedEvidenceReadHandle> RevalidateOmittedEvidenceReadHandles(
        IReadOnlyList<OmittedEvidenceReadHandle> retained,
        IReadOnlyDictionary<string, AdvancedAnalysisResolvedEvidence> current,
        IReadOnlyList<PromptSourceHandle>? sourceCatalogue)
    {
        if (sourceCatalogue is null) return [];
        return retained.GroupBy(handle => handle.EvidenceId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(handle => current.TryGetValue(handle.EvidenceId, out var canonical)
                && SameCanonicalReadReference(handle.SourceReference, canonical.Reference)
                && handle.PhysicalPageStart == canonical.Reference.PageStart
                && handle.PhysicalPageEnd == canonical.Reference.PageEnd
                && handle.OriginalContentLength == canonical.Content.Length
                && handle.CanonicalContentSha256 == CanonicalBodyHash(canonical.Content)
                && CurrentNativeSourceKey(sourceCatalogue, canonical.Reference) == handle.SourceKey)
            .TakeLast(MaximumOmittedEvidenceReadHandles).ToArray();
    }

    // Pure: fitting can try many projections without publishing, pruning or
    // consuming any context capability. The actual prompt commits this result.
    private static OmittedEvidenceReadSnapshot ResolveOmittedEvidenceReadSnapshot(
        NativeResearchTurn? turn, IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active,
        IReadOnlyList<PromptSourceHandle>? sourceCatalogue,
        IReadOnlyList<OmittedEvidenceReadHandle> retained)
    {
        var current = CurrentCanonicalEvidenceById(active);
        var valid = RevalidateOmittedEvidenceReadHandles(retained, current, sourceCatalogue);
        var disclosed = FindCurrentPrimaryReadCapabilities(turn, observations, current, sourceCatalogue);
        var newIds = disclosed.Select(handle => handle.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var merged = valid.Where(handle => !newIds.Contains(handle.EvidenceId)).Concat(disclosed)
            .TakeLast(MaximumOmittedEvidenceReadHandles).ToArray();
        var visible = observations.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        return new(merged, merged.Where(handle => !visible.Contains(handle.EvidenceId)).ToArray());
    }

    private static IReadOnlyList<OmittedEvidenceReadHandle> FindCurrentPrimaryReadCapabilities(
        NativeResearchTurn? turn, IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyDictionary<string, AdvancedAnalysisResolvedEvidence> current,
        IReadOnlyList<PromptSourceHandle>? sourceCatalogue)
    {
        if (turn is null || turn.Rejection is not null || sourceCatalogue is null)
            return [];
        var visible = observations.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var handles = new List<OmittedEvidenceReadHandle>();
        var promisedOmissions = new HashSet<string>(StringComparer.Ordinal);
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

            // Preserve IDs already shown with this executed read, including bodies
            // that fit now but can leave a later projection. The other admitted IDs
            // are exactly the renderer's omittedEvidenceIds.Take(8), never an
            // unlisted result or an unrelated search hit.
            var omitted = result.Evidence.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                    && !visible.Contains(item.Reference.EvidenceId))
                .DistinctBy(item => item.Reference.EvidenceId, StringComparer.Ordinal).Take(8)
                .Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
            var disclosed = result.Evidence.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                    && (visible.Contains(item.Reference.EvidenceId) || omitted.Contains(item.Reference.EvidenceId)))
                .DistinctBy(item => item.Reference.EvidenceId, StringComparer.Ordinal);
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
                    canonical.Content.Length, SnapshotCanonicalReference(canonical.Reference), CanonicalBodyHash(canonical.Content)));
                if (omitted.Contains(id)) promisedOmissions.Add(id);
            }
        }
        // Current read outputs already promise these omissions. Keep them ahead
        // of newly remembered visible bodies when the private registry is full.
        return handles.Where(handle => !promisedOmissions.Contains(handle.EvidenceId))
            .Concat(handles.Where(handle => promisedOmissions.Contains(handle.EvidenceId)))
            .TakeLast(MaximumOmittedEvidenceReadHandles).ToArray();
    }

    private static string AddOmittedEvidenceReadHandlesToPrompt(string userPrompt,
        IReadOnlyList<PromptEvidenceItem> observations, SynthesisResearchContext context)
    {
        var snapshot = ResolveOmittedEvidenceReadSnapshot(context.NativeTurn, observations, context.Tools.Evidence,
            observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection ? projection.SourceCatalogue : null,
            context.RetainedOmittedEvidenceReadHandles);
        context.RetainedOmittedEvidenceReadHandles = snapshot.Retained;
        context.OmittedEvidenceReadHandles = snapshot.Offered;
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
            instruction = "Earlier primary-read results disclosed these currently revalidated canonical IDs, but their bodies do not fit the current evidence projection. These handles are locators, never proof. If useful, read_evidence at offset 0 (or another valid character offset) opens an exact window without repeating read_source or a corpus retrieval. Cite an ID only when its canonical body is present in current user.evidence. Opening one handle does not expire the others. At most 64 handles remain within this job; retired or changed bodies/sources and the oldest excess handles expire."
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
