using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private readonly ApiClient _api;
    private readonly ILlmClient _llm;
    private readonly ToolMemory _mem;
    private readonly AppSettings? _settings;
    private long _lastRouterMs;
    private long _lastToolsMs;
    private long _lastWriterMs;
    private long _lastTotalMs;
    private List<(string tool, long durationMs, bool ok)> _lastToolDurations = new();

    // limite “sécurité perf” (spec : max 5 RAG/calls par requête)
    private const int MaxToolCalls = 8;

    internal ToolAgentOrchestrator(ApiClient api, ILlmClient llm, ToolMemory mem, AppSettings? settings = null)
    {
        _api = api;
        _llm = llm;
        _mem = mem;
        _settings = settings;
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
        var swTotalPipeline = Stopwatch.StartNew();

        // 1) Router (LLM) => plan JSON strict
        onPhase?.Invoke("Routeur…");

        var swRouter = Stopwatch.StartNew();
        RouterPlan plan;
        if (TryBuildDeterministicPlan(chatHistory, userMessage, out var det))
            plan = det;
        else
            plan = await RouterAsync(chatHistory, userMessage, ct);
        swRouter.Stop();
        _lastRouterMs = swRouter.ElapsedMilliseconds;

        _mem.LastLanguage = plan.Language;

        // Clarifications (0-2)
        if (plan.NeedClarification && plan.ClarificationQuestions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var q in plan.ClarificationQuestions.Take(2))
                sb.AppendLine($"- {q}");
            return (sb.ToString().Trim(), null);
        }

        // 2) Execute tools (local first)
        onPhase?.Invoke("Outils…");

        var localItems = ExecuteLocalTools(plan, chatHistory);
        if (localItems.Count > 0)
            plan.ToolCalls = plan.ToolCalls.Where(c => !string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)).ToList();

        var swTools = Stopwatch.StartNew();
        var toolResults = await ExecuteToolsAsync(plan, userMessage, ct, onPhase);
        swTools.Stop();
        _lastToolsMs = swTools.ElapsedMilliseconds;
        _lastToolDurations = toolResults.Items.Select(x => (x.ToolName, x.DurationMs, string.IsNullOrWhiteSpace(x.Error))).ToList();
        if (localItems.Count > 0)
            toolResults.Items.InsertRange(0, localItems);

        // Fast-path: sources.resolve (deterministic, avoid mixed answers)
        if (toolResults.Items.Any(x => x.ToolName == "sources.resolve"))
        {
            var src = TryBuildSourceFromResolveResult(toolResults);
            if (src is null || string.IsNullOrWhiteSpace(src.DocPath))
                return (plan.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "The document could not be found." : "Le document n'a pas pu être trouvé.", null);

            var dp = (src.DocPath ?? "").Replace('\\','/').TrimStart('/');
            var label = (src.Label ?? "").Trim().Replace("|", " ").Replace("]", ")");
            var txt = plan.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? $"Source:\n1. [[open|{dp}|{Math.Max(1, src.PageStart)}|{label}]]"
                : $"Source :\n1. [[open|{dp}|{Math.Max(1, src.PageStart)}|{label}]]";

            var payload = new { sources = new[] { new { docPath = dp, pageStart = src.PageStart, pageEnd = src.PageEnd, label } } };
            return (txt, payload);
        }

        // Fast-path: list of questions previously asked (must win over any accidental inventory tool calls)
        if (
            toolResults.Items.Any(x => x.ToolName == "meta.list_questions") &&
            userMessage.Contains("question", StringComparison.OrdinalIgnoreCase)
        )
        {
            var fast = BuildQuestionsListAnswer(toolResults);
            if (!string.IsNullOrWhiteSpace(fast))
                return (fast, null);
        }

        // Fast-path: deterministic inventory responses (no sources, no hallucinations)
        // IMPORTANT: inventory data itself should come from backend tools; the client must not
        // rebuild the tree from a flat list when a dedicated backend tree tool exists.
        var hasDocTree = toolResults.Items.Any(x => x.ToolName == "documents.tree");
        var onlySafeTreeTools = toolResults.Items.All(x => x.ToolName is "documents.tree" or "meta.list_questions");
        if (hasDocTree && onlySafeTreeTools)
        {
            var fastTree = BuildDocumentsTreeAnswer(toolResults, plan.Language);
            return (fastTree, null);
        }

        var hasDocList = toolResults.Items.Any(x => x.ToolName is "documents.list" or "documents.search");
        var onlySafeInventoryTools = toolResults.Items.All(x => x.ToolName is
            "documents.list" or "documents.search" or "rag.categories" or "meta.list_questions");

        if (hasDocList && onlySafeInventoryTools)
        {
            var fastList = BuildDocumentsListAnswer(toolResults, plan.Language);
            return (fastList, null);
        }

        // 3) Answer (LLM) => texte (et éventuellement sources)
        onPhase?.Invoke("Rédaction…");
        var swWriter = Stopwatch.StartNew();
        var (answer, sources) = await AnswerAsync(chatHistory, userMessage, plan, toolResults, ct);
        swWriter.Stop();
        _lastWriterMs = swWriter.ElapsedMilliseconds;

        // Clean up common markdown artifacts (UX)
        answer = (answer ?? string.Empty).Replace("**", string.Empty).Trim();

        // mémoriser les sources utilisées pour “source du PDFxx” après une Q/R
        if (sources is { Count: > 0 })
            _mem.LastSourcesUsed = sources;

        // Inject clickable sources in the bubble (token syntax)
        if (sources is { Count: > 0 })
            answer = InjectInlineSources(answer, sources, plan.Language);

        // payload attendu par ton UI actuelle : { sources: [...] } ou null
        object? sourcesPayload = null;
        if (sources is { Count: > 0 })
        {
            sourcesPayload = new
            {
                sources = sources.Select(x => new { docPath = x.DocPath, pageStart = x.PageStart, pageEnd = x.PageEnd, label = x.Label }).ToList()
            };
        }

        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;

        return (answer, sourcesPayload);
    }

    private static List<ToolResults.Item> ExecuteLocalTools(RouterPlan plan, IReadOnlyList<(string role, string content)> chatHistory)
    {
        var items = new List<ToolResults.Item>();

        if (plan.ToolCalls.Any(c => string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)))
        {
            var questions = chatHistory
                .Where(m => string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.content ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .TakeLast(50)
                .ToList();

            var payload = new { questions };
            items.Add(new ToolResults.Item
            {
                ToolName = "meta.list_questions",
                Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
                DurationMs = 0
            });
        }

        return items;
    }

    private string BuildDocumentsListAnswer(ToolResults toolResults, string language)
    {
        // We always format deterministically from tool results to avoid hallucinated paths.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (item is null)
            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "No documents found."
                : "Aucun document trouvé.";

        try
        {
            var (docs, _, _, _, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            var list = DocumentListHelper.BuildUserText(docs, endOfList, dropped);
            if (!string.IsNullOrWhiteSpace(list))
                return list;

            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "No documents found."
                : "Aucun document trouvé.";
        }
        catch
        {
            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Unable to list documents (unexpected error)."
                : "Impossible de lister les documents (erreur inattendue).";
        }
    }

    private string BuildDocumentsTreeAnswer(ToolResults toolResults, string language)
    {
        // Deterministic tree rendered directly from the dedicated backend documents.tree tool.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree");
        if (item is null)
            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "No tree result returned by the backend."
                : "Le backend n'a renvoyé aucune arborescence.";

        try
        {
            if (item.Result.ValueKind == System.Text.Json.JsonValueKind.Object
                && item.Result.TryGetProperty("markdown", out var md)
                && md.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var markdown = (md.GetString() ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(markdown))
                    return markdown;
            }

            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "The tree is available, but no markdown rendering was returned by the backend."
                : "L’arborescence est disponible, mais aucun rendu markdown n'a été renvoyé par le backend.";
        }
        catch
        {
            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Unable to display the tree (unexpected error)."
                : "Impossible d’afficher l’arborescence (erreur inattendue).";
        }
    }

    private string BuildQuestionsListAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "meta.list_questions");
            if (item is null) return string.Empty;

            if (item.Result.ValueKind != JsonValueKind.Object || !item.Result.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var i = 1;
            var sb = new StringBuilder();
            foreach (var q in arr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (s.Length == 0) continue;
                sb.AppendLine($"{i}. {s}");
                i++;
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string InjectInlineSources(string answer, List<ToolMemory.SourceRef> sources, string language)
    {
        if (sources is null || sources.Count == 0) return answer;

        // If the LLM already emitted clickable tokens, do not add more.
        if (answer.Contains("[[open|", StringComparison.OrdinalIgnoreCase))
            return answer;

        var heading = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Sources" : "Sources";

        var sb = new StringBuilder();
        sb.AppendLine(answer.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"{heading}:");

        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var dp = (s.DocPath ?? "").Replace('\\', '/').TrimStart('/');
            var mainCat = "";
            var slash = dp.IndexOf('/');
            if (slash > 0) mainCat = dp.Substring(0, slash);

            var label = (s.Label ?? "").Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                var fn = Path.GetFileName(dp);
                label = string.IsNullOrWhiteSpace(mainCat) ? fn : $"{fn} ({mainCat})";
            }

            // Keep label minimal + safe.
            label = label.Replace("|", " ").Replace("]", ")");

            sb.AppendLine($"{i + 1}. [[open|{dp}|{Math.Max(1, s.PageStart)}|{label}]]");
        }

        return sb.ToString().TrimEnd();
    }

    private bool TryBuildDeterministicPlan(
    IReadOnlyList<(string role, string content)> chatHistory,
    string userMessage,
    out RouterPlan plan)
{
    plan = new RouterPlan
    {
        Mode = "auto",
        Language = GuessLanguage(userMessage),
        Intent = "chat",
        ResponseFormat = "auto"
    };

    // 1) "source du 2", "PDF01", "et le 3 ?" (si une liste existe)
    if (TryExtractPdfRefForSource(userMessage, out var pdfRef))
    {
        plan.Intent = "source_resolve";
        plan.ToolCalls = new List<RouterPlan.ToolCall>
        {
            new RouterPlan.ToolCall
            {
                Name = "sources.resolve",
                Args = JsonDocument.Parse(JsonSerializer.Serialize(new { pdfRef })).RootElement
            }
        };
        return true;
    }

    // 2) Arborescence => on force le vrai tool backend documents.tree
    if (LooksLikeTreeRequest(userMessage))
    {
        plan.Intent = "inventory.tree";
        plan.ToolCalls = new List<RouterPlan.ToolCall>
        {
            new RouterPlan.ToolCall
            {
                Name = "documents.tree",
                Args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = (string?)null, depth = 20, format = "markdown" })).RootElement
            }
        };
        return true;
    }

    return false;
}

private bool TryExtractPdfRefForSource(string userMessage, out string pdfRef)
{
    pdfRef = "";
    var s = (userMessage ?? "").Trim();
    if (s.Length == 0) return false;

    // "et le 3 ?" / "le 2" => si on a déjà listé des docs
    var mShort = Regex.Match(s, @"^(?:et\s+)?(?:la\s+|le\s+)?(?<n>\d{1,4})\s*[\?\.!]*$", RegexOptions.IgnoreCase);
    if (mShort.Success && int.TryParse(mShort.Groups["n"].Value, out var n) && n > 0)
    {
        if (_mem.LastListedDocuments is { Count: > 0 } && n <= _mem.LastListedDocuments.Count)
        {
            pdfRef = n.ToString();
            return true;
        }
    }

    // Otherwise, we require that the user is *explicitly asking for a source / link / open action*.
    // IMPORTANT: generic mentions like "document 2" must NOT trigger sources.resolve.
    // This keeps the Router free for prompts like "de quoi parle le document 2 ?".
    var wantsOpenOrSource =
        s.Contains("source", StringComparison.OrdinalIgnoreCase)
        || s.Contains("lien", StringComparison.OrdinalIgnoreCase)
        || s.Contains("link", StringComparison.OrdinalIgnoreCase)
        || s.Contains("open", StringComparison.OrdinalIgnoreCase)
        || s.Contains("ouvrir", StringComparison.OrdinalIgnoreCase)
        || s.Contains("ouvre", StringComparison.OrdinalIgnoreCase)
        // German (common substrings)
        || s.Contains("öffn", StringComparison.OrdinalIgnoreCase)
        || s.Contains("oeffn", StringComparison.OrdinalIgnoreCase);

    if (!wantsOpenOrSource)
        return false;

    var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
    if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
    {
        pdfRef = $"PDF{nPdf:00}";
        return true;
    }

    // "source du 2" / "lien du 2" / "open 2" / "ouvrir le document 2" ...
    var mNum = Regex.Match(s, @"(?i)\b(?:source|lien|link|open|ouvrir|ouvre|document|pdf|file)\b[^\d]*(?<n>\d{1,4})\b");
    if (mNum.Success && int.TryParse(mNum.Groups["n"].Value, out var n2) && n2 > 0)
    {
        pdfRef = n2.ToString();
        return true;
    }

    // Fallback: if it looks like an open request and contains a number, accept it.
    // Example: "je veux pouvoir ouvrir le document 3".
    var mAnyNum = Regex.Match(s, @"\b(?<n>\d{1,4})\b");
    if (mAnyNum.Success && int.TryParse(mAnyNum.Groups["n"].Value, out var n3) && n3 > 0)
    {
        pdfRef = n3.ToString();
        return true;
    }

    return false;
}

private static bool LooksLikeTreeRequest(string userMessage)
{
    var s = (userMessage ?? "").Trim();
    if (s.Length == 0) return false;

    return s.Contains("arborescence", StringComparison.OrdinalIgnoreCase)
        || s.Contains("tree", StringComparison.OrdinalIgnoreCase)
        || s.Contains("structure", StringComparison.OrdinalIgnoreCase)
        || s.Contains("hiérarchie", StringComparison.OrdinalIgnoreCase)
        || s.Contains("hierarchie", StringComparison.OrdinalIgnoreCase);
}

private string GuessLanguage(string userMessage)
{
    var s = (userMessage ?? "").Trim();
    if (s.Length == 0) return _mem.LastLanguage;

    var frHints = new[] { "je ", "veux", "donne", "affiche", "arborescence", "merci", "stp", "source du", "documents" };
    var enHints = new[] { "please", "show", "give", "list", "source of", "documents" };

    var fr = frHints.Count(h => s.Contains(h, StringComparison.OrdinalIgnoreCase));
    var en = enHints.Count(h => s.Contains(h, StringComparison.OrdinalIgnoreCase));

    if (en > fr) return "en";
    if (fr > 0) return "fr";
    return _mem.LastLanguage;
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
- For inventory *documents list/search*, use documents.list or documents.search ONLY (do NOT call rag.categories unless the user explicitly asks for categories). Never use rag.search for inventory.
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
                    "documents.get" => await ExecDocumentsGetResolvedAsync(call.Args, ct),
                    "documents.count" => await ExecDocumentsCountAsync(call.Args, ct),
                    "documents.tree" => await ExecDocumentsTreeAsync(call.Args, ct),
                    "documents.stats" => await ExecDocumentsStatsAsync(call.Args, ct),
                    "rag.search" => await ExecRagSearchAsync(call.Args, ct),
                    "rag.multi_search" => await ExecRagMultiSearchAsync(call.Args, ct),
                    "rag.summarize_live" => await ExecRagSummarizeLiveAsync(call.Args, ct),
                    "sources.resolve" => ExecSourcesResolveV2(call.Args),
                    "summary.get" => await ExecSummaryGetAsync(call.Args, ct),
                    "summary.exists" => await ExecSummaryExistsAsync(call.Args, ct),
                    "summary.search" => await ExecSummarySearchAsync(call.Args, ct),
                    "admin.summary.missing" => await ExecAdminSummaryMissingAsync(call.Args, ct),
                    "admin.summary.request" => await ExecAdminSummaryRequestAsync(call.Args, ct),
                    "admin.summary.submit" => await ExecAdminSummarySubmitAsync(call.Args, ct),
                    "admin.summary.status" => await ExecAdminSummaryStatusAsync(call.Args, ct),
                    "admin.summary.delete" => await ExecAdminSummaryDeleteAsync(call.Args, ct),
                    "admin.summary.generate" => await ExecAdminSummaryGenerateAsync(call.Args, ct),
                    "admin.catalog.health" => await ExecAdminCatalogHealthAsync(ct),
                    "admin.catalog.rescan_now" => await ExecAdminCatalogRescanNowAsync(ct),
                    "admin.ingestion.reindex" => await ExecAdminIngestionReindexAsync(call.Args, ct),
                    "admin.jobs.list" => await ExecAdminJobsListAsync(call.Args, ct),
                    "admin.jobs.cancel" => await ExecAdminJobsCancelAsync(call.Args, ct),
                    "diagnostic.performance" => ExecDiagnosticPerformance(call.Args),
                    "export.create" => ExecExportCreate(call.Args),
                    "support.bundle" => await ExecSupportBundleAsync(ct),
                    "rag.debug.scroll" => await ExecRagDebugScrollAsync(call.Args, ct),
                    "admin.qdrant.health" => await ExecAdminQdrantHealthAsync(ct),
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

            // Fallback: if the LLM forgot sources, derive them from the rag.search tool results.
            if (sources.Count == 0)
                sources = DeriveSourcesFromRagHits(toolResults);
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

    private static List<ToolMemory.SourceRef> DeriveSourcesFromRagHits(ToolResults toolResults)
    {
        try
        {
            // Accept both rag.search and rag.multi_search results.
            var candidates = toolResults.Items
                .Where(x => x.ToolName is "rag.search" or "rag.multi_search")
                .Select(x => x.Result)
                .ToList();

            var sources = new List<ToolMemory.SourceRef>();
            foreach (var res in candidates)
            {
                if (res.ValueKind != JsonValueKind.Object) continue;
                if (!res.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;

                foreach (var h in hits.EnumerateArray().Take(8))
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var docPath = h.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;
                    var ps = h.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = h.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;

                    var label = h.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String ? (dn.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(label)) label = Path.GetFileName(docPath);

                    sources.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath.Replace('\\', '/'),
                        PageStart = ps,
                        PageEnd = pe,
                        Label = $"{label} (p.{ps}{(pe != ps ? $"–{pe}" : "")})"
                    });
                }
            }

            // Dedup by docPath + page
            return sources
                .GroupBy(s => $"{s.DocPath}|{s.PageStart}|{s.PageEnd}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            return new List<ToolMemory.SourceRef>();
        }
    }

    private static JsonElement NormalizeRagHits(JsonElement raw)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            // Already normalized
            if (raw.TryGetProperty("hits", out var hits0) && hits0.ValueKind == JsonValueKind.Array)
                return raw;

            // Backend often returns { items: [...] }
            if (!raw.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            var list = new List<object>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? "";
                docPath = (docPath ?? "").Trim().Replace('\\', '/').TrimStart('/');

                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);
                var score = TryGetDouble(it, "score") ?? 0.0;

                var ps = TryGetInt(it, "pageStart") ?? TryGetInt(it, "page") ?? 1;
                var pe = TryGetInt(it, "pageEnd") ?? ps;

                var text = TryGetString(it, "text") ?? TryGetString(it, "excerpt") ?? "";
                if (text.Length > 320) text = text.Substring(0, 320) + "…";

                list.Add(new
                {
                    docPath,
                    docName,
                    pageStart = ps,
                    pageEnd = pe,
                    excerpt = text,
                    score
                });
            }

            var payload = new { hits = list };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return null;
    }

    // ---------------- Tools exec helpers ----------------

    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(category, q, limit, offset, ct);

        _mem.LastListCategory = category;
        _mem.LastListQuery = q;

        // Sanitize against filesystem + update PDFxx mapping (robust against moves/renames)
        DocumentListHelper.Sanitize(res, _mem);

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

        DocumentListHelper.Sanitize(res, _mem);

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        return await ExecDocumentsGetResolvedAsync(args, ct);
    }

    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetProperty("query").GetString() ?? "";
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var raw = await _api.RagSearchToolAsync(query, topK, category, mode, ct);
        return NormalizeRagHits(raw);
    }

    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        // args: { queries: string[], topK: int, category: string|null, mode: ... }
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var category = args.TryGetProperty("category", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            var s = (q1.GetString() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
        }

        if (queries.Count == 0)
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        var merged = new List<JsonElement>();
        foreach (var q in queries.Take(5))
        {
            var raw = await _api.RagSearchToolAsync(q, topK, category, mode, ct);
            var norm = NormalizeRagHits(raw);
            if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hits.EnumerateArray())
                    merged.Add(h);
            }
        }

        // Dedup by docPath + pageStart + pageEnd (diversity-friendly)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniq = new List<JsonElement>();
        foreach (var h in merged)
        {
            var dp = h.TryGetProperty("docPath", out var dpEl) ? (dpEl.GetString() ?? "") : "";
            var p1 = h.TryGetProperty("pageStart", out var p1El) && p1El.ValueKind == JsonValueKind.Number ? p1El.GetInt32() : 1;
            var p2 = h.TryGetProperty("pageEnd", out var p2El) && p2El.ValueKind == JsonValueKind.Number ? p2El.GetInt32() : p1;
            var key = $"{dp}|{p1}|{p2}";
            if (!seen.Add(key)) continue;
            uniq.Add(h);
        }

        // Sort by score desc when present
        uniq = uniq
            .OrderByDescending(h => h.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0)
            .Take(Math.Max(10, topK * 2))
            .ToList();

        var payload = new
        {
            hits = uniq,
            meta = new { queries = queries.Take(5).ToArray(), mode = (mode ?? "balanced"), category }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private JsonElement ExecSourcesResolve(JsonElement args)
    {
        if (!args.TryGetProperty("pdfRef", out var pr) || pr.ValueKind != JsonValueKind.String)
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        var input = (pr.GetString() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input))
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        // Robust: tolerate punctuation and variants like "PDF1?" or "2 ?".
        input = input.Trim().TrimEnd('?', '.', '!', ':', ';', ',', ')', ']', '}', '"', '\'', '’').Trim();

        string raw;

        // 1) Explicit PDFxx
        var mPdf = Regex.Match(input, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            raw = $"PDF{nPdf:00}";
        }
        else
        {
            // 2) Numeric index ("source du 2") or shorthand ("et le 3")
            var mNum = Regex.Match(input, @"\b(?<n>\d{1,4})\b");
            if (mNum.Success && int.TryParse(mNum.Groups["n"].Value, out var n) && n > 0)
            {
                // Prefer last listed docs page index.
                if (_mem.LastListedDocuments is { Count: > 0 } && n <= _mem.LastListedDocuments.Count)
                {
                    var d = _mem.LastListedDocuments[n - 1];
                    if (d is not null && !string.IsNullOrWhiteSpace(d.DocPath))
                    {
                        var payload0 = new
                        {
                            source = new { docPath = d.DocPath, pageStart = 1, pageEnd = 1, label = $"{d.DocName} (p.1)" }
                        };
                        return JsonDocument.Parse(JsonSerializer.Serialize(payload0)).RootElement;
                    }
                }

                // Fallback: treat as PDFxx.
                raw = $"PDF{n:00}";
            }
            else
            {
                // 3) Fallback
                raw = input.Trim().ToUpperInvariant();
            }
        }

        if (!_mem.PdfMap.TryGetValue(raw, out var doc) || doc is null || string.IsNullOrWhiteSpace(doc.DocPath))
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        // Default: after inventory => p.1.
        var pageStart = 1;
        var pageEnd = 1;

        // If we have recent sources used for this doc, prefer the most relevant page.
        var used = _mem.LastSourcesUsed?
            .FirstOrDefault(s => string.Equals((s.DocPath ?? "").Trim().Replace('\\','/'), (doc.DocPath ?? "").Trim().Replace('\\','/'), StringComparison.OrdinalIgnoreCase));
        if (used is not null)
        {
            pageStart = used.PageStart;
            pageEnd = used.PageEnd;
        }

        var src = new ToolMemory.SourceRef
        {
            DocPath = doc.DocPath,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = $"{doc.DocName} (p.{pageStart}{(pageEnd != pageStart ? $"–{pageEnd}" : "")})"
        };

        var payload = new
        {
            source = new { docPath = src.DocPath, pageStart = src.PageStart, pageEnd = src.PageEnd, label = src.Label }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

private JsonElement ExecExportCreate(JsonElement args)
    {
        var format = args.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? (f.GetString() ?? "txt") : "txt";
        var fileName = args.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "export") : "export";
        var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";

        var path = ExportService.Create(format, fileName, content);
        var payload = new { savedPath = path };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSupportBundleAsync(CancellationToken ct)
    {
        if (_settings is null)
            return JsonDocument.Parse("{\"error\":\"missing_settings\"}").RootElement;

        var zip = await SupportBundleBuilder.BuildAsync(_settings).ConfigureAwait(false);
        var payload = new { zipPath = zip };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecRagDebugScrollAsync(JsonElement args, CancellationToken ct)
    {
        var cursor = args.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 100;
        string? docPath = null;
        if (args.TryGetProperty("docRef", out var dref) && dref.ValueKind == JsonValueKind.String)
        {
            var resolved = await ResolveDocRefAsync(dref.GetString() ?? string.Empty, ct).ConfigureAwait(false);
            docPath = resolved?.DocPath;
        }
        else if (args.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String)
        {
            docPath = dp.GetString();
        }

        try
        {
            var raw = await _api.RagDebugScrollAsync(cursor, limit, docPath, ct).ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}").RootElement;
        }
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
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.tree" or "documents.stats" => "Recherche documents…",
            "rag.categories" => "Chargement catégories…",
            "rag.search" => "Recherche RAG…",
            "rag.multi_search" => "Recherche RAG…",
            "sources.resolve" => "Résolution source…",
            "summary.get" or "summary.exists" or "summary.search" => "Chargement résumé…",
            "admin.summary.missing" or "admin.summary.request" or "admin.summary.submit" or "admin.summary.status" or "admin.summary.delete" or "admin.summary.generate" => "Outils admin résumés…",
            "admin.catalog.health" or "admin.catalog.rescan_now" => "Outils admin catalogue…",
            "admin.ingestion.reindex" => "Relance ingestion…",
            "admin.jobs.list" or "admin.jobs.cancel" => "Gestion jobs admin…",
            "diagnostic.performance" => "Diagnostic…",
            "export.create" => "Export…",
            "support.bundle" => "Support…",
            "rag.debug.scroll" => "Debug…",
            _ => "Outils…"
        };
}
