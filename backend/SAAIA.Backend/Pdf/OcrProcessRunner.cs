using System.Diagnostics;
using System.Text;

internal sealed record OcrProcessResult(bool Succeeded, int? ExitCode = null,
    bool TimedOut = false, string? Stderr = null, string? FailureReason = null, string? Stdout = null);

/// <summary>One bounded process lifecycle shared by both OCR engines and discovery.</summary>
internal static class OcrProcessRunner
{
    private const int MaximumCapturedCharacters = 16 * 1024;
    internal const long MaximumTextFileBytes = 64L * 1024 * 1024;

    internal static async Task<string> ReadTextFileAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaximumTextFileBytes)
            throw new InvalidDataException("ocr_output_size_limit");
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    internal static async Task<OcrProcessResult> RunAsync(string fileName, string arguments,
        TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new(fileName, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        try
        {
            if (!process.Start())
                return new(false, FailureReason: "process_start_failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, Stderr: ex.GetType().Name, FailureReason: "process_start_failed");
        }

        using var reads = new CancellationTokenSource();
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumCapturedCharacters, reads.Token);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumCapturedCharacters, reads.Token);
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var timedOut = false;
        string? lifecycleFailure = null;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            TryKill(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lifecycleFailure = "process_observation_failed";
            TryKill(process);
        }
        // A descendant can keep inherited handles open after the parent has exited.
        // Draining therefore has a separate bound, including after a failed kill.
        var drains = Task.WhenAll(stdout, stderr);
        var drained = await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
            .ConfigureAwait(false) == drains;
        if (!drained)
        {
            TryKill(process);
            reads.Cancel();
            _ = drains.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        if (ct.IsCancellationRequested)
        {
            _ = drains.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
        }
        string? error = null;
        if (drained)
        {
            try { await drains.ConfigureAwait(false); error = stderr.Result; }
            catch (OperationCanceledException) { }
        }
        var exited = process.HasExited;
        return new(exited && process.ExitCode == 0 && !timedOut && drained && lifecycleFailure is null,
            exited ? process.ExitCode : null, timedOut, error,
            lifecycleFailure ?? (!drained ? "output_drain_timeout" : timedOut ? "timeout" : !exited ? "cleanup_incomplete" : null),
            drained && stdout.IsCompletedSuccessfully ? stdout.Result : null);
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int maximum, CancellationToken ct)
    {
        var captured = new StringBuilder(Math.Min(maximum, 4096));
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var remaining = maximum - captured.Length;
            if (remaining > 0)
                captured.Append(buffer, 0, Math.Min(remaining, read));
        }
        return captured.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                  or NotSupportedException) { }
    }
}
