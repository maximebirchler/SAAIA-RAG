using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> ExtractSourceBackedLlmToolCallExplorationPasses(
        JsonElement root,
        HashSet<string> emitted)
    {
        if (!root.TryGetProperty("toolCalls", out var toolCalls)
            && !root.TryGetProperty("tool_calls", out toolCalls)
            && !root.TryGetProperty("tools", out toolCalls))
        {
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }

        if (toolCalls.ValueKind != JsonValueKind.Array)
            return Array.Empty<SourceBackedEvidenceExplorationPass>();

        var passes = new List<SourceBackedEvidenceExplorationPass>();
        foreach (var call in toolCalls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object)
                continue;

            var name = NormalizeToolName(TryGetString(call, "name") ?? TryGetString(call, "tool"));
            var isDocumentContextCall = string.Equals(name, "documents.context", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(name, "rag.search", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "rag.multi_search", StringComparison.OrdinalIgnoreCase)
                && !isDocumentContextCall)
            {
                continue;
            }

            var args = TryGetObject(call, "args")
                       ?? TryGetObject(call, "arguments")
                       ?? TryGetObject(call, "input");
            if (args is null)
                continue;

            var queries = isDocumentContextCall
                ? Array.Empty<string>()
                : ExtractSanitizedSourceBackedLlmExplorationQueries(args.Value, emitted);
            if (!isDocumentContextCall && queries.Length == 0)
            {
                var query = SanitizeSourceBackedLlmExplorationQuery(
                    TryGetString(args.Value, "query")
                    ?? TryGetString(args.Value, "q"));
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var key = NormalizeGeneratedSourceBackedExplorationQueryForDedup(query);
                    if (!string.IsNullOrWhiteSpace(key) && emitted.Add(key))
                        queries = new[] { query };
                }
            }

            var docRef = NullIfWhiteSpace(
                TryGetString(args.Value, "docRef")
                ?? TryGetString(args.Value, "documentRef")
                ?? TryGetString(args.Value, "ref"));
            var docId = NullIfWhiteSpace(TryGetString(args.Value, "docId") ?? TryGetString(args.Value, "documentId"));
            var docPath = NullIfWhiteSpace(TryGetString(args.Value, "docPath") ?? TryGetString(args.Value, "documentPath"));
            var chunkId = NullIfWhiteSpace(TryGetString(args.Value, "chunkId") ?? TryGetString(args.Value, "chunk_id"));
            var pageStart = ReadSourceBackedNavigationTargetPageStart(args.Value);
            var pageEnd = ReadSourceBackedNavigationTargetPageEnd(args.Value, pageStart);

            if (!isDocumentContextCall && queries.Length == 0)
                continue;
            if (isDocumentContextCall
                && string.IsNullOrWhiteSpace(docRef)
                && string.IsNullOrWhiteSpace(docId)
                && string.IsNullOrWhiteSpace(docPath)
                && string.IsNullOrWhiteSpace(chunkId))
            {
                continue;
            }

            passes.Add(new SourceBackedEvidenceExplorationPass(
                isDocumentContextCall ? "llm_context_read" : "llm_tool_call",
                isDocumentContextCall
                    ? "LLM-planned document context read converted to an exploration pass."
                    : "LLM-planned retrieval tool call converted to an exploration pass.",
                queries,
                SanitizeSourceBackedLlmExplorationCategory(
                    TryGetString(args.Value, "categoryScope")
                    ?? TryGetString(args.Value, "category")
                    ?? TryGetString(args.Value, "categoryPath")
                    ?? TryGetString(args.Value, "categoryRef")),
                docId,
                docPath,
                pageStart,
                pageEnd,
                "llm_planner",
                docRef,
                chunkId,
                isDocumentContextCall ? "documents.context" : name));
            if (passes.Count >= MaxSourceBackedLlmEvidenceExplorationPasses)
                break;
        }

        return passes;
    }

}
