using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record EvidenceReadWindow(
        int Offset,
        int Length,
        IReadOnlyList<EvidenceReadWindow>? PreviousWindows = null);

    private static EvidenceReadWindow ParseEvidenceReadArguments(
        JsonElement arguments, JsonElement prompt, out string evidenceId)
    {
        evidenceId = arguments.ValueKind == JsonValueKind.Object ? ReadString(arguments, "evidenceId") : string.Empty;
        var names = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().Select(item => item.Name).ToArray()
            : [];
        var expected = new[] { "evidenceId", "offset", "length" };
        if (names.Length != expected.Length
            || names.Distinct(StringComparer.Ordinal).Count() != expected.Length
            || names.Any(name => !expected.Contains(name, StringComparer.Ordinal))
            || !arguments.TryGetProperty("offset", out var offsetValue)
            || offsetValue.ValueKind != JsonValueKind.Number
            || !offsetValue.TryGetInt32(out var offset)
            || !arguments.TryGetProperty("length", out var lengthValue)
            || lengthValue.ValueKind != JsonValueKind.Number
            || !lengthValue.TryGetInt32(out var length)
            || offset < 0 || length is < 256 or > 8_000)
        {
            throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid");
        }
        var requestedId = evidenceId;
        var visible = prompt.GetProperty("evidence").EnumerateArray()
            .FirstOrDefault(item => ReadString(item, "evidenceId") == requestedId);
        if (visible.ValueKind != JsonValueKind.Object
            || visible.TryGetProperty("sourceObservation", out var secondary) && secondary.ValueKind != JsonValueKind.Null
            || visible.TryGetProperty("nativeObservation", out var native) && native.ValueKind != JsonValueKind.Null
            || !visible.TryGetProperty("originalContentLength", out var originalLength)
            || !originalLength.TryGetInt32(out var maximum)
            || offset >= maximum)
        {
            throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid");
        }
        return new EvidenceReadWindow(offset, length);
    }

    private static IReadOnlyDictionary<string, EvidenceReadWindow> ParseEvidenceReadRequests(
        JsonElement root,
        IReadOnlyList<PromptEvidenceItem> observations,
        SynthesisResearchContext context)
    {
        if (!root.TryGetProperty("evidenceReads", out var reads))
            return new Dictionary<string, EvidenceReadWindow>(StringComparer.Ordinal);
        if (reads.ValueKind != JsonValueKind.Array || reads.GetArrayLength() is < 1 or > 8)
            throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid");
        using var prompt = JsonDocument.Parse(JsonSerializer.Serialize(new { evidence = observations }, JsonOptions));
        var canonical = context.Tools.Evidence.ToDictionary(item => item.Reference.EvidenceId!, StringComparer.Ordinal);
        var updates = new Dictionary<string, EvidenceReadWindow>(StringComparer.Ordinal);
        foreach (var read in reads.EnumerateArray())
        {
            var window = ParseEvidenceReadArguments(read, prompt.RootElement, out var id);
            if (!canonical.TryGetValue(id, out var evidence)
                || window.Offset >= evidence.Content.Length || !updates.TryAdd(id, window))
            {
                throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid");
            }
            if (!IsEvidenceTextBoundary(evidence.Content, window.Offset))
                throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid",
                    researchArgumentFeedback: new("read_evidence_offset_splits_unicode_character",
                        "read_evidence", string.Empty, null, null, 4));
        }
        return updates;
    }

    private static int ApplyEvidenceReadRequests(
        string raw,
        IReadOnlyList<PromptEvidenceItem> observations,
        SynthesisResearchContext context)
    {
        using var document = JsonDocument.Parse(UnwrapJson(raw));
        var updates = ParseEvidenceReadRequests(document.RootElement, observations, context);
        return ApplyEvidenceReadWindows(updates, context);
    }

    private static int ApplyEvidenceReadWindows(
        IReadOnlyDictionary<string, EvidenceReadWindow> updates,
        SynthesisResearchContext context)
    {
        var changed = 0;
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, window) in updates)
        {
            context.EvidenceReadWindows.TryGetValue(id, out var prior);
            if (prior is not null && SameEvidenceReadProjection(id, prior, window, context))
                continue;

            var previous = prior is null ? [] : new[] { new EvidenceReadWindow(prior.Offset, prior.Length) }
                .Concat(prior.PreviousWindows ?? [])
                .Where(item => !SameEvidenceReadProjection(id, item, window, context))
                .DistinctBy(item => (item.Offset, item.Length))
                .Take(3)
                .Select(item => new EvidenceReadWindow(item.Offset, item.Length))
                .ToArray();
            context.EvidenceReadWindows[id] = window with
            {
                PreviousWindows = previous.Length == 0 ? null : previous
            };
            changed++;
            changedIds.Add(id);
        }
        if (changedIds.Count > 0)
        {
            // A newer window leads the projection without forgetting other
            // explicit reads that still belong to the active working set.
            RememberActiveExplicitReadIds(context, updates.Keys.Where(changedIds.Contains));
            context.CandidateInventory = context.CandidateInventory.Select(candidate =>
                candidate.Status == "needs_body" && candidate.BodyEvidenceIds.Any(changedIds.Contains)
                    ? candidate with { Status = "awaiting_review", TargetRoles = [], SelectedRoles = [] }
                    : candidate).ToArray();
        }
        return changed;
    }

    private static bool SameEvidenceReadProjection(
        string evidenceId,
        EvidenceReadWindow first,
        EvidenceReadWindow second,
        SynthesisResearchContext context)
    {
        if (first.Offset != second.Offset) return false;
        if (first.Length == second.Length) return true;
        var canonical = context.Tools.Evidence.FirstOrDefault(item =>
            string.Equals(item.Reference.EvidenceId, evidenceId, StringComparison.Ordinal));
        if (canonical is null) return false;
        var remaining = Math.Max(0, canonical.Content.Length - first.Offset);
        return Math.Min(first.Length, remaining) == Math.Min(second.Length, remaining);
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeEvidenceReadWindows(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused,
        IReadOnlyDictionary<string, EvidenceReadWindow> windows)
    {
        if (windows.Count == 0) return focused;
        var expanded = evidence.Where(item => item.Reference.EvidenceId is { } id && windows.ContainsKey(id)).ToArray();
        var ids = expanded.Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        return expanded.Concat(focused.Where(item => !ids.Contains(item.Reference.EvidenceId!))).ToArray();
    }

    private static void RememberLatestExplicitRead(SynthesisResearchContext context,
        IReadOnlyList<AdvancedAnalysisSearchRequest> requests,
        IReadOnlyDictionary<string, AdvancedAnalysisSearchObservation> results)
    {
        var active = context.Tools.Evidence.Select(item => item.Reference.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var groups = requests.Where(request => request.Operation is "read_source" or "find_source_text")
            .Select(request => (results.TryGetValue(BuildSearchIdentity(request), out var result)
                    ? result.Evidence : [])
                .Select(item => item.Reference.EvidenceId)
                .Where(id => !string.IsNullOrEmpty(id) && active.Contains(id))
                .Cast<string>().Distinct(StringComparer.Ordinal).ToArray())
            .Where(group => group.Length > 0).ToArray();
        var positions = new int[groups.Length];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firstRoundCount = 0;
        var firstRound = true;
        // Give every request in this batch one current body before a request
        // consumes a second slot. Overlapping hits share one slot; advance to
        // that request's next distinct body without caching any old content.
        var advanced = true;
        while (advanced)
        {
            advanced = false;
            for (var group = 0; group < groups.Length; group++)
            {
                while (positions[group] < groups[group].Length)
                {
                    var id = groups[group][positions[group]++];
                    if (!seen.Add(id)) continue;
                    ids.Add(id);
                    advanced = true;
                    break;
                }
            }
            if (firstRound)
            {
                firstRoundCount = ids.Count;
                firstRound = false;
            }
        }
        RememberActiveExplicitReadIds(context, ids, firstRoundCount);
    }

    private static void RememberActiveExplicitReadIds(
        SynthesisResearchContext context, IEnumerable<string> ids, int preferredPrefixCount = 1)
    {
        // Keep identifiers only, bounded by the current canonical working set.
        // A retired identifier can never recover a historical body or source.
        var active = context.Tools.Evidence.Select(item => item.Reference.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var current = ids.Where(active.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var currentIds = current.ToHashSet(StringComparer.Ordinal);
        var retained = context.LatestExplicitReadEvidenceIds.Where(active.Contains)
            .Where(id => !currentIds.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        var prefixCount = Math.Clamp(preferredPrefixCount, 0, current.Length);
        var ordered = current.Take(prefixCount).ToList();
        // Every current request gets its first turn before retained reads.
        // Then keep earlier active alternatives ahead of another whole batch
        // of secondary hits. These are IDs, never remembered documentary text.
        for (var index = 0; index < Math.Max(retained.Length, current.Length - prefixCount); index++)
        {
            if (index < retained.Length) ordered.Add(retained[index]);
            if (prefixCount + index < current.Length) ordered.Add(current[prefixCount + index]);
        }
        context.LatestExplicitReadEvidenceIds = ordered.ToArray();
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeLatestExplicitRead(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused,
        IReadOnlyList<string> requestedIds)
    {
        var current = evidence.Where(item => item.Reference.EvidenceId is not null)
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var latest = requestedIds.Where(current.ContainsKey).Distinct(StringComparer.Ordinal)
            .Select(id => current[id]).ToArray();
        var ids = latest.Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        // Focus is an ordering hint, never a second store of documentary bodies.
        // Rebind it too: an expired or replaced object cannot re-enter a prompt.
        var activeFocused = focused.Select(item => item.Reference.EvidenceId is { } id
                ? current.GetValueOrDefault(id)
                : evidence.FirstOrDefault(active => ReferenceEquals(active, item)))
            .Where(item => item is not null).Cast<AdvancedAnalysisResolvedEvidence>();
        return latest.Concat(activeFocused.Where(item =>
            item.Reference.EvidenceId is not { } id || ids.Add(id))).ToArray();
    }
}
