using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("arbitrary")]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("case")]
    public async Task Answered_grid_rejects_twenty_claims_that_do_not_identify_exactly_the_twenty_requested_coordinates(string defect)
    {
        var ids = Enumerable.Range(1, 20).Select(index => $"C{index}").ToArray();
        if (defect == "arbitrary")
            ids = Enumerable.Range(1, 20).Select(index => $"OTHER{index}").ToArray();
        if (defect == "duplicate") ids[^1] = ids[0];
        if (defect == "outside") ids[^1] = "C21";
        if (defect == "case") ids[^1] = "c20";
        var output = CoordinateAnswer(ids);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""), Completion(output));
        var options = CreateOptions();
        options.ExternalMaximumCallsPerJob = 2;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(answerUnitCount: 20),
                    new RecordingToolGateway(BuildEvidence("E1", "Documented evidence.")),
                    CancellationToken.None));

        Assert.Equal("advanced_writer_claim_coordinates_invalid", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public async Task Coordinate_error_uses_the_existing_single_writer_repair_without_adding_calls()
    {
        var invalid = CoordinateAnswer(Enumerable.Range(1, 20).Select(index => $"OTHER{index}"));
        const string repaired = """{"outcome":"insufficient_documentation","answerText":"Les preuves disponibles ne suffisent pas pour les vingt cases.","claims":[]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""), Completion(invalid), Completion(repaired));
        var options = CreateOptions();
        options.ExternalMaximumCallsPerJob = 3;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20),
                new RecordingToolGateway(BuildEvidence("E1", "Documented evidence.")),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Contains("Repair one SAAIA Writer", factory.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinate_guard_accepts_exact_grid_ids_in_any_claim_array_order_and_does_not_impose_grid_ids_on_flat_lists()
    {
        var claims = Enumerable.Range(1, 20).Reverse().Select(index => new AdvancedAnalysisResultClaim
        {
            ClaimId = $"C{index}", Text = $"Unit {index}", EvidenceIds = ["E1"]
        }).ToArray();
        OpenAiCompatibleAdvancedAnalysisProvider.EnsureAnsweredClaimCoordinates(
            "answered", claims, BuildRequest(answerUnitCount: 20).Handoff.Load);

        var flat = new[]
        {
            new AdvancedAnalysisResultClaim { ClaimId = "first", Text = "First", EvidenceIds = ["E1"] },
            new AdvancedAnalysisResultClaim { ClaimId = "second", Text = "Second", EvidenceIds = ["E1"] }
        };
        OpenAiCompatibleAdvancedAnalysisProvider.EnsureAnsweredClaimCoordinates(
            "answered", flat, BuildFlatNamedItemRequest().Handoff.Load);
    }

    [Fact]
    public void Coordinate_guard_rejects_duplicate_ids_in_a_flat_answer_but_allows_unsupported_grid_cells_to_be_omitted_for_insufficiency()
    {
        var duplicate = Enumerable.Range(1, 2).Select(index => new AdvancedAnalysisResultClaim
        {
            ClaimId = "same", Text = $"Unit {index}", EvidenceIds = ["E1"]
        }).ToArray();
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            OpenAiCompatibleAdvancedAnalysisProvider.EnsureAnsweredClaimCoordinates(
                "answered", duplicate, BuildFlatNamedItemRequest().Handoff.Load));
        Assert.Equal("advanced_writer_claim_coordinates_invalid", error.ErrorCode);
        OpenAiCompatibleAdvancedAnalysisProvider.EnsureAnsweredClaimCoordinates(
            "insufficient_documentation", [], BuildRequest(answerUnitCount: 20).Handoff.Load);
    }

    [Fact]
    public async Task Semantic_critic_insufficiency_is_not_replaced_with_a_structurally_valid_writer_answer()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var evidence = Enumerable.Range(1, 20).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nProcédure {index}.",
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 20).Select(index => new
            {
                key = $"candidate-{index}", exactTitle = $"R{index}",
                sourceKey = "internal-source-1", targetRoles = new[] { roles[(index - 1) % 4] },
                selectedRoles = Array.Empty<string>(), status = "body_verified", note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        const string critic = """{"outcome":"insufficient_documentation","answerText":"Le choix de petit-déjeuner doit être revu avant de fournir un planning complet.","claims":[]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady), Completion(CandidateAnswered(20)), Completion(critic));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateBindingFeedbackEnabled = true;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Contains("doit être revu", result.AnswerText, StringComparison.Ordinal);
        Assert.Equal(5, result.ProviderCallCount);
    }

    [Theory]
    [InlineData("insufficient_quota", "credit_balance_exhausted")]
    [InlineData("insufficient_quota", "insufficient_quota")]
    [InlineData("billing_error", "billing_hard_limit_reached")]
    public async Task Terminal_credit_or_billing_429_is_not_retried_as_transient_rate_limit(string type, string code)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent.Create(new { error = new { type, code, message = "No credits remaining." } })
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(100));
        using var factory = new QueuedHttpClientFactory(response);
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        options.LlmMaximumHttpAttempts = 3;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "unit-test-key")
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(), CancellationToken.None));

        Assert.Equal("advanced_llm_quota_exhausted", error.ErrorCode);
        Assert.False(error.IsRetryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Single(factory.Requests);
        using var ledgerEntry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal("advanced_llm_quota_exhausted", ledgerEntry.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(0m, ledgerEntry.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.Equal(1, ledgerEntry.RootElement.GetProperty("httpAttemptCount").GetInt32());
    }

    [Theory]
    [InlineData("{\"error\":{\"type\":\"rate_limit_exceeded\",\"message\":\"quota or credits\"}}")]
    [InlineData("{\"error\":{\"code\":null,\"type\":\"tokens\"}}")]
    [InlineData("Too many requests")]
    [InlineData("[]")]
    public void Quota_classification_does_not_convert_transient_or_unknown_429_payloads_to_terminal_billing_errors(string body)
        => Assert.False(OpenAiCompatibleAdvancedAnalysisProvider.IsTerminalProviderQuotaError(body));

    private static string CoordinateAnswer(IEnumerable<string> ids)
    {
        var claimIds = ids.ToArray();
        return JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(" ", claimIds.Distinct(StringComparer.Ordinal).Select(id => $"Unit [{id}].")),
            claims = claimIds.Select((id, index) => new
            {
                claimId = id, selectedItem = $"Unit {index + 1}",
                text = $"Documented unit {index + 1}", evidenceIds = new[] { "E1" }
            }).ToArray()
        });
    }
}
