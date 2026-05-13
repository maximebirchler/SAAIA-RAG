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
    expected = int(target["expectedChunks"])
    category = target.get("category") or ""
    doc_path = target.get("docPath") or ""
    body = {
        "exact": True,
        "filter": {
            "must": [
                {"key": "tenant_id", "match": {"value": tenant_id}},
                {"key": "doc_id", "match": {"value": doc_id}},
                {"key": "ingestion_version", "match": {"value": version}},
            ]
        }
    }
    request = urllib.request.Request(
        f"{base_url}/collections/{collection}/points/count",
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST")
    if api_key:
        request.add_header("api-key", api_key)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.loads(response.read().decode("utf-8"))
            actual = int(payload.get("result", {}).get("count", -1))
        delta = actual - expected
        metric = f"doc={doc_path};expected={expected};actual={actual};version={version}"
        emit("qdrant_vectors", category, metric, 0 if delta == 0 else delta)
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
                              OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content'))
         || '/'
         || count(*)
         || ',ratio='
         || round(
              (
                count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                                  OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content'))
              )::numeric / greatest(count(*), 1),
              3),
         500),
       count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                         OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content'))::text
FROM current_rev cr
JOIN retrieval_chunks c ON c.revision_id=cr.revision_id
WHERE NOT cr.has_active_job
GROUP BY cr.category, cr.doc_path
HAVING count(*) >= 20
   AND (
     count(*) FILTER (WHERE COALESCE(c.metadata->>'contentRole', '') IN ('navigation','mixed_navigation_content')
                       OR COALESCE(c.metadata->>'chunkType', '') IN ('navigation','mixed_navigation_content'))
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
    cc.title ~ '^[[:lower:]]'
    OR lower(cc.title) ~ '^(a|à|au|aux|de|du|des|d[''’]|pour|par|avec|sans|sur|of|for|with|without|and|or|the|to|in|on|from|per|con|senza|su|di|del|della|para|por|com|do|da|dos|das|e|y|und|oder|zu|zur|zum|von|mit)([[:space:][:punct:]]|$)'
    OR length(regexp_replace(cc.title, '[^[:alpha:]]', '', 'g')) < 4
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
  SELECT cr.*,
    (SELECT count(*) FROM retrieval_chunks c WHERE c.revision_id=cr.revision_id) AS chunks
  FROM current_rev cr
)
SELECT COALESCE(json_agg(json_build_object(
  'tenantId', tenant_id::text,
  'docId', doc_id::text,
  'docPath', doc_path,
  'category', category,
  'indexedVersion', indexed_version,
  'expectedChunks', chunks
) ORDER BY category, doc_path)::text, '[]')
FROM coverage
WHERE revision_id IS NOT NULL
  AND chunks > 0
  AND NOT has_active_job;
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
        "profile_card_problem" {
            if ($valueNumber -gt 0) { $warnings.Add("profile cards missing page/evidence metadata: category='$($row.Scope)' count=$valueNumber") }
        }
        "navigation_heavy_document" {
            if ($valueNumber -gt 0) { $warnings.Add("navigation-heavy document: category='$($row.Scope)' $($row.Metric)") }
        }
        "suspicious_profile_card_title" {
            if ($valueNumber -gt 0) { $warnings.Add("suspicious profile card titles: category='$($row.Scope)' count=$valueNumber examples=$($row.Metric)") }
        }
        "qdrant_vectors" {
            if ($valueNumber -ne 0) { $issues.Add("DB/Qdrant vector mismatch: category='$($row.Scope)' $($row.Metric)") }
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
