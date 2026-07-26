using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationFinalValidationTests
{
    [Fact]
    public void SelectFinalists_keeps_top_profiles_and_adds_runtime_topology_diversity()
    {
        var cuda4 = Candidate("cuda-t4", "llama.cpp-cuda", "CUDA0", threads: 4);
        var cuda6 = Candidate("cuda-t6", "llama.cpp-cuda", "CUDA0", threads: 6);
        var vulkan = Candidate("vulkan-t6", "llama.cpp-vulkan", "Vulkan1", threads: 6);
        var cpu = Candidate("cpu", "llama.cpp-cpu", "none", threads: 6);
        var refinement = Refinement(
            new[]
            {
                ("topology-cuda", cuda4, 10d),
                ("topology-cuda", cuda6, 11d),
                ("topology-vulkan", vulkan, 13d),
                ("topology-cpu", cpu, 40d)
            });

        var finalists = LocalLlmQualificationFinalValidation.SelectFinalists(
            refinement,
            overallFinalistCount: 2,
            maxFinalists: 3);

        Assert.Equal(
            new[] { "cuda-t4", "cuda-t6", "vulkan-t6" },
            finalists.Select(static item => item.CandidateId));
    }

    [Fact]
    public void BuildSchedule_rotates_first_position_between_rounds()
    {
        var schedule = LocalLlmQualificationFinalValidation.BuildSchedule(
            finalistCount: 3,
            roundCount: 3);

        Assert.Equal(
            new[] { 0, 1, 2, 1, 2, 0, 2, 0, 1 },
            schedule.Select(static item => item.CandidateIndex));
        Assert.Equal(Enumerable.Range(0, 9), schedule.Select(static item => item.SequenceIndex));
    }

    [Fact]
    public async Task RunAsync_rejects_a_semantic_contract_failure_and_ranks_complete_finalists()
    {
        var first = Candidate("first", "llama.cpp-cuda", "CUDA0", threads: 4);
        var second = Candidate("second", "llama.cpp-vulkan", "Vulkan1", threads: 6);
        var refinement = Refinement(
            new[]
            {
                ("topology-cuda", first, 10d),
                ("topology-vulkan", second, 12d)
            });
        var order = new List<string>();

        var outcome = await LocalLlmQualificationFinalValidation.RunAsync(
            refinement,
            modelPath: @"C:\models\model.gguf",
            modelId: "model",
            new LocalLlmQualificationFinalValidationOptions(
                OverallFinalistCount: 1,
                MaxFinalists: 2,
                RoundCount: 3,
                RoundTimeout: TimeSpan.FromSeconds(10)),
            roundRunner: (candidate, round, sequence, _) =>
            {
                order.Add(candidate.CandidateId);
                var structuredPass = candidate.CandidateId != "first" || round != 1;
                return Task.FromResult(Round(
                    candidate,
                    round,
                    sequence,
                    structuredPass,
                    structuredDurationMs: candidate.CandidateId == "first" ? 100 : 200));
            });

        Assert.Equal(
            new[] { "first", "second", "second", "first", "first", "second" },
            order);
        Assert.Equal("second", outcome.RankedCandidates[0].Candidate.CandidateId);
        Assert.True(outcome.RankedCandidates[0].Eligible);
        var rejected = Assert.Single(
            outcome.RankedCandidates,
            static score => score.Candidate.CandidateId == "first");
        Assert.False(rejected.Eligible);
        Assert.Contains("structured_quality_contract_failed", rejected.Reasons);
    }

    [Fact]
    public void Structured_probe_parser_preserves_exact_decision_properties()
    {
        var parsed = LocalLlmStructuredQualificationProbe.ParseDecisions(
            """
            {
              "V1": "named_item_suitable_for_slot",
              "V2": "instruction_or_action",
              "V3": "heading_or_broad_category",
              "V4": "isolated_component_or_ingredient"
            }
            """);

        Assert.Equal("named_item_suitable_for_slot", parsed["V1"]);
        Assert.Equal("isolated_component_or_ingredient", parsed["V4"]);
        Assert.Throws<JsonException>(() =>
            LocalLlmStructuredQualificationProbe.ParseDecisions("""["not", "an", "object"]"""));
    }

    private static LocalLlmQualificationRefinementOutcome Refinement(
        IReadOnlyList<(string Topology, LocalLlmQualificationCandidate Candidate, double Score)> candidates)
    {
        var entries = candidates.Select(item => new LocalLlmQualificationRefinementEntry(
            item.Topology,
            item.Candidate,
            Fit(item.Candidate),
            new LocalLlmFitAssessment(LocalLlmFitAssessmentStatus.Fits, new[] { "fits" }),
            new[]
            {
                Measurement(item.Candidate, "control", 100, 10)
            })).ToArray();
        var ranked = candidates.Select(item => new LocalLlmQualificationCandidateScore(
            item.Candidate,
            Eligible: true,
            ProjectedWorkloadSeconds: item.Score,
            Reasons: new[] { "pass" },
            Measurements: new[]
            {
                Measurement(item.Candidate, "control", 100, 10)
            })).ToArray();
        var queues = candidates
            .GroupBy(static item => item.Topology)
            .Select(group => new LocalLlmQualificationRefinementQueue(
                group.Key,
                group.Select(static item => item.Candidate).ToArray()))
            .ToArray();
        return new LocalLlmQualificationRefinementOutcome(
            queues.Select(static queue => queue.TopologyId).ToArray(),
            queues,
            entries,
            ranked);
    }

    private static LocalLlmQualificationCandidate Candidate(
        string id,
        string runtime,
        string device,
        int threads)
        => new(
            id,
            "finalist",
            $@"C:\runtime\{runtime}\llama-server.exe",
            WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = id,
                Runtime = runtime,
                DeviceIds = new[] { device },
                Threads = threads,
                ThreadsBatch = threads
            },
            Array.Empty<string>());

    private static LocalLlmQualificationFinalRound Round(
        LocalLlmQualificationCandidate candidate,
        int round,
        int sequence,
        bool structuredPass,
        int structuredDurationMs)
        => new(
            candidate.CandidateId,
            round,
            sequence,
            Port: 18000 + sequence,
            StartSucceeded: true,
            StartupLoadMs: 1000,
            new WarmupMeasurement(
                LoadMs: 1000,
                TtftMs: 500,
                TokPerSec: 10,
                Succeeded: true),
            new LocalLlmStructuredQualificationProbeResult(
                structuredPass,
                structuredDurationMs,
                new Dictionary<string, string>(),
                structuredPass ? new[] { "pass" } : new[] { "semantic_mismatch" },
                "{}"),
            Diagnostic: string.Empty);

    private static LocalLlmFitParamsProbeResult Fit(LocalLlmQualificationCandidate candidate)
        => new(
            candidate.CandidateId,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 1,
            new[] { new LocalLlmFitMemoryEstimate("Host", 100, 10, 10) },
            Diagnostic: string.Empty);

    private static LocalLlmQualificationBenchmarkResult Measurement(
        LocalLlmQualificationCandidate candidate,
        string scenario,
        double prompt,
        double generation)
        => new(
            candidate.CandidateId,
            scenario,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 1,
            PromptTokensPerSecond: prompt,
            GenerationTokensPerSecond: generation,
            Diagnostic: string.Empty);
}
