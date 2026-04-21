using System.Text;
using System.Text.Json;

namespace SAAIA.Backend;

internal sealed class LocalLlmChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ChatOptions _options;

    public LocalLlmChatClient(
        IHttpClientFactory httpFactory,
        ChatOptions options)
    {
        _httpFactory = httpFactory;
        _options = options;
    }

    internal bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.LlmBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.LlmModel);

    internal async Task<string?> TryCompleteAsync(
        string systemPrompt,
        string userPrompt,
        int? maxTokens,
        double? temperature,
        CancellationToken ct)
    {
        if (!IsConfigured)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = _options.LlmModel,
                    temperature = temperature ?? _options.LlmTemperature,
                    max_tokens = Math.Clamp(maxTokens ?? _options.LlmMaxTokens, 64, 4096),
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                }, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        try
        {
            var http = _httpFactory.CreateClient("llm");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
                return null;

            return ReadContent(content);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("text", out var text))
                continue;

            var value = text.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(value.Trim());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
