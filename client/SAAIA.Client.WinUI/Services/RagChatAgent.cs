using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

public sealed class RagChatAgent
{
    private readonly ApiClient _api;
    private readonly OpenAiLlmClient _llm;

    private bool _llmEnabled = true;
    private bool _strictMode = false;
    private double _temperature = 0.2;
    private int _maxTokens = 900;
    private string _ragQualityPreset = "balanced";

    private readonly ToolMemory _mem = new();

    public RagChatAgent(ApiClient api, OpenAiLlmClient llm)
    {
        _api = api;
        _llm = llm;
    }

    internal void ApplySettings(AppSettings s)
    {
        _llmEnabled = s.UseLocalLlm;
        _strictMode = s.StrictMode;

        var t = s.LlmTemperature;
        if (double.IsNaN(t) || double.IsInfinity(t)) t = 0.2;
        _temperature = Math.Clamp(t, 0, 1);

        var mt = s.LlmMaxOutputTokens;
        _maxTokens = Math.Clamp(mt, 128, 4096);

        _ragQualityPreset = string.IsNullOrWhiteSpace(s.RagQualityPreset)
            ? "balanced"
            : s.RagQualityPreset.Trim().ToLowerInvariant();

        if (_ragQualityPreset is not ("quick" or "balanced" or "deep"))
            _ragQualityPreset = "balanced";
    }

    public async Task<(string finalAnswer, object? sourcesPayload)> RunAsync(
        string userText,
        string category,
        IReadOnlyList<ChatMessageItem> conversationTail,
        Action<string> onDelta,
        CancellationToken ct,
        Action<string>? onPhase = null,
        Action<string>? onProgress = null)
    {
        userText ??= string.Empty;

        if (!_llmEnabled)
        {
            if (LocalizedStrings.TryDetectLanguagePreferenceChange(userText, out var requestedLanguage))
            {
                if (!string.IsNullOrWhiteSpace(_mem.LastUserMessage)
                    && string.Equals(_mem.LastRouterIntent, "rag_search_fallback", StringComparison.OrdinalIgnoreCase))
                {
                    var replayCategory = string.IsNullOrWhiteSpace(_mem.LastSearchOnlyCategory) ? category : _mem.LastSearchOnlyCategory;
                    onPhase?.Invoke(DeterministicAgentText.PhaseRag(requestedLanguage));
                    onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(requestedLanguage));
                    var (replayedAnswer, replayedPayload) = await RunSearchOnlyFallbackAsync(_mem.LastUserMessage, replayCategory, ct, requestedLanguage).ConfigureAwait(false);
                    await SimulateStreamingAsync(replayedAnswer, onDelta, ct).ConfigureAwait(false);
                    onProgress?.Invoke(string.Empty);
                    return (replayedAnswer, replayedPayload);
                }

                var ack = LocalizedStrings.NoPreviousAnswerToTranslate(requestedLanguage);
                await SimulateStreamingAsync(ack, onDelta, ct).ConfigureAwait(false);
                onProgress?.Invoke(string.Empty);
                _mem.LastUserMessage = userText;
                _mem.LastAssistantAnswer = ack;
                _mem.LastRouterIntent = "meta.set_language";
                return (ack, null);
            }

            var language = LocalizedStrings.DetectLanguage(userText, "fr");
            _mem.LastLanguage = language;
            _mem.LastUserDetectedLanguage = language;
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));
            var (ans, payload) = await RunSearchOnlyFallbackAsync(userText, category, ct, language).ConfigureAwait(false);
            await SimulateStreamingAsync(ans, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (ans, payload);
        }

        var history = (conversationTail ?? Array.Empty<ChatMessageItem>())
            .Where(m => m is not null)
            .Select(m => (role: NormalizeRole(m.Role), content: (m.Content ?? string.Empty).Trim()))
            .Where(m => !string.IsNullOrWhiteSpace(m.content))
            .TakeLast(12)
            .ToList();

        if (_strictMode)
        {
            history.Insert(0, ("system",
                "User preference: strict documentary mode. Avoid invention. If missing sources, say so and ask 1 clarification question."));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            history.Insert(0, ("system",
                $"Default retrieval category hint: {category}. Use it only if it matches the user's intent."));
        }

        var llm = new LlmAdapter(_llm, _temperature, _maxTokens);
        var orchSettings = new AppSettings { StrictMode = _strictMode };
        var orch = new ToolAgentOrchestrator(_api, llm, _mem, orchSettings);

        return await orch.RunAsync(
            history,
            userText,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunSearchOnlyFallbackAsync(
        string userText,
        string category,
        CancellationToken ct,
        string? forcedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return (string.Empty, null);

        var topK = _ragQualityPreset switch
        {
            "quick" => 5,
            "deep" => 12,
            _ => 8
        };

        var resp = await _api.RagSearchAsync(userText, category, topK: topK, mode: "balanced", ct).ConfigureAwait(false);
        var items = (resp.Items ?? new List<RagItem>())
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();

        var sources = items.Select(x => new
        {
            docPath = (x.DocPath ?? string.Empty).Replace('\u005C', '/'),
            docName = x.DocName ?? string.Empty,
            pageStart = x.PageStart ?? 1,
            pageEnd = x.PageEnd ?? x.PageStart ?? 1,
            label = $"{(x.DocName ?? "document")} (p.{(x.PageStart ?? 1)}{(((x.PageEnd ?? x.PageStart ?? 1) != (x.PageStart ?? 1)) ? $"–{(x.PageEnd ?? x.PageStart ?? 1)}" : string.Empty)})",
            snippet = (x.Text ?? string.Empty).Length > 240 ? (x.Text ?? string.Empty).Substring(0, 240) + "…" : (x.Text ?? string.Empty)
        }).ToList();

        var payload = new { intent = "rag_search", sources };

        var detectedLanguage = string.IsNullOrWhiteSpace(forcedLanguage)
            ? LocalizedStrings.DetectLanguage(userText, "fr")
            : LocalizedStrings.NormalizeLanguage(forcedLanguage);
        var ans = items.Count == 0
            ? LocalizedStrings.NoDocumentsFound(detectedLanguage)
            : DeterministicAgentText.DegradedNoLlm(detectedLanguage);

        _mem.LastLanguage = detectedLanguage;
        _mem.LastUserMessage = userText;
        _mem.LastAssistantAnswer = ans;
        _mem.LastRouterIntent = "rag_search_fallback";
        _mem.LastSearchOnlyCategory = category;

        return (ans, payload);
    }

    private static string NormalizeRole(string? role)
    {
        var r = (role ?? "user").Trim().ToLowerInvariant();
        return r switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user"
        };
    }

    private static async Task SimulateStreamingAsync(string text, Action<string> onDelta, CancellationToken ct)
    {
        if (onDelta is null || string.IsNullOrEmpty(text)) return;

        const int chunk = 48;
        for (var i = 0; i < text.Length; i += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(chunk, text.Length - i);
            onDelta(text.Substring(i, take));
            await Task.Yield();
        }
    }

    private sealed class LlmAdapter : ILlmClient
    {
        private readonly OpenAiLlmClient _llm;
        private readonly double _temperature;
        private readonly int _maxTokens;

        public LlmAdapter(OpenAiLlmClient llm, double temperature, int maxTokens)
        {
            _llm = llm;
            _temperature = Math.Clamp(temperature, 0, 1);
            _maxTokens = Math.Clamp(maxTokens, 128, 4096);
        }

        public async Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        {
            var list = messages?.ToList() ?? new List<(string role, string content)>();
            if (forceJson)
                list.Insert(0, ("system", "Return ONLY valid JSON. No markdown. No extra text."));

            return await _llm.ChatOnceAsync(list, _temperature, _maxTokens, ct).ConfigureAwait(false);
        }

        public async Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        {
            var list = messages?.ToList() ?? new List<(string role, string content)>();
            if (forceJson)
            {
                var full = await CompleteAsync(list, forceJson: true, ct).ConfigureAwait(false);
                await SimulateStreamingAsync(full, onDelta, ct).ConfigureAwait(false);
                return;
            }

            await _llm.ChatStreamAsync(list, _temperature, _maxTokens, onDelta, ct).ConfigureAwait(false);
        }
    }
}
