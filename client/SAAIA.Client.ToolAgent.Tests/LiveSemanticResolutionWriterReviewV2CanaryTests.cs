using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSemanticResolutionWriterReviewV2CanaryTests(
    ITestOutputHelper output)
{
    private const string LiveFlag =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_WRITER_REVIEW_V2_A482_CANARY";
    private const string OutputVariable =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_WRITER_REVIEW_V2_A482_CANARY_OUTPUT";
    private const string LiveFlagV5 =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_WRITER_REVIEW_V5_A482_CANARY";
    private const string OutputVariableV5 =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_WRITER_REVIEW_V5_A482_CANARY_OUTPUT";
    private const string LiveFlagA531 =
        "SAAIA_LIVE_SEMANTIC_WRITER_HANDOFF_A531";
    private const string OutputVariableA531 =
        "SAAIA_LIVE_SEMANTIC_WRITER_HANDOFF_A531_OUTPUT";
    private const string LiveFlagA537 =
        "SAAIA_LIVE_RESOLUTION_POLICY_A537";
    private const string OutputVariableA537 =
        "SAAIA_LIVE_RESOLUTION_POLICY_A537_OUTPUT";
    private const string LiveFlagA545 =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_V6_A545";
    private const string OutputVariableA545 =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_V6_A545_OUTPUT";
    private const string ResolutionContract =
        "source_backed_semantic_resolution_v4";
    private const string ResolutionContractV5 =
        "source_backed_semantic_resolution_v5";
    private const string ResolutionContractV6 =
        "source_backed_semantic_resolution_v6";
    private const string WriterContract = "source_backed_flat_writer_v1";
    private const string ReviewerTool = "submit_semantic_review";

    [Fact]
    public void V2_a482_canary_fixtures_match_the_frozen_fifth_v3_cases()
    {
        var sourceMethod = typeof(LiveSemanticAnswerTransactionCanaryTests)
            .GetMethod("BuildFifthV3Cases", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sourceMethod);
        var sourceCases = Assert.IsAssignableFrom<IEnumerable>(
                sourceMethod!.Invoke(null, null))
            .Cast<object>()
            .Select(ToFixtureSnapshot)
            .ToArray();
        var v2Cases = BuildA482Cases()
            .Select(canaryCase => new FixtureSnapshot(
                canaryCase.Id,
                canaryCase.Domain,
                canaryCase.Question,
                canaryCase.Language,
                canaryCase.QuestionFocus,
                canaryCase.SemanticPlan,
                canaryCase.ExpectedDecision,
                canaryCase.ExpectedEvidenceIds,
                canaryCase.RequiredPatterns,
                canaryCase.Excerpts,
                canaryCase.ExpectedAdequacy,
                canaryCase.ExpectedNextCapability,
                canaryCase.ExpectedLeadEvidenceIds))
            .ToArray();

        Assert.Equal(
            JsonSerializer.Serialize(sourceCases),
            JsonSerializer.Serialize(v2Cases));
    }

    [Theory]
    [InlineData("answer", "requested_information_present", true, false, "NONE", "E1", "E1")]
    [InlineData("research", "requested_information_missing", false, false, "NONE", "", "NONE")]
    [InlineData("clarify", "user_clarification_required", false, true, "NONE", "", "NONE")]
    [InlineData("context", "visible_context_required", false, false, "E1", "", "E1")]
    public void V5_raw_resolution_observation_derives_compatibility_aliases(
        string decision,
        string expectedAdequacy,
        bool expectedComplete,
        bool expectedMissingInput,
        string expectedVisibleContext,
        string expectedSelectedEvidence,
        string expectedLead)
    {
        var raw = JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds = decision is "answer" or "context"
                ? new[] { "E1" }
                : Array.Empty<string>(),
            reason = "La décision respecte le contrat minimal attendu."
        });

        var observed = ParseResolution(raw);

        Assert.Equal(decision, observed.Decision);
        Assert.Equal(expectedAdequacy, observed.AnswerAdequacy);
        Assert.Equal(expectedComplete, observed.RequestedDeliverableComplete);
        Assert.Equal(expectedMissingInput,
            observed.MissingUserInputPreventsUniqueResult);
        Assert.Equal(expectedVisibleContext, observed.VisibleContextEvidenceId);
        Assert.Equal(
            string.IsNullOrEmpty(expectedSelectedEvidence)
                ? Array.Empty<string>()
                : new[] { expectedSelectedEvidence },
            observed.SelectedEvidenceIds);
        Assert.Equal(expectedLead, observed.LeadEvidenceId);
    }

    [Fact]
    public void V5_raw_resolution_observation_rejects_redundant_alias()
    {
        var raw = JsonSerializer.Serialize(new
        {
            decision = "answer",
            evidenceIds = new[] { "E1" },
            reason = "La preuve visible permet la réponse demandée.",
            answerAdequacy = "requested_information_present"
        });

        Assert.False(ParseResolution(raw).ShapeValid);
    }

    [Theory]
    [InlineData("answer", new string[0])]
    [InlineData("context", new[] { "E1", "E2" })]
    [InlineData("research", new[] { "E1" })]
    [InlineData("clarify", new[] { "E1" })]
    public void V5_raw_resolution_observation_rejects_invalid_cardinality(
        string decision,
        string[] evidenceIds)
    {
        var raw = JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds,
            reason = "La cardinalité doit suivre la décision sémantique."
        });

        Assert.False(ParseResolution(raw).ShapeValid);
    }

    [Theory]
    [InlineData("answer", new[] { "E1", "E2" }, new[] { "E2" },
        "requested_information_present", "NONE", new[] { "E2", "E1" })]
    [InlineData("context", new[] { "E1" }, new[] { "E1" },
        "visible_context_required", "E1", new[] { "E1" })]
    [InlineData("research", new string[0], new string[0],
        "requested_information_missing", "NONE", new string[0])]
    [InlineData("clarify", new string[0], new string[0],
        "user_clarification_required", "NONE", new string[0])]
    public void A545_v6_raw_resolution_observation_preserves_explicit_roles(
        string decision,
        string[] evidenceIds,
        string[] leadEvidenceIds,
        string expectedAdequacy,
        string expectedVisibleContext,
        string[] expectedResolvedSelection)
    {
        var raw = JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds,
            leadEvidenceIds,
            reason = "La décision V6 conserve explicitement les rôles des preuves."
        });

        var observed = ParseResolution(raw);

        Assert.True(observed.ShapeValid);
        Assert.Equal(decision, observed.Decision);
        Assert.Equal(expectedAdequacy, observed.AnswerAdequacy);
        Assert.Equal(expectedVisibleContext, observed.VisibleContextEvidenceId);
        Assert.Equal(evidenceIds, observed.RawEvidenceIds);
        Assert.Equal(leadEvidenceIds, observed.RawLeadEvidenceIds);
        Assert.Equal(leadEvidenceIds, observed.LeadEvidenceIds);
        Assert.Equal(expectedResolvedSelection, observed.SelectedEvidenceIds);
    }

    [Theory]
    [InlineData("answer", new[] { "E1", "E2" }, new string[0])]
    [InlineData("answer", new[] { "E1" }, new[] { "E2" })]
    [InlineData("context", new[] { "E1" }, new[] { "E2" })]
    [InlineData("research", new[] { "E1" }, new string[0])]
    [InlineData("clarify", new string[0], new[] { "E1" })]
    public void A545_v6_raw_resolution_observation_rejects_role_mismatch(
        string decision,
        string[] evidenceIds,
        string[] leadEvidenceIds)
    {
        var raw = JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds,
            leadEvidenceIds,
            reason = "La cardinalité V6 doit respecter la décision sémantique."
        });

        Assert.False(ParseResolution(raw).ShapeValid);
    }

    [Fact]
    public async Task Frozen_evidence_executor_accepts_normalized_internal_search_once()
    {
        var canaryCase = BuildA482Cases()[0];
        var evidence = BuildToolResults(canaryCase);
        var executor = new FrozenEvidenceExecutor(evidence);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            query = canaryCase.Question,
            topK = 5
        });

        var observed = await executor.ExecuteToolCallAsync(
            BuildIntake(canaryCase),
            "rag.search",
            arguments,
            CancellationToken.None);

        Assert.Same(evidence, observed);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Frozen_evidence_executor_rejects_external_name_after_normalization_boundary()
    {
        var canaryCase = BuildA482Cases()[0];
        var executor = new FrozenEvidenceExecutor(BuildToolResults(canaryCase));
        var arguments = JsonSerializer.SerializeToElement(new
        {
            query = canaryCase.Question,
            topK = 5
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteToolCallAsync(
                BuildIntake(canaryCase),
                "rag_search",
                arguments,
                CancellationToken.None));

        Assert.Contains("observed 1:rag_search", exception.Message);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Frozen_evidence_executor_rejects_second_internal_search_call()
    {
        var canaryCase = BuildA482Cases()[0];
        var executor = new FrozenEvidenceExecutor(BuildToolResults(canaryCase));
        var arguments = JsonSerializer.SerializeToElement(new
        {
            query = canaryCase.Question,
            topK = 5
        });

        await executor.ExecuteToolCallAsync(
            BuildIntake(canaryCase),
            "rag.search",
            arguments,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteToolCallAsync(
                BuildIntake(canaryCase),
                "rag.search",
                arguments,
                CancellationToken.None));

        Assert.Contains("observed 2:rag.search", exception.Message);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public Task Live_qwen_semantic_resolution_writer_review_v2_matches_five_a482_cases_when_enabled()
        => RunLiveCanaryAsync(new CanaryProfile(
            LiveFlag,
            OutputVariable,
            ResolutionContract,
            "semantic_resolution_writer_review_v2_a482_canary_v1",
            "a498-live-semantic-resolution-writer-review-v2-a482",
            "semantic-resolution-writer-review-v2-canary.json",
            "semantic-resolution-writer-review-v2-calls.jsonl",
            "a498"));

    [Fact]
    public Task Live_qwen_semantic_resolution_writer_review_v5_matches_five_frozen_a482_cases_when_enabled()
        => RunLiveCanaryAsync(new CanaryProfile(
            LiveFlagV5,
            OutputVariableV5,
            ResolutionContractV5,
            "semantic_resolution_writer_review_v5_a482_canary_v1",
            "a513-live-semantic-resolution-writer-review-v5-a482",
            "semantic-resolution-writer-review-v5-canary.json",
            "semantic-resolution-writer-review-v5-calls.jsonl",
            "a513"));

    [Fact]
    public void A531_semantic_writer_handoff_fixtures_match_the_three_pre_registered_transactions()
    {
        var cases = BuildA531Cases();

        Assert.Equal(3, cases.Count);
        Assert.Equal(
            new[]
            {
                "pump_torque_direct",
                "survey_drone_focus_support",
                "rail_wear_anchor_context"
            },
            cases.Select(canaryCase => canaryCase.Id));
        Assert.Equal(new[] { "answer", "answer", "context" },
            cases.Select(canaryCase => canaryCase.ExpectedDecision));
        Assert.Equal(new[] { "E1" }, cases[0].ExpectedEvidenceIds);
        Assert.Equal(new[] { "E2", "E3" }, cases[1].ExpectedEvidenceIds);
        Assert.Empty(cases[2].ExpectedEvidenceIds);
        Assert.Equal(new[] { "E1" }, cases[2].ExpectedLeadEvidenceIds);
    }

    [Fact]
    public void A531_recorded_prompt_is_complete_counted_and_sha256_authenticated()
    {
        var prompt = BuildRecordedPrompt(new[]
        {
            SourceBackedAgentMessage.System("alpha"),
            SourceBackedAgentMessage.User("HANDOFF_SEMANTIQUE_DU_JUGE:\nbeta")
        });

        Assert.Equal(
            "system\nalpha\n\nuser\nHANDOFF_SEMANTIQUE_DU_JUGE:\nbeta",
            prompt);
        Assert.Equal(51, prompt.Length);
        Assert.Equal(
            "D8C2AC544F1216C06FDF98F226918D2A1B859EDC9790658AF15796C104B5A3AF",
            ComputeSha256(prompt));
    }

    [Fact]
    public Task Live_qwen_semantic_writer_handoff_a531_matches_three_pre_registered_transactions_when_enabled()
        => RunLiveCanaryAsync(new CanaryProfile(
            LiveFlagA531,
            OutputVariableA531,
            ResolutionContractV5,
            "semantic_writer_handoff_a531_three_transactions_v1",
            "a531-live-semantic-writer-handoff-three-transactions",
            "a531-semantic-writer-handoff-three-transactions.json",
            "a531-semantic-writer-handoff-three-transactions-calls.jsonl",
            "a531",
            CaseSet: "a531",
            ExpectedMinimumLlmCalls: 7,
            ExpectedMaximumLlmCalls: 11,
            RequireSemanticWriterHandoff: true,
            MaximumTotalTokens: 12_000));

    [Fact]
    public void A537_neutral_fixtures_match_the_four_pre_registered_transactions()
    {
        var cases = BuildA537Cases();

        Assert.Equal(4, cases.Count);
        Assert.Equal(
            new[] { "txn_kappa", "txn_lambda", "txn_mu", "txn_nu" },
            cases.Select(canaryCase => canaryCase.Id));
        Assert.Equal(
            new[] { "answer", "research", "context", "research" },
            cases.Select(canaryCase => canaryCase.ExpectedDecision));
        Assert.Equal(new[] { "E2", "E1" }, cases[0].ExpectedEvidenceIds);
        Assert.Equal(new[] { "E2" }, cases[0].ExpectedLeadEvidenceIds);
        Assert.Empty(cases[1].ExpectedEvidenceIds);
        Assert.Empty(cases[2].ExpectedEvidenceIds);
        Assert.Equal(new[] { "E1" }, cases[2].ExpectedLeadEvidenceIds);
        Assert.Empty(cases[3].ExpectedEvidenceIds);
        Assert.Equal(new[] { 2, 2, 1, 2 },
            cases.Select(canaryCase => canaryCase.SourceIdentities?.Count ?? 0));
    }

    [Fact]
    public void A537_source_identities_are_unique_and_oracle_neutral()
    {
        var forbidden = new[]
        {
            "answer", "research", "context", "focus", "support", "winner",
            "requires", "expected"
        };
        var identities = BuildA537Cases()
            .SelectMany(canaryCase => canaryCase.SourceIdentities
                                      ?? Array.Empty<CanarySourceIdentity>())
            .ToArray();

        Assert.Equal(7, identities.Length);
        Assert.Equal(7, identities.Select(identity => identity.DocId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(7, identities.Select(identity => identity.DocPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(7, identities.Select(identity => identity.ChunkId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var identity in identities)
        {
            foreach (var value in new[]
                     {
                         identity.DocId,
                         identity.DocName,
                         identity.DocPath,
                         identity.ChunkId
                     })
            {
                Assert.DoesNotContain(forbidden, token => value.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public Task Live_qwen_resolution_policy_a537_matches_four_neutral_transactions_when_enabled()
        => RunLiveCanaryAsync(new CanaryProfile(
            LiveFlagA537,
            OutputVariableA537,
            ResolutionContractV5,
            "resolution_policy_a537_four_neutral_transactions_v1",
            "a537-live-resolution-policy-neutral",
            "a537-resolution-policy-neutral.json",
            "a537-resolution-policy-neutral-calls.jsonl",
            "a537",
            CaseSet: "a537",
            ExpectedMinimumLlmCalls: 6,
            ExpectedMaximumLlmCalls: 8,
            RequireSemanticWriterHandoff: true,
            MaximumTotalTokens: 10_000));

    [Fact]
    public void A545_v6_fixtures_are_byte_for_byte_equivalent_to_a537_semantic_cases()
    {
        Assert.Equal(
            JsonSerializer.Serialize(BuildA537Cases()),
            JsonSerializer.Serialize(BuildA545Cases()));
    }

    [Fact]
    public Task Live_qwen_semantic_resolution_v6_a545_closes_bug133_when_enabled()
        => RunLiveCanaryAsync(new CanaryProfile(
            LiveFlagA545,
            OutputVariableA545,
            ResolutionContractV6,
            "semantic_resolution_v6_a545_bug133_end_to_end_v1",
            "a545-live-semantic-resolution-v6-end-to-end",
            "a545-semantic-resolution-v6-end-to-end.json",
            "a545-semantic-resolution-v6-end-to-end-calls.jsonl",
            "a545",
            CaseSet: "a545",
            ExpectedMinimumLlmCalls: 6,
            ExpectedMaximumLlmCalls: 8,
            RequireSemanticWriterHandoff: true,
            MaximumTotalTokens: 10_000));

    private async Task RunLiveCanaryAsync(CanaryProfile profile)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(profile.LiveFlag),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine($"Skipped: set {profile.LiveFlag}=1 to run the canary.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The semantic resolution canary requires local LLM URL and model settings.");
        }

        var artifact = FirstNonBlank(
                           Environment.GetEnvironmentVariable(profile.OutputVariable))
                       ?? Path.Combine(
                           FindRepoRoot(),
                           "artifacts",
                           "goal-rag-product-20260827-1041",
                           "phase5",
                           profile.RunDirectoryName,
                           profile.JsonFileName);
        var callsArtifact = Path.Combine(
            Path.GetDirectoryName(artifact)!,
            profile.CallsFileName);
        var cases = profile.CaseSet switch
        {
            "a531" => BuildA531Cases(),
            "a537" => BuildA537Cases(),
            "a545" => BuildA545Cases(),
            _ => BuildA482Cases()
        };
        var results = new List<CanaryResult>();
        var allCalls = new List<RecordedCall>();
        var globalErrors = new List<string>();
        LlamaCppProcessManager? runtimeManager = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            var liveSettings = settings.Clone();
            liveSettings.UseLocalLlm = true;
            liveSettings.LlmMaxOutputTokens = 640;
            if (liveSettings.ManageLocalLlmProcess)
            {
                runtimeManager = new LlamaCppProcessManager();
                runtimeManager.SetIdleStopSuppressionProvider(static () => true);
                var (ok, message) = await runtimeManager.EnsureRunningAsync(
                    liveSettings,
                    cts.Token);
                if (!ok)
                {
                    throw new InvalidOperationException(
                        "Managed LLM runtime did not start: " + message);
                }
            }

            foreach (var canaryCase in cases)
            {
                var inner = new OpenAiLlmClient();
                inner.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
                var llm = new RecordingLlmAdapter(inner, canaryCase.Id);
                var executor = new FrozenEvidenceExecutor(
                    BuildToolResults(canaryCase));
                var runner = new SourceBackedAgentV2Runner(
                    llm,
                    executor,
                    new SourceBackedAgentV2Options(
                        MaximumTurns: canaryCase.ExpectedDecision == "answer" ? 2 : 1,
                        MaximumToolCalls: 1,
                        MaximumObservationItems: 12,
                        MaximumObservationExcerptCharacters: 520,
                        MaximumOutputTokens: 900,
                        MaximumContextTokens: 4096,
                        MaximumSemanticCorrectionTurns: 1,
                        SemanticReviewTemperature: 0,
                        SeparateActionAndWriter: true,
                        RequireEvidenceSelectionBeforeWriter: true,
                        StructuredFlatWriterEnabled: true,
                        SemanticAnswerTransactionEnabled: false,
                        SemanticResolutionWriterReviewEnabled: true));
                var stopwatch = Stopwatch.StartNew();
                SourceBackedPipelineResult? result = null;
                IReadOnlyList<string> errors;
                try
                {
                    result = await runner.RunAsync(
                        BuildIntake(canaryCase, profile.ActionPrefix),
                        cts.Token);
                    errors = Evaluate(
                        canaryCase,
                        result,
                        llm.Calls,
                        executor.CallCount,
                        profile.ResolutionContract,
                        profile.RequireSemanticWriterHandoff);
                }
                catch (Exception exception)
                {
                    errors = new[] { "case_exception:" + exception };
                }
                stopwatch.Stop();
                allCalls.AddRange(llm.Calls);
                globalErrors.AddRange(errors.Select(error =>
                    canaryCase.Id + ": " + error));
                var observed = BuildObserved(
                    result,
                    llm.Calls,
                    executor.CallCount,
                    profile.ResolutionContract);
                results.Add(new CanaryResult(
                    canaryCase.Id,
                    canaryCase.Domain,
                    canaryCase.Question,
                    canaryCase.ExpectedDecision,
                    canaryCase.ExpectedEvidenceIds,
                    canaryCase.ExpectedLeadEvidenceIds
                    ?? canaryCase.ExpectedEvidenceIds,
                    observed,
                    llm.Calls,
                    errors,
                    stopwatch.ElapsedMilliseconds));
                output.WriteLine(
                    $"{canaryCase.Id}: decision={observed.ResolutionDecision}; "
                    + $"published={observed.AnswerPublished}; "
                    + $"verified={observed.SourceVerified}; "
                    + $"ids={string.Join(',', observed.SelectedEvidenceIds)}; "
                    + $"cited={string.Join(',', observed.CitedEvidenceIds)}; "
                    + $"calls={llm.Calls.Count}; writer/reviewer="
                    + $"{observed.WriterCallCount}/{observed.ReviewerCallCount}; "
                    + $"repair={observed.RepairScheduledCount}; errors={errors.Count}");
                if (llm.Calls.Count > 7)
                    break;
            }
        }
        catch (Exception exception)
        {
            globalErrors.Add("transport_or_harness: " + exception);
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                var totalTokens = allCalls.Sum(call =>
                    (call.PromptTokens ?? 0) + (call.CompletionTokens ?? 0));
                var elapsedMilliseconds = results.Sum(result =>
                    result.ElapsedMilliseconds);
                if (allCalls.Count < profile.ExpectedMinimumLlmCalls
                    || allCalls.Count > profile.ExpectedMaximumLlmCalls)
                {
                    globalErrors.Add(
                        $"llm_call_budget={allCalls.Count};expected="
                        + $"{profile.ExpectedMinimumLlmCalls}-"
                        + profile.ExpectedMaximumLlmCalls);
                }
                if (totalTokens > profile.MaximumTotalTokens)
                {
                    globalErrors.Add(
                        $"total_token_budget={totalTokens};maximum="
                        + profile.MaximumTotalTokens);
                }
                if (elapsedMilliseconds > 360_000)
                {
                    globalErrors.Add(
                        $"performance_budget_ms={elapsedMilliseconds};maximum=360000");
                }
                var payload = new
                {
                    SchemaVersion = profile.SchemaVersion,
                    ResolutionContract = profile.ResolutionContract,
                    Model = model,
                    LlmBaseUrl = llmBaseUrl,
                    ExpectedCaseCount = cases.Count,
                    ExecutedCaseCount = results.Count,
                    profile.ExpectedMinimumLlmCalls,
                    profile.ExpectedMaximumLlmCalls,
                    LlmCallCount = allCalls.Count,
                    PassedCaseCount = results.Count(result => result.Errors.Count == 0),
                    ProtocolValidCaseCount = results.Count(result =>
                        result.Observed.ResolutionShapeValid
                        && result.Observed.ResolutionProtocolValid),
                    RawDecisionMatchesOracleCount = results.Count(result =>
                        string.Equals(
                            result.Observed.ResolutionDecision,
                            result.ExpectedDecision,
                            StringComparison.Ordinal)),
                    WriterCallCount = results.Sum(result =>
                        result.Observed.WriterCallCount),
                    ReviewerCallCount = results.Sum(result =>
                        result.Observed.ReviewerCallCount),
                    RepairScheduledCount = results.Sum(result =>
                        result.Observed.RepairScheduledCount),
                    WriterHandoffTraceCount = results.Sum(result =>
                        result.Observed.WriterHandoffTraceCount),
                    WriterPromptHandoffCount = results.Sum(result =>
                        result.Observed.WriterPromptHandoffCount),
                    AnswerPublishedCount = results.Count(result =>
                        result.Observed.AnswerPublished),
                    FalseAnswerPublishedCount = results.Count(result =>
                        result.Observed.AnswerPublished
                        && result.ExpectedDecision != "answer"),
                    PublicationWithoutReviewerAcceptCount = results.Count(result =>
                        result.Observed.AnswerPublished
                        && !string.Equals(
                            result.Observed.FinalReviewerDecision,
                            "accept",
                            StringComparison.OrdinalIgnoreCase)),
                    PromptTokens = allCalls.Sum(call => call.PromptTokens ?? 0),
                    CompletionTokens = allCalls.Sum(call =>
                        call.CompletionTokens ?? 0),
                    TotalTokens = totalTokens,
                    profile.MaximumTotalTokens,
                    LlmElapsedMilliseconds = allCalls.Sum(call =>
                        call.ElapsedMilliseconds),
                    CaseElapsedMilliseconds = elapsedMilliseconds,
                    PerformanceTargetMilliseconds = 180_000,
                    PerformanceMaximumMilliseconds = 360_000,
                    RuntimeManaged = runtimeManager is not null,
                    GlobalErrors = globalErrors,
                    Cases = results
                };
                await File.WriteAllTextAsync(
                    artifact,
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions { WriteIndented = true }),
                    CancellationToken.None);
                await File.WriteAllLinesAsync(
                    callsArtifact,
                    allCalls.Select(call => JsonSerializer.Serialize(call)),
                    CancellationToken.None);
                output.WriteLine("Artifact: " + artifact);
                output.WriteLine("Calls artifact: " + callsArtifact);
            }
            finally
            {
                runtimeManager?.Stop();
            }
        }

        Assert.Equal(cases.Count, results.Count);
        Assert.InRange(
            allCalls.Count,
            profile.ExpectedMinimumLlmCalls,
            profile.ExpectedMaximumLlmCalls);
        Assert.True(
            globalErrors.Count == 0,
            string.Join(Environment.NewLine, globalErrors));
    }

    private static IReadOnlyList<string> Evaluate(
        CanaryCase canaryCase,
        SourceBackedPipelineResult result,
        IReadOnlyList<RecordedCall> calls,
        int toolCallCount,
        string resolutionContract,
        bool requireSemanticWriterHandoff = false)
    {
        var errors = new List<string>();
        var resolutionCall = calls.FirstOrDefault(call =>
            call.Contract == resolutionContract);
        var resolution = ParseResolution(resolutionCall?.RawOutput);
        var writerCalls = calls.Where(call => call.Role == "writer").ToArray();
        var reviewerCalls = calls.Where(call => call.Role == "reviewer").ToArray();
        var finalReviewerDecision = reviewerCalls.LastOrDefault()?.SemanticDecision;
        var nextCapability = ReadTraceField(
            result,
            "source_backed_agent_v2.semantic_resolution.completed",
            "next_capability");
        var repairCount = result.TraceEvents.Count(trace =>
            trace.EventName ==
            "source_backed_agent_v2.semantic_resolution_writer_review.repair_scheduled");
        var blocked = result.TraceEvents.Any(trace =>
            trace.EventName ==
            "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked");
        var skipped = result.TraceEvents.Any(trace =>
            trace.EventName ==
            "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");

        if (string.Equals(
                resolutionContract,
                ResolutionContractV5,
                StringComparison.Ordinal)
            || string.Equals(
                resolutionContract,
                ResolutionContractV6,
                StringComparison.Ordinal))
        {
            if (!resolution.ShapeValid)
            {
                errors.Add(string.Equals(
                        resolutionContract,
                        ResolutionContractV6,
                        StringComparison.Ordinal)
                    ? "resolution_v6_shape_invalid"
                    : "resolution_v5_shape_invalid");
            }
            if (!string.Equals(
                    ReadTraceField(
                        result,
                        "source_backed_agent_v2.semantic_resolution.completed",
                        "protocol_valid"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("resolution_protocol_valid=false");
            }
            var protocolError = ReadTraceField(
                result,
                "source_backed_agent_v2.semantic_resolution.completed",
                "protocol_error");
            if (!string.IsNullOrWhiteSpace(protocolError))
                errors.Add("resolution_protocol_error=" + protocolError);

            var expectedRawEvidenceIds = canaryCase.ExpectedDecision == "context"
                ? canaryCase.ExpectedLeadEvidenceIds ?? Array.Empty<string>()
                : canaryCase.ExpectedEvidenceIds;
            if (!resolution.RawEvidenceIds.OrderBy(id => id).SequenceEqual(
                    expectedRawEvidenceIds.OrderBy(id => id),
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("raw_resolution_evidence_ids="
                           + string.Join(',', resolution.RawEvidenceIds));
            }
            if (string.Equals(
                    resolutionContract,
                    ResolutionContractV6,
                    StringComparison.Ordinal))
            {
                var expectedRawLeadEvidenceIds =
                    canaryCase.ExpectedLeadEvidenceIds
                    ?? canaryCase.ExpectedEvidenceIds;
                if (!resolution.RawLeadEvidenceIds.SequenceEqual(
                        expectedRawLeadEvidenceIds,
                        StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add("raw_resolution_lead_evidence_ids="
                               + string.Join(',', resolution.RawLeadEvidenceIds));
                }
            }
            AssertDerivedResolutionTrace(result, resolution, errors);
        }

        if (toolCallCount != 1)
            errors.Add("tool_call_count=" + toolCallCount);
        if (resolutionCall is null)
            errors.Add("resolution_call_missing");
        if (calls.Count > 7)
            errors.Add("llm_call_count=" + calls.Count);
        if (resolution.Decision != canaryCase.ExpectedDecision)
            errors.Add("resolution_decision=" + resolution.Decision);
        if (resolution.AnswerAdequacy != canaryCase.ExpectedAdequacy)
            errors.Add("answer_adequacy=" + resolution.AnswerAdequacy);
        if (nextCapability != canaryCase.ExpectedNextCapability)
            errors.Add("next_capability=" + nextCapability);
        var expectedSelectedEvidenceIds =
            string.Equals(
                resolutionContract,
                ResolutionContractV6,
                StringComparison.Ordinal)
            && canaryCase.ExpectedDecision == "context"
                ? canaryCase.ExpectedLeadEvidenceIds ?? Array.Empty<string>()
                : canaryCase.ExpectedEvidenceIds;
        if (!resolution.SelectedEvidenceIds.OrderBy(id => id).SequenceEqual(
                expectedSelectedEvidenceIds.OrderBy(id => id),
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("selected_evidence_ids="
                       + string.Join(',', resolution.SelectedEvidenceIds));
        }
        var expectedLead = canaryCase.ExpectedLeadEvidenceIds
                           ?? canaryCase.ExpectedEvidenceIds;
        var observedLead = resolution.LeadEvidenceId == "NONE"
            ? Array.Empty<string>()
            : new[] { resolution.LeadEvidenceId };
        if (!observedLead.OrderBy(id => id).SequenceEqual(
                expectedLead.OrderBy(id => id),
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("lead_evidence_ids=" + string.Join(',', observedLead));
        }
        if (string.Equals(
                resolutionContract,
                ResolutionContractV6,
                StringComparison.Ordinal))
        {
            if (!resolution.SelectedEvidenceIds.SequenceEqual(
                    expectedSelectedEvidenceIds,
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("ordered_selected_evidence_ids="
                           + string.Join(',', resolution.SelectedEvidenceIds));
            }
            if (!resolution.LeadEvidenceIds.SequenceEqual(
                    expectedLead,
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("ordered_lead_evidence_ids="
                           + string.Join(',', resolution.LeadEvidenceIds));
            }
        }
        var expectedPresented = Enumerable.Range(1, canaryCase.Excerpts.Count)
            .Select(index => "E" + index)
            .ToArray();
        var presented = ReadTraceCsv(
            result,
            "source_backed_agent_v2.fast_evidence_review.completed",
            "presented_evidence_ids");
        if (!presented.OrderBy(id => id).SequenceEqual(
                expectedPresented.OrderBy(id => id),
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("presented_evidence_ids=" + string.Join(',', presented));
        }
        if (calls.Any(call => call.PromptTokens is not > 0
                              || call.CompletionTokens is not > 0
                              || call.ElapsedMilliseconds < 0))
        {
            errors.Add("call_metrics_incomplete");
        }

        if (canaryCase.ExpectedDecision == "answer")
        {
            if (!result.IsSourceVerified || string.IsNullOrWhiteSpace(result.Answer))
                errors.Add("answer_not_published_verified");
            if (writerCalls.Length is < 1 or > 2)
                errors.Add("writer_call_count=" + writerCalls.Length);
            if (reviewerCalls.Length != writerCalls.Length)
                errors.Add("reviewer_call_count=" + reviewerCalls.Length);
            if (repairCount > 1)
                errors.Add("repair_count=" + repairCount);
            if (skipped)
                errors.Add("reviewer_skipped");
            if (!string.Equals(finalReviewerDecision, "accept", StringComparison.Ordinal))
                errors.Add("final_reviewer_decision=" + finalReviewerDecision);
            if (blocked)
                errors.Add("publication_blocked");
            var cited = result.CitedEvidence.Select(item => item.EvidenceId).ToArray();
            if (!cited.OrderBy(id => id).SequenceEqual(
                    canaryCase.ExpectedEvidenceIds.OrderBy(id => id),
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("cited_evidence_ids=" + string.Join(',', cited));
            }
            foreach (var pattern in canaryCase.RequiredPatterns)
            {
                if (!Regex.IsMatch(
                        result.Answer ?? string.Empty,
                        pattern,
                        RegexOptions.CultureInvariant))
                {
                    errors.Add("missing_pattern=" + pattern);
                }
            }
        }
        else
        {
            if (result.IsSourceVerified || !string.IsNullOrWhiteSpace(result.Answer))
                errors.Add("unexpected_answer=" + result.Answer);
            if (writerCalls.Length != 0 || reviewerCalls.Length != 0)
                errors.Add($"unexpected_writer_reviewer={writerCalls.Length}/{reviewerCalls.Length}");
            if (calls.Count != 1)
                errors.Add("non_answer_llm_call_count=" + calls.Count);
            if (repairCount != 0)
                errors.Add("unexpected_repair=" + repairCount);
            if (canaryCase.ExpectedDecision == "clarify"
                && string.IsNullOrWhiteSpace(result.Clarification?.Message))
            {
                errors.Add("clarification_missing");
            }
            if (canaryCase.ExpectedDecision == "context")
            {
                if (resolution.VisibleContextEvidenceId != "E1"
                    || resolution.LeadEvidenceId != "E1")
                {
                    errors.Add("context_anchor="
                               + resolution.VisibleContextEvidenceId + "/"
                               + resolution.LeadEvidenceId);
                }
            }
        }
        if (requireSemanticWriterHandoff)
        {
            EvaluateSemanticWriterHandoff(
                canaryCase,
                result,
                calls,
                resolution,
                writerCalls,
                reviewerCalls,
                errors);
        }
        return errors;
    }

    private static void EvaluateSemanticWriterHandoff(
        CanaryCase canaryCase,
        SourceBackedPipelineResult result,
        IReadOnlyList<RecordedCall> calls,
        ResolutionObservation resolution,
        IReadOnlyList<RecordedCall> writerCalls,
        IReadOnlyList<RecordedCall> reviewerCalls,
        ICollection<string> errors)
    {
        const string eventName =
            "source_backed_agent_v2.semantic_resolution.writer_handoff.completed";
        const string marker = "HANDOFF_SEMANTIQUE_DU_JUGE:";
        var handoffTraces = result.TraceEvents
            .Where(trace => trace.EventName == eventName)
            .ToArray();

        if (canaryCase.ExpectedDecision != "answer")
        {
            if (calls.Count != 1)
                errors.Add("non_answer_handoff_llm_call_count=" + calls.Count);
            if (handoffTraces.Length != 0)
                errors.Add("non_answer_handoff_trace_count=" + handoffTraces.Length);
            if (calls.Any(call => call.PromptText.Contains(
                    marker,
                    StringComparison.Ordinal)))
            {
                errors.Add("non_answer_handoff_prompt_present");
            }
            return;
        }

        if (calls.Count is < 3 or > 5)
            errors.Add("a531_answer_llm_call_count=" + calls.Count);
        if (handoffTraces.Length != 1)
        {
            errors.Add("a531_handoff_trace_count=" + handoffTraces.Length);
        }
        else
        {
            var fields = handoffTraces[0].Fields;
            AssertTraceField(fields, "decision_source", "llm_evidence_judge", errors);
            AssertTraceField(fields, "decision", "answer", errors);
            AssertTraceField(
                fields,
                "selected_evidence_ids",
                string.Join(',', canaryCase.ExpectedEvidenceIds),
                errors);
            AssertTraceField(
                fields,
                "lead_evidence_ids",
                string.Join(',', canaryCase.ExpectedLeadEvidenceIds
                                  ?? canaryCase.ExpectedEvidenceIds),
                errors);
            AssertTraceField(fields, "assessment_forwarded", "true", errors);
            AssertTraceField(
                fields,
                "assessment_characters",
                resolution.Reason.Length.ToString(),
                errors);
            AssertTraceField(
                fields,
                "source_window_expansion_policy",
                "unchanged",
                errors);
        }

        if (writerCalls.Count is < 1 or > 2)
            errors.Add("a531_writer_call_count=" + writerCalls.Count);
        if (reviewerCalls.Count != writerCalls.Count)
            errors.Add("a531_reviewer_call_count=" + reviewerCalls.Count);

        var expectedEvidenceLine =
            "EVIDENCEIDS_SELECTIONNES_PAR_LE_JUGE: "
            + string.Join(',', canaryCase.ExpectedEvidenceIds);
        var expectedLeadEvidenceLine =
            "LEADEVIDENCEIDS_PRINCIPAUX_PAR_LE_JUGE: "
            + string.Join(',', canaryCase.ExpectedLeadEvidenceIds
                                ?? canaryCase.ExpectedEvidenceIds);
        var expectedReasonLine =
            "RAISON_DU_JUGE: " + NormalizePromptValue(resolution.Reason, 900);
        foreach (var writerCall in writerCalls)
        {
            if (CountOccurrences(writerCall.PromptText, marker) != 1)
                errors.Add($"a531_writer_{writerCall.CallIndex}_handoff_marker_count="
                           + CountOccurrences(writerCall.PromptText, marker));
            if (!writerCall.PromptText.Contains(
                    "DECISION: answer",
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_decision_missing");
            }
            if (!writerCall.PromptText.Contains(
                    expectedEvidenceLine,
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_evidence_ids_missing");
            }
            if (!writerCall.PromptText.Contains(
                    expectedLeadEvidenceLine,
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_lead_evidence_ids_missing");
            }
            if (!writerCall.PromptText.Contains(
                    expectedReasonLine,
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_reason_missing");
            }
            if (!writerCall.PromptText.Contains(
                    "ROLE_DES_PREUVES:",
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_evidence_role_missing");
            }
            if (!writerCall.PromptText.Contains(
                    "FENETRE_CONTEXTUELLE_CITABLE:",
                    StringComparison.Ordinal))
            {
                errors.Add($"a531_writer_{writerCall.CallIndex}_source_window_missing");
            }
        }

        var citedEvidenceIds = result.CitedEvidence
            .Select(item => item.EvidenceId)
            .ToArray();
        if (!citedEvidenceIds.SequenceEqual(
                canaryCase.ExpectedEvidenceIds,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("a531_ordered_cited_evidence_ids="
                       + string.Join(',', citedEvidenceIds));
        }
        foreach (var forbiddenPattern in canaryCase.ForbiddenPatterns
                                         ?? Array.Empty<string>())
        {
            if (Regex.IsMatch(
                    result.Answer ?? string.Empty,
                    forbiddenPattern,
                    RegexOptions.CultureInvariant))
            {
                errors.Add("forbidden_pattern=" + forbiddenPattern);
            }
        }
    }

    private static void AssertTraceField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string expected,
        ICollection<string> errors)
    {
        var observed = fields.TryGetValue(name, out var value)
            ? value
            : string.Empty;
        if (!string.Equals(observed, expected, StringComparison.Ordinal))
            errors.Add($"a531_handoff_{name}={observed};expected={expected}");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }
        return count;
    }

    private static string NormalizePromptValue(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximum
            ? normalized
            : normalized[..maximum].TrimEnd() + "…";
    }

    private static Observed BuildObserved(
        SourceBackedPipelineResult? result,
        IReadOnlyList<RecordedCall> calls,
        int toolCallCount,
        string resolutionContract)
    {
        var resolution = ParseResolution(calls.FirstOrDefault(call =>
            call.Contract == resolutionContract)?.RawOutput);
        var reviewers = calls.Where(call => call.Role == "reviewer").ToArray();
        var traces = result?.TraceEvents ?? Array.Empty<SourceBackedTraceEvent>();
        var protocolValid = result is not null
                            && string.Equals(
                                ReadTraceField(
                                    result,
                                    "source_backed_agent_v2.semantic_resolution.completed",
                                    "protocol_valid"),
                                "true",
                                StringComparison.OrdinalIgnoreCase);
        return new Observed(
            resolution.Decision,
            resolution.AnswerAdequacy,
            resolution.RequestedDeliverableComplete,
            resolution.MissingUserInputPreventsUniqueResult,
            resolution.VisibleContextEvidenceId,
            resolution.SelectedEvidenceIds,
            resolution.LeadEvidenceId,
            resolution.LeadEvidenceIds,
            resolution.RawEvidenceIds,
            resolution.RawLeadEvidenceIds,
            resolution.Reason,
            resolution.ShapeValid,
            protocolValid,
            result?.IsSourceVerified == true,
            !string.IsNullOrWhiteSpace(result?.Answer),
            result?.Answer,
            result?.CitedEvidence.Select(item => item.EvidenceId).ToArray()
                ?? Array.Empty<string>(),
            result?.Clarification?.Message,
            calls.Count(call => call.Role == "writer"),
            reviewers.Length,
            reviewers.LastOrDefault()?.SemanticDecision,
            traces.Count(trace => trace.EventName.EndsWith(
                ".repair_scheduled",
                StringComparison.Ordinal)),
            traces.Any(trace => trace.EventName.EndsWith(
                ".publication_blocked",
                StringComparison.Ordinal)),
            traces.Count(trace => trace.EventName ==
                "source_backed_agent_v2.semantic_resolution.writer_handoff.completed"),
            calls.Count(call => call.Role == "writer"
                                && call.PromptText.Contains(
                                    "HANDOFF_SEMANTIQUE_DU_JUGE:",
                                    StringComparison.Ordinal)),
            toolCallCount,
            traces);
    }

    private static ResolutionObservation ParseResolution(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return ResolutionObservation.Empty;
        try
        {
            using var document = JsonDocument.Parse(rawOutput);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("leadEvidenceIds", out _))
            {
                return ParseV6Resolution(root);
            }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("evidenceIds", out _))
            {
                return ParseV5Resolution(root);
            }
            var selectedEvidenceIds = ReadStringArray(root, "selectedEvidenceIds");
            var leadEvidenceId = ReadString(root, "leadEvidenceId");
            var leadEvidenceIds = string.IsNullOrWhiteSpace(leadEvidenceId)
                                  || string.Equals(
                                      leadEvidenceId,
                                      "NONE",
                                      StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : new[] { leadEvidenceId };
            return new ResolutionObservation(
                ReadString(root, "decision"),
                ReadString(root, "answerAdequacy"),
                ReadBool(root, "requestedDeliverableComplete"),
                ReadBool(root, "missingUserInputPreventsUniqueResult"),
                ReadString(root, "visibleContextEvidenceId"),
                selectedEvidenceIds,
                leadEvidenceId,
                leadEvidenceIds,
                selectedEvidenceIds,
                leadEvidenceIds,
                ReadString(root, "reason"),
                root.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException)
        {
            return ResolutionObservation.Empty;
        }
    }

    private static ResolutionObservation ParseV5Resolution(JsonElement root)
    {
        var decision = ReadString(root, "decision").Trim().ToLowerInvariant();
        var rawEvidenceIds = ReadStringArray(root, "evidenceIds")
            .Select(static id => id.Trim().ToUpperInvariant())
            .ToArray();
        var reason = ReadString(root, "reason").Trim();
        var propertyNames = root.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var exactProperties = propertyNames.SequenceEqual(
            new[] { "decision", "evidenceIds", "reason" },
            StringComparer.Ordinal);
        var evidenceArrayValid = root.TryGetProperty(
                                     "evidenceIds",
                                     out var evidenceIdsNode)
                                 && evidenceIdsNode.ValueKind == JsonValueKind.Array
                                 && evidenceIdsNode.EnumerateArray().All(static node =>
                                     node.ValueKind == JsonValueKind.String);
        var idsValid = evidenceArrayValid
                       && rawEvidenceIds.Length <= 12
                       && rawEvidenceIds.All(static id => id.Length > 0)
                       && rawEvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase)
                           .Count() == rawEvidenceIds.Length;
        var cardinalityValid = decision switch
        {
            "answer" => rawEvidenceIds.Length > 0,
            "context" => rawEvidenceIds.Length == 1,
            "research" or "clarify" => rawEvidenceIds.Length == 0,
            _ => false
        };
        var shapeValid = exactProperties
                         && idsValid
                         && cardinalityValid
                         && reason.Length is >= 8 and <= 480;
        var adequacy = decision switch
        {
            "answer" => "requested_information_present",
            "research" => "requested_information_missing",
            "clarify" => "user_clarification_required",
            "context" => "visible_context_required",
            _ => string.Empty
        };
        var visibleContext = decision == "context" && rawEvidenceIds.Length == 1
            ? rawEvidenceIds[0]
            : "NONE";
        var selectedEvidenceIds = decision == "answer"
            ? rawEvidenceIds
            : Array.Empty<string>();
        var leadEvidenceId = decision is "answer" or "context"
                             && rawEvidenceIds.Length > 0
            ? rawEvidenceIds[0]
            : "NONE";
        return new ResolutionObservation(
            decision,
            adequacy,
            decision == "answer",
            decision == "clarify",
            visibleContext,
            selectedEvidenceIds,
            leadEvidenceId,
            leadEvidenceId == "NONE"
                ? Array.Empty<string>()
                : new[] { leadEvidenceId },
            rawEvidenceIds,
            leadEvidenceId == "NONE"
                ? Array.Empty<string>()
                : new[] { leadEvidenceId },
            reason,
            shapeValid);
    }

    private static ResolutionObservation ParseV6Resolution(JsonElement root)
    {
        var decision = ReadString(root, "decision").Trim().ToLowerInvariant();
        var rawEvidenceIds = ReadStringArray(root, "evidenceIds")
            .Select(static id => id.Trim().ToUpperInvariant())
            .ToArray();
        var rawLeadEvidenceIds = ReadStringArray(root, "leadEvidenceIds")
            .Select(static id => id.Trim().ToUpperInvariant())
            .ToArray();
        var reason = ReadString(root, "reason").Trim();
        var propertyNames = root.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var exactProperties = propertyNames.SequenceEqual(
            new[] { "decision", "evidenceIds", "leadEvidenceIds", "reason" },
            StringComparer.Ordinal);
        var evidenceArrayValid = IsStringArray(root, "evidenceIds");
        var leadArrayValid = IsStringArray(root, "leadEvidenceIds");
        var evidenceSet = rawEvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idsValid = evidenceArrayValid
                       && leadArrayValid
                       && rawEvidenceIds.Length <= 12
                       && rawLeadEvidenceIds.Length <= 12
                       && rawEvidenceIds.All(static id => id.Length > 0)
                       && rawLeadEvidenceIds.All(static id => id.Length > 0)
                       && rawEvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase)
                           .Count() == rawEvidenceIds.Length
                       && rawLeadEvidenceIds
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Count() == rawLeadEvidenceIds.Length;
        var cardinalityValid = decision switch
        {
            "answer" => rawEvidenceIds.Length > 0
                        && rawLeadEvidenceIds.Length > 0
                        && rawLeadEvidenceIds.All(evidenceSet.Contains),
            "context" => rawEvidenceIds.Length == 1
                         && rawLeadEvidenceIds.Length == 1
                         && string.Equals(
                             rawEvidenceIds[0],
                             rawLeadEvidenceIds[0],
                             StringComparison.OrdinalIgnoreCase),
            "research" or "clarify" => rawEvidenceIds.Length == 0
                                        && rawLeadEvidenceIds.Length == 0,
            _ => false
        };
        var shapeValid = exactProperties
                         && idsValid
                         && cardinalityValid
                         && reason.Length is >= 8 and <= 480;
        var adequacy = decision switch
        {
            "answer" => "requested_information_present",
            "research" => "requested_information_missing",
            "clarify" => "user_clarification_required",
            "context" => "visible_context_required",
            _ => string.Empty
        };
        var visibleContext = decision == "context" && rawEvidenceIds.Length == 1
            ? rawEvidenceIds[0]
            : "NONE";
        var selectedEvidenceIds = decision switch
        {
            "answer" => rawLeadEvidenceIds
                .Concat(rawEvidenceIds.Where(id => !rawLeadEvidenceIds.Contains(
                    id,
                    StringComparer.OrdinalIgnoreCase)))
                .ToArray(),
            "context" => rawEvidenceIds,
            _ => Array.Empty<string>()
        };
        var leadEvidenceIds = decision is "answer" or "context"
            ? rawLeadEvidenceIds
            : Array.Empty<string>();
        return new ResolutionObservation(
            decision,
            adequacy,
            decision == "answer",
            decision == "clarify",
            visibleContext,
            selectedEvidenceIds,
            leadEvidenceIds.FirstOrDefault() ?? "NONE",
            leadEvidenceIds,
            rawEvidenceIds,
            rawLeadEvidenceIds,
            reason,
            shapeValid);
    }

    private static bool IsStringArray(JsonElement root, string property)
        => root.TryGetProperty(property, out var node)
           && node.ValueKind == JsonValueKind.Array
           && node.EnumerateArray().All(static item =>
               item.ValueKind == JsonValueKind.String);

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string property)
        => root.TryGetProperty(property, out var ids)
           && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray()
                .Where(node => node.ValueKind == JsonValueKind.String)
                .Select(node => node.GetString() ?? string.Empty)
                .Where(id => id.Length > 0)
                .ToArray()
            : Array.Empty<string>();

    private static void AssertDerivedResolutionTrace(
        SourceBackedPipelineResult result,
        ResolutionObservation resolution,
        ICollection<string> errors)
    {
        const string eventName =
            "source_backed_agent_v2.semantic_resolution.completed";
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["answer_adequacy"] = resolution.AnswerAdequacy,
            ["requested_deliverable_complete"] =
                resolution.RequestedDeliverableComplete ? "true" : "false",
            ["missing_user_input_prevents_unique_result"] =
                resolution.MissingUserInputPreventsUniqueResult ? "true" : "false",
            ["visible_context_evidence_id"] = resolution.VisibleContextEvidenceId,
            ["derived_alias_source"] = "decision"
        };
        foreach (var entry in expected)
        {
            var observed = ReadTraceField(result, eventName, entry.Key);
            if (!string.Equals(observed, entry.Value, StringComparison.OrdinalIgnoreCase))
                errors.Add($"trace_{entry.Key}={observed}");
        }
        var selected = ReadTraceCsv(result, eventName, "selected_evidence_ids");
        if (!selected.SequenceEqual(
                resolution.SelectedEvidenceIds,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("trace_selected_evidence_ids=" + string.Join(',', selected));
        }
        var lead = ReadTraceCsv(result, eventName, "lead_evidence_ids");
        if (!lead.SequenceEqual(
                resolution.LeadEvidenceIds,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("trace_lead_evidence_ids=" + string.Join(',', lead));
        }
    }

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var node)
           && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement root, string property)
        => root.TryGetProperty(property, out var node)
           && node.ValueKind is JsonValueKind.True or JsonValueKind.False
           && node.GetBoolean();

    private static IReadOnlyList<string> ReadTraceCsv(
        SourceBackedPipelineResult result,
        string eventName,
        string field)
    {
        var trace = result.TraceEvents.LastOrDefault(item =>
            item.EventName == eventName);
        return trace is not null
               && trace.Fields.TryGetValue(field, out var value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                               StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static string ReadTraceField(
        SourceBackedPipelineResult result,
        string eventName,
        string field)
    {
        var trace = result.TraceEvents.LastOrDefault(item =>
            item.EventName == eventName);
        return trace is not null
               && trace.Fields.TryGetValue(field, out var value)
            ? value
            : string.Empty;
    }

    private static SourceBackedIntake BuildIntake(
        CanaryCase canaryCase,
        string actionPrefix = "a498")
    {
        var actionId = actionPrefix + "-" + canaryCase.Id;
        var arguments = JsonSerializer.SerializeToElement(new
        {
            query = canaryCase.Question,
            topK = 5
        });
        var planLines = canaryCase.SemanticPlan.Split('\n');
        var deliverable = planLines[0].Split(':', 2)[1].Trim();
        var atomicEvidence = planLines[1].Split(':', 2)[1].Trim();
        return new SourceBackedIntake(
            canaryCase.Question,
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: canaryCase.Language)
        {
            QuestionFocus = canaryCase.QuestionFocus,
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    actionId,
                    "rag_search",
                    arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    deliverable,
                    structuredLayout = false,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = atomicEvidence,
                    answerUnitType = atomicEvidence,
                    answerUnitMode = "content_claim",
                    initialCapability = "rag_search",
                    rowHeader = "",
                    rowLabels = Array.Empty<string>(),
                    columns = Array.Empty<string>()
                }),
                "llm_router")
        };
    }

    private static IReadOnlyList<CanaryCase> BuildA482Cases()
        => new[]
        {
            new CanaryCase(
                "survey_drone_shortest_recharge",
                "field_survey_equipment",
                "Which survey drone with a payload capacity of at least 4 kg has the shortest documented full recharge time? Give its name and time.",
                "en",
                "content",
                "DELIVERABLE: fastest documented recharge among equipment satisfying the payload threshold, with name and duration\nATOMIC_EVIDENCE: payload capacity and full recharge time for each visible alternative",
                "answer",
                new[] { "E3" },
                new[] { "(?i)cygnus", "(?<!\\d)36(?!\\d)" },
                new[]
                {
                    "Survey drone Arden carries 3 kg and requires 20 minutes for a full recharge.",
                    "Survey drone Brio carries 4.5 kg and requires 58 minutes for a full recharge.",
                    "Survey drone Cygnus carries 5 kg and requires 36 minutes for a full recharge."
                },
                "requested_information_present",
                "write"),
            new CanaryCase(
                "valve_global_exclusivity_beyond_pool",
                "industrial_procurement_scope",
                "Is the Oriole pressure valve the only valve certified anywhere in the world for hydrogen service? Answer yes or no.",
                "en",
                "content",
                "DELIVERABLE: globally exhaustive yes-or-no exclusivity conclusion\nATOMIC_EVIDENCE: certification and explicit catalogue coverage",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "In the North Region procurement catalogue, the Oriole pressure valve is certified for hydrogen service under standard S-9.",
                    "This catalogue covers approved suppliers for the North Region only and does not list products offered in other regions."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "harbor_beacon_delay_missing",
                "harbor_safety_operations",
                "Quel est le délai exact, en secondes, avant que la balise bascule après la détection du brouillard ?",
                "fr",
                "content",
                "LIVRABLE: delai exact de bascule en secondes\nPREUVES_ATOMIQUES: valeur explicite du delai",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "En présence de brouillard, la balise de chenal doit afficher une lumière ambre.",
                    "Le capteur de visibilité de la balise doit être testé chaque mois."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "greenhouse_profile_requires_crop",
                "greenhouse_nutrient_operations",
                "Je dois choisir exactement un profil N-4 pour aujourd'hui, mais la culture n'est pas indiquée. Quelle concentration appliquer ?",
                "fr",
                "content",
                "LIVRABLE: concentration unique du profil adaptee a la culture\nPREUVES_ATOMIQUES: type de culture et concentration documentee",
                "clarify",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Pour les légumes-feuilles, le profil nutritif N-4 utilise une concentration de 45 mg/L.",
                    "Pour les cultures fruitières, le profil nutritif N-4 utilise une concentration de 70 mg/L."
                },
                "user_clarification_required",
                "clarification"),
            new CanaryCase(
                "rail_wear_annex_requires_context",
                "rail_maintenance_navigation",
                "Quelle est la limite d'usure exacte, en millimètres, du composant Q-17 ?",
                "fr",
                "content",
                "LIVRABLE: limite d'usure exacte en millimetres\nPREUVES_ATOMIQUES: valeur du tableau de maintenance documente",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Index du manuel Q-17 : la limite d'usure exacte figure dans l'annexe H, page 92. Cette entrée d'index ne contient pas la valeur."
                },
                "visible_context_required",
                "documents_context",
                new[] { "E1" })
        };

    private static IReadOnlyList<CanaryCase> BuildA531Cases()
        => new[]
        {
            new CanaryCase(
                "pump_torque_direct",
                "industrial_pump_maintenance",
                "Quel couple et quel ordre de serrage sont documentés pour le couvercle de la pompe HX-42 ?",
                "fr",
                "content",
                "LIVRABLE: couple et ordre de serrage documentes pour le couvercle HX-42\nPREUVES_ATOMIQUES: valeur du couple, nombre de boulons et ordre de serrage",
                "answer",
                new[] { "E1" },
                new[] { "(?i)HX-42|pompe", "(?<!\\d)85(?!\\d)", "(?i)croix" },
                new[]
                {
                    "Le couvercle de la pompe HX-42 comporte quatre boulons à serrer en croix au couple de 85 N.m."
                },
                "requested_information_present",
                "write"),
            new CanaryCase(
                "survey_drone_focus_support",
                "field_survey_equipment",
                "Parmi les drones de relevé portant au moins 4 kg, lequel a le temps de recharge complète documenté le plus court ? Donne son nom et sa durée.",
                "fr",
                "content",
                "LIVRABLE: drone eligible rechargeant le plus vite, avec nom et duree\nPREUVES_ATOMIQUES: capacite et recharge de chaque alternative eligible",
                "answer",
                new[] { "E2", "E3" },
                new[]
                {
                    "(?i)cygnus",
                    "(?<!\\d)36(?!\\d)",
                    "(?i)brio",
                    "(?<!\\d)58(?!\\d)"
                },
                new[]
                {
                    "Le drone de relevé Arden porte 3 kg et demande 20 minutes pour une recharge complète.",
                    "Le drone de relevé Brio porte 4,5 kg et demande 58 minutes pour une recharge complète.",
                    "Le drone de relevé Cygnus porte 5 kg et demande 36 minutes pour une recharge complète."
                },
                "requested_information_present",
                "write",
                ForbiddenPatterns: new[]
                {
                    "(?i)arden",
                    "(?<!\\d)20(?!\\d)"
                }),
            new CanaryCase(
                "rail_wear_anchor_context",
                "rail_maintenance_navigation",
                "Quelle est la limite d'usure exacte, en millimètres, du composant Q-17 ?",
                "fr",
                "content",
                "LIVRABLE: limite d'usure exacte en millimetres\nPREUVES_ATOMIQUES: valeur du tableau de maintenance documente",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Index du manuel Q-17 : la limite d'usure exacte figure dans l'annexe H, page 92. Cette entrée d'index ne contient pas la valeur."
                },
                "visible_context_required",
                "documents_context",
                new[] { "E1" })
        };

    private static IReadOnlyList<CanaryCase> BuildA537Cases()
        => new[]
        {
            new CanaryCase(
                "txn_kappa",
                "thermal_storage_modules",
                "Parmi les deux modules documentés Kappa et Lambda, lequel offre la plus longue autonomie à une charge de 8 kg ? Donne le nom, l'autonomie et les valeurs des deux modules.",
                "fr",
                "content",
                "LIVRABLE: meilleur module dans le pool explicitement borne, avec son autonomie et les deux valeurs comparees\nPREUVES_ATOMIQUES: autonomie a 8 kg de chaque module visible",
                "answer",
                new[] { "E2", "E1" },
                new[]
                {
                    "(?i)lambda",
                    "(?<!\\d)17(?!\\d)",
                    "(?i)kappa",
                    "(?<!\\d)12(?!\\d)"
                },
                new[]
                {
                    "À une charge de 8 kg, le module Kappa fournit une autonomie mesurée de 12 heures.",
                    "À une charge de 8 kg, le module Lambda fournit une autonomie mesurée de 17 heures."
                },
                "requested_information_present",
                "write",
                ExpectedLeadEvidenceIds: new[] { "E2" },
                SourceIdentities: new[]
                {
                    new CanarySourceIdentity(
                        "doc-041",
                        "Dossier-041.pdf",
                        "Synthetic/Set-A/Dossier-041.pdf",
                        "seg-041-a"),
                    new CanarySourceIdentity(
                        "doc-117",
                        "Dossier-117.pdf",
                        "Synthetic/Set-A/Dossier-117.pdf",
                        "seg-117-b")
                }),
            new CanaryCase(
                "txn_lambda",
                "cryogenic_insulation_materials",
                "Le matériau Zeta est-il le seul matériau au monde certifié pour une conduite à -150 °C ? Réponds oui ou non.",
                "fr",
                "content",
                "LIVRABLE: conclusion mondiale exhaustive oui ou non\nPREUVES_ATOMIQUES: certification et couverture explicite du catalogue",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Dans le catalogue régional 2026, le matériau Zeta est certifié pour les conduites fonctionnant à -150 °C.",
                    "Ce catalogue couvre uniquement les fournisseurs agréés de la région Est et ne recense pas les produits des autres régions."
                },
                "requested_information_missing",
                "research",
                SourceIdentities: new[]
                {
                    new CanarySourceIdentity(
                        "doc-208",
                        "Notice-208.pdf",
                        "Synthetic/Set-B/Notice-208.pdf",
                        "seg-208-a"),
                    new CanarySourceIdentity(
                        "doc-233",
                        "Notice-233.pdf",
                        "Synthetic/Set-B/Notice-233.pdf",
                        "seg-233-b")
                }),
            new CanaryCase(
                "txn_mu",
                "hydraulic_network_maintenance",
                "Quelles sont les trois exceptions exactes à la procédure M-7 ?",
                "fr",
                "content",
                "LIVRABLE: trois exceptions exactes de la procedure M-7\nPREUVES_ATOMIQUES: contenu de la liste documentee",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "L'index du manuel M-7 indique que la liste complète des trois exceptions figure dans la section 6, page 44. Cette entrée d'index ne contient pas les exceptions."
                },
                "visible_context_required",
                "documents_context",
                ExpectedLeadEvidenceIds: new[] { "E1" },
                SourceIdentities: new[]
                {
                    new CanarySourceIdentity(
                        "doc-304",
                        "Manuel-304.pdf",
                        "Synthetic/Set-C/Manuel-304.pdf",
                        "seg-304-a")
                }),
            new CanaryCase(
                "txn_nu",
                "cold_room_signalling",
                "Quel délai exact, en secondes, sépare l'alarme initiale de l'arrêt automatique ?",
                "fr",
                "content",
                "LIVRABLE: delai exact en secondes entre alarme et arret automatique\nPREUVES_ATOMIQUES: valeur explicite du delai",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "L'alarme visuelle s'active lorsque la température dépasse le seuil configuré.",
                    "Le bouton d'arrêt d'urgence doit être inspecté chaque trimestre."
                },
                "requested_information_missing",
                "research",
                SourceIdentities: new[]
                {
                    new CanarySourceIdentity(
                        "doc-411",
                        "Fiche-411.pdf",
                        "Synthetic/Set-D/Fiche-411.pdf",
                        "seg-411-a"),
                    new CanarySourceIdentity(
                        "doc-428",
                        "Fiche-428.pdf",
                        "Synthetic/Set-D/Fiche-428.pdf",
                        "seg-428-b")
                })
        };

    private static IReadOnlyList<CanaryCase> BuildA545Cases()
        => BuildA537Cases();

    private static FixtureSnapshot ToFixtureSnapshot(object instance)
        => new(
            ReadProperty<string>(instance, "Id"),
            ReadProperty<string>(instance, "Domain"),
            ReadProperty<string>(instance, "Question"),
            ReadProperty<string>(instance, "Language"),
            ReadProperty<string>(instance, "QuestionFocus"),
            ReadProperty<string>(instance, "SemanticPlan"),
            ReadProperty<string>(instance, "ExpectedDecision"),
            ReadProperty<IReadOnlyList<string>>(instance, "ExpectedEvidenceIds"),
            ReadProperty<IReadOnlyList<string>>(instance, "RequiredPatterns"),
            ReadProperty<IReadOnlyList<string>>(instance, "Excerpts"),
            ReadProperty<string>(instance, "ExpectedAdequacy"),
            ReadProperty<string>(instance, "ExpectedNextCapability"),
            ReadNullableStringList(instance, "ExpectedLeadEvidenceIds"));

    private static T ReadProperty<T>(object instance, string propertyName)
        => (T)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static IReadOnlyList<string>? ReadNullableStringList(
        object instance,
        string propertyName)
        => (IReadOnlyList<string>?)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance);

    private static ToolResults BuildToolResults(CanaryCase canaryCase)
    {
        if (canaryCase.SourceIdentities is not null
            && canaryCase.SourceIdentities.Count != canaryCase.Excerpts.Count)
        {
            throw new InvalidOperationException(
                "Each excerpt must have exactly one frozen source identity.");
        }
        var hits = canaryCase.Excerpts.Select((excerpt, index) =>
        {
            var identity = canaryCase.SourceIdentities?[index]
                           ?? new CanarySourceIdentity(
                               "canary-" + canaryCase.Id,
                               canaryCase.Id + ".pdf",
                               "Synthetic/" + canaryCase.Domain + "/"
                               + canaryCase.Id + ".pdf",
                               canaryCase.Id + ":" + (index + 1));
            return new
            {
                docId = identity.DocId,
                docName = identity.DocName,
                docPath = identity.DocPath,
                revisionId = "canary-v1",
                sourceHash = new string((char)('a' + index), 64),
                pageStart = 1,
                pageEnd = 1,
                chunkId = identity.ChunkId,
                excerpt,
                score = 1d - index / 100d
            };
        }).ToArray();
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new { hits }),
            DurationMs = 0
        });
        return results;
    }

    private sealed class FrozenEvidenceExecutor(ToolResults evidence)
        : ISourceBackedAgentToolExecutor
    {
        public int CallCount { get; private set; }

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            CallCount++;
            if (CallCount != 1 || toolName != "rag.search")
            {
                throw new InvalidOperationException(
                    $"Frozen canary expected one internal rag.search, observed {CallCount}:{toolName}.");
            }
            return Task.FromResult(evidence);
        }
    }

    private sealed class RecordingLlmAdapter(
        OpenAiLlmClient inner,
        string caseId)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient,
          ISourceBackedAgentInputTokenCounter,
          ISourceBackedAgentRuntimeContextProvider
    {
        public List<RecordedCall> Calls { get; } = new();

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var role = tools.Any(tool => tool.Name == ReviewerTool)
                ? "reviewer"
                : "native_other";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var completion = await inner.ChatOnceNativeAsync(
                    messages,
                    tools,
                    temperatureOverride ?? 0,
                    Math.Clamp(maxTokens, 64, 4096),
                    ct,
                    requireToolCall);
                stopwatch.Stop();
                Calls.Add(ToRecordedCall(
                    caseId,
                    Calls.Count + 1,
                    role,
                    null,
                    tools.Select(tool => tool.Name).ToArray(),
                    messages,
                    temperatureOverride ?? 0,
                    maxTokens,
                    requireToolCall,
                    completion,
                    stopwatch.ElapsedMilliseconds,
                    null));
                return completion;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Calls.Add(FailedCall(
                    caseId, Calls.Count + 1, role, null,
                    tools.Select(tool => tool.Name).ToArray(), messages,
                    temperatureOverride ?? 0, maxTokens, requireToolCall,
                    stopwatch.ElapsedMilliseconds, exception));
                throw;
            }
        }

        public async Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            var role = contract.Name is ResolutionContract
                or ResolutionContractV5
                or ResolutionContractV6
                ? "resolution"
                : contract.Name == WriterContract
                    ? "writer"
                    : "structured_other";
            var modelVisibleMessages = SourceBackedLlmPromptSanitizer
                .RemoveControlMetadata(messages.Select(message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray());
            var recordedMessages = modelVisibleMessages
                .Select(message => new SourceBackedAgentMessage(
                    message.role,
                    message.content))
                .ToArray();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var completion = await inner.ChatOnceStructuredCompletionAsync(
                    modelVisibleMessages,
                    temperatureOverride ?? 0,
                    Math.Clamp(maxTokens, 64, 4096),
                    contract,
                    ct);
                stopwatch.Stop();
                Calls.Add(ToRecordedCall(
                    caseId,
                    Calls.Count + 1,
                    role,
                    contract.Name,
                    Array.Empty<string>(),
                    recordedMessages,
                    temperatureOverride ?? 0,
                    maxTokens,
                    false,
                    completion,
                    stopwatch.ElapsedMilliseconds,
                    null));
                return completion;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Calls.Add(FailedCall(
                    caseId, Calls.Count + 1, role, contract.Name,
                    Array.Empty<string>(), recordedMessages,
                    temperatureOverride ?? 0,
                    maxTokens, false, stopwatch.ElapsedMilliseconds, exception));
                throw;
            }
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => inner.CountNativeInputTokensAsync(messages, tools, ct, requireToolCall);

        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct)
            => inner.GetNativeRuntimeContextTokensAsync(ct);
    }

    private static RecordedCall ToRecordedCall(
        string caseId,
        int index,
        string role,
        string? contract,
        IReadOnlyList<string> tools,
        IReadOnlyList<SourceBackedAgentMessage> messages,
        double temperature,
        int maximumTokens,
        bool requireToolCall,
        SourceBackedAgentCompletion completion,
        long elapsedMilliseconds,
        string? error)
    {
        var promptText = BuildRecordedPrompt(messages);
        var recordedTools = completion.ToolCalls.Select(call =>
            new RecordedToolCall(call.Name, call.Arguments.GetRawText())).ToArray();
        var semanticDecision = recordedTools
            .Where(call => call.Name == ReviewerTool)
            .Select(call => ReadDecision(call.Arguments))
            .FirstOrDefault(decision => decision.Length > 0);
        return new RecordedCall(
            caseId,
            index,
            role,
            contract,
            tools,
            requireToolCall,
            temperature,
            maximumTokens,
            promptText.Length,
            promptText,
            ComputeSha256(promptText),
            completion.FinishReason,
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.ServerCacheTokens,
            completion.ServerPromptTokensEvaluated,
            completion.ServerPromptMilliseconds,
            completion.ServerPredictedTokens,
            completion.ServerPredictedMilliseconds,
            elapsedMilliseconds,
            completion.Content,
            recordedTools,
            semanticDecision,
            error,
            completion.ProtocolError);
    }

    private static RecordedCall FailedCall(
        string caseId,
        int index,
        string role,
        string? contract,
        IReadOnlyList<string> tools,
        IReadOnlyList<SourceBackedAgentMessage> messages,
        double temperature,
        int maximumTokens,
        bool requireToolCall,
        long elapsedMilliseconds,
        Exception exception)
    {
        var promptText = BuildRecordedPrompt(messages);
        return new RecordedCall(
            caseId, index, role, contract, tools, requireToolCall, temperature,
            maximumTokens, promptText.Length,
            promptText, ComputeSha256(promptText), "exception", null, null, null,
            null, null, null, null, elapsedMilliseconds, string.Empty,
            Array.Empty<RecordedToolCall>(), null, exception.ToString(), null);
    }

    private static string BuildRecordedPrompt(
        IReadOnlyList<SourceBackedAgentMessage> messages)
        => string.Join(
            "\n\n",
            messages.Select(message =>
                (message.Role ?? string.Empty) + "\n" + (message.Content ?? string.Empty)));

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ReadDecision(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return ReadString(document.RootElement, "decision");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeLlmBaseUrl(string value)
        => value.Trim().TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value.Trim().TrimEnd('/')[..^3]
            : value.Trim().TrimEnd('/');

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record CanaryCase(
        string Id,
        string Domain,
        string Question,
        string Language,
        string QuestionFocus,
        string SemanticPlan,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> RequiredPatterns,
        IReadOnlyList<string> Excerpts,
        string ExpectedAdequacy,
        string ExpectedNextCapability,
        IReadOnlyList<string>? ExpectedLeadEvidenceIds = null,
        IReadOnlyList<string>? ForbiddenPatterns = null,
        IReadOnlyList<CanarySourceIdentity>? SourceIdentities = null);

    private sealed record CanarySourceIdentity(
        string DocId,
        string DocName,
        string DocPath,
        string ChunkId);

    private sealed record FixtureSnapshot(
        string Id,
        string Domain,
        string Question,
        string Language,
        string QuestionFocus,
        string SemanticPlan,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> RequiredPatterns,
        IReadOnlyList<string> Excerpts,
        string ExpectedAdequacy,
        string ExpectedNextCapability,
        IReadOnlyList<string>? ExpectedLeadEvidenceIds);

    private sealed record ResolutionObservation(
        string Decision,
        string AnswerAdequacy,
        bool RequestedDeliverableComplete,
        bool MissingUserInputPreventsUniqueResult,
        string VisibleContextEvidenceId,
        IReadOnlyList<string> SelectedEvidenceIds,
        string LeadEvidenceId,
        IReadOnlyList<string> LeadEvidenceIds,
        IReadOnlyList<string> RawEvidenceIds,
        IReadOnlyList<string> RawLeadEvidenceIds,
        string Reason,
        bool ShapeValid)
    {
        public static ResolutionObservation Empty { get; } = new(
            string.Empty, string.Empty, false, false, string.Empty,
            Array.Empty<string>(), string.Empty, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), string.Empty, false);
    }

    private sealed record Observed(
        string ResolutionDecision,
        string AnswerAdequacy,
        bool RequestedDeliverableComplete,
        bool MissingUserInputPreventsUniqueResult,
        string VisibleContextEvidenceId,
        IReadOnlyList<string> SelectedEvidenceIds,
        string LeadEvidenceId,
        IReadOnlyList<string> LeadEvidenceIds,
        IReadOnlyList<string> RawResolutionEvidenceIds,
        IReadOnlyList<string> RawResolutionLeadEvidenceIds,
        string ResolutionReason,
        bool ResolutionShapeValid,
        bool ResolutionProtocolValid,
        bool SourceVerified,
        bool AnswerPublished,
        string? Answer,
        IReadOnlyList<string> CitedEvidenceIds,
        string? Clarification,
        int WriterCallCount,
        int ReviewerCallCount,
        string? FinalReviewerDecision,
        int RepairScheduledCount,
        bool PublicationBlocked,
        int WriterHandoffTraceCount,
        int WriterPromptHandoffCount,
        int ToolCallCount,
        IReadOnlyList<SourceBackedTraceEvent> Traces);

    private sealed record CanaryProfile(
        string LiveFlag,
        string OutputVariable,
        string ResolutionContract,
        string SchemaVersion,
        string RunDirectoryName,
        string JsonFileName,
        string CallsFileName,
        string ActionPrefix,
        string CaseSet = "a482",
        int ExpectedMinimumLlmCalls = 5,
        int ExpectedMaximumLlmCalls = 35,
        bool RequireSemanticWriterHandoff = false,
        int MaximumTotalTokens = int.MaxValue);

    private sealed record RecordedToolCall(string Name, string Arguments);

    private sealed record RecordedCall(
        string CaseId,
        int CallIndex,
        string Role,
        string? Contract,
        IReadOnlyList<string> Tools,
        bool RequireToolCall,
        double Temperature,
        int MaximumTokens,
        int PromptCharacters,
        string PromptText,
        string PromptSha256,
        string FinishReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? ServerCacheTokens,
        int? ServerPromptTokensEvaluated,
        double? ServerPromptMilliseconds,
        int? ServerPredictedTokens,
        double? ServerPredictedMilliseconds,
        long ElapsedMilliseconds,
        string RawOutput,
        IReadOnlyList<RecordedToolCall> ToolCalls,
        string? SemanticDecision,
        string? Error,
        string? ProtocolError);

    private sealed record CanaryResult(
        string Id,
        string Domain,
        string Question,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> ExpectedLeadEvidenceIds,
        Observed Observed,
        IReadOnlyList<RecordedCall> Calls,
        IReadOnlyList<string> Errors,
        long ElapsedMilliseconds);
}
