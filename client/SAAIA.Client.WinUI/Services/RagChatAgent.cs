using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;

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
        // 1) PLAN: 1-3 requêtes RAG ou question de clarification
        var plan = await BuildPlanAsync(userText, category, ct);

        if (plan.NeedClarification && !string.IsNullOrWhiteSpace(plan.ClarificationQuestion))
        {
            var q = plan.ClarificationQuestion.Trim();
            var payload = new { plan, searches = Array.Empty<object>() };
            return (q, payload);
        }

        var queries = (plan.Queries?.Count > 0 ? plan.Queries : new List<string> { userText })
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .Distinct()
            .Take(3)
            .ToList();

        // 2) MULTI SEARCH (initiative)
        var searches = new List<RagSearchResponse>();
        foreach (var q in queries)
        {
            var s = await _api.RagSearchAsync(q, category, topK: 6, mode: "balanced", ct);
            searches.Add(s);
        }

        // merge items (dedupe)
        var merged = searches
            .SelectMany(s => s.Items)
            .GroupBy(m => m.ChunkId ?? $"{m.DocId}:{m.PageStart}:{m.ChunkIndex}")
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(m => m.Score)
            .Take(12)
            .ToList();

        // 3) FINAL ANSWER (stream)
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

        var txt = await _llm.ChatOnceAsync(
            new List<(string role, string content)> { ("system", sys), ("user", user) },
            temperature: 0.1,
            maxTokens: 220,
            ct);

        // Parse tolérant
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;

            var need = root.TryGetProperty("needClarification", out var n)
                       && (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False)
                       && n.GetBoolean();

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

            return new Plan(need, q, queries);
        }
        catch
        {
            // fallback: une seule requête = question utilisateur
            return new Plan(false, null, new List<string> { userText });
        }
    }
}
