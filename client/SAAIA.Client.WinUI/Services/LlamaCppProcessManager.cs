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

    internal async Task<(bool ok, string message)> StartAsync(AppSettings s, CancellationToken ct)
    {
        if (!s.UseLocalLlm)
            return (false, "LLM is disabled (search-only mode). ");

        if (!s.ManageLocalLlmProcess)
            return (false, "Local LLM process management is disabled.");

        if (IsRunning)
            return (true, "Already running.");

        if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
            return (false, "Server executable not found (llama.cpp).");

        if (string.IsNullOrWhiteSpace(s.ModelPath) || !File.Exists(s.ModelPath))
            return (false, "Model file not found (.gguf).");

        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SAAIA", "logs");
        Directory.CreateDirectory(logsDir);

        LastLogFile = Path.Combine(logsDir, $"llama-server_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        var args = BuildArgs(s);
        LastCommandLine = $"\"{s.LlamaExePath}\" {args}";

        var psi = new ProcessStartInfo
        {
            FileName = s.LlamaExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(s.LlamaExePath) ?? Environment.CurrentDirectory
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!_proc.Start())
                return (false, "Failed to start process.");

            // async log piping
            _ = PipeToFileAsync(_proc.StandardOutput, LastLogFile!, ct);
            _ = PipeToFileAsync(_proc.StandardError, LastLogFile!, ct);

            // readiness loop
            // IMPORTANT: readiness = /v1/models returns 200 (not /health), to avoid "ready" while the model is still loading.
            var timeout = TimeSpan.FromSeconds(Math.Max(90, s.StartupTimeoutSeconds));
            var ok = await WaitModelsReadyAsync(s.LlmBaseUrl, timeout, ct);
            if (!ok)
            {
                // Avoid leaving a stray llama.cpp process running when readiness fails.
                try { Stop(); } catch { }
                return (false, $"Started but /v1/models did not become ready within {timeout.TotalSeconds:0}s. See logs: {LastLogFile}");
            }

            return (true, "Ready.");
        }
        catch (Exception ex)
        {
            try { Stop(); } catch { }
            return (false, "Start failed: " + ex.Message);
        }
    }

    public void Stop()
    {
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
            {
                try { _proc.Kill(entireProcessTree: true); }
                catch { _proc.Kill(); }
            }
        }
        finally
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }

    private static string BuildArgs(AppSettings s)
    {
        var host = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim();
        var port = s.Port <= 0 ? 1234 : s.Port;

        var baseArgs = $"--host {host} --port {port} --model \"{s.ModelPath}\"";

        var extra = (s.ExtraArgs ?? "").Trim();
        if (extra.Length > 0)
            baseArgs += " " + extra;

        return baseArgs;
    }

    private static async Task PipeToFileAsync(StreamReader reader, string filePath, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs) { AutoFlush = true };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                await sw.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static async Task<bool> WaitModelsReadyAsync(string llmBaseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        var modelsUrl = llmBaseUrl.TrimEnd('/') + "/models";

        while (DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

                // 200 => ready
                if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode <= 299)
                    return true;

                // 503 while loading => keep waiting (not ready yet)
                if ((int)resp.StatusCode == 503)
                {
                    await Task.Delay(650, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch
            {
                // keep waiting
            }

            await Task.Delay(650, ct).ConfigureAwait(false);
        }

        return false;
    }
}
