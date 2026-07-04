using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;
using System.Text.Json;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    private const int ExtractionPagePreviewMaxItems = 3;
    private const int ExtractionPagePreviewMaxChars = 420;

    private static async Task<IResult> ExtractionQualityPagesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string docSql = @"
SELECT
  d.doc_id AS ""DocId"",
  d.doc_path AS ""DocPath"",
  d.status AS ""DocumentStatus"",
  lr.status AS ""ProcessingRunStatus"",
  d.indexed_version AS ""IndexedVersion"",
  rev.revision_id AS ""RevisionId"",
  CASE
    WHEN LOWER(COALESCE(NULLIF(lr.payload ->> 'documentIndexable', ''), '')) IN ('true','false')
      THEN LOWER(lr.payload ->> 'documentIndexable')::boolean
    WHEN NULLIF(COALESCE(lr.payload ->> 'failureReason', d.auto_ingest_pause_reason), '') IS NOT NULL
      THEN false
    ELSE true
  END AS ""DocumentIndexable"",
  NULLIF(COALESCE(lr.payload ->> 'failureReason', d.auto_ingest_pause_reason), '') AS ""FailureReason"",
  lr.payload ->> 'extractionSource' AS ""ExtractionSource"",
  lr.payload ->> 'diagnosticScope' AS ""DiagnosticScope"",
  CASE
    WHEN LOWER(COALESCE(NULLIF(lr.payload ->> 'ocrAttempted', ''), '')) IN ('true','false')
      THEN LOWER(lr.payload ->> 'ocrAttempted')::boolean
    ELSE false
  END AS ""OcrAttempted"",
  CASE
    WHEN LOWER(COALESCE(NULLIF(lr.payload ->> 'ocrApplied', ''), '')) IN ('true','false')
      THEN LOWER(lr.payload ->> 'ocrApplied')::boolean
    ELSE false
  END AS ""OcrApplied"",
  lr.payload ->> 'ocrLanguages' AS ""OcrLanguages"",
  CASE
    WHEN COALESCE(lr.payload ->> 'ocrDurationMs', '') ~ '^[0-9]{1,18}$'
      THEN (lr.payload ->> 'ocrDurationMs')::bigint
    ELSE NULL
  END AS ""OcrDurationMs"",
  (lr.payload -> 'ocrDiagnostics')::text AS ""OcrDiagnosticsJson"",
  CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,pageCount}', '') ~ '^[0-9]{1,9}$'
    THEN (lr.payload #>> '{extractionQuality,pageCount}')::int ELSE 0 END AS ""RunPageCount"",
  CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,emptyPageCount}', '') ~ '^[0-9]{1,9}$'
    THEN (lr.payload #>> '{extractionQuality,emptyPageCount}')::int ELSE 0 END AS ""RunEmptyPageCount"",
  CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,sparsePageCount}', '') ~ '^[0-9]{1,9}$'
    THEN (lr.payload #>> '{extractionQuality,sparsePageCount}')::int ELSE 0 END AS ""RunSparsePageCount"",
  CASE
    WHEN LOWER(COALESCE(NULLIF(lr.payload #>> '{extractionQuality,ocrRecommended}', ''), '')) IN ('true','false')
      THEN LOWER(lr.payload #>> '{extractionQuality,ocrRecommended}')::boolean
    ELSE false
  END AS ""RunOcrRecommended"",
  (lr.payload -> 'pageDiagnostics')::text AS ""RunPageDiagnosticsJson""
FROM documents d
LEFT JOIN document_revisions rev
  ON rev.tenant_id=d.tenant_id
 AND rev.doc_id=d.doc_id
 AND rev.indexed_version=d.indexed_version
LEFT JOIN LATERAL (
  SELECT status, payload
  FROM document_processing_runs pr
  WHERE pr.tenant_id=d.tenant_id
    AND pr.doc_id=d.doc_id
    AND pr.action='upsert'
    AND (
      (
        d.status='indexed'
        AND pr.status='done'
        AND (
          (rev.revision_id IS NOT NULL AND pr.revision_id=rev.revision_id)
          OR (rev.revision_id IS NULL AND pr.indexed_version_after=d.indexed_version)
        )
      )
      OR (
        d.status='error'
        AND pr.status='failed'
        AND pr.indexed_version_after=COALESCE(d.indexed_version, 0)
        AND (
          LOWER(COALESCE(NULLIF(pr.payload ->> 'documentIndexable', ''), ''))='false'
          OR COALESCE(pr.payload ->> 'failureReason', '') <> ''
        )
      )
    )
  ORDER BY pr.finished_at DESC NULLS LAST, pr.started_at DESC NULLS LAST
  LIMIT 1
) lr ON true
WHERE d.tenant_id=@tenant
  AND d.doc_id=@docId
  AND (
    d.status='indexed'
    OR (
      d.status='error'
      AND COALESCE(d.indexed_version, 0)=0
      AND COALESCE(d.auto_ingest_paused, false)
      AND NULLIF(d.auto_ingest_pause_reason, '') IS NOT NULL
      AND lr.payload IS NOT NULL
    )
  )
LIMIT 1;";

        var doc = await conn.QueryFirstOrDefaultAsync<ExtractionQualityDocumentRow>(
            new CommandDefinition(docSql, new { tenant = tenantId, docId }, cancellationToken: ct));
        if (doc is null)
            return Results.NotFound(new { error = "document_not_found", docId });

        var ocrDiagnostics = ParseOptionalJsonElement(doc.OcrDiagnosticsJson);
        var failedPageItems = ParseFailedPageDiagnostics(doc.RunPageDiagnosticsJson);
        if (failedPageItems.Length > 0)
        {
            return Results.Ok(new
            {
                docId = doc.DocId,
                docPath = doc.DocPath,
                documentStatus = doc.DocumentStatus,
                processingRunStatus = doc.ProcessingRunStatus,
                indexedVersion = doc.IndexedVersion,
                documentIndexable = doc.DocumentIndexable,
                failureReason = doc.FailureReason,
                diagnosticScope = doc.DiagnosticScope,
                extractionSource = doc.ExtractionSource,
                ocrAttempted = doc.OcrAttempted,
                ocrApplied = doc.OcrApplied,
                ocrLanguages = doc.OcrLanguages,
                ocrDurationMs = doc.OcrDurationMs,
                ocrDiagnostics,
                publishedRevision = doc.RevisionId is null
                    ? null
                    : new
                    {
                        indexedVersion = doc.IndexedVersion,
                        revisionId = doc.RevisionId,
                        searchable = true
                    },
                summary = new
                {
                    pageCount = failedPageItems.Length,
                    manualReviewRecommendedPages = failedPageItems.Count(static page => page.ManualReviewRecommended),
                    probableOcrNoisePages = failedPageItems.Count(static page => page.SuspiciousUnitCount > 0),
                    emptyTextPages = failedPageItems.Count(static page => page.TextEmpty),
                    lowTextPages = failedPageItems.Count(static page => page.TextSparse),
                    imagePages = failedPageItems.Count(static page => page.ImageCount > 0)
                },
                pages = failedPageItems.Select(static page => new
                {
                    pageNumber = page.PageNumber,
                    qualityStatus = page.QualityStatus,
                    extractionConfidence = page.ExtractionConfidence,
                    manualReviewRecommended = page.ManualReviewRecommended,
                    charCount = page.CharCount,
                    wordCount = page.WordCount,
                    imageCount = page.ImageCount,
                    unitCount = page.UnitCount,
                    suspiciousUnitCount = page.SuspiciousUnitCount,
                    chunkCount = page.ChunkCount,
                    textStatus = page.TextStatus,
                    textEmpty = page.TextEmpty,
                    textSparse = page.TextSparse,
                    ocrCandidate = page.OcrCandidate,
                    imageOcrStatus = page.ImageOcrDiagnostic?.Status,
                    imageOcrReason = page.ImageOcrDiagnostic?.Reason,
                    imageOcrWordCount = page.ImageOcrDiagnostic?.OcrWordCount,
                    imageOcrCharCount = page.ImageOcrDiagnostic?.OcrCharCount,
                    imageOcrExitCode = page.ImageOcrDiagnostic?.ExitCode,
                    imageOcrTimedOut = page.ImageOcrDiagnostic?.TimedOut,
                    averageCharsPerWord = page.AverageCharsPerWord,
                    signals = page.Signals,
                    unitPreviews = page.UnitPreviews,
                    chunkPreviews = page.ChunkPreviews
                })
            });
        }

        if (doc.RevisionId is null)
        {
            return Results.Ok(new
            {
                docId = doc.DocId,
                docPath = doc.DocPath,
                documentStatus = doc.DocumentStatus,
                processingRunStatus = doc.ProcessingRunStatus,
                indexedVersion = doc.IndexedVersion,
                documentIndexable = doc.DocumentIndexable,
                failureReason = doc.FailureReason,
                diagnosticScope = doc.DiagnosticScope,
                extractionSource = doc.ExtractionSource,
                ocrAttempted = doc.OcrAttempted,
                ocrApplied = doc.OcrApplied,
                ocrLanguages = doc.OcrLanguages,
                ocrDurationMs = doc.OcrDurationMs,
                ocrDiagnostics,
                summary = new
                {
                    pageCount = doc.RunPageCount,
                    manualReviewRecommendedPages = doc.DocumentIndexable ? 0 : Math.Max(1, doc.RunPageCount),
                    probableOcrNoisePages = 0,
                    emptyTextPages = doc.RunEmptyPageCount,
                    lowTextPages = doc.RunSparsePageCount,
                    imagePages = 0
                },
                pages = Array.Empty<object>()
            });
        }

        const string pagesSql = @"
SELECT
  pi.page_number AS ""PageNumber"",
  pi.char_count AS ""CharCount"",
  CASE
    WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'wordCount')::int
    ELSE 0
  END AS ""WordCount"",
  CASE
    WHEN COALESCE(pi.metadata ->> 'imageCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'imageCount')::int
    ELSE 0
  END AS ""ImageCount"",
  pi.metadata #>> '{extractionQuality,signals}' AS ""SignalsJson""
FROM document_page_index pi
WHERE pi.tenant_id=@tenant
  AND pi.revision_id=@revisionId
ORDER BY pi.page_number;";

        const string unitsSql = @"
SELECT
  page_start AS ""PageStart"",
  page_end AS ""PageEnd"",
  text_content AS ""TextContent""
FROM document_units
WHERE tenant_id=@tenant
  AND revision_id=@revisionId
ORDER BY ordinal;";

        const string chunksSql = @"
SELECT
  page_start AS ""PageStart"",
  page_end AS ""PageEnd"",
  text_content AS ""TextContent""
FROM retrieval_chunks
WHERE tenant_id=@tenant
  AND revision_id=@revisionId
ORDER BY chunk_index;";

        var revisionId = doc.RevisionId.Value;
        var pages = (await conn.QueryAsync<ExtractionQualityPageRow>(
            new CommandDefinition(pagesSql, new { tenant = tenantId, revisionId }, cancellationToken: ct))).ToList();
        var units = (await conn.QueryAsync<ExtractionQualityUnitRow>(
            new CommandDefinition(unitsSql, new { tenant = tenantId, revisionId }, cancellationToken: ct))).ToList();
        var chunks = (await conn.QueryAsync<ExtractionQualityChunkRow>(
            new CommandDefinition(chunksSql, new { tenant = tenantId, revisionId }, cancellationToken: ct))).ToList();
        var imageOcrDiagnosticsByPage = ParseImagePageOcrDiagnostics(doc.OcrDiagnosticsJson);
        var unitsByPage = BuildExtractionPageLookup(units, static unit => unit.PageStart, static unit => unit.PageEnd);
        var chunksByPage = BuildExtractionPageLookup(chunks, static chunk => chunk.PageStart, static chunk => chunk.PageEnd);

        var pageItems = pages.Select(page =>
        {
            var unitsOnPage = unitsByPage.TryGetValue(page.PageNumber, out var pageUnits)
                ? pageUnits
                : [];
            var suspiciousUnitCount = unitsOnPage.Count(unit => OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(unit.TextContent));
            var chunksOnPage = chunksByPage.TryGetValue(page.PageNumber, out var pageChunks)
                ? pageChunks
                : [];
            var review = ExtractionQualityDiagnostics.AssessPage(
                page.WordCount,
                page.CharCount,
                page.ImageCount,
                unitsOnPage.Length,
                suspiciousUnitCount,
                chunksOnPage.Length,
                ParseJsonStringArray(page.SignalsJson));
            var includePreviews = ShouldIncludeExtractionPagePreviews(review);

            return new ExtractionQualityPageItem(
                page.PageNumber,
                review.Status,
                review.ExtractionConfidence,
                review.ManualReviewRecommended,
                page.CharCount,
                page.WordCount,
                page.ImageCount,
                unitsOnPage.Length,
                suspiciousUnitCount,
                chunksOnPage.Length,
                review.TextStatus,
                review.TextEmpty,
                review.TextSparse,
                review.OcrCandidate,
                review.AverageCharsPerWord,
                review.Signals,
                includePreviews ? BuildExtractionPreviews(unitsOnPage.Select(static unit => unit.TextContent)) : [],
                includePreviews ? BuildExtractionPreviews(chunksOnPage.Select(static chunk => chunk.TextContent)) : [],
                imageOcrDiagnosticsByPage.TryGetValue(page.PageNumber, out var imageOcrDiagnostic)
                    ? imageOcrDiagnostic
                    : null);
        }).ToArray();

        return Results.Ok(new
        {
            docId = doc.DocId,
            docPath = doc.DocPath,
            documentStatus = doc.DocumentStatus,
            processingRunStatus = doc.ProcessingRunStatus,
            indexedVersion = doc.IndexedVersion,
            documentIndexable = doc.DocumentIndexable,
            failureReason = doc.FailureReason,
            extractionSource = doc.ExtractionSource,
            ocrAttempted = doc.OcrAttempted,
            ocrApplied = doc.OcrApplied,
            ocrLanguages = doc.OcrLanguages,
            ocrDurationMs = doc.OcrDurationMs,
            ocrDiagnostics,
            summary = new
            {
                pageCount = pageItems.Length,
                manualReviewRecommendedPages = pageItems.Count(static page => page.ManualReviewRecommended),
                probableOcrNoisePages = pageItems.Count(static page => page.SuspiciousUnitCount > 0),
                emptyTextPages = pageItems.Count(static page => page.TextEmpty),
                lowTextPages = pageItems.Count(static page => page.TextSparse),
                indexedByContextPages = pageItems.Count(static page => page.QualityStatus == "page_ok_indexed_by_context"),
                imagePages = pageItems.Count(static page => page.ImageCount > 0)
            },
            pages = pageItems.Select(static page => new
            {
                pageNumber = page.PageNumber,
                qualityStatus = page.QualityStatus,
                extractionConfidence = page.ExtractionConfidence,
                manualReviewRecommended = page.ManualReviewRecommended,
                charCount = page.CharCount,
                wordCount = page.WordCount,
                imageCount = page.ImageCount,
                unitCount = page.UnitCount,
                suspiciousUnitCount = page.SuspiciousUnitCount,
                chunkCount = page.ChunkCount,
                textStatus = page.TextStatus,
                textEmpty = page.TextEmpty,
                textSparse = page.TextSparse,
                ocrCandidate = page.OcrCandidate,
                imageOcrStatus = page.ImageOcrDiagnostic?.Status,
                imageOcrReason = page.ImageOcrDiagnostic?.Reason,
                imageOcrWordCount = page.ImageOcrDiagnostic?.OcrWordCount,
                imageOcrCharCount = page.ImageOcrDiagnostic?.OcrCharCount,
                imageOcrExitCode = page.ImageOcrDiagnostic?.ExitCode,
                imageOcrTimedOut = page.ImageOcrDiagnostic?.TimedOut,
                averageCharsPerWord = page.AverageCharsPerWord,
                signals = page.Signals,
                unitPreviews = page.UnitPreviews,
                chunkPreviews = page.ChunkPreviews
            })
        });
    }

    private static Dictionary<int, T[]> BuildExtractionPageLookup<T>(
        IEnumerable<T> items,
        Func<T, int> pageStart,
        Func<T, int> pageEnd)
    {
        var mutable = new Dictionary<int, List<T>>();
        foreach (var item in items)
        {
            var start = Math.Max(1, pageStart(item));
            var end = Math.Max(start, pageEnd(item));
            for (var page = start; page <= end; page++)
            {
                if (!mutable.TryGetValue(page, out var list))
                {
                    list = new List<T>();
                    mutable[page] = list;
                }

                list.Add(item);
            }
        }

        return mutable.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray());
    }

    private static bool RangesOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd)
        => leftStart <= rightEnd && rightStart <= leftEnd;

    private static bool ShouldIncludeExtractionPagePreviews(ExtractionPageReview review)
        => review.ManualReviewRecommended
           || review.TextEmpty
           || review.TextSparse
           || review.Signals.Contains("probable_ocr_noise_units", StringComparer.Ordinal);

    private static string[] BuildExtractionPreviews(IEnumerable<string> texts)
        => texts
            .Select(NormalizeExtractionPreview)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(ExtractionPagePreviewMaxItems)
            .ToArray();

    private static string NormalizeExtractionPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= ExtractionPagePreviewMaxChars
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, ExtractionPagePreviewMaxChars - 3), "...");
    }

    private static JsonElement? ParseOptionalJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<int, ExtractionQualityImageOcrDiagnostic> ParseImagePageOcrDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("imagePageDiagnostics", out var diagnostics)
                || diagnostics.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new Dictionary<int, ExtractionQualityImageOcrDiagnostic>();
            foreach (var item in diagnostics.EnumerateArray())
            {
                if (!TryGetInt32(item, "pageNumber", out var pageNumber) || pageNumber <= 0)
                    continue;

                var status = TryGetString(item, "status");
                if (string.IsNullOrWhiteSpace(status))
                    continue;

                result[pageNumber] = new ExtractionQualityImageOcrDiagnostic(
                    status,
                    TryGetString(item, "reason"),
                    TryGetNullableInt32(item, "ocrWordCount"),
                    TryGetNullableInt32(item, "ocrCharCount"),
                    TryGetNullableInt32(item, "exitCode"),
                    TryGetBoolean(item, "timedOut"));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ExtractionQualityPageItem[] ParseFailedPageDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<ExtractionQualityPageItem>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!TryGetInt32(item, "pageNumber", out var pageNumber) || pageNumber <= 0)
                    continue;

                var qualityStatus = TryGetString(item, "qualityStatus") ?? "manual_review_extraction_failed";
                var imageOcrStatus = TryGetString(item, "imageOcrStatus");
                var imageOcrDiagnostic = string.IsNullOrWhiteSpace(imageOcrStatus)
                    ? null
                    : new ExtractionQualityImageOcrDiagnostic(
                        imageOcrStatus,
                        TryGetString(item, "imageOcrReason"),
                        TryGetNullableInt32(item, "imageOcrWordCount"),
                        TryGetNullableInt32(item, "imageOcrCharCount"),
                        TryGetNullableInt32(item, "imageOcrExitCode"),
                        TryGetBoolean(item, "imageOcrTimedOut"));

                result.Add(new ExtractionQualityPageItem(
                    pageNumber,
                    qualityStatus,
                    TryGetDouble(item, "extractionConfidence") ?? 0.15,
                    TryGetBoolean(item, "manualReviewRecommended") || qualityStatus.StartsWith("manual_review_", StringComparison.Ordinal),
                    TryGetNullableInt32(item, "charCount") ?? 0,
                    TryGetNullableInt32(item, "wordCount") ?? 0,
                    TryGetNullableInt32(item, "imageCount") ?? 0,
                    TryGetNullableInt32(item, "unitCount") ?? 0,
                    TryGetNullableInt32(item, "suspiciousUnitCount") ?? 0,
                    TryGetNullableInt32(item, "chunkCount") ?? 0,
                    TryGetString(item, "textStatus") ?? "empty",
                    TryGetBoolean(item, "textEmpty"),
                    TryGetBoolean(item, "textSparse"),
                    TryGetBoolean(item, "ocrCandidate"),
                    TryGetDouble(item, "averageCharsPerWord") ?? 0.0,
                    TryGetStringArray(item, "signals"),
                    [],
                    [],
                    imageOcrDiagnostic));
            }

            return result.OrderBy(static page => page.PageNumber).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryGetString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryGetNullableInt32(JsonElement source, string propertyName)
        => TryGetInt32(source, propertyName, out var value) ? value : null;

    private static bool TryGetInt32(JsonElement source, string propertyName, out int value)
    {
        value = 0;
        if (!source.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static double? TryGetDouble(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static bool TryGetBoolean(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.True;

    private static string[] TryGetStringArray(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class ExtractionQualityDocumentRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocumentStatus { get; set; } = "";
        public string? ProcessingRunStatus { get; set; }
        public int IndexedVersion { get; set; }
        public Guid? RevisionId { get; set; }
        public bool DocumentIndexable { get; set; } = true;
        public string? FailureReason { get; set; }
        public string? DiagnosticScope { get; set; }
        public string? ExtractionSource { get; set; }
        public bool OcrAttempted { get; set; }
        public bool OcrApplied { get; set; }
        public string? OcrLanguages { get; set; }
        public long? OcrDurationMs { get; set; }
        public string? OcrDiagnosticsJson { get; set; }
        public int RunPageCount { get; set; }
        public int RunEmptyPageCount { get; set; }
        public int RunSparsePageCount { get; set; }
        public bool RunOcrRecommended { get; set; }
        public string? RunPageDiagnosticsJson { get; set; }
    }

    private sealed class ExtractionQualityPageRow
    {
        public int PageNumber { get; set; }
        public int CharCount { get; set; }
        public int WordCount { get; set; }
        public int ImageCount { get; set; }
        public string? SignalsJson { get; set; }
    }

    private sealed class ExtractionQualityUnitRow
    {
        public int PageStart { get; set; }
        public int PageEnd { get; set; }
        public string TextContent { get; set; } = "";
    }

    private sealed class ExtractionQualityChunkRow
    {
        public int PageStart { get; set; }
        public int PageEnd { get; set; }
        public string TextContent { get; set; } = "";
    }

    private sealed record ExtractionQualityPageItem(
        int PageNumber,
        string QualityStatus,
        double ExtractionConfidence,
        bool ManualReviewRecommended,
        int CharCount,
        int WordCount,
        int ImageCount,
        int UnitCount,
        int SuspiciousUnitCount,
        int ChunkCount,
        string TextStatus,
        bool TextEmpty,
        bool TextSparse,
        bool OcrCandidate,
        double AverageCharsPerWord,
        string[] Signals,
        string[] UnitPreviews,
        string[] ChunkPreviews,
        ExtractionQualityImageOcrDiagnostic? ImageOcrDiagnostic);

    private sealed record ExtractionQualityImageOcrDiagnostic(
        string Status,
        string? Reason,
        int? OcrWordCount,
        int? OcrCharCount,
        int? ExitCode,
        bool TimedOut);
}
