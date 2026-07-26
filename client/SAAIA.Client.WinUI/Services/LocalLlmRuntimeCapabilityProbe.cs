using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmRuntimeDeviceInfo(
    string RuntimeId,
    string DeviceId,
    string Description,
    string ExecutablePath,
    string DiscoverySource)
{
    public int? ReportedMemoryMiB { get; init; }
    public int? ReportedFreeMemoryMiB { get; init; }
    public bool ReportsSharedMemory { get; init; }
    public string MemoryArchitecture { get; init; } = "unknown";
    public string HardwareMatchSource { get; init; } = "runtime_report";
    public string? MatchedHardwareDeviceId { get; init; }
}

internal sealed record LocalLlmRuntimeCapabilityProbeResult(
    string RuntimeId,
    string ExecutablePath,
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    int DurationMs,
    IReadOnlyList<LocalLlmRuntimeDeviceInfo> Devices,
    string Diagnostic);

internal static partial class LocalLlmRuntimeCapabilityProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private sealed record SyclPrerequisiteResult(
        bool Usable,
        bool TimedOut,
        int? ExitCode,
        string Diagnostic);

    public static async Task<IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult>> CaptureInstalledAsync(
        IEnumerable<string>? additionalExecutablePaths = null,
        CancellationToken ct = default,
        IReadOnlyList<GpuInfo>? gpuInventory = null)
    {
        var paths = new[]
            {
                LlamaCppReleaseDownloader.CudaServerExePath,
                LlamaCppReleaseDownloader.HipServerExePath,
                LlamaCppReleaseDownloader.SyclServerExePath,
                LlamaCppReleaseDownloader.VulkanServerExePath,
                LlamaCppReleaseDownloader.CpuServerExePath
            }
            .Concat(additionalExecutablePaths ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        var results = new List<LocalLlmRuntimeCapabilityProbeResult>(paths.Length);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ProbeAsync(path, DefaultTimeout, ct).ConfigureAwait(false));
        }

        gpuInventory ??= await GpuDetector.TryGetGpusAsync(ct).ConfigureAwait(false);
        return EnrichMemoryArchitecture(results, gpuInventory);
    }

    internal static IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> EnrichMemoryArchitecture(
        IReadOnlyList<LocalLlmRuntimeCapabilityProbeResult> probes,
        IReadOnlyList<GpuInfo> gpuInventory)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(gpuInventory);

        return probes.Select(probe => probe with
        {
            Devices = probe.Devices.Select(device =>
            {
                if (string.Equals(device.MemoryArchitecture, "system", StringComparison.OrdinalIgnoreCase))
                    return device;

                var match = FindHardwareMatch(device, gpuInventory);
                if (match.Gpu is null)
                    return device;

                var architecture = device.MemoryArchitecture;
                if (string.Equals(architecture, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    architecture = match.Gpu.IsIntegrated
                        ? "unified"
                        : match.Gpu.DedicatedVramBytes > 0
                            ? "dedicated"
                            : "unknown";
                }

                return device with
                {
                    ReportsSharedMemory = device.ReportsSharedMemory
                                          || string.Equals(
                                              architecture,
                                              "unified",
                                              StringComparison.OrdinalIgnoreCase),
                    MemoryArchitecture = architecture,
                    HardwareMatchSource = match.Source,
                    MatchedHardwareDeviceId = match.Gpu.StableDeviceId
                                              ?? match.Gpu.PnpDeviceId
                };
            }).ToArray()
        }).ToArray();
    }

    internal static async Task<LocalLlmRuntimeCapabilityProbeResult> ProbeAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(fullPath);
        var stopwatch = Stopwatch.StartNew();
        var timedOut = false;
        int? exitCode = null;
        string stdout = string.Empty;
        string stderr = string.Empty;

        try
        {
            if (string.Equals(runtimeId, "llama.cpp-sycl", StringComparison.OrdinalIgnoreCase))
            {
                var syclPrerequisite = await ProbeSyclPrerequisiteAsync(
                    fullPath,
                    ct).ConfigureAwait(false);
                if (syclPrerequisite is { Usable: false })
                {
                    return BuildResult(
                        runtimeId,
                        fullPath,
                        succeeded: false,
                        timedOut: syclPrerequisite.TimedOut,
                        exitCode: syclPrerequisite.ExitCode,
                        stopwatch: stopwatch,
                        stdout: stdout,
                        diagnostic: syclPrerequisite.Diagnostic);
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "--list-devices",
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return BuildResult(
                    runtimeId,
                    fullPath,
                    succeeded: false,
                    timedOut: false,
                    exitCode: null,
                    stopwatch,
                    stdout,
                    "process_start_failed");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested)
                    throw;
                timedOut = true;
            }

            stdout = await ReadCompletedOutputAsync(stdoutTask).ConfigureAwait(false);
            stderr = await ReadCompletedOutputAsync(stderrTask).ConfigureAwait(false);
            return BuildResult(
                runtimeId,
                fullPath,
                succeeded: !timedOut && exitCode == 0,
                timedOut,
                exitCode,
                stopwatch,
                stdout,
                timedOut && string.IsNullOrWhiteSpace(stderr)
                    ? "runtime_capability_probe_timeout"
                    : stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildResult(
                runtimeId,
                fullPath,
                succeeded: false,
                timedOut,
                exitCode,
                stopwatch,
                stdout,
                ex.GetType().Name);
        }
    }

    // sycl-ls may return exit code 0 while reporting an unavailable Level Zero
    // driver on stderr, so a non-empty device inventory is the success signal.
    internal static string? EvaluateSyclInventory(
        int? exitCode,
        string stdout,
        string stderr)
    {
        if (exitCode is not 0)
            return $"sycl_inventory_exit:{exitCode?.ToString() ?? "unknown"}:{CompactDiagnostic(stderr)}";
        if (!string.IsNullOrWhiteSpace(stdout))
            return null;
        if (!string.IsNullOrWhiteSpace(stderr))
            return "sycl_inventory_failed:" + CompactDiagnostic(stderr);
        return "sycl_device_inventory_empty";
    }

    private static async Task<SyclPrerequisiteResult?> ProbeSyclPrerequisiteAsync(
        string serverExecutablePath,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(serverExecutablePath)
                        ?? AppContext.BaseDirectory;
        var executablePath = Path.Combine(directory, "sycl-ls.exe");
        if (!File.Exists(executablePath))
            return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        try
        {
            if (!process.Start())
            {
                return new SyclPrerequisiteResult(
                    false,
                    false,
                    null,
                    "sycl_inventory_process_start_failed");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested)
                    throw;

                return new SyclPrerequisiteResult(
                    false,
                    true,
                    null,
                    "sycl_inventory_timeout");
            }

            var stdout = await ReadCompletedOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOutputAsync(stderrTask).ConfigureAwait(false);
            var diagnostic = EvaluateSyclInventory(process.ExitCode, stdout, stderr);
            return new SyclPrerequisiteResult(
                diagnostic is null,
                false,
                process.ExitCode,
                diagnostic ?? "sycl_inventory_available");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new SyclPrerequisiteResult(
                false,
                false,
                null,
                "sycl_inventory_exception:" + ex.GetType().Name);
        }
    }

    internal static IReadOnlyList<LocalLlmRuntimeDeviceInfo> ParseDevices(
        string runtimeId,
        string executablePath,
        string stdout,
        string stderr)
    {
        var allLines = EnumerateLines(stdout).Concat(EnumerateLines(stderr)).ToArray();
        var reportedMemoryArchitectures = ParseReportedMemoryArchitectures(runtimeId, allLines);
        var devices = new List<LocalLlmRuntimeDeviceInfo>();
        foreach (var line in allLines)
        {
            var match = RuntimeDeviceLine().Match(line);
            if (!match.Success)
                continue;

            var deviceId = match.Groups["id"].Value;
            var description = match.Groups["description"].Value.Trim();
            var memoryArchitecture = reportedMemoryArchitectures.TryGetValue(deviceId, out var reported)
                ? reported
                : description.Contains("shared", StringComparison.OrdinalIgnoreCase)
                    ? "unified"
                    : "unknown";
            devices.Add(new LocalLlmRuntimeDeviceInfo(
                runtimeId,
                deviceId,
                description,
                executablePath,
                "llama.cpp --list-devices")
            {
                ReportedMemoryMiB = ParseFirstMiB(description),
                ReportedFreeMemoryMiB = ParseFreeMiB(description),
                ReportsSharedMemory = string.Equals(memoryArchitecture, "unified", StringComparison.Ordinal)
                    || description.Contains("shared", StringComparison.OrdinalIgnoreCase),
                MemoryArchitecture = memoryArchitecture
            });
        }

        if (devices.Count == 0
            && string.Equals(runtimeId, "llama.cpp-cpu", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("loaded CPU backend", StringComparison.OrdinalIgnoreCase))
        {
            var backendLine = EnumerateLines(stderr)
                .FirstOrDefault(static line => line.Contains("loaded CPU backend", StringComparison.OrdinalIgnoreCase));
            devices.Add(new LocalLlmRuntimeDeviceInfo(
                runtimeId,
                "CPU",
                string.IsNullOrWhiteSpace(backendLine) ? "CPU backend" : backendLine.Trim(),
                executablePath,
                "llama.cpp backend load")
            {
                MemoryArchitecture = "system"
            });
        }

        return devices;
    }

    private static IReadOnlyDictionary<string, string> ParseReportedMemoryArchitectures(
        string runtimeId,
        IEnumerable<string> lines)
    {
        var architectures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(runtimeId, "llama.cpp-vulkan", StringComparison.OrdinalIgnoreCase))
            return architectures;

        foreach (var line in lines)
        {
            var match = VulkanDeviceDetailsLine().Match(line);
            if (!match.Success)
                continue;

            var deviceId = "Vulkan" + match.Groups["index"].Value;
            architectures[deviceId] = string.Equals(match.Groups["uma"].Value, "1", StringComparison.Ordinal)
                ? "unified"
                : "dedicated";
        }

        return architectures;
    }

    private static (GpuInfo? Gpu, string Source) FindHardwareMatch(
        LocalLlmRuntimeDeviceInfo device,
        IReadOnlyList<GpuInfo> gpuInventory)
    {
        var hinted = gpuInventory
            .Where(gpu => !string.IsNullOrWhiteSpace(gpu.RuntimeDeviceHint)
                          && string.Equals(
                              gpu.RuntimeDeviceHint,
                              device.DeviceId,
                              StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (hinted.Length == 1)
            return (hinted[0], "runtime_device_hint");

        var runtimeName = NormalizeAdapterName(device.Description);
        if (runtimeName.Length == 0)
            return (null, "unmatched");

        var named = gpuInventory
            .Select(gpu => new
            {
                Gpu = gpu,
                Normalized = NormalizeAdapterName(gpu.Name)
            })
            .Where(item => item.Normalized.Length >= 3
                           && (string.Equals(
                                   item.Normalized,
                                   runtimeName,
                                   StringComparison.Ordinal)
                               || runtimeName.Contains(item.Normalized, StringComparison.Ordinal)
                               || item.Normalized.Contains(runtimeName, StringComparison.Ordinal)))
            .OrderByDescending(item => string.Equals(
                item.Normalized,
                runtimeName,
                StringComparison.Ordinal))
            .ThenByDescending(static item => item.Normalized.Length)
            .ToArray();
        return named.Length == 0
            ? (null, "unmatched")
            : (named[0].Gpu, "normalized_adapter_name");
    }

    private static string NormalizeAdapterName(string value)
    {
        var withoutMemory = TrailingRuntimeMemory().Replace(value ?? string.Empty, string.Empty);
        var normalized = NonAlphaNumeric().Replace(withoutMemory.ToUpperInvariant(), string.Empty);
        foreach (var noise in new[]
                 {
                     "NVIDIACORPORATION",
                     "INTELCORPORATION",
                     "ADVANCEDMICRODEVICES",
                     "NVIDIA",
                     "INTEL",
                     "AMD"
                 })
        {
            normalized = normalized.Replace(noise, string.Empty, StringComparison.Ordinal);
        }

        return normalized;
    }

    private static LocalLlmRuntimeCapabilityProbeResult BuildResult(
        string runtimeId,
        string executablePath,
        bool succeeded,
        bool timedOut,
        int? exitCode,
        Stopwatch stopwatch,
        string stdout,
        string diagnostic)
    {
        stopwatch.Stop();
        // A failed probe may expose diagnostics such as
        // "sycl_inventory_exit:..." which deliberately resemble a labelled
        // runtime line. They are evidence about why the backend was rejected,
        // not usable devices and must never enter the qualification inventory.
        var devices = succeeded
            ? ParseDevices(runtimeId, executablePath, stdout, diagnostic)
            : Array.Empty<LocalLlmRuntimeDeviceInfo>();
        return new LocalLlmRuntimeCapabilityProbeResult(
            runtimeId,
            executablePath,
            succeeded,
            timedOut,
            exitCode,
            (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
            devices,
            CompactDiagnostic(diagnostic));
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> EnumerateLines(string text)
        => (text ?? string.Empty).Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CompactDiagnostic(string value)
    {
        var lines = EnumerateLines(value).Take(8);
        var compact = string.Join(" | ", lines);
        return compact.Length <= 600 ? compact : compact[..600];
    }

    private static int? ParseFirstMiB(string value)
    {
        var match = FirstMiB().Match(value);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var parsed)
            ? parsed
            : null;
    }

    private static int? ParseFreeMiB(string value)
    {
        var match = FreeMiB().Match(value);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(
        @"^\s*(?<id>(?:CUDA|Vulkan|ROCm|HIP|SYCL|Metal|GPU|CPU)[A-Za-z0-9_.-]*):\s*(?<description>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeDeviceLine();

    [GeneratedRegex(
        @"(?<value>\d+)\s*MiB\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstMiB();

    [GeneratedRegex(
        @"(?<value>\d+)\s*MiB\s+free\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FreeMiB();

    [GeneratedRegex(
        @"^\s*ggml_vulkan:\s*(?<index>\d+)\s*=.+?\|\s*uma:\s*(?<uma>[01])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VulkanDeviceDetailsLine();

    [GeneratedRegex(
        @"\s*\(\d+\s*MiB(?:,\s*\d+\s*MiB\s+free)?\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRuntimeMemory();

    [GeneratedRegex(
        @"[^A-Z0-9]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();
}
