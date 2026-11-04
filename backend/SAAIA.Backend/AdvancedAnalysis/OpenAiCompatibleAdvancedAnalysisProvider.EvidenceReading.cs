using System.Text.Json;
using SAAIA.Contracts;

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
        if (visible.ValueKind != JsonValueKind.Object)
            visible = FindOmittedEvidenceReadHandle(prompt, requestedId);
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
        // Revalidate the current disclosed capabilities against the same active
        // source catalogue, not merely an ID remembered from an older prompt.
        var handles = ReferenceEquals(context.OmittedEvidenceReadTurn, context.NativeTurn)
            ? BuildOmittedEvidenceReadHandles(context.NativeTurn, observations, context.Tools.Evidence)
                .Where(handle => context.OmittedEvidenceReadHandles.Any(disclosed =>
                    disclosed.EvidenceId == handle.EvidenceId && disclosed.SourceKey == handle.SourceKey
                    && SameCanonicalReadReference(disclosed.SourceReference, handle.SourceReference)
                    && disclosed.OriginalContentLength == handle.OriginalContentLength)).ToArray()
            : [];
        using var prompt = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidence = observations,
            omittedEvidenceReadHandles = OmittedEvidenceReadHandlesForPrompt(handles)
        }, JsonOptions));
        var canonical = CurrentCanonicalEvidenceById(context.Tools.Evidence);
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
            context.LatestExplicitReadIsFindOnly = false;
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
        var active = context.Tools.Evidence.Where(item => !string.IsNullOrWhiteSpace(item.Reference.EvidenceId))
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var groups = requests.Where(request => request.Operation is "read_source" or "find_source_text")
            .Select(request => (results.TryGetValue(BuildSearchIdentity(request), out var result)
                    ? result.Evidence : [])
                .Select(item => item.Reference.EvidenceId)
                .Where(id => !string.IsNullOrEmpty(id) && active.ContainsKey(id))
                .Cast<string>().Distinct(StringComparer.Ordinal)
                // Find results retain canonical physical order in the tool result.
                // Only the prompt preference leads with a substantive hit when
                // one exists; an index is still available as a locator.
                .OrderBy(id => request.Operation == "find_source_text"
                    ? RetrievalContentClassifier.AnalyzeEvidenceContent(active[id].Content, active[id].ExactTitle).ContentRole switch
                    {
                        RetrievalContentClassifier.ContentRole => 0,
                        RetrievalContentClassifier.MixedNavigationContentRole => 1,
                        _ => 2
                    } : 0).ToArray())
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
        if (ids.Count > 0)
            // Ordinary searches in the same batch do not turn literal finds
            // into explicit page reads. An actual page request keeps its priority.
            context.LatestExplicitReadIsFindOnly = !requests.Any(request => request.Operation == "read_source");
    }

    private static void RememberActiveExplicitReadIds(
        SynthesisResearchContext context, IEnumerable<string> ids, int preferredPrefixCount = 1)
    {
        // Keep identifiers only, bounded by the current canonical working set.
        // A retired identifier can never recover a historical body or source.
        var active = context.Tools.Evidence.Select(item => item.Reference.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var current = ids.Where(active.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var previousPrimary = context.LatestExplicitReadEvidenceIds.Take(context.LatestExplicitReadPrimaryCount)
            .Where(active.Contains).ToHashSet(StringComparer.Ordinal);
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
        context.LatestExplicitReadPrimaryCount = current.Length > 0 ? prefixCount
            : ordered.TakeWhile(previousPrimary.Contains).Count();
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizePreviousProjectionEvidence(
        SynthesisResearchContext context,
        AdvancedAnalysisLoadDescriptor load,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
    {
        if (context.NativeTurn is { } nativeTurn)
            context.NativeTurn = nativeTurn with { ContinuityEvidenceIds = [] };
        var explicitOrder = PrioritizeLatestExplicitRead(evidence, focused, context.LatestExplicitReadEvidenceIds);
        if ((context.DiscoveryBaselineEvidenceIds ?? context.PreviousProjectionEvidenceIds).Count == 0
            && context.PreviousProjectionEvidenceIds.Count == 0
            && (!context.LatestExplicitReadIsFindOnly || context.PreviousCitedEvidenceIds.Count == 0))
            return explicitOrder;
        var current = evidence.Where(item => item.Reference.EvidenceId is not null)
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        context.PreviousProjectionEvidenceIds = context.PreviousProjectionEvidenceIds
            .Where(current.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        context.PreviousCitedEvidenceIds = context.PreviousCitedEvidenceIds
            .Where(current.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        var primaryIds = context.LatestExplicitReadEvidenceIds.Take(context.LatestExplicitReadPrimaryCount)
            .Where(current.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var interleavePriorBodies = primaryIds.Count == 0 || context.LatestExplicitReadIsFindOnly;
        // Keep the pre-batch cohort stable through Writer and Critic. A second
        // role inspecting the same discovery batch must not relabel its first
        // fresh result as old and displace an initial source yet again.
        var priorProjectionIds = interleavePriorBodies && context.DiscoveryBaselineEvidenceIds is { } baseline
            ? baseline : context.PreviousProjectionEvidenceIds;
        if (primaryIds.Count == 0)
        {
            // Preserve an explicit model selection ahead of this implicit
            // continuity hint. Without a native discovery batch, leave the
            // legacy focus ordering unchanged.
            if (context.DiscoveryBaselineEvidenceIds is null
                || context.ActiveProposalEvidenceIds.Any(current.ContainsKey)
                || context.Workspace.Any(item => item.Status != "rejected" && item.EvidenceIds.Any(current.ContainsKey))
                || context.CandidateInventory.Any(item => item.Status is "body_verified" or "selected"
                    && item.BodyEvidenceIds.Any(current.ContainsKey))
                || !explicitOrder.Any(item => !priorProjectionIds.Contains(
                    item.Reference.EvidenceId!, StringComparer.Ordinal)))
                return explicitOrder;
        }
        var rejectedIds = context.Workspace.Where(item => item.Status == "rejected")
            .SelectMany(item => item.EvidenceIds)
            .Concat(context.CandidateInventory.Where(item => item.Status == "rejected")
                .SelectMany(item => item.LocatorEvidenceIds.Concat(item.BodyEvidenceIds)))
            .ToHashSet(StringComparer.Ordinal);
        var cited = context.LatestExplicitReadIsFindOnly
            ? context.PreviousCitedEvidenceIds.Where(current.ContainsKey)
                .Where(id => !primaryIds.Contains(id) && !rejectedIds.Contains(id))
                .Distinct(StringComparer.Ordinal).Select(id => current[id])
                .Where(item => RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole
                    != RetrievalContentClassifier.NavigationRole).ToArray()
            : [];
        var citedIds = cited.Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        // Keep one current hit per explicit request first. Then let previously
        // visible query/source groups take turns before further current hits.
        // An earlier prompt is never a second store of documentary bodies.
        string Group(AdvancedAnalysisResolvedEvidence item)
        {
            var queries = item.Reference.EvidenceId is { } id
                && context.RetrievalQueriesByEvidenceId.TryGetValue(id, out var found)
                ? found.Order(StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();
            return BuildCanonicalSourceIdentity(item, item.Reference.EvidenceId!) + "\n"
                + string.Join("\n", queries);
        }
        var groups = priorProjectionIds.Where(current.ContainsKey)
            .Where(id => !primaryIds.Contains(id) && !rejectedIds.Contains(id) && !citedIds.Contains(id)).Distinct(StringComparer.Ordinal)
            .Select(id => current[id])
            .Where(item => !interleavePriorBodies || (!string.IsNullOrWhiteSpace(item.Content)
                && item.SourceObservation is null && item.NativeObservation is null
                && RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole
                    != RetrievalContentClassifier.NavigationRole))
            .GroupBy(Group, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<AdvancedAnalysisResolvedEvidence>(group)).ToArray();
        var previous = new List<AdvancedAnalysisResolvedEvidence>(cited);
        // Ordinary discovery gets a bounded soft allowance, not a new pin or
        // an entitlement to the entire previous prompt. Explicit page/window
        // reads retain the existing ordering and allowance.
        const int maximumPriorDiscoveryBodies = 8;
        var previousLimit = interleavePriorBodies ? cited.Length + maximumPriorDiscoveryBodies : int.MaxValue;
        if (interleavePriorBodies && load.StructuredLayout && load.AnswerUnitCount > 1
            && load.Columns.Count > 0 && string.Equals(load.AtomicEvidenceMode, "named_item", StringComparison.OrdinalIgnoreCase))
            AppendBalancedNamedGridContinuity(load, context.RetrievalQueriesByEvidenceId, groups, previous, previousLimit);
        else
            while (previous.Count < previousLimit && groups.Any(group => group.Count > 0))
                foreach (var group in groups)
                    if (previous.Count < previousLimit && group.Count > 0) previous.Add(group.Dequeue());
        if (previous.Count == 0) return explicitOrder;
        if (context.NativeTurn is { } currentTurn)
            context.NativeTurn = currentTurn with
            {
                ContinuityEvidenceIds = previous.Select(item => item.Reference.EvidenceId!).ToArray()
            };
        var previousIds = previous.Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        var primary = explicitOrder.Where(item => primaryIds.Contains(item.Reference.EvidenceId!)).ToArray();
        var rest = explicitOrder.Where(item => !primaryIds.Contains(item.Reference.EvidenceId!)
            && !previousIds.Contains(item.Reference.EvidenceId!)).ToArray();
        if (interleavePriorBodies)
        {
            // Give current discovery its first place, but do not let a whole
            // batch of searches/finds consume the prompt before earlier bodies.
            var discovery = primary.Concat(rest).ToArray();
            var interleaved = new List<AdvancedAnalysisResolvedEvidence>();
            for (var index = 0; index < Math.Max(discovery.Length, previous.Count); index++)
            {
                if (index < discovery.Length) interleaved.Add(discovery[index]);
                if (index < previous.Count) interleaved.Add(previous[index]);
            }
            return interleaved;
        }
        var result = primary.ToList();
        for (var index = 0; index < Math.Max(previous.Count, rest.Length); index++)
        {
            if (index < previous.Count) result.Add(previous[index]);
            if (index < rest.Length) result.Add(rest[index]);
        }
        return result;
    }

    private static void AppendBalancedNamedGridContinuity(
        AdvancedAnalysisLoadDescriptor load,
        IReadOnlyDictionary<string, HashSet<string>> queriesByEvidence,
        IReadOnlyList<Queue<AdvancedAnalysisResolvedEvidence>> groups,
        List<AdvancedAnalysisResolvedEvidence> previous,
        int limit)
    {
        var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coveredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var served = new HashSet<int>();
        string Source(AdvancedAnalysisResolvedEvidence item) => BuildCanonicalSourceIdentity(item, item.Reference.EvidenceId!);
        IReadOnlyList<string> Targets(AdvancedAnalysisResolvedEvidence item) =>
            item.Reference.EvidenceId is { } id && queriesByEvidence.TryGetValue(id, out var queries)
                ? ResolveTargetColumnsForPrompt(load, queries.ToArray()) : [];
        void Count(AdvancedAnalysisResolvedEvidence item)
        {
            var source = Source(item);
            sourceCounts[source] = sourceCounts.GetValueOrDefault(source) + 1;
            coveredTargets.UnionWith(Targets(item));
        }
        foreach (var item in previous) Count(item);
        var indices = Enumerable.Range(0, groups.Count).ToArray();
        int Pick(Func<AdvancedAnalysisResolvedEvidence, bool> eligible) => indices
            .Where(index => !served.Contains(index) && groups[index].TryPeek(out var item) && eligible(item))
            .OrderBy(index => sourceCounts.GetValueOrDefault(Source(groups[index].Peek())))
            .FirstOrDefault(-1);
        void Add(int index)
        {
            served.Add(index);
            var item = groups[index].Dequeue();
            previous.Add(item);
            Count(item);
        }

        // The query's explicit target is an ordering hint only. Give each
        // requested column a chance before broad queries consume the bounded
        // continuity allowance, and break ties in favor of less-used sources.
        foreach (var column in load.Columns)
        {
            if (previous.Count >= limit) return;
            if (coveredTargets.Contains(column)) continue;
            var index = Pick(item => Targets(item).Contains(column, StringComparer.OrdinalIgnoreCase));
            if (index >= 0) Add(index);
        }
        while (previous.Count < limit)
        {
            var index = Pick(_ => true);
            if (index < 0)
            {
                if (served.Count == 0) return;
                served.Clear();
                continue;
            }
            Add(index);
        }
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

    private static bool SameCanonicalReadReference(AdvancedAnalysisResultEvidence first,
        AdvancedAnalysisResultEvidence second)
        => string.Equals(first.EvidenceId, second.EvidenceId, StringComparison.Ordinal)
            && string.Equals(first.DocId, second.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.RevisionId, second.RevisionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.SourceHash, second.SourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AdvancedAnalysisToolGateway.NormalizePath(first.DocPath),
                AdvancedAnalysisToolGateway.NormalizePath(second.DocPath), StringComparison.Ordinal)
            && string.Equals(first.ChunkId, second.ChunkId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.AnchorId, second.AnchorId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.ContentCardId, second.ContentCardId, StringComparison.OrdinalIgnoreCase)
            && first.PageStart == second.PageStart && first.PageEnd == second.PageEnd
            && first.SourceObservation is null && second.SourceObservation is null
            && first.NativeObservation is null && second.NativeObservation is null;

    private static bool MatchesBoundReadSource(AdvancedAnalysisResultEvidence reference, AdvancedAnalysisResultEvidence scope)
        => string.Equals(reference.DocId, scope.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference.RevisionId, scope.RevisionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference.SourceHash, scope.SourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AdvancedAnalysisToolGateway.NormalizePath(reference.DocPath),
                AdvancedAnalysisToolGateway.NormalizePath(scope.DocPath), StringComparison.Ordinal);

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeCurrentPrimaryReadEvidence(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> ordered,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active,
        NativeResearchTurn? turn,
        IReadOnlyDictionary<string, EvidenceReadWindow>? evidenceReadWindows)
    {
        if (turn is null || turn.Rejection is not null) return ordered;
        var current = active.Where(item => item.Reference.EvidenceId is not null)
            .GroupBy(item => item.Reference.EvidenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var readOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstReadOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var readSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, read) in turn.CallRequests)
        {
            if (turn.DeferredCallIds.Contains(callId) || read.Operation != "read_source"
                || read.NativeObservationView is not null || read.TableObservationView is not null
                || !turn.CallSourceScopes.TryGetValue(callId, out var scope)
                || !turn.Results.TryGetValue(BuildSearchIdentity(read), out var observed)
                || observed.ResourceLimit is not null || observed.NotExecutedAfterResourceLimit
                || observed.AlternativeNativeReadings?.View is not null
                || observed.AlternativeTableReadings?.View is not null
                || observed.Evidence.Any(item => !MatchesBoundReadSource(item.Reference, scope)))
                continue;
            var admitted = new List<AdvancedAnalysisResolvedEvidence>();
            foreach (var item in observed.Evidence)
            {
                var reference = item.Reference;
                if (reference.EvidenceId is not { } id || !current.TryGetValue(id, out var canonical)
                    || item.SourceObservation is not null || item.NativeObservation is not null
                    || !SameCanonicalReadReference(reference, canonical.Reference))
                    continue;
                readOrder.TryAdd(id, readOrder.Count);
                admitted.Add(canonical);
            }
            foreach (var item in admitted)
                readSources.Add(BuildCanonicalSourceIdentity(item, item.Reference.EvidenceId!));
            var first = admitted.OrderBy(item => item.SourceStructure?.HasUnassignedPhysicalContext == true)
                .FirstOrDefault(item => !firstReadOrder.ContainsKey(item.Reference.EvidenceId!));
            if (first is not null) firstReadOrder.TryAdd(first.Reference.EvidenceId!, firstReadOrder.Count);
        }
        if (readOrder.Count == 0) return ordered;
        var allowedContinuity = turn.ContinuityEvidenceIds.ToHashSet(StringComparer.Ordinal);
        var continuityIds = ordered.Where(item => item.Reference.EvidenceId is { } id
                && allowedContinuity.Contains(id) && current.TryGetValue(id, out var canonical)
                && SameCanonicalReadReference(item.Reference, canonical.Reference)
                && !readSources.Contains(BuildCanonicalSourceIdentity(item, id))
                && item.SourceObservation is null && item.NativeObservation is null
                && !string.IsNullOrWhiteSpace(item.Content)
                && item.SourceStructure?.HasUnassignedPhysicalContext != true
                && RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole
                    != RetrievalContentClassifier.NavigationRole)
            .DistinctBy(item => BuildCanonicalSourceIdentity(item, item.Reference.EvidenceId!), StringComparer.Ordinal)
            .Take(8).Select(item => item.Reference.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        // A successful page read is an explicit request for every returned body,
        // including headings and paragraphs without a prior lexical query hit.
        // Reorder active canonical objects only; inventories are not body proof.
        int Priority(AdvancedAnalysisResolvedEvidence item)
        {
            var id = item.Reference.EvidenceId;
            if (id is not null && evidenceReadWindows?.ContainsKey(id) == true) return 0;
            if (id is not null && firstReadOrder.ContainsKey(id)) return 1;
            if (id is not null && continuityIds.Contains(id)) return 2;
            if (id is null || !readOrder.ContainsKey(id)) return 5;
            return item.SourceStructure?.HasUnassignedPhysicalContext == true ? 4 : 3;
        }
        return ordered.OrderBy(Priority)
            .ThenBy(item => Priority(item) == 1 ? firstReadOrder[item.Reference.EvidenceId!]
                : Priority(item) is 3 or 4 ? readOrder[item.Reference.EvidenceId!] : 0).ToArray();
    }
}
