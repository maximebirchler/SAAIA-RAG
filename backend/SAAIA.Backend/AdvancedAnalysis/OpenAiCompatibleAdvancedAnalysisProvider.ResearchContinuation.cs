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
        public IReadOnlyList<CandidateInventoryItem> CandidateInventory { get; set; } = [];
        public int CandidateInventoryAdmissionBlockedCount { get; set; }
        public HashSet<string> ObservedCandidateEvidenceIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvidenceReadWindow> EvidenceReadWindows { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> LatestExplicitReadEvidenceIds { get; set; } = [];
        public Dictionary<string, string> PromptSourceKeys { get; } = new(StringComparer.Ordinal);
        public bool CandidateInventoryCheckpointLoaded { get; set; }
        public CandidateExplorerDossier? CandidateExplorerDossier { get; set; }
        public IReadOnlyList<string> ActiveProposalEvidenceIds { get; set; } = [];
        public int ActiveProposalOmittedEvidenceCount { get; set; }
        public AdvancedAnalysisResearchResourceLimit? ResourceLimit { get; set; }
    }

    private sealed record SynthesisCompletion(
        CompletionResult Completion,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
        IReadOnlyList<PromptEvidenceItem> PromptEvidence,
        bool InsufficiencyReviewed = false);

    private sealed record CandidateSupportCorrection(string ClaimId, string SelectedItem,
        IReadOnlyList<string> EvidenceIds, string Reason, string ReasonCode = "candidate_body_required");

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
           researchTools.documentaryBudget, when present, is the actual shared
           budget for search, page read and literal find operations. Each new
           operation consumes one call; an open model-call budget does not grant
           additional documentary calls. Keep complete batches within the remainder.
           A limit on further research does not prove absence from the corpus.
           When native read_evidence is declared, evidenceReadingAllowed separately
           controls reading another window of already revalidated canonical text.
           It may remain available after the documentary search budget is spent.
           Read only through the declared function; do not publish evidenceReads
           directly in a text JSON result. Each returned window is separate and
           gaps between windows do not establish continuous source wording.
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
        CancellationToken cancellationToken,
        bool candidateExplorer = false)
    {
        var maximumCalls = Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024);
        var followup = 0;
        var supportCorrection = 0;
        var noProgressRecovery = 0;
        var argumentRecovery = 0;
        var insufficiencyReviewed = false;
        object? insufficiencyReview = null;
        AdvancedAnalysisResearchArgumentFeedback? argumentFeedback = null;
        IReadOnlyList<AdvancedAnalysisSearchRequest>? repeatedSearches = null;
        var nextPhase = phase;
        object? candidateSupportFeedback = null;
        var blockedCandidateAdmissionsForFeedback = 0;
        if (_options.NativeResearchToolsEnabled
            && !context.CandidateInventoryCheckpointLoaded)
        {
            await RestoreCandidateInventoryCheckpointAsync(
                    request,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completions.Count >= maximumCalls)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_call_budget_exhausted");
            PruneRetiredEvidenceCopies(context);
            var toolBudget = context.Tools.Budget;
            var evidence = FilterEvidenceToRequestedDocumentSet(request,
                OrderEvidenceForPrompt(context.Tools.Evidence, context.EvidenceGroups));
            var focusedEvidence = PrioritizeFocusedEvidenceForPrompt(evidence,
                context.RetrievalQueriesByEvidenceId, context.FocusSearches);
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled)
                focusedEvidence = PrioritizeResearchWorkspace(evidence, context.Workspace, focusedEvidence);
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled
                && CandidateInventoryEnabled(request))
            {
                var priorityInventory = !candidateExplorer
                    && context.CandidateExplorerDossier is not null
                    ? context.CandidateInventory.Where(item =>
                            (request.Handoff.Load.Columns.Count == 0 || item.TargetRoles.Count > 0)
                            && item.Status is "body_verified" or "selected")
                        .ToArray()
                    : context.CandidateInventory;
                focusedEvidence = PrioritizeCandidateInventoryEvidence(
                    evidence,
                    priorityInventory,
                    focusedEvidence);
            }
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
                focusedEvidence = PrioritizeActiveProposalEvidence(evidence, context.ActiveProposalEvidenceIds, focusedEvidence);
            focusedEvidence = PrioritizeEvidenceReadWindows(evidence, focusedEvidence, context.EvidenceReadWindows);
            focusedEvidence = PrioritizeLatestExplicitRead(evidence, focusedEvidence, context.LatestExplicitReadEvidenceIds);
            var observations = BuildPromptEvidenceWithPersistentSourceKeys(
                request, evidence, context.RetrievalQueriesByEvidenceId,
                focusedEvidence, context.PromptSourceKeys, context.EvidenceReadWindows, context.NativeTurn);
            if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled
                && CandidateInventoryEnabled(request))
            {
                var observedInventory = ObserveCandidateInventory(
                    request,
                    context.CandidateInventory,
                    observations,
                    out var blockedDiscoveries,
                    context.ObservedCandidateEvidenceIds);
                context.CandidateInventoryAdmissionBlockedCount = blockedDiscoveries;
                if (JsonSerializer.Serialize(observedInventory, JsonOptions)
                    != JsonSerializer.Serialize(context.CandidateInventory, JsonOptions))
                {
                    context.CandidateInventory = observedInventory;
                    await SaveCandidateInventoryCheckpointAsync(
                            context,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            context.Tools.BeginEvidenceBatch(RetainedResearchEvidenceIds(context, observations));
            toolBudget = context.Tools.Budget;
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
                if (_options.NativeResearchToolsEnabled && _options.NativeResearchWorkspaceEnabled
                    && CandidateInventoryEnabled(request))
                {
                    var promptInventory = !candidateExplorer
                        && context.CandidateExplorerDossier is not null
                        ? context.CandidateInventory.Where(item =>
                                (request.Handoff.Load.Columns.Count == 0 || item.TargetRoles.Count > 0)
                                && item.Status is "body_verified" or "selected")
                            .ToArray()
                        : context.CandidateInventory;
                    payload["candidateInventory"] = JsonSerializer.SerializeToNode(
                        BuildCandidateInventoryForPrompt(
                            request,
                            promptInventory,
                            observations),
                        JsonOptions);
                    if (Math.Max(context.CandidateInventoryAdmissionBlockedCount,
                            blockedCandidateAdmissionsForFeedback) > 0)
                        payload["candidateInventoryAdmissionFeedback"] = JsonSerializer.SerializeToNode(new
                        {
                            blockedDiscoveryCount = Math.Max(context.CandidateInventoryAdmissionBlockedCount,
                                blockedCandidateAdmissionsForFeedback),
                            instruction = "Some proposed identities could not enter the bounded active inventory. Existing candidates and their evidence remain available. Assess or reject unsuitable pending items before admitting more; this operational limit does not establish corpus absence."
                        }, JsonOptions);
                    if (candidateExplorer)
                    {
                        var assignmentGap = BuildCandidateExplorerCoverage(
                            request,
                            context.CandidateInventory);
                        payload["candidateAssignmentGap"] = JsonSerializer.SerializeToNode(new
                        {
                            assignmentGap.RequiredDistinctCount,
                            assignmentGap.BodyVerifiedDistinctCount,
                            assignmentGap.MaximumAssignableCount,
                            assignmentGap.MissingByRole,
                            focusRoles = assignmentGap.MissingByRole.Keys.ToArray(),
                            instruction = assignmentGap.Ready
                                ? "The distinct assignment is complete. Finish any already justified reserve work, then submit candidate_dossier_ready."
                                : "Spend the next documentary operations on the listed focusRoles. Choose the titles and tools semantically, and save every verified body's targetRoles. Do not spend new searches on a role whose missing count is zero unless the same candidate is also being assessed for a listed focus role. The application will recompute the global assignment after each inventory update."
                        }, JsonOptions);
                    }
                }
                if (!candidateExplorer && context.CandidateExplorerDossier is not null)
                    payload["candidateDossier"] = JsonSerializer.SerializeToNode(
                        context.CandidateExplorerDossier,
                        JsonOptions);
                if (_options.AdaptiveResearchEnabled)
                    payload["researchTools"] = JsonSerializer.SerializeToNode(new
                {
                        tools = BuildResearchToolsForPrompt(includeEvidenceReading: _options.NativeResearchToolsEnabled),
                    researchAllowed = maximumCalls - completions.Count - 1 - reservedFinalCalls > 0
                        && context.ResourceLimit is null
                        && HasDocumentaryResearchBudget(toolBudget),
                    remainingModelCalls = Math.Max(0, maximumCalls - completions.Count - 1 - reservedFinalCalls),
                    evidenceReadingAllowed = maximumCalls - completions.Count - 1 - reservedFinalCalls > 0,
                    documentaryBudget = toolBudget,
                    maximumQueries = ResolveMaximumPlanQueries(request),
                    availableCategories = context.AvailableCategories,
                    priorSearches = BuildPriorSearchesForPrompt(context.PriorSearches, observations)
                }, JsonOptions);
                if (context.ResourceLimit is not null)
                    payload["researchResourceLimit"] = JsonSerializer.SerializeToNode(new
                    {
                        context.ResourceLimit.ReasonCode, context.ResourceLimit.MaximumEvidenceItems,
                        context.ResourceLimit.ConsumedEvidenceItems, context.ResourceLimit.RequestedNewEvidenceItems,
                        instruction = "Research stopped at this operational capacity limit. Previously admitted evidence remains valid after current revalidation. The refused result admitted no new evidence; later batch operations did not run. Produce a supported synthesis from current evidence or explain the bounded research limit. This is not proof of corpus absence or a numerical shortage."
                    }, JsonOptions);
                if (candidateSupportFeedback is not null)
                    payload["candidateSupportCorrections"] = JsonSerializer.SerializeToNode(candidateSupportFeedback, JsonOptions);
                if (insufficiencyReview is not null)
                    payload["insufficiencyReview"] = JsonSerializer.SerializeToNode(insufficiencyReview, JsonOptions);
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
                    ? BuildNativeToolTurnMessages(context.NativeTurn, observations, UsesNativeResponses) : null,
                canonicalProjection: observations).ConfigureAwait(false);
            if (!RequestsCorpusResearch(completion.Content))
            {
                if (candidateExplorer)
                    return new SynthesisCompletion(completion, evidence, observations);
                if (_options.AdaptiveResearchEnabled && _options.NativeResearchToolsEnabled
                    && phase == "writer" && request.Handoff.Load.StructuredLayout
                    && string.Equals(request.Handoff.Load.AtomicEvidenceMode, "named_item", StringComparison.OrdinalIgnoreCase)
                    && IsInsufficientDocumentaryResult(completion.Content))
                {
                    var remainingCalls = Math.Max(0, maximumCalls - completions.Count - 1 - reservedFinalCalls);
                    var reason = insufficiencyReviewed ? "insufficiency_review_already_used"
                        : remainingCalls < 2 ? "model_budget_unavailable"
                        : context.ResourceLimit is not null || !HasDocumentaryResearchBudget(toolBudget)
                            ? "documentary_budget_unavailable"
                        : !HasOpenResearchLocator(observations) ? "no_current_research_locator"
                        : followup > 0 || context.NativeTurn is not null || context.CandidateExplorerDossier is not null
                            ? "insufficiency_after_partial_documentary_research"
                        : "initial_insufficiency_before_documentary_research";
                    var review = reason is "initial_insufficiency_before_documentary_research"
                        or "insufficiency_after_partial_documentary_research";
                    using (var probe = BackendDiagnostics.Start("llm.insufficiency_review", _logger))
                    {
                        probe.Set("job_id", request.JobId);
                        probe.Set("role", nextPhase);
                        probe.Set("selection_mode", ResolveSelectionMode(request.Handoff.Load));
                        probe.Set("review_scheduled", review);
                        probe.Set("remaining_model_calls", remainingCalls);
                        probe.Set("remaining_tool_calls", toolBudget?.RemainingCalls);
                        probe.Set("remaining_tool_ms", toolBudget?.RemainingElapsedMilliseconds);
                        probe.Set("remaining_evidence_items", toolBudget?.RemainingEvidenceItems);
                        probe.Set("visible_evidence_count", observations.Count);
                        probe.Set("visible_source_count", observations.Select(item => item.SourceKey).Distinct(StringComparer.Ordinal).Count());
                        probe.Set("prior_documentary_followup_count", followup);
                        probe.Set("native_turn_present", context.NativeTurn is not null);
                        probe.Set("available_source_locator_count", observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
                            ? projection.SourceCatalogue.Count : observations.Select(item => item.SourceKey).Distinct(StringComparer.Ordinal).Count());
                        probe.Complete(review ? "continued" : "skipped", reason);
                    }
                    if (review)
                    {
                        completions.Add(completion);
                        insufficiencyReviewed = true;
                        insufficiencyReview = new
                        {
                            reasonCode = reason,
                            selectionContract = BuildResolvedSelectionContract(request.Handoff.Load),
                            instruction = "Reconsider this insufficiency using the resolved selection contract. The required claim count counts coordinates, not distinct objects when reuse is allowed. The current retrieval sample and any prior searches are bounded; current source locators remain available and this sample does not establish corpus absence. Choose a declared research function to inspect missing item bodies or reformulate a focused search when needed. Do not invent candidates, qualifiers, page relationships or a complete answer. You may still return a terminal insufficiency with the precise unresolved documentary gap. This is one bounded review within the existing call and tool budgets, not permission to repeat searches or exceed them."
                        };
                        nextPhase = $"{phase}-insufficiency-review";
                        continue;
                    }
                }
                if (_options.NativeResearchToolsEnabled && _options.NativeResearchActiveProposalEnabled)
                    RememberActiveProposalEvidence(completion.Content, evidence, observations, request, context);
                if (string.Equals(phase, "writer", StringComparison.Ordinal)
                    && context.CandidateExplorerDossier is not null)
                {
                    try
                    {
                        var proposal = ParseResult(completion.Content, evidence, request);
                        if (RememberLateWriterCandidatesAfterBoundedDossier(
                                request,
                                proposal,
                                observations,
                                context))
                        {
                            await SaveCandidateInventoryCheckpointAsync(
                                    context,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (AdvancedAnalysisProviderException)
                    {
                        // The normal parser and binding correction path below owns
                        // malformed or unsupported terminal proposals.
                    }
                }
                var corrections = FindCandidateBindingCorrections(
                    completion.Content,
                    evidence,
                    observations,
                    request,
                    context.CandidateInventory,
                    context.CandidateExplorerDossier is not null);
                if (corrections.Count == 0)
                    return new SynthesisCompletion(completion, evidence, observations, insufficiencyReviewed);
                completions.Add(completion);
                if (completions.Count >= maximumCalls - reservedFinalCalls)
                    throw new AdvancedAnalysisProviderException(corrections.Any(c => c.ReasonCode == "candidate_evidence_not_visible")
                        ? "advanced_synthesis_candidate_evidence_not_visible"
                        : corrections.Any(c => c.ReasonCode != "candidate_body_required")
                            ? "advanced_synthesis_candidate_identity_not_supported"
                            : "advanced_synthesis_candidate_body_not_supported");
                var proposed = ParseResult(completion.Content, evidence, request);
                candidateSupportFeedback = new
                {
                    corrections,
                    previousProposal = new { proposed.Outcome, proposed.AnswerText, proposed.Claims },
                    instruction = "Correct the listed bindings or selections before returning a terminal answer. Use only currently visible substantive evidence; request research if necessary and available. Preserve supported choices. An identity locator is not the selected item's body. Copy exact documentary item wording or choose another supported item yourself. The code has not selected a replacement or performed further research. This correction consumes the existing call budget."
                };
                nextPhase = $"{phase}-support-correction-{++supportCorrection}";
                continue;
            }

            completions.Add(completion);
            insufficiencyReview = null;
            if (!_options.AdaptiveResearchEnabled)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_disabled");
            if (completions.Count >= maximumCalls - reservedFinalCalls)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_call_budget_exhausted");

            IReadOnlyList<AdvancedAnalysisSearchRequest> queries;
            IReadOnlyList<ResearchWorkspaceItem>? workspaceUpdate = null;
            IReadOnlyList<CandidateInventoryItem>? candidateInventoryUpdates = null;
            IReadOnlyList<CandidateInventoryItem>? mergedCandidateInventory = null;
            IReadOnlyDictionary<string, EvidenceReadWindow>? evidenceReadUpdates = null;
            var blockedCandidateAdmissions = 0;
            try
            {
                if (completion.NativeArgumentFeedback is { } nativeFeedback)
                    throw new AdvancedAnalysisProviderException("advanced_native_tool_arguments_invalid",
                        researchArgumentFeedback: nativeFeedback);
                using var document = JsonDocument.Parse(UnwrapJson(completion.Content));
                evidenceReadUpdates = ParseEvidenceReadRequests(document.RootElement, observations, context);
                if (evidenceReadUpdates.Count > 0 && completion.NativeToolCallsJson is null)
                    throw new JsonException();
                if (document.RootElement.TryGetProperty("researchState", out var state))
                {
                    if (completion.NativeToolCallsJson is null) throw new JsonException();
                    using var current = JsonDocument.Parse(userPrompt);
                    workspaceUpdate = ParseResearchWorkspace(state, current.RootElement);
                }
                if (document.RootElement.TryGetProperty(
                        "candidateInventoryUpdates",
                        out var candidateUpdates))
                {
                    if (completion.NativeToolCallsJson is null) throw new JsonException();
                    using var current = JsonDocument.Parse(userPrompt);
                    candidateInventoryUpdates = ParseCandidateInventoryUpdates(
                        candidateUpdates,
                        current.RootElement);
                    mergedCandidateInventory = MergeCandidateInventory(
                        context.CandidateInventory,
                        candidateInventoryUpdates,
                        out blockedCandidateAdmissions);
                }
                if (!document.RootElement.TryGetProperty("queries", out var values)
                    || values.ValueKind != JsonValueKind.Array
                    || values.GetArrayLength() == 0
                    && workspaceUpdate is null
                    && candidateInventoryUpdates is null
                    && evidenceReadUpdates.Count == 0)
                    throw new JsonException();
                queries = values.GetArrayLength() == 0 ? [] : ParseResearchReview(JsonSerializer.Serialize(new
                {
                    decision = "search_more", queries = values
                }, JsonOptions), request, context.AvailableCategories, observations);
                if (queries.Count > 0 && context.ResourceLimit is not null)
                    throw new AdvancedAnalysisProviderException("advanced_synthesis_research_after_resource_limit");
                EnsureResearchBatchFitsToolBudget(context.Tools, queries, context.PreviouslyExecuted);
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
                if (error is AdvancedAnalysisProviderException
                    { ErrorCode: "advanced_synthesis_research_tool_budget_exceeded" })
                    throw;
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_protocol_invalid");
            }
            argumentFeedback = null;
            blockedCandidateAdmissionsForFeedback = blockedCandidateAdmissions;
            var workspaceChanged = workspaceUpdate is not null
                && JsonSerializer.Serialize(workspaceUpdate, JsonOptions) != JsonSerializer.Serialize(context.Workspace, JsonOptions);
            var candidateInventoryChanged = mergedCandidateInventory is not null
                && JsonSerializer.Serialize(mergedCandidateInventory, JsonOptions)
                != JsonSerializer.Serialize(context.CandidateInventory, JsonOptions);
            var evidenceReadsChanged = evidenceReadUpdates is not null && evidenceReadUpdates.Any(pair =>
                !context.EvidenceReadWindows.TryGetValue(pair.Key, out var priorWindow)
                || !SameEvidenceReadProjection(pair.Key, priorWindow, pair.Value, context));
            var rememberedFocus = RememberFocusedSearches(context.FocusSearches, queries);
            context.FocusSearches.Clear();
            context.FocusSearches.AddRange(rememberedFocus);
            if (!workspaceChanged && !candidateInventoryChanged && !evidenceReadsChanged
                && !queries.Any(query => !context.PreviouslyExecuted.Contains(BuildSearchIdentity(query))))
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
            if (mergedCandidateInventory is not null)
            {
                context.CandidateInventory = mergedCandidateInventory;
                context.CandidateInventoryAdmissionBlockedCount = blockedCandidateAdmissions;
            }
            // Apply a validated batch atomically: window reads reopen needs_body
            // items from the just-merged inventory, never a stale pre-merge copy.
            if (evidenceReadUpdates is not null)
                ApplyEvidenceReadWindows(evidenceReadUpdates, context);
            if (evidenceReadsChanged
                || candidateInventoryChanged && CandidateInventoryEnabled(request))
                await SaveCandidateInventoryCheckpointAsync(context, cancellationToken).ConfigureAwait(false);
            if (workspaceUpdate is not null) context.Workspace = workspaceUpdate;
            var retainedForBatch = RetainedResearchEvidenceIds(context, observations);
            context.Tools.BeginEvidenceBatch(retainedForBatch);
            if (context.Tools.Budget?.EvidenceRotationEnabled is true)
                await SaveCandidateInventoryCheckpointAsync(context, cancellationToken,
                    retainedForBatch.ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
            var nativeResults = new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase);
            var resourceLimit = await ExecuteSearchBatchAsync(context.Tools, queries, context.PreviouslyExecuted,
                context.PriorSearches, context.EvidenceGroups, context.RetrievalQueriesByEvidenceId,
                cancellationToken, nativeResults, allowResourceLimitFeedback: true).ConfigureAwait(false);
            PruneRetiredEvidenceCopies(context);
            RememberLatestExplicitRead(context, queries, nativeResults);
            context.ResourceLimit ??= resourceLimit;
            if (completion.NativeToolCallsJson is { } executedCalls)
                context.NativeTurn = new(executedCalls, queries, nativeResults!, ResponseOutputItemsJson: completion.NativeResponseOutputJson,
                    WorkspaceStored: workspaceChanged,
                    CandidateInventoryStored: candidateInventoryChanged,
                    EvidenceReadsStored: evidenceReadUpdates?.Count > 0);
            followup++;
            nextPhase = $"{phase}-research-followup-{followup}";
        }
    }

    private static void EnsureResearchBatchFitsToolBudget(
        IAdvancedAnalysisToolGateway tools,
        IReadOnlyList<AdvancedAnalysisSearchRequest> queries,
        IReadOnlySet<string> previouslyExecuted)
    {
        if (tools.Budget is not { } budget)
            return;
        var newOperations = queries.Select(BuildSearchIdentity)
            .Where(identity => !previouslyExecuted.Contains(identity))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (newOperations <= budget.RemainingCalls
            && (newOperations == 0 || budget.RemainingElapsedMilliseconds > 0))
            return;
        var reason = newOperations > budget.RemainingCalls
            ? "research_tool_call_budget_exceeded" : "research_tool_time_budget_exhausted";
        throw new AdvancedAnalysisProviderException(
            "advanced_synthesis_research_tool_budget_exceeded",
            researchArgumentFeedback: new(reason, "batch", string.Empty, null, null, 4,
                budget, newOperations));
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

    private static bool IsInsufficientDocumentaryResult(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && ReadString(document.RootElement, "outcome") == "insufficient_documentation";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasOpenResearchLocator(IReadOnlyList<PromptEvidenceItem> observations)
        => observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
            && projection.SourceCatalogue.Any(source => observations.All(item => item.SourceKey != source.SourceKey))
            || observations.Any(item => !string.IsNullOrWhiteSpace(item.SourceKey)
            && (item.ContentRole == RetrievalContentClassifier.NavigationRole
                || item.ContentTruncated && item.NextContentOffset is not null
                || item.SourceOverview is { } overview
                    && (overview.FirstIndexedPhysicalPage < item.PhysicalPageStart
                        || overview.LastIndexedPhysicalPage > item.PhysicalPageEnd
                        || overview.CanonicalChunkCount > observations.Count(other => other.SourceKey == item.SourceKey))));
}
