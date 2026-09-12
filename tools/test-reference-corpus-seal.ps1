[CmdletBinding()]
param([string]$ArtifactDirectory = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "reference-corpus-seal.ps1")
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repositoryRoot (
        "artifacts\reference-corpus-seal-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$catalogJson = '{"snapshotId":"snapshot-a","catalogVersion":"2026-09-12T06:00:00Z","categories":[{"categoryRef":"CAT-001","categoryPath":"private/path","displayOrder":1,"canonicalName":"private-name","documentCount":42,"lastUpdatedUtc":"2026-09-12T06:00:00Z","aliases":["private-alias"]}],"totals":{"documents":42,"categories":1}}'
$jobsJson = '{"items":[{"jobId":"11111111-1111-1111-1111-111111111111","docPath":"private/document.pdf","status":"done","finishedAt":"2026-09-12T06:01:00Z"}],"limit":2000,"offset":0}'
$qdrantJson = '{"ok":true,"baseUrl":"private-host","collection":"knowledge_base","status":"green","httpStatus":200,"latencyMs":91,"vectorsCount":100,"pointsCount":100,"segmentsCount":7,"environment":"Production"}'

$before = New-SaaiaReferenceCorpusSeal `
    -CatalogJson $catalogJson `
    -IngestionJobsJson $jobsJson `
    -QdrantHealthJson $qdrantJson
$sameWithDifferentLatency = New-SaaiaReferenceCorpusSeal `
    -CatalogJson ($catalogJson.Replace('snapshot-a', 'snapshot-b').Replace('2026-09-12T06:00:00Z', '2026-09-12T07:00:00Z')) `
    -IngestionJobsJson $jobsJson `
    -QdrantHealthJson ($qdrantJson.Replace('"latencyMs":91', '"latencyMs":999'))

$checks = @()
$checks += [ordered]@{
    name = "stable_payloads_match"
    pass = [bool](Assert-SaaiaReferenceCorpusUnchanged -Before $before -After $sameWithDifferentLatency)
}
$checks += [ordered]@{
    name = "volatile_catalog_refresh_and_qdrant_latency_ignored"
    pass = $before.compositeSha256 -eq $sameWithDifferentLatency.compositeSha256
}
$serializedSeal = $before | ConvertTo-Json -Depth 8
$checks += [ordered]@{
    name = "private_values_not_persisted"
    pass = $serializedSeal -notmatch "private/path|private-name|private-alias|private/document|private-host"
}

$changedCatalog = New-SaaiaReferenceCorpusSeal `
    -CatalogJson ($catalogJson.Replace('"documents":42', '"documents":43')) `
    -IngestionJobsJson $jobsJson `
    -QdrantHealthJson $qdrantJson
$changedJobs = New-SaaiaReferenceCorpusSeal `
    -CatalogJson $catalogJson `
    -IngestionJobsJson ($jobsJson.Replace('"status":"done"', '"status":"failed"')) `
    -QdrantHealthJson $qdrantJson
$changedQdrant = New-SaaiaReferenceCorpusSeal `
    -CatalogJson $catalogJson `
    -IngestionJobsJson $jobsJson `
    -QdrantHealthJson ($qdrantJson.Replace('"pointsCount":100', '"pointsCount":101'))

foreach ($change in @(
    [ordered]@{ name = "catalog_change_rejected"; seal = $changedCatalog },
    [ordered]@{ name = "ingestion_history_change_rejected"; seal = $changedJobs },
    [ordered]@{ name = "qdrant_count_change_rejected"; seal = $changedQdrant }
)) {
    $message = $null
    try { Assert-SaaiaReferenceCorpusUnchanged -Before $before -After $change.seal | Out-Null }
    catch { $message = $_.Exception.Message }
    $checks += [ordered]@{
        name = $change.name
        pass = $message -match "Reference corpus changed during the campaign"
    }
}

$activeJobs = New-SaaiaReferenceCorpusSeal `
    -CatalogJson $catalogJson `
    -IngestionJobsJson ($jobsJson.Replace('"status":"done"', '"status":"running"')) `
    -QdrantHealthJson $qdrantJson
$activeMessage = $null
try { Assert-SaaiaReferenceCorpusUnchanged -Before $activeJobs -After $activeJobs | Out-Null }
catch { $activeMessage = $_.Exception.Message }
$checks += [ordered]@{
    name = "active_ingestion_rejected"
    pass = $activeMessage -match "active ingestion before"
}

$failures = @($checks | Where-Object { -not $_.pass })
$assessment = [ordered]@{
    schemaVersion = "saaia-reference-corpus-seal-assessment-v1"
    assessedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    checks = $checks
    total = $checks.Count
    passed = $checks.Count - $failures.Count
    failed = $failures.Count
    externalProviderCalls = 0
    privatePayloadPersisted = $false
    productStatus = "TESTE_NON_APPROUVE"
}
$assessmentPath = Join-Path $ArtifactDirectory "assessment.v1.json"
$assessment | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $assessmentPath -Encoding utf8
Write-Output "Reference corpus seal assessment: $assessmentPath"
Write-Output "Passed: $($assessment.passed)/$($assessment.total)"
if ($failures.Count -gt 0) { exit 2 }
