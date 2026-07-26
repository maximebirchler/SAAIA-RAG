using System.Security.Cryptography;
using System.Text;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmQualificationCandidateLadder(
    string TopologyId,
    IReadOnlyList<LocalLlmQualificationCandidate> Candidates);

internal sealed record LocalLlmQualificationScreeningEntry(
    string TopologyId,
    LocalLlmQualificationCandidate Candidate,
    LocalLlmFitParamsProbeResult FitProbe,
    LocalLlmFitAssessment FitAssessment,
    LocalLlmQualificationBenchmarkResult? Benchmark);

internal sealed record LocalLlmQualificationScreeningOutcome(
    IReadOnlyList<LocalLlmQualificationCandidateLadder> Ladders,
    IReadOnlyList<LocalLlmQualificationScreeningEntry> Entries,
    IReadOnlyList<LocalLlmQualificationCandidateScore> RankedRepresentatives);

internal static class LocalLlmQualificationScreening
{
    public static IReadOnlyList<LocalLlmQualificationCandidateLadder> BuildLadders(
        IReadOnlyList<LocalLlmQualificationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .GroupBy(BuildTopologyIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalLlmQualificationCandidateLadder(
                BuildTopologyId(group.Key),
                group
                    .OrderBy(static candidate => CandidatePriority(candidate.CandidateKind))
                    .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(static ladder => ladder.TopologyId, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<LocalLlmQualificationScreeningOutcome> RunAsync(
        IReadOnlyList<LocalLlmQualificationCandidate> candidates,
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> runtimeProbes,
        string modelPath,
        LocalLlmQualificationBenchmarkScenario scenario,
        int? availableSystemRamMiB,
        int dedicatedDeviceMarginMiB,
        int systemRamReserveMiB,
        Func<LocalLlmQualificationCandidate, CancellationToken, Task<LocalLlmFitParamsProbeResult>>? fitRunner = null,
        Func<LocalLlmQualificationCandidate, CancellationToken, Task<LocalLlmQualificationBenchmarkResult>>? benchmarkRunner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(runtimeProbes);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(scenario);

        fitRunner ??= (candidate, token) => LocalLlmFitParamsProbe.RunAsync(
            candidate,
            modelPath,
            dedicatedDeviceMarginMiB,
            Math.Min(2048, Math.Max(1, candidate.Profile.CtxSize)),
            ct: token);
        benchmarkRunner ??= (candidate, token) => LocalLlmQualificationBenchmarkRunner.RunAsync(
            candidate,
            modelPath,
            scenario,
            repetitions: 1,
            ct: token);

        var ladders = BuildLadders(candidates);
        var entries = new List<LocalLlmQualificationScreeningEntry>();
        var representatives = new List<LocalLlmQualificationCandidate>();
        var benchmarkResults = new List<LocalLlmQualificationBenchmarkResult>();
        foreach (var ladder in ladders)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var candidate in ladder.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                var runtimeProbe = FindRuntimeProbe(candidate, runtimeProbes);
                var fitProbe = await fitRunner(candidate, ct).ConfigureAwait(false);
                var assessment = runtimeProbe is null
                    ? new LocalLlmFitAssessment(
                        LocalLlmFitAssessmentStatus.Indeterminate,
                        new[] { "runtime_probe_missing" })
                    : LocalLlmFitParamsProbe.Assess(
                        fitProbe,
                        runtimeProbe,
                        availableSystemRamMiB,
                        dedicatedDeviceMarginMiB,
                        systemRamReserveMiB);
                if (assessment.Status == LocalLlmFitAssessmentStatus.DoesNotFit)
                {
                    entries.Add(new LocalLlmQualificationScreeningEntry(
                        ladder.TopologyId,
                        candidate,
                        fitProbe,
                        assessment,
                        Benchmark: null));
                    if (LocalLlmFitParamsProbe.IsTopologyUnsupported(fitProbe.Diagnostic))
                        break;
                    continue;
                }

                var benchmark = await benchmarkRunner(candidate, ct).ConfigureAwait(false);
                entries.Add(new LocalLlmQualificationScreeningEntry(
                    ladder.TopologyId,
                    candidate,
                    fitProbe,
                    assessment,
                    benchmark));
                benchmarkResults.Add(benchmark);
                if (!benchmark.Succeeded)
                    continue;

                representatives.Add(candidate);
                break;
            }
        }

        var ranked = LocalLlmQualificationBenchmarkRanker.Rank(
            representatives,
            benchmarkResults,
            new[] { scenario });
        return new LocalLlmQualificationScreeningOutcome(ladders, entries, ranked);
    }

    private static LocalLlmRuntimeCapabilityProbeResult? FindRuntimeProbe(
        LocalLlmQualificationCandidate candidate,
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> runtimeProbes)
        => runtimeProbes.FirstOrDefault(probe =>
               string.Equals(probe.ExecutablePath, candidate.RuntimeExecutablePath, StringComparison.OrdinalIgnoreCase))
           ?? runtimeProbes.FirstOrDefault(probe =>
               string.Equals(probe.RuntimeId, candidate.Profile.Runtime, StringComparison.OrdinalIgnoreCase));

    private static string BuildTopologyIdentity(LocalLlmQualificationCandidate candidate)
        => string.Join(
            '|',
            candidate.Profile.Runtime.Trim().ToLowerInvariant(),
            string.Join(
                '/',
                candidate.Profile.DeviceIds
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Select(static id => id.Trim().ToLowerInvariant())),
            candidate.Profile.SplitMode.Trim().ToLowerInvariant());

    private static string BuildTopologyId(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..12];
        return "topology-" + hash;
    }

    private static int CandidatePriority(string candidateKind)
    {
        var normalized = candidateKind.Trim().ToLowerInvariant();
        if (normalized == "cpu-balanced")
            return 0;
        if (normalized.Contains("all-model-layers", StringComparison.Ordinal))
            return 0;
        if (normalized.Contains("-capacity", StringComparison.Ordinal))
            return 0;
        if (normalized.Contains("-balanced", StringComparison.Ordinal))
            return 1;
        if (normalized.Contains("full-offload", StringComparison.Ordinal))
            return 1;
        if (normalized.Contains("-prefer-", StringComparison.Ordinal))
            return 2;
        if (normalized.Contains("partial-offload", StringComparison.Ordinal))
            return 2;
        if (normalized.Contains("minimal", StringComparison.Ordinal))
            return 3;
        if (normalized.Contains("long-context", StringComparison.Ordinal))
            return 4;
        return 5;
    }
}
