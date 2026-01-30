using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SAAIA.Backend.Chat;

public sealed class LlmClient
{
    private readonly HttpClient _llm;
    private readonly ChatOptions _defaults;
    private readonly ILogger<LlmClient> _log;

    public LlmClient(IHttpClientFactory http, IOptions<ChatOptions> opt, ILogger<LlmClient> log)
    {
        _llm = http.CreateClient("llm");
        _defaults = opt.Value;
        _log = log;

        // IMPORTANT: streaming SSE -> Timeout infini côté HttpClient
        _llm.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Vérifie que le provider LLM répond réellement (pas seulement /v1/models).
    /// Envoie une mini completion non-stream (max_tokens=1) et retourne true si succès.
    /// </summary>
    public async Task<bool> ProbeAsync(
        int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        var o = _defaults;

        var payload = new
        {
            model = o.LlmModel,
            stream = false,
            temperature = 0.0,
            max_tokens = 1,
            messages = new[]
            {
                new { role = "user", content = "ping" }
            }
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.LlmBaseUrl.TrimEnd('/')}/v1/chat/completions");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _llm.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stream texte (OpenAI-compatible) -> IAsyncEnumerable<string>.
    /// </summary>
    public IAsyncEnumerable<string> StreamAsync(string userMessage, ChatOptions o, string? systemPrompt, CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await ProduceAsync(ch.Writer, userMessage, o, systemPrompt, ct);
                ch.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                ch.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                ch.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        return ch.Reader.ReadAllAsync(ct);
    }

    public async Task ProduceAsync(ChannelWriter<string> writer, string userMessage, ChatOptions o, string? systemPrompt, CancellationToken ct)
    {
        // 1) llama.cpp / OpenAI-compatible
        try
        {
            await foreach (var chunk in StreamOpenAiAsync(userMessage, systemPrompt, o, ct))
                await writer.WriteAsync(chunk, ct);

            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 2) fallback Ollama optionnel
            if (o.EnableOllamaFallback)
            {
                try
                {
                    await foreach (var chunk in StreamOllamaAsync(userMessage, systemPrompt, o, ct))
                        await writer.WriteAsync(chunk, ct);

                    return;
                }
                catch
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }
            }

            throw;
        }
    }

    private async IAsyncEnumerable<string> StreamOpenAiAsync(
        string userMessage,
        string? systemPrompt,
        ChatOptions o,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var msgs = new List<object>(2);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgs.Add(new { role = "system", content = systemPrompt!.Trim() });
        msgs.Add(new { role = "user", content = userMessage });

        var payload = new
        {
            model = o.LlmModel,
            stream = true,
            temperature = o.LlmTemperature,
            max_tokens = o.LlmMaxTokens,
            messages = msgs
        };

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            var preview = payloadJson.Length > 2000 ? payloadJson.Substring(0, 2000) + "…(truncated)" : payloadJson;
            _log.LogInformation("Sending LLM payload (len={Len}): {Preview}", payloadJson.Length, preview);
        }
        catch { }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.LlmBaseUrl.TrimEnd('/')}/v1/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _llm.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line.Substring(6).Trim();
            if (data == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice0 = choices[0];

                if (choice0.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var s = contentEl.GetString();
                    if (!string.IsNullOrEmpty(s))
                        yield return s!;
                }

                if (choice0.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                    msg.TryGetProperty("content", out var msgContent) && msgContent.ValueKind == JsonValueKind.String)
                {
                    var s2 = msgContent.GetString();
                    if (!string.IsNullOrEmpty(s2))
                        yield return s2!;
                }
            }
        }
    }

    private async IAsyncEnumerable<string> StreamOllamaAsync(
        string userMessage,
        string? systemPrompt,
        ChatOptions o,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.OllamaBaseUrl))
            throw new InvalidOperationException("OllamaBaseUrl est requis si EnableOllamaFallback=true.");

        if (string.IsNullOrWhiteSpace(o.OllamaModel))
            throw new InvalidOperationException("OllamaModel est requis si EnableOllamaFallback=true.");

        var baseUrl = o.OllamaBaseUrl.TrimEnd('/');

        var finalPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? userMessage
            : $"[SYSTEM]\n{systemPrompt!.Trim()}\n\n[USER]\n{userMessage}";

        var payload = new
        {
            model = o.OllamaModel,
            stream = true,
            prompt = finalPrompt,
            options = new
            {
                temperature = o.LlmTemperature,
                num_predict = o.LlmMaxTokens
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/generate");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _llm.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.String)
            {
                var s = responseEl.GetString();
                if (!string.IsNullOrEmpty(s))
                    yield return s!;
            }

            if (root.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True)
                yield break;
        }
    }
}
