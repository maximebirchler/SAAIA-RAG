using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

public sealed class RagChatAgent
{
    private const int DocumentOverviewAnswerMaxTokens = 320;
    private const int DocumentOverviewSelectionMaxTokens = 64;
    private const int DocumentOverviewCandidateRepairMaxTokens = 96;

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

    internal void RehydrateConversationState(
        IReadOnlyList<ChatMessageItem>? conversation)
    {
        _mem.RehydrateConversationState(
            (conversation ?? Array.Empty<ChatMessageItem>())
            .Where(static message => message is not null)
            .Select(static message => new ToolMemory.ConversationMemoryMessage(
                message.Role,
                message.Content,
                message.SourcesJson)));
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

        var mt = NormalizeAnswerMaxTokens(s.LlmMaxOutputTokens);
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
        Action<string>? onProgress = null,
        string? sessionId = null)
    {
        userText ??= string.Empty;
        ClientLog.Info(
            "RagChatAgent turn start: " +
            $"llm={_llmEnabled}|" +
            $"mode={_activeMode}|" +
            $"quality={_ragQualityPreset}|" +
            $"maxTokens={_maxTokens}|" +
            $"category={category}|" +
            $"tail={conversationTail?.Count ?? 0}|" +
            $"chars={userText.Length}");

        RehydrateConversationState(conversationTail);

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

            var fallbackLanguage = string.IsNullOrWhiteSpace(_mem.LastLanguage)
                ? LocalizedStrings.NormalizeLanguage(_effectiveSettings.UiLanguage)
                : _mem.LastLanguage;
            var language = LocalizedStrings.DetectLanguage(userText, fallbackLanguage);
            if (ShouldPreferConfiguredLanguageForShortAmbiguousQuery(userText, language, fallbackLanguage))
                language = fallbackLanguage;
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
        ClientLog.Info(
            "RagChatAgent orchestrator start: " +
            $"history={history.Count}|" +
            $"mode={orchSettings.ActiveMode}|" +
            $"uiLang={orchSettings.UiLanguage}|" +
            $"ctx={orchSettings.QualifiedProfile?.CtxSize ?? 0}|" +
            $"ctxPerSlot={orchSettings.QualifiedProfile?.ResolvePerSlotContextSize() ?? 0}|" +
            $"parallel={orchSettings.QualifiedProfile?.Parallel ?? 1}|" +
            $"maxTokens={orchSettings.LlmMaxOutputTokens}");
        var result = await orch.RunAsync(
            history,
            userText,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);

        if (orch.LastAdvancedAnalysisHandoff is { } advancedHandoff)
        {
            if (Guid.TryParse(sessionId, out var advancedSessionId)
                && advancedSessionId != Guid.Empty)
            {
                var advanced = await orch.ExecuteAdvancedAnalysisHandoffAsync(
                        advancedSessionId,
                        advancedHandoff,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
                if (advanced.Handled)
                {
                    result = (
                        advanced.FinalAnswer ?? result.finalAnswer,
                        advanced.SourcesPayload);
                }
            }
            else
            {
                ClientLog.Warn(
                    "Advanced analysis handoff retained locally because the current chat session id is unavailable.");
            }
        }

        _activeMode = AppSettings.NormalizeActiveMode(orchSettings.ActiveMode);
        _mem.LastMode = _activeMode;
        ClientLog.Info(
            "RagChatAgent turn end: " +
            $"mode={_activeMode}|" +
            $"answerChars={result.finalAnswer?.Length ?? 0}|" +
            $"sourcesPayload={(result.sourcesPayload is null ? "none" : result.sourcesPayload.GetType().Name)}");

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

        var fallbackLanguage = string.IsNullOrWhiteSpace(_mem.LastLanguage)
            ? LocalizedStrings.NormalizeLanguage(_effectiveSettings.UiLanguage)
            : _mem.LastLanguage;
        var detectedLanguage = string.IsNullOrWhiteSpace(forcedLanguage)
            ? LocalizedStrings.DetectLanguage(userText, fallbackLanguage)
            : LocalizedStrings.NormalizeLanguage(forcedLanguage);
        if (string.IsNullOrWhiteSpace(forcedLanguage)
            && ShouldPreferConfiguredLanguageForShortAmbiguousQuery(userText, detectedLanguage, fallbackLanguage))
        {
            detectedLanguage = fallbackLanguage;
        }

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

        var isBroadSearchOnlyFallback = LooksLikeBroadSearchOnlyFallbackRequest(userText);
        var ans = items.Count == 0
            ? BuildNoDocumentsFoundAnswer(resp, detectedLanguage)
            : isBroadSearchOnlyFallback
                ? BuildBroadSearchOnlyFallbackAnswer(resp, answerItems, detectedLanguage)
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

    private static string BuildBroadSearchOnlyFallbackAnswer(
        RagSearchResponse response,
        IReadOnlyList<RagItem> items,
        string language)
    {
        var sb = new StringBuilder(BroadSearchOnlyFallbackIntro(language));
        var guidance = response.Guidance;

        AppendGuidanceLine(sb, guidance?.QualificationNote);
        AppendGuidanceLine(sb, guidance?.ClarifyingQuestion);

        var candidates = items
            .Select(item => new
            {
                Label = BuildFallbackSourceLabel(item, language),
                Title = BuildFallbackCandidateTitle(item)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
            .GroupBy(item => $"{item.Label}|{item.Title}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(6)
            .ToList();

        if (candidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(BroadSearchOnlyFallbackCandidatesHeader(language));
            foreach (var candidate in candidates)
            {
                sb.Append("- ");
                if (!string.IsNullOrWhiteSpace(candidate.Title))
                {
                    sb.Append(candidate.Title);
                    sb.Append(" (");
                    sb.Append(candidate.Label);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(candidate.Label);
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine(BroadSearchOnlyFallbackClosing(language));
        return sb.ToString().TrimEnd();
    }

    private static string BuildNoDocumentsFoundAnswer(RagSearchResponse response, string language)
    {
        var sb = new StringBuilder(LocalizedStrings.NoDocumentsFound(language).Trim());
        var guidance = response.Guidance;

        AppendGuidanceLine(sb, guidance?.QualificationNote);
        AppendGuidanceLine(sb, guidance?.ClarifyingQuestion);

        return sb.ToString().TrimEnd();
    }

    private static bool ShouldPreferConfiguredLanguageForShortAmbiguousQuery(
        string userText,
        string detectedLanguage,
        string fallbackLanguage)
    {
        fallbackLanguage = LocalizedStrings.NormalizeLanguage(fallbackLanguage);
        if (fallbackLanguage == "fr" || !string.Equals(detectedLanguage, "fr", StringComparison.OrdinalIgnoreCase))
            return false;

        var tokens = Regex.Matches(userText ?? string.Empty, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Count;
        if (tokens is 0 or > 6)
            return false;

        return Regex.IsMatch(userText ?? string.Empty, @"[\d\-_/]|\b[A-Z]{2,}\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeBroadSearchOnlyFallbackRequest(string? userText)
    {
        var text = NormalizeBroadFallbackText(userText);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (Regex.IsMatch(
            text,
            @"\b(?:plan|planning|programme|semaine|weekly|week|semana|woche|settimana|liste|list|lista|lister|ideas?|idees?|idées|options?|suggest|suggestions?|propose|proposer|propostas?|vorschlag|vorschlaege|vorschläge|consigli|recommend|recommand|recommande|recommander|plusieurs|several|varie|varied|varié|varies|compare|comparison|comparer|choisir|selection|sélection)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            text,
            @"\b(?:quels?|which|what|que|quoi|como|cosa|was)\b.*\b(?:documents?|sources?|corpus|dossier|available|disponibles?|trouves?|trouvés?|indexed|indexe|indexés?)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string NormalizeBroadFallbackText(string? text)
        => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

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

    private static string? BuildFallbackCandidateTitle(RagItem item)
    {
        var candidates = new List<string>();
        if (item.MatchedContentCards is { Count: > 0 })
        {
            candidates.AddRange(item.MatchedContentCards
                .Select(card => card.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title)));
        }

        candidates.Add(item.Context?.SectionTitle ?? string.Empty);
        candidates.Add(item.SectionTitle ?? string.Empty);
        candidates.Add(item.Context?.HeadingPath ?? string.Empty);
        candidates.Add(item.HeadingPath ?? string.Empty);

        foreach (var candidate in candidates)
        {
            var title = CleanFallbackCandidateTitle(candidate);
            if (!LooksLikeWeakFallbackCandidateTitle(title))
                return title;
        }

        return null;
    }

    private static string CleanFallbackCandidateTitle(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (text.Contains('>'))
            text = text.Split('>', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? text;
        if (text.Contains('/'))
            text = text.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? text;
        return text.Trim(' ', '-', ':', ';', '.', ',');
    }

    private static bool LooksLikeWeakFallbackCandidateTitle(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length < 4 || text.Length > 120)
            return true;

        var letters = text.Count(char.IsLetter);
        if (letters < 3)
            return true;

        if (Regex.IsMatch(text, @"^(?:source|document|documents?|chapter|section|page|index|contents|table of contents|sommaire|résumé|resume|summary|title|titre|heading|category|categorie|catégorie)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(text, @"\b(?:copyright|all rights reserved|become a|follow us|www\.|https?://)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        return false;
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

    private static string BroadSearchOnlyFallbackIntro(string? language)
        => LocalizedStrings.NormalizeLanguage(language) switch
        {
            "en" => "The local assistant is not available to write and check a complete answer. I can still show the most relevant source elements to verify first.",
            "es" => "El asistente local no está disponible para redactar y comprobar una respuesta completa. Aun así, puedo mostrar los elementos fuente más relevantes para verificarlos primero.",
            "pt" => "O assistente local não está disponível para redigir e verificar uma resposta completa. Ainda assim, posso mostrar os elementos fonte mais relevantes para verificar primeiro.",
            "de" => "Der lokale Assistent ist nicht verfügbar, um eine vollständige Antwort zu schreiben und zu prüfen. Ich kann trotzdem die wichtigsten Quellenhinweise zur Kontrolle anzeigen.",
            "it" => "L'assistente locale non è disponibile per scrivere e verificare una risposta completa. Posso comunque mostrare gli elementi fonte più pertinenti da controllare prima.",
            _ => "L'assistant local n'est pas disponible pour rédiger et vérifier une réponse complète. Je peux tout de même afficher les éléments sources les plus pertinents à contrôler d'abord."
        };

    private static string BroadSearchOnlyFallbackCandidatesHeader(string? language)
        => LocalizedStrings.NormalizeLanguage(language) switch
        {
            "en" => "Source elements to verify:",
            "es" => "Elementos fuente que verificar:",
            "pt" => "Elementos fonte a verificar:",
            "de" => "Zu prüfende Quellenhinweise:",
            "it" => "Elementi fonte da verificare:",
            _ => "Éléments sources à vérifier :"
        };

    private static string BroadSearchOnlyFallbackClosing(string? language)
        => LocalizedStrings.NormalizeLanguage(language) switch
        {
            "en" => "I am not turning these snippets into a final answer until the local assistant has rewritten and checked them.",
            "es" => "No convierto estos fragmentos en una respuesta final hasta que el asistente local los reescriba y los compruebe.",
            "pt" => "Não transformo estes trechos numa resposta final enquanto o assistente local não os reescrever e verificar.",
            "de" => "Ich mache daraus keine endgültige Antwort, solange der lokale Assistent sie nicht umgeschrieben und geprüft hat.",
            "it" => "Non trasformo questi estratti in una risposta finale finché l'assistente locale non li riscrive e verifica.",
            _ => "Je ne transforme pas ces extraits en réponse finale tant que l'assistant local ne les a pas réécrits et vérifiés."
        };

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
            category = item.Category,
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
                item.ExtractionQuality.ChunkTextStatus,
                item.ExtractionQuality.ChunkTextSparse,
                item.ExtractionQuality.ChunkOcrCandidate,
                item.ExtractionQuality.ChunkQualitySignals,
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
            profileSignals = item.ProfileSignals is null ? null : new
            {
                item.ProfileSignals.ProfileVersion,
                item.ProfileSignals.Language,
                item.ProfileSignals.Keywords,
                item.ProfileSignals.Entities,
                item.ProfileSignals.Topics,
                item.ProfileSignals.HypotheticalQuestions,
                item.ProfileSignals.Limits,
                item.ProfileSignals.MatchedTerms,
                item.ProfileSignals.MatchCount
            },
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

    private static int NormalizeAnswerMaxTokens(int configuredMaxTokens)
    {
        if (configuredMaxTokens <= 650)
            return Math.Clamp(configuredMaxTokens, 128, 650);

        return configuredMaxTokens <= 1150
            ? 1600
            : configuredMaxTokens;
    }

    internal static int ResolveLlmAdapterMaxTokensForTests(int configuredMaxTokens, bool forceJson, string prompt)
        => ResolveLlmAdapterMaxTokens(configuredMaxTokens, forceJson, prompt);

    internal static bool ShouldUseLlmAdapterJsonResponseFormatForTests(string prompt)
        => ShouldUseLlmAdapterJsonResponseFormat(prompt);

    private static int ResolveLlmAdapterMaxTokens(int configuredMaxTokens, bool forceJson, string prompt)
    {
        var normalizedConfigured = Math.Clamp(configuredMaxTokens, 128, 4096);
        if (!forceJson && LooksLikeDocumentOverviewCandidateRepairPrompt(prompt))
            return Math.Min(
                normalizedConfigured,
                DocumentOverviewCandidateRepairMaxTokens);
        if (!forceJson && LooksLikeDocumentOverviewSelectionPrompt(prompt))
            return Math.Min(normalizedConfigured, DocumentOverviewSelectionMaxTokens);
        if (!forceJson && LooksLikeDocumentOverviewWriterPrompt(prompt))
            return Math.Min(normalizedConfigured, DocumentOverviewAnswerMaxTokens);

        if (forceJson)
        {
            var sourceBackedBudget = SourceBackedLlmOutputBudget.TryResolveJsonMaxTokens(prompt, normalizedConfigured);
            if (sourceBackedBudget is not null)
                return sourceBackedBudget.Value;

            return Math.Clamp(normalizedConfigured, 512, 1600);
        }

        return LooksLikeBroadDocumentaryWriterPrompt(prompt)
            ? Math.Clamp(normalizedConfigured, 512, 4096)
            : normalizedConfigured;
    }

    private static bool LooksLikeDocumentOverviewWriterPrompt(string? prompt)
        => (prompt ?? string.Empty).Contains(
            "SAAIA_DOCUMENT_OVERVIEW_WRITER",
            StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDocumentOverviewSelectionPrompt(string? prompt)
        => (prompt ?? string.Empty).Contains(
            "SAAIA_DOCUMENT_OVERVIEW_SELECTOR",
            StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDocumentOverviewCandidateRepairPrompt(
        string? prompt)
        => (prompt ?? string.Empty).Contains(
            "SAAIA_DOCUMENT_OVERVIEW_CANDIDATE_REPAIR",
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseLlmAdapterJsonResponseFormat(string prompt)
        => prompt.Contains(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReview",
            StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFit",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeFinalSelectionAtomicRepair",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=StructuredThinCellAtomicRepair",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldReview",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBrief",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeAudit",
               StringComparison.OrdinalIgnoreCase)
           || prompt.Contains(
               "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeCoherenceReview",
               StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBroadDocumentaryWriterPrompt(string prompt)
        => prompt.Contains("PRIVATE_SOURCE_WRITING_BRIEF", StringComparison.OrdinalIgnoreCase)
           || prompt.Contains("PRIVATE_SOURCE_COVERAGE_NOTE", StringComparison.OrdinalIgnoreCase)
           || prompt.Contains("PRIVATE_SOURCE_EVIDENCE_INVENTORY", StringComparison.OrdinalIgnoreCase)
           || prompt.Contains("PRIVATE_SOURCE_RESEARCH_MAP", StringComparison.OrdinalIgnoreCase)
           || prompt.Contains("PRIVATE_SOURCE_REFERENCE_INDEX", StringComparison.OrdinalIgnoreCase)
           || prompt.Contains("REQUESTED_STRUCTURE", StringComparison.OrdinalIgnoreCase);

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

    private sealed class LlmAdapter :
        ILlmClient,
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentStructuredLlmClient,
        ISourceBackedAgentInputTokenCounter,
        ISourceBackedAgentRuntimeContextProvider
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

        public bool SupportsStructuredOutput => true;

        public async Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        {
            var list = messages?.ToList() ?? new List<(string role, string content)>();
            if (forceJson)
                list.Insert(0, ("system", "Return ONLY valid JSON. No markdown. No extra text."));

            var joinedPrompt = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
            var useJsonResponseFormat = forceJson && ShouldUseLlmAdapterJsonResponseFormat(joinedPrompt);
            var maxTokens = ResolveMaxTokens(list, forceJson);
            var modelVisibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);

            return await _llm.ChatOnceAsync(
                    modelVisibleMessages,
                    _temperature,
                    maxTokens,
                    ct,
                    useJsonResponseFormat)
                .ConfigureAwait(false);
        }

        public async Task<string> CompleteStructuredAsync(
            IReadOnlyList<(string role, string content)> messages,
            LlmStructuredOutputContract contract,
            CancellationToken ct)
        {
            var list = messages?.ToList() ?? new List<(string role, string content)>();
            list.Insert(0, ("system", "Return ONLY JSON matching the supplied schema. No markdown or extra text."));

            var maxTokens = ResolveMaxTokens(list, forceJson: true);
            var modelVisibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
            return await _llm.ChatOnceStructuredAsync(
                    modelVisibleMessages,
                    _temperature,
                    maxTokens,
                    contract,
                    ct)
                .ConfigureAwait(false);
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

            var maxTokens = ResolveMaxTokens(list, forceJson);
            var modelVisibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
            await _llm.ChatStreamAsync(modelVisibleMessages, _temperature, maxTokens, onDelta, ct).ConfigureAwait(false);
        }

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var effectiveMaxTokens = Math.Clamp(maxTokens, 64, 4096);
            return await SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                    SourceBackedLlmCumulativeBudgetContext
                        .ResolveNativeCallClass(tools),
                    SourceBackedLlmCumulativeBudgetContext
                        .IsTerminalNativeCall(tools),
                    messages,
                    tools,
                    effectiveMaxTokens,
                    token => _llm.CountNativeInputTokensAsync(
                        messages,
                        tools,
                        token,
                        requireToolCall),
                    token => _llm.ChatOnceNativeAsync(
                        messages,
                        tools,
                        temperatureOverride ?? _temperature,
                        effectiveMaxTokens,
                        token,
                        requireToolCall),
                    ct)
                .ConfigureAwait(false);
        }

        async Task<SourceBackedAgentCompletion>
            ISourceBackedAgentStructuredLlmClient.CompleteStructuredAsync(
                IReadOnlyList<SourceBackedAgentMessage> messages,
                LlmStructuredOutputContract contract,
                int maxTokens,
                CancellationToken ct,
                double? temperatureOverride)
        {
            var modelVisibleMessages = SourceBackedLlmPromptSanitizer
                .RemoveControlMetadata(messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray());
            var nativeMessages = modelVisibleMessages
                .Select(static message => new SourceBackedAgentMessage(
                    message.role,
                    message.content))
                .ToArray();
            var noTools = Array.Empty<SourceBackedAgentToolDefinition>();
            var effectiveMaxTokens = Math.Clamp(maxTokens, 64, 4096);
            return await SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                    contract.Name,
                    SourceBackedLlmCumulativeBudgetContext
                        .IsTerminalStructuredCall(contract.Name),
                    nativeMessages,
                    noTools,
                    effectiveMaxTokens,
                    token => _llm.CountNativeInputTokensAsync(
                        nativeMessages,
                        noTools,
                        token),
                    token => _llm.ChatOnceStructuredCompletionAsync(
                        modelVisibleMessages,
                        temperatureOverride ?? _temperature,
                        effectiveMaxTokens,
                        contract,
                        token),
                    ct)
                .ConfigureAwait(false);
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => _llm.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);

        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct)
            => _llm.GetNativeRuntimeContextTokensAsync(ct);

        private int ResolveMaxTokens(IReadOnlyList<(string role, string content)> messages, bool forceJson)
        {
            var joined = string.Join('\n', messages.Select(static m => m.content ?? string.Empty));
            return ResolveLlmAdapterMaxTokens(_maxTokens, forceJson, joined);
        }
    }
}
