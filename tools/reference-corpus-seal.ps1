Set-StrictMode -Version Latest

function Get-SaaiaReferenceSealSha256 {
    param([Parameter(Mandatory = $true)][string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
    }
    finally {
        $sha.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function New-SaaiaReferenceCorpusSeal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$CatalogJson,
        [Parameter(Mandatory = $true)][string]$IngestionJobsJson,
        [Parameter(Mandatory = $true)][string]$QdrantHealthJson
    )

    $catalog = $CatalogJson | ConvertFrom-Json
    $ingestionJobs = $IngestionJobsJson | ConvertFrom-Json
    $qdrant = $QdrantHealthJson | ConvertFrom-Json
    $jobs = @($ingestionJobs.items)
    $activeJobs = @($jobs | Where-Object { $_.status -in @("queued", "running", "paused") })

    if ($null -eq $catalog.snapshotId -or $null -eq $catalog.totals) {
        throw "Reference catalog snapshot response is incomplete."
    }
    if ($null -eq $ingestionJobs.items) {
        throw "Reference ingestion jobs response is incomplete."
    }
    if ($null -eq $qdrant.ok -or -not [bool]$qdrant.ok) {
        throw "Reference Qdrant health response is not healthy."
    }
    if ($null -eq $qdrant.pointsCount -or $null -eq $qdrant.vectorsCount) {
        throw "Reference Qdrant health response has no stable point/vector counts."
    }

    $catalogCategories = @($catalog.categories | ForEach-Object {
        [ordered]@{
            categoryRef = [string]$_.categoryRef
            categoryPath = [string]$_.categoryPath
            displayOrder = [int]$_.displayOrder
            canonicalName = [string]$_.canonicalName
            documentCount = [long]$_.documentCount
            aliases = @($_.aliases | ForEach-Object { [string]$_ } | Sort-Object)
        }
    })
    $catalogStableState = [ordered]@{
        documents = [long]$catalog.totals.documents
        categories = [long]$catalog.totals.categories
        categoryItems = $catalogCategories
    }
    $catalogHash = Get-SaaiaReferenceSealSha256 -Value (
        $catalogStableState | ConvertTo-Json -Depth 8 -Compress)
    $ingestionJobsHash = Get-SaaiaReferenceSealSha256 -Value $IngestionJobsJson
    $qdrantStableState = [ordered]@{
        collection = [string]$qdrant.collection
        status = [string]$qdrant.status
        httpStatus = [int]$qdrant.httpStatus
        vectorsCount = [long]$qdrant.vectorsCount
        pointsCount = [long]$qdrant.pointsCount
    }
    $qdrantHash = Get-SaaiaReferenceSealSha256 -Value (
        $qdrantStableState | ConvertTo-Json -Compress)
    $compositeHash = Get-SaaiaReferenceSealSha256 -Value (
        "$catalogHash`n$ingestionJobsHash`n$qdrantHash")

    return [pscustomobject][ordered]@{
        schemaVersion = "saaia-reference-corpus-seal-v1"
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        compositeSha256 = $compositeHash
        catalogStateSha256 = $catalogHash
        catalogSnapshotId = [string]$catalog.snapshotId
        catalogDocumentCount = [long]$catalog.totals.documents
        catalogCategoryCount = [long]$catalog.totals.categories
        ingestionJobsPayloadSha256 = $ingestionJobsHash
        ingestionJobsObserved = $jobs.Count
        activeIngestionJobs = $activeJobs.Count
        qdrantStateSha256 = $qdrantHash
        qdrantCollection = [string]$qdrantStableState.collection
        qdrantStatus = [string]$qdrantStableState.status
        qdrantHttpStatus = [int]$qdrantStableState.httpStatus
        qdrantVectorsCount = [long]$qdrantStableState.vectorsCount
        qdrantPointsCount = [long]$qdrantStableState.pointsCount
        volatileCatalogTimestampsExcluded = $true
        privateCatalogPayloadPersisted = $false
        privateIngestionPayloadPersisted = $false
    }
}

function Get-SaaiaReferenceCorpusSeal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ReferenceBackendUrl,
        [Parameter(Mandatory = $true)][string]$ServerApiKey,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15
    )

    $baseUrl = $ReferenceBackendUrl.TrimEnd('/')
    $catalogResponse = Invoke-WebRequest `
        -Uri "$baseUrl/catalog/snapshot" `
        -Headers @{ "X-Api-Key" = $ServerApiKey } `
        -UseBasicParsing `
        -TimeoutSec $TimeoutSeconds
    $jobsResponse = Invoke-WebRequest `
        -Uri "$baseUrl/ingestion/jobs?limit=2000" `
        -Headers @{ "X-Admin-Key" = $ServerApiKey } `
        -UseBasicParsing `
        -TimeoutSec $TimeoutSeconds
    $qdrantResponse = Invoke-WebRequest `
        -Uri "$baseUrl/admin/qdrant/health" `
        -Headers @{ "X-Admin-Key" = $ServerApiKey } `
        -UseBasicParsing `
        -TimeoutSec $TimeoutSeconds

    return New-SaaiaReferenceCorpusSeal `
        -CatalogJson $catalogResponse.Content `
        -IngestionJobsJson $jobsResponse.Content `
        -QdrantHealthJson $qdrantResponse.Content
}

function Assert-SaaiaReferenceCorpusUnchanged {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    if ([int]$Before.activeIngestionJobs -ne 0) {
        throw "Reference corpus seal reports active ingestion before the campaign."
    }
    if ([int]$After.activeIngestionJobs -ne 0) {
        throw "Reference corpus seal reports active ingestion after the campaign."
    }
    if ([string]$Before.compositeSha256 -ne [string]$After.compositeSha256) {
        throw "Reference corpus changed during the campaign (before=$($Before.compositeSha256), after=$($After.compositeSha256))."
    }
    return $true
}
