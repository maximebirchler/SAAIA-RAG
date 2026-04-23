using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed class LlamaCppProcessManager
{
    private Process? _proc;

    public bool IsRunning => _proc is { HasExited: false };

    public string? LastCommandLine { get; private set; }
    public string? LastLogFile { get; private set; }

    /// <summary>
    /// Stops the managed llama-server process if running.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
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

        var host = "127.0.0.1";
        var port = 1234;

        // Best-effort parse host/port from args (if present)
        // If not present, defaults above are OK for readiness check.
        try
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "--host" && i + 1 < parts.Length) host = parts[i + 1];
                if (parts[i] == "--port" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var p)) port = p;
            }
        }
        catch { /* ignore */ }

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
            _proc = Process.Start(psi);
            if (_proc is null)
                return (false, "Failed to start llama-server process.");

            LastLogFile = logPath;
            LastCommandLine = $"\"{exePath}\" {args}";
            ClientLog.Info($"[LlamaCpp] Starting: {LastCommandLine}");

            _ = PipeToFileAsync(_proc, logPath, ct);

            // Wait for /v1/models
            var timeout = Math.Max(5,  (new AppSettings()).StartupTimeoutSeconds); // default fallback if caller doesn't set
            // If args include a known port/host, use that.
            var baseUrl = $"http://{host}:{port}";
            var ok = await WaitModelsReadyAsync(baseUrl, timeoutSeconds: 120, ct).ConfigureAwait(false);

            if (!ok)
            {
                try { Stop(); } catch { }
                return (false, $"Started but /v1/models did not become ready within timeout. See log: {logPath}");
            }

            return (true, $"LLM ready at {baseUrl} (log: {logPath})");
        }
        catch (Exception ex)
        {
            try { Stop(); } catch { }
            return (false, "Start failed: " + ex.Message);
        }
    }

    private static string BuildArgs(AppSettings s)
    {
        var host = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim();
        var port = s.Port <= 0 ? 1234 : s.Port;

        var model = (s.ModelPath ?? "").Trim();

        var baseArgs = $"--host {host} --port {port} --model \"{model}\"";

        var extra = (s.ExtraArgs ?? "").Trim();
        if (extra.Length > 0)
            baseArgs += " " + extra;

        return baseArgs;
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

    private static async Task<bool> WaitModelsReadyAsync(string baseUrl, int timeoutSeconds, CancellationToken ct)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var url = baseUrl.TrimEnd('/') + "/v1/models";

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
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
