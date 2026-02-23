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

    public async Task<(bool ok, string message)> StartAsync(AppSettings s, CancellationToken ct)
    {
        if (!s.UseLocalLlm)
            return (false, "Local LLM is disabled.");

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
            var timeout = TimeSpan.FromSeconds(Math.Max(5, s.StartupTimeoutSeconds));
            var ok = await WaitReadyAsync(s.LlmBaseUrl, timeout, ct);
            if (!ok)
                return (false, $"Started but did not become ready within {timeout.TotalSeconds:0}s. See logs: {LastLogFile}");

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
        // llama.cpp server arguments (common)
        // NOTE: user extra args are appended last so they can override defaults if needed.
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

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
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

    private static async Task<bool> WaitReadyAsync(string llmBaseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Try /v1/models then fallback to /health on root (varies by build)
        var modelsUrl = llmBaseUrl.TrimEnd('/') + "/models";
        var root = llmBaseUrl.Replace("/v1", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var healthUrl = root + "/health";

        while (DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            if (await Is200Async(http, modelsUrl, ct).ConfigureAwait(false)) return true;
            if (await Is200Async(http, healthUrl, ct).ConfigureAwait(false)) return true;

            await Task.Delay(600, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> Is200Async(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return (int)resp.StatusCode >= 200 && (int)resp.StatusCode <= 299;
        }
        catch
        {
            return false;
        }
    }
}
