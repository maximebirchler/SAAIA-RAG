using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", "agent", "retry")]
    [InlineData("chat-completions", "agent", "retry")]
    [InlineData("responses", "agent", "omitted")]
    [InlineData("chat-completions", "agent", "omitted")]
    [InlineData("responses", "agent", "other_source_read")]
    [InlineData("chat-completions", "agent", "other_source_read")]
    [InlineData("responses", "reviewed", "retry")]
    public async Task BootstrapEvidenceCapacity_partial_initial_batch_reaches_writer_and_native_rotation(
        string protocol, string topology, string continuation)
    {
        var gateway = new BootstrapCapacityGateway();
        var operation = continuation == "other_source_read" ? "read_source" : "search_corpus";
        var query = continuation == "omitted" ? "bootstrap-7" : "bootstrap-6";
        var arguments = continuation == "other_source_read"
            ? """{"sourceKey":"internal-source-2","pageStart":2,"pageEnd":5,"topK":12}"""
            : JsonSerializer.Serialize(new { query, sourceKey = "", category = "", documentHint = "", topK = 32 });
        var responses = new List<HttpResponseMessage>
        {
            Completion(BootstrapPlan()),
            PostResearchFunction(protocol, operation, arguments)
        };
        if (continuation != "other_source_read")
            responses.Add(PostResearchFunction(protocol, "read_source",
                """{"sourceKey":"internal-source-2","pageStart":2,"pageEnd":5,"topK":12}"""));
        responses.Add(EarlyReply(protocol, RepeatableTwentyAnswer()));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = false; options.NativeResearchTopology = topology;
        options.MaximumPlanQueries = 8; options.ExternalMaximumCallsPerJob = 4;
        options.MaximumEvidencePromptCharacters = 20_000;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(continuation == "other_source_read" ? 3 : 4, factory.Requests.Count);
        Assert.Equal(continuation == "other_source_read" ? 7 : 8, gateway.Searches.Count);
        Assert.Equal(110, gateway.CountAtInitialRefusal);
        Assert.Equal(19, gateway.RequestedAtInitialRefusal);
        Assert.True(gateway.AtomicRefusalPreservedEveryPriorBody);
        Assert.DoesNotContain(gateway.Searches, request => request.Query == "bootstrap-8");
        Assert.True(gateway.Budget.EvidenceRotationEnabled);
        Assert.InRange(gateway.Evidence.Count, 1, 128);
        Assert.All(gateway.TargetBodies, item => Assert.Contains(gateway.Evidence,
            stored => stored.Reference.EvidenceId == item.Reference.EvidenceId && stored.Content == item.Content));
        if (continuation == "other_source_read")
        {
            Assert.Equal(gateway.OtherSourceId, gateway.Searches[^1].DocId);
            Assert.NotEqual(gateway.PrimarySourceId, gateway.Searches[^1].DocId);
        }
        else
        {
            Assert.Equal(query, gateway.Searches[6].Query);
            Assert.Equal("read_source", gateway.Searches[^1].Operation);
            Assert.Equal(gateway.OtherSourceId, gateway.Searches[^1].DocId);
            Assert.Single(gateway.RetiredEvidenceIds);
            Assert.Equal(128, gateway.Evidence.Count);
        }
        var writer = WorkspaceUser(factory.Requests[1].Body);
        Assert.True(writer.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.True(writer.GetProperty("researchTools").GetProperty("documentaryBudget")
            .GetProperty("evidenceRotationEnabled").GetBoolean());
        Assert.False(writer.TryGetProperty("researchResourceLimit", out _));
        var bootstrap = writer.GetProperty("bootstrapResearch");
        Assert.Equal("initial_batch_interrupted", bootstrap.GetProperty("status").GetString());
        Assert.Equal(128, bootstrap.GetProperty("maximumEvidenceItems").GetInt32());
        Assert.Equal(110, bootstrap.GetProperty("consumedEvidenceItems").GetInt32());
        Assert.Equal(19, bootstrap.GetProperty("requestedNewEvidenceItems").GetInt32());
        Assert.Equal(5, bootstrap.GetProperty("successfulQueryCount").GetInt32());
        Assert.Equal(2, bootstrap.GetProperty("omittedOperationCount").GetInt32());
        Assert.Equal(new[] { "bootstrap-7", "bootstrap-8" }, bootstrap.GetProperty("omittedQueries")
            .EnumerateArray().Select(item => item.GetProperty("query").GetString()));
        var priors = writer.GetProperty("researchTools").GetProperty("priorSearches").EnumerateArray().ToArray();
        Assert.Equal(6, priors.Length);
        Assert.All(priors.Take(5), item => Assert.Equal("succeeded", item.GetProperty("executionStatus").GetString()));
        Assert.Equal("bootstrap-6", priors[^1].GetProperty("query").GetString());
        Assert.Equal("resource_limit_before_evidence_admission", priors[^1].GetProperty("executionStatus").GetString());
        Assert.Equal(0, priors[^1].GetProperty("admittedEvidenceCount").GetInt32());
        Assert.True(priors[^1].GetProperty("retryAllowed").GetBoolean());
        var followup = WorkspaceUser(factory.Requests[2].Body);
        Assert.False(followup.TryGetProperty("researchResourceLimit", out _));
        Assert.DoesNotContain("search_already_executed", factory.Requests[2].Body, StringComparison.Ordinal);
        var final = WorkspaceUser(factory.Requests[^1].Body);
        Assert.Contains(final.GetProperty("evidence").EnumerateArray(), item =>
            item.GetProperty("evidenceId").GetString() == "E4"
            && item.GetProperty("content").GetString() == gateway.TargetBodies[3].Content);
        var diagnostic = Assert.Single(logger.Events, item => item.Values.GetValueOrDefault("Operation")?.ToString() == "advanced.bootstrap_research");
        Assert.Equal("continued", diagnostic.Values["Outcome"]);
        using var measures = JsonDocument.Parse(diagnostic.Values["Measurements"]!.ToString()!);
        Assert.Equal(110, measures.RootElement.GetProperty("admitted_evidence_items").GetInt32());
        Assert.Equal(19, measures.RootElement.GetProperty("requested_new_evidence_items").GetInt32());
        Assert.Equal(128, measures.RootElement.GetProperty("maximum_evidence_items").GetInt32());
        Assert.Equal(6, measures.RootElement.GetProperty("executed_operation_count").GetInt32());
        Assert.Equal(5, measures.RootElement.GetProperty("successful_operation_count").GetInt32());
        Assert.Equal(2, measures.RootElement.GetProperty("omitted_operation_count").GetInt32());
        Assert.True(measures.RootElement.GetProperty("research_can_continue").GetBoolean());
        Assert.True(measures.RootElement.GetProperty("working_set_rotation_enabled").GetBoolean());
        Assert.Equal(0, measures.RootElement.GetProperty("diagnostic_dropped_measurements").GetInt32());
        Assert.Equal(0, measures.RootElement.GetProperty("diagnostic_truncated_values").GetInt32());
        Assert.DoesNotContain("bootstrap-", measures.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task BootstrapEvidenceCapacity_security_failure_after_partial_success_is_still_fatal(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(BootstrapPlan()));
        var gateway = new BootstrapCapacityGateway("retrieval_evidence_revalidation_failed");
        var options = WorkspaceOptions(protocol); options.MaximumPlanQueries = 8;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None));
        Assert.Equal("retrieval_evidence_revalidation_failed", error.ErrorCode);
        Assert.Equal(110, gateway.Evidence.Count);
        Assert.Equal(6, gateway.Searches.Count);
        Assert.Single(factory.Requests);
        Assert.False(gateway.Budget.EvidenceRotationEnabled);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task BootstrapEvidenceCapacity_feedback_previews_are_bounded_and_explicit(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(BootstrapPlan(longPreviews: true)),
            EarlyReply(protocol, EarlyInsufficient));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = false;
        options.MaximumPlanQueries = 8; options.ExternalMaximumCallsPerJob = 2;
        options.MaximumEvidencePromptCharacters = 20_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), new BootstrapCapacityGateway(), CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        var feedback = WorkspaceUser(factory.Requests[1].Body).GetProperty("bootstrapResearch");
        Assert.True(feedback.GetProperty("refusedQuery").GetProperty("queryTruncated").GetBoolean());
        Assert.Equal(256, feedback.GetProperty("refusedQuery").GetProperty("query").GetString()!.Length);
        Assert.All(feedback.GetProperty("omittedQueries").EnumerateArray(), item =>
        {
            Assert.True(item.GetProperty("queryTruncated").GetBoolean());
            Assert.Equal(256, item.GetProperty("query").GetString()!.Length);
        });
        Assert.True(feedback.GetRawText().Length < 4_000);
        Assert.False(feedback.GetProperty("researchCanContinue").GetBoolean());
    }

    private static string BootstrapPlan(bool longPreviews = false)
        => JsonSerializer.Serialize(new
        {
            selectionMode = "repeatable_named_items",
            queries = Enumerable.Range(1, 8).Select(index => new
            {
                query = "bootstrap-" + index + (longPreviews && index >= 6 ? " " + new string('x', 2_000) : ""), topK = 32
            })
        });

    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task BootstrapEvidenceCapacity_failed_history_is_labeled_and_does_not_blacklist_retry(
        string protocol, bool reviewed)
    {
        var body = BuildEvidence("E1", "Le seuil est 17 bar.");
        var request = BuildDirectRequest() with
        {
            PreviousToolEvents = [
                new(1, 1, "retrieval.search", "failed", new("previous failed scope"), [], [], 1, "accumulated_evidence_limit_exceeded"),
                new(2, 1, "retrieval.search", "failed", new("overview", TopK: 12), [], [], 1, "accumulated_evidence_limit_exceeded"),
                new(3, 1, "retrieval.search", "legacy_unknown", new("legacy scope"), [], [], 1, null)]
        };
        var responses = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}""")
        };
        if (reviewed) responses.Add(Completion("""{"decision":"ready","queries":[]}"""));
        responses.Add(EarlyReply(protocol, NativeAnswered));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [body], [], 1, number));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = false;
        options.NativeResearchTopology = reviewed ? "reviewed" : "agent";
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal("overview", Assert.Single(gateway.Searches).Query);
        Assert.Equal(reviewed ? 3 : 2, factory.Requests.Count);
        var searches = WorkspaceUser(factory.Requests[^1].Body).GetProperty("researchTools")
            .GetProperty("priorSearches").EnumerateArray().ToArray();
        Assert.Equal(4, searches.Length);
        Assert.Equal("failed", searches[0].GetProperty("executionStatus").GetString());
        Assert.Equal("failed", searches[1].GetProperty("executionStatus").GetString());
        Assert.True(searches[0].GetProperty("retryAllowed").GetBoolean());
        Assert.False(searches[1].GetProperty("retryAllowed").GetBoolean());
        Assert.Equal("unknown", searches[2].GetProperty("executionStatus").GetString());
        Assert.Equal("succeeded", searches[3].GetProperty("executionStatus").GetString());
        if (reviewed)
        {
            var system = CorpusSearchSystem(factory.Requests[1].Body);
            Assert.Contains("Do not repeat a succeeded search", system, StringComparison.Ordinal);
            Assert.Contains("retryAllowed is explicitly true", system, StringComparison.Ordinal);
            Assert.Contains("Unknown execution status is not a", system, StringComparison.Ordinal);
            Assert.DoesNotContain("Do not repeat an identical search with identical filters", system, StringComparison.Ordinal);
            Assert.DoesNotContain("subtypes absent from priorQueries", system, StringComparison.Ordinal);
            var history = WorkspaceUser(factory.Requests[1].Body).GetProperty("priorSearches");
            Assert.Equal("failed", history[0].GetProperty("executionStatus").GetString());
            Assert.Equal("failed", history[1].GetProperty("executionStatus").GetString());
            Assert.Equal("unknown", history[2].GetProperty("executionStatus").GetString());
            Assert.Equal("succeeded", history[3].GetProperty("executionStatus").GetString());
            Assert.True(history[0].GetProperty("retryAllowed").GetBoolean());
            Assert.False(history[1].GetProperty("retryAllowed").GetBoolean());
        }
    }

    private sealed class BootstrapCapacityGateway(string? failureCode = null) : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisToolGateway _active = WorkingSetGateway(128);
        public string PrimarySourceId => "11111111-1111-4111-8111-111111111111";
        public string OtherSourceId => TargetBodies[0].Reference.DocId;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> TargetBodies { get; } = PostResearchBodies();
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public List<string> RetiredEvidenceIds { get; } = [];
        public int CountAtInitialRefusal { get; private set; }
        public int? RequestedAtInitialRefusal { get; private set; }
        public bool AtomicRefusalPreservedEveryPriorBody { get; private set; }
        private AdvancedAnalysisResolvedEvidence[]? _initial;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active.Evidence;
        public AdvancedAnalysisToolBudget Budget => _active.Budget with { ConsumedCalls = Searches.Count };
        public void BeginEvidenceBatch(IReadOnlyCollection<string> ids) => _active.BeginEvidenceBatch(ids);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Searches.Add(request);
            _initial ??= Enumerable.Range(0, 110).Select(index => index == 1 ? BuildEvidence("bootstrap-other-locator",
                    "Table of contents\nOptionA 2\nOptionB 3\nOptionC 4\nOptionD 5", TargetBodies[0].Reference.FileName,
                    TargetBodies[0].Reference.DocId, TargetBodies[0].Reference.RevisionId, exactTitle: "Table of contents")
                    with { SourceOverview = new(1, 40, 100) }
                : WorkingSetBody("bootstrap-body-" + index, "Canonical documented body " + index + ".")).ToArray();
            if (Searches.Count == 6 && failureCode is not null)
                throw new AdvancedAnalysisToolException(failureCode);
            var results = Searches.Count <= 5 ? _initial.Skip((Searches.Count - 1) * 22).Take(22).ToArray()
                : request.Operation == "read_source" ? TargetBodies.ToArray()
                : TargetBodies.Concat(Enumerable.Range(0, 15).Select(index => WorkingSetBody("refused-body-" + index,
                    "Complete canonical new body " + index + "."))).ToArray();
            var before = _active.Evidence;
            try { RetiredEvidenceIds.AddRange(_active.AdmitRevalidatedEvidence(results)); }
            catch (AdvancedAnalysisToolException error) when (Searches.Count == 6 && error.ErrorCode == "accumulated_evidence_limit_exceeded")
            {
                CountAtInitialRefusal = _active.Evidence.Count;
                RequestedAtInitialRefusal = error.ResourceLimit?.RequestedNewEvidenceItems;
                AtomicRefusalPreservedEveryPriorBody = before.SequenceEqual(_active.Evidence)
                    && results.All(item => _active.Evidence.All(prior => prior.Reference.EvidenceId != item.Reference.EvidenceId));
                throw;
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, results, [], 1, Searches.Count));
        }
    }
}
