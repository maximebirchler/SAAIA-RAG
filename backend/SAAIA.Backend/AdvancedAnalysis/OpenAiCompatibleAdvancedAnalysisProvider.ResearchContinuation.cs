using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record SynthesisResearchContext(
        IAdvancedAnalysisToolGateway Tools,
        IReadOnlyList<string> AvailableCategories,
        List<AdvancedAnalysisSearchRequest> PriorSearches,
        HashSet<string> PreviouslyExecuted,
        List<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> EvidenceGroups,
        Dictionary<string, HashSet<string>> RetrievalQueriesByEvidenceId)
    {
        public List<AdvancedAnalysisSearchRequest> FocusSearches { get; } = [];
        public NativeResearchTurn? NativeTurn { get; set; }
        public IReadOnlyList<ResearchWorkspaceItem> Workspace { get; set; } = [];
        public IReadOnlyList<string> ActiveProposalEvidenceIds { get; set; } = [];
        public int ActiveProposalOmittedEvidenceCount { get; set; }
    }

    private sealed record SynthesisCompletion(
        CompletionResult Completion,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
        IReadOnlyList<PromptEvidenceItem> PromptEvidence);

    private sealed record CandidateSupportCorrection(string ClaimId, string SelectedItem,
        IReadOnlyList<string> EvidenceIds, string Reason);

    private static object BuildResearchArgumentFeedbackForPrompt(AdvancedAnalysisResearchArgumentFeedback feedback) => new
    {
        reasonCode = "research_batch_rejected_before_execution", correction = feedback,
        instruction = "No request from the rejected batch ran. Submit a corrected complete batch using the catalogue and observed evidence, or return a supported final result. Page bounds are inclusive; do not widen the limits or invent a source handle. This correction consumes the existing model-call budget."
    };

    internal static bool IsSourceScopedResearch(AdvancedAnalysisSearchRequest search)
        => search.Operation == "read_source" || !string.IsNullOrWhiteSpace(search.DocId)
           || !string.IsNullOrWhiteSpace(search.DocPath) || !string.IsNullOrWhiteSpace(search.DocumentHint);

    internal static IReadOnlyList<AdvancedAnalysisSearchRequest> RememberFocusedSearches(
        IReadOnlyList<AdvancedAnalysisSearchRequest> remembered,
        IReadOnlyList<AdvancedAnalysisSearchRequest> additions)
    {
        var distinct = remembered.Concat(additions)
            .GroupBy(BuildSearchIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last()).ToArray();
        var scoped = distinct.Where(IsSourceScopedResearch).TakeLast(20).ToArray();
        return scoped.Concat(distinct.Where(static search => !IsSourceScopedResearch(search))
            .TakeLast(20 - scoped.Length)).ToArray();
    }

    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeFocusedEvidenceForPrompt(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId,
        IReadOnlyList<AdvancedAnalysisSearchRequest> searches)
    {
        var buckets = searches.Take(20).Select(search =>
        {
            var ordered = OrderEvidenceForQuery(search.Query, search.DocumentHint, evidence.Where(item =>
                item.Reference.EvidenceId is { } id
                && retrievalQueriesByEvidenceId.TryGetValue(id, out var queries)
                && queries.Contains(search.Query)
                && (string.IsNullOrWhiteSpace(search.DocId)
                    || string.Equals(search.DocId, item.Reference.DocId, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(search.RevisionId)
                    || string.Equals(search.RevisionId, item.Reference.RevisionId, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(search.DocPath)
                    || string.Equals(search.DocPath.Replace('\\', '/'), item.Reference.DocPath, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(search.Category)
                    || item.Reference.DocPath.StartsWith(search.Category.Trim('/') + "/", StringComparison.OrdinalIgnoreCase))
                && (search.PageStart is null || item.Reference.PageEnd >= search.PageStart)
                && (search.PageEnd is null || item.Reference.PageStart <= search.PageEnd)).ToArray())
            .OrderBy(item => RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole switch
            {
                RetrievalContentClassifier.ContentRole => 0,
                RetrievalContentClassifier.MixedNavigationContentRole => 1,
                _ => 2
            }).ToArray();
            if (search.Operation != "read_source") return ordered.Take(3).ToArray();
            var heading = ordered.Where(item => item.Reference.PageStart == search.PageStart
                && RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole == RetrievalContentClassifier.NavigationRole)
                .OrderByDescending(static item => item.Content.Length).FirstOrDefault();
            var substantive = ordered.Where(item => item != heading)
                .OrderBy(item => RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole == RetrievalContentClassifier.NavigationRole ? 1 : 0)
                .ThenByDescending(static item => item.Content.Length).ToArray();
            var primaryBody = substantive.FirstOrDefault(item => item.Reference.PageStart == search.PageStart
                && RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole != RetrievalContentClassifier.NavigationRole);
            var body = primaryBody ?? substantive.FirstOrDefault(item =>
                RetrievalContentClassifier.AnalyzeEvidenceContent(item.Content, item.ExactTitle).ContentRole != RetrievalContentClassifier.NavigationRole);
            var anchors = new[] { body, heading }.Where(static item => item is not null)
                .Cast<AdvancedAnalysisResolvedEvidence>().ToArray();
            return anchors.Concat(substantive.Where(item => !anchors.Contains(item))).Take(3).ToArray();
        }).ToArray();
        var selected = new List<AdvancedAnalysisResolvedEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < 3 && selected.Count < 20; rank++)
            foreach (var bucket in buckets)
                if (rank < bucket.Length && selected.Count < 20
                    && seen.Add(bucket[rank].Reference.EvidenceId!))
                    selected.Add(bucket[rank]);
        return selected;
    }

    private static IReadOnlyList<CandidateSupportCorrection> FindIdentityOnlyCandidateSupport(
        string raw, IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        AdvancedAnalysisProviderRequest request)
    {
        AdvancedAnalysisProviderResult result;
        try { result = ParseResult(raw, evidence, request); }
        catch (AdvancedAnalysisProviderException) { return []; }
        return FindIdentityOnlyCandidateClaims(result, evidence, request);
    }

    private static IReadOnlyList<CandidateSupportCorrection> FindIdentityOnlyCandidateClaims(
        AdvancedAnalysisProviderResult result, IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        AdvancedAnalysisProviderRequest request)
    {
        if (!request.Handoff.Load.StructuredLayout
            || !request.Handoff.Load.AtomicEvidenceMode.Contains("named_item", StringComparison.OrdinalIgnoreCase))
            return [];
        if (result.Outcome != "answered")
            return [];
        var byId = evidence.ToDictionary(item => item.Reference.EvidenceId!, StringComparer.Ordinal);
        return result.Claims.Where(claim => !string.IsNullOrWhiteSpace(claim.SelectedItem)
                && claim.EvidenceIds.All(id => RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(
                    byId[id].Content, byId[id].ExactTitle, claim.SelectedItem!)))
            .Select(claim => new CandidateSupportCorrection(claim.ClaimId, claim.SelectedItem!,
                claim.EvidenceIds, "The bound evidence only locates or names this selected item. Cite its substantive canonical content; retain a locator as additional same-source scope only. Use search_corpus if that content is missing. Do not infer corpus absence from this correction."))
            .ToArray();
    }

    private string BuildSynthesisResearchContract()
        => !_options.AdaptiveResearchEnabled ? string.Empty : """
           You may request new documentary research before your final result.
           Your research operations are search_corpus, read_source and
           find_source_text in this job's private corpus. researchTools.tools
           lists their parameters; choose the operation explicitly in each query.
           researchTools describes actual categories and previous searches. If a needed item's content
           is missing, ambiguous or inconsistent, return this non-final object:
           {"outcome":"research_required","queries":[{"query":"exact observed title or targeted reformulation","sourceKey":"exact observed opaque sourceKey, or empty","category":"allowed category or empty","topK":20}]}.
           Leave documentHint empty when using sourceKey. Do not repeat a search
           with the same query and scope. Do not answer or publish claims in a
           research_required object. SAAIA executes the searches and calls you
           again with revalidated evidence. Request documentary facts, not optional
           user preferences. Only a subsequent terminal result reaches the user.
           Use observed covers, headings and contents to discover candidate names,
           then follow their exact titles in that source or read their physical pages.
           A contents entry identifies where to investigate, not the item's body.
           Prefer this follow-up to another general query for the same unit family.
           Request research only when researchTools.researchAllowed is true.
           A limit on further research does not prove absence from the corpus.
           """ + "\n" + CanonicalSourceReadContract;

    private async Task<SynthesisCompletion> CompleteWithCorpusResearchAsync(
        AdvancedAnalysisProviderRequest request,
        string phase,
        string systemPrompt,
        Func<IReadOnlyList<PromptEvidenceItem>, string> buildUserPrompt,
        int maximumTokens,
        int reservedFinalCalls,
        SynthesisResearchContext context,
        List<CompletionResult> completions,
        CancellationToken cancellationToken)
    {
        var maximumCalls = Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024);
        var followup = 0;
        var supportCorrection = 0;
        var noProgressRecovery = 0;
        var argumentRecovery = 0;
        AdvancedAnalysisResearchArgumentFeedback? argumentFeedback = null;
        IReadOnlyList<AdvancedAnalysisSearchRequest>? repeatedSearches = null;
        var nextPhase = phase;
        object? candidateSupportFeedback = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completions.Count >= maximumCalls)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_call_budget_exhausted");
            var evidence = FilterEvidenceToRequestedDocumentSet(request,
                OrderEvidenceForPrompt(context.Tools.Evidence, context.EvidenceGroups));
            var focusedEvidence = PrioritizeFocusedEvidenceForPrompt(evidence,
                context.RetrievalQueriesByEvidenceId, context.FocusSearches);
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled)
                focusedEvidence = PrioritizeResearchWorkspace(evidence, context.Workspace, focusedEvidence);
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
                focusedEvidence = PrioritizeActiveProposalEvidence(evidence, context.ActiveProposalEvidenceIds, focusedEvidence);
            var observations = BuildPromptEvidence(request, evidence, context.RetrievalQueriesByEvidenceId,
                focusedEvidence);
            var userPrompt = buildUserPrompt(observations);
            if (_options.AdaptiveResearchEnabled || candidateSupportFeedback is not null || argumentFeedback is not null
                || _options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
            {
                var payload = JsonNode.Parse(userPrompt)!.AsObject();
                if (_options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
                    payload["activeProposal"] = JsonSerializer.SerializeToNode(new
                    {
                        retainedEvidenceIds = context.ActiveProposalEvidenceIds,
                        omittedBeyondRetentionLimit = context.ActiveProposalOmittedEvidenceCount,
                        currentlyVisibleEvidenceIds = context.ActiveProposalEvidenceIds.Where(id => observations.Any(e => e.EvidenceId == id)).ToArray(),
                        currentlyAbsentEvidenceIds = context.ActiveProposalEvidenceIds.Where(id => observations.All(e => e.EvidenceId != id)).ToArray(),
                        instruction = "References explicitly cited by your last structured proposal, not facts or approval. Canonical excerpts are prioritized within the existing context limit. Only current evidence bodies support claims; absent references cannot be cited. Reassess substantive content and source scope yourself, research or replace a choice if needed."
                    }, JsonOptions);
                if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled)
                    payload["researchWorkspace"] = JsonSerializer.SerializeToNode(new
                    {
                        enabled = true, items = context.Workspace,
                        currentVisibleEvidenceIds = context.Workspace.SelectMany(i => i.EvidenceIds).Distinct(StringComparer.Ordinal)
                            .Where(id => observations.Any(e => e.EvidenceId == id)).ToArray(),
                        instruction = "Your bounded operational workspace, not proof. When researchTools.researchAllowed is true, you may use save_research_state to retain chosen items, purposes, decisions and gaps before changing research focus. Only current evidence excerpts support facts. Choose further research yourself; absence from this view or memory does not demonstrate corpus absence."
                    }, JsonOptions);
                if (_options.AdaptiveResearchEnabled)
                    payload["researchTools"] = JsonSerializer.SerializeToNode(new
                {
                    tools = BuildResearchToolsForPrompt(),
                    researchAllowed = maximumCalls - completions.Count - 1 - reservedFinalCalls > 0,
                    remainingModelCalls = Math.Max(0, maximumCalls - completions.Count - 1 - reservedFinalCalls),
                    maximumQueries = ResolveMaximumPlanQueries(request),
                    availableCategories = context.AvailableCategories,
                    priorSearches = BuildPriorSearchesForPrompt(context.PriorSearches, observations)
                }, JsonOptions);
                if (candidateSupportFeedback is not null)
                    payload["candidateSupportCorrections"] = JsonSerializer.SerializeToNode(candidateSupportFeedback, JsonOptions);
                if (argumentFeedback is not null)
                    payload["researchArgumentFeedback"] = JsonSerializer.SerializeToNode(BuildResearchArgumentFeedbackForPrompt(argumentFeedback), JsonOptions);
                if (repeatedSearches is not null)
                    payload["researchFeedback"] = JsonSerializer.SerializeToNode(new
                    {
                        reasonCode = "search_already_executed",
                        searches = BuildPriorSearchesForPrompt(repeatedSearches, observations),
                        instruction = "These searches already ran. Their existing evidence is prioritized in this context; they were not executed again. Use the available substantive content, or reformulate the query/scope if a different fact is needed. Do not repeat the same search. Lack of a visible passage does not prove corpus absence."
                    }, JsonOptions);
                userPrompt = payload.ToJsonString(JsonOptions);
            }
            var completion = await CompleteJsonAsync(request.JobId,
                nextPhase,
                systemPrompt, userPrompt, maximumTokens, cancellationToken,
                allowNativeResearch: true,
                nativeToolTurnMessages: _options.NativeResearchToolsEnabled
                    ? BuildNativeToolTurnMessages(context.NativeTurn, observations, UsesNativeResponses) : null).ConfigureAwait(false);
            if (!RequestsCorpusResearch(completion.Content))
            {
                if (_options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
                    RememberActiveProposalEvidence(completion.Content, evidence, observations, request, context);
                var corrections = FindIdentityOnlyCandidateSupport(completion.Content, evidence, request);
                if (corrections.Count == 0)
                    return new SynthesisCompletion(completion, evidence, observations);
                completions.Add(completion);
                if (completions.Count >= maximumCalls - reservedFinalCalls)
                    throw new AdvancedAnalysisProviderException("advanced_synthesis_candidate_body_not_supported");
                var proposed = ParseResult(completion.Content, evidence, request);
                candidateSupportFeedback = new
                {
                    corrections,
                    previousProposal = new { proposed.Outcome, proposed.AnswerText, proposed.Claims },
                    instruction = "Correct the listed selections before returning a terminal answer. Reuse substantive evidence already observed when possible; request research if necessary and available. Preserve supported choices. An identity locator is not the selected item's body."
                };
                nextPhase = $"{phase}-support-correction-{++supportCorrection}";
                continue;
            }

            completions.Add(completion);
            if (!_options.AdaptiveResearchEnabled)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_disabled");
            if (completions.Count >= maximumCalls - reservedFinalCalls)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_call_budget_exhausted");

            IReadOnlyList<AdvancedAnalysisSearchRequest> queries;
            IReadOnlyList<ResearchWorkspaceItem>? workspaceUpdate = null;
            try
            {
                using var document = JsonDocument.Parse(UnwrapJson(completion.Content));
                if (document.RootElement.TryGetProperty("researchState", out var state))
                {
                    if (completion.NativeToolCallsJson is null) throw new JsonException();
                    using var current = JsonDocument.Parse(userPrompt);
                    workspaceUpdate = ParseResearchWorkspace(state, current.RootElement);
                }
                if (!document.RootElement.TryGetProperty("queries", out var values)
                    || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0 && workspaceUpdate is null)
                    throw new JsonException();
                queries = values.GetArrayLength() == 0 ? [] : ParseResearchReview(JsonSerializer.Serialize(new
                {
                    decision = "search_more", queries = values
                }, JsonOptions), request, context.AvailableCategories, observations);
            }
            catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException)
            {
                if (error is AdvancedAnalysisProviderException { ResearchArgumentFeedback: { } feedback }
                    && argumentRecovery == 0 && completions.Count < maximumCalls - reservedFinalCalls - 1)
                {
                    argumentFeedback = feedback;
                    if (completion.NativeToolCallsJson is { } rejectedCalls)
                        context.NativeTurn = new(rejectedCalls, null,
                            new Dictionary<string, AdvancedAnalysisSearchObservation>(), feedback, completion.NativeResponseOutputJson);
                    nextPhase = $"{phase}-research-argument-recovery-{++argumentRecovery}";
                    continue;
                }
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_protocol_invalid");
            }
            argumentFeedback = null;
            var workspaceChanged = workspaceUpdate is not null
                && JsonSerializer.Serialize(workspaceUpdate, JsonOptions) != JsonSerializer.Serialize(context.Workspace, JsonOptions);
            var rememberedFocus = RememberFocusedSearches(context.FocusSearches, queries);
            context.FocusSearches.Clear();
            context.FocusSearches.AddRange(rememberedFocus);
            if (!workspaceChanged && !queries.Any(query => !context.PreviouslyExecuted.Contains(BuildSearchIdentity(query))))
            {
                if (++noProgressRecovery > 1)
                    throw new AdvancedAnalysisProviderException("advanced_synthesis_research_no_progress");
                repeatedSearches = queries;
                if (completion.NativeToolCallsJson is { } duplicateCalls)
                    context.NativeTurn = new(duplicateCalls, queries,
                        new Dictionary<string, AdvancedAnalysisSearchObservation>(), ResponseOutputItemsJson: completion.NativeResponseOutputJson);
                nextPhase = $"{phase}-research-recovery-{noProgressRecovery}";
                continue;
            }
            repeatedSearches = null;
            var nativeResults = completion.NativeToolCallsJson is null ? null
                : new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase);
            await ExecuteSearchBatchAsync(context.Tools, queries, context.PreviouslyExecuted,
                context.PriorSearches, context.EvidenceGroups, context.RetrievalQueriesByEvidenceId,
                cancellationToken, nativeResults).ConfigureAwait(false);
            if (workspaceUpdate is not null) context.Workspace = workspaceUpdate;
            if (completion.NativeToolCallsJson is { } executedCalls)
                context.NativeTurn = new(executedCalls, queries, nativeResults!, ResponseOutputItemsJson: completion.NativeResponseOutputJson,
                    WorkspaceStored: workspaceChanged);
            followup++;
            nextPhase = $"{phase}-research-followup-{followup}";
        }
    }

    private static bool RequestsCorpusResearch(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            return string.Equals(ReadString(document.RootElement, "outcome").Trim(),
                "research_required", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
