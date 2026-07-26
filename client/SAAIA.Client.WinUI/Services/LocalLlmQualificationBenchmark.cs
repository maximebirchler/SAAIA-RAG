using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmQualificationBenchmarkScenario(
    string ScenarioId,
    int PromptTokens,
    int GenerationTokens,
    double Weight = 1d);

internal sealed record LocalLlmQualificationBenchmarkResult(
    string CandidateId,
    string ScenarioId,
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    int DurationMs,
    double? PromptTokensPerSecond,
    double? GenerationTokensPerSecond,
    string Diagnostic);

internal sealed record LocalLlmQualificationCandidateScore(
    LocalLlmQualificationCandidate Candidate,
    bool Eligible,
    double? ProjectedWorkloadSeconds,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<LocalLlmQualificationBenchmarkResult> Measurements);

internal static class LocalLlmQualificationBenchmarkRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public static async Task<LocalLlmQualificationBenchmarkResult> RunAsync(
        LocalLlmQualificationCandidate candidate,
        string modelPath,
        LocalLlmQualificationBenchmarkScenario scenario,
        int repetitions = 1,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(scenario);

        var benchmarkPath = ResolveBenchmarkPath(candidate.RuntimeExecutablePath);
        if (!File.Exists(benchmarkPath))
        {
            return Failure(
                candidate,
                scenario,
                timedOut: false,
                exitCode: null,
                durationMs: 0,
                "llama_bench_missing");
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = BuildStartInfo(
                benchmarkPath,
                modelPath,
                candidate.Profile,
                scenario,
                repetitions)
        };

        try
        {
            if (!process.Start())
            {
                return Failure(
                    candidate,
                    scenario,
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
                    candidate,
                    scenario,
                    timedOut: true,
                    exitCode: TryGetExitCode(process),
                    durationMs: ElapsedMilliseconds(stopwatch),
                    "benchmark_timeout");
            }

            var stdout = await ReadCompletedOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOutputAsync(stderrTask).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                return Failure(
                    candidate,
                    scenario,
                    timedOut: false,
                    exitCode,
                    ElapsedMilliseconds(stopwatch),
                    CompactDiagnostic(stderr));
            }

            if (!TryParseMeasurements(
                    stdout,
                    scenario,
                    out var promptTokensPerSecond,
                    out var generationTokensPerSecond,
                    out var parseError))
            {
                return Failure(
                    candidate,
                    scenario,
                    timedOut: false,
                    exitCode,
                    ElapsedMilliseconds(stopwatch),
                    parseError);
            }

            return new LocalLlmQualificationBenchmarkResult(
                candidate.CandidateId,
                scenario.ScenarioId,
                Succeeded: true,
                TimedOut: false,
                exitCode,
                ElapsedMilliseconds(stopwatch),
                promptTokensPerSecond,
                generationTokensPerSecond,
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
                candidate,
                scenario,
                timedOut: false,
                exitCode: TryGetExitCode(process),
                durationMs: ElapsedMilliseconds(stopwatch),
                ex.GetType().Name);
        }
    }

    internal static string ResolveBenchmarkPath(string serverExecutablePath)
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(serverExecutablePath)) ?? string.Empty,
            "llama-bench.exe");

    internal static ProcessStartInfo BuildStartInfo(
        string benchmarkPath,
        string modelPath,
        QualifiedProfile profile,
        LocalLlmQualificationBenchmarkScenario scenario,
        int repetitions)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(benchmarkPath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(benchmarkPath)) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Add(startInfo, "-m", Path.GetFullPath(modelPath));
        Add(startInfo, "-p", Math.Clamp(scenario.PromptTokens, 1, profile.CtxSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-n", Math.Clamp(scenario.GenerationTokens, 1, profile.CtxSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-r", Math.Clamp(repetitions, 1, 10).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-b", Math.Max(1, profile.BatchSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-ub", Math.Max(1, profile.UbatchSize).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-t", Math.Max(1, profile.Threads).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-ngl", Math.Max(0, profile.Ngl).ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "-fa", profile.FlashAttn ? "on" : "off");
        Add(startInfo, "-ctk", NormalizeCacheType(profile.CacheTypeK));
        Add(startInfo, "-ctv", NormalizeCacheType(profile.CacheTypeV));
        Add(startInfo, "-sm", NormalizeSplitMode(profile.SplitMode));
        Add(startInfo, "-mg", Math.Max(0, profile.MainGpu).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--no-warmup");
        Add(startInfo, "-o", "json");

        var deviceIds = profile.DeviceIds
            .Where(static id => !string.IsNullOrWhiteSpace(id)
                                && !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (deviceIds.Length > 0)
            Add(startInfo, "-dev", string.Join('/', deviceIds));

        if (profile.TensorSplit.Count > 0)
        {
            Add(
                startInfo,
                "-ts",
                string.Join(
                    '/',
                    profile.TensorSplit.Select(static value =>
                        Math.Max(0d, value).ToString("0.######", CultureInfo.InvariantCulture))));
        }

        return startInfo;
    }

    internal static bool TryParseMeasurements(
        string json,
        LocalLlmQualificationBenchmarkScenario scenario,
        out double promptTokensPerSecond,
        out double generationTokensPerSecond,
        out string error)
    {
        promptTokensPerSecond = 0;
        generationTokensPerSecond = 0;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "benchmark_json_not_array";
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var prompt = ReadInt(item, "n_prompt");
                var generation = ReadInt(item, "n_gen");
                var tokensPerSecond = ReadDouble(item, "avg_ts");
                if (tokensPerSecond is null or <= 0)
                    continue;

                if (prompt == scenario.PromptTokens && generation == 0)
                    promptTokensPerSecond = tokensPerSecond.Value;
                if (prompt == 0 && generation == scenario.GenerationTokens)
                    generationTokensPerSecond = tokensPerSecond.Value;
            }

            if (promptTokensPerSecond <= 0 || generationTokensPerSecond <= 0)
            {
                error = $"benchmark_measurement_missing:prompt={promptTokensPerSecond:0.###};generation={generationTokensPerSecond:0.###}";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "benchmark_json_invalid";
            return false;
        }
    }

    private static void Add(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static int? ReadInt(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static double? ReadDouble(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;

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

    private static LocalLlmQualificationBenchmarkResult Failure(
        LocalLlmQualificationCandidate candidate,
        LocalLlmQualificationBenchmarkScenario scenario,
        bool timedOut,
        int? exitCode,
        int durationMs,
        string diagnostic)
        => new(
            candidate.CandidateId,
            scenario.ScenarioId,
            Succeeded: false,
            timedOut,
            exitCode,
            durationMs,
            PromptTokensPerSecond: null,
            GenerationTokensPerSecond: null,
            CompactDiagnostic(diagnostic));
}

internal static class LocalLlmQualificationBenchmarkRanker
{
    public static IReadOnlyList<LocalLlmQualificationCandidateScore> Rank(
        IReadOnlyList<LocalLlmQualificationCandidate> candidates,
        IReadOnlyList<LocalLlmQualificationBenchmarkResult> measurements,
        IReadOnlyList<LocalLlmQualificationBenchmarkScenario> workload)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(workload);
        if (workload.Count == 0)
            throw new ArgumentException("At least one benchmark scenario is required.", nameof(workload));

        var scores = new List<LocalLlmQualificationCandidateScore>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var candidateMeasurements = measurements
                .Where(measurement => string.Equals(
                    measurement.CandidateId,
                    candidate.CandidateId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var byScenario = candidateMeasurements
                .GroupBy(static measurement => measurement.ScenarioId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            var reasons = new List<string>();
            double projectedSeconds = 0;
            foreach (var scenario in workload)
            {
                if (!byScenario.TryGetValue(scenario.ScenarioId, out var measurement))
                {
                    reasons.Add($"measurement_missing:{scenario.ScenarioId}");
                    continue;
                }

                if (!measurement.Succeeded
                    || measurement.PromptTokensPerSecond is not > 0
                    || measurement.GenerationTokensPerSecond is not > 0)
                {
                    reasons.Add($"measurement_failed:{scenario.ScenarioId}");
                    continue;
                }

                projectedSeconds += scenario.Weight
                    * ((scenario.PromptTokens / measurement.PromptTokensPerSecond.Value)
                       + (scenario.GenerationTokens / measurement.GenerationTokensPerSecond.Value));
            }

            var eligible = reasons.Count == 0;
            scores.Add(new LocalLlmQualificationCandidateScore(
                candidate,
                eligible,
                eligible ? projectedSeconds : null,
                reasons.Count == 0 ? new[] { "all_required_measurements_passed" } : reasons,
                candidateMeasurements));
        }

        return scores
            .OrderByDescending(static score => score.Eligible)
            .ThenBy(static score => score.ProjectedWorkloadSeconds ?? double.MaxValue)
            .ThenBy(static score => score.Candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }
}
