using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    private Dictionary<string, object?> BuildAgentRuntimeSnapshot()
    {
        var memorySummary = BuildAgentMemorySummary();

        return new Dictionary<string, object?>
        {
            ["supported"] = true,
            ["routerMs"] = _lastRouterMs,
            ["toolsMs"] = _lastToolsMs,
            ["writerMs"] = _lastWriterMs,
            ["totalMs"] = _lastTotalMs,
            ["language"] = _mem.LastLanguage,
            ["critic"] = new Dictionary<string, object?>
            {
                ["enabled"] = _lastCriticMs > 0 || !string.IsNullOrWhiteSpace(_lastCriticStatus) || _lastCriticEligible,
                ["eligible"] = _lastCriticEligible,
                ["durationMs"] = _lastCriticMs,
                ["status"] = _lastCriticStatus,
                ["revisedAnswer"] = _lastCriticRevisedAnswer,
                ["warning"] = _lastCriticWarning,
                ["skipReason"] = _lastCriticSkipReason
            },
            ["tools"] = _lastToolDurations.Select(x => new Dictionary<string, object?>
            {
                ["tool"] = x.tool,
                ["durationMs"] = x.durationMs,
                ["ok"] = x.ok
            }).ToList(),
            ["turn"] = new Dictionary<string, object?>
            {
                ["intent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["writerToolNames"] = _lastWriterToolNames,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate,
                ["routerConfidence"] = _mem.LastRouterConfidence,
                ["riskFlags"] = _mem.LastRiskFlags,
                ["mode"] = _lastEffectiveMode,
                ["responseFormat"] = _lastResponseFormat
            },
            ["session"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["qa"] = new Dictionary<string, object?>
            {
                ["usedGeneralChatPrompt"] = _lastUsedGeneralChatPrompt,
                ["usedInventoryRendered"] = _lastUsedInventoryRendered,
                ["usedSummaryFlow"] = _lastUsedSummaryFlow,
                ["answerSource"] = _lastAnswerSource
            },
            ["rag"] = new Dictionary<string, object?>
            {
                ["queries"] = _mem.LastRagQueries?.Take(8).ToArray() ?? Array.Empty<string>(),
                ["hitLabels"] = _mem.LastRagHitLabels?.Take(30).ToArray() ?? Array.Empty<string>(),
                ["degradedRetrievers"] = _mem.LastRagDegradedRetrievers?.Take(16).ToArray() ?? Array.Empty<string>(),
                ["traceEventCount"] = _mem.LastRagTraceEvents?.Count ?? 0,
                ["traceEvents"] = _mem.LastRagTraceEvents?.TakeLast(80).ToArray() ?? Array.Empty<string>(),
                ["lastInferredCategoryScope"] = _mem.Execution.LastRagInferredCategoryScope,
                ["lastInferredCategoryReason"] = _mem.Execution.LastRagInferredCategoryReason,
                ["researchWorkingNoteCount"] = _mem.ResearchWorkingNotes?.Count ?? 0,
                ["researchWorkingNotes"] = _mem.ResearchWorkingNotes?
                    .TakeLast(12)
                    .Select(static note => new Dictionary<string, object?>
                    {
                        ["topicKey"] = note.TopicKey,
                        ["requestShape"] = note.RequestShape,
                        ["label"] = note.Label,
                        ["origin"] = note.Origin,
                        ["purpose"] = note.Purpose,
                        ["queries"] = note.Queries.Take(6).ToArray(),
                        ["categoryScope"] = note.CategoryScope,
                        ["docPath"] = note.DocPath,
                        ["pageStart"] = note.PageStart,
                        ["pageEnd"] = note.PageEnd,
                        ["outcome"] = note.Outcome,
                        ["accepted"] = note.Accepted,
                        ["rejectReason"] = note.RejectReason,
                        ["reasonBefore"] = note.ReasonBefore,
                        ["reasonAfter"] = note.ReasonAfter,
                        ["candidateDelta"] = note.CandidateDelta,
                        ["distinctPageDelta"] = note.DistinctPageDelta,
                        ["usableHitDelta"] = note.UsableHitDelta,
                        ["elapsedMs"] = note.ElapsedMs
                    })
                    .ToArray() ?? Array.Empty<object>(),
                ["explorationPassCount"] = _mem.Execution.LastRagEvidenceExploration?.Count ?? 0,
                ["acceptedExplorationPassCount"] = _mem.Execution.LastRagEvidenceExploration?.Count(static pass => pass.Accepted) ?? 0,
                ["lastInsufficiencyReason"] = _mem.Execution.LastRagEvidenceExploration?.LastOrDefault()?.ReasonAfter
                    ?? _mem.Execution.LastRagEvidenceExploration?.LastOrDefault()?.ReasonBefore,
                ["explorationPasses"] = _mem.Execution.LastRagEvidenceExploration?
                    .Take(8)
                    .Select(static pass => new Dictionary<string, object?>
                    {
                        ["label"] = pass.Label,
                        ["origin"] = pass.Origin,
                        ["purpose"] = pass.Purpose,
                        ["queries"] = pass.Queries.Take(6).ToArray(),
                        ["categoryScope"] = pass.CategoryScope,
                        ["docId"] = pass.DocId,
                        ["docPath"] = pass.DocPath,
                        ["pageStart"] = pass.PageStart,
                        ["pageEnd"] = pass.PageEnd,
                        ["reasonBefore"] = pass.ReasonBefore,
                        ["reasonAfter"] = pass.ReasonAfter,
                        ["scoreBefore"] = pass.ScoreBefore,
                        ["scoreAfter"] = pass.ScoreAfter,
                        ["hitsBefore"] = pass.UsableHitsBefore,
                        ["hitsAfter"] = pass.UsableHitsAfter,
                        ["candidatesBefore"] = pass.CandidateCountBefore,
                        ["candidatesAfter"] = pass.CandidateCountAfter,
                        ["distinctPagesBefore"] = pass.DistinctPagesBefore,
                        ["distinctPagesAfter"] = pass.DistinctPagesAfter,
                        ["elapsedMs"] = pass.ElapsedMs,
                        ["accepted"] = pass.Accepted,
                        ["rejectReason"] = pass.RejectReason
                    })
                    .ToArray() ?? Array.Empty<object>()
            },
            ["memorySummary"] = memorySummary,
            ["memory"] = new Dictionary<string, object?>
            {
                ["profile"] = _mem.MemoryProfile,
                ["schemaVersion"] = _mem.SchemaVersion,
                ["m1Lite"] = new Dictionary<string, object?>
                {
                    ["hasCatalogSnapshot"] = _mem.CatalogSnapshotCache is not null,
                    ["catalogCategoriesCount"] = _mem.CatalogSnapshotCache?.Categories?.Count ?? 0,
                    ["hasCapabilitiesSnapshot"] = _mem.CapabilitiesCache is not null,
                    ["isAdmin"] = _mem.CapabilitiesCache?.IsAdmin,
                    ["knownDocumentsCount"] = _mem.WorkspaceKnownDocuments?.Count ?? 0
                },
                ["m3"] = new Dictionary<string, object?>
                {
                    ["hasPendingClarification"] = _mem.PendingClarification is not null,
                    ["pendingClarificationKind"] = _mem.PendingClarification?.Kind,
                    ["hasFocusedDocument"] = _mem.LastFocusedDocument is not null,
                    ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
                    {
                        ["docId"] = _mem.LastFocusedDocument.DocId,
                        ["docPath"] = _mem.LastFocusedDocument.DocPath,
                        ["docName"] = _mem.LastFocusedDocument.DocName,
                        ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                        ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
                    },
                    ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                    ["pdfMapSize"] = _mem.PdfMap?.Count ?? 0,
                    ["lastSourcesCount"] = _mem.LastSourcesUsed?.Count ?? 0,
                    ["researchWorkingNotesCount"] = _mem.ResearchWorkingNotes?.Count ?? 0,
                    ["hasResolvedCategory"] = _mem.LastResolvedCategory is not null,
                    ["presentedCategoriesCount"] = _mem.LastPresentedCategories?.Count ?? 0
                },
                ["m6"] = new Dictionary<string, object?>
                {
                    ["lastMode"] = _mem.LastMode,
                    ["lastRouterIntent"] = _mem.LastRouterIntent,
                    ["lastToolNamesCount"] = _mem.LastToolNames?.Count ?? 0,
                    ["lastRagQueriesCount"] = _mem.LastRagQueries?.Count ?? 0,
                    ["lastRagHitLabelsCount"] = _mem.LastRagHitLabels?.Count ?? 0,
                    ["lastRagDegradedRetrieversCount"] = _mem.LastRagDegradedRetrievers?.Count ?? 0,
                    ["lastRagTraceEventCount"] = _mem.LastRagTraceEvents?.Count ?? 0,
                    ["lastRagEvidenceExplorationCount"] = _mem.Execution.LastRagEvidenceExploration?.Count ?? 0,
                    ["lastRiskFlagsCount"] = _mem.LastRiskFlags?.Count ?? 0,
                    ["hasPlannerMemoryUpdate"] = !string.IsNullOrWhiteSpace(_mem.LastPlannerMemoryUpdate),
                    ["routerConfidence"] = _mem.LastRouterConfidence,
                    ["hasAdminOperation"] = _mem.LastAdminOperation is not null,
                    ["hasStagedDirectCommand"] = _mem.StagedDirectCommand is not null
                },
                ["hasPendingClarification"] = _mem.PendingClarification is not null,
                ["pendingClarificationKind"] = _mem.PendingClarification?.Kind,
                ["lastMode"] = _mem.LastMode,
                ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
                {
                    ["docId"] = _mem.LastFocusedDocument.DocId,
                    ["docPath"] = _mem.LastFocusedDocument.DocPath,
                    ["docName"] = _mem.LastFocusedDocument.DocName,
                    ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                    ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
                },
                ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                ["pdfMapSize"] = _mem.PdfMap?.Count ?? 0,
                ["lastSourcesCount"] = _mem.LastSourcesUsed?.Count ?? 0
            }
        };
    }

    private Dictionary<string, object?> BuildAgentMemorySummary()
    {
        return new Dictionary<string, object?>
        {
            ["profile"] = _mem.MemoryProfile,
            ["schemaVersion"] = _mem.SchemaVersion,
            ["cdcAlignment"] = "v3.1",
            ["persistence"] = new Dictionary<string, object?>
            {
                ["language"] = true,
                ["style"] = true,
                ["mode"] = false,
                ["focusedDocument"] = false,
                ["resolvedCategory"] = false
            },
            ["resetPolicy"] = new Dictionary<string, object?>
            {
                ["preservesM1Lite"] = true,
                ["preservesPreferences"] = true,
                ["clearsM3"] = true,
                ["clearsM6"] = true,
                ["resetsModeToAuto"] = true
            },
            ["workspace"] = new Dictionary<string, object?>
            {
                ["catalogCategoriesCount"] = _mem.CatalogSnapshotCache?.Categories?.Count ?? 0,
                ["knownDocumentsCount"] = _mem.WorkspaceKnownDocuments?.Count ?? 0,
                ["hasCapabilitiesSnapshot"] = _mem.CapabilitiesCache is not null
            },
            ["session"] = new Dictionary<string, object?>
            {
                ["hasFocusedDocument"] = _mem.LastFocusedDocument is not null,
                ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                ["researchWorkingNotesCount"] = _mem.ResearchWorkingNotes?.Count ?? 0,
                ["hasResolvedCategory"] = _mem.LastResolvedCategory is not null,
                ["hasPendingClarification"] = _mem.PendingClarification is not null
            },
            ["execution"] = new Dictionary<string, object?>
            {
                ["mode"] = _mem.LastMode,
                ["hasRouterIntent"] = !string.IsNullOrWhiteSpace(_mem.LastRouterIntent),
                ["toolNamesCount"] = _mem.LastToolNames?.Count ?? 0,
                ["lastRagQueriesCount"] = _mem.LastRagQueries?.Count ?? 0,
                ["lastRagHitLabelsCount"] = _mem.LastRagHitLabels?.Count ?? 0,
                ["lastRagDegradedRetrieversCount"] = _mem.LastRagDegradedRetrievers?.Count ?? 0,
                ["lastRagTraceEventCount"] = _mem.LastRagTraceEvents?.Count ?? 0,
                ["lastRagEvidenceExplorationCount"] = _mem.Execution.LastRagEvidenceExploration?.Count ?? 0,
                ["hasAdminOperation"] = _mem.LastAdminOperation is not null
            }
        };
    }

}
