using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmFitMemoryEstimate(
    string Target,
    int ModelMiB,
    int ContextMiB,
    int ComputeMiB)
{
    public int TotalMiB => checked(ModelMiB + ContextMiB + ComputeMiB);
}

internal sealed record LocalLlmFitParamsProbeResult(
    string CandidateId,
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    int DurationMs,
    IReadOnlyList<LocalLlmFitMemoryEstimate> Estimates,
    string Diagnostic);

internal enum LocalLlmFitAssessmentStatus
{
    Fits,
    DoesNotFit,
    Indeterminate
}

internal sealed record LocalLlmFitAssessment(
    LocalLlmFitAssessmentStatus Status,
    IReadOnlyList<string> Reasons);

internal static partial class LocalLlmFitParamsProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<LocalLlmFitParamsProbeResult> RunAsync(
        LocalLlmQualificationCandidate candidate,
        string modelPath,
        int targetMarginMiB,
        int minimumContextSize,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var executablePath = ResolveExecutablePath(candidate.RuntimeExecutablePath);
        if (!File.Exists(executablePath))
        {
            return Failure(
                candidate.CandidateId,
                timedOut: false,
                exitCode: null,
                durationMs: 0,
                "llama_fit_params_missing");
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = BuildStartInfo(
                executablePath,
                modelPath,
                candidate.Profile,
                targetMarginMiB,
                minimumContextSize)
        };

        try
        {
            if (!process.Start())
            {
                return Failure(
                    candidate.CandidateId,
                    timedOut: false,
                    exitCode: null,
                    durationMs: ElapsedMilliseconds(stopwatch),
                    "process_start_failed");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested)
                    throw;

                return Failure(
                    candidate.CandidateId,
                    timedOut: true,
                    TryGetExitCode(process),
                    ElapsedMilliseconds(stopwatch),
                    "fit_params_timeout");
            }

            var stdout = await ReadCompletedOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOutputAsync(stderrTask).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            var estimates = ParseEstimates(stdout);
            if (exitCode != 0 || estimates.Count == 0)
            {
                return Failure(
                    candidate.CandidateId,
                    timedOut: false,
                    exitCode,
                    ElapsedMilliseconds(stopwatch),
                    exitCode == 0 ? "fit_params_output_missing" : CompactDiagnostic(stderr));
            }

            return new LocalLlmFitParamsProbeResult(
                candidate.CandidateId,
                Succeeded: true,
                TimedOut: false,
                exitCode,
                ElapsedMilliseconds(stopwatch),
                estimates,
                CompactDiagnostic(stderr));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            return Failure(
                candidate.CandidateId,
                timedOut: false,
                TryGetExitCode(process),
                ElapsedMilliseconds(stopwatch),
                ex.GetType().Name);
        }
    }

    internal static string ResolveExecutablePath(string serverExecutablePath)
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(serverExecutablePath)) ?? string.Empty,
            "llama-fit-params.exe");

    internal static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string modelPath,
        QualifiedProfile profile,
        int targetMarginMiB,
        int minimumContextSize)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Add(startInfo, "-m", Path.GetFullPath(modelPath));
        Add(startInfo, "-c", Math.Max(1, profile.CtxSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-b", Math.Max(1, profile.BatchSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-ub", Math.Max(1, profile.UbatchSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-t", Math.Max(1, profile.Threads).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-tb", Math.Max(1, profile.ThreadsBatch).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-ngl", Math.Max(0, profile.Ngl).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-fa", profile.FlashAttn ? "on" : "off");
        Add(startInfo, "-ctk", NormalizeCacheType(profile.CacheTypeK));
        Add(startInfo, "-ctv", NormalizeCacheType(profile.CacheTypeV));
        Add(startInfo, "-np", Math.Max(1, profile.Parallel).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-sm", NormalizeSplitMode(profile.SplitMode));
        Add(startInfo, "-mg", Math.Max(0, profile.MainGpu).ToString(CultureInfo.InvariantCulture));

        var deviceIds = profile.DeviceIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => string.Equals(id, "none", StringComparison.OrdinalIgnoreCase) ? "none" : id)
            .ToArray();
        if (deviceIds.Length > 0)
            Add(startInfo, "-dev", string.Join(',', deviceIds));

        if (profile.TensorSplit.Count > 0)
        {
            Add(
                startInfo,
                "-ts",
                string.Join(
                    ',',
                    profile.TensorSplit.Select(static value =>
                        Math.Max(0d, value).ToString("0.######", CultureInfo.InvariantCulture))));
        }

        Add(startInfo, "-fit", "on");
        Add(startInfo, "-fitt", Math.Max(0, targetMarginMiB).ToString(CultureInfo.InvariantCulture));
        Add(
            startInfo,
            "-fitc",
            Math.Clamp(minimumContextSize, 1, Math.Max(1, profile.CtxSize)).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-fitp", "on");
        Add(startInfo, "-n", "1");
        Add(startInfo, "-p", "x");
        return startInfo;
    }

    internal static IReadOnlyList<LocalLlmFitMemoryEstimate> ParseEstimates(string output)
    {
        var estimates = new List<LocalLlmFitMemoryEstimate>();
        foreach (var line in (output ?? string.Empty).Split(
                     new[] { "\r\n", "\n" },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = EstimateLine().Match(line);
            if (!match.Success)
                continue;

            estimates.Add(new LocalLlmFitMemoryEstimate(
                match.Groups["target"].Value,
                int.Parse(match.Groups["model"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["context"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["compute"].Value, CultureInfo.InvariantCulture)));
        }

        return estimates;
    }

    internal static LocalLlmFitAssessment Assess(
        LocalLlmFitParamsProbeResult result,
        LocalLlmRuntimeCapabilityProbeResult runtimeProbe,
        int? availableSystemRamMiB,
        int dedicatedDeviceMarginMiB,
        int systemRamReserveMiB)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(runtimeProbe);
        if (!result.Succeeded)
        {
            if (IsTopologyUnsupported(result.Diagnostic))
            {
                return new LocalLlmFitAssessment(
                    LocalLlmFitAssessmentStatus.DoesNotFit,
                    new[] { "runtime_topology_unsupported" });
            }

            return new LocalLlmFitAssessment(
                LocalLlmFitAssessmentStatus.Indeterminate,
                new[] { result.TimedOut ? "fit_probe_timeout" : "fit_probe_failed" });
        }

        var reasons = new List<string>();
        var indeterminate = false;
        var hostRequiredMiB = result.Estimates
            .Where(static estimate => string.Equals(estimate.Target, "Host", StringComparison.OrdinalIgnoreCase))
            .Sum(static estimate => estimate.TotalMiB);
        var unifiedRequiredMiB = 0;
        foreach (var estimate in result.Estimates.Where(static estimate =>
                     !string.Equals(estimate.Target, "Host", StringComparison.OrdinalIgnoreCase)))
        {
            var device = runtimeProbe.Devices.FirstOrDefault(item => string.Equals(
                item.DeviceId,
                estimate.Target,
                StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                indeterminate = true;
                reasons.Add($"device_budget_unknown:{estimate.Target}");
                continue;
            }

            if (device.ReportsSharedMemory
                || string.Equals(device.MemoryArchitecture, "unified", StringComparison.OrdinalIgnoreCase))
            {
                unifiedRequiredMiB += estimate.TotalMiB;
                continue;
            }

            var freeMiB = device.ReportedFreeMemoryMiB ?? device.ReportedMemoryMiB;
            if (freeMiB is null)
            {
                indeterminate = true;
                reasons.Add($"device_budget_unknown:{estimate.Target}");
                continue;
            }

            var requiredWithMargin = estimate.TotalMiB + Math.Max(0, dedicatedDeviceMarginMiB);
            if (requiredWithMargin > freeMiB.Value)
            {
                reasons.Add(
                    $"dedicated_budget_exceeded:{estimate.Target}:{requiredWithMargin}>{freeMiB.Value}");
            }
        }

        var systemRequiredMiB = checked(
            hostRequiredMiB
            + unifiedRequiredMiB
            + Math.Max(0, systemRamReserveMiB));
        if (availableSystemRamMiB is null)
        {
            if (systemRequiredMiB > 0)
            {
                indeterminate = true;
                reasons.Add("system_ram_budget_unknown");
            }
        }
        else if (systemRequiredMiB > availableSystemRamMiB.Value)
        {
            reasons.Add($"system_ram_budget_exceeded:{systemRequiredMiB}>{availableSystemRamMiB.Value}");
        }

        if (reasons.Any(static reason => reason.Contains("_exceeded:", StringComparison.Ordinal)))
            return new LocalLlmFitAssessment(LocalLlmFitAssessmentStatus.DoesNotFit, reasons);
        if (indeterminate)
            return new LocalLlmFitAssessment(LocalLlmFitAssessmentStatus.Indeterminate, reasons);
        return new LocalLlmFitAssessment(
            LocalLlmFitAssessmentStatus.Fits,
            new[] { "estimated_memory_fits_observed_budgets" });
    }

    internal static bool IsTopologyUnsupported(string diagnostic)
        => diagnostic.Contains("does not support split buffers", StringComparison.OrdinalIgnoreCase)
           || diagnostic.Contains("split mode is not supported", StringComparison.OrdinalIgnoreCase)
           || diagnostic.Contains("unsupported split mode", StringComparison.OrdinalIgnoreCase);

    private static void Add(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string NormalizeCacheType(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "q8_0" => "q8_0",
            "q4_0" => "q4_0",
            _ => "f16"
        };

    private static string NormalizeSplitMode(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "layer" => "layer",
            "row" => "row",
            "tensor" => "tensor",
            _ => "none"
        };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static int ElapsedMilliseconds(Stopwatch stopwatch)
        => (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);

    private static async Task<string> ReadCompletedOutputAsync(Task<string> outputTask)
    {
        try { return await outputTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string CompactDiagnostic(string value)
    {
        var lines = (value ?? string.Empty).Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var important = lines.Where(static line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        var compact = string.Join(
            " | ",
            important
                .Concat(lines)
                .Distinct(StringComparer.Ordinal)
                .Take(8));
        return compact.Length <= 800 ? compact : compact[..800];
    }

    private static LocalLlmFitParamsProbeResult Failure(
        string candidateId,
        bool timedOut,
        int? exitCode,
        int durationMs,
        string diagnostic)
        => new(
            candidateId,
            Succeeded: false,
            timedOut,
            exitCode,
            durationMs,
            Array.Empty<LocalLlmFitMemoryEstimate>(),
            CompactDiagnostic(diagnostic));

    [GeneratedRegex(
        @"^(?<target>\S+)\s+(?<model>\d+)\s+(?<context>\d+)\s+(?<compute>\d+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EstimateLine();
}
