using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Models;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildOverflowRetryWriterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults overflowRetryToolResults,
        string followupContextNote,
        int evidenceInventoryChars,
        string? repairFeedback = null)
    {
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        evidenceInventoryChars = Math.Clamp(evidenceInventoryChars, 2400, 9000);
        return $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 4)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{followupContextNote}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

STRUCTURED_PLANNING_WRITER_CONTRACT:
{BuildStructuredPlanningWriterContractForWriter(writerUserMessage, plan.Language)}

PRIVATE_REPAIR_FEEDBACK:
{(string.IsNullOrWhiteSpace(repairFeedback) ? "none" : TruncateForPrompt(repairFeedback, 1400))}

COMPACT_RETRY_INSTRUCTIONS:
- The first writer prompt was too large. Use this smaller evidence packet only.
- Do not paste raw passages. Rewrite naturally in the target language with correct spelling, accents and punctuation.
- If the request asks for a plan, recommendation, list, procedure or synthesis, organize the sourced items into the requested shape when possible.
- If PRIVATE_SOURCE_EVIDENCE_INVENTORY contains EVIDENCE_ITEM rows, every concrete structured-plan cell/item you fill must end with exactly one valid [E#] id from those rows.
- Do not fill a structured-plan cell with only [E#], a source/page citation, or a file name. Write the concrete item/action/value first, then [E#].
- If evidence is partial, still give a useful partial answer first, then state the limit briefly. Do not ask to broaden the search again unless no usable item exists.
- Do not add a final Source/Sources section; the application appends clickable source cards.

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{BuildSourceBackedCandidateLeadsForWriter(
    overflowRetryToolResults,
    writerUserMessage,
    plan.Language,
    maxItems: ResolveEvidenceInventoryItemLimitForPromptChars(evidenceInventoryChars, writerUserMessage),
    maxChars: evidenceInventoryChars)}
";
    }

    private static int ResolveOverflowRetryEvidenceInventoryPromptChars(
        WriterPromptBudget budget,
        bool structuredPlanningWriter)
    {
        if (!structuredPlanningWriter)
            return Math.Min(4200, Math.Max(2800, budget.EvidenceInventoryChars));

        if (budget.ContextTokens <= 4096)
            return 3600;

        return budget.ContextTokens <= 8192 ? 6400 : 9000;
    }

    private static int ResolveRepairEvidenceInventoryPromptChars(
        WriterPromptBudget budget,
        bool structuredPlanningWriter)
    {
        if (!structuredPlanningWriter)
            return Math.Min(4200, Math.Max(2600, budget.EvidenceInventoryChars));

        if (budget.ContextTokens <= 4096)
            return 3600;

        return budget.ContextTokens <= 8192 ? 10000 : 12000;
    }

    private static string BuildCompactSourceBackedWriterSystemPrompt(
        string language,
        string mode,
        string style,
        bool repairMode = false,
        bool compactRetry = false)
    {
        var normalizedLanguage = NormalizeLanguageCode(language);
        var normalizedMode = NormalizePlanMode(mode);
        var normalizedStyle = string.IsNullOrWhiteSpace(style) ? "auto" : style.Trim();
        var task = repairMode
            ? "Repair or rewrite the draft from the provided source evidence."
            : "Write the final answer from the provided source evidence.";
        var retry = compactRetry
            ? " This is a compact retry after a context overflow; use the compact evidence packet and do not request another search unless it contains no usable source item."
            : string.Empty;

        return $@"
You are SAAIA assistant.
Target answer language: {normalizedLanguage}.
Mode: {normalizedMode}. Style: {normalizedStyle}.

Task: {task}{retry}

Rules:
- Use only the provided source evidence for concrete facts, items, actions, quantities, dates and citations.
- You are the semantic judge: choose which visible EVIDENCE_ITEM rows actually support the user's request. Treat diagnostics as advice, not a veto.
- Do not invent missing concrete content. If evidence is partial, provide the useful sourced part first and mark missing parts plainly.
- Write polished user-facing prose in the target language. Clean OCR damage when the meaning stays the same.
- Do not expose private labels such as EVIDENCE_ITEM, candidate, evidenceRole, coverage, writerEvidence, tool result or diagnostics.
- Do not paste raw excerpts or group the final answer by source/page. Synthesize the evidence into the user's requested shape.
- For source-backed plans, lists, recommendations, procedures or comparisons, every concrete item/action/value must carry one source marker.
- When an item comes from an EVIDENCE_ITEM, append that item's id exactly like [E1]. The application converts valid ids into visible file/page citations.
- A citation id is proof, not content. Never fill a cell or bullet with only [E#], a file name, or a page citation; write the concrete item text first.
- For structured plans, mirror the user's visible axes and fill only supported cells. Use one useful [E#] per filled cell; use ""a completer avec une source utile"" for unsupported cells.
- Prefer distinct useful source pages before repeating one page, but never invent a source to increase variety.
- Do not add a final Sources section. Clickable source cards are appended by the application.
- Return only the final answer.
";
    }

    private static ToolResults BuildOverflowRetryWriterToolResults(ToolResults writerToolResults, string userMessage)
    {
        var retry = new ToolResults();
        var ragItems = writerToolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        if (ragItems.Count == 0)
            return retry;

        retry.Items.Add(new ToolResults.Item
        {
            ToolName = ragItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
            Error = null,
            DurationMs = ragItems.Sum(static item => Math.Max(0, item.DurationMs)),
            Result = CompactMergedRagResultsForWriter(
                ragItems.Select(static item => item.Result).ToList(),
                userMessage,
                precise: false,
                maxHitsOverride: LooksLikeAnyDocumentaryPlanningRequest(userMessage) ? 6 : 5)
        });

        return retry;
    }

    private static bool HasMergedOrMultipleRagEvidence(ToolResults toolResults)
    {
        var ragItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        if (ragItems.Count > 1)
            return true;

        foreach (var item in ragItems)
        {
            var meta = TryGetObject(item.Result, "meta") ?? TryGetObject(item.Result, "Meta");
            if (meta.HasValue && TryGetBool(meta.Value, "merged") is true)
                return true;
        }

        return false;
    }

    private readonly record struct WriterPromptBudget(
        int ContextTokens,
        int ToolResultsChars,
        int CoverageNoteChars,
        int WritingBriefChars,
        int ResearchMapChars,
        int EvidenceInventoryChars);

    private readonly record struct SourceBackedCandidateAdjudicationPromptBudget(
        int ToolResultsChars,
        int EvidenceInventoryChars,
        int CoverageTraceLines,
        int MaxEvidenceItems,
        int PromptTargetChars);

    private static SourceBackedCandidateAdjudicationPromptBudget CreateSourceBackedCandidateAdjudicationPromptBudget(
        WriterPromptBudget writerPromptBudget,
        bool compactRetry = false)
    {
        var contextTokens = Math.Max(WriterPromptMinimumContextTokens, writerPromptBudget.ContextTokens);
        if (compactRetry)
        {
            return new SourceBackedCandidateAdjudicationPromptBudget(
                ToolResultsChars: SourceBackedCandidateAdjudicationMinimumToolResultsChars,
                EvidenceInventoryChars: SourceBackedCandidateAdjudicationMinimumEvidenceInventoryChars,
                CoverageTraceLines: 3,
                MaxEvidenceItems: 6,
                PromptTargetChars: contextTokens <= 4096 ? 6500 : 9000);
        }

        if (contextTokens <= 4096)
        {
            return new SourceBackedCandidateAdjudicationPromptBudget(
                ToolResultsChars: 1600,
                EvidenceInventoryChars: 2600,
                CoverageTraceLines: 5,
                MaxEvidenceItems: 8,
                PromptTargetChars: 8200);
        }

        if (contextTokens <= 8192)
        {
            return new SourceBackedCandidateAdjudicationPromptBudget(
                ToolResultsChars: Math.Min(4200, Math.Max(2400, writerPromptBudget.ToolResultsChars / 4)),
                EvidenceInventoryChars: Math.Min(7600, Math.Max(4200, writerPromptBudget.EvidenceInventoryChars * 3)),
                CoverageTraceLines: 8,
                MaxEvidenceItems: 20,
                PromptTargetChars: 18000);
        }

        return new SourceBackedCandidateAdjudicationPromptBudget(
            ToolResultsChars: Math.Min(SourceBackedCandidateAdjudicationToolResultsChars, Math.Max(5200, writerPromptBudget.ToolResultsChars / 3)),
            EvidenceInventoryChars: Math.Min(7600, Math.Max(4400, writerPromptBudget.EvidenceInventoryChars * 2)),
            CoverageTraceLines: Math.Min(MaxSourceBackedLlmEvidencePlannerCoverageTraceLines, 12),
            MaxEvidenceItems: 20,
            PromptTargetChars: 22000);
    }

    private static int ResolveWriterEvidenceInventoryPromptChars(
        WriterPromptBudget budget,
        bool structuredPlanningWriter)
    {
        if (!structuredPlanningWriter)
            return budget.EvidenceInventoryChars;

        if (budget.ContextTokens <= 4096)
        {
            var compactCeiling = Math.Max(budget.EvidenceInventoryChars, budget.ToolResultsChars / 3);
            return Math.Min(Math.Max(budget.EvidenceInventoryChars, 3600), compactCeiling);
        }

        var structuredFloor = budget.ContextTokens <= 8192 ? 10500 : 13000;
        var contextAwareCeiling = Math.Max(structuredFloor, (budget.ToolResultsChars * 4) / 5);
        return Math.Min(Math.Max(budget.EvidenceInventoryChars, structuredFloor), contextAwareCeiling);
    }

    private ToolResults BuildWriterToolResultsForRuntime(
        RouterPlan plan,
        ToolResults toolResults,
        string userMessage,
        WriterPromptBudget promptBudget)
    {
        var compacted = BuildWriterToolResults(plan, toolResults, userMessage);
        return ApplyWriterToolResultsBudget(compacted, userMessage, promptBudget.ToolResultsChars);
    }

    private WriterPromptBudget ResolveWriterPromptBudget()
    {
        var contextTokens = ResolveActiveLlmContextTokens(_settings);
        var configuredMaxOutputTokens = _settings?.LlmMaxOutputTokens ?? 900;
        var contextAwareMaxOutputTokens = Math.Clamp(contextTokens / 2, 900, 4096);
        var maxOutputTokens = Math.Clamp(configuredMaxOutputTokens, 384, contextAwareMaxOutputTokens);
        return CreateWriterPromptBudget(contextTokens, maxOutputTokens);
    }

    private static bool ShouldUseCleanSourceBackedWriterPrompt(RouterPlan plan, ToolResults writerToolResults, string userMessage)
    {
        if (!writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return false;

        return ShouldAvoidRawSourceBackedFallback(userMessage)
            || ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage)
            || LooksLikeBroadSynthesisRequestShape(userMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            || LooksLikeGenericCollectionOrListRequest(userMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(userMessage)
            || string.Equals(plan.Intent, "rag.multi_search", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWriterToolResultsPromptBlock(
        ToolResults writerToolResults,
        string userMessage,
        string language,
        bool useCleanSourceBrief,
        int? sourceReferenceIndexChars = null,
        ToolResults? sourceReferenceToolResults = null)
    {
        if (!useCleanSourceBrief)
            return $"TOOL_RESULTS (json):{Environment.NewLine}{SerializeToolResults(writerToolResults)}";

        var sourceReferenceIndex = BuildSourceBackedCandidateLeadsForWriter(
            sourceReferenceToolResults ?? writerToolResults,
            userMessage,
            language,
            maxItems: ResolveEvidenceInventoryItemLimitForPromptChars(sourceReferenceIndexChars, userMessage),
            maxChars: sourceReferenceIndexChars);
        if (sourceReferenceIndexChars is > 0)
            sourceReferenceIndex = TruncateForPrompt(sourceReferenceIndex, sourceReferenceIndexChars.Value);
        if (string.IsNullOrWhiteSpace(sourceReferenceIndex))
            sourceReferenceIndex = "none";

        var omitted = JsonSerializer.Serialize(new
        {
            omittedFromWriterPrompt = true,
            reason = "broad_source_backed_synthesis_uses_private_brief",
            availablePrivateSections = new[]
            {
                "PRIVATE_SOURCE_COVERAGE_NOTE",
                "PRIVATE_SOURCE_WRITING_BRIEF",
                "PRIVATE_SOURCE_RESEARCH_MAP",
                "PRIVATE_SOURCE_CANDIDATE_ADJUDICATION",
                "PRIVATE_SOURCE_EVIDENCE_INVENTORY",
                "PRIVATE_SOURCE_REFERENCE_INDEX"
            }
        });

        return $"""
TOOL_RESULTS (omitted):
{omitted}

PRIVATE_SOURCE_REFERENCE_INDEX:
{sourceReferenceIndex}
""";
    }

    private static WriterPromptBudget CreateWriterPromptBudget(int contextTokens, int maxOutputTokens)
    {
        var safeContextTokens = Math.Max(WriterPromptMinimumContextTokens, contextTokens);
        var reservedSystemAndTailTokens = safeContextTokens <= 4096
            ? 1200
            : safeContextTokens <= 8192
                ? 1900
                : 2600;
        var availableToolTokens = Math.Max(
            WriterPromptMinimumToolResultsChars / WriterPromptCharsPerTokenEstimate,
            safeContextTokens - maxOutputTokens - reservedSystemAndTailTokens);
        var toolResultsChars = Math.Clamp(
            availableToolTokens * WriterPromptCharsPerTokenEstimate,
            WriterPromptMinimumToolResultsChars,
            WriterPromptMaximumToolResultsChars);
        var sectionScale = safeContextTokens <= 4096
            ? 0.62
            : safeContextTokens <= 8192
                ? 1.0
                : 1.35;

        return new WriterPromptBudget(
            safeContextTokens,
            toolResultsChars,
            CoverageNoteChars: (int)Math.Round(1100 * sectionScale),
            WritingBriefChars: (int)Math.Round(1500 * sectionScale),
            ResearchMapChars: (int)Math.Round(1800 * sectionScale),
            EvidenceInventoryChars: (int)Math.Round(2200 * sectionScale));
    }

    private static int ResolveActiveLlmContextTokens(AppSettings? settings)
    {
        if (settings?.QualifiedProfile is { } profile)
        {
            var perSlotContext = profile.ResolvePerSlotContextSize();
            if (perSlotContext >= WriterPromptMinimumContextTokens)
                return perSlotContext;
        }

        if (TryExtractCtxSizeFromArgs(settings?.ExtraArgs) is int argsCtx && argsCtx >= WriterPromptMinimumContextTokens)
        {
            var parallel = TryExtractParallelFromArgs(settings?.ExtraArgs) ?? 1;
            return Math.Max(
                WriterPromptMinimumContextTokens,
                argsCtx / Math.Clamp(parallel, 1, 16));
        }

        return AppSettings.DefaultCtxSize;
    }

    private static int? TryExtractCtxSizeFromArgs(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
            return null;

        var match = Regex.Match(
            extraArgs,
            @"(?:^|\s)(?:--ctx-size|-c)(?:=|\s+)(?<value>\d{3,6})(?=\s|$)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : null;
    }

    private static int? TryExtractParallelFromArgs(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
            return null;

        var match = Regex.Match(
            extraArgs,
            @"(?:^|\s)(?:--parallel|-np)(?:=|\s+)(?<value>\d{1,2})(?=\s|$)",
            RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : null;
    }

    private static ToolResults ApplyWriterToolResultsBudget(ToolResults toolResults, string userMessage, int maxSerializedChars)
    {
        if (maxSerializedChars <= 0 || SerializeToolResults(toolResults).Length <= maxSerializedChars)
            return toolResults;

        var precise = !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
                      || LooksLikeShortTechnicalEvidenceTopic(userMessage);
        var ragItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var summaryItems = toolResults.Items
            .Where(static item => item.ToolName == "summary.search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var otherItems = toolResults.Items
            .Where(static item => item.ToolName is not ("rag.search" or "rag.multi_search" or "summary.search"))
            .ToList();

        var hitLimits = ResolveBudgetedWriterHitLimits(userMessage, precise);
        var summaryLimits = new[] { 8, 4, 2, 0 };
        ToolResults? best = null;

        foreach (var hitLimit in hitLimits)
        {
            foreach (var summaryLimit in summaryLimits)
            {
                var candidate = new ToolResults();
                if (ragItems.Count > 0 && hitLimit > 0)
                {
                    candidate.Items.Add(new ToolResults.Item
                    {
                        ToolName = ragItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
                        Error = null,
                        DurationMs = ragItems.Sum(static item => Math.Max(0, item.DurationMs)),
                        Result = CompactMergedRagResultsForWriter(
                            ragItems.Select(static item => item.Result).ToList(),
                            userMessage,
                            precise,
                            maxHitsOverride: hitLimit)
                    });
                }

                foreach (var summary in summaryItems.Take(summaryLimit))
                {
                    candidate.Items.Add(new ToolResults.Item
                    {
                        ToolName = summary.ToolName,
                        Error = summary.Error,
                        DurationMs = summary.DurationMs,
                        Result = CompactSummarySearchResultForWriter(summary.Result, maxItems: Math.Max(1, summaryLimit), summaryTextChars: 420)
                    });
                }

                foreach (var item in otherItems)
                {
                    TryAddWriterBudgetedItem(candidate, item, Math.Max(1200, maxSerializedChars / 4), maxSerializedChars);
                }

                var serializedLength = SerializeToolResults(candidate).Length;
                if (best is null || serializedLength < SerializeToolResults(best).Length)
                    best = candidate;
                if (serializedLength <= maxSerializedChars)
                    return candidate;
            }
        }

        return best ?? new ToolResults();
    }

    private static IEnumerable<int> ResolveBudgetedWriterHitLimits(string userMessage, bool precise)
    {
        if (precise)
            return new[] { 4, 3, 2, 1 };

        if (LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            || LooksLikeBroadSynthesisRequestShape(userMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage))
        {
            if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
            {
                var targetSlots = ResolveSourceBackedPlanningTargetItemCount(userMessage);
                if (targetSlots >= 10)
                    return new[] { 16, 14, 12, 10, 8, 6, 4, 3, 2 };
            }

            return new[] { 8, 6, 4, 3, 2 };
        }

        return new[] { 6, 4, 3, 2 };
    }

    private static void TryAddWriterBudgetedItem(
        ToolResults candidate,
        ToolResults.Item item,
        int maxItemChars,
        int maxSerializedChars)
    {
        if (!string.IsNullOrWhiteSpace(item.Error))
        {
            candidate.Items.Add(new ToolResults.Item
            {
                ToolName = item.ToolName,
                Error = item.Error,
                DurationMs = item.DurationMs,
                Result = JsonDocument.Parse("""{"error":"tool_failed"}""").RootElement.Clone()
            });
            if (SerializeToolResults(candidate).Length > maxSerializedChars)
                candidate.Items.RemoveAt(candidate.Items.Count - 1);
            return;
        }

        var raw = item.Result.ValueKind == JsonValueKind.Undefined ? string.Empty : item.Result.GetRawText();
        if (raw.Length > maxItemChars)
        {
            var omitted = new ToolResults.Item
            {
                ToolName = item.ToolName,
                Error = item.Error,
                DurationMs = item.DurationMs,
                Result = BuildWriterOmittedToolPayload(item.ToolName, raw.Length)
            };
            candidate.Items.Add(omitted);
            if (SerializeToolResults(candidate).Length > maxSerializedChars)
                candidate.Items.RemoveAt(candidate.Items.Count - 1);
            return;
        }

        candidate.Items.Add(item);
        if (SerializeToolResults(candidate).Length > maxSerializedChars)
            candidate.Items.RemoveAt(candidate.Items.Count - 1);
    }

    private static JsonElement BuildWriterOmittedToolPayload(string toolName, int originalChars)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            omittedFromWriterPrompt = true,
            reason = "writer_context_budget",
            originalTool = toolName,
            originalChars,
            guidance = "This payload was available to the research/navigation phase but was not included in the final writer prompt. Do not treat it as factual evidence; rely on concrete RAG/source hits for final claims."
        })).RootElement.Clone();

    private static ToolResults BuildWriterToolResults(RouterPlan plan, ToolResults toolResults, string userMessage)
    {
        var inventoryRendered = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (inventoryRendered is null)
            return CompactRagToolResultsForWriter(toolResults, userMessage);

        var inventoryIntent = IsInventoryIntent(plan.Intent);
        var inventoryOnly = HasOnlyInventoryTools(toolResults);

        if (!inventoryIntent && !inventoryOnly)
            return CompactRagToolResultsForWriter(toolResults, userMessage);

        var filtered = new ToolResults();
        filtered.Items.Add(new ToolResults.Item
        {
            ToolName = inventoryRendered.ToolName,
            Result = inventoryRendered.Result,
            Error = inventoryRendered.Error,
            DurationMs = inventoryRendered.DurationMs
        });

        var diagnostic = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (diagnostic is not null)
        {
            filtered.Items.Add(new ToolResults.Item
            {
                ToolName = diagnostic.ToolName,
                Result = diagnostic.Result,
                Error = diagnostic.Error,
                DurationMs = diagnostic.DurationMs
            });
        }

        return CompactRagToolResultsForWriter(filtered, userMessage);
    }
}
