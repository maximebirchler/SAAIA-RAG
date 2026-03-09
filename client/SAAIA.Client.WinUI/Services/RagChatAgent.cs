using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// RAG Chat Agent (client).
///
/// Règles d'architecture (v2.8.1) : Router → Tools → Writer.
/// - Pas de réponses « codées en dur » côté client.
/// - Pas de listing d'inventaire basé sur le filesystem (source of truth = backend index).
///
/// Nota : le filesystem reste utile uniquement pour la résolution UX (ouvrir un fichier local)
/// via <see cref="DocumentInventory"/> et <see cref="DocumentPathResolver"/>.
/// </summary>
public sealed class RagChatAgent
{
    private readonly ApiClient _api;
    private readonly OpenAiLlmClient _llm;

    private bool _llmEnabled = true;
    private bool _strictMode = false;
    private double _temperature = 0.2;
    private int _maxTokens = 900;
    private string _ragQualityPreset = "balanced"; // quick|balanced|deep

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
        Action<string>? onPhase = null)
    {
        userText ??= "";
        var raw = userText.Trim();

        // Mode dégradé (sans LLM)
        if (!_llmEnabled)
        {
            onPhase?.Invoke("Recherche RAG…");
            var (ans, payload) = await RunSearchOnlyFallbackAsync(userText, category, ct);
            await SimulateStreamingAsync(ans, onDelta, ct);
            return (ans, payload);
        }

        // Sinon: pipeline ToolAgent normal
        var history = (conversationTail ?? Array.Empty<ChatMessageItem>())
            .Where(m => m is not null)
            .Select(m => (role: NormalizeRole(m.Role), content: (m.Content ?? "").Trim()))
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
        var orch = new ToolAgentOrchestrator(_api, llm, _mem, null);

        var (finalAnswer, sourcesPayload) = await orch.RunAsync(history, userText, ct, onPhase);
        await SimulateStreamingAsync(finalAnswer, onDelta, ct);
        return (finalAnswer, sourcesPayload);
    }

    // --------------------
    // Degraded fallback (no LLM)
    // --------------------

    private async Task<(string finalAnswer, object? sourcesPayload)> RunSearchOnlyFallbackAsync(
        string userText,
        string category,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return ("", null);

        var topK = _ragQualityPreset switch
        {
            "quick" => 5,
            "deep" => 12,
            _ => 8
        };

        var resp = await _api.RagSearchAsync(userText, category, topK: topK, mode: "balanced", ct);
        var items = (resp.Items ?? new List<RagItem>())
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();

        var sources = items.Select(x => new
        {
            docPath = (x.DocPath ?? "").Replace('\u005C', '/'),
            docName = x.DocName ?? "",
            pageStart = x.PageStart ?? 1,
            pageEnd = x.PageEnd ?? x.PageStart ?? 1,
            label = $"{(x.DocName ?? "document")} (p.{(x.PageStart ?? 1)}{(((x.PageEnd ?? x.PageStart ?? 1) != (x.PageStart ?? 1)) ? $"–{(x.PageEnd ?? x.PageStart ?? 1)}" : "")})",
            snippet = (x.Text ?? "").Length > 240 ? (x.Text ?? "").Substring(0, 240) + "…" : (x.Text ?? "")
        }).ToList();

        var payload = new { intent = "rag_search", sources };

        var ans = items.Count == 0
            ? "Je n’ai trouvé aucun extrait pertinent dans les documents indexés."
            : "Je ne peux pas utiliser le LLM local pour rédiger la réponse (mode dégradé). Regarde les sources à droite.";

        return (ans, payload);
    }

    // --------------------
    // Misc helpers
    // --------------------

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
        if (onDelta is null) return;
        if (string.IsNullOrEmpty(text)) return;

        const int chunk = 64;
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

            return await _llm.ChatOnceAsync(list, _temperature, _maxTokens, ct);
        }
    }
}