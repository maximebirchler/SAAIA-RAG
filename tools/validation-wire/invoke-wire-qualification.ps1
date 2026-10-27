#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SourceCapturePath,
    [Parameter(Mandatory)][string]$ImporterDll,
    [ValidateSet('chat-completions','responses')][string]$Protocol = 'chat-completions',
    [ValidateSet('positive','native-raw-sha-correction','source-hash-forged','invented-citation','output-limit')][string]$ScenarioVariant = 'positive',
    [ValidateRange(1024,65535)][int]$BackendPort = 22401,
    [ValidateRange(1024,65535)][int]$DependencyPort = 22402,
    [ValidateRange(1024,65535)][int]$WorkerPort = 22403,
    [ValidateRange(1024,65535)][int]$ModelPort = 22404,
    [string]$PythonExe = '',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-WireCompletionMessage([string]$Variant, [int]$ProviderPosts) {
    if ($Variant -in @('positive','native-raw-sha-correction')) {
        return 'Exact documentary bytes reached the loopback model endpoint; API/store/tool parity passed and owned runtime stopped.'
    }
    return "Expected refusal validated; $ProviderPosts provider POST(s), no persisted answer; owned runtime stopped."
}
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$capture = [IO.Path]::GetFullPath($SourceCapturePath)
$importer = [IO.Path]::GetFullPath($ImporterDll)
$expectedCapture = '83875b876e35010e9309b7648794c342734d31b857af2396c15ecc1e20f61219'
if ((Get-FileHash -LiteralPath $capture -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedCapture) { throw 'Only the pinned real source27 publication capture is admitted.' }
if (-not (Test-Path -LiteralPath $importer -PathType Leaf)) { throw 'Build the dedicated import tool first; this script never builds product code.' }
$backendDll = Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
$backendSha = (Get-FileHash -LiteralPath $backendDll -Algorithm SHA256).Hash.ToLowerInvariant()
$importBackendSha = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $importer) 'SAAIA.Backend.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($backendSha -cne $importBackendSha) { throw 'Importer and runtime must use the exact same backend assembly.' }
if (@(@($BackendPort,$DependencyPort,$WorkerPort,$ModelPort) | Select-Object -Unique).Count -ne 4 -or
    5432 -in @($BackendPort,$DependencyPort,$WorkerPort,$ModelPort)) { throw 'Four distinct unprivileged ports, separate from PostgreSQL5432, are required.' }
if (-not $PythonExe) { $PythonExe = (Get-Command python).Source }
$dotnet = (Get-Command dotnet).Source
$plan = [ordered]@{ schemaVersion='saaia-wire-qualification-plan-v1';executeRequested=[bool]$Execute;
    protocol=$Protocol;scenarioVariant=$ScenarioVariant;outputDirectory=$output;sourceCaptureSha256=$expectedCapture;
    importerSha256=(Get-FileHash -LiteralPath $importer).Hash.ToLowerInvariant();
    backendAssemblySha256=$backendSha;
    scope='Two exact captured publications; real backend/API/worker/store/tools; loopback HTTP fake provider';
    source27CanonicalRowsReplayed=$true;newIngestionExecuted=$false;externalCorpusTransmission=$false;paidProvider=$false;
    ports=@($BackendPort,$DependencyPort,$WorkerPort,$ModelPort);productStatus='TESTE_NON_APPROUVE' }
if (-not $Execute) { $plan | ConvertTo-Json -Depth 6; return }
if (Test-Path -LiteralPath $output) { throw 'Choose a new evidence directory; no old proof is replaced.' }
foreach ($port in $plan.ports) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Qualification port $port is occupied." }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.File]::WriteAllText((Join-Path $output 'plan.json'),($plan | ConvertTo-Json -Depth 6))
$cases = Join-Path $output 'cases.json'
& $PythonExe -X utf8 -B (Join-Path $PSScriptRoot 'prepare_cases.py') --capture $capture --output $cases *> (Join-Path $output 'prepare-cases.log')
if ($LASTEXITCODE -ne 0) { throw 'Case preparation failed before any runtime was started.' }
$runtime = Join-Path $output 'owned-runtime'
$failure = $null
$cleanupFailure = $null
$verified = $false
function Invoke-LegacyFixtureScript([string]$Script, [hashtable]$Parameters) {
    # Existing fixture scripts predate strict property access. Keep their
    # historical semantics scoped to this invocation (e.g. optional instanceName).
    Set-StrictMode -Off
    & $Script @Parameters
}
try {
    Invoke-LegacyFixtureScript (Join-Path $repo 'tools/validation-winui/start-environment.ps1') @{
        OutputDirectory=$runtime;BackendPort=$BackendPort;FixturePort=$DependencyPort;NoBuild=$true
    } *> (Join-Path $output 'start-environment.log')
    & $dotnet $importer --owner (Join-Path $runtime 'environment.json') --capture $capture --capture-sha256 $expectedCapture --output (Join-Path $runtime 'corpus-replay.json') *> (Join-Path $output 'corpus-import.log')
    if ($LASTEXITCODE -ne 0) { throw 'Exact publication import failed.' }
    $first = (Get-Content -LiteralPath $cases -Raw | ConvertFrom-Json).cases[0]
    [IO.File]::WriteAllText((Join-Path $runtime 'wire-control.json'),(@{caseId=$first.caseId;scenario=$first} | ConvertTo-Json -Depth 15))
    Invoke-LegacyFixtureScript (Join-Path $repo 'tools/validation-winui/start-advanced-worker.ps1') @{
        OutputDirectory=$runtime;BackendPort=$WorkerPort;FixturePort=$ModelPort;WireQualification=$true;
        NativeResearchApiProtocol=$Protocol;WireControlPath=(Join-Path $runtime 'wire-control.json')
    } *> (Join-Path $output 'start-worker.log')
    & $PythonExe -X utf8 -B (Join-Path $PSScriptRoot 'run_cases.py') --runtime $runtime --cases $cases --dotnet $dotnet --importer $importer --variant $ScenarioVariant *> (Join-Path $output 'api-run.log')
    if ($LASTEXITCODE -ne 0) { throw 'Wire API run failed; preserving captures and job states.' }
    $verifier = if($ScenarioVariant -in @('positive','native-raw-sha-correction')){'verify_wire.py'}else{'verify_negative_wire.py'}
    & $PythonExe -X utf8 -B (Join-Path $PSScriptRoot $verifier) --runtime $runtime --cases $cases --capture $capture --output (Join-Path $output 'verification.json') *> (Join-Path $output 'verification.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent wire verification failed.' }
    $verified = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    if (Test-Path -LiteralPath (Join-Path $runtime 'environment.json')) {
        try {
            Invoke-LegacyFixtureScript (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') @{
                OutputDirectory=$runtime;PlanOutputPath=(Join-Path $output 'stop-plan.json');Confirm=$false
            } *> (Join-Path $output 'stop-environment.log')
        } catch { $cleanupFailure = $_.Exception.Message }
    }
    [IO.File]::WriteAllText((Join-Path $output 'execution-result.json'),(@{
        schemaVersion='saaia-wire-execution-result-v1';protocol=$Protocol;finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        wireVerified=$verified;failure=$failure;cleanupFailure=$cleanupFailure;
        passed=($verified -and -not $failure -and -not $cleanupFailure);paidProviderCalls=0;
        productStatus='TESTE_NON_APPROUVE' } | ConvertTo-Json -Depth 5))
}
if ($failure) { throw $failure }
if ($cleanupFailure) { throw $cleanupFailure }
$verification = Get-Content -LiteralPath (Join-Path $output 'verification.json') -Raw | ConvertFrom-Json
$providerPosts = if ($ScenarioVariant -in @('positive','native-raw-sha-correction')) { 0 } else {
    ($verification.cases | Measure-Object -Property providerPosts -Sum).Sum
}
Write-Output (Get-WireCompletionMessage -Variant $ScenarioVariant -ProviderPosts $providerPosts)
