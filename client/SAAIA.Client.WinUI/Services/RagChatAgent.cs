using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    private readonly UserPrefsStore.UserPrefs _initialPrefs;

    private bool _llmEnabled = true;
    private string _activeMode = "auto";
    private double _temperature = 0.2;
    private int _maxTokens = 900;
    private string _ragQualityPreset = "balanced";
    private AppSettings _effectiveSettings = new();

    private readonly ToolMemory _mem = new();

    public RagChatAgent(ApiClient api, OpenAiLlmClient llm)
    {
        _api = api;
        _llm = llm;
        _initialPrefs = UserPrefsStore.Load();
        _mem.LastLanguage = _initialPrefs.Language;
        _mem.LastStyle = _initialPrefs.Style;
        _mem.LastMode = "auto";
        _activeMode = "auto";
    }

    /// <summary>
    /// Resets per-conversation state while preserving only session-agnostic
    /// preferences (language and style). Operating mode is conversation-local
    /// and goes back to auto on every new chat/session.
    /// </summary>
    internal void ResetConversationState()
    {
        _mem.ResetConversationState();
        _activeMode = "auto";
    }

    internal void ApplySettings(AppSettings s)
    {
        _effectiveSettings = s.Clone();
        _llmEnabled = s.UseLocalLlm;
        _mem.LastLanguage = LocalizedStrings.Normalize(s.UiLanguage);
        var configuredMode = AppSettings.NormalizeActiveMode(s.ActiveMode);
        _activeMode = configuredMode;
        _mem.LastMode = _activeMode;

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

        if (LocalizedStrings.TryDetectStylePreferenceChange(userText, out var requestedStyle))
        {
            var interactionLanguage = LocalizedStrings.DetectLanguage(userText, _mem.LastLanguage);
            _mem.LastStyle = LocalizedStrings.NormalizeStyle(requestedStyle);
            UserPrefsStore.SaveStyle(_mem.LastStyle);

            var ack = LocalizedStrings.StyleChanged(_mem.LastStyle, interactionLanguage);
            await SimulateStreamingAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            _mem.LastUserMessage = userText;
            _mem.LastAssistantAnswer = ack;
            _mem.LastLanguage = interactionLanguage;
            _mem.LastRouterIntent = "meta.set_style";
            return (ack, null);
        }

        if (LocalizedStrings.TryDetectModePreferenceChange(userText, out var requestedMode))
        {
            var interactionLanguage = LocalizedStrings.DetectLanguage(userText, _mem.LastLanguage);
            _activeMode = AppSettings.NormalizeActiveMode(requestedMode);
            _mem.LastMode = _activeMode;

            var ack = LocalizedStrings.ModeChanged(_activeMode, interactionLanguage);
            await SimulateStreamingAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            _mem.LastUserMessage = userText;
            _mem.LastAssistantAnswer = ack;
            _mem.LastLanguage = interactionLanguage;
            _mem.LastRouterIntent = "meta.set_mode";
            return (ack, null);
        }

        if (!_llmEnabled)
        {
            if (LocalizedStrings.TryDetectLanguagePreferenceChange(userText, out var requestedLanguage))
            {
            if (!string.IsNullOrWhiteSpace(_mem.LastUserMessage)
                && string.Equals(_mem.LastRouterIntent, "rag_search_fallback", StringComparison.OrdinalIgnoreCase))
            {
                _mem.LastLanguage = requestedLanguage;
                UserPrefsStore.SaveLanguage(requestedLanguage);
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
                _mem.LastLanguage = requestedLanguage;
                _mem.LastRouterIntent = "meta.set_language";
                UserPrefsStore.SaveLanguage(requestedLanguage);
                return (ack, null);
            }

            var language = LocalizedStrings.DetectLanguage(userText, "fr");
            _mem.LastLanguage = language;
            _mem.LastUserDetectedLanguage = language;
            ApplyDefaultCategoryScope(category);
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

        if (string.Equals(_activeMode, "strict", StringComparison.OrdinalIgnoreCase))
        {
            history.Insert(0, ("system",
                "User preference: strict documentary mode. Avoid invention. If missing sources, say so and ask 1 clarification question."));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            ApplyDefaultCategoryScope(category);
            history.Insert(0, ("system",
                $"Default retrieval category hint: {category}. Use it only if it matches the user's intent."));
        }

        var llm = new LlmAdapter(_llm, _temperature, _maxTokens);
        var orchSettings = _effectiveSettings.Clone();
        orchSettings.ActiveMode = _activeMode;
        var orch = new ToolAgentOrchestrator(_api, llm, _mem, orchSettings);
        var result = await orch.RunAsync(
            history,
            userText,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);

        _activeMode = AppSettings.NormalizeActiveMode(orchSettings.ActiveMode);
        _mem.LastMode = _activeMode;

        return result;
    }


    public async Task<DirectCommandExecutionResult> ExecuteDirectCommandAsync(DirectCommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var llm = new LlmAdapter(_llm, _temperature, _maxTokens);
        var orchSettings = _effectiveSettings.Clone();
        orchSettings.ActiveMode = _activeMode;
        var orch = new ToolAgentOrchestrator(_api, llm, _mem, orchSettings);
        var result = await orch.ExecuteDirectCommandAsync(request, ct).ConfigureAwait(false);
        _activeMode = AppSettings.NormalizeActiveMode(orchSettings.ActiveMode);
        _mem.LastMode = _activeMode;
        return result;
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

        var detectedLanguage = string.IsNullOrWhiteSpace(forcedLanguage)
            ? LocalizedStrings.DetectLanguage(userText, "fr")
            : LocalizedStrings.NormalizeLanguage(forcedLanguage);

        RagSearchResponse resp;
        try
        {
            resp = await _api.RagSearchAsync(userText, category, topK: topK, mode: "balanced", ct).ConfigureAwait(false);
        }
        catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
        {
            var ansBusy = LocalizedStrings.RagSearchBusy(detectedLanguage);
            var payloadBusy = new
            {
                intent = "rag_search",
                busy = true,
                retryAfterSeconds = ex.RetryAfterSeconds,
                sources = Array.Empty<object>()
            };

            _mem.LastLanguage = detectedLanguage;
            _mem.LastUserMessage = userText;
            _mem.LastAssistantAnswer = ansBusy;
            _mem.LastRouterIntent = "rag_search_busy";
            _mem.LastSearchOnlyCategory = category;

            return (ansBusy, payloadBusy);
        }

        var items = (resp.Items ?? new List<RagItem>())
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();
        var answerItems = SelectSearchOnlyFallbackAnswerItems(items);

        var sources = answerItems.Select(BuildSearchOnlyFallbackSourcePayload).ToList();

        var payload = new { intent = "rag_search", sources };

        var ans = items.Count == 0
            ? LocalizedStrings.NoDocumentsFound(detectedLanguage)
            : BuildSearchOnlyFallbackAnswer(resp, answerItems, detectedLanguage);

        _mem.LastLanguage = detectedLanguage;
        _mem.LastUserMessage = userText;
        _mem.LastAssistantAnswer = ans;
        _mem.LastRouterIntent = "rag_search_fallback";
        _mem.LastSearchOnlyCategory = category;

        return (ans, payload);
    }

    private static string BuildSearchOnlyFallbackAnswer(
        RagSearchResponse response,
        IReadOnlyList<RagItem> items,
        string language)
    {
        var sb = new StringBuilder(DeterministicAgentText.DegradedNoLlm(language).Trim());
        var guidance = response.Guidance;

        AppendGuidanceLine(sb, guidance?.QualificationNote);
        AppendGuidanceLine(sb, guidance?.ClarifyingQuestion);

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(FallbackSourcesHeader(language));

        foreach (var item in items.Take(3))
        {
            var label = BuildFallbackSourceLabel(item, language);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            sb.Append("- ");
            sb.Append(label);

            var cards = item.MatchedContentCards?
                .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                .Select(static card => card.Title.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (cards is { Length: > 0 })
            {
                sb.Append(" - ");
                sb.Append(string.Join(", ", cards));
            }

            var snippet = CompactFallbackSnippet(BestFallbackSnippet(item));
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                sb.Append(": ");
                sb.Append(snippet);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendGuidanceLine(StringBuilder sb, string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        sb.AppendLine();
        sb.Append(trimmed);
    }

    private static IReadOnlyList<RagItem> SelectSearchOnlyFallbackAnswerItems(IReadOnlyList<RagItem> items)
    {
        if (items.Count == 0)
            return items;

        var preferred = items
            .Where(static item => !IsLowSignalFallbackItem(item))
            .ToList();

        return preferred.Count == 0 ? items : preferred;
    }

    private static bool IsLowSignalFallbackItem(RagItem item)
    {
        var role = item.SelectionHints?.EvidenceRole;
        if (string.Equals(role, "navigation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "fragment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "low_confidence", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var contextRole = item.Context?.ContentRole;
        if (string.Equals(contextRole, "navigation", StringComparison.OrdinalIgnoreCase))
            return true;

        var navigationScore = item.Context?.NavigationScore;
        var contentDensityScore = item.Context?.ContentDensityScore;
        return navigationScore is >= 0.72 && contentDensityScore is null or < 0.50;
    }

    private static string BuildFallbackSourceLabel(RagItem item, string language)
    {
        var docName = string.IsNullOrWhiteSpace(item.DocName)
            ? item.DocPath?.Split('/', '\\').LastOrDefault()
            : item.DocName;

        if (string.IsNullOrWhiteSpace(docName))
            return string.Empty;

        var pageStart = item.PageStart;
        var pageEnd = item.PageEnd ?? item.PageStart;
        if (pageStart is null)
            return docName.Trim();

        var pagePrefix = string.Equals(LocalizedStrings.NormalizeLanguage(language), "de", StringComparison.OrdinalIgnoreCase)
            ? "S."
            : "p.";
        return pageEnd.HasValue && pageEnd.Value != pageStart.Value
            ? $"{docName.Trim()} ({pagePrefix}{pageStart.Value}-{pageEnd.Value})"
            : $"{docName.Trim()} ({pagePrefix}{pageStart.Value})";
    }

    private static string CompactFallbackSnippet(string? text)
    {
        var normalized = string.Join(
            ' ',
            (text ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length <= 180)
            return normalized;

        return normalized[..180] + "...";
    }

    private static string? BestFallbackSnippet(RagItem item)
        => FirstNonBlank(item.Snippet, item.ContextualSnippet, item.Text);

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string FallbackSourcesHeader(string? language)
        => LocalizedStrings.RagDegradedSourcesHeader(language);

    private static object BuildSearchOnlyFallbackSourcePayload(RagItem item)
    {
        var pageStart = item.PageStart ?? 1;
        var pageEnd = item.PageEnd ?? item.PageStart ?? 1;
        var docPath = (item.DocPath ?? string.Empty).Replace('\u005C', '/');
        var docName = item.DocName ?? string.Empty;
        var snippetText = BestFallbackSnippet(item) ?? string.Empty;

        return new
        {
            docId = item.DocId,
            docPath,
            docName,
            pageStart,
            pageEnd,
            label = string.IsNullOrWhiteSpace(docName) ? "document" : docName,
            snippet = snippetText.Length > 240 ? snippetText[..240] + "..." : snippetText,
            contextualSnippet = item.ContextualSnippet,
            score = item.Score,
            sourceHash = item.SourceHash,
            docLanguage = item.DocLanguage,
            profileLanguage = item.ProfileLanguage,
            categoryRef = item.CategoryRef,
            categoryPath = item.CategoryPath,
            chunkId = item.ChunkId,
            provenanceInfo = item.ProvenanceInfo is null ? null : new
            {
                item.ProvenanceInfo.OffsetStart,
                item.ProvenanceInfo.OffsetEnd
            },
            context = item.Context is null ? null : new
            {
                item.Context.ChunkType,
                item.Context.SectionTitle,
                item.Context.HeadingPath,
                item.Context.PrevChunkId,
                item.Context.NextChunkId,
                item.Context.SameSectionChunkId,
                item.Context.ContentRole,
                item.Context.NavigationReason,
                item.Context.OriginalChunkType,
                item.Context.NavigationScore,
                item.Context.ContentDensityScore
            },
            extractionQuality = item.ExtractionQuality is null ? null : new
            {
                item.ExtractionQuality.ExtractionSource,
                item.ExtractionQuality.OcrAttempted,
                item.ExtractionQuality.OcrApplied,
                item.ExtractionQuality.DocumentQualityStatus,
                item.ExtractionQuality.DocumentExtractionConfidence,
                item.ExtractionQuality.DocumentManualReviewRecommended,
                item.ExtractionQuality.PageQualityStatus,
                item.ExtractionQuality.PageExtractionConfidence,
                item.ExtractionQuality.PageManualReviewRecommended,
                item.ExtractionQuality.TextStatus,
                item.ExtractionQuality.OcrRecommended,
                item.ExtractionQuality.Signals,
                item.ExtractionQuality.DiagnosticSummary
            },
            matchedContentCards = item.MatchedContentCards?
                .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                .Select(static card => new
                {
                    card.ContentCardId,
                    card.Title,
                    card.PageStart,
                    card.PageEnd,
                    card.Kind,
                    card.Signals,
                    card.Evidence
                })
                .Take(5)
                .ToList(),
            selectionHints = item.SelectionHints is null ? null : new
            {
                item.SelectionHints.EvidenceRole,
                item.SelectionHints.ActionabilityScore,
                item.SelectionHints.SupportScore,
                item.SelectionHints.FragmentScore,
                item.SelectionHints.NavigationScore,
                item.SelectionHints.QualityPenalty
            }
        };
    }

    private void ApplyDefaultCategoryScope(string? category)
    {
        var categoryPath = NormalizeCategoryPath(category);
        if (string.IsNullOrWhiteSpace(categoryPath))
            return;

        _mem.LastResolvedCategory = new ToolMemory.CategorySnapshot
        {
            CategoryPath = categoryPath,
            DisplayName = categoryPath.Split('/').LastOrDefault(part => !string.IsNullOrWhiteSpace(part)) ?? categoryPath,
            Aliases = new List<string> { categoryPath }
        };
    }

    private static string NormalizeCategoryPath(string? category)
    {
        var normalized = (category ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .TrimEnd('/');

        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : normalized;
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
