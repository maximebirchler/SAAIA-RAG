using System;
using System.Net.Http;
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
