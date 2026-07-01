param(
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia",
    [string]$BackendContainer = "infra-backend-1",
    [string]$QdrantBaseUrl = "http://qdrant:6333",
    [string]$QdrantCollection = "knowledge_base",
    [string]$Category = "",
    [int]$RecentHours = 24,
    [switch]$RequireIdle,
    [switch]$StrictFailedJobs,
    [switch]$SkipQdrantVectorCheck,
    [switch]$AllowQdrantVectorCheckError
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
}
catch {
    # Best effort only: older hosts may not allow changing console encodings.
}

function Escape-SqlLiteral([string]$Value) {
    return $Value.Replace("'", "''")
}

function Invoke-PostgresQuery([string]$Query) {
    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        $output = $Query | docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F "|"
        if ($LASTEXITCODE -ne 0) { throw "Postgres query failed with exit code $LASTEXITCODE." }
        return $output
    }

    $remote = "docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F '|'"
    $output = $Query | ssh $SshTarget $remote
    if ($LASTEXITCODE -ne 0) { throw "Remote Postgres query failed with exit code $LASTEXITCODE." }
    return $output
}

function Quote-Sh([string]$Value) {
    return "'" + $Value.Replace("'", "'""'""'") + "'"
}

function Invoke-QdrantVectorCountCheck([string]$TargetsJson) {
    if ([string]::IsNullOrWhiteSpace($TargetsJson) -or $TargetsJson.Trim() -eq "[]") {
        return @()
    }

    $python = @'
import json
import os
import sys
import urllib.error
import urllib.request

base_url = sys.argv[1].rstrip("/")
collection = sys.argv[2]
api_key = os.environ.get("QDRANT_API_KEY") or os.environ.get("QDRANT__SERVICE__API_KEY") or ""
targets_json = sys.stdin.buffer.read().decode("utf-8-sig")
targets = json.loads(targets_json or "[]")

def emit(check, scope, metric, value):
    print(json.dumps({
        "check": check,
        "scope": scope or "",
        "metric": metric,
        "value": str(value)
    }, ensure_ascii=True))

for target in targets:
    tenant_id = target["tenantId"]
    doc_id = target["docId"]
    version = int(target["indexedVersion"])
    expected = int(target["expectedEmbeddableChunks"])
    ineligible_chunk_ids = [str(x) for x in (target.get("ineligibleChunkIds") or []) if str(x).strip()]
    category = target.get("category") or ""
    doc_path = target.get("docPath") or ""

    def count_points(filter_body):
        request = urllib.request.Request(
            f"{base_url}/collections/{collection}/points/count",
            data=json.dumps({"exact": True, "filter": filter_body}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST")
        if api_key:
            request.add_header("api-key", api_key)
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.loads(response.read().decode("utf-8"))
            return int(payload.get("result", {}).get("count", -1))

    body = {
        "must": [
            {"key": "tenant_id", "match": {"value": tenant_id}},
            {"key": "doc_id", "match": {"value": doc_id}},
            {"key": "ingestion_version", "match": {"value": version}},
        ]
    }
    try:
        actual = count_points(body)
        delta = actual - expected
        metric = f"doc={doc_path};expected={expected};actual={actual};version={version}"
        emit("qdrant_vectors", category, metric, 0 if delta == 0 else delta)

        ineligible_count = 0
        for start in range(0, len(ineligible_chunk_ids), 256):
            batch = ineligible_chunk_ids[start:start + 256]
            if not batch:
                continue
            ineligible_filter = {
                "must": body["must"] + [
                    {"key": "chunk_id", "match": {"any": batch}}
                ]
            }
            ineligible_count += count_points(ineligible_filter)
        if ineligible_count > 0:
            metric = f"doc={doc_path};ineligible_vectors={ineligible_count};checked={len(ineligible_chunk_ids)};version={version}"
            emit("qdrant_ineligible_vectors", category, metric, ineligible_count)
    except Exception as exc:
        metric = f"doc={doc_path};version={version};error={type(exc).__name__}:{str(exc)[:180]}"
        emit("qdrant_vector_check_error", category, metric, 1)
'@

    $encodedPython = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($python))
    $bootstrap = 'import base64,sys;code=base64.b64decode(sys.argv[1]).decode();sys.argv=[sys.argv[0]]+sys.argv[2:];exec(code)'
    $scriptArg = Quote-Sh $encodedPython
    $baseArg = Quote-Sh $QdrantBaseUrl
    $collectionArg = Quote-Sh $QdrantCollection

    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        $output = $TargetsJson | docker exec -i $BackendContainer python3 -c $bootstrap $encodedPython $QdrantBaseUrl $QdrantCollection
        if ($LASTEXITCODE -ne 0) { throw "Qdrant vector check failed with exit code $LASTEXITCODE." }
        return $output
    }

    $remote = "docker exec -i $BackendContainer python3 -c " + (Quote-Sh $bootstrap) + " $scriptArg $baseArg $collectionArg"
    $output = $TargetsJson | ssh $SshTarget $remote
    if ($LASTEXITCODE -ne 0) { throw "Remote Qdrant vector check failed with exit code $LASTEXITCODE." }
    return $output
}

$categoryFilterDocuments = ""
$categoryFilterJobs = ""
$categoryFilterCurrent = ""
if (-not [string]::IsNullOrWhiteSpace($Category)) {
    $categoryLiteral = Escape-SqlLiteral $Category.Trim().ToLowerInvariant()
    $categoryFilterDocuments = " AND lower(d.category) = '$categoryLiteral'"
    $categoryFilterJobs = " AND lower(coalesce(j.category, '')) = '$categoryLiteral'"
    $categoryFilterCurrent = " AND lower(cr.category) = '$categoryLiteral'"
}

$sql = @"
WITH current_rev AS (
  SELECT d.tenant_id,
         d.doc_id,
         d.doc_path,
         d.category,
         d.indexed_version,
         r.revision_id,
         EXISTS (
           SELECT 1 FROM ingestion_jobs j
           WHERE j.tenant_id=d.tenant_id
             AND j.doc_path=d.doc_path
             AND j.status IN ('queued','running','paused')
         ) AS has_active_job
  FROM documents d
  LEFT JOIN document_revisions r
    ON r.tenant_id=d.tenant_id
   AND r.doc_id=d.doc_id
   AND r.indexed_version=d.indexed_version
  WHERE d.status='indexed'
    AND COALESCE(d.indexed_version, 0) > 0
    $categoryFilterDocuments
),
coverage AS (
  SELECT cr.*,
    (SELECT count(*) FROM document_page_index p WHERE p.revision_id=cr.revision_id) AS pages,
    (SELECT count(*) FROM document_units u WHERE u.revision_id=cr.revision_id) AS units,
    (SELECT count(*) FROM retrieval_chunks c WHERE c.revision_id=cr.revision_id) AS chunks,
    (SELECT count(*) FROM contextual_text_entries x WHERE x.revision_id=cr.revision_id AND x.retrieval_chunk_id IS NOT NULL) AS contextual,
    (SELECT count(*) FROM document_profiles p WHERE p.revision_id=cr.revision_id) AS profiles,
    (SELECT count(*) FROM document_profile_content_cards cc WHERE cc.revision_id=cr.revision_id) AS cards
  FROM current_rev cr
),
chunk_flags AS (
  SELECT cr.category,
         cr.doc_path,
         cr.tenant_id,
         cr.doc_id,
         cr.indexed_version,
         cr.revision_id,
         c.retrieval_chunk_id,
         c.chunk_index,
         c.page_start,
         c.page_end,
         c.token_count,
         c.text_content,
         c.metadata,
         lower(coalesce(c.metadata->>'contentRole', 'content')) AS content_role,
         coalesce(c.metadata->>'chunkType', '') AS chunk_type,
         CASE
           WHEN NULLIF(c.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
             THEN greatest(0.0, least(1.0, (c.metadata->>'navigationScore')::double precision))
           ELSE 0
         END AS navigation_score,
         CASE
           WHEN NULLIF(c.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
             THEN greatest(0.0, least(1.0, (c.metadata->>'contentDensityScore')::double precision))
           ELSE 0
         END AS content_density_score,
         lower(coalesce(c.metadata->>'extractionTextStatus', 'ok')) AS extraction_status,
         lower(coalesce(c.metadata->>'extractionTextSparse', 'false')) = 'true' AS extraction_sparse,
         coalesce((c.metadata->'extractionQualitySignals') ? 'replacement_chars_remaining', false) AS has_replacement_chars,
         NOT (
           lower(coalesce(c.metadata->>'contentRole', 'content')) = 'navigation'
           OR coalesce(c.metadata->>'chunkType', '') = 'navigation_index_v1'
           OR lower(coalesce(c.metadata->>'extractionTextStatus', 'ok')) = 'empty_text'
           OR (lower(coalesce(c.metadata->>'extractionTextSparse', 'false')) = 'true' AND c.token_count < 20)
           OR coalesce((c.metadata->'extractionQualitySignals') ? 'replacement_chars_remaining', false)
         ) AS embeddable
  FROM current_rev cr
  JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
  WHERE NOT cr.has_active_job
),
page_flags AS (
  SELECT cr.category,
         cr.doc_path,
         p.revision_id,
         p.page_number,
         coalesce(p.metadata #>> '{extractionQuality,qualityStatus}', '') AS quality_status,
         lower(coalesce(p.metadata #>> '{extractionQuality,manualReviewRecommended}', 'false')) = 'true'
           OR coalesce(p.metadata #>> '{extractionQuality,qualityStatus}', '') LIKE 'manual_review_%' AS manual_review
  FROM current_rev cr
  JOIN document_page_index p ON p.revision_id=cr.revision_id
  WHERE NOT cr.has_active_job
)
SELECT 'active_jobs', coalesce(j.category, ''), j.status, count(*)::text
FROM ingestion_jobs j
WHERE j.status IN ('queued','running','paused')
  $categoryFilterJobs
GROUP BY coalesce(j.category, ''), j.status
UNION ALL
SELECT 'failed_recent', coalesce(j.category, ''), 'failed', count(*)::text
FROM ingestion_jobs j
WHERE j.status='failed'
  AND j.created_at >= now() - ((GREATEST($RecentHours, 1)::text || ' hours')::interval)
  $categoryFilterJobs
GROUP BY coalesce(j.category, '')
UNION ALL
SELECT 'version_gap_no_active', d.category, 'count', count(*)::text
FROM documents d
WHERE d.status='indexed'
  AND d.ingestion_version > d.indexed_version
  $categoryFilterDocuments
  AND NOT EXISTS (
    SELECT 1 FROM ingestion_jobs j
    WHERE j.tenant_id=d.tenant_id
      AND j.doc_path=d.doc_path
      AND j.status IN ('queued','running','paused')
  )
GROUP BY d.category
UNION ALL
SELECT 'current_revision_problem', cr.category, 'count', count(*)::text
FROM current_rev cr
JOIN documents d ON d.tenant_id=cr.tenant_id AND d.doc_id=cr.doc_id
LEFT JOIN document_revisions r ON r.revision_id=cr.revision_id
WHERE (cr.revision_id IS NULL OR r.ingestion_version <> d.indexed_version)
  AND NOT cr.has_active_job
GROUP BY cr.category
UNION ALL
SELECT 'missing_content_role', cr.category, 'count', count(*)::text
FROM current_rev cr
JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
WHERE NOT (c.metadata ? 'contentRole')
  AND NOT cr.has_active_job
GROUP BY cr.category
UNION ALL
SELECT 'bad_chunks', cr.category, 'count', count(*)::text
FROM current_rev cr
JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
WHERE (length(trim(coalesce(c.text_content, ''))) = 0
   OR c.text_content LIKE '%' || U&'\FFFD' || '%')
  AND NOT cr.has_active_job
GROUP BY cr.category
UNION ALL
SELECT 'review_only_embeddable_chunk', category,
       left(doc_path || ': chunks=' || count(*), 500),
       count(*)::text
FROM (
  SELECT c.category,
         c.doc_path,
         c.retrieval_chunk_id,
         c.embeddable,
         bool_or(p.manual_review) AS any_review_page,
         bool_and(p.manual_review) AS all_review_pages
  FROM chunk_flags c
  JOIN page_flags p
    ON p.revision_id=c.revision_id
   AND p.page_number BETWEEN c.page_start AND c.page_end
  GROUP BY c.category, c.doc_path, c.retrieval_chunk_id, c.embeddable
) review_chunks
WHERE embeddable AND all_review_pages
GROUP BY category, doc_path
UNION ALL
SELECT 'review_page_embeddable_chunk', category,
       left(doc_path || ': chunks=' || count(*), 500),
       count(*)::text
FROM (
  SELECT c.category,
         c.doc_path,
         c.retrieval_chunk_id,
         c.embeddable,
         bool_or(p.manual_review) AS any_review_page,
         bool_and(p.manual_review) AS all_review_pages
  FROM chunk_flags c
  JOIN page_flags p
    ON p.revision_id=c.revision_id
   AND p.page_number BETWEEN c.page_start AND c.page_end
  GROUP BY c.category, c.doc_path, c.retrieval_chunk_id, c.embeddable
) review_chunks
WHERE embeddable AND any_review_page AND NOT all_review_pages
GROUP BY category, doc_path
HAVING count(*) >= 3
UNION ALL
SELECT 'poor_page_ratio', category,
       left(doc_path || ': manual=' || manual_pages || '/' || pages || ',ratio=' || round(manual_pages::numeric / greatest(pages, 1), 3), 500),
       manual_pages::text
FROM (
  SELECT category,
         doc_path,
         count(*) AS pages,
         count(*) FILTER (WHERE quality_status LIKE 'manual_review_%') AS manual_pages
  FROM page_flags
  GROUP BY category, doc_path
) poor_pages
WHERE (pages >= 5 AND manual_pages::numeric / greatest(pages, 1) >= 0.20)
   OR manual_pages >= 3
UNION ALL
SELECT 'navigation_score_content_conflict', category,
       left(string_agg(doc_path || '#chunk=' || chunk_index, ' ; ' ORDER BY doc_path, chunk_index), 900),
       count(*)::text
FROM chunk_flags
WHERE embeddable
  AND content_role='content'
  AND navigation_score >= 0.82
  AND content_density_score < 0.50
GROUP BY category
UNION ALL
SELECT 'invalid_navigation_score', category,
       left(string_agg(doc_path || '#chunk=' || chunk_index, ' ; ' ORDER BY doc_path, chunk_index), 900),
       count(*)::text
FROM chunk_flags
WHERE (NULLIF(metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
       AND ((metadata->>'navigationScore')::double precision < 0.0 OR (metadata->>'navigationScore')::double precision > 1.0))
   OR (NULLIF(metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
       AND ((metadata->>'contentDensityScore')::double precision < 0.0 OR (metadata->>'contentDensityScore')::double precision > 1.0))
   OR (NULLIF(metadata->>'navigationScore', '') IS NOT NULL
       AND NOT (metadata->>'navigationScore') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$')
   OR (NULLIF(metadata->>'contentDensityScore', '') IS NOT NULL
       AND NOT (metadata->>'contentDensityScore') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$')
GROUP BY category
UNION ALL
SELECT 'navigation_without_entries', c.category,
       left(c.doc_path || ': nav_chunks=' || count(*) || ',entries=' || coalesce(ne.entries, 0), 500),
       count(*)::text
FROM chunk_flags c
LEFT JOIN LATERAL (
  SELECT count(*) AS entries
  FROM document_navigation_entries ne
  WHERE ne.revision_id=c.revision_id
) ne ON true
WHERE c.content_role='navigation'
   OR c.chunk_type='navigation_index_v1'
GROUP BY c.category, c.doc_path, ne.entries
HAVING count(*) >= 5 AND coalesce(ne.entries, 0)=0
UNION ALL
SELECT 'ocr_noise_page_embeddable_chunk', p.category,
       left(string_agg(DISTINCT p.doc_path || '#p=' || p.page_number, ' ; ' ORDER BY p.doc_path || '#p=' || p.page_number), 900),
       count(DISTINCT c.retrieval_chunk_id)::text
FROM page_flags p
JOIN chunk_flags c
  ON c.revision_id=p.revision_id
 AND p.page_number BETWEEN c.page_start AND c.page_end
WHERE c.embeddable
  AND p.quality_status IN ('manual_review_probable_ocr_noise','manual_review_empty_text')
  AND NOT EXISTS (
    SELECT 1
    FROM page_flags ok_page
    WHERE ok_page.revision_id=c.revision_id
      AND ok_page.page_number BETWEEN c.page_start AND c.page_end
      AND ok_page.quality_status NOT IN ('manual_review_probable_ocr_noise','manual_review_empty_text')
  )
GROUP BY p.category
UNION ALL
SELECT 'low_text_page_embeddable_chunk', p.category,
       left(string_agg(DISTINCT p.doc_path || '#p=' || p.page_number, ' ; ' ORDER BY p.doc_path || '#p=' || p.page_number), 900),
       count(DISTINCT c.retrieval_chunk_id)::text
FROM page_flags p
JOIN chunk_flags c
  ON c.revision_id=p.revision_id
 AND p.page_number BETWEEN c.page_start AND c.page_end
WHERE c.embeddable
  AND p.quality_status='manual_review_low_text'
GROUP BY p.category
UNION ALL
SELECT 'profile_card_problem', cr.category, 'count', count(*)::text
FROM current_rev cr
JOIN document_profile_content_cards cc ON cc.revision_id=cr.revision_id
WHERE (cc.page_start IS NULL
   OR cc.page_end IS NULL
   OR NOT (cc.metadata ? 'evidence'))
  AND NOT cr.has_active_job
GROUP BY cr.category
UNION ALL
SELECT 'navigation_heavy_document', cr.category,
       left(
         cr.doc_path
         || ': navigation_or_mixed='
         || count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                              OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content','navigation_index_v1'))
         || '/'
         || count(*)
         || ',ratio='
         || round(
              (
                count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                                  OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content','navigation_index_v1'))
              )::numeric / greatest(count(*), 1),
              3),
         500),
       count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                         OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content','navigation_index_v1'))::text
FROM current_rev cr
JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
WHERE NOT cr.has_active_job
GROUP BY cr.category, cr.doc_path
HAVING count(*) >= 20
   AND (
     count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                       OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content','navigation_index_v1'))
   )::numeric / greatest(count(*), 1) >= 0.35
UNION ALL
SELECT 'suspicious_profile_card_title', cr.category,
       left(
         string_agg(
           left(cr.doc_path || ' => ' || cc.title, 160),
           ' ; '
           ORDER BY cr.doc_path, cc.card_index),
         900),
       count(*)::text
FROM current_rev cr
JOIN document_profile_content_cards cc ON cc.revision_id=cr.revision_id
WHERE NOT cr.has_active_job
  AND NULLIF(BTRIM(cc.title), '') IS NOT NULL
  AND (
    (cc.title ~ '^[[:lower:]]' AND NOT (cc.metadata ? 'evidence'))
    OR lower(cc.title) ~ '^(a|à|au|aux|de|du|des|d[''’]|pour|par|avec|sans|sur|of|for|with|without|and|or|the|to|in|on|from|per|con|senza|su|di|del|della|para|por|com|do|da|dos|das|e|y|und|oder|zu|zur|zum|von|mit)([[:space:][:punct:]]|$)'
    OR length(regexp_replace(cc.title, '[^[:alpha:]]', '', 'g')) < 4
  )
GROUP BY cr.category
UNION ALL
SELECT 'image_ocr_page_metadata_mismatch', cr.category,
       left(string_agg(DISTINCT cr.doc_path, ' ; ' ORDER BY cr.doc_path), 900),
       count(*)::text
FROM current_rev cr
JOIN document_processing_runs run ON run.revision_id=cr.revision_id
JOIN LATERAL jsonb_array_elements(
  CASE
    WHEN jsonb_typeof(run.payload #> '{ocrDiagnostics,imagePageDiagnostics}') = 'array'
      THEN run.payload #> '{ocrDiagnostics,imagePageDiagnostics}'
    ELSE '[]'::jsonb
  END
) diag ON true
LEFT JOIN document_page_index p
  ON p.revision_id=cr.revision_id
 AND p.page_number=CASE
     WHEN (diag->>'pageNumber') ~ '^[0-9]+$' THEN (diag->>'pageNumber')::int
     ELSE -1
   END
WHERE NOT cr.has_active_job
  AND run.status='done'
  AND NULLIF(diag->>'status', '') IS NOT NULL
  AND (
    p.page_number IS NULL
    OR COALESCE(p.metadata->>'imageOcrStatus', '') <> COALESCE(diag->>'status', '')
  )
GROUP BY cr.category
UNION ALL
SELECT 'artifact_coverage', category,
       concat_ws(',',
         'docs=' || count(*),
         'no_revision=' || count(*) FILTER (WHERE revision_id IS NULL),
         'no_pages=' || count(*) FILTER (WHERE pages = 0),
         'no_units=' || count(*) FILTER (WHERE units = 0),
         'no_chunks=' || count(*) FILTER (WHERE chunks = 0),
         'no_contextual=' || count(*) FILTER (WHERE contextual = 0),
         'no_profiles=' || count(*) FILTER (WHERE profiles = 0),
         'no_cards=' || count(*) FILTER (WHERE cards = 0),
         'avg_chunks=' || round(avg(chunks), 1)
       ),
       count(*)::text
FROM coverage
GROUP BY category
ORDER BY 1, 2, 3;
"@

$raw = Invoke-PostgresQuery $sql

$rows = @()
foreach ($line in $raw) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split '\|', 4
    if ($parts.Count -lt 4) { continue }
    $rows += [pscustomobject]@{
        Check = $parts[0]
        Scope = $parts[1]
        Metric = $parts[2]
        Value = $parts[3]
    }
}

if (-not $SkipQdrantVectorCheck) {
    $vectorSql = @"
WITH current_rev AS (
  SELECT d.tenant_id,
         d.doc_id,
         d.doc_path,
         d.category,
         d.indexed_version,
         r.revision_id,
         EXISTS (
           SELECT 1 FROM ingestion_jobs j
           WHERE j.tenant_id=d.tenant_id
             AND j.doc_path=d.doc_path
             AND j.status IN ('queued','running','paused')
         ) AS has_active_job
  FROM documents d
  LEFT JOIN document_revisions r
    ON r.tenant_id=d.tenant_id
   AND r.doc_id=d.doc_id
   AND r.indexed_version=d.indexed_version
  WHERE d.status='indexed'
    AND COALESCE(d.indexed_version, 0) > 0
    $categoryFilterDocuments
),
coverage AS (
  SELECT cr.*
  FROM current_rev cr
),
chunk_flags AS (
  SELECT cr.category,
         cr.doc_path,
         cr.tenant_id,
         cr.doc_id,
         cr.indexed_version,
         cr.revision_id,
         c.retrieval_chunk_id,
         NOT (
           lower(coalesce(c.metadata->>'contentRole', 'content')) = 'navigation'
           OR coalesce(c.metadata->>'chunkType', '') = 'navigation_index_v1'
           OR lower(coalesce(c.metadata->>'extractionTextStatus', 'ok')) = 'empty_text'
           OR (lower(coalesce(c.metadata->>'extractionTextSparse', 'false')) = 'true' AND c.token_count < 20)
           OR coalesce((c.metadata->'extractionQualitySignals') ? 'replacement_chars_remaining', false)
         ) AS embeddable
  FROM coverage cr
  JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
  WHERE NOT cr.has_active_job
)
SELECT COALESCE(json_agg(json_build_object(
  'tenantId', tenant_id::text,
  'docId', doc_id::text,
  'docPath', doc_path,
  'category', category,
  'indexedVersion', indexed_version,
  'expectedEmbeddableChunks', expected_embeddable_chunks,
  'ineligibleChunkIds', ineligible_chunk_ids
) ORDER BY category, doc_path)::text, '[]')
FROM (
  SELECT tenant_id,
         doc_id,
         doc_path,
         category,
         indexed_version,
         revision_id,
         count(*)::int AS total_chunks,
         count(*) FILTER (WHERE embeddable)::int AS expected_embeddable_chunks,
         COALESCE(array_agg(retrieval_chunk_id::text) FILTER (WHERE NOT embeddable), ARRAY[]::text[]) AS ineligible_chunk_ids
  FROM chunk_flags
  GROUP BY tenant_id, doc_id, doc_path, category, indexed_version, revision_id
) coverage
WHERE revision_id IS NOT NULL
  AND total_chunks > 0;
"@

    try {
        $targetsJson = (Invoke-PostgresQuery $vectorSql) -join "`n"
        $qdrantRaw = Invoke-QdrantVectorCountCheck $targetsJson
        foreach ($line in $qdrantRaw) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $item = $line | ConvertFrom-Json
            $rows += [pscustomobject]@{
                Check = [string]$item.check
                Scope = [string]$item.scope
                Metric = [string]$item.metric
                Value = [string]$item.value
            }
        }
    }
    catch {
        $rows += [pscustomobject]@{
            Check = "qdrant_vector_check_error"
            Scope = ""
            Metric = $_.Exception.Message
            Value = "1"
        }
    }
}

$issues = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

foreach ($row in $rows) {
    $valueNumber = 0
    [void][int]::TryParse($row.Value, [ref]$valueNumber)
    switch ($row.Check) {
        "active_jobs" {
            if ($valueNumber -gt 0) {
                $message = "active jobs remain: category='$($row.Scope)' status='$($row.Metric)' count=$valueNumber"
                if ($RequireIdle) { $issues.Add($message) } else { $warnings.Add($message) }
            }
        }
        "failed_recent" {
            if ($valueNumber -gt 0) {
                $message = "recent failed jobs: category='$($row.Scope)' count=$valueNumber"
                if ($StrictFailedJobs) { $issues.Add($message) } else { $warnings.Add($message) }
            }
        }
        "version_gap_no_active" {
            if ($valueNumber -gt 0) { $issues.Add("indexed version gaps without active job: category='$($row.Scope)' count=$valueNumber") }
        }
        "current_revision_problem" {
            if ($valueNumber -gt 0) { $issues.Add("current revision problems: category='$($row.Scope)' count=$valueNumber") }
        }
        "missing_content_role" {
            if ($valueNumber -gt 0) { $issues.Add("chunks missing contentRole: category='$($row.Scope)' count=$valueNumber") }
        }
        "bad_chunks" {
            if ($valueNumber -gt 0) { $issues.Add("blank or replacement-character chunks: category='$($row.Scope)' count=$valueNumber") }
        }
        "review_only_embeddable_chunk" {
            if ($valueNumber -gt 0) { $issues.Add("review-only pages still produce embeddable chunks: category='$($row.Scope)' $($row.Metric)") }
        }
        "review_page_embeddable_chunk" {
            if ($valueNumber -gt 0) { $warnings.Add("chunks overlap review pages: category='$($row.Scope)' $($row.Metric)") }
        }
        "poor_page_ratio" {
            $ratioValue = 0.0
            if ($row.Metric -match 'ratio=([0-9]+(?:\.[0-9]+)?)') {
                $ratioValue = [double]::Parse($Matches[1], [System.Globalization.CultureInfo]::InvariantCulture)
            }
            if ($ratioValue -ge 0.50 -or ($ratioValue -ge 0.35 -and $valueNumber -ge 10)) {
                $issues.Add("poor extraction page ratio: category='$($row.Scope)' $($row.Metric)")
            }
            elseif ($valueNumber -gt 0) {
                $warnings.Add("poor extraction page ratio: category='$($row.Scope)' $($row.Metric)")
            }
        }
        "navigation_score_content_conflict" {
            if ($valueNumber -ge 5) { $issues.Add("navigation-like chunks classified as content: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
            elseif ($valueNumber -gt 0) { $warnings.Add("navigation-like chunks classified as content: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "invalid_navigation_score" {
            if ($valueNumber -gt 0) { $warnings.Add("navigation/content score outside 0..1: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "navigation_without_entries" {
            if ($valueNumber -gt 0) { $warnings.Add("navigation chunks without navigation entries: category='$($row.Scope)' $($row.Metric)") }
        }
        "ocr_noise_page_embeddable_chunk" {
            if ($valueNumber -gt 0) { $issues.Add("OCR/no-text review pages still produce embeddable chunks: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "low_text_page_embeddable_chunk" {
            if ($valueNumber -gt 0) { $warnings.Add("low-text review pages overlap embeddable chunks: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "profile_card_problem" {
            if ($valueNumber -gt 0) { $warnings.Add("profile cards missing page/evidence metadata: category='$($row.Scope)' count=$valueNumber") }
        }
        "navigation_heavy_document" {
            if ($valueNumber -gt 0) { $warnings.Add("navigation-heavy document: category='$($row.Scope)' $($row.Metric)") }
        }
        "suspicious_profile_card_title" {
            if ($valueNumber -gt 0) { $warnings.Add("suspicious profile card titles: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "image_ocr_page_metadata_mismatch" {
            if ($valueNumber -gt 0) { $issues.Add("image OCR page diagnostics mismatch in page metadata: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "qdrant_vectors" {
            if ($valueNumber -ne 0) { $issues.Add("DB/Qdrant vector mismatch: category='$($row.Scope)' $($row.Metric)") }
        }
        "qdrant_ineligible_vectors" {
            if ($valueNumber -gt 0) { $issues.Add("Qdrant contains vectors for non-embeddable chunks: category='$($row.Scope)' $($row.Metric)") }
        }
        "qdrant_vector_check_error" {
            if ($valueNumber -gt 0) {
                $message = "DB/Qdrant vector check error: category='$($row.Scope)' $($row.Metric)"
                if ($AllowQdrantVectorCheckError) { $warnings.Add($message) } else { $issues.Add($message) }
            }
        }
    }
}

$result = [pscustomobject]@{
    ok = $issues.Count -eq 0
    category = if ([string]::IsNullOrWhiteSpace($Category)) { $null } else { $Category }
    requireIdle = [bool]$RequireIdle
    strictFailedJobs = [bool]$StrictFailedJobs
    qdrantVectorCheck = -not [bool]$SkipQdrantVectorCheck
    allowQdrantVectorCheckError = [bool]$AllowQdrantVectorCheckError
    issues = $issues
    warnings = $warnings
    rows = $rows
}

$result | ConvertTo-Json -Depth 8
if ($issues.Count -gt 0) { exit 1 }
