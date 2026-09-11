using System.Security.Cryptography;
using System.Text;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmQualificationCandidate(
    string CandidateId,
    string CandidateKind,
    string RuntimeExecutablePath,
    QualifiedProfile Profile,
    IReadOnlyList<string> CapabilityRefs);

internal sealed record LocalLlmMultiDeviceSplitCandidate(
    string Name,
    IReadOnlyList<double> Weights,
    int MainGpu);

internal static class LocalLlmQualificationCandidateFactory
{
    public static IReadOnlyList<LocalLlmQualificationCandidate> Build(
        string modelId,
        string? modelPath,
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> runtimeProbes,
        int logicalProcessorCount,
        int? blockCountOverride = null,
        int maxCandidates = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(runtimeProbes);

        var metadata = blockCountOverride is null && !string.IsNullOrWhiteSpace(modelPath)
            ? GgufMetadataReader.TryRead(modelPath)
            : null;
        var blockCount = Math.Clamp(blockCountOverride ?? (int?)metadata?.BlockCount ?? 36, 1, 256);
        var logicalProcessors = Math.Clamp(logicalProcessorCount, 1, 256);
        var candidateLimit = Math.Clamp(maxCandidates, 1, 256);
        const int generationLimit = 2048;
        var candidates = new List<LocalLlmQualificationCandidate>();

        foreach (var probe in runtimeProbes.Where(static probe => probe.Succeeded))
        {
            var runtimeDevices = probe.Devices
                .GroupBy(static device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            var cpuRuntime = string.Equals(probe.RuntimeId, "llama.cpp-cpu", StringComparison.OrdinalIgnoreCase);
            if (cpuRuntime)
            {
                AddCpuCandidates(
                    candidates,
                    modelId,
                    probe,
                    logicalProcessors,
                    generationLimit);
                if (candidates.Count >= generationLimit)
                    break;
                continue;
            }

            foreach (var device in runtimeDevices.Where(static device =>
                         !string.Equals(device.DeviceId, "CPU", StringComparison.OrdinalIgnoreCase)))
            {
                AddSingleAcceleratorCandidates(
                    candidates,
                    modelId,
                    probe,
                    device,
                    blockCount,
                    logicalProcessors,
                    generationLimit);
                if (candidates.Count >= generationLimit)
                    break;
            }

            if (runtimeDevices.Length > 1 && candidates.Count < generationLimit)
            {
                AddMultiAcceleratorCandidates(
                    candidates,
                    modelId,
                    probe,
                    runtimeDevices,
                    blockCount,
                    logicalProcessors,
                    generationLimit);
            }

            if (candidates.Count >= generationLimit)
                break;
        }

        return SelectFairBoundedCandidates(candidates, candidateLimit);
    }

    internal static IReadOnlyList<LocalLlmQualificationCandidate> SelectFairBoundedCandidates(
        IReadOnlyList<LocalLlmQualificationCandidate> candidates,
        int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var limit = Math.Clamp(maxCandidates, 1, 256);
        if (candidates.Count <= limit)
            return candidates.ToArray();

        var groups = candidates
            .GroupBy(
                static candidate => candidate.Profile.Runtime
                                    + "|"
                                    + string.Join(
                                        ",",
                                        candidate.Profile.DeviceIds
                                        ?? Array.Empty<string>())
                                    + "|"
                                    + candidate.Profile.SplitMode,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => new Queue<LocalLlmQualificationCandidate>(group))
            .ToArray();
        var selected = new List<LocalLlmQualificationCandidate>(limit);
        while (selected.Count < limit)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (group.Count == 0)
                    continue;

                selected.Add(group.Dequeue());
                added = true;
                if (selected.Count >= limit)
                    break;
            }

            if (!added)
                break;
        }

        return selected;
    }

    private static void AddCpuCandidates(
        ICollection<LocalLlmQualificationCandidate> candidates,
        string modelId,
        LocalLlmRuntimeCapabilityProbeResult probe,
        int logicalProcessors,
        int limit)
    {
        var balancedThreads = Math.Clamp(logicalProcessors / 2, 1, 32);
        var throughputThreads = Math.Clamp(logicalProcessors - 2, 1, 64);
        Add(
            candidates,
            limit,
            "cpu-minimal",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "cpu-minimal", 2048, 256, 128, balancedThreads, 0, false)
                with
            { DeviceIds = new[] { "none" } });
        Add(
            candidates,
            limit,
            "cpu-balanced",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "cpu-balanced", 4096, 512, 128, throughputThreads, 0, false)
                with
            { DeviceIds = new[] { "none" } });
        Add(
            candidates,
            limit,
            "cpu-long-context-q8-kv",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "cpu-long-context-q8-kv", 8192, 512, 128, throughputThreads, 0, false)
                with
            {
                DeviceIds = new[] { "none" },
                CacheTypeK = "q8_0",
                CacheTypeV = "q8_0"
            });
    }

    private static void AddSingleAcceleratorCandidates(
        ICollection<LocalLlmQualificationCandidate> candidates,
        string modelId,
        LocalLlmRuntimeCapabilityProbeResult probe,
        LocalLlmRuntimeDeviceInfo device,
        int blockCount,
        int logicalProcessors,
        int limit)
    {
        var threads = Math.Clamp(logicalProcessors / 2, 1, 32);
        var deviceRef = device.DeviceId;
        Add(
            candidates,
            limit,
            "accelerator-minimal",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "accelerator-minimal-" + deviceRef, 2048, 256, 128, threads, Math.Max(1, blockCount / 4), false)
                with
            { DeviceIds = new[] { deviceRef } },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-partial-offload",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "accelerator-partial-offload-" + deviceRef, 3072, 512, 128, threads, Math.Max(1, blockCount / 2), false)
                with
            { DeviceIds = new[] { deviceRef } },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-full-offload",
            probe,
            BuildProfile(modelId, probe.RuntimeId, "accelerator-full-offload-" + deviceRef, 4096, 1024, 256, threads, blockCount, true)
                with
            { DeviceIds = new[] { deviceRef } },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-all-model-layers",
            probe,
            BuildProfile(
                    modelId,
                    probe.RuntimeId,
                    "accelerator-all-model-layers-" + deviceRef,
                    4096,
                    1024,
                    256,
                    threads,
                    Math.Min(256, blockCount + 1),
                    true)
                with
            { DeviceIds = new[] { deviceRef } },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-long-context-partial-offload-q8-kv",
            probe,
            BuildProfile(
                    modelId,
                    probe.RuntimeId,
                    "accelerator-long-context-partial-offload-q8-kv-" + deviceRef,
                    8192,
                    512,
                    128,
                    threads,
                    Math.Max(1, blockCount / 2),
                    true)
                with
            {
                DeviceIds = new[] { deviceRef },
                CacheTypeK = "q8_0",
                CacheTypeV = "q8_0"
            },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-long-context-q8-kv",
            probe,
            BuildProfile(
                    modelId,
                    probe.RuntimeId,
                    "accelerator-long-context-q8-kv-" + deviceRef,
                    8192,
                    512,
                    128,
                    threads,
                    Math.Min(256, blockCount + 1),
                    true)
                with
            {
                DeviceIds = new[] { deviceRef },
                CacheTypeK = "q8_0",
                CacheTypeV = "q8_0"
            },
            deviceRef);
        Add(
            candidates,
            limit,
            "accelerator-dual-slot-long-context-q4-kv",
            probe,
            BuildProfile(
                    modelId,
                    probe.RuntimeId,
                    "accelerator-dual-slot-long-context-q4-kv-" + deviceRef,
                    16384,
                    512,
                    128,
                    threads,
                    Math.Min(256, blockCount + 1),
                    true)
                with
            {
                DeviceIds = new[] { deviceRef },
                CacheTypeK = "q4_0",
                CacheTypeV = "q4_0",
                Parallel = 2
            },
            deviceRef);
    }

    private static void AddMultiAcceleratorCandidates(
        ICollection<LocalLlmQualificationCandidate> candidates,
        string modelId,
        LocalLlmRuntimeCapabilityProbeResult probe,
        IReadOnlyList<LocalLlmRuntimeDeviceInfo> devices,
        int blockCount,
        int logicalProcessors,
        int limit)
    {
        var deviceIds = devices.Select(static device => device.DeviceId).ToArray();
        var capacityWeights = devices
            .Select(static device => (double)Math.Max(
                1,
                device.ReportedFreeMemoryMiB
                ?? device.ReportedMemoryMiB
                ?? 1))
            .ToArray();
        var threads = Math.Clamp(logicalProcessors / 2, 1, 32);
        var deviceIdentity = string.Join('-', deviceIds);
        foreach (var splitCandidate in BuildMultiDeviceSplitCandidates(devices, capacityWeights))
        {
            foreach (var splitMode in new[] { "layer", "row" })
            {
                var candidateKind = $"multi-accelerator-{splitMode}-{splitCandidate.Name}";
                Add(
                    candidates,
                    limit,
                    candidateKind,
                    probe,
                    BuildProfile(
                            modelId,
                            probe.RuntimeId,
                            candidateKind + "-" + deviceIdentity,
                            4096,
                            1024,
                            256,
                            threads,
                            Math.Min(256, blockCount + 1),
                            true)
                        with
                    {
                        DeviceIds = deviceIds,
                        SplitMode = splitMode,
                        TensorSplit = splitCandidate.Weights,
                        MainGpu = splitCandidate.MainGpu,
                        CacheTypeK = "q8_0",
                        CacheTypeV = "q8_0"
                    },
                    deviceIds);
                var longContextCandidateKind =
                    $"multi-accelerator-long-context-{splitMode}-{splitCandidate.Name}";
                Add(
                    candidates,
                    limit,
                    longContextCandidateKind,
                    probe,
                    BuildProfile(
                            modelId,
                            probe.RuntimeId,
                            longContextCandidateKind + "-" + deviceIdentity,
                            8192,
                            512,
                            128,
                            threads,
                            Math.Min(256, blockCount + 1),
                            true)
                        with
                    {
                        DeviceIds = deviceIds,
                        SplitMode = splitMode,
                        TensorSplit = splitCandidate.Weights,
                        MainGpu = splitCandidate.MainGpu,
                        CacheTypeK = "q8_0",
                        CacheTypeV = "q8_0"
                    },
                    deviceIds);
            }
        }
    }

    internal static IReadOnlyList<LocalLlmMultiDeviceSplitCandidate> BuildMultiDeviceSplitCandidates(
        IReadOnlyList<LocalLlmRuntimeDeviceInfo> devices,
        IReadOnlyList<double>? capacityWeights = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count < 2)
            return Array.Empty<LocalLlmMultiDeviceSplitCandidate>();

        var effectiveCapacityWeights = capacityWeights?.Count == devices.Count
            ? capacityWeights.Select(static weight => Math.Max(1d, weight)).ToArray()
            : devices.Select(static device => (double)Math.Max(
                1,
                device.ReportedFreeMemoryMiB
                ?? device.ReportedMemoryMiB
                ?? 1)).ToArray();
        var capacityMainGpu = Array.IndexOf(effectiveCapacityWeights, effectiveCapacityWeights.Max());
        var variants = new List<LocalLlmMultiDeviceSplitCandidate>
        {
            new("capacity", effectiveCapacityWeights, capacityMainGpu),
            new("balanced", Enumerable.Repeat(1d, devices.Count).ToArray(), capacityMainGpu)
        };

        var dominantWeight = Math.Max(4d, devices.Count);
        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            var weights = Enumerable.Repeat(1d, devices.Count).ToArray();
            weights[deviceIndex] = dominantWeight;
            variants.Add(new(
                "prefer-" + Sanitize(devices[deviceIndex].DeviceId, 18),
                weights,
                deviceIndex));
        }

        return variants
            .GroupBy(
                static variant => string.Join(
                    "/",
                    variant.Weights.Select(weight => weight.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))
                    + $"|{variant.MainGpu}",
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(8)
            .ToArray();
    }

    private static QualifiedProfile BuildProfile(
        string modelId,
        string runtimeId,
        string kind,
        int context,
        int batch,
        int ubatch,
        int threads,
        int ngl,
        bool flashAttention)
        => new(
            ProfileId: BuildProfileId(modelId, runtimeId, kind),
            Runtime: runtimeId,
            ModelId: modelId,
            CtxSize: context,
            BatchSize: batch,
            UbatchSize: ubatch,
            Threads: threads,
            ThreadsBatch: threads,
            Ngl: ngl,
            FlashAttn: flashAttention,
            Mlock: false,
            BatteryPolicyRef: "client-balanced",
            FallbackProfileRef: "auto-cpu-safe");

    private static void Add(
        ICollection<LocalLlmQualificationCandidate> candidates,
        int limit,
        string kind,
        LocalLlmRuntimeCapabilityProbeResult probe,
        QualifiedProfile profile,
        params string[] deviceRefs)
    {
        if (candidates.Count >= limit)
            return;

        var refs = new[] { $"runtime:{probe.RuntimeId}" }
            .Concat(deviceRefs.Select(static device => $"device:{device}"))
            .Concat(
                probe.Devices
                    .Where(device => deviceRefs.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase))
                    .Select(device => $"memory:{device.DeviceId}:{device.MemoryArchitecture}"))
            .ToArray();
        candidates.Add(new LocalLlmQualificationCandidate(
            profile.ProfileId,
            kind,
            probe.ExecutablePath,
            profile,
            refs));
    }

    private static string BuildProfileId(string modelId, string runtimeId, string kind)
    {
        var identity = $"{modelId}|{runtimeId}|{kind}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..12];
        return $"auto-{Sanitize(kind, 28)}-{hash}";
    }

    private static string Sanitize(string value, int maxLength)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var compact = string.Join(
            '-',
            new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maxLength ? compact : compact[..maxLength].TrimEnd('-');
    }
}
