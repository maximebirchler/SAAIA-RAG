using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmStructuredQualificationProbeResult(
    bool Succeeded,
    int DurationMs,
    IReadOnlyDictionary<string, string> Decisions,
    IReadOnlyList<string> Reasons,
    string Response);

internal sealed record LocalLlmQualificationFinalRound(
    string CandidateId,
    int RoundIndex,
    int SequenceIndex,
    int Port,
    bool StartSucceeded,
    int? StartupLoadMs,
    WarmupMeasurement Warmup,
    LocalLlmStructuredQualificationProbeResult StructuredProbe,
    string Diagnostic);

internal sealed record LocalLlmQualificationFinalCandidateScore(
    LocalLlmQualificationCandidate Candidate,
    bool Eligible,
    double? StageTwoProjectedSeconds,
    double? MedianStructuredProbeMs,
    double? MedianStartupLoadMs,
    int? WorstTtftMs,
    double? MinimumTokensPerSecond,
    double? FinalScoreSeconds,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<LocalLlmQualificationFinalRound> Rounds);

internal sealed record LocalLlmQualificationFinalValidationOptions(
    int OverallFinalistCount,
    int MaxFinalists,
    int RoundCount,
    TimeSpan RoundTimeout)
{
    public static LocalLlmQualificationFinalValidationOptions Default => new(
        OverallFinalistCount: 2,
        MaxFinalists: 4,
        RoundCount: 3,
        RoundTimeout: TimeSpan.FromMinutes(5));
}

internal sealed record LocalLlmQualificationFinalValidationOutcome(
    IReadOnlyList<LocalLlmQualificationCandidate> Finalists,
    IReadOnlyList<LocalLlmQualificationFinalRound> Rounds,
    IReadOnlyList<LocalLlmQualificationFinalCandidateScore> RankedCandidates);

internal static class LocalLlmQualificationFinalValidation
{
    public static IReadOnlyList<LocalLlmQualificationCandidate> SelectFinalists(
        LocalLlmQualificationRefinementOutcome refinement,
        int overallFinalistCount,
        int maxFinalists)
    {
        ArgumentNullException.ThrowIfNull(refinement);
        var eligible = refinement.RankedCandidates
            .Where(static score => score.Eligible)
            .OrderBy(static score => score.ProjectedWorkloadSeconds ?? double.MaxValue)
            .ThenBy(static score => score.Candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length == 0)
            return Array.Empty<LocalLlmQualificationCandidate>();

        var overallLimit = Math.Clamp(overallFinalistCount, 1, 8);
        var finalistLimit = Math.Clamp(maxFinalists, overallLimit, 12);
        var selected = eligible
            .Take(overallLimit)
            .Select(static score => score.Candidate)
            .ToList();
        var topologyByCandidate = refinement.Entries
            .GroupBy(static entry => entry.Candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last().TopologyId,
                StringComparer.OrdinalIgnoreCase);
        var selectedTopologies = selected
            .Select(candidate => topologyByCandidate.GetValueOrDefault(candidate.CandidateId))
            .Where(static topology => !string.IsNullOrWhiteSpace(topology))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedExecutionShapes = selected
            .Select(candidate => BuildExecutionShape(
                topologyByCandidate.GetValueOrDefault(
                    candidate.CandidateId),
                candidate.Profile))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A multi-slot profile changes prompt-cache persistence even when its
        // raw token throughput and device topology match a single-slot
        // profile. Keep one measured execution-shape alternative in the final
        // live rounds instead of deciding from llama-bench alone.
        foreach (var score in eligible)
        {
            if (selected.Count >= finalistLimit)
                break;
            if (selected.Any(candidate => string.Equals(
                    candidate.CandidateId,
                    score.Candidate.CandidateId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var topology = topologyByCandidate.GetValueOrDefault(
                score.Candidate.CandidateId);
            var executionShape = BuildExecutionShape(
                topology,
                score.Candidate.Profile);
            if (selectedExecutionShapes.Contains(executionShape))
                continue;

            selected.Add(score.Candidate);
            selectedExecutionShapes.Add(executionShape);
            if (!string.IsNullOrWhiteSpace(topology))
                selectedTopologies.Add(topology);
        }

        foreach (var score in eligible)
        {
            if (selected.Count >= finalistLimit)
                break;
            if (selected.Any(candidate => string.Equals(
                    candidate.CandidateId,
                    score.Candidate.CandidateId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var topology = topologyByCandidate.GetValueOrDefault(score.Candidate.CandidateId);
            if (string.IsNullOrWhiteSpace(topology) || selectedTopologies.Contains(topology))
                continue;

            selected.Add(score.Candidate);
            selectedTopologies.Add(topology);
            selectedExecutionShapes.Add(BuildExecutionShape(
                topology,
                score.Candidate.Profile));
        }

        foreach (var score in eligible)
        {
            if (selected.Count >= finalistLimit)
                break;
            if (selected.Any(candidate => string.Equals(
                    candidate.CandidateId,
                    score.Candidate.CandidateId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            selected.Add(score.Candidate);
        }

        return selected;
    }

    private static string BuildExecutionShape(
        string? topology,
        QualifiedProfile profile)
        => (topology ?? string.Empty)
           + "|parallel="
           + Math.Clamp(profile.Parallel, 1, 16)
           + "|ctx-per-slot="
           + profile.ResolvePerSlotContextSize();

    internal static IReadOnlyList<(int RoundIndex, int CandidateIndex, int SequenceIndex)> BuildSchedule(
        int finalistCount,
        int roundCount)
    {
        var candidateCount = Math.Clamp(finalistCount, 0, 12);
        var rounds = Math.Clamp(roundCount, 0, 10);
        var schedule = new List<(int RoundIndex, int CandidateIndex, int SequenceIndex)>(
            candidateCount * rounds);
        for (var round = 0; round < rounds; round++)
        {
            for (var offset = 0; offset < candidateCount; offset++)
            {
                schedule.Add((
                    round,
                    (round + offset) % candidateCount,
                    schedule.Count));
            }
        }

        return schedule;
    }

    public static async Task<LocalLlmQualificationFinalValidationOutcome> RunAsync(
        LocalLlmQualificationRefinementOutcome refinement,
        string modelPath,
        string modelId,
        LocalLlmQualificationFinalValidationOptions? options = null,
        Func<LocalLlmQualificationCandidate, int, int, CancellationToken, Task<LocalLlmQualificationFinalRound>>? roundRunner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refinement);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        options ??= LocalLlmQualificationFinalValidationOptions.Default;

        var finalists = SelectFinalists(
            refinement,
            options.OverallFinalistCount,
            options.MaxFinalists);
        var schedule = BuildSchedule(finalists.Count, options.RoundCount);
        roundRunner ??= (candidate, roundIndex, sequenceIndex, token) => RunRoundAsync(
            candidate,
            modelPath,
            modelId,
            roundIndex,
            sequenceIndex,
            options.RoundTimeout,
            token);

        var rounds = new List<LocalLlmQualificationFinalRound>(schedule.Count);
        foreach (var item in schedule)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = finalists[item.CandidateIndex];
            rounds.Add(await roundRunner(
                    candidate,
                    item.RoundIndex,
                    item.SequenceIndex,
                    ct)
                .ConfigureAwait(false));
        }

        var scores = BuildScores(refinement, finalists, rounds, options.RoundCount);
        return new LocalLlmQualificationFinalValidationOutcome(finalists, rounds, scores);
    }

    internal static IReadOnlyList<LocalLlmQualificationFinalCandidateScore> BuildScores(
        LocalLlmQualificationRefinementOutcome refinement,
        IReadOnlyList<LocalLlmQualificationCandidate> finalists,
        IReadOnlyList<LocalLlmQualificationFinalRound> rounds,
        int requiredRoundCount)
    {
        var scores = new List<LocalLlmQualificationFinalCandidateScore>(finalists.Count);
        foreach (var candidate in finalists)
        {
            var candidateRounds = rounds
                .Where(round => string.Equals(
                    round.CandidateId,
                    candidate.CandidateId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static round => round.RoundIndex)
                .ToArray();
            var reasons = new List<string>();
            if (candidateRounds.Length < Math.Clamp(requiredRoundCount, 1, 10))
                reasons.Add("final_rounds_missing");
            if (candidateRounds.Any(static round => !round.StartSucceeded))
                reasons.Add("server_start_failed");
            if (candidateRounds.Any(static round => !round.Warmup.Succeeded))
                reasons.Add("warmup_contract_failed");
            if (candidateRounds.Any(static round => !round.StructuredProbe.Succeeded))
                reasons.Add("structured_quality_contract_failed");

            var stageTwo = refinement.RankedCandidates.FirstOrDefault(score => string.Equals(
                score.Candidate.CandidateId,
                candidate.CandidateId,
                StringComparison.OrdinalIgnoreCase));
            if (stageTwo?.ProjectedWorkloadSeconds is null)
                reasons.Add("stage_two_score_missing");

            var structuredDurations = candidateRounds
                .Where(static round => round.StructuredProbe.Succeeded)
                .Select(static round => (double)round.StructuredProbe.DurationMs)
                .ToArray();
            var startupLoads = candidateRounds
                .Where(static round => round.StartupLoadMs is > 0)
                .Select(static round => (double)round.StartupLoadMs!.Value)
                .ToArray();
            var warmups = candidateRounds
                .Where(static round => round.Warmup.Succeeded)
                .Select(static round => round.Warmup)
                .ToArray();
            var medianStructuredMs = Median(structuredDurations);
            var medianStartupMs = Median(startupLoads);
            var eligible = reasons.Count == 0;
            var finalScore = eligible
                ? stageTwo!.ProjectedWorkloadSeconds!.Value
                  + (medianStructuredMs ?? double.MaxValue) / 1000d
                : (double?)null;
            scores.Add(new LocalLlmQualificationFinalCandidateScore(
                candidate,
                eligible,
                stageTwo?.ProjectedWorkloadSeconds,
                medianStructuredMs,
                medianStartupMs,
                warmups.Length == 0 ? null : warmups.Max(static run => run.TtftMs),
                warmups.Length == 0 ? null : warmups.Min(static run => run.TokPerSec),
                finalScore,
                reasons.Count == 0
                    ? new[] { "all_final_server_and_structured_rounds_passed" }
                    : reasons.Distinct(StringComparer.Ordinal).ToArray(),
                candidateRounds));
        }

        return scores
            .OrderByDescending(static score => score.Eligible)
            .ThenBy(static score => score.FinalScoreSeconds ?? double.MaxValue)
            .ThenBy(static score => score.Candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<LocalLlmQualificationFinalRound> RunRoundAsync(
        LocalLlmQualificationCandidate candidate,
        string modelPath,
        string modelId,
        int roundIndex,
        int sequenceIndex,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var port = ReserveLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var settings = new AppSettings
        {
            UseLocalLlm = true,
            ManageLocalLlmProcess = true,
            LlamaExePath = candidate.RuntimeExecutablePath,
            ModelPath = Path.GetFullPath(modelPath),
            Host = "127.0.0.1",
            Port = port,
            ModelId = modelId,
            ExtraArgs = "--metrics",
            QualifiedProfile = candidate.Profile,
            StartupTimeoutSeconds = 120
        };
        var manager = new LlamaCppProcessManager();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        try
        {
            var start = await manager.StartAsync(
                    candidate.RuntimeExecutablePath,
                    LlamaCppProcessManager.BuildArgs(settings),
                    token)
                .ConfigureAwait(false);
            if (!start.ok)
            {
                return FailureRound(
                    candidate,
                    roundIndex,
                    sequenceIndex,
                    port,
                    manager.LastStartupLoadMs,
                    start.message);
            }

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var harness = new LocalLlmWarmupHarness(http);
            var scenarioRuns = await harness.RunContractScenariosAsync(
                    baseUrl,
                    modelId,
                    ct: token)
                .ConfigureAwait(false);
            var warmup = ApplyObservedLoad(
                LocalLlmWarmupHarness.AggregateScenarioMeasurements(scenarioRuns),
                manager.LastStartupLoadMs);
            var structuredProbe = await LocalLlmStructuredQualificationProbe.RunAsync(
                    http,
                    baseUrl,
                    modelId,
                    token)
                .ConfigureAwait(false);
            return new LocalLlmQualificationFinalRound(
                candidate.CandidateId,
                roundIndex,
                sequenceIndex,
                port,
                StartSucceeded: true,
                manager.LastStartupLoadMs,
                warmup,
                structuredProbe,
                string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FailureRound(
                candidate,
                roundIndex,
                sequenceIndex,
                port,
                manager.LastStartupLoadMs,
                "final_round_timeout");
        }
        catch (Exception ex)
        {
            return FailureRound(
                candidate,
                roundIndex,
                sequenceIndex,
                port,
                manager.LastStartupLoadMs,
                ex.GetType().Name);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static WarmupMeasurement ApplyObservedLoad(
        WarmupMeasurement measurement,
        int? startupLoadMs)
    {
        if (startupLoadMs is null or <= 0)
            return measurement;

        var metrics = measurement.RuntimeMetrics is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(
                measurement.RuntimeMetrics,
                StringComparer.OrdinalIgnoreCase);
        metrics["runtime.observed_start_load_ms"] = startupLoadMs.Value;
        return measurement with
        {
            LoadMs = Math.Max(measurement.LoadMs, startupLoadMs.Value),
            RuntimeMetrics = metrics
        };
    }

    private static LocalLlmQualificationFinalRound FailureRound(
        LocalLlmQualificationCandidate candidate,
        int roundIndex,
        int sequenceIndex,
        int port,
        int? startupLoadMs,
        string diagnostic)
        => new(
            candidate.CandidateId,
            roundIndex,
            sequenceIndex,
            port,
            StartSucceeded: false,
            startupLoadMs,
            new WarmupMeasurement(
                startupLoadMs ?? 0,
                0,
                0,
                Succeeded: false,
                Error: diagnostic,
                Scenario: "contract_suite"),
            new LocalLlmStructuredQualificationProbeResult(
                Succeeded: false,
                DurationMs: 0,
                Decisions: new Dictionary<string, string>(),
                Reasons: new[] { diagnostic },
                Response: string.Empty),
            diagnostic);

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;
        var ordered = values.OrderBy(static value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}

internal static class LocalLlmStructuredQualificationProbe
{
    private static readonly IReadOnlyDictionary<string, string> Expected =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V1"] = "named_item_suitable_for_slot",
            ["V2"] = "instruction_or_action",
            ["V3"] = "heading_or_broad_category",
            ["V4"] = "isolated_component_or_ingredient"
        };

    public static async Task<LocalLlmStructuredQualificationProbeResult> RunAsync(
        HttpClient http,
        string baseUrl,
        string modelId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var candidates = BuildCandidates();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = new OpenAiCompatLlmClient(http, baseUrl, modelId);
            var response = await client.CompleteStructuredAsync(
                    SourceBackedStructuredValueTypeDecisionBatchPrompt.BuildMessages(
                        BuildIntake(),
                        BuildEvidenceBundle(),
                        candidates),
                    SourceBackedStructuredValueTypeDecisionBatchPrompt.BuildContract(candidates),
                    ct)
                .ConfigureAwait(false);
            stopwatch.Stop();
            var decisions = ParseDecisions(response);
            var reasons = new List<string>();
            foreach (var expected in Expected)
            {
                if (!decisions.TryGetValue(expected.Key, out var actual))
                    reasons.Add("decision_missing:" + expected.Key);
                else if (!string.Equals(actual, expected.Value, StringComparison.Ordinal))
                    reasons.Add($"decision_mismatch:{expected.Key}:{actual}!={expected.Value}");
            }

            foreach (var unexpected in decisions.Keys.Where(key => !Expected.ContainsKey(key)))
                reasons.Add("decision_unexpected:" + unexpected);
            return new LocalLlmStructuredQualificationProbeResult(
                reasons.Count == 0,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                decisions,
                reasons.Count == 0 ? new[] { "structured_contract_and_semantics_passed" } : reasons,
                Trim(response, 1200));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new LocalLlmStructuredQualificationProbeResult(
                Succeeded: false,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                new Dictionary<string, string>(),
                new[] { "structured_probe_failed:" + ex.GetType().Name },
                string.Empty);
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseDecisions(string response)
    {
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Structured qualification response must be an object.");

        var decisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException($"Decision '{property.Name}' must be a string.");
            decisions[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return decisions;
    }

    private static SourceBackedIntake BuildIntake()
        => new(
            UserQuestion: "Classify four immutable candidate values for a source-backed structured plan.",
            TaskKind: "structured_planning",
            ExplicitConstraints: Array.Empty<string>(),
            RequestedAxes: new[] { "requested field", "value" },
            AllowsPartialAnswer: false,
            Language: "en");

    private static IReadOnlyList<SourceBackedStructuredValueTypeCandidate> BuildCandidates()
        => new[]
        {
            new SourceBackedStructuredValueTypeCandidate(
                "V1",
                "meal",
                "roasted vegetable soup",
                new[] { "E1" },
                new[] { "C1" }),
            new SourceBackedStructuredValueTypeCandidate(
                "V2",
                "meal",
                "stir until smooth",
                new[] { "E2" },
                new[] { "C2" }),
            new SourceBackedStructuredValueTypeCandidate(
                "V3",
                "product",
                "industrial components",
                new[] { "E3" },
                new[] { "C3" }),
            new SourceBackedStructuredValueTypeCandidate(
                "V4",
                "complete meal",
                "2 tablespoons flour",
                new[] { "E4" },
                new[] { "C4" })
        };

    private static EvidenceBundle BuildEvidenceBundle()
        => new(
            "hardware-qualification-bundle",
            "Classify four immutable candidate values for a source-backed structured plan.",
            new[]
            {
                Evidence("E1", "Roasted vegetable soup. Ingredients: vegetables and stock. Preparation: roast, blend and serve."),
                Evidence("E2", "For the complete sauce recipe, add the liquid and stir until smooth before serving."),
                Evidence("E3", "Industrial components is the broad catalogue category containing several unrelated product families."),
                Evidence("E4", "The complete meal is vegetable stew. Its ingredients include 2 tablespoons flour, vegetable stock and onions.")
            },
            Array.Empty<SourceBackedTraceEvent>());

    private static EvidenceItem Evidence(string id, string excerpt)
        => new(
            id,
            SourceKind: "qualification_fixture",
            ToolName: "qualification.probe",
            QueryUsed: "structured value type",
            DocId: "qualification-" + id,
            DocName: "qualification-fixture.txt",
            DocPath: "qualification-fixture.txt",
            SourceHash: "qualification",
            RevisionId: "1",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: id,
            Excerpt: excerpt,
            NormalizedExcerpt: excerpt,
            Score: 1,
            Rank: 1,
            CategoryPath: "qualification",
            DocLanguage: "en",
            ProfileLanguage: "en",
            ExtractionQuality: "native_text",
            MatchedContentCards: null,
            SelectionHints: new Dictionary<string, string>(),
            CodeHints: new Dictionary<string, string>(),
            RiskFlags: Array.Empty<string>(),
            Lineage: new[] { "qualification_fixture" });

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
