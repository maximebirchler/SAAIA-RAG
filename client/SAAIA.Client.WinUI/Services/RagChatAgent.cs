using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

public sealed class RagChatAgent
{
    private readonly ApiClient _api;
    private readonly OpenAiLlmClient _llm;

    public RagChatAgent(ApiClient api, OpenAiLlmClient llm)
    {
        _api = api;
        _llm = llm;
    }

    private sealed record Plan(bool NeedClarification, string? ClarificationQuestion, List<string> Queries);

    public async Task<(string finalAnswer, object sourcesPayload)> RunAsync(
        string userText,
        string category,
        IReadOnlyList<ChatMessageItem> conversationTail,
        Action<string> onDelta,
        CancellationToken ct)
    {
        // 1) PLAN
        var plan = await BuildPlanAsync(userText, category, ct);

        if (plan.NeedClarification && !string.IsNullOrWhiteSpace(plan.ClarificationQuestion))
        {
            var q = plan.ClarificationQuestion.Trim();
            var payload = new { plan, searches = Array.Empty<object>(), merged = Array.Empty<object>() };
            return (q, payload);
        }

        var queries = (plan.Queries?.Count > 0 ? plan.Queries : new List<string> { userText })
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .Distinct()
            .Take(3)
            .ToList();

        // 2) MULTI SEARCH
        var searches = new List<RagSearchResponse>();
        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            var s = await _api.RagSearchAsync(q, category, topK: 6, mode: "balanced", ct);
            searches.Add(s);
        }

        // merge
        var merged = searches
            .SelectMany(s => s.Items)
            .GroupBy(m => m.ChunkId ?? $"{m.DocId}:{m.PageStart}:{m.ChunkIndex}")
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(m => m.Score)
            .Take(12)
            .ToList();

        // ✅ Payload construit AVANT le LLM => dispo même si cancel pendant génération
        var sourcesPayload = new
        {
            plan,
            queries,
            searches = searches.Select(s => new
            {
                s.RequestId,
                s.Query,
                s.Metrics,
                s.Items
            }),
            merged
        };

        // 3) FINAL ANSWER
        var sys = """
Tu es SAAIA, un assistant IA local “type ChatGPT” MAIS sans internet.
Ta base de connaissance vient uniquement des SOURCES fournies (extraits de documents).
Comportement attendu :
- Prends des initiatives : reformule, structure, propose des étapes.
- Si les sources ne suffisent pas, dis-le et pose UNE question de clarification.
- Donne une réponse claire et utile.
- Cite tes sources directement dans le texte sous forme : [DocName p.X-Y]
- Ne cite JAMAIS une source non présente.
""";

        var conv = string.Join("\n", conversationTail.TakeLast(6).Select(m =>
            $"{m.Role.ToUpperInvariant()}: {m.Content}".Trim()));

        var sourcesBlock = string.Join("\n\n", merged.Select((m, i) =>
            $"SOURCE {i + 1}\nDOC: {m.DocName}\nPAGES: {m.PageStart}-{m.PageEnd}\nEXTRAIT: {m.Text}"));

        var userPrompt = $"""
CONVERSATION (résumé des derniers messages) :
{conv}

QUESTION UTILISATEUR :
{userText}

SOURCES :
{sourcesBlock}

INSTRUCTION :
Réponds en français. Donne une réponse actionnable. Ajoute des citations [DocName p.X-Y] aux affirmations basées sur les sources.
""";

        var msgs = new List<(string role, string content)>
        {
            ("system", sys),
            ("user", userPrompt)
        };

        var answer = "";

        try
        {
            await _llm.ChatStreamAsync(
                msgs,
                temperature: 0.2,
                maxTokens: 900,
                onDelta: t =>
                {
                    answer += t;
                    onDelta(t);
                },
                ct);

            if (string.IsNullOrWhiteSpace(answer))
            {
                // LLM a répondu vide => fallback (vraie anomalie)
                answer = BuildRagOnlyFallback(userText, merged, "LLM returned empty response");
                onDelta(answer);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ✅ Cancel utilisateur : PAS de mode dégradé, on garde answer partiel (ou vide)
        }
        catch (Exception ex)
        {
            // Vraie erreur LLM => mode dégradé
            answer = BuildRagOnlyFallback(userText, merged, ex.Message);
            onDelta(answer);
        }

        answer = DedupConsecutiveRepeat(answer);
        return (answer, sourcesPayload);
    }

    private async Task<Plan> BuildPlanAsync(string userText, string category, CancellationToken ct)
    {
        var sys = """
Tu es un assistant qui prépare une stratégie de recherche dans une base documentaire (RAG).
Tu dois produire UNIQUEMENT un JSON valide (pas de texte autour).
Format:
{
  "needClarification": true|false,
  "clarificationQuestion": "..." | null,
  "queries": ["...","..."]
}
Règles:
- 1 à 3 queries max, courtes, pertinentes.
- Si la question utilisateur est trop vague, needClarification=true et une seule question claire.
""";

        var user = $"""
Question: {userText}
Catégorie: {category}
""";

        string txt;
        try
        {
            txt = await _llm.ChatOnceAsync(
                new List<(string role, string content)> { ("system", sys), ("user", user) },
                temperature: 0.1,
                maxTokens: 220,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new Plan(false, null, new List<string> { userText });
        }

        if (TryParsePlanJson(txt, out var parsed))
            return parsed;

        var extracted = ExtractFirstJsonObject(txt);
        if (extracted is not null && TryParsePlanJson(extracted, out parsed))
            return parsed;

        return new Plan(false, null, new List<string> { userText });
    }

    private static bool TryParsePlanJson(string json, out Plan plan)
    {
        plan = new Plan(false, null, new List<string>());

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("plan", out var planNode) &&
                planNode.ValueKind == JsonValueKind.Object)
            {
                root = planNode;
            }

            bool need = root.TryGetProperty("needClarification", out var n) &&
                        (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False) &&
                        n.GetBoolean();

            string? q = null;
            if (root.TryGetProperty("clarificationQuestion", out var cq) && cq.ValueKind == JsonValueKind.String)
                q = cq.GetString();

            var queries = new List<string>();
            if (root.TryGetProperty("queries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        queries.Add(el.GetString() ?? "");
            }

            plan = new Plan(need, q, queries);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.Trim();

        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl > 0)
            {
                var endFence = t.IndexOf("```", firstNl + 1, StringComparison.Ordinal);
                if (endFence > firstNl)
                    t = t.Substring(firstNl + 1, endFence - firstNl - 1).Trim();
            }
        }

        var start = t.IndexOf('{');
        if (start < 0) return null;

        bool inStr = false;
        bool esc = false;
        int depth = 0;

        for (int i = start; i < t.Length; i++)
        {
            char c = t[i];

            if (inStr)
            {
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') inStr = false;
                continue;
            }

            if (c == '"') { inStr = true; continue; }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return t.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    private static string BuildRagOnlyFallback(string userText, List<RagItem> merged, string err)
    {
        var top = merged.Take(6).ToList();

        var docs = string.Join("\n", top.Select(m =>
            $"- {m.DocName} p.{m.PageStart}-{m.PageEnd}"));

        var extracts = string.Join("\n\n---\n\n", top.Take(3).Select(m =>
            $"[{m.DocName} p.{m.PageStart}-{m.PageEnd}]\n{m.Text}"));

        return
$@"⚠️ Mode dégradé : le serveur LLM local est indisponible (synthèse impossible).
Erreur : {err}

Question :
{userText}

Meilleures sources trouvées :
{docs}

Extraits :
{extracts}";
    }

    private static string DedupConsecutiveRepeat(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length < 300) return t;

        var prefixLen = Math.Min(80, t.Length);
        var prefix = t.Substring(0, prefixLen);

        var idx = t.IndexOf(prefix, prefixLen, StringComparison.Ordinal);
        if (idx <= 0) return t;

        var a = t.Substring(0, idx).Trim();
        var b = t.Substring(idx).Trim();

        return (a.Length > 0 && a.Equals(b, StringComparison.Ordinal)) ? a : t;
    }

}
