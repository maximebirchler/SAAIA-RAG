param(
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia",
    [string]$Category = "",
    [int]$RecentHours = 24,
    [switch]$RequireIdle,
    [switch]$StrictFailedJobs
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Escape-SqlLiteral([string]$Value) {
    return $Value.Replace("'", "''")
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

if ([string]::IsNullOrWhiteSpace($SshTarget)) {
    $raw = $sql | docker exec -i $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F "|"
} else {
    $remote = "docker exec -i $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F '|'"
    $raw = $sql | ssh $SshTarget $remote
}

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
    }
}

$result = [pscustomobject]@{
    ok = $issues.Count -eq 0
    category = if ([string]::IsNullOrWhiteSpace($Category)) { $null } else { $Category }
    requireIdle = [bool]$RequireIdle
    strictFailedJobs = [bool]$StrictFailedJobs
    issues = $issues
    warnings = $warnings
    rows = $rows
}

$result | ConvertTo-Json -Depth 8
if ($issues.Count -gt 0) { exit 1 }
