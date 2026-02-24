using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal static class LlmInstallScriptRunner
{
    public static async Task<(bool Ok, string? Error)> RunElevatedAsync(string scriptPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return (false, "Missing script path.");

        if (!File.Exists(scriptPath))
            return (false, "Script not found: " + scriptPath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
            };

            using var p = Process.Start(psi);
            if (p is null) return (false, "Failed to start installer process.");

            // Cannot truly cancel an elevated external process; we just stop waiting.
            while (!p.HasExited)
            {
                if (ct.IsCancellationRequested) return (false, "Cancelled.");
                await Task.Delay(300, CancellationToken.None);
            }

            if (p.ExitCode == 0) return (true, null);
            return (false, "Installer exit code: " + p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            // The operation was canceled by the user.
            return (false, "Cancelled by user.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
