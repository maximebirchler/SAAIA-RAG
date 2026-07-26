using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationBenchmarkTests
{
    [Fact]
    public void BuildStartInfo_materializes_the_exact_qualified_profile_without_shell_fragments()
    {
        var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
        {
            DeviceIds = new[] { "Vulkan0", "Vulkan1" },
            SplitMode = "row",
            TensorSplit = new[] { 1d, 4d },
            MainGpu = 1,
            CacheTypeK = "q8_0",
            CacheTypeV = "q4_0"
        };
        var scenario = new LocalLlmQualificationBenchmarkScenario("rag-control", 768, 64);

        var startInfo = LocalLlmQualificationBenchmarkRunner.BuildStartInfo(
            @"C:\runtime\llama-bench.exe",
            @"C:\models\model.gguf",
            profile,
            scenario,
            repetitions: 3);
        var arguments = startInfo.ArgumentList.ToArray();

        AssertPair(arguments, "-p", "768");
        AssertPair(arguments, "-n", "64");
        AssertPair(arguments, "-r", "3");
        AssertPair(arguments, "-b", "512");
        AssertPair(arguments, "-ub", "128");
        AssertPair(arguments, "-ngl", "37");
        AssertPair(arguments, "-fa", "on");
        AssertPair(arguments, "-dev", "Vulkan0/Vulkan1");
        AssertPair(arguments, "-sm", "row");
        AssertPair(arguments, "-mg", "1");
        AssertPair(arguments, "-ts", "1/4");
        AssertPair(arguments, "-ctk", "q8_0");
        AssertPair(arguments, "-ctv", "q4_0");
        AssertPair(arguments, "-o", "json");
        Assert.Contains("--no-warmup", arguments);
        Assert.DoesNotContain(arguments, static argument => argument.Contains(';', StringComparison.Ordinal));
    }

    [Fact]
    public void ParseMeasurements_reads_prompt_and_generation_throughput_from_llama_bench_json()
    {
        var scenario = new LocalLlmQualificationBenchmarkScenario("rag-control", 512, 64);
        var json = JsonSerializer.Serialize(new object[]
        {
            new { n_prompt = 512, n_gen = 0, avg_ts = 208.338954 },
            new { n_prompt = 0, n_gen = 64, avg_ts = 11.904277 }
        });

        var parsed = LocalLlmQualificationBenchmarkRunner.TryParseMeasurements(
            json,
            scenario,
            out var prompt,
            out var generation,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal(208.338954, prompt, 6);
        Assert.Equal(11.904277, generation, 6);
    }

    [Theory]
    [InlineData("{}", "benchmark_json_not_array")]
    [InlineData("not-json", "benchmark_json_invalid")]
    [InlineData("[{\"n_prompt\":512,\"n_gen\":0,\"avg_ts\":10}]", "benchmark_measurement_missing")]
    public void ParseMeasurements_rejects_incomplete_or_invalid_output(string json, string expectedError)
    {
        var parsed = LocalLlmQualificationBenchmarkRunner.TryParseMeasurements(
            json,
            new LocalLlmQualificationBenchmarkScenario("rag-control", 512, 64),
            out _,
            out _,
            out var error);

        Assert.False(parsed);
        Assert.StartsWith(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rank_uses_measured_projected_workload_and_keeps_failed_candidates_visible()
    {
        var fastPrefill = Candidate("fast-prefill");
        var fastDecode = Candidate("fast-decode");
        var failed = Candidate("failed");
        var scenarios = new[]
        {
            new LocalLlmQualificationBenchmarkScenario("control", 800, 64, Weight: 8),
            new LocalLlmQualificationBenchmarkScenario("writer", 2400, 512, Weight: 1)
        };
        var results = new[]
        {
            Success(fastPrefill, "control", 200, 12),
            Success(fastPrefill, "writer", 200, 12),
            Success(fastDecode, "control", 90, 16),
            Success(fastDecode, "writer", 90, 16),
            new LocalLlmQualificationBenchmarkResult(
                failed.CandidateId,
                "control",
                Succeeded: false,
                TimedOut: true,
                ExitCode: null,
                DurationMs: 1000,
                PromptTokensPerSecond: null,
                GenerationTokensPerSecond: null,
                Diagnostic: "timeout")
        };

        var ranked = LocalLlmQualificationBenchmarkRanker.Rank(
            new[] { failed, fastDecode, fastPrefill },
            results,
            scenarios);

        Assert.Equal(fastPrefill.CandidateId, ranked[0].Candidate.CandidateId);
        Assert.True(ranked[0].Eligible);
        Assert.Equal(fastDecode.CandidateId, ranked[1].Candidate.CandidateId);
        Assert.False(ranked[2].Eligible);
        Assert.Contains(ranked[2].Reasons, reason => reason.StartsWith("measurement_", StringComparison.Ordinal));
    }

    private static LocalLlmQualificationCandidate Candidate(string id)
        => new(
            id,
            "test",
            @"C:\runtime\llama-server.exe",
            WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with { ProfileId = id },
            Array.Empty<string>());

    private static LocalLlmQualificationBenchmarkResult Success(
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
            DurationMs: 1000,
            PromptTokensPerSecond: prompt,
            GenerationTokensPerSecond: generation,
            Diagnostic: string.Empty);

    private static void AssertPair(IReadOnlyList<string> arguments, string name, string value)
    {
        var index = Array.IndexOf(arguments.ToArray(), name);
        Assert.InRange(index, 0, arguments.Count - 2);
        Assert.Equal(value, arguments[index + 1]);
    }
}
