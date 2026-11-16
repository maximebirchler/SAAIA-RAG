using System.Diagnostics;
using System.Runtime.InteropServices;

try
{
    var installation = AppContext.BaseDirectory;
    var client = Path.Combine(installation, "Client", "SAAIA.Client.WinUI.exe");
    var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SAAIA", "presentation", "profile");
    if (!File.Exists(client) || !File.Exists(Path.Combine(profile, "client", "settings.json")))
        throw new InvalidOperationException("Installation incomplete. Lancez Installer-SAAIA.ps1 depuis l'archive.");

    var start = new ProcessStartInfo(client)
    {
        WorkingDirectory = Path.GetDirectoryName(client)!,
        UseShellExecute = false
    };
    foreach (var name in start.Environment.Keys.ToArray())
    {
        if (name.StartsWith("SAAIA_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RUNPOD_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AZURE_OPENAI_", StringComparison.OrdinalIgnoreCase))
            start.Environment.Remove(name);
    }
    start.Environment["SAAIA_CLIENT_DATA_ROOT"] = profile;
    start.Environment["SAAIA_LLM_PROVIDER_MODE"] = "Local";
    start.Environment["SAAIA_LLM_EXTERNAL_POLICY"] = "ProductionLocal";
    start.Environment["SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_TOKENS"] = "24000";
    start.Environment["SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_ELAPSED_MS"] = "600000";
    using var process = Process.Start(start)
        ?? throw new InvalidOperationException("Le client Windows n'a pas démarré.");
}
catch (Exception error)
{
    MessageBox(IntPtr.Zero, error.Message, "SAAIA — Présentation", 0x10);
    Environment.ExitCode = 1;
}

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
