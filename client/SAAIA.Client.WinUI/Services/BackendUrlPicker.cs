using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Probes backend URLs in parallel and returns the first one reachable on the current network.
/// Uses /health for reachability so a busy deep /ready dependency (TEI/Qdrant) does not
/// make roaming between LAN/VPN look like a network outage.
/// </summary>
internal static class BackendUrlPicker
{
    internal readonly record struct PickResult(string? Url, bool ChangedFromPrimary, string? FailureSummary);

    internal static async Task<PickResult> PickAsync(
        IReadOnlyList<string> candidates,
        TimeSpan perUrlTimeout,
        CancellationToken ct)
    {
        if (candidates is null || candidates.Count == 0)
            return new PickResult(null, false, "no candidates");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>(candidates.Count);
        foreach (var u in candidates)
        {
            var trimmed = (u ?? "").Trim().TrimEnd('/');
            if (trimmed.Length == 0) continue;
            if (seen.Add(trimmed)) deduped.Add(trimmed);
        }

        if (deduped.Count == 0)
            return new PickResult(null, false, "no candidates");

        if (deduped.Count == 1)
        {
            var only = await ProbeAsync(deduped[0], perUrlTimeout, ct).ConfigureAwait(false);
            return new PickResult(
                only.Reachable ? only.Url : null,
                false,
                only.Reachable ? null : $"{only.Url}: {only.Error}");
        }

        using var winnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = deduped.Select(u => ProbeAsync(u, perUrlTimeout, winnerCts.Token)).ToList();

        var failures = new List<string>(tasks.Count);
        ProbeOutcome? bestSoFar = null;

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);

            ProbeOutcome outcome;
            try
            {
                outcome = await done.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
                continue;
            }

            if (outcome.Reachable && outcome.Is2xx)
            {
                try { winnerCts.Cancel(); } catch { }
                var changed = !string.Equals(outcome.Url, deduped[0], StringComparison.OrdinalIgnoreCase);
                return new PickResult(outcome.Url, changed, null);
            }

            if (outcome.Reachable)
            {
                bestSoFar ??= outcome;
                continue;
            }

            failures.Add($"{outcome.Url}: {outcome.Error}");
        }

        if (bestSoFar is { } b)
        {
            var changed = !string.Equals(b.Url, deduped[0], StringComparison.OrdinalIgnoreCase);
            return new PickResult(b.Url, changed, null);
        }

        return new PickResult(null, false, string.Join(" | ", failures));
    }

    private readonly record struct ProbeOutcome(string Url, bool Reachable, bool Is2xx, string? Error);

    private static async Task<ProbeOutcome> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var trimmed = (url ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
            return new ProbeOutcome(trimmed, false, false, "empty url");

        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var resp = await client.GetAsync(trimmed + "/health", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var is2xx = code >= 200 && code < 300;
            return new ProbeOutcome(trimmed, true, is2xx, is2xx ? null : $"HTTP {code}");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return new ProbeOutcome(trimmed, false, false, "cancelled");
        }
        catch (TaskCanceledException)
        {
            return new ProbeOutcome(trimmed, false, false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeOutcome(trimmed, false, false, ex.Message);
        }
        catch (Exception ex)
        {
            return new ProbeOutcome(trimmed, false, false, ex.GetType().Name);
        }
    }
}
