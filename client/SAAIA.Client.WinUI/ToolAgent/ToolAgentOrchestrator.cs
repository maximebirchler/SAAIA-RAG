using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class ToolAgentOrchestrator
{
    private readonly ApiClient _api;
    private readonly ILlmClient _llm;
    private readonly ToolMemory _mem;

    // limite “sécurité perf” (spec : max 5 RAG/calls par requête)
    private const int MaxToolCalls = 8;

    public ToolAgentOrchestrator(ApiClient api, ILlmClient llm, ToolMemory mem)
    {
        _api = api;
        _llm = llm;
        _mem = mem;
    }

    /// <summary>
    /// Exécute le pipeline Router → Tools → Answer.
    /// </summary>
    /// <param name="onPhase">Callback UX (status bar) : "Routeur…", "Recherche documents…", "Rédaction…", etc.</param>
    public async Task<(string finalAnswer, object? sourcesPayload)> RunAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        Action<string>? onPhase = null)
    {
        // 1) Router (LLM) => plan JSON strict
        onPhase?.Invoke("Routeur…");
        var plan = await RouterAsync(chatHistory, userMessage, ct);

        _mem.LastLanguage = plan.Language;

        // Clarifications (0-2) : l’IA décide. Ici on respecte le plan.
        if (plan.NeedClarification && plan.ClarificationQuestions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var q in plan.ClarificationQuestions.Take(2))
                sb.AppendLine($"- {q}");
            return (sb.ToString().Trim(), null);
        }

        // 2) Execute tools
        onPhase?.Invoke("Outils…");
        var toolResults = await ExecuteToolsAsync(plan, userMessage, ct, onPhase);

        // Fast-path: for list_documents we can return a deterministic response without running
        // the (slow) writer LLM, and without risking filename/path hallucinations.
        if (plan.Intent == "list_documents" || (plan.ToolCalls.Count == 1 && plan.ToolCalls[0].Name == "documents.list"))
        {
            var fast = BuildDocumentsListAnswer(toolResults);
            await SimulateStreamingAsync(fast, onDelta, ct);
            return (fast, null);
        }

        // 3) Answer (LLM) => texte (et éventuellement sources)
        onPhase?.Invoke("Rédaction…");
        var (answer, sources) = await AnswerAsync(chatHistory, userMessage, plan, toolResults, ct);

        // mémoriser les sources utilisées pour “source du PDFxx” après une Q/R
        if (sources is { Count: > 0 })
            _mem.LastSourcesUsed = sources;

        // payload attendu par ton UI actuelle : { sources: [...] } ou null
        object? sourcesPayload = null;
        if (sources is { Count: > 0 })
        {
            sourcesPayload = new
            {
                sources = sources.Select(x => new { docPath = x.DocPath, pageStart = x.PageStart, pageEnd = x.PageEnd, label = x.Label }).ToList()
            };
        }

        return (answer, sourcesPayload);
    }

	private static string BuildDocumentsListAnswer(ToolResults toolResults)
	{
		try
		{
			var item = toolResults.Items.FirstOrDefault(x => x.Name == "documents.list");
			if (item == null)
				return "Je n'ai trouvé aucun document (la liste est vide).";

			var json = item.Result.GetRawText();
			var resp = JsonSerializer.Deserialize<DocumentCatalogResponse>(json);
			var docs = (resp?.Items ?? new List<DocumentCatalogItem>())
				.Where(x => !string.IsNullOrWhiteSpace(x.DocPath))
				.OrderBy(x => x.DocPath, StringComparer.OrdinalIgnoreCase)
				.ToList();

			var sb = new StringBuilder();
			sb.AppendLine("Voici la liste des documents présents sur le serveur :");

			if (docs.Count == 0)
			{
				sb.AppendLine("- (aucun document)");
			}
			else
			{
				foreach (var d in docs)
					sb.AppendLine($"- {d.DocPath}");
			}

			return sb.ToString().TrimEnd();
		}
		catch
		{
			return "Voici la liste des documents présents sur le serveur.";
		}
	}

    private async Task<RouterPlan> RouterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct)
    {
        var manifestJson = ToolManifest.BuildManifestJson();
        var toolbook = ToolManifest.ToolbookText;

        // Contexte mémoire minimal (évite heuristiques hardcodées)
        var memoryCtx = new
        {
            lastLanguage = _mem.LastLanguage,
            lastList = new
            {
                offset = _mem.LastListOffset,
                limit = _mem.LastListLimit,
                category = _mem.LastListCategory,
                q = _mem.LastListQuery,
                total = _mem.LastListTotal
            }
        };

        var system = $@"
You are SAAIA Router. Output ONLY valid JSON (no markdown).
Decide which tools to call and the language to answer in.

Tool manifest (JSON):
{manifestJson}

Toolbook:
{toolbook}

Rules:
- Use tools ONLY from the manifest.
- For inventory questions (documents/categories), prefer documents.list/search or rag.categories; do NOT use rag.search.
- For technical/factual questions about norms/procedures (e.g., ATEX zone 2/22), use rag.search (mode strict is recommended).
- Language: answer in the user's language (detect automatically).
- 0 to 2 clarification questions max if needed.
- If user asks to continue a list, use memory lastList.offset and increase offset.
- Output schema exactly like:
{{""mode"":""auto|standard|strict"",""language"":""fr|en|..."",""intent"":""..."",""responseFormat"":""auto"",
""needClarification"":false,""clarificationQuestions"":[],""toolCalls"":[{{""name"":""..."",""args"":{{...}}}}]}}
";

        var user = $@"
MEMORY (json):
{JsonSerializer.Serialize(memoryCtx)}

CHAT_TAIL (for context):
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}
";

        var raw = await _llm.CompleteAsync(new[]
        {
            ("system", system),
            ("user", user)
        }, forceJson: true, ct);

        if (!TryExtractJsonObject(raw, out var planJson))
        {
            // Fallback safe: conversationnel sans tools
            return new RouterPlan { Mode = "auto", Language = _mem.LastLanguage, Intent = "chat" };
        }

        try
        {
            var plan = JsonSerializer.Deserialize<RouterPlan>(planJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new RouterPlan();

            // Safety: clamp tool calls
            if (plan.ToolCalls.Count > MaxToolCalls)
                plan.ToolCalls = plan.ToolCalls.Take(MaxToolCalls).ToList();

            if (string.IsNullOrWhiteSpace(plan.Language))
                plan.Language = _mem.LastLanguage;

            return plan;
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = _mem.LastLanguage, Intent = "chat" };
        }
    }

    private async Task<ToolResults> ExecuteToolsAsync(RouterPlan plan, string userMessage, CancellationToken ct, Action<string>? onPhase)
    {
        var results = new ToolResults();

        foreach (var call in plan.ToolCalls)
        {
            onPhase?.Invoke(PhaseLabelForTool(call.Name));

            var sw = Stopwatch.StartNew();
            try
            {
                JsonElement res = call.Name switch
                {
                    "rag.categories" => await _api.RagCategoriesAsync(ct),
                    "documents.list" => await ExecDocumentsListAsync(call.Args, ct),
                    "documents.search" => await ExecDocumentsSearchAsync(call.Args, ct),
                    "documents.get" => await ExecDocumentsGetAsync(call.Args, ct),
                    "rag.search" => await ExecRagSearchAsync(call.Args, ct),
                    "sources.resolve" => ExecSourcesResolve(call.Args),
                    _ => JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                };

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Result = res,
                    DurationMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Error = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = JsonDocument.Parse("{\"error\":\"tool_failed\"}").RootElement
                });
            }
        }

        return results;
    }

    private async Task<(string answer, List<ToolMemory.SourceRef>? sources)> AnswerAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        CancellationToken ct)
    {
        // IMPORTANT : pas de phrases figées dans le code.
        // On donne au LLM des règles, et les résultats d’outils.
        var system = $@"
You are SAAIA assistant. You must answer ONLY using the provided tool results.
No internet. No invention when the question is documentary/technical.

Language: {plan.Language}
Mode:
- standard: natural & concise.
- strict: no invention; if insufficient sources, explain what is missing and ask 1 question.
- auto: choose based on user intent.

Rules:
- If tools returned a documents list, present the documents clearly (one per line, keep PDF labels if present in results).
- If list is not complete, propose continuing; if complete, indicate it's the end.
- For inventory (listing), do NOT output sources.
- For sources.resolve (""source du PDFxx""), you MAY output a single source.
- For rag.search, output sources as an array of SourceRef (docPath/pageStart/pageEnd/label) that you actually relied on.
Return ONLY valid JSON (no markdown) with schema:
{{""finalAnswer"":""..."",""sources"":[]}}
";

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 10)}

USER_MESSAGE:
{userMessage}

TOOL_RESULTS (json):
{SerializeToolResults(toolResults)}
";

        var raw = await _llm.CompleteAsync(new[]
        {
            ("system", system),
            ("user", user)
        }, forceJson: true, ct);

        // --- Robust envelope parsing ---
        if (!TryExtractJsonObject(raw, out var jsonCandidate))
        {
            // Do NOT leak internal JSON-ish content to the user.
            if (TryExtractFinalAnswerFromRaw(raw, out var extracted))
                return (extracted.Trim(), null);

            return (BuildJsonEnvelopeError(plan.Language), null);
        }

        if (!TryParseAnswerEnvelope(jsonCandidate, out var finalAnswer, out var sourcesFromLlm))
        {
            if (TryExtractFinalAnswerFromRaw(raw, out var extracted))
                return (extracted.Trim(), null);

            return (BuildJsonEnvelopeError(plan.Language), null);
        }

        finalAnswer = (finalAnswer ?? "").Trim();

        // --- Sources policy enforcement ---
        var usedRagSearch = toolResults.Items.Any(x => x.ToolName == "rag.search");
        var usedSourcesResolve = toolResults.Items.Any(x => x.ToolName == "sources.resolve");

        var sources = sourcesFromLlm;

        if (usedRagSearch)
        {
            // OK: keep sources from LLM, but sanitize.
            sources = (sources ?? new List<ToolMemory.SourceRef>())
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.DocPath))
                .ToList();
        }
        else if (usedSourcesResolve)
        {
            // OK: single source expected. If the LLM forgot, we can synthesize it from tool result.
            sources ??= new List<ToolMemory.SourceRef>();
            if (sources.Count == 0)
            {
                var resolved = TryBuildSourceFromResolveResult(toolResults);
                if (resolved is not null)
                    sources.Add(resolved);
            }

            // Limit to 1 source to match UX.
            if (sources.Count > 1)
                sources = sources.Take(1).ToList();
        }
        else
        {
            // Inventory / conversation: NEVER show sources.
            sources = null;
        }

        return (finalAnswer, sources);
    }

    private static bool TryParseAnswerEnvelope(string json, out string? finalAnswer, out List<ToolMemory.SourceRef>? sources)
    {
        finalAnswer = null;
        sources = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("finalAnswer", out var a) && a.ValueKind == JsonValueKind.String)
                finalAnswer = a.GetString();
            else
                finalAnswer = "";

            if (root.TryGetProperty("sources", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ToolMemory.SourceRef>();
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    var docPath = s.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;

                    var ps = s.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = s.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
                    var label = s.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

                    list.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath,
                        PageStart = ps,
                        PageEnd = pe,
                        Label = label
                    });
                }

                sources = list;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceFromResolveResult(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "sources.resolve");
            if (item is null) return null;

            var res = item.Result;
            if (res.ValueKind != JsonValueKind.Object) return null;
            if (!res.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object) return null;

            var docPath = src.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(docPath)) return null;

            var ps = src.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
            var pe = src.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
            var label = src.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

            return new ToolMemory.SourceRef
            {
                DocPath = docPath,
                PageStart = ps,
                PageEnd = pe,
                Label = string.IsNullOrWhiteSpace(label) ? $"{docPath} (p.{ps})" : label
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Tools exec helpers ----------------

    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(category, q, limit, offset, ct);

        // Update memory (PDF mapping)
        _mem.LastListCategory = category;
        _mem.LastListQuery = q;
        _mem.LastListLimit = limit;
        _mem.LastListOffset = offset;

        // Map PDF labels on client side for continuity
        var docItems = _api.ParseDocumentItems(res);
        _mem.LastListedDocuments = docItems;

        return res;
    }

    private async Task<JsonElement> ExecDocumentsSearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = args.GetProperty("q").GetString() ?? "";
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsSearchAsync(q, category, limit, offset, ct);

        _mem.LastListCategory = category;
        _mem.LastListQuery = q;
        _mem.LastListLimit = limit;
        _mem.LastListOffset = offset;

        _mem.LastListedDocuments = _api.ParseDocumentItems(res);

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        var docId = args.GetProperty("docId").GetString() ?? "";
        return await _api.DocumentsGetAsync(docId, ct);
    }

    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetProperty("query").GetString() ?? "";
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;

        return await _api.RagSearchAsync(query, topK, category, ct);
    }

    private JsonElement ExecSourcesResolve(JsonElement args)
    {
        var pdfRef = args.GetProperty("pdfRef").GetString() ?? "";
        // PDF34 => index 34 (1-based)
        if (!TryParsePdfRef(pdfRef, out var idx1Based))
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        var idx0 = idx1Based - 1;
        if (idx0 < 0 || idx0 >= _mem.LastListedDocuments.Count)
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        var doc = _mem.LastListedDocuments[idx0];

        // Après inventaire => p.1 par défaut.
        var src = new ToolMemory.SourceRef
        {
            DocPath = doc.DocPath,
            PageStart = 1,
            PageEnd = 1,
            Label = $"{doc.DocName} (p.1)"
        };

        var payload = new
        {
            source = new { docPath = src.DocPath, pageStart = src.PageStart, pageEnd = src.PageEnd, label = src.Label }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    // ---------------- Utility ----------------

    private static bool TryParsePdfRef(string s, out int idx1Based)
    {
        idx1Based = 0;
        s = s.Trim().ToUpperInvariant();
        if (!s.StartsWith("PDF")) return false;
        var numPart = s.Substring(3);
        return int.TryParse(numPart, out idx1Based);
    }

    private static string SerializeToolResults(ToolResults tr)
    {
        var o = tr.Items.Select(x => new
        {
            tool = x.ToolName,
            error = x.Error,
            durationMs = x.DurationMs,
            result = x.Result
        }).ToList();

        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string SerializeTail(IReadOnlyList<(string role, string content)> hist, int maxTurns)
    {
        var tail = hist.TakeLast(maxTurns).Select(m => new { role = m.role, content = m.content }).ToList();
        return JsonSerializer.Serialize(tail);
    }

    private static bool TryExtractJsonObject(string raw, out string json)
    {
        json = (raw ?? "").Trim();

        // enlever ```json ... ``` si présent
        if (json.StartsWith("```"))
        {
            var i = json.IndexOf('\n');
            if (i >= 0) json = json.Substring(i + 1);
            json = json.Replace("```", "").Trim();
        }

        // trouver premier '{' et dernier '}'
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return false;

        json = json.Substring(start, end - start + 1);
        return true;
    }

    private static bool TryExtractFinalAnswerFromRaw(string raw, out string finalAnswer)
    {
        finalAnswer = "";
        raw ??= "";

        // 1) Try to parse a JSON substring first
        if (TryExtractJsonObject(raw, out var json) && TryParseAnswerEnvelope(json, out var fa, out _))
        {
            finalAnswer = (fa ?? "").Trim();
            return !string.IsNullOrWhiteSpace(finalAnswer);
        }

        // 2) Regex fallback: "finalAnswer":"..."
        var m = Regex.Match(raw, "\\\"finalAnswer\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Singleline);
        if (m.Success)
        {
            var v = m.Groups["v"].Value;
            try
            {
                // Use JSON parser to unescape
                finalAnswer = JsonSerializer.Deserialize<string>("\"" + v + "\"") ?? "";
                finalAnswer = finalAnswer.Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
            catch
            {
                // best-effort
                finalAnswer = v.Replace("\\n", "\n").Replace("\\t", "\t").Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
        }

        return false;
    }

    private static string BuildJsonEnvelopeError(string? lang)
    {
        var l = (lang ?? "fr").Trim().ToLowerInvariant();
        if (l.StartsWith("fr"))
            return "⚠️ La réponse interne est arrivée dans un format inattendu. Peux-tu relancer ta question (ou reformuler) ?";
        if (l.StartsWith("en"))
            return "⚠️ The internal response arrived in an unexpected format. Please retry (or rephrase).";

        // generic
        return "⚠️ Unexpected internal response format. Please retry.";
    }

    private static string PhaseLabelForTool(string toolName)
        => toolName switch
        {
            "documents.list" or "documents.search" or "documents.get" => "Recherche documents…",
            "rag.categories" => "Chargement catégories…",
            "rag.search" => "Recherche RAG…",
            "sources.resolve" => "Résolution source…",
            _ => "Outils…"
        };
}
