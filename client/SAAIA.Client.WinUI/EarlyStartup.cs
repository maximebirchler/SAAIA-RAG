using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI;

/// <summary>
/// Runs as early as possible (before App ctor) to register global handlers.
/// Helps capture startup crashes that happen before the window is visible.
/// </summary>
internal static class EarlyStartup
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Do not throw from here.
        try
        {
            ClientLog.RegisterGlobalHandlers();
            ClientLog.Info("ModuleInitializer: early startup initialized");
            var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            ClientLog.Info($"Version: {ver}");
            ClientLog.Info($"OS: {Environment.OSVersion}");
        }
        catch
        {
            // ignore
        }
    }
}
