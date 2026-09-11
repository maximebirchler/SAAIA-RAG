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
    private const int CompactSourceBackedRouterCategoryHintLimit = 32;

    private static string BuildCompactSourceBackedRouterSystemPrompt(string detectedLanguage, bool disallowMetaSetLanguage)
        => $@"
You are SAAIA Router. Output ONLY valid JSON.
SAAIA is corpus-first. Use documentary tools whenever the answer needs a
real-world fact, item, option, instruction, method, recommendation, comparison,
selection or plan. This includes requests equivalent to propose, advise or give
me an option. Never answer those from model memory. chat.general without tools
is reserved for greetings, social/meta chat, rewriting, translation and purely
creative work. Clarify only when no safe action is possible.

Capabilities:
- rag.search: one narrow source query.
- rag.multi_search: complementary queries for a broad or multi-item need.
- documents.navigation: locate a named document, section or page; map only.
- documents.context: read around an already known document/page/chunk.
- documents.categories: inspect corpus structure.

Rules:
- Response language hint: {NormalizeLanguageCode(detectedLanguage)}.
- meta.set_language forbidden: {(disallowMetaSetLanguage ? "true" : "false")}.
- Choose tools, queries and their number yourself. Keep source queries short.
- Preserve every explicit slot, role, criterion and quality. Do not turn a
  quality such as easy into an invented proxy such as fast or few ingredients.
- CATEGORY_HINTS are exact ""scope = meaning"" pairs. Use the exact scope only
  when its meaning fits; otherwise omit category. Never invent one.
- Navigation is not evidence. Context needs an existing document/page/chunk.
- Never search for meta-terms such as sommaire, index, catalogue or list unless
  the user explicitly asks for them.
- Every source-backed route must include sourceBackedMission, authored by you.
  A complete atomic evidence item is one usable requested instance, not a
  heading, component, category, axis label or formatting slot.
- Operational test for atomicEvidenceType: name the distinct source-backed
  object that will actually occupy and be cited in each output position. Its
  type remains the same if two row or column labels are exchanged. Never use a
  requested role, period, column label or generic slot type as that object.
- Preserve only details explicitly requested by the user. Never add fields,
  subfields, ingredients, steps, measurements or completeness criteria that
  were not requested. Asking for one option requires one usable option, not a
  full record about it.
- A structured layout exists only when visible positions form axes. Then include
  structuredLayout=true, rowCount, columnCount, atomicEvidenceCount=rowCount*
  columnCount, rowHeader, rowLabels and columns in their requested order.
- For an unstructured request, omit those defaults; one atomic item is assumed.
- sourceBackedMission.planKind is single_item for one unstructured instance,
  multi_item for several unstructured instances, or structured_layout for
  visible axes.
- initialCapability must match the first tool and does not restrict later tools.
- Keep JSON minimal. Omit defaults, empty fields, null category, topK=8 and the
  default mode.

Shapes:
source-backed:
{{""intent"":""rag.answer"",""toolCalls"":[{{""name"":""rag.search"",""args"":{{""query"":""short query"",""category"":""exact scope if useful""}}}}],""sourceBackedMission"":{{""planKind"":""single_item"",""deliverable"":""exact deliverable"",""atomicEvidenceType"":""complete requested instance"",""initialCapability"":""rag_search""}}}}
clarification: {{""needClarification"":true,""clarificationQuestions"":[""short question""]}}
chat only: {{""intent"":""chat.general""}}
";

    private string BuildCompactSourceBackedRouterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        bool includeCategoryHints = true)
        => $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 2)}

FOCUSED_DOCUMENT_MEMORY:
{BuildCompactSourceBackedRouterFocusedDocumentContext()}

CATEGORY_HINTS:
{(includeCategoryHints
    ? BuildSourceBackedLlmCategoryHintsForPrompt(
        userMessage,
        maxCategories: CompactSourceBackedRouterCategoryHintLimit,
        compact: true)
    : "omitted_for_context_budget")}

USER_MESSAGE:
{userMessage}
";

    private string BuildCompactSourceBackedRouterFocusedDocumentContext()
    {
        var document = _mem.LastFocusedDocument;
        if (document is null
            || string.IsNullOrWhiteSpace(document.DocId)
               && string.IsNullOrWhiteSpace(document.DocPath)
               && string.IsNullOrWhiteSpace(document.DocName))
        {
            return "none";
        }

        return JsonSerializer.Serialize(new
        {
            docId = TruncateForPrompt(document.DocId, 120),
            docPath = TruncateForPrompt(document.DocPath, 220),
            docName = TruncateForPrompt(document.DocName, 160),
            categoryPath = TruncateForPrompt(document.CategoryPath, 180)
        });
    }

    private Dictionary<string, object?> BuildRouterMemoryContext(DocumentRefResolver.AnalysisResult resolverHint)
    {
        return new Dictionary<string, object?>
        {
            ["profile"] = _mem.MemoryProfile,
            ["lastLanguage"] = _mem.LastLanguage,
            ["lastUserDetectedLanguage"] = _mem.LastUserDetectedLanguage,
            ["lastAnswerLanguage"] = _mem.LastAnswerLanguage,
            ["lastList"] = new Dictionary<string, object?>
            {
                ["offset"] = _mem.LastListOffset,
                ["limit"] = _mem.LastListLimit,
                ["categoryPath"] = _mem.LastListCategoryPath,
                ["q"] = _mem.LastListQuery,
                ["total"] = _mem.LastListTotal
            },
            ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
            {
                ["docId"] = _mem.LastFocusedDocument.DocId,
                ["docPath"] = _mem.LastFocusedDocument.DocPath,
                ["docName"] = _mem.LastFocusedDocument.DocName,
                ["category"] = _mem.LastFocusedDocument.Category,
                ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
            },
            ["lastTurn"] = new Dictionary<string, object?>
            {
                ["user"] = _mem.LastUserMessage,
                ["assistant"] = _mem.LastAssistantAnswer,
                ["routerIntent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["reasoningTracePublic"] = _mem.LastReasoningTracePublic,
                ["routerConfidence"] = _mem.LastRouterConfidence,
                ["riskFlags"] = _mem.LastRiskFlags,
                ["mode"] = _mem.LastMode,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate
            },
            ["pendingClarification"] = _mem.PendingClarification is null ? null : new Dictionary<string, object?>
            {
                ["kind"] = _mem.PendingClarification.Kind,
                ["originalUserMessage"] = _mem.PendingClarification.OriginalUserMessage,
                ["hint"] = _mem.PendingClarification.Hint,
                ["language"] = _mem.PendingClarification.Language,
                ["createdAtUtc"] = _mem.PendingClarification.CreatedAtUtc
            },
            ["adminSession"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["lastResolvedCategory"] = _mem.LastResolvedCategory is null ? null : new Dictionary<string, object?>
            {
                ["categoryRef"] = _mem.LastResolvedCategory.CategoryRef,
                ["categoryPath"] = _mem.LastResolvedCategory.CategoryPath,
                ["displayName"] = _mem.LastResolvedCategory.DisplayName,
                ["ordinal"] = _mem.LastResolvedCategory.Ordinal,
                ["totalDocuments"] = _mem.LastResolvedCategory.TotalDocuments,
                ["aliases"] = _mem.LastResolvedCategory.Aliases
            },
            ["lastPresentedCategories"] = _mem.LastPresentedCategories?.Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["totalDocuments"] = x.TotalDocuments,
                ["aliases"] = x.Aliases
            }).ToList(),
            ["m1Lite"] = new Dictionary<string, object?>
            {
                ["canonicalCategories"] = BuildRouterCanonicalCategoryHints(),
                ["canonicalDocuments"] = BuildRouterCanonicalDocumentHints()
            },
            ["lastSummaryStatus"] = _mem.LastSummaryStatusSnapshot is null ? null : new Dictionary<string, object?>
            {
                ["categoryPath"] = _mem.LastSummaryStatusSnapshot.CategoryPath,
                ["categoryRef"] = _mem.LastSummaryStatusSnapshot.CategoryRef,
                ["mode"] = _mem.LastSummaryStatusSnapshot.Mode,
                ["total"] = _mem.LastSummaryStatusSnapshot.Total,
                ["missingStored"] = _mem.LastSummaryStatusSnapshot.MissingStored,
                ["staleStored"] = _mem.LastSummaryStatusSnapshot.StaleStored,
                ["profileMissing"] = _mem.LastSummaryStatusSnapshot.ProfileMissing,
                ["itemsCount"] = _mem.LastSummaryStatusSnapshot.Items?.Count ?? 0
            },
            ["resolverHint"] = new Dictionary<string, object?>
            {
                ["isContentRequest"] = resolverHint.IsContentRequest,
                ["wantsAbout"] = resolverHint.WantsAbout,
                ["wantsSummary"] = resolverHint.WantsSummary,
                ["wantsStoredSummaryCheck"] = resolverHint.WantsStoredSummaryCheck,
                ["wantsStoredSummaryStore"] = resolverHint.WantsStoredSummaryStore,
                ["resolvedDocRef"] = resolverHint.ResolvedDocRef,
                ["needsClarification"] = resolverHint.NeedsClarification,
                ["clarificationKind"] = resolverHint.ClarificationKind
            }
        };
    }

    private Dictionary<string, object?> BuildCompactRouterMemoryContext(DocumentRefResolver.AnalysisResult resolverHint)
    {
        return new Dictionary<string, object?>
        {
            ["lastLanguage"] = _mem.LastLanguage,
            ["lastIntent"] = _mem.LastRouterIntent,
            ["lastToolNames"] = _mem.LastToolNames?.Take(5).ToArray() ?? Array.Empty<string>(),
            ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
            {
                ["docId"] = _mem.LastFocusedDocument.DocId,
                ["docPath"] = _mem.LastFocusedDocument.DocPath,
                ["docName"] = _mem.LastFocusedDocument.DocName,
                ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath
            },
            ["pendingClarification"] = _mem.PendingClarification is null ? null : new Dictionary<string, object?>
            {
                ["kind"] = _mem.PendingClarification.Kind,
                ["hint"] = _mem.PendingClarification.Hint,
                ["language"] = _mem.PendingClarification.Language
            },
            ["lastResolvedCategory"] = _mem.LastResolvedCategory is null ? null : new Dictionary<string, object?>
            {
                ["categoryRef"] = _mem.LastResolvedCategory.CategoryRef,
                ["categoryPath"] = _mem.LastResolvedCategory.CategoryPath,
                ["displayName"] = _mem.LastResolvedCategory.DisplayName
            },
            ["resolverHint"] = new Dictionary<string, object?>
            {
                ["isContentRequest"] = resolverHint.IsContentRequest,
                ["wantsAbout"] = resolverHint.WantsAbout,
                ["wantsSummary"] = resolverHint.WantsSummary,
                ["wantsStoredSummaryCheck"] = resolverHint.WantsStoredSummaryCheck,
                ["resolvedDocRef"] = resolverHint.ResolvedDocRef,
                ["needsClarification"] = resolverHint.NeedsClarification,
                ["clarificationKind"] = resolverHint.ClarificationKind
            }
        };
    }

    private List<Dictionary<string, object?>> BuildRouterCanonicalCategoryHints()
        => (_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DisplayName) || !string.IsNullOrWhiteSpace(x.CategoryPath))
            .OrderBy(x => x.Ordinal == 0 ? int.MaxValue : x.Ordinal)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["aliases"] = x.Aliases?.Take(4).ToList()
            })
            .ToList();

    private List<Dictionary<string, object?>> BuildRouterCanonicalDocumentHints()
        => (_mem.WorkspaceKnownDocuments ?? new List<ToolMemory.DocumentItem>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DocName) || !string.IsNullOrWhiteSpace(x.DocPath))
            .OrderBy(x => x.DocName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["docId"] = x.DocId,
                ["docPath"] = x.DocPath,
                ["docName"] = x.DocName,
                ["category"] = x.Category,
                ["categoryPath"] = x.CategoryPath
            })
            .ToList();

    private static string DescribeToolAction(string toolName, string userMessage, string language, JsonElement args)
    {
        _ = userMessage;
        var docRef = GetStringArg(args, "docRef") ?? GetStringArg(args, "pdfRef") ?? string.Empty;

        return toolName switch
        {
            "summary.exists" => DeterministicAgentText.ProgressCheckStoredSummaryForDocument(docRef, language),
            "summary.get" => DeterministicAgentText.ProgressLoadStoredSummaryForDocument(docRef, language),
            "rag.summarize_live" => DeterministicAgentText.ProgressBuildLiveSummaryForDocument(docRef, language),
            _ => DeterministicAgentText.ToolAction(toolName, language)
        };
    }

}
