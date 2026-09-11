using System.Diagnostics;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedEvidenceWindowCandidateLimit = 5;
    private const int SourceBackedEvidenceWindowBefore = 4;
    private const int SourceBackedEvidenceWindowAfter = 2;
    private const int SourceBackedEvidenceWindowItemLimit = 7;
    private const int SourceBackedEvidenceWindowParallelism = 4;

    /// <summary>
    /// Adds a bounded indexed neighborhood to the best canonical RAG hits.
    /// This is a mechanical retrieval projection: ranking and semantic
    /// suitability remain owned by the LLM evidence judge.
    /// </summary>
    private async Task<JsonElement> EnrichSourceBackedEvidenceWindowsAsync(
        JsonElement normalized,
        CancellationToken ct)
    {
        if (normalized.ValueKind != JsonValueKind.Object
            || !normalized.TryGetProperty("hits", out var hitsElement)
            || hitsElement.ValueKind != JsonValueKind.Array
            || hitsElement.GetArrayLength() == 0)
        {
            return normalized;
        }

        var stopwatch = Stopwatch.StartNew();
        var hits = hitsElement
            .EnumerateArray()
            .Select(static hit => hit.Clone())
            .ToArray();
        var enrichmentCount = Math.Min(
            SourceBackedEvidenceWindowCandidateLimit,
            hits.Length);
        using var parallelism = new SemaphoreSlim(
            SourceBackedEvidenceWindowParallelism);

        EmitRagTrace(
            "rag.search.evidence_windows.start",
            ("hits", hits.Length),
            ("candidates", enrichmentCount),
            ("before", SourceBackedEvidenceWindowBefore),
            ("after", SourceBackedEvidenceWindowAfter));

        var windows = await Task.WhenAll(
                hits.Take(enrichmentCount).Select(
                    async (hit, index) =>
                    {
                        var chunkId = TryGetString(hit, "chunkId");
                        if (string.IsNullOrWhiteSpace(chunkId))
                            return new SourceBackedEvidenceWindowResult(
                                index,
                                chunkId,
                                Array.Empty<JsonElement>(),
                                null);

                        await parallelism.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var context = await _api.DocumentsContextAsync(
                                    TryGetString(hit, "docId"),
                                    TryGetString(hit, "docPath"),
                                    chunkId,
                                    pageStart: null,
                                    pageEnd: null,
                                    SourceBackedEvidenceWindowBefore,
                                    SourceBackedEvidenceWindowAfter,
                                    SourceBackedEvidenceWindowItemLimit,
                                    offset: 0,
                                    ct)
                                .ConfigureAwait(false);
                            var identityError = ValidateSourceBackedEvidenceWindowIdentity(hit, context);
                            var items = identityError is null
                                ? ReadSourceBackedEvidenceWindowItems(context)
                                : Array.Empty<JsonElement>();
                            if (identityError is null && !items.Any(item => string.Equals(
                                    TryGetString(item, "chunkId"), chunkId, StringComparison.OrdinalIgnoreCase)))
                            {
                                identityError = "source_window_anchor_missing";
                                items = Array.Empty<JsonElement>();
                            }
                            return new SourceBackedEvidenceWindowResult(
                                index,
                                chunkId,
                                items,
                                identityError);
                        }
                        catch (OperationCanceledException)
                            when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            return new SourceBackedEvidenceWindowResult(
                                index,
                                chunkId,
                                Array.Empty<JsonElement>(),
                                ex.GetType().Name);
                        }
                        finally
                        {
                            parallelism.Release();
                        }
                    }))
            .ConfigureAwait(false);

        var byIndex = windows.ToDictionary(
            static window => window.HitIndex);
        var enrichedHits = new JsonElement[hits.Length];
        var windowCount = 0;
        var windowItemCount = 0;
        var failureCount = 0;
        for (var index = 0; index < hits.Length; index++)
        {
            if (!byIndex.TryGetValue(index, out var window))
            {
                enrichedHits[index] = hits[index];
                continue;
            }

            if (!string.IsNullOrWhiteSpace(window.Error))
            {
                failureCount++;
                var failedProperties = hits[index].EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => (object?)property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
                failedProperties["sourceWindowError"] = window.Error;
                enrichedHits[index] = JsonSerializer.SerializeToElement(failedProperties, ClientJson.CamelCase);
                continue;
            }
            if (window.Items.Count == 0)
            {
                enrichedHits[index] = hits[index];
                continue;
            }

            windowCount++;
            windowItemCount += window.Items.Count;
            var properties = hits[index]
                .EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => (object?)property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
            properties["sourceWindowAnchorChunkId"] = window.AnchorChunkId;
            properties["sourceWindow"] = window.Items;
            enrichedHits[index] = JsonSerializer.SerializeToElement(
                properties,
                ClientJson.CamelCase);
        }

        var rootProperties = normalized
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        rootProperties["hits"] = enrichedHits;
        var enriched = JsonSerializer.SerializeToElement(
            rootProperties,
            ClientJson.CamelCase);

        EmitRagTrace(
            "rag.search.evidence_windows.end",
            ("hits", hits.Length),
            ("windows", windowCount),
            ("window_items", windowItemCount),
            ("failures", failureCount),
            ("ms", stopwatch.ElapsedMilliseconds));
        return enriched;
    }

    private static string? ValidateSourceBackedEvidenceWindowIdentity(JsonElement hit, JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object || TryGetBool(context, "found") != true)
            return "source_window_document_not_found";
        if (!context.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object)
            return "source_window_identity_missing";

        // A file can be re-extracted without changing its hash. Every identity
        // component must still match before neighboring text inherits the hit's provenance.
        foreach (var field in new[] { "docId", "docPath", "revisionId", "sourceHash" })
        {
            var expected = TryGetString(hit, field);
            var actual = TryGetString(document, field);
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
                return "source_window_identity_missing";
            if (field == "docPath")
            {
                expected = expected.Trim().Replace('\\', '/').TrimStart('/');
                actual = actual.Trim().Replace('\\', '/').TrimStart('/');
            }
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return "source_window_identity_mismatch";
        }

        return TryGetBool(context, "anchorFound") == true ? null : "source_window_anchor_not_found";
    }

    private static IReadOnlyList<JsonElement>
        ReadSourceBackedEvidenceWindowItems(JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return items
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Where(static item =>
                !string.IsNullOrWhiteSpace(
                    TryGetString(item, "chunkId"))
                && !string.IsNullOrWhiteSpace(
                    TryGetString(item, "text")))
            .Take(SourceBackedEvidenceWindowItemLimit)
            .Select(static item => item.Clone())
            .ToArray();
    }

    private sealed record SourceBackedEvidenceWindowResult(
        int HitIndex,
        string? AnchorChunkId,
        IReadOnlyList<JsonElement> Items,
        string? Error);
}
