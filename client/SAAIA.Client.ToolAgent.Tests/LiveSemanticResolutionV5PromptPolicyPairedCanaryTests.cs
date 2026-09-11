using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSemanticResolutionV5PromptPolicyPairedCanaryTests(
    ITestOutputHelper output)
{
    private const string LiveFlag =
        "SAAIA_LIVE_PAIRED_RESOLUTION_POLICY_A539";
    private const string OutputVariable =
        "SAAIA_LIVE_PAIRED_RESOLUTION_POLICY_A539_OUTPUT";
    private const string ResolutionContract =
        "source_backed_semantic_resolution_v6";
    private const string SchemaVersion =
        "semantic_resolution_v5_prompt_policy_paired_a539_v1";
    private const string ProtocolSha256 =
        "FD4B384F8A98799CFCC79364A6CAF76F37CEC35647C7850EAF79798FBEEAF510";
    private const string GoalSha256 =
        "6BAE663308460C8F68B0CCEEF3420E8BD30DEDB19B987F9207130DCEF35E63CD";
    private const string RuntimeSha256 =
        "4D8071C42A815795D51DCEFE8A558AEA241228126F1D8FD27B1325C09C93DE7D";
    private const int MaximumTotalTokens = 12_000;
    private const long MaximumCampaignMilliseconds = 360_000;

    [Fact]
    public void A539_cases_match_the_four_preregistered_transactions()
    {
        var cases = BuildCases();

        Assert.Equal(4, cases.Count);
        Assert.Equal(
            new[] { "pair_omicron", "pair_pi", "pair_rho", "pair_sigma" },
            cases.Select(static item => item.Id));
        Assert.Equal(
            new[] { "answer", "context", "research", "research" },
            cases.Select(static item => item.ExpectedDecision));
        Assert.Equal(new[] { "E2", "E1" }, cases[0].ExpectedEvidenceIds);
        Assert.Equal(new[] { "E1" }, cases[1].ExpectedEvidenceIds);
        Assert.Empty(cases[2].ExpectedEvidenceIds);
        Assert.Empty(cases[3].ExpectedEvidenceIds);
        Assert.Equal(new[] { 2, 1, 2, 2 },
            cases.Select(static item => item.Excerpts.Count));
    }

    [Fact]
    public void A539_schedule_matches_the_eight_preregistered_positions()
    {
        var observed = BuildSchedule(BuildCases())
            .Select(static item =>
                $"{item.Position}:{item.Case.Id}:{ArmName(item.Arm)}")
            .ToArray();

        Assert.Equal(
            new[]
            {
                "1:pair_omicron:candidate",
                "2:pair_omicron:current",
                "3:pair_pi:current",
                "4:pair_pi:candidate",
                "5:pair_rho:candidate",
                "6:pair_rho:current",
                "7:pair_sigma:current",
                "8:pair_sigma:candidate"
            },
            observed);
    }

    [Fact]
    public void A539_source_identities_are_unique_and_oracle_neutral()
    {
        var forbidden = new[]
        {
            "answer", "research", "context", "focus", "support", "winner",
            "requires", "expected", "current", "candidate", "lead"
        };
        var identities = BuildCases()
            .SelectMany(static item => item.SourceIdentities)
            .ToArray();

        Assert.Equal(7, identities.Length);
        Assert.Equal(7, identities.Select(static item => item.DocId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(7, identities.Select(static item => item.DocPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(7, identities.Select(static item => item.ChunkId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var identity in identities)
        {
            foreach (var value in identity.Values)
            {
                Assert.DoesNotContain(forbidden, token => value.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task A539_candidate_changes_only_three_clauses_of_the_real_runner_prompt()
    {
        var priorFixtureTokens = new[]
        {
            "txn_kappa", "txn_lambda", "txn_mu", "txn_nu", "Kappa", "Lambda",
            "Zeta", "M-7", "Q-17", "survey_drone", "rail_wear", "pump_handoff"
        };
        var candidatePolicy = string.Join(
            " ",
            SemanticResolutionV5PromptPolicyExperiment.CandidateSupportClause,
            SemanticResolutionV5PromptPolicyExperiment.CandidateContextClause,
            SemanticResolutionV5PromptPolicyExperiment.CandidateReasonClause);
        Assert.DoesNotContain(priorFixtureTokens, token => candidatePolicy.Contains(
            token,
            StringComparison.OrdinalIgnoreCase));

        foreach (var canaryCase in BuildCases())
        {
            var capture = await CaptureCurrentPromptAsync(
                canaryCase,
                CancellationToken.None);
            var current = SemanticResolutionV5PromptPolicyExperiment.Apply(
                capture.Messages,
                SemanticResolutionV5PromptPolicyArm.Current);
            var candidate = SemanticResolutionV5PromptPolicyExperiment.Apply(
                capture.Messages,
                SemanticResolutionV5PromptPolicyArm.Candidate);
            var currentSystem = SingleMessage(current, "system");
            var candidateSystem = SingleMessage(candidate, "system");

            Assert.Equal(2, capture.Messages.Count);
            Assert.Equal(1, capture.ToolCallCount);
            Assert.Equal(1, capture.ResolutionCallCount);
            Assert.Equal(ResolutionContract, capture.Contract.Name);
            Assert.Equal(0d, capture.Temperature);
            Assert.Equal(256, capture.MaximumTokens);
            Assert.Equal(
                new[]
                {
                    "decision", "evidenceIds", "leadEvidenceIds", "reason"
                },
                ContractPropertyOrder(capture.Contract));
            Assert.Equal(
                new[]
                {
                    "decision", "evidenceIds", "leadEvidenceIds", "reason"
                },
                ContractRequiredOrder(capture.Contract));
            Assert.Equal(
                Enumerable.Range(1, canaryCase.Excerpts.Count)
                    .Select(static index => "E" + index),
                ContractAllowedEvidenceIds(capture.Contract));
            Assert.Equal(SingleMessage(current, "user"), SingleMessage(candidate, "user"));
            Assert.NotEqual(currentSystem, candidateSystem);
            Assert.Equal(
                currentSystem,
                SemanticResolutionV5PromptPolicyExperiment.RecoverCurrentSystem(
                    candidateSystem));
            Assert.Contains("PORTEE_COMPARATIVE:", currentSystem, StringComparison.Ordinal);
            Assert.Contains("PORTEE_GLOBALE:", currentSystem, StringComparison.Ordinal);
            Assert.Contains(
                SemanticResolutionV5PromptPolicyExperiment.CurrentSupportClause,
                currentSystem,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                SemanticResolutionV5PromptPolicyExperiment.CurrentSupportClause,
                candidateSystem,
                StringComparison.Ordinal);
            Assert.Contains(
                SemanticResolutionV5PromptPolicyExperiment.CandidateSupportClause,
                candidateSystem,
                StringComparison.Ordinal);
        }

        Assert.True(SemanticResolutionV5PromptPolicyExperiment.IsCandidateReasonValid(
            "La preuve localise le contenu demandé."));
        Assert.False(SemanticResolutionV5PromptPolicyExperiment.IsCandidateReasonValid(
            "La preuve localise le contenu demandé"));
        Assert.False(SemanticResolutionV5PromptPolicyExperiment.IsCandidateReasonValid(
            new string('a', 240) + "."));
    }

    [Theory]
    [InlineData(false, 4, 4, 4, 4, 2, "campaign_invalid")]
    [InlineData(true, 4, 4, 4, 4, 1, "candidate_rejected_safety")]
    [InlineData(true, 3, 4, 3, 4, 2, "candidate_rejected_accuracy")]
    [InlineData(true, 3, 3, 4, 3, 2, "candidate_rejected_reason")]
    [InlineData(true, 4, 4, 4, 4, 2, "paired_equal_perfect_inconclusive")]
    [InlineData(true, 3, 2, 4, 4, 2, "candidate_supported")]
    public void A539_verdict_follows_the_preregistered_classification(
        bool integrity,
        int currentExact,
        int currentReasons,
        int candidateExact,
        int candidateReasons,
        int candidateSafety,
        string expected)
        => Assert.Equal(
            expected,
            SemanticResolutionV5PromptPolicyExperiment.Classify(
                integrity,
                currentExact,
                currentReasons,
                candidateExact,
                 candidateReasons,
                 candidateSafety));

    [Fact]
    public void A542_campaign_outcome_keeps_integrity_and_candidate_gates_separate()
    {
        const string gateError = "pair_omicron:candidate_decision_or_ids";
        var outcome = BuildCampaignOutcome(
            Array.Empty<string>(),
            new[] { gateError },
            currentExact: 3,
            currentReasons: 0,
            candidateExact: 3,
            candidateReasons: 4,
            candidateSafety: 2);

        Assert.Equal("candidate_rejected_accuracy", outcome.PairedVerdict);
        Assert.Empty(outcome.IntegrityErrors);
        Assert.DoesNotContain(gateError, outcome.IntegrityErrors);
        Assert.Equal(new[] { gateError }, outcome.CandidateGateErrors);
        Assert.Contains(gateError, outcome.FailureReasons);
        Assert.Contains(
            "paired_verdict=candidate_rejected_accuracy",
            outcome.FailureReasons);
    }

    [Fact]
    public async Task A542_prompt_capture_serializes_named_messages()
    {
        var capture = await CaptureCurrentPromptAsync(
            BuildCases()[0],
            CancellationToken.None);
        var json = JsonSerializer.Serialize(
            capture,
            new JsonSerializerOptions { WriteIndented = true });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("Messages", out _));
        var recordedMessages = root.GetProperty("RecordedMessages")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, recordedMessages.Length);
        Assert.Equal("system", recordedMessages[0].GetProperty("Role").GetString());
        Assert.Equal("user", recordedMessages[1].GetProperty("Role").GetString());
        Assert.All(recordedMessages, message => Assert.False(string.IsNullOrWhiteSpace(
            message.GetProperty("Content").GetString())));
        Assert.Equal(
            capture.PromptSha256,
            HashText(BuildRecordedPrompt(capture.Messages)));
    }

    [Fact]
    public Task Live_qwen_a539_paired_prompt_policy_matches_preregistered_gates_when_enabled()
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
                "A539 requires configured local LLM settings.");
        }

        var repo = FindRepoRoot();
        var artifact = FirstNonBlank(
                           Environment.GetEnvironmentVariable(OutputVariable))
                       ?? Path.Combine(
                           repo,
                           "artifacts",
                           "goal-rag-product-20260827-1041",
                           "phase5",
                           "a539-live-paired-resolution-policy",
                           "a539-paired-resolution-policy.json");
        var callsArtifact = Path.Combine(
            Path.GetDirectoryName(artifact)!,
            "a539-paired-resolution-policy-calls.jsonl");
        var cases = BuildCases();
        var schedule = BuildSchedule(cases);
        var captures = new Dictionary<string, PromptCapture>(StringComparer.Ordinal);
        var results = new List<RunResult>();
        var calls = new List<RecordedCall>();
        var integrityErrors = new List<string>();
        var campaignFailures = new List<string>();
        LlamaCppProcessManager? runtimeManager = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var campaignStopwatch = Stopwatch.StartNew();

        try
        {
            AssertFrozenProductFiles(repo, integrityErrors);
            foreach (var canaryCase in cases)
            {
                captures[canaryCase.Id] = await CaptureCurrentPromptAsync(
                    canaryCase,
                    cts.Token);
            }
            integrityErrors.AddRange(EvaluateCaptures(cases, captures));

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
                var capture = captures[scheduled.Case.Id];
                var modelMessages = SemanticResolutionV5PromptPolicyExperiment.Apply(
                    capture.Messages,
                    scheduled.Arm);
                var inner = new OpenAiLlmClient();
                inner.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
                var stopwatch = Stopwatch.StartNew();
                RecordedCall call;
                try
                {
                    var completion = await inner.ChatOnceStructuredCompletionAsync(
                        modelMessages,
                        capture.Temperature,
                        Math.Clamp(capture.MaximumTokens, 64, 4096),
                        capture.Contract,
                        cts.Token);
                    stopwatch.Stop();
                    call = BuildRecordedCall(
                        scheduled,
                        modelMessages,
                        capture,
                        completion,
                        stopwatch.ElapsedMilliseconds,
                        null);
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    call = BuildRecordedCall(
                        scheduled,
                        modelMessages,
                        capture,
                        new SourceBackedAgentCompletion(
                            string.Empty,
                            Array.Empty<SourceBackedAgentToolCall>(),
                            "exception"),
                        stopwatch.ElapsedMilliseconds,
                        exception.ToString());
                }

                calls.Add(call);
                var observed = ParseResolution(
                    call.RawOutput,
                    ContractAllowedEvidenceIds(capture.Contract));
                var run = EvaluateRun(scheduled, call, observed);
                results.Add(run);
                output.WriteLine(
                    $"{scheduled.Position}:{scheduled.Case.Id}:{ArmName(scheduled.Arm)} "
                    + $"decision={observed.Decision};ids={string.Join(',', observed.EvidenceIds)};"
                    + $"reason={observed.Reason.Length};protocol={observed.ProtocolValid};"
                    + $"exact={run.OrderedDecisionAndIdsExact};format={run.ReasonFormatValid};"
                    + $"semantic={run.ReasonSemanticValid}");
            }
        }
        catch (Exception exception)
        {
            integrityErrors.Add("transport_or_harness:" + exception);
        }
        finally
        {
            campaignStopwatch.Stop();
            try
            {
                var campaignEvaluation = EvaluateCampaign(
                    cases,
                    schedule,
                    captures,
                    results,
                    calls,
                    campaignStopwatch.ElapsedMilliseconds);
                integrityErrors.AddRange(campaignEvaluation.IntegrityErrors);

                var currentRuns = results.Where(static run => run.Arm == "current").ToArray();
                var candidateRuns = results.Where(static run => run.Arm == "candidate").ToArray();
                var currentExact = currentRuns.Count(static run =>
                    run.OrderedDecisionAndIdsExact && run.ReasonSemanticValid);
                var candidateExact = candidateRuns.Count(static run =>
                    run.OrderedDecisionAndIdsExact && run.ReasonSemanticValid);
                var currentRawExact = currentRuns.Count(static run =>
                    run.OrderedDecisionAndIdsExact);
                var candidateRawExact = candidateRuns.Count(static run =>
                    run.OrderedDecisionAndIdsExact);
                var currentReasons = currentRuns.Count(static run => run.ReasonFormatValid);
                var candidateReasons = candidateRuns.Count(static run => run.ReasonFormatValid);
                var candidateSafety = candidateRuns.Count(static run =>
                    run.IsSafetyCase && run.SafetyValid);
                var campaignOutcome = BuildCampaignOutcome(
                    integrityErrors,
                    campaignEvaluation.CandidateGateErrors,
                    currentExact,
                    currentReasons,
                    candidateExact,
                    candidateReasons,
                    candidateSafety);
                campaignFailures.AddRange(campaignOutcome.FailureReasons);

                var payload = new
                {
                    SchemaVersion,
                    ResolutionContract,
                    Model = model,
                    LlmBaseUrl = llmBaseUrl,
                    Branch = "SAAIA_V3.1",
                    Head = "5f35881cdc67d12a076fcd2a7a1004656ac9a37a",
                    GoalSha256,
                    ProtocolSha256,
                    RuntimeSha256,
                    ExpectedCaseCount = 4,
                    ExpectedRunCount = 8,
                    ExecutedRunCount = results.Count,
                    CurrentExactDecisionAndOrderedIds = currentRawExact,
                    CandidateExactDecisionAndOrderedIds = candidateRawExact,
                    CurrentFunctionalExact = currentExact,
                    CandidateFunctionalExact = candidateExact,
                    CurrentReasonGateValid = currentReasons,
                    CandidateReasonGateValid = candidateReasons,
                    CandidateSafetyValid = candidateSafety,
                    CandidateFalseAnswerOrContextCount = candidateRuns.Count(static run =>
                        run.IsSafetyCase && !run.SafetyValid),
                    PairedVerdict = campaignOutcome.PairedVerdict,
                    ProtocolValidRunCount = results.Count(static run =>
                        run.Observed.ProtocolValid),
                    WriterCallCount = 0,
                    ReviewerCallCount = 0,
                    HandoffCount = 0,
                    PublicationCount = 0,
                    PromptTokens = calls.Sum(static call => call.PromptTokens ?? 0),
                    CompletionTokens = calls.Sum(static call => call.CompletionTokens ?? 0),
                    CacheTokens = calls.Sum(static call => call.ServerCacheTokens ?? 0),
                    TotalTokens = calls.Sum(static call =>
                        (call.PromptTokens ?? 0) + (call.CompletionTokens ?? 0)),
                    LlmElapsedMilliseconds = calls.Sum(static call => call.ElapsedMilliseconds),
                    CampaignElapsedMilliseconds = campaignStopwatch.ElapsedMilliseconds,
                    MaximumTotalTokens,
                    MaximumCampaignMilliseconds,
                    RuntimeManaged = runtimeManager is not null,
                    IntegrityErrors = campaignOutcome.IntegrityErrors,
                    CandidateGateErrors = campaignOutcome.CandidateGateErrors,
                    IntegrityAndGateErrors = campaignOutcome.FailureReasons,
                    Captures = captures.Values.OrderBy(static capture => capture.CaseId),
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
                    calls.Select(static call => JsonSerializer.Serialize(call)),
                    CancellationToken.None);
                output.WriteLine("Artifact: " + artifact);
                output.WriteLine("Calls artifact: " + callsArtifact);
            }
            finally
            {
                runtimeManager?.Stop();
            }
        }

        Assert.Equal(8, results.Count);
        Assert.True(
            campaignFailures.Count == 0,
            string.Join(Environment.NewLine, campaignFailures));
    }

    private static async Task<PromptCapture> CaptureCurrentPromptAsync(
        PairedCase canaryCase,
        CancellationToken ct)
    {
        var llm = new PromptCaptureLlmAdapter(canaryCase.Id);
        var executor = new FrozenEvidenceExecutor(BuildToolResults(canaryCase));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
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

        await runner.RunAsync(BuildIntake(canaryCase), ct);
        if (llm.Capture is null)
            throw new InvalidOperationException("A539 runner did not emit a resolution prompt.");
        return llm.Capture with
        {
            ToolCallCount = executor.CallCount,
            ResolutionCallCount = llm.ResolutionCallCount
        };
    }

    private static IReadOnlyList<string> EvaluateCaptures(
        IReadOnlyList<PairedCase> cases,
        IReadOnlyDictionary<string, PromptCapture> captures)
    {
        var errors = new List<string>();
        if (captures.Count != 4)
            errors.Add("capture_count=" + captures.Count);
        foreach (var canaryCase in cases)
        {
            if (!captures.TryGetValue(canaryCase.Id, out var capture))
            {
                errors.Add(canaryCase.Id + ":capture_missing");
                continue;
            }
            if (capture.ToolCallCount != 1 || capture.ResolutionCallCount != 1)
            {
                errors.Add(canaryCase.Id + ":capture_counts="
                           + capture.ToolCallCount + "/" + capture.ResolutionCallCount);
            }
            if (capture.Contract.Name != ResolutionContract)
                errors.Add(canaryCase.Id + ":contract=" + capture.Contract.Name);
            if (capture.Temperature != 0 || capture.MaximumTokens != 256)
            {
                errors.Add(canaryCase.Id + ":capture_parameters="
                           + capture.Temperature + "/" + capture.MaximumTokens);
            }
            if (!ContractPropertyOrder(capture.Contract).SequenceEqual(
                    new[] { "decision", "evidenceIds", "reason" },
                    StringComparer.Ordinal)
                || !ContractRequiredOrder(capture.Contract).SequenceEqual(
                    new[] { "decision", "evidenceIds", "reason" },
                    StringComparer.Ordinal))
            {
                errors.Add(canaryCase.Id + ":contract_order");
            }
            try
            {
                var candidate = SemanticResolutionV5PromptPolicyExperiment.Apply(
                    capture.Messages,
                    SemanticResolutionV5PromptPolicyArm.Candidate);
                var currentSystem = SingleMessage(capture.Messages, "system");
                var candidateSystem = SingleMessage(candidate, "system");
                if (SemanticResolutionV5PromptPolicyExperiment.RecoverCurrentSystem(
                        candidateSystem) != currentSystem)
                {
                    errors.Add(canaryCase.Id + ":candidate_reverse_mismatch");
                }
                if (SingleMessage(capture.Messages, "user") != SingleMessage(candidate, "user"))
                    errors.Add(canaryCase.Id + ":candidate_user_changed");
            }
            catch (Exception exception)
            {
                errors.Add(canaryCase.Id + ":candidate_transform:" + exception.Message);
            }
        }
        return errors;
    }

    private static CampaignEvaluation EvaluateCampaign(
        IReadOnlyList<PairedCase> cases,
        IReadOnlyList<ScheduledRun> schedule,
        IReadOnlyDictionary<string, PromptCapture> captures,
        IReadOnlyList<RunResult> results,
        IReadOnlyList<RecordedCall> calls,
        long elapsedMilliseconds)
    {
        var integrityErrors = new List<string>();
        var candidateGateErrors = new List<string>();
        if (results.Count != 8)
            integrityErrors.Add("run_count=" + results.Count);
        if (calls.Count != 8)
            integrityErrors.Add("call_count=" + calls.Count);
        var expectedSchedule = schedule.Select(static item =>
            $"{item.Position}:{item.Case.Id}:{ArmName(item.Arm)}");
        var observedSchedule = results.Select(static item =>
            $"{item.Position}:{item.CaseId}:{item.Arm}");
        if (!observedSchedule.SequenceEqual(expectedSchedule, StringComparer.Ordinal))
            integrityErrors.Add("schedule_mismatch");
        if (calls.Any(static call => call.Error is not null))
            integrityErrors.Add("transport_error_present");
        if (calls.Any(static call =>
                call.PromptTokens is not > 0
                || call.CompletionTokens is not > 0
                || call.ElapsedMilliseconds < 0))
        {
            integrityErrors.Add("call_metrics_invalid");
        }
        if (results.Count(static run => run.Observed.ProtocolValid) != 8)
            integrityErrors.Add("protocol_valid_count=" + results.Count(static run =>
                run.Observed.ProtocolValid));
        if (calls.Sum(static call =>
                (call.PromptTokens ?? 0) + (call.CompletionTokens ?? 0)) > MaximumTotalTokens)
        {
            integrityErrors.Add("total_token_budget_exceeded");
        }
        if (elapsedMilliseconds > MaximumCampaignMilliseconds)
            integrityErrors.Add("campaign_time_budget_exceeded=" + elapsedMilliseconds);

        foreach (var canaryCase in cases)
        {
            var pair = results.Where(run => run.CaseId == canaryCase.Id).ToArray();
            if (pair.Length != 2)
            {
                integrityErrors.Add(canaryCase.Id + ":pair_count=" + pair.Length);
                continue;
            }
            var pairCalls = calls.Where(call => call.CaseId == canaryCase.Id).ToArray();
            if (pairCalls.Length != 2)
            {
                integrityErrors.Add(canaryCase.Id + ":pair_call_count=" + pairCalls.Length);
                continue;
            }
            if (pairCalls.Select(static call => call.UserSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
            {
                integrityErrors.Add(canaryCase.Id + ":user_hash_mismatch");
            }
            if (pairCalls.Select(static call => call.ContractSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
            {
                integrityErrors.Add(canaryCase.Id + ":contract_hash_mismatch");
            }
            if (pairCalls.Select(static call => call.PromptSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 2)
            {
                integrityErrors.Add(canaryCase.Id + ":prompt_hash_not_distinct");
            }
            var current = pairCalls.SingleOrDefault(static call => call.Arm == "current");
            var candidate = pairCalls.SingleOrDefault(static call => call.Arm == "candidate");
            if (current is null || candidate is null)
            {
                integrityErrors.Add(canaryCase.Id + ":arm_missing");
                continue;
            }
            try
            {
                if (SemanticResolutionV5PromptPolicyExperiment.RecoverCurrentSystem(
                        candidate.SystemMessage) != current.SystemMessage)
                {
                    integrityErrors.Add(canaryCase.Id + ":system_difference_not_three_clauses");
                }
                if (current.SystemMessage != SingleMessage(
                        captures[canaryCase.Id].Messages,
                        "system"))
                {
                    integrityErrors.Add(canaryCase.Id + ":current_not_runner_capture");
                }
            }
            catch (Exception exception)
            {
                integrityErrors.Add(canaryCase.Id + ":pair_transform:" + exception.Message);
            }
        }

        foreach (var run in results.Where(static item => item.Arm == "candidate"))
        {
            if (!run.OrderedDecisionAndIdsExact)
                candidateGateErrors.Add(run.CaseId + ":candidate_decision_or_ids");
            if (!run.ReasonFormatValid)
                candidateGateErrors.Add(run.CaseId + ":candidate_reason_format");
            if (!run.ReasonSemanticValid)
                candidateGateErrors.Add(run.CaseId + ":candidate_reason_semantic");
            if (run.IsSafetyCase && !run.SafetyValid)
                candidateGateErrors.Add(run.CaseId + ":candidate_safety");
        }
        return new CampaignEvaluation(
            integrityErrors.ToArray(),
            candidateGateErrors.ToArray());
    }

    private static CampaignOutcome BuildCampaignOutcome(
        IReadOnlyList<string> integrityErrors,
        IReadOnlyList<string> candidateGateErrors,
        int currentExact,
        int currentReasons,
        int candidateExact,
        int candidateReasons,
        int candidateSafety)
    {
        var isolatedIntegrityErrors = integrityErrors.ToArray();
        var isolatedCandidateGateErrors = candidateGateErrors.ToArray();
        var verdict = SemanticResolutionV5PromptPolicyExperiment.Classify(
            isolatedIntegrityErrors.Length == 0,
            currentExact,
            currentReasons,
            candidateExact,
            candidateReasons,
            candidateSafety);
        var failureReasons = isolatedIntegrityErrors
            .Concat(isolatedCandidateGateErrors)
            .ToList();
        if (verdict != "candidate_supported")
            failureReasons.Add("paired_verdict=" + verdict);
        return new CampaignOutcome(
            verdict,
            isolatedIntegrityErrors,
            isolatedCandidateGateErrors,
            failureReasons.ToArray());
    }

    private static RunResult EvaluateRun(
        ScheduledRun scheduled,
        RecordedCall call,
        ResolutionObservation observed)
    {
        var exact = observed.ProtocolValid
                    && string.Equals(
                        observed.Decision,
                        scheduled.Case.ExpectedDecision,
                        StringComparison.Ordinal)
                    && observed.EvidenceIds.SequenceEqual(
                        scheduled.Case.ExpectedEvidenceIds,
                        StringComparer.OrdinalIgnoreCase);
        var reasonSemanticValid = scheduled.Case.RequiredReasonPatterns.All(pattern =>
            Regex.IsMatch(
                observed.Reason,
                pattern,
                RegexOptions.CultureInvariant));
        var isSafetyCase = scheduled.Case.Id is "pair_rho" or "pair_sigma";
        var safetyValid = !isSafetyCase
                          || string.Equals(observed.Decision, "research", StringComparison.Ordinal);
        return new RunResult(
            scheduled.Case.Id,
            scheduled.Case.Domain,
            ArmName(scheduled.Arm),
            scheduled.Position,
            scheduled.Case.Question,
            scheduled.Case.ExpectedDecision,
            scheduled.Case.ExpectedEvidenceIds,
            observed,
            exact,
            SemanticResolutionV5PromptPolicyExperiment.IsCandidateReasonValid(
                observed.Reason),
            reasonSemanticValid,
            isSafetyCase,
            safetyValid,
            call.ElapsedMilliseconds);
    }

    private static RecordedCall BuildRecordedCall(
        ScheduledRun scheduled,
        IReadOnlyList<(string role, string content)> messages,
        PromptCapture capture,
        SourceBackedAgentCompletion completion,
        long elapsedMilliseconds,
        string? error)
    {
        var promptText = BuildRecordedPrompt(messages);
        var system = SingleMessage(messages, "system");
        var user = SingleMessage(messages, "user");
        var contractText = capture.Contract.Name + "\n" + capture.Contract.Schema.GetRawText();
        return new RecordedCall(
            scheduled.Case.Id,
            ArmName(scheduled.Arm),
            scheduled.Position,
            promptText.Length,
            promptText,
            HashText(promptText),
            system,
            HashText(system),
            user,
            HashText(user),
            capture.Contract.Name,
            capture.Contract.Schema.GetRawText(),
            HashText(contractText),
            ContractPropertyOrder(capture.Contract),
            ContractRequiredOrder(capture.Contract),
            capture.Temperature,
            capture.MaximumTokens,
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
            error,
            completion.ProtocolError);
    }

    private static ResolutionObservation ParseResolution(
        string? raw,
        IReadOnlyList<string> allowedEvidenceIds)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ResolutionObservation.Empty;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ResolutionObservation.Empty with { RawOutput = raw };
            var propertyOrder = root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            var propertySetValid = propertyOrder.OrderBy(static name => name)
                .SequenceEqual(
                    new[] { "decision", "evidenceIds", "reason" }
                        .OrderBy(static name => name),
                    StringComparer.Ordinal);
            var decisionValid = root.TryGetProperty("decision", out var decisionNode)
                                && decisionNode.ValueKind == JsonValueKind.String;
            var evidenceValid = root.TryGetProperty("evidenceIds", out var evidenceNode)
                                && evidenceNode.ValueKind == JsonValueKind.Array
                                && evidenceNode.EnumerateArray().All(static node =>
                                    node.ValueKind == JsonValueKind.String);
            var reasonValid = root.TryGetProperty("reason", out var reasonNode)
                              && reasonNode.ValueKind == JsonValueKind.String;
            if (!propertySetValid || !decisionValid || !evidenceValid || !reasonValid)
            {
                return ResolutionObservation.Empty with
                {
                    RawOutput = raw,
                    PropertyOrder = propertyOrder
                };
            }
            var decision = (decisionNode.GetString() ?? string.Empty)
                .Trim().ToLowerInvariant();
            var evidenceIds = evidenceNode.EnumerateArray()
                .Select(static node => (node.GetString() ?? string.Empty)
                    .Trim().ToUpperInvariant())
                .ToArray();
            var reason = (reasonNode.GetString() ?? string.Empty).Trim();
            var allowed = allowedEvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var idsValid = evidenceIds.Length <= allowed.Count
                           && evidenceIds.All(id => id.Length > 0 && allowed.Contains(id))
                           && evidenceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                           == evidenceIds.Length;
            var cardinalityValid = decision switch
            {
                "answer" => evidenceIds.Length > 0,
                "context" => evidenceIds.Length == 1,
                "research" or "clarify" => evidenceIds.Length == 0,
                _ => false
            };
            var protocolValid = idsValid
                                && cardinalityValid
                                && reason.Length is >= 8 and <= 480;
            return new ResolutionObservation(
                raw,
                decision,
                evidenceIds,
                reason,
                propertyOrder,
                protocolValid);
        }
        catch (JsonException)
        {
            return ResolutionObservation.Empty with { RawOutput = raw };
        }
    }

    private static IReadOnlyList<ScheduledRun> BuildSchedule(
        IReadOnlyList<PairedCase> cases)
    {
        var byId = cases.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        return new[]
        {
            new ScheduledRun(byId["pair_omicron"], SemanticResolutionV5PromptPolicyArm.Candidate, 1),
            new ScheduledRun(byId["pair_omicron"], SemanticResolutionV5PromptPolicyArm.Current, 2),
            new ScheduledRun(byId["pair_pi"], SemanticResolutionV5PromptPolicyArm.Current, 3),
            new ScheduledRun(byId["pair_pi"], SemanticResolutionV5PromptPolicyArm.Candidate, 4),
            new ScheduledRun(byId["pair_rho"], SemanticResolutionV5PromptPolicyArm.Candidate, 5),
            new ScheduledRun(byId["pair_rho"], SemanticResolutionV5PromptPolicyArm.Current, 6),
            new ScheduledRun(byId["pair_sigma"], SemanticResolutionV5PromptPolicyArm.Current, 7),
            new ScheduledRun(byId["pair_sigma"], SemanticResolutionV5PromptPolicyArm.Candidate, 8)
        };
    }

    private static IReadOnlyList<PairedCase> BuildCases()
        => new[]
        {
            new PairedCase(
                "pair_omicron",
                "particle_filtration",
                "Parmi les deux filtres documentés Ilex et Junon, lequel retient le plus de particules à un débit de 30 L/min ? Donne le nom et les valeurs des deux filtres.",
                "LIVRABLE: meilleur filtre dans le pool borne, avec son nom et les deux valeurs comparees\nPREUVES_ATOMIQUES: taux de retention a 30 L/min pour chaque filtre visible",
                "answer",
                new[] { "E2", "E1" },
                new[] { "(?i)junon", "(?<!\\d)97(?!\\d)", "(?i)ilex", "(?<!\\d)92(?!\\d)" },
                new[]
                {
                    "À un débit de 30 L/min, le filtre Ilex retient 92 % des particules mesurées.",
                    "À un débit de 30 L/min, le filtre Junon retient 97 % des particules mesurées."
                },
                new[]
                {
                    new SourceIdentity("doc-512", "Archive-512.pdf", "Synthetic/Set-E/Archive-512.pdf", "seg-512-a"),
                    new SourceIdentity("doc-583", "Archive-583.pdf", "Synthetic/Set-E/Archive-583.pdf", "seg-583-b")
                }),
            new PairedCase(
                "pair_pi",
                "inspection_procedure",
                "Quelles sont les quatre exemptions exactes de la procédure P-4 ?",
                "LIVRABLE: quatre exemptions exactes de la procedure P-4\nPREUVES_ATOMIQUES: contenu de la liste documentee",
                "context",
                new[] { "E1" },
                new[] { "(?i)volume 2", "(?i)section 8", "(?i)page 71" },
                new[]
                {
                    "L'index du dossier P-4 indique que la liste complète des quatre exemptions se trouve dans le volume 2, section 8, page 71. Cette entrée d'index ne contient pas les exemptions."
                },
                new[]
                {
                    new SourceIdentity("doc-604", "Bulletin-604.pdf", "Synthetic/Set-F/Bulletin-604.pdf", "seg-604-a")
                }),
            new PairedCase(
                "pair_rho",
                "submersible_coatings",
                "Le revêtement Silex est-il le seul revêtement au monde approuvé pour une immersion au-delà de 800 mètres ? Réponds oui ou non.",
                "LIVRABLE: conclusion mondiale exhaustive oui ou non\nPREUVES_ATOMIQUES: approbation et couverture explicite du registre",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Dans le registre côtier 2026, le revêtement Silex est approuvé pour une immersion jusqu'à 900 mètres.",
                    "Le registre côtier couvre uniquement les installations du secteur Nord et ne recense pas les produits des autres secteurs ou pays."
                },
                new[]
                {
                    new SourceIdentity("doc-702", "Releve-702.pdf", "Synthetic/Set-G/Releve-702.pdf", "seg-702-a"),
                    new SourceIdentity("doc-731", "Releve-731.pdf", "Synthetic/Set-G/Releve-731.pdf", "seg-731-b")
                }),
            new PairedCase(
                "pair_sigma",
                "conveyor_control",
                "Quel délai exact, en secondes, sépare la détection du blocage de la réinitialisation automatique ?",
                "LIVRABLE: delai exact en secondes entre detection et reinitialisation automatique\nPREUVES_ATOMIQUES: valeur explicite du delai",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Le capteur de blocage déclenche un voyant orange lorsque le convoyeur s'arrête.",
                    "Le bouton de réinitialisation manuelle doit être contrôlé chaque mois."
                },
                new[]
                {
                    new SourceIdentity("doc-808", "Memo-808.pdf", "Synthetic/Set-H/Memo-808.pdf", "seg-808-a"),
                    new SourceIdentity("doc-829", "Memo-829.pdf", "Synthetic/Set-H/Memo-829.pdf", "seg-829-b")
                })
        };

    private static SourceBackedIntake BuildIntake(PairedCase canaryCase)
    {
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
            Language: "fr")
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "a539-" + canaryCase.Id,
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
            docId = canaryCase.SourceIdentities[index].DocId,
            docName = canaryCase.SourceIdentities[index].DocName,
            docPath = canaryCase.SourceIdentities[index].DocPath,
            revisionId = "a539-v1",
            sourceHash = new string((char)('a' + index), 64),
            pageStart = index + 1,
            pageEnd = index + 1,
            chunkId = canaryCase.SourceIdentities[index].ChunkId,
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

    private static string SingleMessage(
        IReadOnlyList<(string role, string content)> messages,
        string role)
        => messages.Single(message => string.Equals(
            message.role,
            role,
            StringComparison.Ordinal)).content;

    private static IReadOnlyList<string> ContractPropertyOrder(
        LlmStructuredOutputContract contract)
        => contract.Schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

    private static IReadOnlyList<string> ContractRequiredOrder(
        LlmStructuredOutputContract contract)
        => contract.Schema.GetProperty("required")
            .EnumerateArray()
            .Select(static node => node.GetString() ?? string.Empty)
            .ToArray();

    private static IReadOnlyList<string> ContractAllowedEvidenceIds(
        LlmStructuredOutputContract contract)
        => contract.Schema.GetProperty("properties")
            .GetProperty("evidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static node => node.GetString() ?? string.Empty)
            .ToArray();

    private static string BuildRecordedPrompt(
        IReadOnlyList<(string role, string content)> messages)
        => string.Join("\n\n", messages.Select(static message =>
            message.role + "\n" + message.content));

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ArmName(SemanticResolutionV5PromptPolicyArm arm)
        => arm.ToString().ToLowerInvariant();

    private static string NormalizeLlmBaseUrl(string value)
        => value.Trim().TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value.Trim().TrimEnd('/')[..^3]
            : value.Trim().TrimEnd('/');

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

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

    private static void AssertFrozenProductFiles(
        string repo,
        ICollection<string> errors)
    {
        var runtime = Path.Combine(
            repo,
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag",
            "SourceBackedAgentSemanticResolutionWriterReview.cs");
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtime)));
        if (actual != RuntimeSha256)
            errors.Add("runtime_sha256=" + actual);
        var protocol = Path.Combine(
            repo,
            "artifacts",
            "goal-rag-product-20260827-1041",
            "phase5",
            "PROTOCOLE-A539-LIVE-APPARIE-PROMPT-POLITIQUE-V5.md");
        var protocolActual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(protocol)));
        if (protocolActual != ProtocolSha256)
            errors.Add("protocol_sha256=" + protocolActual);
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
                    $"A539 expected one internal rag.search; observed {CallCount}:{toolName}.");
            }
            return Task.FromResult(evidence);
        }
    }

    private sealed class PromptCaptureLlmAdapter(string caseId)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient,
          ISourceBackedAgentInputTokenCounter,
          ISourceBackedAgentRuntimeContextProvider
    {
        public PromptCapture? Capture { get; private set; }
        public int ResolutionCallCount { get; private set; }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
            => throw new InvalidOperationException(
                "A539 prompt capture must not execute native writer or reviewer calls.");

        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            if (contract.Name != ResolutionContract || Capture is not null)
            {
                throw new InvalidOperationException(
                    "A539 prompt capture observed an unexpected structured call.");
            }
            ResolutionCallCount++;
            var visible = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(
                messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray());
            Capture = new PromptCapture(
                caseId,
                visible,
                contract,
                temperatureOverride ?? 0,
                maxTokens,
                0,
                0);
            var raw = JsonSerializer.Serialize(new
            {
                decision = "research",
                evidenceIds = Array.Empty<string>(),
                leadEvidenceIds = Array.Empty<string>(),
                reason = "La capture s'arrête avant toute décision live."
            });
            return Task.FromResult(new SourceBackedAgentCompletion(
                raw,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"));
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => Task.FromResult<int?>(100);

        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct)
            => Task.FromResult<int?>(4096);
    }

    private sealed record PairedCase(
        string Id,
        string Domain,
        string Question,
        string SemanticPlan,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> RequiredReasonPatterns,
        IReadOnlyList<string> Excerpts,
        IReadOnlyList<SourceIdentity> SourceIdentities);

    private sealed record SourceIdentity(
        string DocId,
        string DocName,
        string DocPath,
        string ChunkId)
    {
        public IReadOnlyList<string> Values { get; } =
            new[] { DocId, DocName, DocPath, ChunkId };
    }

    private sealed record ScheduledRun(
        PairedCase Case,
        SemanticResolutionV5PromptPolicyArm Arm,
        int Position);

    private sealed record CampaignEvaluation(
        IReadOnlyList<string> IntegrityErrors,
        IReadOnlyList<string> CandidateGateErrors);

    private sealed record CampaignOutcome(
        string PairedVerdict,
        IReadOnlyList<string> IntegrityErrors,
        IReadOnlyList<string> CandidateGateErrors,
        IReadOnlyList<string> FailureReasons);

    private sealed record PromptCapture(
        string CaseId,
        [property: JsonIgnore]
        IReadOnlyList<(string role, string content)> Messages,
        LlmStructuredOutputContract Contract,
        double Temperature,
        int MaximumTokens,
        int ToolCallCount,
        int ResolutionCallCount)
    {
        public IReadOnlyList<RecordedMessage> RecordedMessages { get; } = Messages
            .Select(static message => new RecordedMessage(message.role, message.content))
            .ToArray();
        public string PromptSha256 { get; } = HashText(BuildRecordedPrompt(Messages));
        public string ContractSha256 { get; } = HashText(
            Contract.Name + "\n" + Contract.Schema.GetRawText());
    }

    private sealed record RecordedMessage(string Role, string Content);

    private sealed record ResolutionObservation(
        string RawOutput,
        string Decision,
        IReadOnlyList<string> EvidenceIds,
        string Reason,
        IReadOnlyList<string> PropertyOrder,
        bool ProtocolValid)
    {
        public static ResolutionObservation Empty { get; } = new(
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            Array.Empty<string>(),
            false);
    }

    private sealed record RunResult(
        string CaseId,
        string Domain,
        string Arm,
        int Position,
        string Question,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        ResolutionObservation Observed,
        bool OrderedDecisionAndIdsExact,
        bool ReasonFormatValid,
        bool ReasonSemanticValid,
        bool IsSafetyCase,
        bool SafetyValid,
        long ElapsedMilliseconds);

    private sealed record RecordedCall(
        string CaseId,
        string Arm,
        int Position,
        int PromptCharacters,
        string PromptText,
        string PromptSha256,
        string SystemMessage,
        string SystemSha256,
        string UserMessage,
        string UserSha256,
        string ContractName,
        string ContractSchema,
        string ContractSha256,
        IReadOnlyList<string> ContractPropertyOrder,
        IReadOnlyList<string> ContractRequiredOrder,
        double Temperature,
        int MaximumTokens,
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
        string? Error,
        string? ProtocolError);
}
