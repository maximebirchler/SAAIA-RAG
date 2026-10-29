using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(false, 4, 4)]
    [InlineData(true, 6, 5)]
    public async Task Direct_research_can_observe_an_empty_followup_then_reformulate_within_existing_synthesis_reserve(
        bool critic, int maximumCalls, int expectedCalls)
    {
        var replies = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"first reformulation","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"exact threshold","topK":12}]}"""),
            Completion(NativeAnswered)
        };
        if (critic) replies.Add(Completion(NativeAnswered));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.SemanticCriticEnabled = critic;
        options.ExternalMaximumCallsPerJob = maximumCalls;
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new FeedbackToolGateway((query, number) => new(
            query.Query, number == 3 ? [evidence] : [], [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(expectedCalls, result.ProviderCallCount);
        Assert.Equal(expectedCalls, factory.Requests.Count);
        Assert.Equal(["overview", "first reformulation", "exact threshold"],
            gateway.Searches.Select(search => search.Query));
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds.Single());
        using var review = FeedbackUserPayload(factory.Requests[2].Body);
        Assert.Empty(review.RootElement.GetProperty("observations").EnumerateArray());
        Assert.Equal(2, review.RootElement.GetProperty("priorSearches").GetArrayLength());
        Assert.Equal(0, review.RootElement.GetProperty("researchBudget")
            .GetProperty("remainingReviewCallsAfterThisTurn").GetInt32());
        Assert.Equal(critic ? 3 : 1, review.RootElement.GetProperty("researchBudget")
            .GetProperty("reservedSynthesisCalls").GetInt32());
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task Empty_direct_research_never_expands_the_configured_call_cap_or_uses_synthesis_reserve(
        int maximumCalls, int expectedCalls)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"alternate","topK":12}]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = maximumCalls;
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [], [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(expectedCalls, result.ProviderCallCount);
        Assert.Equal(expectedCalls, factory.Requests.Count);
        Assert.Equal(expectedCalls, gateway.Searches.Count);
        Assert.Equal(maximumCalls, options.ExternalMaximumCallsPerJob);
    }

    [Fact]
    public async Task Direct_review_stops_a_batch_of_already_executed_searches_without_spending_another_review()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"OVERVIEW","topK":12}]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 8;
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [], [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Exhausted_documentary_budget_prevents_an_unusable_paid_review()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 8;
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [], [], 1, number), 1);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(factory.Requests);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Named_document_query_uses_the_identity_as_a_hint_and_the_model_subject_as_retrieval_text()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"NIST_CSF_2_0.pdf exact threshold","topK":12}]}"""),
            Completion(NativeAnswered));
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.", "NIST_CSF_2_0.pdf");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query,
            query.Query == "exact threshold" && query.DocumentHint == "NIST_CSF_2_0.pdf" ? [evidence] : [],
            [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, CreateOptions(), null)
            .ExecuteAsync(BuildNamedDocumentRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var search = Assert.Single(gateway.Searches);
        Assert.Equal("exact threshold", search.Query);
        Assert.Equal("NIST_CSF_2_0.pdf", search.DocumentHint);
        Assert.Equal(2, result.ProviderCallCount);
        using var request = JsonDocument.Parse(factory.Requests[0].Body);
        var system = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("\"documentHint\"", system);
        Assert.DoesNotContain("Retain that document's complete identifier in the", system);
    }

    [Fact]
    public async Task Named_document_followup_keeps_the_required_identity_separate_from_reformulated_content_terms()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"NIST_CSF_2_0.pdf exact threshold","topK":12}]}"""),
            Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.", "NIST_CSF_2_0.pdf");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query,
            query.Query == "exact threshold" && query.DocumentHint == "NIST_CSF_2_0.pdf" ? [evidence] : [],
            [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildNamedDocumentRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(["overview", "exact threshold"], gateway.Searches.Select(search => search.Query));
        Assert.All(gateway.Searches, search => Assert.Equal("NIST_CSF_2_0.pdf", search.DocumentHint));
        Assert.Equal(3, result.ProviderCallCount);
    }

    [Theory]
    [InlineData("resolved", 1, "Documents/known.pdf")]
    [InlineData("ambiguous", 2, null)]
    [InlineData("no_match", 0, null)]
    public async Task Empty_search_feedback_distinguishes_document_resolution_and_retrieval_without_making_a_source_handle(
        string status, int candidates, string? path)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [], ["dense"], 1, number,
            RetrievalDiagnostic: FeedbackRetrievalDiagnostic(),
            DocumentResolution: new(status, candidates, path)));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        using var payload = FeedbackUserPayload(factory.Requests[1].Body);
        var search = payload.RootElement.GetProperty("priorSearches")[0];
        Assert.Equal(status, search.GetProperty("documentResolution").GetProperty("status").GetString());
        Assert.Equal(candidates, search.GetProperty("documentResolution").GetProperty("candidateCount").GetInt32());
        Assert.Equal(path, search.GetProperty("documentResolution").GetProperty("resolvedDocPath").GetString());
        Assert.Equal("dense", search.GetProperty("degradedRetrievers")[0].GetString());
        Assert.Equal(0, search.GetProperty("retrievalDiagnostic").GetProperty("returnedEvidenceCandidates").GetInt32());
        Assert.False(search.GetProperty("retrievalDiagnostic").TryGetProperty("phases", out _));
        Assert.Equal(1, search.GetProperty("retrievalDiagnostic").GetProperty("omittedPhases").GetInt32());
        Assert.Equal(JsonValueKind.Null, search.GetProperty("sourceKey").ValueKind);
        Assert.Empty(payload.RootElement.GetProperty("observations").EnumerateArray());
        Assert.DoesNotContain("CONTENT_ONLY_DO_NOT_LEAK", factory.Requests[1].Body);
        Assert.DoesNotContain("RAW_DIAGNOSTIC_QUERY", factory.Requests[1].Body);
    }

    [Fact]
    public async Task Native_tool_results_share_compact_retrieval_feedback_without_candidate_snippets_or_invented_evidence()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            NativeCompletion(("search_corpus", """{"query":"alternate","sourceKey":"","category":"","documentHint":"","topK":20}""")),
            Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query,
            number == 1 ? [evidence] : [], ["dense"], 1, number,
            RetrievalDiagnostic: FeedbackRetrievalDiagnostic(),
            DocumentResolution: new("no_match", 0, null)));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        using var request = JsonDocument.Parse(factory.Requests[3].Body);
        var toolMessage = request.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "tool");
        using var output = JsonDocument.Parse(toolMessage.GetProperty("content").GetString()!);
        Assert.Equal("no_match", output.RootElement.GetProperty("documentResolution").GetProperty("status").GetString());
        Assert.Equal("dense", output.RootElement.GetProperty("degradedRetrievers")[0].GetString());
        Assert.Equal(0, output.RootElement.GetProperty("retrievalDiagnostic").GetProperty("returnedEvidenceCandidates").GetInt32());
        Assert.Empty(output.RootElement.GetProperty("visibleEvidenceIds").EnumerateArray());
        Assert.DoesNotContain("CONTENT_ONLY_DO_NOT_LEAK", factory.Requests[3].Body);
        Assert.DoesNotContain("RAW_DIAGNOSTIC_QUERY", factory.Requests[3].Body);
    }

    [Fact]
    public async Task Resolved_document_metadata_without_observed_evidence_does_not_authorize_a_canonical_read()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"search_more","queries":[{"operation":"read_source","sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":12}]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [], [], 1, number,
            DocumentResolution: new("resolved", 1, "Documents/known.pdf")));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));

        Assert.Equal("advanced_research_review_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Retrieval_feedback_caps_phase_reason_and_degradation_metadata_without_copying_candidate_payloads()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var diagnostic = FeedbackRetrievalDiagnostic();
        diagnostic = diagnostic with
        {
            Phases = Enumerable.Range(0, 50).Select(number => diagnostic.Phases[0] with { Name = "phase-" + number }).ToArray(),
            RejectionCounts = Enumerable.Range(0, 50).ToDictionary(number => "reason-" + number, _ => 1)
        };
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [],
            Enumerable.Range(0, 50).Select(item => "retriever-" + item).ToArray(), 1, number,
            RetrievalDiagnostic: diagnostic));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        using var payload = FeedbackUserPayload(factory.Requests[1].Body);
        var search = payload.RootElement.GetProperty("priorSearches")[0];
        var feedback = search.GetProperty("retrievalDiagnostic");
        Assert.False(feedback.TryGetProperty("phases", out _));
        Assert.Equal(50, feedback.GetProperty("omittedPhases").GetInt32());
        Assert.Equal(16, feedback.GetProperty("rejectionCounts").GetArrayLength());
        Assert.Equal(34, feedback.GetProperty("omittedRejectionReasons").GetInt32());
        Assert.Equal(16, search.GetProperty("degradedRetrievers").GetArrayLength());
        Assert.True(feedback.GetRawText().Length < 6_000);
        Assert.DoesNotContain("CONTENT_ONLY_DO_NOT_LEAK", factory.Requests[1].Body);
    }

    private static JsonDocument FeedbackUserPayload(string envelope)
    {
        using var request = JsonDocument.Parse(envelope);
        return JsonDocument.Parse(request.RootElement.GetProperty("messages").EnumerateArray()
            .Last(message => message.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_document_resolution_path_is_explicitly_omitted_without_truncated_identity_or_scope_change(bool native)
    {
        var replies = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}""")
        };
        if (native)
        {
            replies.Add(NativeCompletion(("search_corpus",
                """{"query":"alternate","sourceKey":"","category":"","documentHint":"","topK":20}""")));
            replies.Add(Completion(NativeAnswered));
        }
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.NativeResearchToolsEnabled = native;
        options.ExternalMaximumCallsPerJob = native ? 5 : 3;
        var path = "Documents/OVERSIZED_IDENTITY_" + new string('x', 2_100) + ".pdf";
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query,
            native && number == 1 ? [evidence] : [], [], 1, number,
            DocumentResolution: new("resolved", 1, path)));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal(native ? "answered" : "insufficient_documentation", result.Outcome);
        using var review = FeedbackUserPayload(factory.Requests[1].Body);
        AssertOmittedResolution(review.RootElement.GetProperty("priorSearches")[0].GetProperty("documentResolution"));
        if (native)
        {
            using var next = JsonDocument.Parse(factory.Requests[3].Body);
            var tool = next.RootElement.GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "tool");
            using var output = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
            AssertOmittedResolution(output.RootElement.GetProperty("documentResolution"));
        }
        Assert.All(factory.Requests, request => Assert.DoesNotContain("OVERSIZED_IDENTITY_", request.Body));
        Assert.All(gateway.Searches, search => Assert.Null(search.DocPath));

        static void AssertOmittedResolution(JsonElement resolution)
        {
            Assert.Equal("resolved", resolution.GetProperty("status").GetString());
            Assert.Equal(1, resolution.GetProperty("candidateCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, resolution.GetProperty("resolvedDocPath").ValueKind);
            Assert.True(resolution.GetProperty("resolvedDocPathOmitted").GetBoolean());
        }
    }

    private static RagRetrievalDiagnosticsDto FeedbackRetrievalDiagnostic()
    {
        var candidate = new RagRetrievalCandidateDiagnosticsDto(1, 0.5, null, "sparse", null,
            "doc", "known.pdf", "Documents/known.pdf", 5, 5, "chunk", 0, null, null, null,
            "CONTENT_ONLY_DO_NOT_LEAK");
        return new("broad", "RAW_DIAGNOSTIC_QUERY", "RAW_DIAGNOSTIC_QUERY", "RAW_DIAGNOSTIC_QUERY",
            null, null, 12, 100, 12, 12,
            [new("sparse", "sparse", 0, 1, [candidate])],
            new(0, 12, 12, 12, 0, [candidate]),
            RejectionCounts: new Dictionary<string, int> { ["source_identity_rejected"] = 1 });
    }

    private sealed class FeedbackToolGateway(
        Func<AdvancedAnalysisSearchRequest, int, AdvancedAnalysisSearchObservation> respond,
        int maximumCalls = 32) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public AdvancedAnalysisToolBudget? Budget => new(maximumCalls, Searches.Count, 60_000, Searches.Count);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["Corpus"]);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            var observation = respond(request, Searches.Count);
            foreach (var evidence in observation.Evidence)
                if (_evidence.All(existing => existing.Reference.EvidenceId != evidence.Reference.EvidenceId))
                    _evidence.Add(evidence);
            return Task.FromResult(observation);
        }
    }
}
