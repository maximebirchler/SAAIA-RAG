using System.Collections.Concurrent;
using System.Diagnostics;

internal static class RuntimeToolVersionProbe
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Resolve(string command, string arguments = "--version")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var key = $"{command}\n{arguments}";
        return Cache.GetOrAdd(key, _ => Probe(command, arguments));
    }

    private static string Probe(string command, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new()
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
                return "unavailable";

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return "timeout";
            }

            var line = (stdout + "\n" + stderr)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
                return $"exit_{process.ExitCode}";
            return line.Length <= 160 ? line : line[..160];
        }
        catch
        {
            return "unavailable";
        }
    }
}
