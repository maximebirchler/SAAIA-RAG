using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmQualificationRefinementOptions(
    int TopTopologyCount,
    int MaxSuccessfulCandidatesPerTopology,
    int MaxAttemptsPerTopology,
    int LogicalProcessorCount,
    int BenchmarkRepetitions = 1)
{
    public static LocalLlmQualificationRefinementOptions CreateDefault(int logicalProcessorCount)
        => new(
            TopTopologyCount: 2,
            MaxSuccessfulCandidatesPerTopology: 4,
            MaxAttemptsPerTopology: 12,
            LogicalProcessorCount: Math.Clamp(logicalProcessorCount, 1, 256),
            BenchmarkRepetitions: 1);
}

internal sealed record LocalLlmQualificationRefinementQueue(
    string TopologyId,
    IReadOnlyList<LocalLlmQualificationCandidate> Candidates);

internal sealed record LocalLlmQualificationRefinementEntry(
    string TopologyId,
    LocalLlmQualificationCandidate Candidate,
    LocalLlmFitParamsProbeResult FitProbe,
    LocalLlmFitAssessment FitAssessment,
    IReadOnlyList<LocalLlmQualificationBenchmarkResult> Benchmarks);

internal sealed record LocalLlmQualificationRefinementOutcome(
    IReadOnlyList<string> SelectedTopologyIds,
    IReadOnlyList<LocalLlmQualificationRefinementQueue> Queues,
    IReadOnlyList<LocalLlmQualificationRefinementEntry> Entries,
    IReadOnlyList<LocalLlmQualificationCandidateScore> RankedCandidates);

internal static class LocalLlmQualificationRefinement
{
    public static IReadOnlyList<LocalLlmQualificationBenchmarkScenario> CreateDefaultWorkload()
        => new[]
        {
            new LocalLlmQualificationBenchmarkScenario(
                "rag-control-p1536-n128",
                PromptTokens: 1536,
                GenerationTokens: 128,
                Weight: 6),
            new LocalLlmQualificationBenchmarkScenario(
                "rag-writer-p3072-n512",
                PromptTokens: 3072,
                GenerationTokens: 512,
                Weight: 1),
            new LocalLlmQualificationBenchmarkScenario(
                "rag-evidence-inventory-p5632-n384",
                PromptTokens: 5632,
                GenerationTokens: 384,
                Weight: 2)
        };

    public static IReadOnlyList<string> SelectTopologies(
        LocalLlmQualificationScreeningOutcome screening,
        int topTopologyCount)
    {
        ArgumentNullException.ThrowIfNull(screening);
        var topologyLimit = Math.Clamp(topTopologyCount, 1, 16);
        var topologyByCandidate = screening.Entries
            .GroupBy(static entry => entry.Candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last().TopologyId,
                StringComparer.OrdinalIgnoreCase);

        return screening.RankedRepresentatives
            .Where(static score => score.Eligible)
            .Select(score => topologyByCandidate.GetValueOrDefault(score.Candidate.CandidateId))
            .Where(static topologyId => !string.IsNullOrWhiteSpace(topologyId))
            .Select(static topologyId => topologyId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(topologyLimit)
            .ToArray();
    }

    internal static IReadOnlyList<LocalLlmQualificationCandidate> BuildCandidateQueue(
        IReadOnlyList<LocalLlmQualificationCandidate> topologyCandidates,
        IReadOnlyList<LocalLlmQualificationBenchmarkScenario> workload,
        int logicalProcessorCount,
        int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(topologyCandidates);
        ArgumentNullException.ThrowIfNull(workload);
        if (workload.Count == 0)
            throw new ArgumentException("At least one benchmark scenario is required.", nameof(workload));

        var attemptLimit = Math.Clamp(maxAttempts, 1, 64);
        var throughputThreads = Math.Clamp(logicalProcessorCount - 2, 1, 64);
        var baseCandidates = topologyCandidates
            .Where(candidate => SupportsWorkload(candidate.Profile, workload))
            .OrderBy(static candidate => CandidatePriority(candidate.CandidateKind))
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
        var queue = new List<LocalLlmQualificationCandidate>();

        // First compare the meaningful profile families: layer count, context and KV cache.
        AddUnique(queue, baseCandidates);

        // Then measure the machine rather than assuming the best CPU thread count.
        AddUnique(
            queue,
            baseCandidates.Select(candidate => CreateVariant(
                candidate,
                "throughput-threads",
                candidate.Profile with
                {
                    Threads = throughputThreads,
                    ThreadsBatch = throughputThreads
                })));

        // Some backends or older GPUs load only when Flash Attention is disabled.
        AddUnique(
            queue,
            baseCandidates
                .Where(static candidate => candidate.Profile.FlashAttn)
                .Select(candidate => CreateVariant(
                    candidate,
                    "flash-off",
                    candidate.Profile with { FlashAttn = false })));

        // A smaller batch is a useful low-memory fallback without reducing context.
        AddUnique(
            queue,
            baseCandidates.Select(candidate => CreateVariant(
                candidate,
                "compact-batch",
                candidate.Profile with
                {
                    BatchSize = Math.Min(candidate.Profile.BatchSize, 512),
                    UbatchSize = Math.Min(candidate.Profile.UbatchSize, 128),
                    Threads = throughputThreads,
                    ThreadsBatch = throughputThreads
                })));

        return queue.Take(attemptLimit).ToArray();
    }

    public static async Task<LocalLlmQualificationRefinementOutcome> RunAsync(
        LocalLlmQualificationScreeningOutcome screening,
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> runtimeProbes,
        string modelPath,
        IReadOnlyList<LocalLlmQualificationBenchmarkScenario> workload,
        int? availableSystemRamMiB,
        int dedicatedDeviceMarginMiB,
        int systemRamReserveMiB,
        LocalLlmQualificationRefinementOptions options,
        Func<LocalLlmQualificationCandidate, CancellationToken, Task<LocalLlmFitParamsProbeResult>>? fitRunner = null,
        Func<LocalLlmQualificationCandidate, LocalLlmQualificationBenchmarkScenario, CancellationToken, Task<LocalLlmQualificationBenchmarkResult>>? benchmarkRunner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(screening);
        ArgumentNullException.ThrowIfNull(runtimeProbes);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(options);
        if (workload.Count == 0)
            throw new ArgumentException("At least one benchmark scenario is required.", nameof(workload));

        var selectedTopologyIds = SelectTopologies(screening, options.TopTopologyCount);
        var requiredContextSize = workload.Max(static scenario =>
            checked(scenario.PromptTokens + scenario.GenerationTokens));
        fitRunner ??= (candidate, token) => LocalLlmFitParamsProbe.RunAsync(
            candidate,
            modelPath,
            dedicatedDeviceMarginMiB,
            Math.Min(candidate.Profile.CtxSize, requiredContextSize),
            ct: token);
        benchmarkRunner ??= (candidate, scenario, token) =>
            LocalLlmQualificationBenchmarkRunner.RunAsync(
                candidate,
                modelPath,
                scenario,
                repetitions: Math.Clamp(options.BenchmarkRepetitions, 1, 10),
                ct: token);

        var queues = new List<LocalLlmQualificationRefinementQueue>();
        var entries = new List<LocalLlmQualificationRefinementEntry>();
        var attemptedCandidates = new List<LocalLlmQualificationCandidate>();
        var measurements = new List<LocalLlmQualificationBenchmarkResult>();
        foreach (var topologyId in selectedTopologyIds)
        {
            ct.ThrowIfCancellationRequested();
            var ladder = screening.Ladders.FirstOrDefault(item => string.Equals(
                item.TopologyId,
                topologyId,
                StringComparison.OrdinalIgnoreCase));
            if (ladder is null)
                continue;

            var queue = BuildCandidateQueue(
                ladder.Candidates,
                workload,
                options.LogicalProcessorCount,
                options.MaxAttemptsPerTopology);
            queues.Add(new LocalLlmQualificationRefinementQueue(topologyId, queue));
            var successfulCandidates = 0;
            foreach (var candidate in queue)
            {
                ct.ThrowIfCancellationRequested();
                if (successfulCandidates >= Math.Clamp(
                        options.MaxSuccessfulCandidatesPerTopology,
                        1,
                        16))
                {
                    break;
                }

                attemptedCandidates.Add(candidate);
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
                var candidateMeasurements = new List<LocalLlmQualificationBenchmarkResult>();
                if (assessment.Status != LocalLlmFitAssessmentStatus.DoesNotFit)
                {
                    foreach (var scenario in workload)
                    {
                        ct.ThrowIfCancellationRequested();
                        var measurement = await benchmarkRunner(candidate, scenario, ct)
                            .ConfigureAwait(false);
                        candidateMeasurements.Add(measurement);
                        measurements.Add(measurement);
                        if (!measurement.Succeeded)
                            break;
                    }
                }

                entries.Add(new LocalLlmQualificationRefinementEntry(
                    topologyId,
                    candidate,
                    fitProbe,
                    assessment,
                    candidateMeasurements));
                if (candidateMeasurements.Count == workload.Count
                    && candidateMeasurements.All(static result => result.Succeeded))
                {
                    successfulCandidates++;
                }

                if (LocalLlmFitParamsProbe.IsTopologyUnsupported(fitProbe.Diagnostic))
                    break;
            }
        }

        var ranked = LocalLlmQualificationBenchmarkRanker.Rank(
            attemptedCandidates,
            measurements,
            workload);
        return new LocalLlmQualificationRefinementOutcome(
            selectedTopologyIds,
            queues,
            entries,
            ranked);
    }

    private static bool SupportsWorkload(
        QualifiedProfile profile,
        IReadOnlyList<LocalLlmQualificationBenchmarkScenario> workload)
        => workload.All(scenario =>
            scenario.PromptTokens > 0
            && scenario.GenerationTokens > 0
            && checked(scenario.PromptTokens + scenario.GenerationTokens) <= profile.CtxSize);

    private static LocalLlmRuntimeCapabilityProbeResult? FindRuntimeProbe(
        LocalLlmQualificationCandidate candidate,
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> runtimeProbes)
        => runtimeProbes.FirstOrDefault(probe =>
               string.Equals(
                   probe.ExecutablePath,
                   candidate.RuntimeExecutablePath,
                   StringComparison.OrdinalIgnoreCase))
           ?? runtimeProbes.FirstOrDefault(probe =>
               string.Equals(
                   probe.RuntimeId,
                   candidate.Profile.Runtime,
                   StringComparison.OrdinalIgnoreCase));

    private static int CandidatePriority(string candidateKind)
    {
        var normalized = candidateKind.Trim().ToLowerInvariant();
        if (normalized.Contains("all-model-layers", StringComparison.Ordinal))
            return 0;
        if (normalized.Contains("full-offload", StringComparison.Ordinal))
            return 1;
        if (normalized.Contains("long-context", StringComparison.Ordinal))
            return 2;
        if (normalized.Contains("cpu-balanced", StringComparison.Ordinal))
            return 0;
        if (normalized.Contains("partial-offload", StringComparison.Ordinal))
            return 3;
        if (normalized.Contains("minimal", StringComparison.Ordinal))
            return 4;
        return 5;
    }

    private static void AddUnique(
        ICollection<LocalLlmQualificationCandidate> destination,
        IEnumerable<LocalLlmQualificationCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (destination.Any(existing => ProfilesEqual(existing.Profile, candidate.Profile)))
                continue;
            destination.Add(candidate);
        }
    }

    private static bool ProfilesEqual(QualifiedProfile left, QualifiedProfile right)
        => string.Equals(left.Runtime, right.Runtime, StringComparison.OrdinalIgnoreCase)
           && left.CtxSize == right.CtxSize
           && left.BatchSize == right.BatchSize
           && left.UbatchSize == right.UbatchSize
           && left.Threads == right.Threads
           && left.ThreadsBatch == right.ThreadsBatch
           && left.Ngl == right.Ngl
           && left.FlashAttn == right.FlashAttn
           && left.Mlock == right.Mlock
           && left.Parallel == right.Parallel
           && left.MainGpu == right.MainGpu
           && string.Equals(left.SplitMode, right.SplitMode, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.CacheTypeK, right.CacheTypeK, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.CacheTypeV, right.CacheTypeV, StringComparison.OrdinalIgnoreCase)
           && left.DeviceIds.SequenceEqual(right.DeviceIds, StringComparer.OrdinalIgnoreCase)
           && left.TensorSplit.SequenceEqual(right.TensorSplit);

    private static LocalLlmQualificationCandidate CreateVariant(
        LocalLlmQualificationCandidate candidate,
        string variant,
        QualifiedProfile profile)
    {
        if (ProfilesEqual(candidate.Profile, profile))
            return candidate;

        var identity = string.Join(
            '|',
            candidate.CandidateId,
            variant,
            profile.CtxSize.ToString(CultureInfo.InvariantCulture),
            profile.BatchSize.ToString(CultureInfo.InvariantCulture),
            profile.UbatchSize.ToString(CultureInfo.InvariantCulture),
            profile.Threads.ToString(CultureInfo.InvariantCulture),
            profile.ThreadsBatch.ToString(CultureInfo.InvariantCulture),
            profile.Ngl.ToString(CultureInfo.InvariantCulture),
            profile.FlashAttn ? "fa1" : "fa0",
            profile.CacheTypeK,
            profile.CacheTypeV);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..12];
        var profileId = candidate.CandidateId + "-" + variant + "-" + hash;
        return candidate with
        {
            CandidateId = profileId,
            CandidateKind = candidate.CandidateKind + "-" + variant,
            Profile = profile with { ProfileId = profileId },
            CapabilityRefs = candidate.CapabilityRefs
                .Concat(new[] { "profile-variant:" + variant })
                .ToArray()
        };
    }
}
