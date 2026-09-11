param(
    [Parameter(Mandatory = $true)]
    [string]$DocPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia",
    [switch]$IncludeMissingStructuralControl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Escape-SqlLiteral([string]$Value) {
    return $Value.Replace("'", "''")
}

$escapedDocPath = Escape-SqlLiteral $DocPath
$includeMissingStructuralControlSql = if ($IncludeMissingStructuralControl) { "TRUE" } else { "FALSE" }
$query = @"
WITH target_doc AS (
  SELECT
    d.tenant_id,
    d.doc_id,
    d.doc_path,
    d.doc_name,
    d.category,
    d.page_count,
    d.indexed_version,
    saaia_document_summary_source_hash(
      d.content_hash,
      d.doc_path,
      d.file_size,
      d.file_mtime,
      d.indexed_version) AS source_hash,
    r.revision_id
  FROM documents d
  JOIN LATERAL (
    SELECT revision_id
    FROM document_revisions r
    WHERE r.tenant_id=d.tenant_id
      AND r.doc_id=d.doc_id
      AND r.indexed_version=d.indexed_version
    ORDER BY r.published_at DESC NULLS LAST
    LIMIT 1
  ) r ON true
  WHERE d.doc_path='$escapedDocPath'
    AND d.status='indexed'
    AND d.indexed_version>0
), source_candidates AS (
  SELECT
    'retrieval_chunk'::text AS source_kind,
    rc.retrieval_chunk_id AS source_id,
    rc.chunk_index AS ordinal,
    rc.page_start,
    rc.page_end,
    rc.token_count,
    rc.text_content,
    0 AS source_rank,
    COALESCE(rc.metadata->>'contentRole', 'content') AS content_role,
    CASE COALESCE(rc.metadata->>'contentRole', 'content')
      WHEN 'content' THEN 0
      WHEN 'mixed_navigation_content' THEN 1
      ELSE 2
    END AS role_rank,
    CASE
      WHEN (rc.metadata->>'navigationScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$'
        THEN (rc.metadata->>'navigationScore')::double precision
      ELSE 0
    END AS navigation_score,
    CASE
      WHEN (rc.metadata->>'contentDensityScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$'
        THEN (rc.metadata->>'contentDensityScore')::double precision
      ELSE 0
    END AS content_density_score,
    (NULLIF(BTRIM(rc.metadata->>'headingPath'), '') IS NOT NULL
      OR NULLIF(BTRIM(rc.metadata->>'sectionTitle'), '') IS NOT NULL) AS has_structural_anchor
  FROM target_doc d
  JOIN retrieval_chunks rc ON rc.revision_id=d.revision_id
  WHERE length(trim(rc.text_content))>0
    AND COALESCE(rc.metadata->>'contentRole', 'content')<>'navigation'
    AND COALESCE(rc.metadata->>'chunkType', '')<>'navigation_index_v1'
  UNION ALL
  SELECT
    'document_unit'::text,
    du.unit_id,
    du.ordinal,
    du.page_start,
    du.page_end,
    du.token_count,
    du.text_content,
    1,
    'content'::text,
    0,
    0::double precision,
    0::double precision,
    false
  FROM target_doc d
  JOIN document_units du ON du.revision_id=d.revision_id
  WHERE length(trim(du.text_content))>0
    AND NOT EXISTS (
      SELECT 1
      FROM retrieval_chunks rc_any
      WHERE rc_any.revision_id=d.revision_id)
), ordered_units AS (
  SELECT
    *,
    row_number() OVER (ORDER BY ordinal) AS rn,
    count(*) OVER () AS total
  FROM source_candidates
), scored_units AS (
  SELECT
    *,
    CASE
      WHEN text_content ~ '\.{8,}' THEN 1
      WHEN text_content ~* '(^|[[:space:]])(r.f.rence|reference|referencia|referenz|riferiment|refer.ncia)' THEN 1
      WHEN text_content ~* '(https?://|www\.)' THEN 1
      WHEN text_content ~* '(r.daction|remerciements|acknowledg|copyright|isbn)' THEN 1
      ELSE 0
    END AS low_value_rank
  FROM ordered_units
), bucketed AS (
  SELECT
    *,
    floor(((rn - 1)::numeric * 8) / greatest(total, 1))::integer AS bucket,
    length(text_content) AS char_count
  FROM scored_units
), ranked AS (
  SELECT
    *,
    row_number() OVER (
      PARTITION BY bucket
      ORDER BY
        source_rank ASC,
        role_rank ASC,
        low_value_rank ASC,
        navigation_score ASC,
        content_density_score DESC,
        char_count DESC,
        ordinal ASC) AS bucket_rank
  FROM bucketed
), representatives AS (
  SELECT
    source_kind,
    source_id,
    ordinal,
    page_start,
    page_end,
    token_count,
    text_content,
    content_role,
    navigation_score,
    content_density_score,
    has_structural_anchor,
    'representative'::text AS selection_role
  FROM ranked
  WHERE bucket_rank=1
  ORDER BY ordinal
  LIMIT 8
), missing_structural_control AS (
  SELECT
    source_kind,
    source_id,
    ordinal,
    page_start,
    page_end,
    token_count,
    text_content,
    content_role,
    navigation_score,
    content_density_score,
    has_structural_anchor,
    'missing_structural_control'::text AS selection_role
  FROM source_candidates
  WHERE NOT has_structural_anchor
  ORDER BY ordinal
  LIMIT 1
), selected_windows AS (
  SELECT * FROM representatives
  UNION ALL
  SELECT *
  FROM missing_structural_control control
  WHERE $includeMissingStructuralControlSql
    AND NOT EXISTS (
      SELECT 1
      FROM representatives representative
      WHERE representative.source_id=control.source_id)
), windows AS (
  SELECT COALESCE(
    jsonb_agg(
      jsonb_build_object(
        'sourceKind', source_kind,
        'sourceId', source_id,
        'ordinal', ordinal,
        'pageStart', page_start,
        'pageEnd', page_end,
        'tokenCount', token_count,
        'contentRole', content_role,
        'navigationScore', navigation_score,
        'contentDensityScore', content_density_score,
        'hasStructuralAnchor', has_structural_anchor,
        'selectionRole', selection_role,
        'text', text_content)
      ORDER BY ordinal),
    '[]'::jsonb) AS value
  FROM selected_windows
)
SELECT jsonb_build_object(
  'document', jsonb_build_object(
    'docId', d.doc_id,
    'revisionId', d.revision_id,
    'docPath', d.doc_path,
    'docName', d.doc_name,
    'category', d.category,
    'pageCount', d.page_count,
    'indexedVersion', d.indexed_version,
    'sourceHash', d.source_hash),
  'selection', jsonb_build_object(
    'algorithm', CASE WHEN $includeMissingStructuralControlSql
      THEN 'capability_b_representative_v1_plus_missing_structural_control'
      ELSE 'capability_b_representative_v1'
    END,
    'windowCount', jsonb_array_length(windows.value)),
  'windows', windows.value)::text
FROM target_doc d
CROSS JOIN windows;
"@

$remote = "docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA"
$raw = $query | ssh $SshTarget $remote
if ($LASTEXITCODE -ne 0) {
    throw "Remote read-only export failed with exit code $LASTEXITCODE."
}

$json = ($raw -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($json)) {
    throw "No active indexed document found for '$DocPath'."
}

$parsed = $json | ConvertFrom-Json
if ([string]$parsed.document.docPath -ne $DocPath) {
    throw "Exported document path does not match the requested target."
}
$windowCount = @($parsed.windows).Count
if (-not $IncludeMissingStructuralControl -and $windowCount -ne 8) {
    throw "Expected exactly 8 representative windows."
}
if ($IncludeMissingStructuralControl -and ($windowCount -lt 8 -or $windowCount -gt 9)) {
    throw "Expected 8 representative windows and at most one appended structural control."
}
if ($IncludeMissingStructuralControl -and
    @($parsed.windows | Where-Object { -not [bool]$_.hasStructuralAnchor }).Count -lt 1) {
    throw "Expected at least one window without a structural anchor."
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resolvedOutput, $json, $utf8NoBom)

$sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
$shaHex = [Convert]::ToHexString($sha).ToLowerInvariant()
[pscustomobject]@{
    OutputPath = $resolvedOutput
    Sha256 = $shaHex
    DocPath = [string]$parsed.document.docPath
    IndexedVersion = [int]$parsed.document.indexedVersion
    RevisionId = [string]$parsed.document.revisionId
    Windows = @($parsed.windows).Count
    FirstOrdinal = [int]$parsed.windows[0].ordinal
    LastOrdinal = [int]$parsed.windows[-1].ordinal
} | ConvertTo-Json -Compress
