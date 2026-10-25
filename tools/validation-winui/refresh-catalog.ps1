#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ProofName = 'catalog-validation.json')
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
$uri = [Uri]$owner.backendUrl
if ($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or $uri.Port -ne $owner.backendPort) { throw 'Only the owned loopback backend may be refreshed.' }
if ([IO.Path]::GetFileName($ProofName) -ne $ProofName) { throw 'ProofName must be a filename.' }
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Evidence already exists; refusing overwrite.' }
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Api-Key' = $private.apiKey; 'X-Admin-Key' = $private.apiKey }
# Use the production snapshot builder, including its transaction and advisory lock.
$refresh = Invoke-RestMethod -Method Post -Uri ($owner.backendUrl + '/admin/catalog/snapshot/refresh') -Headers $headers
$categories = Invoke-RestMethod -Uri ($owner.backendUrl + '/catalog/categories?pageSize=100') -Headers $headers
$documents = Invoke-RestMethod -Uri ($owner.backendUrl + '/catalog/documents?pageSize=100') -Headers $headers
$stats = Invoke-RestMethod -Uri ($owner.backendUrl + '/documents/stats') -Headers $headers
$scopedValidation = Invoke-RestMethod -Uri ($owner.backendUrl + '/catalog/documents?categoryPath=Validation&pageSize=100') -Headers $headers
$scopedArchive = Invoke-RestMethod -Uri ($owner.backendUrl + '/catalog/documents?categoryPath=Archive&pageSize=100') -Headers $headers
$passed = @($categories.value).Count -eq 2 -and @($documents.value).Count -eq 2 -and
    $stats.totalDocuments -eq 2 -and @($scopedValidation.value).Count -eq 1 -and @($scopedArchive.value).Count -eq 1
@{ schemaVersion = 'saaia-winui-catalog-validation.v1'; performedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    reason = 'Document SQL seed alone does not build catalog projections when periodic CatalogSnapshot is disabled.';
    usedProductionSnapshotBuilder = $true; productCodeModified = $false; existingDatabaseModified = $false;
    refresh = $refresh; categories = $categories; documents = $documents; stats = $stats;
    validationScopedDocuments = $scopedValidation; archiveScopedDocuments = $scopedArchive; passed = $passed;
    uiValidation = $false } | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath $proofPath -Encoding utf8
if (-not $passed) { throw 'Synthetic catalog fixture is inconsistent; inspect catalog evidence.' }
Write-Output 'Owned catalog validated:2 categories,2 documents,1 document per category.'
