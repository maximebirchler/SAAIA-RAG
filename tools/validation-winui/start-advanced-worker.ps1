#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$BackendPort = 5137,
    [int]$FixturePort = 18089,
    [switch]$WireQualification,
    [ValidateSet('chat-completions','responses')][string]$NativeResearchApiProtocol = 'chat-completions',
    [string]$WireControlPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$ownerPath = Join-Path $output 'environment.json'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
foreach ($port in @($BackendPort,$FixturePort)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is occupied." }
}
$runtime = Join-Path $output 'runtime/advanced-worker'
if (Test-Path -LiteralPath $runtime) { throw 'Advanced-worker evidence already exists; refusing overwrite.' }
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$config = Get-Content -LiteralPath (Join-Path $output 'runtime/deployment.config.json') -Raw | ConvertFrom-Json -AsHashtable
$config.AdvancedAnalysis = @{ Provider = 'customer-server'; ProviderKey = 'winui-fixture-customer-server'; LlmLocation = 'internal';
    LlmBaseUrl = "http://127.0.0.1:$FixturePort/v1"; LlmModel = 'saaia-winui-fixture'; LlmApiKeyRef = ''; WorkerEnabled = $true;
    AllowExternalProviderContent = $false; AllowExternalProviderMetadata = $false; SemanticCriticEnabled = $false;
    AdaptiveResearchEnabled = $false; NativeResearchToolsEnabled = $false; PollDelayMilliseconds = 250; LeaseSeconds = 30;
    HeartbeatMilliseconds = 1000; MaximumAttempts = 1; LlmMaximumHttpAttempts = 1; LlmTimeoutSeconds = 60; RetentionDays = 1 }
if ($WireQualification) {
    $expectedControl = [IO.Path]::GetFullPath((Join-Path $output 'wire-control.json'))
    if (-not $WireControlPath -or [IO.Path]::GetFullPath($WireControlPath) -ine $expectedControl -or
        -not (Test-Path -LiteralPath $expectedControl -PathType Leaf)) { throw 'Wire qualification requires its owned control file.' }
    $config.AdvancedAnalysis.ProviderKey = 'loopback-wire-qualification'
    $config.AdvancedAnalysis.LlmModel = 'saaia-wire-fixture'
    $config.AdvancedAnalysis.AdaptiveResearchEnabled = $true
    $config.AdvancedAnalysis.NativeResearchToolsEnabled = $true
    $config.AdvancedAnalysis.NativeObservationReadEnabled = $true
    $config.AdvancedAnalysis.NativeResearchApiProtocol = $NativeResearchApiProtocol
    $config.AdvancedAnalysis.NativeResearchTopology = 'agent'
    $config.AdvancedAnalysis.NativeResearchMaximumHistoryCharacters = 65536
    $config.AdvancedAnalysis.ExternalMaximumCallsPerJob = 16
    $config.AdvancedAnalysis.MaximumToolCalls = 16
    $config.AdvancedAnalysis.MaximumAccumulatedEvidenceItems = 128
    $config.AdvancedAnalysis.MaximumEvidenceCharactersPerItem = 10000
    $config.AdvancedAnalysis.MaximumEvidenceCharactersTotal = 24000
    $config.AdvancedAnalysis.MaximumEvidencePromptCharacters = 14000
    $config.AdvancedAnalysis.MaximumToolElapsedMilliseconds = 120000
    $config.AdvancedAnalysis.DevelopmentTraceDirectory = Join-Path $output 'wire-application-traces'
    $config.AdvancedAnalysis.DevelopmentTraceStrict = $true
}
$configPath = Join-Path $runtime 'deployment.config.json'
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 12))
Copy-Item -LiteralPath (Join-Path $output 'runtime/Db') -Destination (Join-Path $runtime 'Db') -Recurse
$control = Join-Path $runtime 'fixture-control.json'
if ($WireQualification) { $control = $expectedControl }
else { [IO.File]::WriteAllText($control, '{"mode":"advanced","delaySeconds":2}') }
function Start-Owned([string]$Executable,[string[]]$Arguments,[string]$Name,[hashtable]$Environment=@{}) {
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$runtime
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    foreach($environmentName in @($info.Environment.Keys)) {
        if($environmentName -match 'OPENAI|RUNPOD|AZURE|SAAIA_LLM|SAAIA_ADVANCED|ConfigSignature|ConnectionString|QDRANT'){$info.Environment.Remove($environmentName)|Out-Null}
    }
    foreach($entry in $Environment.GetEnumerator()){$info.Environment[$entry.Key]=[string]$entry.Value}
    $process=[Diagnostics.Process]::Start($info)
    $ownedPort = if ($Name -eq 'advanced-fixture-http') { $FixturePort } else { $BackendPort }
    $owner.processes += @{name=$Name;pid=$process.Id;path=$Executable;port=$ownedPort;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o')}
    $owner|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ownerPath -Encoding utf8
    return $process
}
$fixtureScript = Join-Path $PSScriptRoot 'fixture_http.py'
$fixtureArguments = @('--port',"$FixturePort",'--control',$control,'--events',(Join-Path $output 'advanced-fixture-events.jsonl'))
if ($WireQualification) {
    $fixtureScript = Join-Path $repo 'tools/validation-wire/fixture_wire.py'
    $fixtureArguments += @('--capture',(Join-Path $output 'wire-captures'))
}
$fixture=Start-Owned (Get-Command python).Source (@($fixtureScript) + $fixtureArguments) 'advanced-fixture-http'
$backendDll=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
$spec=@{executable=(Get-Command dotnet).Source;arguments=@($backendDll,'--contentRoot',$runtime,'--urls',"http://127.0.0.1:$BackendPort");stdout=(Join-Path $output 'advanced-backend.stdout.log');stderr=(Join-Path $output 'advanced-backend.stderr.log')}
$specPath=Join-Path $runtime 'backend-process.json'
[IO.File]::WriteAllText($specPath,($spec|ConvertTo-Json))
$backend=Start-Owned (Get-Process -Id $PID).Path @('-NoProfile','-File',(Join-Path $PSScriptRoot 'run-helper.ps1'),'-Specification',$specPath) 'advanced-backend-runner' @{
    ASPNETCORE_ENVIRONMENT='Development';DOTNET_ENVIRONMENT='Development';ConfigSignature__ConfigPath=$configPath;
    ConfigSignature__SignaturePath=(Join-Path $runtime 'missing.sig');ConfigSignature__AllowUnsignedInDevelopment='true'
}
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
do {
    try{$ready=Invoke-RestMethod "http://127.0.0.1:$BackendPort/ready" -TimeoutSec 5;if($ready.ok){break}}catch{}
    if($backend.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline){throw 'Advanced worker readiness failed.'}
    Start-Sleep -Milliseconds 500
}while($true)
@{schemaVersion='saaia-winui-advanced-worker.v1';backendUrl="http://127.0.0.1:$BackendPort";fixtureUrl="http://127.0.0.1:$FixturePort";
    realWorkerEnabled=$true;realPostgresSharedOnlyWithinOwnedFixture=$true;providerKey=$config.AdvancedAnalysis.ProviderKey;
    providerIsDeterministicFixture=$true;externalProviderUsed=$false;terminalOutcome=$(if($WireQualification){'controlled_by_wire_scenario'}else{'insufficient_documentation'});
    semanticApproval=$false;wireQualification=[bool]$WireQualification;nativeResearchApiProtocol=$NativeResearchApiProtocol;ready=$ready}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $output 'advanced-worker.json') -Encoding utf8
Write-Output "Real worker with synthetic loopback provider ready on127.0.0.1:$BackendPort."
