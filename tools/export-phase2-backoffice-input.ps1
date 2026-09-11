param(
    [Parameter(Mandatory = $true)]
    [string]$DocPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Escape-SqlLiteral([string]$Value) {
    return $Value.Replace("'", "''")
}

$escapedDocPath = Escape-SqlLiteral $DocPath
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
    r.revision_id,
    p.document_profile_id,
    p.profile_version,
    COALESCE(p.language, 'und') AS language,
    p.summary_text,
    p.keywords,
    p.entities,
    p.topics,
    p.hypothetical_questions,
    p.limits,
    p.search_text,
    p.token_count
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
  JOIN LATERAL (
    SELECT p.*
    FROM document_profiles p
    WHERE p.tenant_id=d.tenant_id
      AND p.doc_id=d.doc_id
      AND p.revision_id=r.revision_id
      AND p.profile_version LIKE 'deterministic_%'
    ORDER BY
      CASE WHEN p.profile_version LIKE 'deterministic_canonical_%' THEN 0 ELSE 1 END,
      p.updated_at DESC NULLS LAST,
      p.profile_version DESC
    LIMIT 1
  ) p ON true
  WHERE d.doc_path='$escapedDocPath'
    AND d.status='indexed'
    AND d.indexed_version>0
), cards AS (
  SELECT COALESCE(
    jsonb_agg(
      jsonb_build_object(
        'contentCardId', c.content_card_id::text,
        'title', c.title,
        'pageStart', c.page_start,
        'pageEnd', c.page_end,
        'kind', c.kind,
        'signals', to_jsonb(c.signals),
        'evidence', CASE
          WHEN c.metadata ? 'evidence' THEN c.metadata->'evidence'
          ELSE NULL
        END)
      ORDER BY c.card_index),
    '[]'::jsonb) AS value
  FROM target_doc d
  JOIN document_profile_content_cards c
    ON c.tenant_id=d.tenant_id
   AND c.document_profile_id=d.document_profile_id
), sections AS (
  SELECT COALESCE(jsonb_agg(title ORDER BY ordinal), '[]'::jsonb) AS value
  FROM (
    SELECT ds.title, ds.ordinal
    FROM target_doc d
    JOIN document_sections ds ON ds.revision_id=d.revision_id
    WHERE NULLIF(BTRIM(ds.title), '') IS NOT NULL
    ORDER BY ds.ordinal
    LIMIT 12
  ) selected
), source_candidates AS (
  SELECT
    rc.text_content,
    rc.chunk_index AS ordinal,
    0 AS source_rank,
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
    END AS content_density_score
  FROM target_doc d
  JOIN retrieval_chunks rc ON rc.revision_id=d.revision_id
  WHERE length(trim(rc.text_content))>0
    AND COALESCE(rc.metadata->>'contentRole', 'content')<>'navigation'
    AND COALESCE(rc.metadata->>'chunkType', '')<>'navigation_index_v1'
  UNION ALL
  SELECT
    du.text_content,
    du.ordinal,
    1,
    0,
    0::double precision,
    0::double precision
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
    text_content,
    ordinal,
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
), excerpts AS (
  SELECT COALESCE(jsonb_agg(text_content ORDER BY ordinal), '[]'::jsonb) AS value
  FROM (
    SELECT text_content, ordinal
    FROM ranked
    WHERE bucket_rank=1
    ORDER BY ordinal
    LIMIT 8
  ) selected
)
SELECT jsonb_build_object(
  'document', jsonb_build_object(
    'docId', d.doc_id,
    'docPath', d.doc_path,
    'docName', d.doc_name,
    'category', d.category,
    'pageCount', d.page_count,
    'indexedVersion', d.indexed_version,
    'sourceHash', d.source_hash),
  'baseline', jsonb_build_object(
    'revisionId', d.revision_id,
    'profileVersion', d.profile_version,
    'language', d.language,
    'summaryText', d.summary_text,
    'keywords', to_jsonb(d.keywords),
    'entities', to_jsonb(d.entities),
    'topics', to_jsonb(d.topics),
    'hypotheticalQuestions', to_jsonb(d.hypothetical_questions),
    'limits', to_jsonb(d.limits),
    'searchText', d.search_text,
    'tokenCount', d.token_count,
    'contentCards', cards.value),
  'sectionTitles', sections.value,
  'excerpts', excerpts.value)::text
FROM target_doc d
CROSS JOIN cards
CROSS JOIN sections
CROSS JOIN excerpts;
"@

$remote = "docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA"
$raw = $query | ssh $SshTarget $remote
if ($LASTEXITCODE -ne 0) {
    throw "Remote read-only export failed with exit code $LASTEXITCODE."
}

$json = ($raw -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($json)) {
    throw "No active deterministic profile found for '$DocPath'."
}

$parsed = $json | ConvertFrom-Json
if ([string]$parsed.document.docPath -ne $DocPath) {
    throw "Exported document path does not match the requested target."
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
    ProfileVersion = [string]$parsed.baseline.profileVersion
    ContentCards = @($parsed.baseline.contentCards).Count
    SectionTitles = @($parsed.sectionTitles).Count
    Excerpts = @($parsed.excerpts).Count
} | ConvertTo-Json -Compress
