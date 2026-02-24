using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Runs the IT/integrator-provided PowerShell installer that downloads the GGUF model
/// and starts the LLM docker stack. Intended for "Option B" (on-prem) deployments.
/// 
/// Important:
/// - The script typically writes to C:\SAAIA and starts Docker. It may require elevation.
/// - We cannot capture stdout/stderr when using Verb=runas (UAC). We only wait for exit.
/// </summary>
internal static class LlmRepairScriptRunner
{
    private const string DefaultInstallRoot = @"C:\SAAIA";

    public static string? FindInstallScriptPath(string? overridePath = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(overridePath))
            candidates.Add(overridePath);

        // Most stable production locations
        candidates.Add(Path.Combine(DefaultInstallRoot, "deploy", "install-llm.ps1"));
        candidates.Add(Path.Combine(DefaultInstallRoot, "install-llm.ps1"));

        // ProgramData (installer can copy there)
        try
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            candidates.Add(Path.Combine(pd, "SAAIA", "install-llm.ps1"));
        }
        catch { }

        // Dev/source checkout: search upwards from app base dir.
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var p = Path.Combine(dir.FullName, "infra", "scripts", "llm", "install-llm.ps1");
                candidates.Add(p);
                dir = dir.Parent;
            }
        }
        catch { }

        foreach (var p in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return p;
            }
            catch { }
        }

        return null;
    }

    public static async Task<bool> RunElevatedAsync(string scriptPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };

            var proc = Process.Start(psi);
            if (proc is null) return false;

            while (!proc.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) { return false; }
        catch
        {
            // UAC rejected or process failed to start.
            return false;
        }
    }
}
