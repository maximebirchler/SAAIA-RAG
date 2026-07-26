using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Minimal file logger for startup/support diagnostics.
/// Writes to %LOCALAPPDATA%\SAAIA\logs\client_startup.log
/// </summary>
internal static class ClientLog
{
    private const long MaximumLogBytes = 10L * 1024 * 1024;
    private const int RetainedLogCount = 3;
    private static readonly object _lock = new();
    private static string? _logDir;
    private static int _handlersRegistered;

    public static string LogDirectory
    {
        get
        {
            _logDir ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAAIA",
                "logs");
            return _logDir;
        }
    }

    public static string StartupLogPath => Path.Combine(LogDirectory, "client_startup.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{context}] {ex.GetType().FullName}: {ex.Message}");
        sb.AppendLine(ex.ToString());
        Write("EX", sb.ToString());
    }

    public static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
            lock (_lock)
            {
                RotateIfNeeded(StartupLogPath, MaximumLogBytes, RetainedLogCount);
                File.AppendAllText(StartupLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            Debug.WriteLine(line);
        }
        catch
        {
            // Never throw from logger.
        }
    }

    internal static void RotateIfNeeded(string logPath, long maximumBytes, int retainedLogCount)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (retainedLogCount < 0)
            throw new ArgumentOutOfRangeException(nameof(retainedLogCount));
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < maximumBytes)
            return;

        if (retainedLogCount == 0)
        {
            File.Delete(logPath);
            return;
        }

        var oldest = logPath + "." + retainedLogCount;
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var index = retainedLogCount - 1; index >= 1; index--)
        {
            var source = logPath + "." + index;
            if (!File.Exists(source))
                continue;

            File.Move(source, logPath + "." + (index + 1));
        }

        File.Move(logPath, logPath + ".1");
    }

    /// <summary>
    /// Registers global exception handlers once per process.
    /// Safe to call multiple times.
    /// </summary>
    public static void RegisterGlobalHandlers()
    {
        if (Interlocked.Exchange(ref _handlersRegistered, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    Exception("AppDomain.UnhandledException", ex);
                else
                    Error($"AppDomain.UnhandledException: {e.ExceptionObject}");
            }
            catch { /* ignore */ }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Exception("TaskScheduler.UnobservedTaskException", e.Exception); }
            catch { /* ignore */ }
        };
    }
}
