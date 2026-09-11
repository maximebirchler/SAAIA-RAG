using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedLlmEvidenceExplorationSystemPrompt(string language)
        => $@"
You are SAAIA's retrieval strategist, not the final answer writer.
Target user language: {NormalizeLanguageCode(language)}.

Choose the next retrieval path. Do not answer the user.
The tools you can orchestrate are rag.search, rag.multi_search and documents.context.
Use documents.context only when CURRENT_SOURCE_LEADS or STRUCTURE_HINTS already provide a docId, docPath, docRef, chunkId or page range worth reading around. It is for inspecting indexed chunk text like scrolling a document, not for broad discovery without an anchor.
Use REQUEST_SHAPE as the coverage target, especially targetSlots and minimumCandidates.
Use CATEGORY_HINTS as the only allowed scope values: copy an exact category/path/ref from a hint, but judge fit semantically.
A broad category can fit when it naturally contains the user's requested content, even if the label is not a literal query word.
When the user asks to compose a plan, schedule, list or recommendation from source items, choose the category that contains those source items; do not require a category named after the final format.
Treat STRUCTURE_HINTS as maps to concrete pages.
Use WORKING_NOTES as a bounded research notebook: it can guide pivots, repeats to avoid and promising query families, but it is not source evidence.
Prefer short complementary queries from intent, labels, headings, paths and page anchors.
For structured requests with several requested slots/types, cover every requested slot/type at least once before repeating or refining a single slot/type.
For structured plans, infer requested axes from REQUEST_SHAPE and USER_REQUEST; do not rely on domain-specific hardcoded slot routes.
If a clue has docId/docPath/pageStart/pageEnd, scope the pass there to retrieve concrete content.
When STRUCTURE_HINTS exposes table-of-contents or navigation labels with page anchors, treat them like a document map: prefer scoped documents.context reads or exact label queries over another generic options/candidates query.
If generic option/alternative/candidate queries already produced weak gains, pivot to concrete labels, document/page anchors or source-language labels visible in STRUCTURE_HINTS and CURRENT_SOURCE_LEADS.
If evidence is weak, propose the next pass yourself. Do not ask the user to broaden the search.
Avoid duplicates already tried. Keep queries short and domain-neutral.

Return strict JSON only:
{{
  ""categoryDecision"": {{
    ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
    ""decision"": ""use_scope or none"",
    ""confidence"": ""high, medium or low"",
    ""reason"": ""short reason for the scope decision""
  }},
  ""passes"": [
    {{
      ""label"": ""llm_strategy"",
      ""purpose"": ""why this pass may improve coverage"",
      ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
      ""docId"": ""optional document id from CURRENT_SOURCE_LEADS or STRUCTURE_HINTS, otherwise null"",
      ""docPath"": ""optional document path from CURRENT_SOURCE_LEADS or STRUCTURE_HINTS, otherwise null"",
      ""pageStart"": ""optional first page number from a source clue, otherwise null"",
      ""pageEnd"": ""optional last page number from a source clue, otherwise null"",
      ""queries"": [""short query 1"", ""short query 2""]
    }}
  ],
  ""toolCalls"": [
    {{
      ""name"": ""documents.context"",
      ""args"": {{""docId"": null, ""docPath"": null, ""chunkId"": null, ""pageStart"": null, ""pageEnd"": null, ""before"": 2, ""after"": 4, ""limit"": 12}}
    }}
  ]
}}";

    private string BuildSourceBackedLlmEvidenceExplorationUserPrompt(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> alreadyTriedQueries,
        string? categoryHintsOverride = null)
    {
        var deterministicSeeds = BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(effectiveUserMessage, language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidencePlannerDeterministicSeeds)
            .ToArray();
        var sourceLeads = BuildSourceBackedLlmEvidenceSnapshotForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerSourceLeadLines,
            cueMaxLength: 70,
            maxCardHints: 1,
            maxProfileHints: 1,
            retrievalQueryMaxLength: 70);
        var structureHints = BuildSourceBackedStructureHintsForPrompt(
            toolResults,
            _mem.LastSourcesUsed,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerStructureHintLines);
        var categoryHints = categoryHintsOverride
            ?? BuildSourceBackedLlmCategoryHintsForPrompt(
                effectiveUserMessage,
                MaxSourceBackedLlmEvidencePlannerCategoryHints);
        var coverageTrace = BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerCoverageTraceLines);
        var weakRetrievalAxes = BuildSourceBackedLlmWeakRetrievalAxesForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            alreadyTriedQueries,
            maxLines: Math.Max(4, MaxSourceBackedLlmEvidencePlannerCoverageTraceLines / 2));
        var workingNotes = BuildSourceBackedLlmWorkingNotesForPrompt(
            effectiveUserMessage,
            language,
            maxLines: MaxSourceBackedLlmEvidencePlannerWorkingNoteLines);

        return $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

SUFFICIENCY:
- kind: {currentAnalysis.Kind}
- reason: {currentAnalysis.Reason}
- score: {currentAnalysis.Score}
- usableHits: {currentAnalysis.UsableHitCount}
- distinctSourcePages: {currentAnalysis.DistinctSourcePageCount}
- navigationAnchors: {currentAnalysis.NavigationAnchorCount}
- candidates: {currentAnalysis.CandidateCount}/{currentAnalysis.MinimumCandidateCount}
- targetSlots: {currentAnalysis.TargetSlotCount}
- hasRequiredAnchor: {currentAnalysis.HasRequiredAnchor}

PLANNING_COVERAGE_TRACE:
{coverageTrace}

WEAK_OR_UNDERCOVERED_AXES:
{weakRetrievalAxes}

WORKING_NOTES:
{workingNotes}

CURRENT_SOURCE_LEADS:
{sourceLeads}

STRUCTURE_HINTS:
{structureHints}

CATEGORY_HINTS:
{categoryHints}

DETERMINISTIC_QUERY_SEEDS:
{FormatPromptList(deterministicSeeds, MaxSourceBackedLlmEvidencePlannerDeterministicSeeds, maxItemLength: 90)}

ALREADY_TRIED_QUERIES:
{FormatPromptList(alreadyTriedQueries, MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries, maxItemLength: 90)}

OUTPUT_RULES:
- CATEGORY_HINTS entries expose semanticLabel for semantic fit and scopeValue for the exact scope accepted by tools. Never substitute one for the other.
- Fill categoryDecision first. Use a CATEGORY_HINTS semanticLabel when it is a clear semantic container for USER_REQUEST, then copy its exact scopeValue into categoryScope; otherwise set categoryScope null and decision ""none"".
- Category fit is where source items live, not whether the label repeats USER_REQUEST. A valid scope may be broader than the specific task.
- Copy the exact chosen scopeValue into every non-document-scoped pass. Do not invent categories outside CATEGORY_HINTS.
- Return at most {MaxSourceBackedLlmEvidenceExplorationQueries} queries total.
- Prefer 3 to 8 strong complementary queries.
- For broad plans, prioritize distinct concrete candidates until REQUEST_SHAPE minimumCandidates/targetSlots are plausible.
- For structured plans, treat days/rows/columns as placement axes, not standalone retrieval targets. Search slots, constraints, candidate inventory, source labels and concrete labels first.
- Use PLANNING_COVERAGE_TRACE and WEAK_OR_UNDERCOVERED_AXES to pivot toward weak slots, user_terms and suggested_pivots; avoid repeating failed_or_low_hit_queries with cosmetic changes.
- Use WORKING_NOTES only as strategy memory. Do not treat WORKING_NOTES as source evidence; retrieve concrete hits before relying on an item.
- Use STRUCTURE_HINTS as a map: table-of-contents, page anchors and navigation labels should lead to scoped documents.context reads or exact label queries.
- Diversify low/zero-hit axes with user aliases, source-language variants, broader/narrower option words or concrete labels from CURRENT_SOURCE_LEADS.
- Cover requested slots/criteria/phases broadly before refining one type, unless CURRENT_SOURCE_LEADS proves it is the only missing part.
- Avoid decorative filler such as details, ideas, ideal examples or suggestions unless paired with a requested slot/type, a concrete label, a constraint or a candidate name.
- Do not fan out the same query once per day/row/column. Query useful option kinds, constraints, source labels or concrete candidates once.
- Use navigation/profile labels only to reach real content pages.
- If a category is uncertain or only weakly hinted, keep categoryScope null and broaden with semantic queries.
- For pairing/recommendation requests, separate option kinds from target anchors.
- No UI prose, no explanations outside JSON, no source excerpts, no final answer text.";
    }
}
