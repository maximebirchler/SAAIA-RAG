using System.Text;

static partial class TeiClient
{
    /// <summary>Same bounded retry policy as embedding requests; successful responses remain caller-owned.</summary>
    internal static async Task<HttpResponseMessage> SendAdmissionRequestAsync(HttpClient tei,
        HttpMethod method, string path, string? payload, string operation, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= DelayWithJitterAsync;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, path);
            if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await tei.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await delay(_retryDelays[attempt], ct);
                continue;
            }
            catch (TaskCanceledException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await delay(_retryDelays[attempt], ct);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            if (IsTransient(response.StatusCode) && attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                response.Dispose();
                await delay(_retryDelays[attempt], ct);
                continue;
            }
            using (response) throw await TeiRequestException.FromResponseAsync(response, operation, ct);
        }
    }
}
