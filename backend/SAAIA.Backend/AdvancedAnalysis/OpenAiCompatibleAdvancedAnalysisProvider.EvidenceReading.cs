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
            context.CandidateInventory = context.CandidateInventory.Select(candidate =>
                candidate.Status == "needs_body" && candidate.BodyEvidenceIds.Any(changedIds.Contains)
                    ? candidate with { Status = "awaiting_review", TargetRoles = [], SelectedRoles = [] }
                    : candidate).ToArray();
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
}
