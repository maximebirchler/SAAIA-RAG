using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal enum LlmModelsStatus
{
    Ok,
    Loading,
    Error
}

internal static class LlmEndpointProbe
{
    /// <summary>
    /// Probes GET {baseUrl}/models (OpenAI-compatible) and classifies common states:
    /// - 200 => Ok
    /// - 503 + "Loading model" => Loading
    /// - Timeout (TaskCanceledException) => Loading if TCP port is open, otherwise Error
    /// </summary>
    public static async Task<(LlmModelsStatus Status, int? HttpStatus, string? ErrorMessage)> GetModelsStatusAsync(
        string llmBaseUrl,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(llmBaseUrl))
            return (LlmModelsStatus.Error, null, "Missing base url");

        try
        {
            var baseUri = new Uri(llmBaseUrl.TrimEnd('/'), UriKind.Absolute);
            var uri = new Uri(baseUri, "models");

            using var http = new HttpClient { Timeout = timeout };
            using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return (LlmModelsStatus.Ok, (int)resp.StatusCode, null);

            // llama.cpp commonly returns 503 while loading model
            if ((int)resp.StatusCode == 503)
            {
                var body = await SafeReadAsync(resp).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(body) &&
                    body.IndexOf("Loading model", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (LlmModelsStatus.Loading, 503, "Loading model");
                }

                return (LlmModelsStatus.Error, 503, "Service unavailable");
            }

            return (LlmModelsStatus.Error, (int)resp.StatusCode, resp.ReasonPhrase);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient uses TaskCanceledException on timeout.
            // If the TCP port is open, treat as "Loading/Busy" to avoid triggering repairs / double starts.
            try
            {
                if (TryParseHostPort(llmBaseUrl, out var host, out var port))
                {
                    var open = await IsPortOpenAsync(host, port, TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
                    if (open)
                        return (LlmModelsStatus.Loading, null, "Timeout while probing /models (port open)");
                }
            }
            catch
            {
                // ignore
            }

            return (LlmModelsStatus.Error, null, ex.Message);
        }
        catch (Exception ex)
        {
            return (LlmModelsStatus.Error, null, ex.Message);
        }
    }

    public static async Task<bool> IsModelsOkAsync(string llmBaseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var (st, _, _) = await GetModelsStatusAsync(llmBaseUrl, timeout, ct).ConfigureAwait(false);
        return st == LlmModelsStatus.Ok;
    }

    private static bool TryParseHostPort(string llmBaseUrl, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 0;

        try
        {
            var baseUri = new Uri(llmBaseUrl.TrimEnd('/'), UriKind.Absolute);
            host = baseUri.Host;
            port = baseUri.Port;
            return port > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            var delayTask = Task.Delay(timeout, ct);

            var done = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
            if (done != connectTask) return false;

            await connectTask.ConfigureAwait(false);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> SafeReadAsync(HttpResponseMessage resp)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
