using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private RouterPlan ParseAndSanitizeLlmRouterPlanJson(
        string planJson,
        string detectedMessageLanguage,
        bool disallowMetaSetLanguage)
    {
        var plan = JsonSerializer.Deserialize<RouterPlan>(planJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new RouterPlan();

        using var legacyDoc = JsonDocument.Parse(planJson);
        if (!plan.RouterConfidence.HasValue
            && legacyDoc.RootElement.TryGetProperty("confidence", out var confidenceEl)
            && confidenceEl.ValueKind == JsonValueKind.Number
            && confidenceEl.TryGetDouble(out var legacyConfidence))
        {
            plan.RouterConfidence = legacyConfidence;
        }

        var sanitized = SanitizeRouterPlan(plan, detectedMessageLanguage, disallowMetaSetLanguage);
        sanitized.Origin = RouterPlanOrigin.Llm;
        return sanitized;
    }

    private async Task<RouterPlan> TryRepairStructuredRouterSearchPlanAsync(
        RouterPlan plan,
        string effectiveUserMessage,
        CancellationToken ct)
    {
        if (plan.Origin != RouterPlanOrigin.Llm
            || plan.NeedClarification
            || !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            || !plan.ToolCalls.Any(call => IsRagToolName(NormalizeToolName(call.Name))))
        {
            return plan;
        }

        var language = NormalizeLanguageCode(plan.Language);
        var missingAxes = DetectMissingStructuredRouterSearchAxes(plan, effectiveUserMessage, language);
        if (missingAxes.Length == 0)
            return plan;

        var existingQueries = ExtractRouterPlanRagQueries(plan).Take(12).ToArray();
        EmitRagTrace(
            "router.repair.start",
            ("reason", "missing_structured_search_axes"),
            ("missing_axes", missingAxes),
            ("existing_queries", existingQueries),
            ("tools", plan.ToolCalls.Count));

        var system = BuildStructuredRouterRepairSystemPrompt(language);
        var user = BuildStructuredRouterRepairUserPrompt(plan, effectiveUserMessage, language, missingAxes, existingQueries);
        var sw = Stopwatch.StartNew();
        try
        {
            using var repairTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            repairTimeoutCts.CancelAfter(SourceBackedRouterLlmTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    repairTimeoutCts.Token)
                .ConfigureAwait(false);

            if (!TryExtractJsonObject(raw ?? string.Empty, out var json))
            {
                EmitRagTrace(
                    "router.repair.end",
                    ("accepted", false),
                    ("reason", "no_json"),
                    ("missing_axes", missingAxes),
                    ("raw_chars", raw?.Length ?? 0),
                    ("elapsed_ms", sw.ElapsedMilliseconds));
                return TryApplyStructuredRouterSearchAxisFallbackPlan(
                    plan,
                    effectiveUserMessage,
                    language,
                    missingAxes,
                    existingQueries,
                    reason: "no_json_preserve_missing_axes",
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            var repairedPlan = JsonSerializer.Deserialize<RouterPlan>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RouterPlan();
            repairedPlan = SanitizeRouterPlan(repairedPlan, language, disallowMetaSetLanguage: false);
            repairedPlan.SourceBackedMission ??= plan.SourceBackedMission;
            repairedPlan.Origin = RouterPlanOrigin.Llm;
            repairedPlan.Language = ResolveTurnLanguage(effectiveUserMessage, repairedPlan.Language, language);
            if (string.IsNullOrWhiteSpace(repairedPlan.Intent) || string.Equals(repairedPlan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
                repairedPlan.Intent = InferIntentFromToolCalls(repairedPlan.ToolCalls) ?? plan.Intent;

            var repairedMissingAxes = DetectMissingStructuredRouterSearchAxes(repairedPlan, effectiveUserMessage, repairedPlan.Language);
            var repairedQueries = ExtractRouterPlanRagQueries(repairedPlan).Take(12).ToArray();
            var regressedAxes = FindStructuredRouterSearchAxisRegressions(missingAxes, repairedMissingAxes);
            var accepted = repairedPlan.ToolCalls.Any(call => IsRagToolName(NormalizeToolName(call.Name)))
                           && repairedMissingAxes.Length < missingAxes.Length
                           && regressedAxes.Length == 0;
            EmitRagTrace(
                "router.repair.end",
                ("accepted", accepted),
                ("reason", accepted ? "coverage_improved" : regressedAxes.Length > 0 ? "coverage_regressed" : "coverage_not_improved"),
                ("missing_before", missingAxes),
                ("missing_after", repairedMissingAxes),
                ("regressed_axes", regressedAxes),
                ("queries_before", existingQueries),
                ("queries_after", repairedQueries),
                ("elapsed_ms", sw.ElapsedMilliseconds));

            if (!accepted)
            {
                return TryApplyStructuredRouterSearchAxisFallbackPlan(
                    plan,
                    effectiveUserMessage,
                    language,
                    missingAxes,
                    existingQueries,
                    reason: "coverage_not_improved_preserve_missing_axes",
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            ClientLog.Info(
                "ToolAgent router repair accepted: " +
                $"missingBefore={string.Join(",", missingAxes)}|missingAfter={string.Join(",", repairedMissingAxes)}|" +
                $"queries={string.Join(" || ", repairedQueries)}|ms={sw.ElapsedMilliseconds}");
            return repairedPlan;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "router.repair.end",
                ("accepted", false),
                ("reason", "timeout"),
                ("missing_axes", missingAxes),
                ("timeout_ms", SourceBackedRouterLlmTimeoutMs),
                ("elapsed_ms", sw.ElapsedMilliseconds));
            return TryApplyStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                existingQueries,
                reason: "timeout_preserve_missing_axes",
                elapsedMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "router.repair.end",
                ("accepted", false),
                ("reason", "error"),
                ("missing_axes", missingAxes),
                ("error", TruncateForPrompt(ex.Message, 220)),
                ("elapsed_ms", sw.ElapsedMilliseconds));
            return TryApplyStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                existingQueries,
                reason: "error_preserve_missing_axes",
                elapsedMs: sw.ElapsedMilliseconds);
        }
    }

    private RouterPlan TryApplyStructuredRouterSearchAxisFallbackPlan(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        IReadOnlyList<string> existingQueries,
        string reason,
        long elapsedMs)
    {
        if (!TryBuildStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                out var fallbackPlan,
                out var fallbackQueries,
                out var missingAfter))
        {
            return plan;
        }

        EmitRagTrace(
            "router.repair.fallback",
            ("accepted", true),
            ("reason", reason),
            ("missing_before", missingAxes),
            ("missing_after", missingAfter),
            ("queries_before", existingQueries),
            ("queries_after", fallbackQueries.Take(12).ToArray()),
            ("elapsed_ms", elapsedMs));

        ClientLog.Info(
            "ToolAgent router repair fallback accepted: " +
            $"reason={reason}|missingBefore={string.Join(",", missingAxes)}|missingAfter={string.Join(",", missingAfter)}|" +
            $"queries={string.Join(" || ", fallbackQueries.Take(12))}|ms={elapsedMs}");

        return fallbackPlan;
    }

    private static string[] DetectMissingStructuredRouterSearchAxes(
        RouterPlan plan,
        string effectiveUserMessage,
        string language)
        => DetectMissingStructuredRouterSearchAxesForQueries(
            ExtractRouterPlanRagQueries(plan),
            effectiveUserMessage,
            language);

    private static string[] DetectMissingStructuredRouterSearchAxesForQueries(
        IReadOnlyList<string> queries,
        string effectiveUserMessage,
        string language)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
            return Array.Empty<string>();

        language = NormalizeLanguageCode(language);
        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var explicitRequestedAxes = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        if (dayAxis.Count < 2 || explicitRequestedAxes.Count == 0)
            return Array.Empty<string>();

        var normalizedQueries = queries
            .Select(NormalizeLexicalLookup)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedQueries.Length == 0)
            return explicitRequestedAxes.ToArray();

        var requestedAxisTerms = BuildStructuredPlanningSlotTermGroups(explicitRequestedAxes, effectiveUserMessage)
            .Select(group => new
            {
                Label = group.Label,
                Terms = group.PrimaryTerms
                    .Concat(group.AlternativeTerms)
                    .Where(static term => !string.IsNullOrWhiteSpace(term))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            })
            .Where(static axis => axis.Terms.Length > 0)
            .ToArray();

        var allRequestedAxisTerms = requestedAxisTerms
            .Select(static axis => axis.Terms)
            .ToArray();

        var missing = new List<string>();
        foreach (var axis in requestedAxisTerms)
        {
            if (!normalizedQueries.Any(query => QueryCoversFocusedStructuredRouterSearchAxis(query, axis.Terms, allRequestedAxisTerms)))
                missing.Add(axis.Label);
        }

        return missing
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] FindStructuredRouterSearchAxisRegressions(
        IReadOnlyList<string> missingBefore,
        IReadOnlyList<string> missingAfter)
    {
        if (missingAfter.Count == 0)
            return Array.Empty<string>();

        var missingBeforeSet = new HashSet<string>(missingBefore, StringComparer.OrdinalIgnoreCase);
        return missingAfter
            .Where(axis => !missingBeforeSet.Contains(axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool QueryCoversFocusedStructuredRouterSearchAxis(
        string normalizedQuery,
        IReadOnlyList<string> axisTerms,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)
            || axisTerms.Count == 0
            || !axisTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
        {
            return false;
        }

        var mentionedAxes = CountStructuredRouterSearchAxesMentioned(normalizedQuery, requestedAxisTerms);
        if (requestedAxisTerms.Count >= 3 && mentionedAxes >= Math.Min(requestedAxisTerms.Count, 3))
            return false;

        return true;
    }

    private static int CountStructuredRouterSearchAxesMentioned(
        string normalizedQuery,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        var count = 0;
        foreach (var terms in requestedAxisTerms)
        {
            if (terms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
                count++;
        }

        return count;
    }

    private static string[] ExtractRouterPlanRagQueries(RouterPlan plan)
    {
        var queries = new List<string>();
        foreach (var call in plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
        {
            var normalizedName = NormalizeToolName(call.Name);
            if (string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctRagQuery(queries, TryGetStringArg(call.Args, "query"));
            }
            else if (string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var query in NormalizeRagMultiSearchQueries(call.Args))
                    AddDistinctRagQuery(queries, query);
            }
        }

        return queries.ToArray();
    }

    private static bool TryBuildStructuredRouterSearchAxisFallbackPlan(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        out RouterPlan fallbackPlan,
        out string[] fallbackQueries,
        out string[] missingAfter)
    {
        fallbackPlan = plan;
        fallbackQueries = ExtractRouterPlanRagQueries(plan);
        missingAfter = missingAxes.ToArray();

        if (missingAxes.Count == 0)
            return false;

        var toolCalls = new List<RouterPlan.ToolCall>();
        var updated = false;
        foreach (var call in plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
        {
            var normalizedName = NormalizeToolName(call.Name);
            if (!updated && string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                var originalQueries = NormalizeRagMultiSearchQueries(call.Args);
                var enriched = BuildStructuredRouterAxisFallbackQueries(
                    originalQueries,
                    missingAxes,
                    effectiveUserMessage,
                    language);
                if (enriched.Length > originalQueries.Length)
                {
                    toolCalls.Add(new RouterPlan.ToolCall
                    {
                        Name = "rag.multi_search",
                        Args = BuildStructuredRouterAxisFallbackArgs(call.Args, enriched)
                    });
                    updated = true;
                    continue;
                }
            }

            toolCalls.Add(new RouterPlan.ToolCall
            {
                Name = call.Name,
                Args = call.Args.ValueKind == JsonValueKind.Undefined ? default : call.Args.Clone()
            });
        }

        if (!updated)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var normalizedName = NormalizeToolName(toolCalls[i].Name);
                if (!string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase))
                    continue;

                var enriched = BuildStructuredRouterAxisFallbackQueries(
                    new[] { TryGetStringArg(toolCalls[i].Args, "query") ?? string.Empty },
                    missingAxes,
                    effectiveUserMessage,
                    language);
                if (enriched.Length == 0)
                    continue;

                toolCalls[i] = new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = BuildStructuredRouterAxisFallbackArgs(toolCalls[i].Args, enriched, convertSearchToMultiSearch: true)
                };
                updated = true;
                break;
            }
        }

        if (!updated)
            return false;

        fallbackPlan = new RouterPlan
        {
            Mode = plan.Mode,
            Language = NormalizeLanguageCode(string.IsNullOrWhiteSpace(plan.Language) ? language : plan.Language),
            Intent = string.IsNullOrWhiteSpace(plan.Intent) || string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
                ? "rag.answer"
                : plan.Intent,
            ResponseFormat = plan.ResponseFormat,
            Origin = plan.Origin,
            NeedClarification = false,
            ClarificationQuestions = new List<string>(),
            ReasoningTracePublic = plan.ReasoningTracePublic?.ToList() ?? new List<string>(),
            RiskFlags = plan.RiskFlags?.ToList() ?? new List<string>(),
            MemoryUpdate = plan.MemoryUpdate,
            RouterConfidence = plan.RouterConfidence,
            ToolCalls = toolCalls,
            SourceBackedMission = plan.SourceBackedMission
        };

        fallbackQueries = ExtractRouterPlanRagQueries(fallbackPlan);
        missingAfter = DetectMissingStructuredRouterSearchAxes(fallbackPlan, effectiveUserMessage, fallbackPlan.Language);
        return missingAfter.Length < missingAxes.Count;
    }

    private static string[] BuildStructuredRouterAxisFallbackQueries(
        IEnumerable<string> existingQueries,
        IReadOnlyList<string> missingAxes,
        string? effectiveUserMessage,
        string language)
    {
        var originalQueries = new List<string>();
        foreach (var query in existingQueries)
            AddDistinctRagQuery(originalQueries, query);

        var genericInventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, effectiveUserMessage)
            .Take(1)
            .ToArray();

        var axisVariants = missingAxes
            .Select(axis => BuildStructuredRouterAxisFallbackVariants(axis, effectiveUserMessage))
            .Where(static variants => variants.Length > 0)
            .ToArray();

        var queries = new List<string>();
        var prioritizedOriginalQueries = originalQueries.ToArray();

        var firstExistingCount = 0;
        foreach (var query in prioritizedOriginalQueries.Take(firstExistingCount))
            AddDistinctRagQuery(queries, query);

        foreach (var variants in axisVariants)
        {
            foreach (var inventory in genericInventoryTerms.Take(1))
                AddRouterAxisFallbackQueryIfMissing(queries, $"{variants[0]} {inventory}", axisVariants);
            AddRouterAxisFallbackQueryIfMissing(queries, variants[0], axisVariants);
        }

        foreach (var query in prioritizedOriginalQueries.Skip(firstExistingCount))
        {
            if (queries.Count >= 8)
                break;
            AddDistinctRagQuery(queries, query);
        }

        for (var variantIndex = 1; queries.Count < 8 && variantIndex < 4; variantIndex++)
        {
            foreach (var variants in axisVariants)
            {
                if (queries.Count >= 8)
                    break;
                if (variantIndex < variants.Length)
                    AddRouterAxisFallbackQueryIfMissing(queries, variants[variantIndex], axisVariants);
            }
        }

        return queries.ToArray();
    }

    private static string[] BuildStructuredRouterAxisFallbackVariants(
        string axis,
        string? effectiveUserMessage)
    {
        var group = BuildStructuredPlanningSlotTermGroups(new[] { axis }, effectiveUserMessage)
            .FirstOrDefault();
        var terms = group is null
            ? ExpandPlanningSlotRetrievalTermVariants(axis).Select(NormalizeLexicalLookup)
            : group.PrimaryTerms.Concat(group.AlternativeTerms);

        return terms
            .Select(CollapseWhitespace)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRouterAxisFallbackQueryIfMissing(
        List<string> queries,
        string query,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return;

        var normalizedQueries = queries
            .Select(NormalizeLexicalLookup)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (normalizedQueries.Any(existing => QueryCoversFocusedStructuredRouterSearchAxis(
                existing,
                new[] { normalizedQuery },
                requestedAxisTerms)))
        {
            return;
        }

        AddDistinctRagQuery(queries, query);
    }

    private static JsonElement BuildStructuredRouterAxisFallbackArgs(
        JsonElement originalArgs,
        IReadOnlyList<string> queries,
        bool convertSearchToMultiSearch = false)
    {
        var args = ParseRouterArgsObject(originalArgs);
        if (convertSearchToMultiSearch)
            args.Remove("query");

        var queryArray = new JsonArray();
        foreach (var query in queries)
            queryArray.Add(query);
        args["queries"] = queryArray;

        if (!args.ContainsKey("topK"))
            args["topK"] = 8;
        if (!args.ContainsKey("mode"))
            args["mode"] = "broad";
        if (!args.ContainsKey("researchMode"))
            args["researchMode"] = "source_exploration";
        if (!args.ContainsKey("includeResearchSurfaces"))
            args["includeResearchSurfaces"] = true;

        return JsonSerializer.SerializeToElement(args).Clone();
    }

    private static JsonObject ParseRouterArgsObject(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        try
        {
            return JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string BuildStructuredRouterRepairSystemPrompt(string language)
        => $@"
You are SAAIA Router Repair. Output ONLY valid JSON.
Target language: {NormalizeLanguageCode(language)}.

The previous router already chose a source-backed route, but its tool queries omitted explicit requested slots/types.
Correct only the route/toolCalls. Do not answer the user.

Rules:
- Keep the LLM as orchestrator: decide the corrected retrieval strategy yourself from the request, request shape, category hints and existing tool calls.
- Use rag.multi_search for broad source-backed structured plans.
- Preserve all explicit requested slots/types/criteria/phases in the query strategy before execution.
- If a category hint clearly fits semantically, use its exact category path; otherwise keep category/categoryPath null.
- Choose the useful query set yourself. No query count is imposed. Avoid one query per day/row/column unless the source genuinely distinguishes them.
- Avoid decorative variants around one broad noun. Queries need a requested slot/type, a concrete source label, a constraint or a candidate name.

Return the normal RouterPlan JSON:
{{""mode"":""auto|standard|strict"",""language"":""fr|en|es|pt|de|it"",""intent"":""rag.answer|rag.compare|rag.followup"",""responseFormat"":""auto"",""needClarification"":false,""clarificationQuestions"":[],""reasoningTracePublic"":[],""riskFlags"":[],""memoryUpdate"":null,""routerConfidence"":0.0,""toolCalls"":[{{""name"":""rag.multi_search"",""args"":{{""queries"":[""short query""],""topK"":8,""category"":null,""mode"":""broad"",""researchMode"":""source_exploration"",""includeResearchSurfaces"":true}}}}]}}";

    private string BuildStructuredRouterRepairUserPrompt(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        IReadOnlyList<string> existingQueries)
        => $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

CATEGORY_HINTS:
{BuildSourceBackedLlmCategoryHintsForPrompt(effectiveUserMessage, maxCategories: 16)}

MISSING_REQUESTED_SLOTS_OR_TYPES:
{FormatPromptList(missingAxes, 12, maxItemLength: 80)}

EXISTING_ROUTER_QUERIES:
{FormatPromptList(existingQueries, 12, maxItemLength: 80)}

EXISTING_TOOL_CALLS:
{FormatRouterToolCallsForPrompt(plan)}

TASK:
Return a corrected RouterPlan JSON whose rag.multi_search queries cover the missing requested slots/types and remain complementary to the existing plan.
Do not add final-answer text.";

    private static string FormatRouterToolCallsForPrompt(RouterPlan plan)
    {
        var lines = (plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
            .Select(call => $"- {NormalizeToolName(call.Name)}: {TruncateForPrompt(call.Args.GetRawText(), 500)}")
            .ToArray();
        return lines.Length == 0 ? "- none" : string.Join(Environment.NewLine, lines);
    }

    private static bool ShouldUseCompactSourceBackedRouterPrompt(string? userMessage)
        => LooksLikeSourceBackedBroadResearchRequest(userMessage)
           || LooksLikeAnyDocumentaryPlanningRequest(userMessage)
           || ShouldUseResearchSurfacesForBroadRagRequest(userMessage);

}
