using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    public static EvidenceBundle FromToolResults(
        ToolResults toolResults,
        string userQuestion,
        string traceId = "",
        int traceSequence = 1,
        bool materializeMatchedContentCards = false)
    {
        var items = new List<EvidenceItem>();
        var retrievalAttempts = new List<SourceBackedRetrievalAttempt>();
        var sequence = 0;
        foreach (var toolItem in toolResults.Items)
        {
            sequence++;
            var before = items.Count;
            AddToolResultItems(
                items,
                toolItem,
                userQuestion,
                sequence,
                materializeMatchedContentCards);
            retrievalAttempts.Add(BuildRetrievalAttempt(toolItem, sequence, items.Count - before));
        }

        var distinctItems = items
            .GroupBy(static item => BuildEvidenceDedupeKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(static group => SelectBestMechanicalRepresentation(group))
            .Select((item, index) => item with
            {
                EvidenceId = string.IsNullOrWhiteSpace(item.EvidenceId)
                    ? $"E{index + 1}"
                    : $"E{index + 1}",
                Rank = index + 1
            })
            .ToArray();

        var resolvedTraceId = string.IsNullOrWhiteSpace(traceId)
            ? "evidence-" + Guid.NewGuid().ToString("N")[..8]
            : traceId;
        var failedAttempts = retrievalAttempts.Count(static attempt => !string.Equals(attempt.Outcome, "completed", StringComparison.OrdinalIgnoreCase));
        var timedOutAttempts = retrievalAttempts.Count(static attempt => attempt.TimedOut);
        var trace = SourceBackedTraceEvent.Create(
            resolvedTraceId,
            traceSequence,
            SourceBackedPipelineStep.EvidenceBundle,
            "evidence_bundle.built",
            new[]
            {
                new KeyValuePair<string, string>("items", distinctItems.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("tools", toolResults.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("retrieval_attempts", retrievalAttempts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("retrieval_degraded_or_failed", failedAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("retrieval_timeouts", timedOutAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture))
            });

        return new EvidenceBundle(
            "evidence-" + Guid.NewGuid().ToString("N"),
            userQuestion,
            distinctItems,
            new[] { trace })
        {
            RetrievalAttempts = retrievalAttempts
        };
    }

    private static void AddToolResultItems(
        List<EvidenceItem> items,
        ToolResults.Item toolItem,
        string userQuestion,
        int toolSequence,
        bool materializeMatchedContentCards)
    {
        if (toolItem.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        if (string.Equals(toolItem.ToolName, "documents.context", StringComparison.OrdinalIgnoreCase))
        {
            AddDocumentContextItems(
                items,
                toolItem,
                userQuestion,
                toolSequence,
                materializeMatchedContentCards);
            return;
        }

        if (string.Equals(toolItem.ToolName, "documents.navigation", StringComparison.OrdinalIgnoreCase))
        {
            AddDocumentNavigationItems(items, toolItem, userQuestion, toolSequence);
            return;
        }

        if (string.Equals(toolItem.ToolName, "documents.content_cards", StringComparison.OrdinalIgnoreCase))
        {
            AddDocumentContentCardItems(items, toolItem, userQuestion, toolSequence);
            return;
        }

        var hits = EnumerateHitObjects(toolItem.Result).ToArray();
        var resultQuery =
            GetString(toolItem.Result, "query", "queryUsed")
            ?? userQuestion;
        var embeddedSourceWindowItems = new List<EvidenceItem>();
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            var docPath = GetString(hit, "docPath", "doc_path", "path", "documentPath");
            var docName = GetString(hit, "docName", "doc_name", "documentName", "fileName");
            var pageStart = GetInt(hit, "pageStart", "page_start", "page", "pageNumber");
            var pageEnd = GetInt(hit, "pageEnd", "page_end") ?? pageStart;
            var excerpt = GetString(
                hit,
                "fullText",
                "text",
                "excerpt",
                "snippet",
                "content");
            var queryUsed =
                GetString(hit, "retrievalQuery", "query", "queryUsed")
                ?? resultQuery;

            var parent = new EvidenceItem(
                EvidenceId: string.Empty,
                SourceKind: ResolveSourceKind(toolItem.ToolName, hit),
                ToolName: toolItem.ToolName,
                QueryUsed: queryUsed,
                DocId: GetString(hit, "docId", "doc_id", "documentId"),
                DocName: docName,
                DocPath: docPath,
                SourceHash: GetString(hit, "sourceHash", "source_hash", "hash"),
                RevisionId: GetString(hit, "revisionId", "revision_id", "indexedVersion"),
                PageStart: pageStart,
                PageEnd: pageEnd,
                ChunkId: GetString(hit, "chunkId", "chunk_id"),
                Excerpt: excerpt,
                NormalizedExcerpt: NormalizeEvidenceText(excerpt),
                Score: GetDouble(hit, "score", "selectionHintSupportScore"),
                Rank: i + 1,
                CategoryPath: GetString(hit, "categoryPath", "category", "category_path"),
                DocLanguage: GetString(hit, "docLanguage", "language", "documentLanguage"),
                ProfileLanguage: GetString(hit, "profileLanguage", "profile_language"),
                ExtractionQuality: GetString(hit, "extractionQuality", "qualityStatus"),
                MatchedContentCards: ReadMatchedContentCards(hit),
                SelectionHints: ReadStringMap(hit, "selectionHints", "contentSignals"),
                CodeHints: BuildCodeHints(
                    toolItem,
                    toolSequence,
                    hit),
                RiskFlags: BuildRiskFlags(hit),
                Lineage: new[] { $"tool:{toolItem.ToolName}", $"toolSequence:{toolSequence}" });
            parent = EnrichParentWithEmbeddedRagSourceWindow(
                parent,
                hit,
                toolItem,
                toolSequence);
            items.Add(parent);
            AddEmbeddedRagSourceWindowItems(
                embeddedSourceWindowItems,
                parent,
                hit,
                toolItem,
                toolSequence);
            if (materializeMatchedContentCards)
                AddAgentMatchedContentCardItems(items, parent, hit);
        }

        // Keep the original retrieval ranking visible before its mechanical
        // neighborhoods. The evidence judge expands each candidate from the
        // canonical bundle, so windows do not crowd later top-ranked hits out.
        items.AddRange(embeddedSourceWindowItems);
    }

    private static void AddDocumentContextItems(
        List<EvidenceItem> items,
        ToolResults.Item toolItem,
        string userQuestion,
        int toolSequence,
        bool materializeMatchedContentCards)
    {
        if (toolItem.Result.ValueKind != JsonValueKind.Object
            || !TryGetProperty(toolItem.Result, "items", out var contextItems)
            || contextItems.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var hasDocument = TryGetProperty(toolItem.Result, "document", out var document)
                          && document.ValueKind == JsonValueKind.Object;
        var requestedAnchorChunkId = GetString(
            toolItem.Result,
            "requestedAnchorChunkId",
            "requested_anchor_chunk_id");
        var sourceAnchorChunkId = GetString(
            toolItem.Result,
            "sourceAnchorChunkId",
            "source_anchor_chunk_id")
            ?? requestedAnchorChunkId;
        var sourceAnchorEvidenceId = GetString(
            toolItem.Result,
            "sourceAnchorEvidenceId",
            "source_anchor_evidence_id");
        var sourceAnchorLabel = GetString(
            toolItem.Result,
            "sourceAnchorLabel",
            "source_anchor_label");
        var sourceAnchorId = GetString(
            toolItem.Result,
            "sourceAnchorId",
            "source_anchor_id");
        var hasExactSourceAnchorChunk =
            !string.IsNullOrWhiteSpace(sourceAnchorChunkId)
            && contextItems.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object
                && string.Equals(
                    GetString(item, "chunkId", "ChunkId", "chunk_id"),
                    sourceAnchorChunkId,
                    StringComparison.OrdinalIgnoreCase));
        var sourceAnchorIdentityAssigned = false;
        for (var i = 0; i < contextItems.GetArrayLength(); i++)
        {
            var contextItem = contextItems[i];
            if (contextItem.ValueKind != JsonValueKind.Object)
                continue;

            var excerpt = GetString(contextItem, "text", "Text", "excerpt", "content", "fullText");
            if (string.IsNullOrWhiteSpace(excerpt))
                continue;

            var docPath = GetString(contextItem, "docPath", "DocPath", "path", "documentPath")
                          ?? (hasDocument ? GetString(document, "docPath", "DocPath", "path", "documentPath") : null);
            var docName = GetString(contextItem, "docName", "DocName", "documentName", "fileName")
                          ?? (hasDocument ? GetString(document, "docName", "DocName", "documentName", "fileName") : null);
            var docId = GetString(contextItem, "docId", "DocId", "documentId")
                        ?? (hasDocument ? GetString(document, "docId", "DocId", "documentId") : null);
            var pageStart = GetInt(contextItem, "pageStart", "PageStart", "page", "pageNumber");
            var pageEnd = GetInt(contextItem, "pageEnd", "PageEnd") ?? pageStart;
            var codeHints = new Dictionary<string, string>(
                BuildCodeHints(toolItem, toolSequence),
                StringComparer.OrdinalIgnoreCase);
            var lineage = new List<string>
            {
                $"tool:{toolItem.ToolName}",
                $"toolSequence:{toolSequence}"
            };
            if (!string.IsNullOrWhiteSpace(requestedAnchorChunkId))
            {
                codeHints["source_window"] = "true";
                codeHints["source_window_anchor_chunk_id"] =
                    requestedAnchorChunkId;
                lineage.Add(
                    $"sourceWindowAnchor:{requestedAnchorChunkId}");
            }
            var chunkIndex = GetInt(
                contextItem,
                "chunkIndex",
                "ChunkIndex",
                "chunk_index");
            if (chunkIndex.HasValue)
            {
                codeHints["source_window_chunk_index"] =
                    chunkIndex.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            var contextChunkId = GetString(
                contextItem,
                "chunkId",
                "ChunkId",
                "chunk_id");
            if (!string.IsNullOrWhiteSpace(contextChunkId)
                && !string.IsNullOrWhiteSpace(requestedAnchorChunkId))
            {
                lineage.Add($"sourceWindowChunk:{contextChunkId}");
            }
            var selectionHints = new Dictionary<string, string>(
                ReadStringMap(
                    contextItem,
                    "selectionHints",
                    "contentSignals"),
                StringComparer.OrdinalIgnoreCase);
            var exactSourceAnchorChunk =
                !string.IsNullOrWhiteSpace(contextChunkId)
                && string.Equals(
                    contextChunkId,
                    sourceAnchorChunkId,
                    StringComparison.OrdinalIgnoreCase);
            var carriesSourceAnchorIdentity = exactSourceAnchorChunk
                || !hasExactSourceAnchorChunk
                && !sourceAnchorIdentityAssigned;
            if (carriesSourceAnchorIdentity)
            {
                if (!string.IsNullOrWhiteSpace(sourceAnchorLabel))
                {
                    selectionHints["sourceAnchorLabel"] =
                        sourceAnchorLabel;
                }
                if (!string.IsNullOrWhiteSpace(sourceAnchorEvidenceId))
                {
                    selectionHints["sourceAnchorEvidenceId"] =
                        sourceAnchorEvidenceId;
                    lineage.Add(
                        $"sourceAnchorEvidence:{sourceAnchorEvidenceId}");
                }
                selectionHints["sourceAnchorResolution"] =
                    exactSourceAnchorChunk
                        ? "exact_chunk"
                        : "first_context_fallback";
                sourceAnchorIdentityAssigned = true;
            }

            var parent = new EvidenceItem(
                EvidenceId: string.Empty,
                SourceKind: ResolveSourceKind(toolItem.ToolName, contextItem),
                ToolName: toolItem.ToolName,
                QueryUsed: GetString(toolItem.Result, "query", "queryUsed") ?? userQuestion,
                DocId: docId,
                DocName: docName,
                DocPath: docPath,
                SourceHash: GetString(
                                contextItem,
                                "sourceHash",
                                "SourceHash",
                                "source_hash",
                                "hash")
                            ?? (hasDocument
                                ? GetString(
                                    document,
                                    "sourceHash",
                                    "SourceHash",
                                    "source_hash",
                                    "hash")
                                : null),
                RevisionId: GetString(
                                contextItem,
                                "revisionId",
                                "RevisionId",
                                "revision_id",
                                "indexedVersion")
                            ?? (hasDocument
                                ? GetString(
                                    document,
                                    "revisionId",
                                    "RevisionId",
                                    "revision_id",
                                    "indexedVersion")
                                : null),
                PageStart: pageStart,
                PageEnd: pageEnd,
                ChunkId: contextChunkId,
                Excerpt: excerpt,
                NormalizedExcerpt: NormalizeEvidenceText(excerpt),
                Score: GetDouble(contextItem, "score", "Score", "selectionHintSupportScore"),
                Rank: i + 1,
                CategoryPath: GetString(contextItem, "categoryPath", "CategoryPath", "category", "category_path")
                              ?? (hasDocument ? GetString(document, "categoryPath", "CategoryPath", "category") : null),
                DocLanguage: GetString(contextItem, "docLanguage", "DocLanguage", "language", "documentLanguage"),
                ProfileLanguage: GetString(contextItem, "profileLanguage", "ProfileLanguage", "profile_language"),
                ExtractionQuality: GetString(contextItem, "extractionQuality", "qualityStatus"),
                MatchedContentCards: ReadMatchedContentCards(contextItem),
                SelectionHints: new ReadOnlyDictionary<string, string>(
                    selectionHints),
                CodeHints: new ReadOnlyDictionary<string, string>(
                    codeHints),
                RiskFlags: BuildRiskFlags(contextItem, toolItem.ToolName),
                Lineage: lineage)
            {
                AnchorId = carriesSourceAnchorIdentity
                    ? sourceAnchorId
                    : null
            };
            items.Add(parent);
            if (materializeMatchedContentCards)
                AddAgentMatchedContentCardItems(items, parent, contextItem);
        }
    }

    private static IEnumerable<JsonElement> EnumerateHitObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(root, "hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in hits.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object))
                    yield return hit;
            }

            if (TryGetProperty(root, "results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in results.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object))
                    yield return hit;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in root.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object))
                yield return hit;
        }
    }

    private static string BuildEvidenceDedupeKey(EvidenceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.DocId)
            && !string.IsNullOrWhiteSpace(item.RevisionId)
            && !string.IsNullOrWhiteSpace(item.SourceHash)
            && !string.IsNullOrWhiteSpace(item.ChunkId))
        {
            // Identical visible text can belong to different canonical chunks
            // or revisions. Preserve that provenance when removing duplicates.
            return JsonSerializer.Serialize(new[]
            {
                "canonical_chunk", item.DocId.Trim(), item.RevisionId.Trim(),
                item.SourceHash.Trim(), item.ChunkId.Trim(), item.AnchorId?.Trim(),
                item.VisibleSourceKey, NormalizeEvidenceText(item.Excerpt)
            });
        }

        if (!string.IsNullOrWhiteSpace(item.VisibleSourceKey))
        {
            // A backend content-card id is the immutable identity of a named source item.
            // The same card can be returned by the inventory with detailed facts and by a
            // search hit with a shorter excerpt. Text differences must not manufacture two
            // EvidenceIds for that one mechanical source identity.
            if (string.Equals(
                    item.SourceKind,
                    "canonical_content_card",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.ContentCardId))
            {
                return item.VisibleSourceKey;
            }

            return item.VisibleSourceKey + "|" + NormalizeEvidenceText(item.Excerpt);
        }

        return $"{item.ToolName}|{item.Rank}|{NormalizeEvidenceText(item.Excerpt)}";
    }

    private static EvidenceItem SelectBestMechanicalRepresentation(
        IEnumerable<EvidenceItem> candidates)
    {
        var representations = candidates.ToArray();
        var selected = representations
            .OrderByDescending(static item =>
                item.SelectionHints.TryGetValue("hasGroundedEvidence", out var grounded)
                && bool.TryParse(grounded, out var parsed)
                && parsed)
            .ThenByDescending(static item => item.PageStart is > 0)
            .ThenByDescending(static item => item.Excerpt?.Length ?? 0)
            .First();
        return selected with
        {
            Lineage = representations.SelectMany(static item => item.Lineage)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static string ResolveSourceKind(string toolName, JsonElement hit)
    {
        var role = GetString(hit, "contentRole", "selectionHintRole", "role");
        if (!string.IsNullOrWhiteSpace(role))
            return role!;

        if (string.Equals(toolName, "documents.context", StringComparison.OrdinalIgnoreCase))
            return "document_context";

        var selectionHints = ReadStringMap(hit, "selectionHints", "contentSignals");
        if (selectionHints.TryGetValue("evidenceRole", out var evidenceRole)
            || selectionHints.TryGetValue("evidence_role", out evidenceRole))
        {
            if (!string.IsNullOrWhiteSpace(evidenceRole))
                return evidenceRole.Trim();
        }

        if (toolName.StartsWith("rag.", StringComparison.OrdinalIgnoreCase))
            return "rag_hit";

        if (string.Equals(toolName, "documents.navigation", StringComparison.OrdinalIgnoreCase))
            return "navigation_map";

        return "tool_result";
    }

}
