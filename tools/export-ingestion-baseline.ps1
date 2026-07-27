param(
    [string]$DefinitionPath = "config/ingestion-benchmark-corpus.v1.json",
    [string]$OutputPath = "artifacts/ingestion-baseline/current-corpus.json",
    [string]$WaveId = "primary",
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

function Format-SqlUnicodeLiteral([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append("U&'")
    foreach ($ch in $Value.ToCharArray()) {
        $code = [int][char]$ch
        if ($ch -eq "'") {
            [void]$builder.Append("''")
        }
        elseif ($ch -eq "\") {
            [void]$builder.Append("\005C")
        }
        elseif ($code -ge 32 -and $code -le 126) {
            [void]$builder.Append($ch)
        }
        else {
            [void]$builder.Append("\" + $code.ToString("X4", [System.Globalization.CultureInfo]::InvariantCulture))
        }
    }
    [void]$builder.Append("'")
    return $builder.ToString()
}

function Invoke-PostgresQuery([string]$Query) {
    $remote = "docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F '|'"
    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        $output = $Query | docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA -F "|"
    }
    else {
        $output = $Query | ssh $SshTarget $remote
    }

    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL baseline query failed with exit code $LASTEXITCODE."
    }

    return @($output)
}

function Get-OpaqueCaseId([string]$CaseFamily, [string]$SourceSha256) {
    $bytes = [Text.Encoding]::UTF8.GetBytes("$CaseFamily`n$SourceSha256")
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($bytes)
    }
    finally {
        $sha256.Dispose()
    }
    $hex = -join ($hash | ForEach-Object { $_.ToString("x2") })
    return "case_" + $hex.Substring(0, 20)
}

$resolvedDefinitionPath = [IO.Path]::GetFullPath($DefinitionPath)
if (-not (Test-Path -LiteralPath $resolvedDefinitionPath -PathType Leaf)) {
    throw "Corpus definition not found: $resolvedDefinitionPath"
}

$definition = Get-Content -LiteralPath $resolvedDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$definition.schemaVersion -ne "ingestion_benchmark_corpus_v1") {
    throw "Unsupported corpus definition schema '$($definition.schemaVersion)'."
}

$wave = @($definition.waves | Where-Object { [string]$_.waveId -eq $WaveId }) | Select-Object -First 1
if ($null -eq $wave) {
    throw "Wave '$WaveId' was not found in $resolvedDefinitionPath."
}

$cases = New-Object System.Collections.Generic.List[object]
foreach ($selector in @($wave.selectors)) {
    $category = [string]$selector.category
    $caseFamily = [string]$selector.caseFamily
    if ([string]::IsNullOrWhiteSpace($category) -or [string]::IsNullOrWhiteSpace($caseFamily)) {
        throw "Every selector requires non-empty category and caseFamily values."
    }

    $categoryLiteral = Format-SqlUnicodeLiteral $category
    $sql = @"
WITH current_revision AS (
  SELECT r.revision_id,
         encode(r.source_hash, 'hex') AS source_sha256,
         COALESCE(r.source_size, 0) AS source_size
  FROM documents d
  JOIN document_revisions r
    ON r.tenant_id=d.tenant_id
   AND r.doc_id=d.doc_id
   AND r.indexed_version=d.indexed_version
  WHERE d.status='indexed'
    AND d.indexed_version > 0
    AND lower(d.category)=lower($categoryLiteral)
),
page_stats AS (
  SELECT p.revision_id,
         count(*)::int AS page_count,
         COALESCE(sum(p.char_count), 0)::bigint AS total_char_count,
         count(*) FILTER (WHERE p.char_count=0)::int AS empty_page_count,
         count(*) FILTER (
           WHERE COALESCE((p.metadata->>'imageCount')::int, 0) > 0
         )::int AS image_page_count,
         count(*) FILTER (
           WHERE NULLIF(p.metadata->>'imageOcrStatus', '') IS NOT NULL
         )::int AS ocr_observed_page_count,
         count(*) FILTER (
           WHERE COALESCE(
             (p.metadata #>> '{extractionQuality,manualReviewRecommended}')::boolean,
             false)
         )::int AS manual_review_page_count
  FROM document_page_index p
  JOIN current_revision cr ON cr.revision_id=p.revision_id
  GROUP BY p.revision_id
),
quality_counts AS (
  SELECT p.revision_id,
         COALESCE(NULLIF(p.metadata #>> '{extractionQuality,qualityStatus}', ''), 'unknown') AS status,
         count(*)::int AS value
  FROM document_page_index p
  JOIN current_revision cr ON cr.revision_id=p.revision_id
  GROUP BY p.revision_id, status
),
quality_stats AS (
  SELECT revision_id, jsonb_object_agg(status, value ORDER BY status) AS histogram
  FROM quality_counts
  GROUP BY revision_id
),
ocr_counts AS (
  SELECT p.revision_id,
         COALESCE(NULLIF(p.metadata->>'imageOcrStatus', ''), 'not_observed') AS status,
         count(*)::int AS value
  FROM document_page_index p
  JOIN current_revision cr ON cr.revision_id=p.revision_id
  GROUP BY p.revision_id, status
),
ocr_stats AS (
  SELECT revision_id, jsonb_object_agg(status, value ORDER BY status) AS histogram
  FROM ocr_counts
  GROUP BY revision_id
)
SELECT cr.source_sha256,
       cr.source_size::text,
       COALESCE(ps.page_count, 0)::text,
       COALESCE(ps.total_char_count, 0)::text,
       COALESCE(ps.empty_page_count, 0)::text,
       COALESCE(ps.image_page_count, 0)::text,
       COALESCE(ps.ocr_observed_page_count, 0)::text,
       COALESCE(ps.manual_review_page_count, 0)::text,
       COALESCE(qs.histogram, '{}'::jsonb)::text,
       COALESCE(os.histogram, '{}'::jsonb)::text
FROM current_revision cr
LEFT JOIN page_stats ps ON ps.revision_id=cr.revision_id
LEFT JOIN quality_stats qs ON qs.revision_id=cr.revision_id
LEFT JOIN ocr_stats os ON os.revision_id=cr.revision_id
ORDER BY cr.source_sha256;
"@

    foreach ($line in @(Invoke-PostgresQuery $sql)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = $line -split '\|', 10
        if ($parts.Count -ne 10 -or $parts[0] -notmatch '^[0-9a-f]{64}$') {
            throw "Unexpected baseline row for case family '$caseFamily'."
        }

        $cases.Add([pscustomobject][ordered]@{
            caseId = Get-OpaqueCaseId $caseFamily $parts[0]
            caseFamily = $caseFamily
            sourceSha256 = $parts[0]
            sourceSizeBytes = [long]$parts[1]
            pageCount = [int]$parts[2]
            totalCharacterCount = [long]$parts[3]
            emptyPageCount = [int]$parts[4]
            imagePageCount = [int]$parts[5]
            ocrObservedPageCount = [int]$parts[6]
            manualReviewPageCount = [int]$parts[7]
            qualityStatusHistogram = $parts[8] | ConvertFrom-Json
            imageOcrStatusHistogram = $parts[9] | ConvertFrom-Json
        })
    }
}

$orderedCases = @($cases | Sort-Object caseFamily, caseId)
$familySummaries = @(
    $orderedCases |
        Group-Object caseFamily |
        ForEach-Object {
            $group = @($_.Group)
            [ordered]@{
                caseFamily = $_.Name
                documentCount = $group.Count
                pageCount = [long](($group | Measure-Object pageCount -Sum).Sum)
                sourceSizeBytes = [long](($group | Measure-Object sourceSizeBytes -Sum).Sum)
                emptyPageCount = [long](($group | Measure-Object emptyPageCount -Sum).Sum)
                imagePageCount = [long](($group | Measure-Object imagePageCount -Sum).Sum)
                manualReviewPageCount = [long](($group | Measure-Object manualReviewPageCount -Sum).Sum)
            }
        }
)

$result = [ordered]@{
    schemaVersion = "ingestion_baseline_inventory_v1"
    definitionSchemaVersion = [string]$definition.schemaVersion
    waveId = $WaveId
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    privacy = $definition.privacy
    summary = [ordered]@{
        documentCount = $orderedCases.Count
        pageCount = [long](($orderedCases | Measure-Object pageCount -Sum).Sum)
        sourceSizeBytes = [long](($orderedCases | Measure-Object sourceSizeBytes -Sum).Sum)
        emptyPageCount = [long](($orderedCases | Measure-Object emptyPageCount -Sum).Sum)
        imagePageCount = [long](($orderedCases | Measure-Object imagePageCount -Sum).Sum)
        manualReviewPageCount = [long](($orderedCases | Measure-Object manualReviewPageCount -Sum).Sum)
    }
    families = $familySummaries
    cases = $orderedCases
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$json = $result | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($resolvedOutputPath, $json + [Environment]::NewLine, $utf8NoBom)

Write-Host "Baseline inventory exported to $resolvedOutputPath"
Write-Host "Documents: $($result.summary.documentCount); pages: $($result.summary.pageCount); bytes: $($result.summary.sourceSizeBytes)"
