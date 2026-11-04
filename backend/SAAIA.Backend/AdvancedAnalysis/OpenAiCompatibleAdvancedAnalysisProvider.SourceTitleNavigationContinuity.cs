using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int MaximumRetainedTitleCursors = 32;

    // Metadata of successful navigation only. No body, citation grant, old
    // provider output or reasoning item is retained or reconstructed here.
    private sealed record SourceTitleCursor(string SourceKey, int Offset, int? NextOffset, int Limit,
        string? TitleContains, int? PageStart, int? PageEnd, bool CardBackedOnly,
        int ReturnedLocatorCount, bool OffsetLimitReached,
        [property: JsonIgnore] AdvancedAnalysisResultEvidence SourceReference)
    {
        [JsonIgnore] public int ObservedThroughOffset { get; init; }
    }

    private sealed record SourceTitleNavigation(bool NavigationOnly, bool Citable,
        int MaximumEntries, int RetainedCursorCount, int ListedCursorCount, int OmittedCursorCount,
        IReadOnlyList<SourceTitleCursor> Items, string Instruction);

    private sealed class SourceTitleNavigationCatalogue(IReadOnlyList<PromptSourceHandle> original,
        SourceTitleNavigation navigation) : ReadOnlyCollection<PromptSourceHandle>(original.ToList())
    {
        public IReadOnlyList<PromptSourceHandle> Original { get; } = original;
        public SourceTitleNavigation Navigation { get; } = navigation;
    }

    private static bool CursorSourceIsCurrent(SourceTitleCursor cursor, IReadOnlyList<PromptSourceHandle> sources)
    {
        var current = sources.Where(source => source.SourceKey == cursor.SourceKey).ToArray();
        return current.Length == 1 && MatchesBoundReadSource(current[0].SourceReference, cursor.SourceReference);
    }

    private static bool SameTitleCursorFilter(SourceTitleCursor left, SourceTitleCursor right)
        => left.SourceKey == right.SourceKey && MatchesBoundReadSource(left.SourceReference, right.SourceReference)
            && left.TitleContains == right.TitleContains && left.PageStart == right.PageStart
            && left.PageEnd == right.PageEnd && left.CardBackedOnly == right.CardBackedOnly;

    private void PruneSourceTitleCursors(AdvancedAnalysisProviderRequest request, SynthesisResearchContext context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
    {
        if (context.SourceTitleCursors.Count == 0) return;
        var sources = BuildPromptSourceCatalogue(active, context.PromptSourceKeys);
        var retained = context.SourceTitleCursors.Where(cursor => CursorSourceIsCurrent(cursor, sources)).ToArray();
        var expired = context.SourceTitleCursors.Count - retained.Length;
        if (expired == 0) return;
        context.SourceTitleCursors = retained;
        using var probe = BackendDiagnostics.Start("llm.source_title_continuity", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("expired_cursor_count", expired);
        probe.Set("retained_cursor_count", retained.Length);
        probe.Complete("expired", "source_scope_no_longer_current");
    }

    private void RememberSourceTitleCursors(AdvancedAnalysisProviderRequest request, SynthesisResearchContext context)
    {
        if (!_options.NativeSourceTitleNavigationEnabled || context.NativeTurn is not { Rejection: null } turn) return;
        var sources = BuildPromptSourceCatalogue(context.Tools.Evidence, context.PromptSourceKeys);
        var retained = context.SourceTitleCursors.Where(cursor => CursorSourceIsCurrent(cursor, sources)).ToList();
        var observedPages = 0;
        foreach (var (callId, query) in turn.CallRequests)
        {
            if (query.Operation != AdvancedAnalysisSourceTitlePage.Operation || turn.DeferredCallIds.Contains(callId)
                || !turn.CallSourceScopes.TryGetValue(callId, out var scope)
                || !turn.Results.TryGetValue(BuildSearchIdentity(query), out var result)
                || result.NotExecutedAfterResourceLimit || result.ResourceLimit is not null || result.Evidence.Count != 0
                || result.SourceTitlePage is not { } page || page.Offset != query.Offset
                || !AdvancedAnalysisSourceTitlePage.IsValidWindow(query.Offset, query.TopK)
                || page.NextOffset is { } next && (next <= page.Offset || next > AdvancedAnalysisSourceTitlePage.MaximumOffset
                    || next != page.Offset + page.Items.Count + page.OmittedOversizedLocators)
                || CurrentNativeSourceKey(sources, scope) is not { } sourceKey)
                continue;
            var cursor = new SourceTitleCursor(sourceKey, page.Offset, page.NextOffset, query.TopK,
                query.TitleContains, query.PageStart, query.PageEnd, query.CardBackedTitlesOnly,
                page.Items.Count, page.OffsetLimitReached, SnapshotCanonicalReference(scope))
            { ObservedThroughOffset = page.NextOffset ?? page.Offset + page.Items.Count + page.OmittedOversizedLocators };
            var previous = retained.FindIndex(value => SameTitleCursorFilter(value, cursor));
            // Preserve the furthest actually observed frontier for each exact
            // filter. An overlapping smaller page cannot move it backwards.
            if (previous >= 0 && (retained[previous].ObservedThroughOffset > cursor.ObservedThroughOffset
                || retained[previous].ObservedThroughOffset == cursor.ObservedThroughOffset
                    && retained[previous].NextOffset is null && cursor.NextOffset is not null)) continue;
            if (previous >= 0) retained.RemoveAt(previous);
            retained.Add(cursor);
            observedPages++;
        }
        if (observedPages == 0) return;
        context.SourceTitleCursors = retained.TakeLast(MaximumRetainedTitleCursors).ToArray();
        using var probe = BackendDiagnostics.Start("llm.source_title_continuity", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("observed_page_count", observedPages);
        probe.Set("retained_cursor_count", context.SourceTitleCursors.Count);
        probe.Set("maximum_retained_cursors", MaximumRetainedTitleCursors);
        probe.Complete("stored", "successful_navigation_metadata_only");
    }

    private static IReadOnlyList<PromptSourceHandle> BuildSourceTitleNavigationCatalogue(
        IReadOnlyList<PromptSourceHandle> sources, IReadOnlyList<SourceTitleCursor> retained, int configuredMaximum)
    {
        var current = retained.Where(cursor => CursorSourceIsCurrent(cursor, sources)).Reverse()
            .Take(MaximumRetainedTitleCursors).ToArray();
        if (current.Length == 0) return sources;
        const string instruction = "Navigation only, never evidence or citations. These are the furthest successful title pages actually observed for each exact source and filter. Continue with the observed nextOffset, limit and unchanged titleContains/pageStart/pageEnd/cardBackedOnly filters. A null nextOffset means this observed filtered page supplied no continuation, not that the whole source lacks other items. Do not restart an already executed offset. Other declared research routes and all existing budgets still apply.";
        var baseline = MeasureEscapedProviderWireTextCharacters(JsonSerializer.Serialize(SourceCatalogueForPrompt(sources), JsonOptions));
        var limit = Math.Min(configuredMaximum / 4, 4_096);
        SourceTitleNavigationCatalogue Wrap(IReadOnlyList<SourceTitleCursor> listed)
            => new(sources, new(true, false, MaximumRetainedTitleCursors, current.Length, listed.Count,
                current.Length - listed.Count, listed, instruction));
        if (MeasureEscapedProviderWireTextCharacters(JsonSerializer.Serialize(SourceCatalogueForPrompt(Wrap([])), JsonOptions)) - baseline > limit)
            return sources;
        var listed = new List<SourceTitleCursor>();
        foreach (var cursor in current)
        {
            var trial = Wrap(listed.Append(cursor).ToArray());
            var characters = MeasureEscapedProviderWireTextCharacters(JsonSerializer.Serialize(SourceCatalogueForPrompt(trial), JsonOptions)) - baseline;
            if (characters <= limit) listed.Add(cursor);
        }
        // The exact same typed catalogue is used by every trial fit and final
        // send. Whole cursor records only; no unmeasured payload is appended.
        return Wrap(listed.ToArray());
    }
}
