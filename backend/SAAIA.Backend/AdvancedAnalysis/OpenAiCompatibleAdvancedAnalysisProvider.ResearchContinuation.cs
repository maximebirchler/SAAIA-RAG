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
        Dictionary<string, HashSet<string>> RetrievalQueriesByEvidenceId);

    private sealed record SynthesisCompletion(
        CompletionResult Completion,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
        IReadOnlyList<PromptEvidenceItem> PromptEvidence);

    private sealed record CandidateSupportCorrection(string ClaimId, string SelectedItem,
        IReadOnlyList<string> EvidenceIds, string Reason);

    private static IReadOnlyList<CandidateSupportCorrection> FindIdentityOnlyCandidateSupport(
        string raw, IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        AdvancedAnalysisProviderRequest request)
    {
        if (!request.Handoff.Load.StructuredLayout
            || !request.Handoff.Load.AtomicEvidenceMode.Contains("named_item", StringComparison.OrdinalIgnoreCase))
            return [];
        AdvancedAnalysisProviderResult result;
        try { result = ParseResult(raw, evidence, request); }
        catch (AdvancedAnalysisProviderException) { return []; }
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
           Your tool is search_corpus(query, category, topK, documentHint, sourceKey),
           executed by SAAIA in this job's private corpus. researchTools describes
           its actual categories and previous searches. If a needed item's content
           is missing, ambiguous or inconsistent, return this non-final object:
           {"outcome":"research_required","queries":[{"query":"exact observed title or targeted reformulation","sourceKey":"exact observed opaque sourceKey, or empty","category":"allowed category or empty","topK":20}]}.
           Leave documentHint empty when using sourceKey. Do not repeat a search
           with the same query and scope. Do not answer or publish claims in a
           research_required object. SAAIA executes the searches and calls you
           again with revalidated evidence. Request documentary facts, not optional
           user preferences. Only a subsequent terminal result reaches the user.
           Request research only when researchTools.researchAllowed is true.
           A limit on further research does not prove absence from the corpus.
           """;

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
        var nextPhase = phase;
        object? candidateSupportFeedback = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completions.Count >= maximumCalls)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_call_budget_exhausted");
            var evidence = FilterEvidenceToRequestedDocumentSet(request,
                OrderEvidenceForPrompt(context.Tools.Evidence, context.EvidenceGroups));
            var observations = BuildPromptEvidence(request, evidence, context.RetrievalQueriesByEvidenceId);
            var userPrompt = buildUserPrompt(observations);
            if (_options.AdaptiveResearchEnabled || candidateSupportFeedback is not null)
            {
                var payload = JsonNode.Parse(userPrompt)!.AsObject();
                if (_options.AdaptiveResearchEnabled)
                    payload["researchTools"] = JsonSerializer.SerializeToNode(new
                {
                    tool = "search_corpus",
                    researchAllowed = maximumCalls - completions.Count - 1 - reservedFinalCalls > 0,
                    remainingModelCalls = Math.Max(0, maximumCalls - completions.Count - 1 - reservedFinalCalls),
                    maximumQueries = ResolveMaximumPlanQueries(request),
                    availableCategories = context.AvailableCategories,
                    priorSearches = BuildPriorSearchesForPrompt(context.PriorSearches, observations)
                }, JsonOptions);
                if (candidateSupportFeedback is not null)
                    payload["candidateSupportCorrections"] = JsonSerializer.SerializeToNode(candidateSupportFeedback, JsonOptions);
                userPrompt = payload.ToJsonString(JsonOptions);
            }
            var completion = await CompleteJsonAsync(request.JobId,
                nextPhase,
                systemPrompt, userPrompt, maximumTokens, cancellationToken).ConfigureAwait(false);
            if (!RequestsCorpusResearch(completion.Content))
            {
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
            try
            {
                using var document = JsonDocument.Parse(UnwrapJson(completion.Content));
                if (!document.RootElement.TryGetProperty("queries", out var values)
                    || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
                    throw new JsonException();
                queries = ParseResearchReview(JsonSerializer.Serialize(new
                {
                    decision = "search_more", queries = values
                }, JsonOptions), request, context.AvailableCategories, observations);
            }
            catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException)
            {
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_protocol_invalid");
            }
            if (!queries.Any(query => !context.PreviouslyExecuted.Contains(BuildSearchIdentity(query))))
                throw new AdvancedAnalysisProviderException("advanced_synthesis_research_no_progress");
            await ExecuteSearchBatchAsync(context.Tools, queries, context.PreviouslyExecuted,
                context.PriorSearches, context.EvidenceGroups, context.RetrievalQueriesByEvidenceId,
                cancellationToken).ConfigureAwait(false);
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
