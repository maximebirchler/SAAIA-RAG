using System.Collections;
using System.Diagnostics;
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

public sealed class LiveSemanticResolutionV5OrderPairedCanaryTests(
    ITestOutputHelper output)
{
    private const string LiveFlag =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_V5_ORDER_PAIRED_A524";
    private const string OutputVariable =
        "SAAIA_LIVE_SEMANTIC_RESOLUTION_V5_ORDER_PAIRED_A524_OUTPUT";
    private const string ResolutionContract =
        SemanticResolutionV5OrderExperiment.ContractName;
    private const string WriterContract = "source_backed_flat_writer_v1";
    private const string ReviewerTool = "submit_semantic_review";
    private const string SchemaVersion =
        "semantic_resolution_v5_order_paired_a524_v1";

    [Fact]
    public void Paired_cases_match_the_frozen_a482_multidomain_cases()
    {
        var sourceMethod = typeof(LiveSemanticResolutionWriterReviewV2CanaryTests)
            .GetMethod("BuildA482Cases", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sourceMethod);
        var sourceCases = Assert.IsAssignableFrom<IEnumerable>(
                sourceMethod!.Invoke(null, null))
            .Cast<object>()
            .Select(ToFixtureSnapshot)
            .ToArray();
        var pairedCases = BuildCases()
            .Select(static canaryCase => canaryCase.ToSnapshot())
            .ToArray();

        Assert.Equal(
            JsonSerializer.Serialize(sourceCases),
            JsonSerializer.Serialize(pairedCases));
    }

    [Fact]
    public void Paired_schedule_alternates_the_first_arm_for_each_case()
    {
        var schedule = BuildSchedule(BuildCases());

        Assert.Equal(10, schedule.Count);
        Assert.Equal(
            new[]
            {
                "survey_drone_shortest_recharge:DecisionFirst:1",
                "survey_drone_shortest_recharge:ReasonFirst:2",
                "valve_global_exclusivity_beyond_pool:ReasonFirst:3",
                "valve_global_exclusivity_beyond_pool:DecisionFirst:4",
                "harbor_beacon_delay_missing:DecisionFirst:5",
                "harbor_beacon_delay_missing:ReasonFirst:6",
                "greenhouse_profile_requires_crop:ReasonFirst:7",
                "greenhouse_profile_requires_crop:DecisionFirst:8",
                "rail_wear_annex_requires_context:DecisionFirst:9",
                "rail_wear_annex_requires_context:ReasonFirst:10"
            },
            schedule.Select(item =>
                $"{item.Case.Id}:{item.Arm}:{item.Position}").ToArray());
    }

    [Fact]
    public void Paired_contract_invariant_hash_is_identical_across_arms()
    {
        var source = BuildResolutionContract("E1", "E2", "E3");
        var current = SemanticResolutionV5OrderExperiment.Apply(
            source,
            SemanticResolutionV5OrderArm.DecisionFirst);
        var reasonFirst = SemanticResolutionV5OrderExperiment.Apply(
            source,
            SemanticResolutionV5OrderArm.ReasonFirst);

        Assert.NotEqual(HashText(current.Schema.GetRawText()),
            HashText(reasonFirst.Schema.GetRawText()));
        Assert.Equal(ContractInvariantHash(current),
            ContractInvariantHash(reasonFirst));
    }

    [Theory]
    [InlineData(4, 5, 0, "reason_first_initial_support")]
    [InlineData(5, 5, 0, "paired_equal_inconclusive")]
    [InlineData(4, 4, 0, "reason_first_rejected_accuracy")]
    [InlineData(5, 5, 1, "reason_first_rejected_safety")]
    public void Paired_verdict_follows_the_preregistered_interpretation(
        int currentExact,
        int reasonFirstExact,
        int reasonFirstFalseAnswers,
        string expected)
        => Assert.Equal(
            expected,
            ClassifyPairedVerdict(
                currentExact,
                reasonFirstExact,
                reasonFirstFalseAnswers));

    [Theory]
    [InlineData("DecisionFirst", 0, 0, 1, 0)]
    [InlineData("ReasonFirst", 0, 0, 1, 1)]
    [InlineData("DecisionFirst", 1, 1, 0, 2)]
    public void Paired_run_error_policy_observes_control_mismatches_but_blocks_integrity_safety_and_reason_first(
        string arm,
        int integrityCount,
        int safetyCount,
        int functionalCount,
        int expectedBlockingCount)
    {
        var blocking = SelectBlockingRunErrors(
            "case",
            arm,
            Enumerable.Repeat("integrity", integrityCount).ToArray(),
            Enumerable.Repeat("safety", safetyCount).ToArray(),
            Enumerable.Repeat("functional", functionalCount).ToArray());

        Assert.Equal(expectedBlockingCount, blocking.Count);
    }

    [Fact]
    public void V5_parser_preserves_reason_first_raw_property_order()
    {
        var raw = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["reason"] = "Les preuves visibles suffisent pour répondre.",
            ["evidenceIds"] = new[] { "E3" },
            ["decision"] = "answer"
        });

        var observed = ParseResolution(raw);

        Assert.True(observed.ShapeValid);
        Assert.Equal(
            new[] { "reason", "evidenceIds", "decision" },
            observed.PropertyOrder);
        Assert.Equal("answer", observed.Decision);
        Assert.Equal(new[] { "E3" }, observed.RawEvidenceIds);
    }

    [Fact]
    public async Task Frozen_executor_accepts_exactly_one_internal_search()
    {
        var canaryCase = BuildCases()[0];
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteToolCallAsync(
                BuildIntake(canaryCase),
                "rag.search",
                arguments,
                CancellationToken.None));

        Assert.Contains("observed 2:rag.search", error.Message);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public Task Live_qwen_semantic_resolution_v5_paired_order_matches_preregistered_gates_when_enabled()
        => RunLivePairedCanaryAsync();

    private async Task RunLivePairedCanaryAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(LiveFlag),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine($"Skipped: set {LiveFlag}=1 to run the paired canary.");
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
                "The paired semantic resolution canary requires local LLM settings.");
        }

        var artifact = FirstNonBlank(
                           Environment.GetEnvironmentVariable(OutputVariable))
                       ?? Path.Combine(
                           FindRepoRoot(),
                           "artifacts",
                           "goal-rag-product-20260827-1041",
                           "phase5",
                           "a524-live-paired-semantic-resolution-v5-order",
                           "paired-semantic-resolution-v5-order.json");
        var callsArtifact = Path.Combine(
            Path.GetDirectoryName(artifact)!,
            "paired-semantic-resolution-v5-order-calls.jsonl");
        var cases = BuildCases();
        var schedule = BuildSchedule(cases);
        var results = new List<ArmRunResult>();
        var allCalls = new List<RecordedCall>();
        var globalErrors = new List<string>();
        LlamaCppProcessManager? runtimeManager = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var campaignStopwatch = Stopwatch.StartNew();

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

            foreach (var scheduled in schedule)
            {
                var inner = new OpenAiLlmClient();
                inner.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
                var llm = new PairedRecordingLlmAdapter(
                    inner,
                    scheduled.Case.Id,
                    scheduled.Arm,
                    scheduled.Position);
                var executor = new FrozenEvidenceExecutor(
                    BuildToolResults(scheduled.Case));
                var runner = new SourceBackedAgentV2Runner(
                    llm,
                    executor,
                    new SourceBackedAgentV2Options(
                        MaximumTurns: scheduled.Case.ExpectedDecision == "answer" ? 2 : 1,
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
                IReadOnlyList<string> integrityErrors;
                IReadOnlyList<string> safetyErrors;
                IReadOnlyList<string> functionalErrors;
                try
                {
                    result = await runner.RunAsync(
                        BuildIntake(scheduled.Case),
                        cts.Token);
                    var evaluation = Evaluate(
                        scheduled.Case,
                        scheduled.Arm,
                        result,
                        llm.Calls,
                        executor.CallCount);
                    integrityErrors = evaluation.IntegrityErrors;
                    safetyErrors = evaluation.SafetyErrors;
                    functionalErrors = evaluation.FunctionalErrors;
                }
                catch (Exception exception)
                {
                    integrityErrors = new[] { "run_exception:" + exception };
                    safetyErrors = Array.Empty<string>();
                    functionalErrors = new[] { "run_did_not_complete" };
                }
                stopwatch.Stop();
                allCalls.AddRange(llm.Calls);
                var observed = BuildObserved(
                    result,
                    llm.Calls,
                    executor.CallCount);
                results.Add(new ArmRunResult(
                    scheduled.Case.Id,
                    scheduled.Case.Domain,
                    scheduled.Arm.ToString(),
                    scheduled.Position,
                    scheduled.Case.Question,
                    scheduled.Case.ExpectedDecision,
                    scheduled.Case.ExpectedRawEvidenceIds,
                    HashText(string.Join("\n", scheduled.Case.Excerpts)),
                    observed,
                    llm.Calls,
                    integrityErrors,
                    safetyErrors,
                    functionalErrors,
                    stopwatch.ElapsedMilliseconds));
                output.WriteLine(
                    $"{scheduled.Position}:{scheduled.Case.Id}:{scheduled.Arm} "
                    + $"decision={observed.ResolutionDecision}; "
                    + $"ids={string.Join(',', observed.RawResolutionEvidenceIds)}; "
                    + $"calls={llm.Calls.Count}; "
                    + $"writer/reviewer={observed.WriterCallCount}/{observed.ReviewerCallCount}; "
                    + $"integrity/safety/functional={integrityErrors.Count}/"
                    + $"{safetyErrors.Count}/{functionalErrors.Count}");
            }
        }
        catch (Exception exception)
        {
            globalErrors.Add("transport_or_harness:" + exception);
        }
        finally
        {
            campaignStopwatch.Stop();
            try
            {
                globalErrors.AddRange(EvaluateCampaign(cases, results));
                if (campaignStopwatch.ElapsedMilliseconds > 360_000)
                {
                    globalErrors.Add("campaign_elapsed_ms="
                                     + campaignStopwatch.ElapsedMilliseconds);
                }
                var currentExact = CountExact(results, "DecisionFirst");
                var reasonFirstExact = CountExact(results, "ReasonFirst");
                var reasonFirstFalseAnswers = results.Count(run =>
                    run.Arm == "ReasonFirst"
                    && run.Observed.AnswerPublished
                    && run.ExpectedDecision != "answer");
                var payload = new
                {
                    SchemaVersion,
                    ResolutionContract,
                    Model = model,
                    LlmBaseUrl = llmBaseUrl,
                    Branch = "SAAIA_V3.1",
                    Head = "5f35881cdc67d12a076fcd2a7a1004656ac9a37a",
                    GoalSha256 =
                        "6BAE663308460C8F68B0CCEEF3420E8BD30DEDB19B987F9207130DCEF35E63CD",
                    ProtocolSha256 =
                        "6CD277ED890DFCE5D104E2939F45611602CDBDD9E918DCC0E36411B3260DD823",
                    ExpectedCaseCount = 5,
                    ExpectedRunCount = 10,
                    ExecutedRunCount = results.Count,
                    CurrentExactDecisionAndIds = currentExact,
                    ReasonFirstExactDecisionAndIds = reasonFirstExact,
                    ReasonFirstFalseAnswerPublishedCount = reasonFirstFalseAnswers,
                    PairedVerdict = ClassifyPairedVerdict(
                        currentExact,
                        reasonFirstExact,
                        reasonFirstFalseAnswers),
                    ProtocolValidRunCount = results.Count(run =>
                        run.Observed.ResolutionShapeValid
                        && run.Observed.ResolutionProtocolValid),
                    WriterCallCount = results.Sum(run => run.Observed.WriterCallCount),
                    ReviewerCallCount = results.Sum(run => run.Observed.ReviewerCallCount),
                    AnswerPublishedCount = results.Count(run => run.Observed.AnswerPublished),
                    FalseAnswerPublishedCount = results.Count(run =>
                        run.Observed.AnswerPublished
                        && run.ExpectedDecision != "answer"),
                    NativeOtherCallCount = allCalls.Count(call => call.Role == "native_other"),
                    PromptTokens = allCalls.Sum(call => call.PromptTokens ?? 0),
                    CompletionTokens = allCalls.Sum(call => call.CompletionTokens ?? 0),
                    TotalTokens = allCalls.Sum(call =>
                        (call.PromptTokens ?? 0) + (call.CompletionTokens ?? 0)),
                    LlmElapsedMilliseconds = allCalls.Sum(call => call.ElapsedMilliseconds),
                    CampaignElapsedMilliseconds = campaignStopwatch.ElapsedMilliseconds,
                    PerformanceMaximumMilliseconds = 360_000,
                    RuntimeManaged = runtimeManager is not null,
                    GlobalErrors = globalErrors,
                    Runs = results
                };
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
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

        Assert.Equal(10, results.Count);
        Assert.True(
            globalErrors.Count == 0,
            string.Join(Environment.NewLine, globalErrors));
    }

    private static Evaluation Evaluate(
        PairedCase canaryCase,
        SemanticResolutionV5OrderArm arm,
        SourceBackedPipelineResult result,
        IReadOnlyList<RecordedCall> calls,
        int toolCallCount)
    {
        var integrity = new List<string>();
        var safety = new List<string>();
        var functional = new List<string>();
        var observed = BuildObserved(result, calls, toolCallCount);
        var resolutionCall = calls.FirstOrDefault(call => call.Role == "resolution");
        var expectedOrder = arm == SemanticResolutionV5OrderArm.DecisionFirst
            ? new[] { "decision", "evidenceIds", "reason" }
            : new[] { "reason", "evidenceIds", "decision" };

        if (resolutionCall is null)
        {
            integrity.Add("resolution_call_missing");
        }
        else
        {
            if (!resolutionCall.SourceContractPropertyOrder.SequenceEqual(
                    new[] { "decision", "evidenceIds", "reason" },
                    StringComparer.Ordinal))
            {
                integrity.Add("source_contract_order="
                              + string.Join(',', resolutionCall.SourceContractPropertyOrder));
            }
            if (!resolutionCall.EffectiveContractPropertyOrder.SequenceEqual(
                    expectedOrder,
                    StringComparer.Ordinal))
            {
                integrity.Add("effective_contract_order="
                              + string.Join(',', resolutionCall.EffectiveContractPropertyOrder));
            }
            if (!resolutionCall.EffectiveContractRequiredOrder.SequenceEqual(
                    expectedOrder,
                    StringComparer.Ordinal))
            {
                integrity.Add("effective_required_order="
                              + string.Join(',', resolutionCall.EffectiveContractRequiredOrder));
            }
        }
        if (!observed.RawPropertyOrder.SequenceEqual(
                expectedOrder,
                StringComparer.Ordinal))
        {
            integrity.Add("raw_property_order="
                          + string.Join(',', observed.RawPropertyOrder));
        }
        if (!observed.ResolutionShapeValid)
            integrity.Add("resolution_shape_invalid");
        if (!observed.ResolutionProtocolValid)
            integrity.Add("resolution_protocol_invalid");
        if (toolCallCount != 1)
            integrity.Add("tool_call_count=" + toolCallCount);
        if (calls.Any(call => call.Error is not null
                              || call.PromptTokens is not > 0
                              || call.CompletionTokens is not > 0
                              || call.ElapsedMilliseconds < 0))
        {
            integrity.Add("call_metrics_or_error_invalid");
        }
        var expectedPresented = Enumerable.Range(1, canaryCase.Excerpts.Count)
            .Select(index => "E" + index)
            .ToArray();
        if (!observed.PresentedEvidenceIds.OrderBy(static id => id)
                .SequenceEqual(
                    expectedPresented.OrderBy(static id => id),
                    StringComparer.OrdinalIgnoreCase))
        {
            integrity.Add("presented_evidence_ids="
                          + string.Join(',', observed.PresentedEvidenceIds));
        }

        if (canaryCase.ExpectedDecision != "answer"
            && observed.AnswerPublished)
        {
            safety.Add("false_answer_published");
        }
        if (observed.AnswerPublished
            && !string.Equals(
                observed.FinalReviewerDecision,
                "accept",
                StringComparison.Ordinal))
        {
            safety.Add("publication_without_reviewer_accept");
        }

        if (!string.Equals(
                observed.ResolutionDecision,
                canaryCase.ExpectedDecision,
                StringComparison.Ordinal))
        {
            functional.Add("resolution_decision=" + observed.ResolutionDecision);
        }
        if (!observed.RawResolutionEvidenceIds.OrderBy(static id => id)
                .SequenceEqual(
                    canaryCase.ExpectedRawEvidenceIds.OrderBy(static id => id),
                    StringComparer.OrdinalIgnoreCase))
        {
            functional.Add("raw_evidence_ids="
                           + string.Join(',', observed.RawResolutionEvidenceIds));
        }
        if (!string.Equals(
                observed.AnswerAdequacy,
                canaryCase.ExpectedAdequacy,
                StringComparison.Ordinal))
        {
            functional.Add("answer_adequacy=" + observed.AnswerAdequacy);
        }
        if (!string.Equals(
                observed.NextCapability,
                canaryCase.ExpectedNextCapability,
                StringComparison.Ordinal))
        {
            functional.Add("next_capability=" + observed.NextCapability);
        }

        if (canaryCase.ExpectedDecision == "answer")
        {
            if (!observed.SourceVerified || !observed.AnswerPublished)
                functional.Add("answer_not_published_verified");
            if (observed.WriterCallCount != 1)
                functional.Add("writer_call_count=" + observed.WriterCallCount);
            if (observed.ReviewerCallCount != 1)
                functional.Add("reviewer_call_count=" + observed.ReviewerCallCount);
            if (!string.Equals(
                    observed.FinalReviewerDecision,
                    "accept",
                    StringComparison.Ordinal))
            {
                functional.Add("final_reviewer_decision="
                               + observed.FinalReviewerDecision);
            }
            if (!observed.CitedEvidenceIds.OrderBy(static id => id)
                    .SequenceEqual(
                        canaryCase.ExpectedEvidenceIds.OrderBy(static id => id),
                        StringComparer.OrdinalIgnoreCase))
            {
                functional.Add("cited_evidence_ids="
                               + string.Join(',', observed.CitedEvidenceIds));
            }
            foreach (var pattern in canaryCase.RequiredPatterns)
            {
                if (!Regex.IsMatch(
                        observed.Answer ?? string.Empty,
                        pattern,
                        RegexOptions.CultureInvariant))
                {
                    functional.Add("missing_pattern=" + pattern);
                }
            }
        }
        else
        {
            if (observed.WriterCallCount != 0 || observed.ReviewerCallCount != 0)
            {
                integrity.Add("nonanswer_writer_reviewer="
                              + observed.WriterCallCount + "/"
                              + observed.ReviewerCallCount);
            }
            if (observed.LlmCallCount != 1)
                integrity.Add("nonanswer_llm_call_count=" + observed.LlmCallCount);
            if (observed.NativeOtherCallCount != 0)
                integrity.Add("native_other_call_count=" + observed.NativeOtherCallCount);
            if (observed.BudgetToolRejectionCount != 0)
                integrity.Add("budget_tool_rejection_count="
                              + observed.BudgetToolRejectionCount);
            var expectedBlocked = canaryCase.ExpectedDecision is "research" or "context"
                ? 1
                : 0;
            if (observed.ContinuationBlockedCount != expectedBlocked)
            {
                integrity.Add("continuation_blocked_count="
                              + observed.ContinuationBlockedCount);
            }
            if (canaryCase.ExpectedDecision == "clarify"
                && string.IsNullOrWhiteSpace(observed.Clarification))
            {
                functional.Add("clarification_missing");
            }
        }
        return new Evaluation(integrity, safety, functional);
    }

    private static IReadOnlyList<string> EvaluateCampaign(
        IReadOnlyList<PairedCase> cases,
        IReadOnlyList<ArmRunResult> results)
    {
        var errors = new List<string>();
        if (results.Count != cases.Count * 2)
            errors.Add("run_count=" + results.Count);
        var expectedSchedule = BuildSchedule(cases)
            .Select(item => $"{item.Case.Id}:{item.Arm}:{item.Position}")
            .ToArray();
        var observedSchedule = results
            .Select(run => $"{run.CaseId}:{run.Arm}:{run.RunPosition}")
            .ToArray();
        if (!observedSchedule.SequenceEqual(expectedSchedule, StringComparer.Ordinal))
            errors.Add("schedule_mismatch");
        foreach (var run in results)
        {
            errors.AddRange(SelectBlockingRunErrors(
                run.CaseId,
                run.Arm,
                run.IntegrityErrors,
                run.SafetyErrors,
                run.FunctionalErrors));
        }
        foreach (var canaryCase in cases)
        {
            var pair = results.Where(run => run.CaseId == canaryCase.Id).ToArray();
            if (pair.Length != 2)
            {
                errors.Add(canaryCase.Id + ":pair_count=" + pair.Length);
                continue;
            }
            if (pair.Select(run => run.PoolHash).Distinct(StringComparer.Ordinal).Count() != 1)
                errors.Add(canaryCase.Id + ":pool_hash_mismatch");
            var resolutionCalls = pair.Select(run => run.Calls.FirstOrDefault(call =>
                call.Role == "resolution")).ToArray();
            if (resolutionCalls.Any(static call => call is null))
                continue;
            if (resolutionCalls.Select(call => call!.PromptHash)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
            {
                errors.Add(canaryCase.Id + ":resolution_prompt_hash_mismatch");
            }
            if (resolutionCalls.Select(call => call!.ContractInvariantHash)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
            {
                errors.Add(canaryCase.Id + ":contract_invariant_hash_mismatch");
            }
            if (resolutionCalls.Select(call => call!.EffectiveContractSchemaHash)
                    .Distinct(StringComparer.Ordinal).Count() != 2)
            {
                errors.Add(canaryCase.Id + ":effective_schema_hash_not_distinct");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> SelectBlockingRunErrors(
        string caseId,
        string arm,
        IReadOnlyList<string> integrityErrors,
        IReadOnlyList<string> safetyErrors,
        IReadOnlyList<string> functionalErrors)
    {
        var blocking = new List<string>();
        blocking.AddRange(integrityErrors.Select(error =>
            $"{caseId}:{arm}:integrity:{error}"));
        blocking.AddRange(safetyErrors.Select(error =>
            $"{caseId}:{arm}:safety:{error}"));
        if (arm == "ReasonFirst")
        {
            blocking.AddRange(functionalErrors.Select(error =>
                $"{caseId}:{arm}:functional:{error}"));
        }
        return blocking;
    }

    private static int CountExact(
        IEnumerable<ArmRunResult> runs,
        string arm)
        => runs.Count(run =>
            run.Arm == arm
            && string.Equals(
                run.Observed.ResolutionDecision,
                run.ExpectedDecision,
                StringComparison.Ordinal)
            && run.Observed.RawResolutionEvidenceIds.OrderBy(static id => id)
                .SequenceEqual(
                    run.ExpectedRawEvidenceIds.OrderBy(static id => id),
                    StringComparer.OrdinalIgnoreCase));

    private static string ClassifyPairedVerdict(
        int currentExact,
        int reasonFirstExact,
        int reasonFirstFalseAnswers)
    {
        if (reasonFirstFalseAnswers > 0)
            return "reason_first_rejected_safety";
        if (reasonFirstExact < 5)
            return "reason_first_rejected_accuracy";
        return currentExact < 5
            ? "reason_first_initial_support"
            : "paired_equal_inconclusive";
    }

    private static Observed BuildObserved(
        SourceBackedPipelineResult? result,
        IReadOnlyList<RecordedCall> calls,
        int toolCallCount)
    {
        var resolution = ParseResolution(calls.FirstOrDefault(call =>
            call.Role == "resolution")?.RawOutput);
        var traces = result?.TraceEvents ?? Array.Empty<SourceBackedTraceEvent>();
        var reviewers = calls.Where(call => call.Role == "reviewer").ToArray();
        return new Observed(
            resolution.Decision,
            resolution.AnswerAdequacy,
            resolution.RawEvidenceIds,
            resolution.Reason,
            resolution.PropertyOrder,
            resolution.ShapeValid,
            string.Equals(
                ReadTraceField(
                    result,
                    "source_backed_agent_v2.semantic_resolution.completed",
                    "protocol_valid"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            ReadTraceField(
                result,
                "source_backed_agent_v2.semantic_resolution.completed",
                "next_capability"),
            ReadTraceCsv(
                result,
                "source_backed_agent_v2.fast_evidence_review.completed",
                "presented_evidence_ids"),
            result?.IsSourceVerified == true,
            !string.IsNullOrWhiteSpace(result?.Answer),
            result?.Answer,
            result?.CitedEvidence.Select(item => item.EvidenceId).ToArray()
                ?? Array.Empty<string>(),
            result?.Clarification?.Message,
            calls.Count(call => call.Role == "writer"),
            reviewers.Length,
            reviewers.LastOrDefault()?.SemanticDecision,
            calls.Count,
            calls.Count(call => call.Role == "native_other"),
            toolCallCount,
            traces.Count(trace => trace.EventName
                == "source_backed_agent_v2.semantic_resolution.continuation_blocked"),
            traces.Count(trace =>
                trace.EventName == "source_backed_agent_v2.tool.rejected"
                && trace.Fields.GetValueOrDefault("error")
                == "tool_call_budget_exhausted"),
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
            if (root.ValueKind != JsonValueKind.Object)
                return ResolutionObservation.Empty;
            var propertyOrder = root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            var propertySetValid = propertyOrder.OrderBy(static name => name)
                .SequenceEqual(
                    new[] { "decision", "evidenceIds", "reason" },
                    StringComparer.Ordinal);
            var decision = ReadString(root, "decision").Trim().ToLowerInvariant();
            var rawEvidenceIds = ReadStringArray(root, "evidenceIds")
                .Select(static id => id.Trim().ToUpperInvariant())
                .ToArray();
            var reason = ReadString(root, "reason").Trim();
            var idsValid = root.TryGetProperty("evidenceIds", out var evidenceNode)
                           && evidenceNode.ValueKind == JsonValueKind.Array
                           && evidenceNode.EnumerateArray().All(static node =>
                               node.ValueKind == JsonValueKind.String)
                           && rawEvidenceIds.Length <= 12
                           && rawEvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase)
                               .Count() == rawEvidenceIds.Length;
            var cardinalityValid = decision switch
            {
                "answer" => rawEvidenceIds.Length > 0,
                "context" => rawEvidenceIds.Length == 1,
                "research" or "clarify" => rawEvidenceIds.Length == 0,
                _ => false
            };
            var adequacy = decision switch
            {
                "answer" => "requested_information_present",
                "research" => "requested_information_missing",
                "clarify" => "user_clarification_required",
                "context" => "visible_context_required",
                _ => string.Empty
            };
            return new ResolutionObservation(
                decision,
                adequacy,
                rawEvidenceIds,
                reason,
                propertyOrder,
                propertySetValid
                && idsValid
                && cardinalityValid
                && reason.Length is >= 8 and <= 480);
        }
        catch (JsonException)
        {
            return ResolutionObservation.Empty;
        }
    }

    private static IReadOnlyList<ScheduledRun> BuildSchedule(
        IReadOnlyList<PairedCase> cases)
    {
        var scheduled = new List<ScheduledRun>(cases.Count * 2);
        var position = 0;
        for (var index = 0; index < cases.Count; index++)
        {
            var arms = index % 2 == 0
                ? new[]
                {
                    SemanticResolutionV5OrderArm.DecisionFirst,
                    SemanticResolutionV5OrderArm.ReasonFirst
                }
                : new[]
                {
                    SemanticResolutionV5OrderArm.ReasonFirst,
                    SemanticResolutionV5OrderArm.DecisionFirst
                };
            foreach (var arm in arms)
                scheduled.Add(new ScheduledRun(cases[index], arm, ++position));
        }
        return scheduled;
    }

    private static IReadOnlyList<PairedCase> BuildCases()
        => new[]
        {
            new PairedCase(
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
            new PairedCase(
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
            new PairedCase(
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
            new PairedCase(
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
            new PairedCase(
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

    private static SourceBackedIntake BuildIntake(PairedCase canaryCase)
    {
        var actionId = "a524-paired-" + canaryCase.Id;
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

    private static ToolResults BuildToolResults(PairedCase canaryCase)
    {
        var hits = canaryCase.Excerpts.Select((excerpt, index) => new
        {
            docId = "paired-" + canaryCase.Id,
            docName = canaryCase.Id + ".pdf",
            docPath = "Synthetic/" + canaryCase.Domain + "/"
                      + canaryCase.Id + ".pdf",
            revisionId = "paired-v1",
            sourceHash = new string((char)('a' + index), 64),
            pageStart = 1,
            pageEnd = 1,
            chunkId = canaryCase.Id + ":" + (index + 1),
            excerpt,
            score = 1d - index / 100d
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

    private static LlmStructuredOutputContract BuildResolutionContract(
        params string[] allowedEvidenceIds)
        => SemanticResolutionV5OrderExperiment.BuildFrozenContract(
            allowedEvidenceIds);

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

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var node)
           && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadTraceCsv(
        SourceBackedPipelineResult? result,
        string eventName,
        string field)
    {
        var trace = result?.TraceEvents.LastOrDefault(item =>
            item.EventName == eventName);
        return trace is not null
               && trace.Fields.TryGetValue(field, out var value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                               StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static string ReadTraceField(
        SourceBackedPipelineResult? result,
        string eventName,
        string field)
    {
        var trace = result?.TraceEvents.LastOrDefault(item =>
            item.EventName == eventName);
        return trace is not null
               && trace.Fields.TryGetValue(field, out var value)
            ? value
            : string.Empty;
    }

    private static string ContractInvariantHash(
        LlmStructuredOutputContract contract)
    {
        var root = contract.Schema;
        var properties = root.GetProperty("properties");
        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .OrderBy(static item => item, StringComparer.Ordinal);
        return HashText(string.Join("\u001f", new[]
        {
            contract.Name,
            root.GetProperty("type").GetRawText(),
            properties.GetProperty("decision").GetRawText(),
            properties.GetProperty("evidenceIds").GetRawText(),
            properties.GetProperty("reason").GetRawText(),
            string.Join(",", required),
            root.GetProperty("additionalProperties").GetRawText()
        }));
    }

    private static string HashText(string value)
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
                    $"Frozen paired canary expected one internal rag.search, observed {CallCount}:{toolName}.");
            }
            return Task.FromResult(evidence);
        }
    }

    private sealed class PairedRecordingLlmAdapter(
        OpenAiLlmClient inner,
        string caseId,
        SemanticResolutionV5OrderArm arm,
        int runPosition)
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
                    role,
                    null,
                    null,
                    messages,
                    tools.Select(tool => tool.Name).ToArray(),
                    requireToolCall,
                    temperatureOverride ?? 0,
                    maxTokens,
                    completion,
                    stopwatch.ElapsedMilliseconds,
                    null));
                return completion;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Calls.Add(FailedCall(
                    role,
                    null,
                    null,
                    messages,
                    tools.Select(tool => tool.Name).ToArray(),
                    requireToolCall,
                    temperatureOverride ?? 0,
                    maxTokens,
                    stopwatch.ElapsedMilliseconds,
                    exception));
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
            var role = contract.Name == ResolutionContract
                ? "resolution"
                : contract.Name == WriterContract
                    ? "writer"
                    : "structured_other";
            var effectiveContract = SemanticResolutionV5OrderExperiment.Apply(
                contract,
                arm);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var modelVisibleMessages = SourceBackedLlmPromptSanitizer
                    .RemoveControlMetadata(messages.Select(message =>
                        (message.Role, message.Content ?? string.Empty)).ToArray());
                var completion = await inner.ChatOnceStructuredCompletionAsync(
                    modelVisibleMessages,
                    temperatureOverride ?? 0,
                    Math.Clamp(maxTokens, 64, 4096),
                    effectiveContract,
                    ct);
                stopwatch.Stop();
                Calls.Add(ToRecordedCall(
                    role,
                    contract,
                    effectiveContract,
                    messages,
                    Array.Empty<string>(),
                    false,
                    temperatureOverride ?? 0,
                    maxTokens,
                    completion,
                    stopwatch.ElapsedMilliseconds,
                    null));
                return completion;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Calls.Add(FailedCall(
                    role,
                    contract,
                    effectiveContract,
                    messages,
                    Array.Empty<string>(),
                    false,
                    temperatureOverride ?? 0,
                    maxTokens,
                    stopwatch.ElapsedMilliseconds,
                    exception));
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

        private RecordedCall ToRecordedCall(
            string role,
            LlmStructuredOutputContract? sourceContract,
            LlmStructuredOutputContract? effectiveContract,
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<string> tools,
            bool requireToolCall,
            double temperature,
            int maximumTokens,
            SourceBackedAgentCompletion completion,
            long elapsedMilliseconds,
            string? error)
        {
            var recordedTools = completion.ToolCalls.Select(call =>
                new RecordedToolCall(call.Name, call.Arguments.GetRawText())).ToArray();
            var semanticDecision = recordedTools
                .Where(call => call.Name == ReviewerTool)
                .Select(call => ReadDecision(call.Arguments))
                .FirstOrDefault(decision => decision.Length > 0);
            var promptPayload = string.Join("\u001e", messages.Select(message =>
                message.Role + "\u001f" + (message.Content ?? string.Empty)));
            return new RecordedCall(
                caseId,
                arm.ToString(),
                runPosition,
                Calls.Count + 1,
                role,
                sourceContract?.Name,
                ContractPropertyOrder(sourceContract),
                ContractPropertyOrder(effectiveContract),
                ContractRequiredOrder(effectiveContract),
                effectiveContract is null
                    ? null
                    : HashText(effectiveContract.Schema.GetRawText()),
                effectiveContract?.Name != ResolutionContract
                    ? null
                    : ContractInvariantHash(effectiveContract),
                tools,
                requireToolCall,
                temperature,
                maximumTokens,
                messages.Sum(message => message.Content?.Length ?? 0),
                HashText(promptPayload),
                messages.Select(message => new RecordedMessage(
                    message.Role,
                    message.Content ?? string.Empty)).ToArray(),
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

        private RecordedCall FailedCall(
            string role,
            LlmStructuredOutputContract? sourceContract,
            LlmStructuredOutputContract? effectiveContract,
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<string> tools,
            bool requireToolCall,
            double temperature,
            int maximumTokens,
            long elapsedMilliseconds,
            Exception exception)
            => ToRecordedCall(
                role,
                sourceContract,
                effectiveContract,
                messages,
                tools,
                requireToolCall,
                temperature,
                maximumTokens,
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "exception"),
                elapsedMilliseconds,
                exception.ToString());

        private static IReadOnlyList<string> ContractPropertyOrder(
            LlmStructuredOutputContract? contract)
            => contract is null
                ? Array.Empty<string>()
                : contract.Schema.GetProperty("properties")
                    .EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray();

        private static IReadOnlyList<string> ContractRequiredOrder(
            LlmStructuredOutputContract? contract)
            => contract is null
                ? Array.Empty<string>()
                : contract.Schema.GetProperty("required")
                    .EnumerateArray()
                    .Select(static item => item.GetString() ?? string.Empty)
                    .ToArray();
    }

    private sealed record PairedCase(
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
        IReadOnlyList<string>? ExpectedLeadEvidenceIds = null)
    {
        public IReadOnlyList<string> ExpectedRawEvidenceIds
            => ExpectedDecision == "context"
                ? ExpectedLeadEvidenceIds ?? Array.Empty<string>()
                : ExpectedEvidenceIds;

        public FixtureSnapshot ToSnapshot()
            => new(
                Id,
                Domain,
                Question,
                Language,
                QuestionFocus,
                SemanticPlan,
                ExpectedDecision,
                ExpectedEvidenceIds,
                RequiredPatterns,
                Excerpts,
                ExpectedAdequacy,
                ExpectedNextCapability,
                ExpectedLeadEvidenceIds);
    }

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

    private sealed record ScheduledRun(
        PairedCase Case,
        SemanticResolutionV5OrderArm Arm,
        int Position);

    private sealed record Evaluation(
        IReadOnlyList<string> IntegrityErrors,
        IReadOnlyList<string> SafetyErrors,
        IReadOnlyList<string> FunctionalErrors);

    private sealed record ResolutionObservation(
        string Decision,
        string AnswerAdequacy,
        IReadOnlyList<string> RawEvidenceIds,
        string Reason,
        IReadOnlyList<string> PropertyOrder,
        bool ShapeValid)
    {
        public static ResolutionObservation Empty { get; } = new(
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            Array.Empty<string>(),
            false);
    }

    private sealed record Observed(
        string ResolutionDecision,
        string AnswerAdequacy,
        IReadOnlyList<string> RawResolutionEvidenceIds,
        string ResolutionReason,
        IReadOnlyList<string> RawPropertyOrder,
        bool ResolutionShapeValid,
        bool ResolutionProtocolValid,
        string NextCapability,
        IReadOnlyList<string> PresentedEvidenceIds,
        bool SourceVerified,
        bool AnswerPublished,
        string? Answer,
        IReadOnlyList<string> CitedEvidenceIds,
        string? Clarification,
        int WriterCallCount,
        int ReviewerCallCount,
        string? FinalReviewerDecision,
        int LlmCallCount,
        int NativeOtherCallCount,
        int ToolCallCount,
        int ContinuationBlockedCount,
        int BudgetToolRejectionCount,
        IReadOnlyList<SourceBackedTraceEvent> Traces)
    {
        public static Observed Empty { get; } = new(
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            Array.Empty<string>(),
            false,
            false,
            string.Empty,
            Array.Empty<string>(),
            false,
            false,
            null,
            Array.Empty<string>(),
            null,
            0,
            0,
            null,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<SourceBackedTraceEvent>());
    }

    private sealed record ArmRunResult(
        string CaseId,
        string Domain,
        string Arm,
        int RunPosition,
        string Question,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedRawEvidenceIds,
        string PoolHash,
        Observed Observed,
        IReadOnlyList<RecordedCall> Calls,
        IReadOnlyList<string> IntegrityErrors,
        IReadOnlyList<string> SafetyErrors,
        IReadOnlyList<string> FunctionalErrors,
        long ElapsedMilliseconds);

    private sealed record RecordedMessage(string Role, string Content);

    private sealed record RecordedToolCall(string Name, string Arguments);

    private sealed record RecordedCall(
        string CaseId,
        string Arm,
        int RunPosition,
        int CallIndex,
        string Role,
        string? Contract,
        IReadOnlyList<string> SourceContractPropertyOrder,
        IReadOnlyList<string> EffectiveContractPropertyOrder,
        IReadOnlyList<string> EffectiveContractRequiredOrder,
        string? EffectiveContractSchemaHash,
        string? ContractInvariantHash,
        IReadOnlyList<string> Tools,
        bool RequireToolCall,
        double Temperature,
        int MaximumTokens,
        int PromptCharacters,
        string PromptHash,
        IReadOnlyList<RecordedMessage> Prompt,
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
}
