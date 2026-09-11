param(
    [Parameter(Mandatory = $true)]
    [string]$WindowInputPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$SshTarget = "saaia-server",
    [string]$PostgresContainer = "infra-postgres-1",
    [string]$PostgresUser = "saaia-admin",
    [string]$Database = "saaia"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedInput = [IO.Path]::GetFullPath($WindowInputPath)
$inputBytes = [IO.File]::ReadAllBytes($resolvedInput)
$inputJson = [Text.Encoding]::UTF8.GetString($inputBytes)
$input = $inputJson | ConvertFrom-Json
$windows = @($input.windows)
if ($windows.Count -lt 1 -or $windows.Count -gt 30) {
    throw "Expected between 1 and 30 frozen source windows."
}

$values = for ($index = 0; $index -lt $windows.Count; $index++) {
    $sourceId = [Guid]$windows[$index].sourceId
    "('$sourceId'::uuid,$index)"
}
$valuesSql = $values -join ",`n"
$query = @"
WITH requested(source_id, requested_order) AS (
  VALUES
  $valuesSql
)
SELECT jsonb_agg(
  jsonb_build_object(
    'sourceId', rc.retrieval_chunk_id,
    'requestedOrder', requested.requested_order,
    'chunkIndex', rc.chunk_index,
    'pageStart', rc.page_start,
    'pageEnd', rc.page_end,
    'sectionId', rc.section_id,
    'headingPath', rc.metadata->>'headingPath',
    'sectionTitle', rc.metadata->>'sectionTitle',
    'chunkType', rc.metadata->>'chunkType',
    'chunkComposition', rc.metadata->>'chunkComposition',
    'extractionQualitySignals', COALESCE(rc.metadata->'extractionQualitySignals', '[]'::jsonb),
    'text', rc.text_content)
  ORDER BY requested.requested_order)::text
FROM requested
JOIN retrieval_chunks rc ON rc.retrieval_chunk_id=requested.source_id;
"@

$remote = "docker exec -i -e PGCLIENTENCODING=UTF8 $PostgresContainer psql -U $PostgresUser -d $Database -v ON_ERROR_STOP=1 -tA"
$raw = $query | ssh $SshTarget $remote
if ($LASTEXITCODE -ne 0) {
    throw "Remote read-only anchor export failed with exit code $LASTEXITCODE."
}

$remoteJson = ($raw -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($remoteJson)) {
    throw "No retrieval chunk metadata returned."
}
$remoteWindows = @($remoteJson | ConvertFrom-Json)
if ($remoteWindows.Count -ne $windows.Count) {
    throw "Expected metadata for every frozen source window."
}

function Get-AnchorId(
    [Guid]$SourceId,
    [string]$Kind,
    [string]$Text
) {
    $separator = [char]0x1F
    $identity = "$SourceId$separator$Kind$separator$Text"
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return "sha256:" + [Convert]::ToHexString($hash).ToLowerInvariant()
}

$outputWindows = for ($index = 0; $index -lt $windows.Count; $index++) {
    $frozen = $windows[$index]
    $remoteWindow = $remoteWindows[$index]
    if ([Guid]$frozen.sourceId -ne [Guid]$remoteWindow.sourceId) {
        throw "Source identity mismatch at window $($index + 1)."
    }
    if ([string]$frozen.text -cne [string]$remoteWindow.text) {
        throw "Source text drift at window $($index + 1)."
    }
    if ([int]$frozen.pageStart -ne [int]$remoteWindow.pageStart -or
        [int]$frozen.pageEnd -ne [int]$remoteWindow.pageEnd) {
        throw "Source page drift at window $($index + 1)."
    }

    $candidates = [Collections.Generic.List[object]]::new()
    $headingPath = [string]$remoteWindow.headingPath
    if (-not [string]::IsNullOrWhiteSpace($headingPath)) {
        $levels = @($headingPath -split '>' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        for ($level = 0; $level -lt $levels.Count; $level++) {
            $candidates.Add([pscustomobject]@{
                kind = "heading_path_level_$level"
                sourceOrdinal = $level
                text = [string]$levels[$level]
            })
        }
    }

    $sectionTitle = [string]$remoteWindow.sectionTitle
    if (-not [string]::IsNullOrWhiteSpace($sectionTitle)) {
        $candidates.Add([pscustomobject]@{
            kind = "section_title"
            sourceOrdinal = 0
            text = $sectionTitle.Trim()
        })
    }

    $lines = @([string]$frozen.text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 8)
    for ($line = 0; $line -lt $lines.Count; $line++) {
        $candidates.Add([pscustomobject]@{
            kind = "source_line"
            sourceOrdinal = $line
            text = [string]$lines[$line]
        })
    }

    $seenText = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $anchors = [Collections.Generic.List[object]]::new()
    foreach ($candidate in $candidates) {
        if (-not $seenText.Add([string]$candidate.text)) {
            continue
        }
        $anchorId = Get-AnchorId `
            -SourceId ([Guid]$frozen.sourceId) `
            -Kind ([string]$candidate.kind) `
            -Text ([string]$candidate.text)
        $anchors.Add([pscustomobject]@{
            anchorId = $anchorId
            kind = [string]$candidate.kind
            sourceOrdinal = [int]$candidate.sourceOrdinal
            text = [string]$candidate.text
        })
    }
    if ($anchors.Count -eq 0) {
        throw "No anchor options created for window $($index + 1)."
    }
    $uniqueIds = @($anchors.anchorId | Select-Object -Unique)
    if ($uniqueIds.Count -ne $anchors.Count) {
        throw "Anchor ID collision at window $($index + 1)."
    }

    [pscustomobject]@{
        index = $index + 1
        sourceKind = [string]$frozen.sourceKind
        sourceId = [Guid]$frozen.sourceId
        ordinal = [int]$frozen.ordinal
        pageStart = [int]$frozen.pageStart
        pageEnd = [int]$frozen.pageEnd
        tokenCount = [int]$frozen.tokenCount
        contentRole = [string]$frozen.contentRole
        headingPath = if ([string]::IsNullOrWhiteSpace($headingPath)) { $null } else { $headingPath }
        sectionTitle = if ([string]::IsNullOrWhiteSpace($sectionTitle)) { $null } else { $sectionTitle }
        sectionId = if ($null -eq $remoteWindow.sectionId) { $null } else { [Guid]$remoteWindow.sectionId }
        chunkType = [string]$remoteWindow.chunkType
        chunkComposition = [string]$remoteWindow.chunkComposition
        extractionQualitySignals = @($remoteWindow.extractionQualitySignals)
        text = [string]$frozen.text
        anchors = @($anchors)
    }
}

$inputHash = [Security.Cryptography.SHA256]::HashData($inputBytes)
$result = [pscustomobject]@{
    document = $input.document
    sourceInput = [pscustomobject]@{
        path = $resolvedInput
        sha256 = [Convert]::ToHexString($inputHash).ToLowerInvariant()
    }
    anchorScheme = [pscustomobject]@{
        version = "source_anchor_selection_v1"
        identity = "sha256(sourceId U+001F kind U+001F exactText)"
        canonicalOrder = @("heading_path_levels", "section_title", "first_8_non_empty_source_lines")
    }
    windows = @($outputWindows)
}

$json = $result | ConvertTo-Json -Depth 20
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resolvedOutput, $json, $utf8NoBom)

$outputBytes = [Text.Encoding]::UTF8.GetBytes($json)
$outputHash = [Security.Cryptography.SHA256]::HashData($outputBytes)
[pscustomobject]@{
    OutputPath = $resolvedOutput
    Sha256 = [Convert]::ToHexString($outputHash).ToLowerInvariant()
    SourceInputSha256 = [Convert]::ToHexString($inputHash).ToLowerInvariant()
    Windows = @($outputWindows).Count
    Anchors = @($outputWindows | ForEach-Object { $_.anchors }).Count
    MissingHeadingPaths = @($outputWindows | Where-Object { $null -eq $_.headingPath }).Count
} | ConvertTo-Json -Compress
