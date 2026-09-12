param(
    [string]$RepositoryRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}

function Read-RequiredFile([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing required file: $RelativePath"
    }
    return [IO.File]::ReadAllText($path)
}

function Assert-Contains(
    [string]$Text,
    [string]$Expected,
    [string]$CheckName) {
    if (-not $Text.Contains($Expected)) {
        throw "Network exposure invariant failed: $CheckName"
    }
}

function Assert-NotContains(
    [string]$Text,
    [string]$Forbidden,
    [string]$CheckName) {
    if ($Text.Contains($Forbidden)) {
        throw "Network exposure invariant failed: $CheckName"
    }
}

function Assert-Count(
    [string]$Text,
    [string]$Pattern,
    [int]$Expected,
    [string]$CheckName) {
    $actual = ([regex]::Matches($Text, [regex]::Escape($Pattern))).Count
    if ($actual -ne $Expected) {
        throw "Network exposure invariant failed: $CheckName (expected $Expected, got $actual)"
    }
}

$production = Read-RequiredFile 'infra/docker-compose.prod.yml'
$observability = Read-RequiredFile 'infra/docker-compose.otel.yml'
$advanced = Read-RequiredFile 'infra/docker-compose.advanced-llm.yml'
$environmentExample = Read-RequiredFile 'infra/.env.example'

$backendBind = '${SAAIA_BIND_ADDR:-127.0.0.1}:${BACKEND_HOST_PORT:-5122}:5122'
$postgresBind = '${SAAIA_INTERNAL_BIND_ADDR:-127.0.0.1}:${POSTGRES_HOST_PORT:-5432}:5432'
$qdrantBind = '${SAAIA_INTERNAL_BIND_ADDR:-127.0.0.1}:${QDRANT_HOST_PORT:-6333}:6333'
$teiBind = '${SAAIA_INTERNAL_BIND_ADDR:-127.0.0.1}:${TEI_HOST_PORT:-8081}:80'

Assert-Contains $production $backendBind 'backend has its dedicated public bind'
Assert-Contains $production $postgresBind 'PostgreSQL stays on the internal bind'
Assert-Contains $production $qdrantBind 'Qdrant stays on the internal bind'
Assert-Contains $production $teiBind 'TEI stays on the internal bind'
Assert-Count $production '${SAAIA_BIND_ADDR' 1 'only the backend inherits the public bind'
Assert-Count $production '${SAAIA_INTERNAL_BIND_ADDR' 3 'exactly three data services inherit the internal bind'
Assert-NotContains $observability '${SAAIA_BIND_ADDR' 'OTLP does not inherit the backend bind'
Assert-Contains $observability '${SAAIA_OBSERVABILITY_BIND_ADDR:-127.0.0.1}:4317:4317' 'OTLP gRPC stays local by default'
Assert-Contains $observability '${SAAIA_OBSERVABILITY_BIND_ADDR:-127.0.0.1}:4318:4318' 'OTLP HTTP stays local by default'
Assert-Contains $advanced '127.0.0.1:${SAAIA_ADVANCED_LLM_HOST_PORT:-1235}:8080' 'advanced LLM diagnostic port stays local'
Assert-Contains $environmentExample 'SAAIA_INTERNAL_BIND_ADDR=127.0.0.1' 'internal bind is explicit in the environment example'
Assert-Contains $environmentExample 'SAAIA_OBSERVABILITY_BIND_ADDR=127.0.0.1' 'observability bind is explicit in the environment example'

[ordered]@{
    schemaVersion = 'saaia-prod-network-exposure-test.v1'
    repositoryRoot = $RepositoryRoot
    checks = 12
    backendLanExposureIsIndependent = $true
    dataServicesRemainLoopbackByDefault = $true
    observabilityRemainsLoopbackByDefault = $true
    advancedLlmDiagnosticRemainsLoopback = $true
    verdict = 'PASS_STATIC_NETWORK_EXPOSURE_INVARIANTS'
} | ConvertTo-Json
