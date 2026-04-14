using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class RagEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);
        app.MapPost("/rag/search", SearchAsync);
        app.MapPost("/rag/query", QueryAsync);
        app.MapGet("/rag/debug/scroll", ScrollAsync).RequireAdminKey();
    }

    private static async Task<IResult> CategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

        const string sql = """
SELECT DISTINCT category
FROM documents
WHERE tenant_id=@tenant_id AND status='indexed'
ORDER BY category;
""";

        var cats = (await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted)
        )).ToArray();

        return Results.Ok(cats);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ds, ragOpt.Value, httpFactory, req);
        return Results.Ok(new
        {
            query = req.Query,
            category = resp.Category,
            topK = resp.TopK,
            matches = resp.Matches
        });
    }

    private static async Task<IResult> SearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ds, ragOpt.Value, httpFactory, req);

        var responseDto = new RagSearchResponseDto(
            RequestId: resp.RequestId,
            Query: resp.Query,
            QueryNormalized: resp.QueryNormalized,
            Category: resp.Category,
            TopK: resp.TopK,
            MinScore: resp.MinScore,
            Candidates: resp.Candidates,
            MaxPerDoc: resp.MaxPerDoc,
            MaxPerPage: resp.MaxPerPage,
            Metrics: new RagMetricsDto(
                TookMs: resp.Timings.TotalMs,
                Returned: resp.Matches.Count,
                ExactMatchReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal)),
                DenseReturned: resp.Matches.Count(m => string.Equals(ResolveRetriever(m), "dense_qdrant", StringComparison.Ordinal)),
                LinkedReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "linked_context_v1", StringComparison.Ordinal)),
                RetrieversUsed: resp.Matches
                    .Select(ResolveRetriever)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                DataHash: ComputeDataHash(resp.Matches),
                TtlSeconds: 600,
                TeiMs: resp.Timings.TeiMs,
                QdrantMs: resp.Timings.QdrantMs,
                CandidatesEvaluated: resp.Candidates
            ),
            Items: resp.Matches
                .Select(m => new RagItemDto(
                    Score: m.Score,
                    DocId: m.DocId,
                    DocName: m.DocName ?? "Unknown",
                    DocPath: m.DocPath,
                    Category: resp.Category,
                    PageStart: m.PageStart,
                    PageEnd: m.PageEnd,
                    ChunkId: m.ChunkId,
                    ChunkIndex: m.ChunkIndex,
                    Text: m.Text ?? string.Empty,
                    Retriever: ResolveRetriever(m),
                    Provenance: ResolveProvenance(m),
                    ExactMatchHit: string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal),
                    SourceHash: m.HashDoc,
                    EmbeddingBasis: m.EmbeddingBasis,
                    ChunkType: m.ChunkType,
                    SectionTitle: m.SectionTitle,
                    HeadingPath: m.HeadingPath,
                    PrevChunkId: m.PrevChunkId,
                    NextChunkId: m.NextChunkId,
                    SameSectionChunkId: m.SameSectionChunkId,
                    ProvenanceInfo: BuildProvenanceInfo(m),
                    Context: BuildContextInfo(m),
                    CategoryPath: resp.Category,
                    Snippet: BuildSnippet(m.Text),
                    RerankScore: null,
                    HasTable: DetectHasTable(m.Text),
                    HasWarning: DetectHasWarning(m.Text, m.ChunkType),
                    ContextualSnippet: m.EmbedText
                ))
                .ToList()
        );

        return Results.Ok(responseDto);
    }

    private static async Task<RagSearchResponse> SearchCoreAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = ctx.GetTenantId();

        if (string.IsNullOrWhiteSpace(req.Query))
            throw new BadHttpRequestException("query is required");

        var queryNorm = NormalizeQuery(req.Query);

        var topK = req.TopK ?? rag.DefaultTopK;
        topK = Math.Clamp(topK, 1, rag.MaxTopK);

        var category = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();

        var mode = (req.Mode ?? "balanced").Trim().ToLowerInvariant();
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * 12,
            _ => topK * 6
        };
        // CDC v3.0 §11.3: max 3 chunks per document default
        int defMaxPerDoc = mode switch
        {
            "focused" => Math.Min(topK, 3),
            "broad" => 2,
            _ => Math.Min(3, Math.Max(2, topK / 2))
        };

        var minScore = Math.Clamp(req.MinScore ?? defMinScore, 0.0, 1.0);
        var candidates = Math.Clamp(req.Candidates ?? defCandidates, topK, Math.Max(topK, rag.MaxTopK * 20));
        var maxPerDoc = Math.Clamp(req.MaxPerDoc ?? defMaxPerDoc, 1, topK);
        var maxPerPage = Math.Clamp(req.MaxPerPage ?? 1, 1, topK);

        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
            if (req.Diversity.PreferDistinctPages == true)
                maxPerPage = 1;
        }

        var ct = ctx.RequestAborted;
        var swTotal = Stopwatch.StartNew();

        var selected = new List<RagMatch>(capacity: topK);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var exactMatches = await SearchExactMatchesAsync(ds, tenantId, req.Query, category, req.DocId, req.DocPath, topK, ct);
        AddRankedMatches(selected, selectedKeys, exactMatches, topK, minScore: 0.0, maxPerDoc, maxPerPage);

        long teiMs = 0;
        long qdrantMs = 0;
        int qdrantStatus = 0;

        if (selected.Count < topK)
        {
            var denseMatches = await SearchDenseMatchesAsync(
                ds,
                httpFactory,
                rag,
                tenantId,
                queryNorm,
                category,
                req.DocId,
                req.DocPath,
                candidates,
                ct,
                teiMsRef: value => teiMs = value,
                qdrantMsRef: value => qdrantMs = value,
                qdrantStatusRef: value => qdrantStatus = value);

            AddRankedMatches(selected, selectedKeys, denseMatches, topK, minScore, maxPerDoc, maxPerPage);
        }

        if (selected.Count < topK)
        {
            var linkedMatches = await SearchLinkedMatchesAsync(
                ds,
                tenantId,
                selected,
                category,
                req.DocId,
                req.DocPath,
                topK - selected.Count,
                ct);

            AddRankedMatches(selected, selectedKeys, linkedMatches, topK, minScore: 0.0, maxPerDoc, Math.Max(maxPerPage, 2));
        }

        if (selected.Count < topK)
        {
            var secondWaveLinkedMatches = await SearchLinkedMatchesAsync(
                ds,
                tenantId,
                selected,
                category,
                req.DocId,
                req.DocPath,
                topK - selected.Count,
                ct);

            AddRankedMatches(selected, selectedKeys, secondWaveLinkedMatches, topK, minScore: 0.0, maxPerDoc, Math.Max(maxPerPage, 2));
        }

        // CDC v3.0 §11.3: autocut — remove trailing results after largest relative score drop
        ApplyAutocut(selected, minScore);

        swTotal.Stop();

        return new RagSearchResponse(
            RequestId: ctx.GetRequestId(),
            Query: req.Query,
            QueryNormalized: queryNorm,
            Category: category,
            TopK: topK,
            MinScore: minScore,
            Candidates: candidates,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            QdrantStatus: qdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: swTotal.ElapsedMilliseconds,
                TeiMs: teiMs,
                QdrantMs: qdrantMs),
            Matches: selected);
    }

    internal static async Task<List<RagMatch>> SearchExactMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        var normalizedTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query);
        if (normalizedTerms.Count == 0)
            return [];

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH lookup_terms AS (
    SELECT DISTINCT term
    FROM unnest(@normalized_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    e.page_start AS "PageStart",
    e.page_end AS "PageEnd",
    e.exact_match_entry_id AS "ExactMatchEntryId",
    e.entry_index AS "ChunkIndex",
    e.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(e.metadata->>'kind', 'verbatim_excerpt') AS "MatchKind",
    s.title AS "SectionTitle",
    lookup_terms.term AS "MatchedTerm"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN exact_match_entries e
  ON e.tenant_id = r.tenant_id
 AND e.revision_id = r.revision_id
JOIN lookup_terms
  ON lookup_terms.term = e.normalized_text
LEFT JOIN document_sections s
  ON s.section_id = e.section_id
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE COALESCE(e.metadata->>'kind', 'verbatim_excerpt')
        WHEN 'standard_ref' THEN 0
        WHEN 'code_ref' THEN 1
        ELSE 2
    END,
    LENGTH(lookup_terms.term) DESC,
    d.updated_at DESC,
    e.page_start ASC,
    e.entry_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<ExactMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            normalized_terms = normalizedTerms.ToArray(),
            category,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK
        }, cancellationToken: ct));

        return rows.Select(row => new RagMatch(
            Score: ComputeExactMatchScore(row.MatchKind, row.MatchedTerm, row.Text),
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            PageStart: row.PageStart,
            PageEnd: row.PageEnd,
            ChunkId: row.ExactMatchEntryId.ToString(),
            ChunkIndex: row.ChunkIndex,
            Text: row.Text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: row.Text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: row.SectionTitle,
            HeadingPath: row.SectionTitle,
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null)).ToList();
    }

    private static async Task<List<RagMatch>> SearchDenseMatchesAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        Guid tenantId,
        string queryNorm,
        string? category,
        string? docId,
        string? docPath,
        int candidates,
        CancellationToken ct,
        Action<long> teiMsRef,
        Action<long> qdrantMsRef,
        Action<int> qdrantStatusRef)
    {
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var swTei = Stopwatch.StartNew();
        var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, [queryNorm], ct);
        swTei.Stop();
        teiMsRef(swTei.ElapsedMilliseconds);

        var qvec = emb[0];
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(category))
            filterMust.Add(new { key = "category", match = new { value = category } });
        if (!string.IsNullOrWhiteSpace(docId))
            filterMust.Add(new { key = "doc_id", match = new { value = docId.Trim() } });
        if (!string.IsNullOrWhiteSpace(docPath))
            filterMust.Add(new { key = "doc_path", match = new { value = docPath.Trim().Replace('\\', '/') } });

        var payload = new
        {
            vector = qvec,
            limit = candidates,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";
        HttpResponseMessage? resp = null;

        var swQ = Stopwatch.StartNew();
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))
                resp = await qdrant.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = null;
                await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

                using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                resp = await qdrant.PostAsync(url, content2, ct);
            }

            qdrantStatusRef((int)resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var rawMatches = QdrantClient.ParseSearchResults(doc);
            var filtered = await FilterMatchesAgainstActiveDocumentVersionsAsync(ds, tenantId, rawMatches, ct);
            return RerankDenseMatches(filtered);
        }
        finally
        {
            swQ.Stop();
            qdrantMsRef(swQ.ElapsedMilliseconds);
            resp?.Dispose();
        }
    }

    private static async Task<List<RagMatch>> FilterMatchesAgainstActiveDocumentVersionsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> rawMatches,
        CancellationToken ct)
    {
        if (rawMatches.Count == 0)
            return [];

        var docIds = rawMatches
            .Select(m => Guid.TryParse(m.DocId, out var docId) ? docId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (docIds.Length == 0)
            return [];

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT doc_id AS "DocId",
       status AS "Status",
       indexed_version AS "IndexedVersion",
       LOWER(ENCODE(content_hash, 'hex')) AS "ContentHashHex"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_id = ANY(@doc_ids);
""";

        var rows = await conn.QueryAsync<DocVersionRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = docIds
        }, cancellationToken: ct));

        var active = rows.ToDictionary(x => x.DocId, x => x);
        var filtered = new List<RagMatch>(rawMatches.Count);
        foreach (var match in rawMatches)
        {
            if (!Guid.TryParse(match.DocId, out var docIdValue))
                continue;
            if (!active.TryGetValue(docIdValue, out var row))
                continue;
            if (!string.Equals(row.Status, "indexed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.IndexedVersion <= 0)
                continue;

            var versionMatches = match.IngestionVersion.HasValue && match.IngestionVersion.Value == row.IndexedVersion;
            var legacyHashMatches = !match.IngestionVersion.HasValue
                && !string.IsNullOrWhiteSpace(match.HashDoc)
                && !string.IsNullOrWhiteSpace(row.ContentHashHex)
                && string.Equals(match.HashDoc, row.ContentHashHex, StringComparison.OrdinalIgnoreCase);

            if (!versionMatches && !legacyHashMatches)
                continue;

            filtered.Add(match);
        }

        return filtered;
    }

    private sealed record DocVersionRow(Guid DocId, string Status, int IndexedVersion, string? ContentHashHex);
    private sealed record ExactMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        Guid ExactMatchEntryId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string MatchKind,
        string? SectionTitle,
        string? MatchedTerm);

    private sealed record LinkedMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        Guid ChunkId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string ChunkType,
        string? SectionTitle,
        string? HeadingPath,
        string LinkType,
        Guid AnchorSourceId,
        Guid AnchorChunkId,
        string? PrevChunkId,
        string? NextChunkId,
        string? SameSectionChunkId);

    private static void AddRankedMatches(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        int maxPerSection = 2)
    {
        var perDoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectedMatch in selected)
        {
            if (string.IsNullOrWhiteSpace(selectedMatch.DocPath))
                continue;

            var docKey = selectedMatch.DocPath!;
            perDoc[docKey] = perDoc.TryGetValue(docKey, out var docCount) ? docCount + 1 : 1;
            var pageKey = $"{docKey}:{selectedMatch.PageStart ?? -1}:{selectedMatch.PageEnd ?? -1}";
            perPage[pageKey] = perPage.TryGetValue(pageKey, out var pageCount) ? pageCount + 1 : 1;
            var sectionKey = BuildSectionKey(selectedMatch);
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secCount) ? secCount + 1 : 1;
        }

        foreach (var match in matches)
        {
            if (selected.Count >= topK)
                break;
            if (match.Score < minScore)
                continue;
            if (string.IsNullOrWhiteSpace(match.DocPath))
                continue;

            var dedupKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(dedupKey))
                continue;

            var docKey = match.DocPath!;
            perDoc.TryGetValue(docKey, out var docCountCurrent);
            if (docCountCurrent >= maxPerDoc)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            var pageKey = $"{docKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCountCurrent);
            if (pageCountCurrent >= maxPerPage)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            // CDC v3.0 §11.3: max 2 chunks per section
            var sectionKey = BuildSectionKey(match);
            if (sectionKey != null)
            {
                perSection.TryGetValue(sectionKey, out var secCountCurrent);
                if (secCountCurrent >= maxPerSection)
                {
                    selectedKeys.Remove(dedupKey);
                    continue;
                }
            }

            selected.Add(match);
            perDoc[docKey] = docCountCurrent + 1;
            perPage[pageKey] = pageCountCurrent + 1;
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secUpdated) ? secUpdated + 1 : 1;
        }
    }

    private static string? BuildSectionKey(RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.DocPath))
            return null;
        // Use SectionOrdinal if available, fallback to SectionTitle
        if (match.SectionOrdinal.HasValue)
            return $"{match.DocPath}:sec:{match.SectionOrdinal.Value}";
        if (!string.IsNullOrWhiteSpace(match.SectionTitle))
            return $"{match.DocPath}:sec:{match.SectionTitle}";
        return null;
    }

    internal static async Task<List<RagMatch>> SearchLinkedMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> anchorMatches,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        if (topK <= 0)
            return [];

        var denseOrLinkedAnchors = anchorMatches
            .Where(match =>
            {
                var retriever = ResolveRetriever(match);
                return (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                        || string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                    && Guid.TryParse(match.ChunkId, out _);
            })
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: ResolveRetriever(match),
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var exactAnchors = anchorMatches
            .Where(match =>
                string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal)
                && Guid.TryParse(match.ChunkId, out _))
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: "exact_match",
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var anchorCandidates = denseOrLinkedAnchors
            .Concat(exactAnchors)
            .OrderByDescending(item => item.AnchorScore)
            .Take(Math.Max(1, Math.Min(topK * 2, 4)))
            .ToList();

        if (anchorCandidates.Count == 0)
            return [];

        var anchorsBySourceId = anchorCandidates.ToDictionary(item => item.SourceId);
        var denseAnchorIds = denseOrLinkedAnchors
            .Where(item => string.Equals(item.SourceRetriever, "dense_qdrant", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var linkedAnchorIds = denseOrLinkedAnchors
            .Where(item => string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var exactAnchorIds = exactAnchors
            .Select(item => item.SourceId)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH requested_anchors AS (
    SELECT source_anchor_id, anchor_chunk_id, anchor_retriever
    FROM (
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'dense_qdrant'::text AS anchor_retriever
        FROM unnest(@dense_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'linked_context'::text AS anchor_retriever
        FROM unnest(@linked_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT
            exact_anchors.exact_match_entry_id AS source_anchor_id,
            resolved_chunks.retrieval_chunk_id AS anchor_chunk_id,
            'exact_match'::text AS anchor_retriever
        FROM unnest(@exact_anchor_entry_ids::uuid[]) AS exact_anchor_id
        JOIN exact_match_entries exact_anchors
          ON exact_anchors.tenant_id = @tenant_id
         AND exact_anchors.exact_match_entry_id = exact_anchor_id
        JOIN LATERAL (
            SELECT rc.retrieval_chunk_id
            FROM retrieval_chunks rc
            WHERE rc.revision_id = exact_anchors.revision_id
              AND (
                    (exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id)
                 OR (exact_anchors.unit_id IS NULL
                     AND exact_anchors.section_id IS NOT NULL
                     AND rc.section_id = exact_anchors.section_id
                     AND rc.page_start <= exact_anchors.page_end
                     AND rc.page_end >= exact_anchors.page_start)
              )
            ORDER BY
                CASE
                    WHEN exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id THEN 0
                    ELSE 1
                END,
                rc.chunk_index ASC
            LIMIT 1
        ) AS resolved_chunks ON TRUE
    ) anchors
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(rc.metadata->>'chunkType', 'linked_context_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    l.link_type AS "LinkType",
    a.source_anchor_id AS "AnchorSourceId",
    l.retrieval_chunk_id AS "AnchorChunkId",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId"
FROM requested_anchors a
JOIN retrieval_chunk_links l
  ON l.tenant_id = @tenant_id
 AND l.retrieval_chunk_id = a.anchor_chunk_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = l.linked_chunk_id
JOIN document_revisions r
  ON r.revision_id = rc.revision_id
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.status = 'indexed'
 AND d.indexed_version = r.indexed_version
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE l.link_type
        WHEN 'same_section' THEN 0
        WHEN 'next' THEN 1
        ELSE 2
    END,
    rc.chunk_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<LinkedMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            dense_anchor_chunk_ids = denseAnchorIds,
            linked_anchor_chunk_ids = linkedAnchorIds,
            exact_anchor_entry_ids = exactAnchorIds,
            category,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK * 3
        }, cancellationToken: ct));

        return rows.Select(row =>
        {
            anchorsBySourceId.TryGetValue(row.AnchorSourceId, out var anchor);
            return new RagMatch(
                Score: ComputeLinkedMatchScore(anchor?.AnchorScore ?? 0.0, row.LinkType, anchor?.SourceRetriever),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.Text,
                EmbeddingBasis: "linked_context_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId);
        }).ToList();
    }

    /// <summary>
    /// CDC v3.0 §11.3: autocut — detect the largest relative score drop between consecutive
    /// results and trim everything after the gap, provided the absolute threshold is also met.
    /// Keeps at least 1 result. Only cuts if the gap is significant (>= 15% relative drop).
    /// </summary>
    internal static void ApplyAutocut(List<RagMatch> matches, double absoluteMinScore)
    {
        if (matches.Count <= 2)
            return;

        var bestGapIndex = -1;
        var bestGapRatio = 0.0;
        const double minRelativeDrop = 0.15;

        for (var i = 1; i < matches.Count; i++)
        {
            var prev = matches[i - 1].Score;
            var curr = matches[i].Score;
            if (prev <= 0.0)
                continue;

            var drop = (prev - curr) / prev;
            if (drop > bestGapRatio)
            {
                bestGapRatio = drop;
                bestGapIndex = i;
            }
        }

        if (bestGapRatio >= minRelativeDrop && bestGapIndex > 0)
            matches.RemoveRange(bestGapIndex, matches.Count - bestGapIndex);

        // Also enforce absolute minimum on remaining items (skip exact_match which always passes)
        matches.RemoveAll(m =>
            m.Score < absoluteMinScore
            && !string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal));
    }

    internal static string BuildMatchDedupKey(RagMatch match)
        => $"{match.DocId}|{match.PageStart}|{match.PageEnd}|{ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty)}";

    internal static List<RagMatch> RerankDenseMatches(IReadOnlyList<RagMatch> matches)
        => matches
            .Select(match => new
            {
                Match = match,
                Score = match.Score + ComputeDenseBoost(match)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .Select(item => item.Match with { Score = item.Score })
            .ToList();

    private static double ComputeDenseBoost(RagMatch match)
    {
        double boost = 0.0;
        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            boost += 0.02;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            boost += 0.01;

        if (!string.IsNullOrWhiteSpace(match.HeadingPath))
            boost += 0.005;
        if (string.Equals(match.EmbeddingBasis, "contextual_text_v1", StringComparison.Ordinal))
            boost += 0.005;

        return boost;
    }

    internal static double ComputeLinkedMatchScore(double anchorScore, string linkType, string? anchorRetriever = null)
    {
        var penalty = linkType switch
        {
            "same_section" => 0.02,
            "next" => 0.035,
            "prev" => 0.04,
            _ => 0.05
        };

        if (string.Equals(anchorRetriever, "linked_context", StringComparison.Ordinal))
            penalty += 0.015;
        else if (string.Equals(anchorRetriever, "exact_match", StringComparison.Ordinal))
            penalty += 0.01;

        return Math.Max(0.0, anchorScore - penalty);
    }

    private sealed record LinkedAnchorCandidate(Guid SourceId, string SourceRetriever, double AnchorScore);

    internal static double ComputeExactMatchScore(string? matchKind, string? matchedTerm, string? text)
    {
        var score = matchKind switch
        {
            "standard_ref" => 1.0,
            "code_ref" => 0.985,
            _ => 0.96
        };

        var normalizedMatchedTerm = ExactMatchEntryExtractor.NormalizeForLookup(matchedTerm ?? string.Empty);
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedMatchedTerm))
        {
            score += Math.Min(0.01, normalizedMatchedTerm.Length / 500.0);
            if (string.Equals(normalizedMatchedTerm, normalizedText, StringComparison.Ordinal))
                score += 0.005;
        }

        return Math.Min(1.02, score);
    }

    private static string ResolveRetriever(RagMatch match)
        => match.EmbeddingBasis switch
        {
            "exact_match_v1" => "exact_match",
            "linked_context_v1" => "linked_context",
            _ => "dense_qdrant"
        };

    internal static string ResolveProvenance(RagMatch match)
        => $"retriever:{ResolveRetriever(match)}";

    internal static RagItemProvenanceDto BuildProvenanceInfo(RagMatch match)
        => new(
            Channel: ResolveRetriever(match),
            Label: ResolveProvenance(match),
            SourceHash: match.HashDoc,
            ChunkId: match.ChunkId,
            PageStart: match.PageStart,
            PageEnd: match.PageEnd);

    internal static RagItemContextDto BuildContextInfo(RagMatch match)
        => new(
            ChunkType: match.ChunkType,
            SectionTitle: match.SectionTitle,
            HeadingPath: match.HeadingPath,
            PrevChunkId: match.PrevChunkId,
            NextChunkId: match.NextChunkId,
            SameSectionChunkId: match.SameSectionChunkId);

    internal static string? BuildSnippet(string? text, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        // Cut at last sentence boundary within limit
        var cutoff = normalized.LastIndexOf('.', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = normalized.LastIndexOf(' ', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return normalized[..(cutoff + 1)].TrimEnd();
    }

    internal static bool DetectHasTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // Heuristic: rows with pipe separators or tab-separated columns
        var lines = text.Split('\n');
        var pipeLines = lines.Count(l => l.Contains('|') && l.Count(c => c == '|') >= 2);
        return pipeLines >= 2;
    }

    internal static bool DetectHasWarning(string? text, string? chunkType)
    {
        if (string.Equals(chunkType, "warning", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var upper = text.AsSpan();
        return upper.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("DANGER", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("CAUTION", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("AVERTISSEMENT", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ComputeDataHash(IReadOnlyList<RagMatch> matches)
    {
        if (matches.Count == 0)
            return null;

        using var sha = SHA256.Create();
        foreach (var match in matches)
        {
            var canonical = $"{match.DocId}|{match.ChunkId}|{match.IngestionVersion}|{match.HashDoc}|{match.EmbeddingBasis}";
            var bytes = Encoding.UTF8.GetBytes(canonical);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string NormalizeQuery(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0)
            return s;

        var sb = new StringBuilder(s.Length);
        var inWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString();
    }

    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit,
        string? docPath)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        docPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(docPath))
            must.Add(new { key = "doc_path", match = new { value = docPath } });

        var body = new
        {
            limit = Math.Clamp(limit ?? 20, 1, 200),
            with_payload = true,
            filter = new
            {
                must = must.ToArray()
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            content,
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }
}

public sealed record RagQueryRequest(string Query, string? Category, int? TopK);

public sealed record RagSearchRequest(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null
);

public sealed record RagSearchTimings(long TotalMs, long TeiMs, long QdrantMs);

public sealed record RagSearchResponse(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    int QdrantStatus,
    RagSearchTimings Timings,
    IReadOnlyList<RagMatch> Matches
);
