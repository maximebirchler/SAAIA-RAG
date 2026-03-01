using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Client-side “agent outillé” :
/// - Le LLM choisit quoi faire via un routeur JSON (tools).
/// - Le code n’écrit pas de réponses préfabriquées (on fournit des règles + résultats d’outils).
/// - Les sources sont renvoyées via payload (chips UI) et non imposées dans le texte.
/// </summary>
public sealed class RagChatAgent
{
    private readonly ApiClient _api;
    private readonly OpenAiLlmClient _llm;

    // Safe, user-facing tuning.
    private bool _llmEnabled = true;
    private bool _strictMode = false;
    private double _temperature = 0.2;
    private int _maxTokens = 900;
    private string _ragQualityPreset = "balanced"; // quick|balanced|deep

    // Tool-memory (spec v2.8.x): keeps paging + PDFxx mapping between turns.
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

        // Fast-path for very common, tool-only intents.
        // Rationale: avoids the router + writer LLM latency and prevents name/path hallucinations.
        if (LooksLikeDocumentsListQuery(userText))
        {
            onPhase?.Invoke("Documents (liste)…");

            // Fast-path local : inventaire disque des PDFs sous le dossier documents.
            // Avantages : réponse quasi instantanée et cohérente avec l'état réel du disque.
            var root = DocumentPathResolver.GetDocumentsRoot();
            var list = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var rel = Path.GetRelativePath(root, file);
                        rel = rel.Replace(Path.DirectorySeparatorChar, '/');
                        if (!string.IsNullOrWhiteSpace(rel))
                            list.Add(rel);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            list = list.Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                       .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Voici la liste des documents présents sur le serveur :");

            if (list.Count == 0)
            {
                sb.AppendLine("- (aucun document)");
            }
            else
            {
                foreach (var d in list)
                    sb.AppendLine($"- {d}");
            }

            var answer = sb.ToString().TrimEnd();
            await SimulateStreamingAsync(answer, onDelta, ct);
            return (answer, null);
        }

        // Degraded mode: no LLM => best-effort RAG search only.
        if (!_llmEnabled)
        {
            onPhase?.Invoke("Recherche RAG…");
            var (ans, payload) = await RunSearchOnlyFallbackAsync(userText, category, ct);
            await SimulateStreamingAsync(ans, onDelta, ct);
            return (ans, payload);
        }

        // Convert UI history into OpenAI roles.
        var history = (conversationTail ?? Array.Empty<ChatMessageItem>())
            .Where(m => m is not null)
            .Select(m => (role: NormalizeRole(m.Role), content: (m.Content ?? "").Trim()))
            .Where(m => !string.IsNullOrWhiteSpace(m.content))
            .TakeLast(12)
            .ToList();

        // Inject soft preferences as context (not hard-coded answers).
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

        // LLM adapter: non-stream call for router/answer; we simulate streaming on the UI.
        var llm = new LlmAdapter(_llm, _temperature, _maxTokens);
        var orch = new ToolAgentOrchestrator(_api, llm, _mem);

        var (answer, sourcesPayload) = await orch.RunAsync(history, userText, ct, onPhase);

        // Progressive UX required by spec (even if the LLM endpoint does not stream).
        await SimulateStreamingAsync(answer, onDelta, ct);

        return (answer, sourcesPayload);
    }

    // --------------------
    // Fallback (LLM disabled)
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

        // Provide sources for UI chips even in degraded mode.
        var sources = items.Select(x => new
        {
            docPath = (x.DocPath ?? "").Replace('\\', '/'),
            docName = x.DocName ?? "",
            pageStart = x.PageStart ?? 1,
            pageEnd = x.PageEnd ?? x.PageStart ?? 1,
            label = $"{(x.DocName ?? "document")} (p.{(x.PageStart ?? 1)}{(((x.PageEnd ?? x.PageStart ?? 1) != (x.PageStart ?? 1)) ? $"–{(x.PageEnd ?? x.PageStart ?? 1)}" : "")})",
            snippet = (x.Text ?? "").Length > 240 ? (x.Text ?? "").Substring(0, 240) + "…" : (x.Text ?? "")
        }).ToList();

        var payload = new { intent = "rag_search", sources };

        // Minimal text (no template-heavy formatting).
        var ans = items.Count == 0
            ? "Je n’ai trouvé aucun extrait pertinent dans les documents indexés."
            : "Je ne peux pas utiliser le LLM local pour rédiger la réponse (mode dégradé). Regarde les sources à droite.";

        return (ans, payload);
    }

    // --------------------
    // Helpers
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

    private static bool LooksLikeDocumentsListQuery(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return false;

        var t = userText.Trim().ToLowerInvariant();

        // FR
        if ((t.Contains("liste") || t.Contains("lister") || t.Contains("affiche")) && t.Contains("document"))
            return true;
        if (t.Contains("quels sont") && t.Contains("documents"))
            return true;
        if (t.Contains("documents présents") || t.Contains("documents presents"))
            return true;

        // EN
        if ((t.Contains("list") || t.Contains("show")) && t.Contains("document"))
            return true;

        return false;
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

    /// <summary>
    /// Adapter between the existing streaming-capable OpenAiLlmClient and the tool-agent ILlmClient.
    /// </summary>
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
            // The ToolAgent prompts already enforce JSON strictly when needed.
            // We keep a small extra nudge in case the local endpoint is lax.
            var list = messages?.ToList() ?? new List<(string role, string content)>();
            if (forceJson)
            {
                list.Insert(0, ("system", "Return ONLY valid JSON. No markdown. No extra text."));
            }

            return await _llm.ChatOnceAsync(list, _temperature, _maxTokens, ct);
        }
    }
}
