using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed class LlamaCppProcessManager
{
    private readonly object _gate = new();
    private Process? _proc;
    private Timer? _idleTimer;
    private int _activeRequests;
    private int _idleTimeoutSeconds;
    private string? _attachedBaseUrl;
    private string? _attachedModelId;

    public bool IsRunning => _proc is { HasExited: false } || !string.IsNullOrWhiteSpace(_attachedBaseUrl);

    public string? LastCommandLine { get; private set; }
    public string? LastLogFile { get; private set; }
    public int? LastStartupLoadMs { get; private set; }
    public int IdleTimeoutSeconds => _idleTimeoutSeconds;

    /// <summary>
    /// Stops the managed llama-server process if running.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        DisposeIdleTimer();
        LastStartupLoadMs = null;
        _attachedBaseUrl = null;
        _attachedModelId = null;
        if (_proc is null) return;

        try
        {
            if (!_proc.HasExited)
            {
                try { _proc.Kill(entireProcessTree: true); }
                catch { try { _proc.Kill(); } catch { } }
            }
        }
        finally
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }

    /// <summary>
    /// Starts llama-server using AppSettings (LlamaExePath + ModelPath + Host/Port + ExtraArgs),
    /// then waits for /v1/models to become ready.
    /// </summary>
    internal async Task<(bool ok, string message)> StartAsync(AppSettings s, CancellationToken ct)
    {
        var exePath = (s.LlamaExePath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(exePath))
            return (false, "Missing LLM runtime path (LlamaExePath).");

        if (!File.Exists(exePath))
            return (false, $"LLM runtime not found: {exePath}");

        var modelPath = (s.ModelPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modelPath))
            return (false, "Missing model path (ModelPath).");

        if (!File.Exists(modelPath))
            return (false, $"Model not found: {modelPath}");

        var modelIntegrity = await ModelIntegrityService.VerifyModelAsync(s, ct: ct).ConfigureAwait(false);
        if (modelIntegrity.Blocked)
            return (false, modelIntegrity.UserMessage ?? "Model integrity verification failed.");

        if (s.QualifiedProfile is not null)
        {
            var blacklistMatch = await BlacklistPolicy.FindMatchAsync(
                s.QualifiedProfile,
                driverVersion: null,
                ct: ct).ConfigureAwait(false);
            if (blacklistMatch is not null)
            {
                return (false, $"LLM profile is blacklisted ({blacklistMatch.RuleId}): {blacklistMatch.Reason}");
            }
        }

        _idleTimeoutSeconds = await ResolveIdleTimeoutSecondsAsync(s, ct: ct).ConfigureAwait(false);
        var args = BuildArgs(s);
        return await StartAsync(exePath, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts llama-server with explicit exePath + args, then waits for /v1/models.
    /// This overload is useful for bootstrap/autotune.
    /// </summary>
    internal async Task<(bool ok, string message)> StartAsync(string exePath, string args, CancellationToken ct)
    {
        // Stop any previous instance we manage
        Stop();
        LastStartupLoadMs = null;

        var host = "127.0.0.1";
        var port = 1234;
        string? modelPath = null;

        // Best-effort parse host/port from args (if present)
        // If not present, defaults above are OK for readiness check.
        try
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "--host" && i + 1 < parts.Length) host = parts[i + 1];
                if (parts[i] == "--port" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var p)) port = p;
                if (parts[i] == "--model" && i + 1 < parts.Length) modelPath = parts[i + 1].Trim('"');
            }
        }
        catch { /* ignore */ }

        var baseUrl = $"http://{host}:{port}";
        var expectedModelName = string.IsNullOrWhiteSpace(modelPath) ? null : Path.GetFileName(modelPath);
        var attachProbe = await TryAttachToExistingServerAsync(baseUrl, expectedModelName, ct).ConfigureAwait(false);
        if (attachProbe.Status == ExistingServerProbeStatus.Attached)
        {
            _attachedBaseUrl = baseUrl;
            _attachedModelId = attachProbe.ModelId;
            LastLogFile = null;
            LastCommandLine = $"Attached to existing llama-server at {baseUrl} ({attachProbe.ModelId ?? "model unknown"})";
            LastStartupLoadMs = 0;
            ClientLog.Info($"[LlamaCpp] {LastCommandLine}");
            ScheduleIdleStop();
            return (true, $"LLM already ready at {baseUrl}.");
        }

        if (attachProbe.Status == ExistingServerProbeStatus.WrongModel)
        {
            return (false, attachProbe.Message ?? $"Port {port} is already used by another llama-server model.");
        }

        StopMatchingRuntimeProcesses(exePath);

        var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "logs");
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, $"llama-server_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };

        try
        {
            var startupSw = Stopwatch.StartNew();
            _proc = Process.Start(psi);
            if (_proc is null)
                return (false, "Failed to start llama-server process.");

            LastLogFile = logPath;
            LastCommandLine = $"\"{exePath}\" {args}";
            ClientLog.Info($"[LlamaCpp] Starting: {LastCommandLine}");

            _ = PipeToFileAsync(_proc, logPath, ct);

            // Wait for /v1/models
            var ok = await WaitModelsReadyAsync(
                baseUrl,
                timeoutSeconds: 120,
                ct,
                shouldAbort: () => _proc is { HasExited: true }).ConfigureAwait(false);
            startupSw.Stop();
            LastStartupLoadMs = (int)Math.Min(int.MaxValue, startupSw.ElapsedMilliseconds);

            if (!ok)
            {
                var exited = _proc is { HasExited: true };
                var exitCode = exited ? _proc?.ExitCode.ToString() : null;
                try { Stop(); } catch { }
                if (exited)
                    return (false, $"llama-server exited before readiness (exit code {exitCode ?? "unknown"}). See log: {logPath}");
                return (false, $"Started but /v1/models did not become ready within timeout. See log: {logPath}");
            }

            ScheduleIdleStop();

            return (true, $"LLM ready at {baseUrl} (log: {logPath})");
        }
        catch (Exception ex)
        {
            try { Stop(); } catch { }
            return (false, "Start failed: " + ex.Message);
        }
    }

    private enum ExistingServerProbeStatus
    {
        NotAvailable,
        Attached,
        WrongModel
    }

    private sealed record ExistingServerProbe(
        ExistingServerProbeStatus Status,
        string? Message = null,
        string? ModelId = null);

    private static async Task<ExistingServerProbe> TryAttachToExistingServerAsync(
        string baseUrl,
        string? expectedModelName,
        CancellationToken ct)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        var url = baseUrl.TrimEnd('/') + "/v1/models";
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200)
                return new ExistingServerProbe(ExistingServerProbeStatus.NotAvailable);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ids = ReadModelIds(json).ToArray();
            if (ExistingModelListContainsExpected(json, expectedModelName))
            {
                var modelId = ids.FirstOrDefault()
                    ?? (string.IsNullOrWhiteSpace(expectedModelName) ? null : expectedModelName);
                return new ExistingServerProbe(ExistingServerProbeStatus.Attached, ModelId: modelId);
            }

            var actual = ids.Length > 0 ? string.Join(", ", ids) : "unknown model";
            return new ExistingServerProbe(
                ExistingServerProbeStatus.WrongModel,
                $"A llama-server is already listening at {baseUrl}, but it serves {actual} instead of {expectedModelName ?? "the configured model"}.");
        }
        catch
        {
            return new ExistingServerProbe(ExistingServerProbeStatus.NotAvailable);
        }
    }

    internal static bool ExistingModelListContainsExpected(string modelsJson, string? expectedModelName)
    {
        var ids = ReadModelIds(modelsJson).ToArray();
        if (ids.Length == 0)
            return false;

        if (string.IsNullOrWhiteSpace(expectedModelName))
            return true;

        return ids.Any(id =>
            string.Equals(id, expectedModelName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(id), expectedModelName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadModelIds(string modelsJson)
    {
        using var doc = JsonDocument.Parse(modelsJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (TryGetStringProperty(item, "id", out var id))
                    yield return id;
                else if (TryGetStringProperty(item, "model", out var model))
                    yield return model;
            }
        }

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                if (TryGetStringProperty(item, "model", out var model))
                    yield return model;
                else if (TryGetStringProperty(item, "name", out var name))
                    yield return name;
            }
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void StopMatchingRuntimeProcesses(string exePath)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(processName))
            return;

        var targetPath = Path.GetFullPath(exePath);
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                if (proc.Id == Environment.ProcessId || proc.HasExited)
                    continue;

                var modulePath = proc.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(modulePath)
                    || !string.Equals(Path.GetFullPath(modulePath), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ClientLog.Warn($"[LlamaCpp] Stopping stale runtime process pid={proc.Id}: {modulePath}");
                try { proc.Kill(entireProcessTree: true); }
                catch { proc.Kill(); }

                if (!proc.WaitForExit(3000))
                    ClientLog.Warn($"[LlamaCpp] Stale runtime process pid={proc.Id} did not exit within 3s.");
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"[LlamaCpp] Could not inspect/stop stale runtime process pid={SafeProcessId(proc)}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    private static string SafeProcessId(Process proc)
    {
        try { return proc.Id.ToString(); }
        catch { return "unknown"; }
    }

    public void NotifyActivityStart()
    {
        lock (_gate)
        {
            _activeRequests++;
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void NotifyActivityFinished()
    {
        lock (_gate)
        {
            if (_activeRequests > 0)
                _activeRequests--;
        }

        ScheduleIdleStop();
    }

    internal static async Task<int> ResolveIdleTimeoutSecondsAsync(
        AppSettings settings,
        string? root = null,
        CancellationToken ct = default)
    {
        var fallback = Math.Max(30, settings.StartupTimeoutSeconds);
        if (settings.QualifiedProfile is null)
            return fallback;

        var profileRead = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
            GovernanceArtifactStore.WarmupProfilesFile,
            root,
            ct).ConfigureAwait(false);

        var profileIdleTimeout = profileRead.Status == GovernanceArtifactReadStatus.Ok && profileRead.Value is not null
            ? profileRead.Value.Items
                .FirstOrDefault(item => string.Equals(item.ProfileId, settings.QualifiedProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
                ?.Thresholds.IdleTimeoutSeconds
            : null;

        var batteryDecision = await BatteryPolicyStore.EvaluateAsync(settings.QualifiedProfile, root, ct).ConfigureAwait(false);
        return batteryDecision.EffectiveIdleTimeoutSeconds
            ?? profileIdleTimeout
            ?? fallback;
    }

    private void ScheduleIdleStop()
    {
        lock (_gate)
        {
            if (_activeRequests > 0 || !IsRunning || _idleTimeoutSeconds <= 0)
                return;

            _idleTimer ??= new Timer(_ => OnIdleTimeout(), null, Timeout.Infinite, Timeout.Infinite);
            _idleTimer.Change(TimeSpan.FromSeconds(_idleTimeoutSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimeout()
    {
        lock (_gate)
        {
            if (_activeRequests > 0 || !IsRunning)
                return;
        }

        ClientLog.Info($"[LlamaCpp] Idle timeout reached ({_idleTimeoutSeconds}s) - stopping managed runtime.");
        Stop();
    }

    private void DisposeIdleTimer()
    {
        lock (_gate)
        {
            try { _idleTimer?.Dispose(); } catch { }
            _idleTimer = null;
            _activeRequests = 0;
        }
    }

    internal static string BuildArgs(AppSettings s)
    {
        var host = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim();
        var port = s.Port <= 0 ? 1234 : s.Port;

        var model = (s.ModelPath ?? "").Trim();

        var baseArgs = $"--host {host} --port {port} --model \"{model}\"";

        var extra = NormalizeExtraArgsForQualifiedProfile(s, (s.ExtraArgs ?? "").Trim());
        if (extra.Length > 0)
            baseArgs += " " + extra;

        return baseArgs;
    }

    private static string NormalizeExtraArgsForQualifiedProfile(AppSettings settings, string extra)
    {
        var profile = GetApplicableQualifiedProfile(settings);
        if (profile is null)
            return extra;

        extra = RemoveArg(extra, "--ctx-size", 1);
        extra = RemoveArg(extra, "-c", 1);
        extra = RemoveArg(extra, "-t", 1);
        extra = RemoveArg(extra, "--threads", 1);
        extra = RemoveArg(extra, "-b", 1);
        extra = RemoveArg(extra, "--batch", 1);
        extra = RemoveArg(extra, "--batch-size", 1);
        extra = RemoveArg(extra, "-ngl", 1);
        extra = RemoveArg(extra, "--n-gpu-layers", 1);
        extra = RemoveArg(extra, "--ubatch-size", 1);
        extra = RemoveArg(extra, "-ub", 1);
        extra = RemoveArg(extra, "--threads-batch", 1);
        extra = RemoveArg(extra, "-tb", 1);
        extra = RemoveArg(extra, "--flash-attn", 1);
        extra = RemoveArg(extra, "-fa", 1);
        extra = RemoveArg(extra, "--mlock", 0);

        extra = AppendArg(extra, "--ctx-size", profile.CtxSize.ToString());
        extra = AppendArg(extra, "-t", profile.Threads.ToString());
        extra = AppendArg(extra, "-b", profile.BatchSize.ToString());
        if (!string.Equals(profile.Runtime, "llama.cpp-cpu", StringComparison.OrdinalIgnoreCase))
            extra = AppendArg(extra, "-ngl", profile.Ngl.ToString());
        extra = AppendArg(extra, "--ubatch-size", profile.UbatchSize.ToString());
        extra = AppendArg(extra, "--threads-batch", profile.ThreadsBatch.ToString());
        extra = AppendArg(extra, "--flash-attn", profile.FlashAttn ? "on" : "off");
        if (profile.Mlock)
            extra = AppendFlag(extra, "--mlock");

        return extra.Trim();
    }

    private static QualifiedProfile? GetApplicableQualifiedProfile(AppSettings settings)
    {
        var profile = settings.QualifiedProfile;
        if (profile is null)
            return null;

        var runtime = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        if (!string.Equals(runtime, profile.Runtime, StringComparison.OrdinalIgnoreCase))
            return null;

        var currentModelId =
            ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(
                string.IsNullOrWhiteSpace(settings.ModelPath) ? null : Path.GetFileName(settings.ModelPath))
            ?? settings.ModelId;

        return string.Equals(currentModelId, profile.ModelId, StringComparison.OrdinalIgnoreCase)
            ? profile
            : null;
    }

    private static string RemoveArg(string args, string key, int valueCount)
    {
        if (string.IsNullOrWhiteSpace(args))
            return string.Empty;

        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i], key, StringComparison.OrdinalIgnoreCase))
                continue;

            tokens.RemoveAt(i);
            for (var removed = 0; removed < valueCount && i < tokens.Count; removed++)
                tokens.RemoveAt(i);
            i--;
        }

        return string.Join(" ", tokens);
    }

    private static string AppendArg(string extra, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return $"{key} {value}";
        return extra + " " + key + " " + value;
    }

    private static string AppendFlag(string extra, string key)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return key;
        return extra + " " + key;
    }

    private static async Task PipeToFileAsync(Process proc, string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await using var sw = new StreamWriter(fs) { AutoFlush = true };

            // Stdout
            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    while (!proc.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break;
                        await sw.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
                catch { }
            }, ct);

            // Stderr
            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    while (!proc.StandardError.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break;
                        await sw.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
                catch { }
            }, ct);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static async Task<bool> WaitModelsReadyAsync(
        string baseUrl,
        int timeoutSeconds,
        CancellationToken ct,
        Func<bool>? shouldAbort = null)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var url = baseUrl.TrimEnd('/') + "/v1/models";

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (shouldAbort?.Invoke() == true)
                return false;

            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 200) return true;

                // 503 while loading is normal: keep waiting
                await Task.Delay(800, ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException)
            {
                // request timeout -> keep waiting
            }
            catch
            {
                // refused / down -> keep waiting a bit
            }

            try { await Task.Delay(900, ct).ConfigureAwait(false); } catch { }
        }

        return false;
    }
}
