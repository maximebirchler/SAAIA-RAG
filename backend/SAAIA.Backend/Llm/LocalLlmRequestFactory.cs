using System.Text;
using System.Text.Json;

namespace SAAIA.Backend;

internal enum LocalLlmResponseShape
{
    OpenAiChat,
    LlamaNative
}

internal sealed record LocalLlmRequest(
    string RelativeUri,
    Dictionary<string, object> Payload,
    LocalLlmResponseShape ResponseShape);

internal static class LocalLlmRequestFactory
{
    internal const string NativeCompletionMode = "llama_completion";

    internal static LocalLlmRequest Create(
        ChatOptions options,
        string systemPrompt,
        string userPrompt,
        double temperature,
        int maxTokens)
    {
        if (string.Equals(
                options.LlmApiMode?.Trim(),
                NativeCompletionMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return new LocalLlmRequest(
                RelativeUri: "completion",
                Payload: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["prompt"] = BuildPrompt(options.LlmPromptFormat, systemPrompt, userPrompt),
                    ["temperature"] = temperature,
                    ["n_predict"] = maxTokens,
                    ["stop"] = new[] { "<|im_end|>" }
                },
                ResponseShape: LocalLlmResponseShape.LlamaNative);
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = options.LlmModel,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        if (!string.IsNullOrWhiteSpace(options.LlmChatTemplate))
            payload["chat_template"] = options.LlmChatTemplate.Trim();

        return new LocalLlmRequest(
            RelativeUri: "v1/chat/completions",
            Payload: payload,
            ResponseShape: LocalLlmResponseShape.OpenAiChat);
    }

    internal static bool TryExtractContent(
        JsonElement root,
        LocalLlmResponseShape responseShape,
        out string? content,
        out string error)
    {
        content = null;

        if (responseShape == LocalLlmResponseShape.LlamaNative)
        {
            if (!root.TryGetProperty("content", out var nativeContent))
            {
                error = "content_missing";
                return false;
            }

            content = ReadContent(nativeContent);
            error = string.IsNullOrWhiteSpace(content) ? "content_missing" : string.Empty;
            return string.IsNullOrWhiteSpace(error);
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            error = "choices_missing";
            return false;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var chatContent))
        {
            error = "content_missing";
            return false;
        }

        content = ReadContent(chatContent);
        error = string.IsNullOrWhiteSpace(content) ? "content_missing" : string.Empty;
        return string.IsNullOrWhiteSpace(error);
    }

    internal static bool TryExtractContent(
        string body,
        LocalLlmResponseShape responseShape,
        out string? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var json = JsonDocument.Parse(body);
            return TryExtractContent(json.RootElement, responseShape, out content, out _);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrompt(
        string? promptFormat,
        string systemPrompt,
        string userPrompt)
    {
        if (string.Equals(promptFormat?.Trim(), "plain", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                "System:\n", systemPrompt,
                "\n\nUser:\n", userPrompt,
                "\n\nAssistant:\n");
        }

        return string.Concat(
            "<|im_start|>system\n", systemPrompt, "<|im_end|>\n",
            "<|im_start|>user\n", userPrompt, "<|im_end|>\n",
            "<|im_start|>assistant\n");
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
                || !string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("text", out var text))
            {
                continue;
            }

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
