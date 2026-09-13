param(
    [string[]]$CaseIds = @('BH6-001','BH6-007','BH6-011','BH6-013','BH6-014','BH6-024'),
    [string]$RunName = 'local-diagnostics-r1',
    [string]$ArtifactRoot = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRepo = Split-Path -Parent $PSScriptRoot
if([string]::IsNullOrWhiteSpace($ArtifactRoot)){$ArtifactRoot=Join-Path $taskRepo 'artifacts/reprise-pc-20260908/a781-holdout-diagnosis-20260913'}
if($RunName -notmatch '^[a-z0-9-]+$'){throw 'Invalid run name'}
$taskRun = Join-Path $ArtifactRoot $RunName
if (Test-Path -LiteralPath $taskRun) { throw 'Existing diagnostic results' }
if (@(Get-NetTCPConnection -LocalPort 1234 -State Listen -ErrorAction SilentlyContinue).Count) { throw 'Port occupied' }
if (@(& git -C $taskRepo status --porcelain).Count) { throw 'Candidate must be clean' }
$taskProfile = Get-Content -LiteralPath (Join-Path $taskRepo 'artifacts/reprise-pc-20260908/a658-preparation/runtime-profile-frozen.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $taskProfile.runtimePath).Hash -ne $taskProfile.runtimeSha256) { throw 'Runtime drift' }
if ((Get-FileHash -LiteralPath $taskProfile.modelPath).Hash -ne $taskProfile.modelSha256) { throw 'Model drift' }
$taskKey = $null
foreach ($taskLine in Get-Content -LiteralPath 'C:\Users\maxim\NextCloud\Maxime\Ecommerce\SAAIA\infra\.env') {
    if ($taskLine -match '^\s*SAAIA_BOOTSTRAP_API_KEY\s*=\s*(.*)$') { $taskKey=$Matches[1].Trim().Trim('"').Trim("'"); break }
}
if ([string]::IsNullOrWhiteSpace($taskKey)) { throw 'Missing server credential' }
if ((Invoke-WebRequest -Uri 'http://saaia-server:5122/health' -TimeoutSec 10).StatusCode -ne 200) { throw 'Backend unavailable' }
$null = New-Item -ItemType Directory -Path $taskRun
$taskPayload = Get-Content -LiteralPath 'C:\Users\maxim\AppData\Local\SAAIA\blind-holdout-diagnostics\a780-20260912-221601\content\private-payload.json' -Raw | ConvertFrom-Json
$taskIds = @($CaseIds)
if($taskIds.Count -eq 0 -or @($taskIds | Select-Object -Unique).Count -ne $taskIds.Count){throw 'Missing or duplicate diagnostic IDs'}
$taskCases = foreach ($taskId in $taskIds) {
    $taskCase = $taskPayload.cases | Where-Object id -eq $taskId
    if($null -eq $taskCase){throw 'Unknown consumed case ID'}
    [ordered]@{ id=$taskId; language=$taskCase.language; axis='consumed_holdout_diagnostic'; difficulty='diagnostic'; theme=$taskCase.theme; corpusTarget=''; question=$taskCase.question; expectedAnswerKind='diagnostic'; validationPoints='Diagnostic rerun only; not blind acceptance.' }
}
$taskBank = Join-Path $taskRun 'diagnostic-bank.json'
[ordered]@{version='consumed-bh6-local-diagnostic-v1';validationCases=@($taskCases)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskBank -Encoding utf8
$taskLedger = Join-Path $env:LOCALAPPDATA 'SAAIA\llm-dev\openai-terra-usage.jsonl'
$taskLedgerBefore=(Get-FileHash -LiteralPath $taskLedger).Hash
$taskCandidate=(& git -C $taskRepo rev-parse HEAD).Trim()
$taskProject=Join-Path $taskRepo 'client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj'
& dotnet build $taskProject -c Release -p:Platform=x64 --no-restore *> (Join-Path $taskRun 'candidate-build.log')
if($LASTEXITCODE -ne 0){throw 'Candidate Release build failed before any model process was started'}
$taskTarget=@(& dotnet msbuild $taskProject -getProperty:TargetPath -p:Configuration=Release -p:Platform=x64 -nologo)
if($LASTEXITCODE -ne 0 -or $taskTarget.Count -ne 1){throw 'Cannot resolve the built candidate assembly'}
$taskAssemblyPath=([string]$taskTarget[0]).Trim()
$taskAssemblyHash=(Get-FileHash -LiteralPath $taskAssemblyPath).Hash
if(@(& git -C $taskRepo status --porcelain).Count -or (& git -C $taskRepo rev-parse HEAD).Trim() -ne $taskCandidate){throw 'Candidate source changed during build'}
[ordered]@{candidateCommit=$taskCandidate;configuration='Release';platform='x64';testAssemblyPath=$taskAssemblyPath;testAssemblySha256=$taskAssemblyHash;runtimeSha256=$taskProfile.runtimeSha256;modelSha256=$taskProfile.modelSha256;builtBeforeExecution=$true;sealedAtUtc=[datetimeoffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRun 'execution-seal.json') -Encoding utf8
$taskEnv=[ordered]@{
    SAAIA_LIVE_AGENT_BANK='1'
    SAAIA_VALIDATION_BACKEND_URL='http://saaia-server:5122'
    SAAIA_API_KEY=$taskKey
    SAAIA_LLM_PROVIDER_MODE='Local'
    SAAIA_LLM_EXTERNAL_POLICY='ProductionLocal'
    SAAIA_AGENT_VALIDATION_ADVANCED_SERVER='0'
    SAAIA_VALIDATION_LLM_BASE_URL='http://127.0.0.1:1234/v1'
    SAAIA_VALIDATION_LLM_MODEL='local'
    SAAIA_AGENT_VALIDATION_BANK_PATH=$taskBank
    SAAIA_AGENT_VALIDATION_IDS=($taskIds -join ',')
    SAAIA_AGENT_VALIDATION_TIMEOUT_SECONDS='300'
    SAAIA_AGENT_VALIDATION_MAX_OUTPUT_TOKENS='900'
    SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS='0'
    SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_TOKENS='12000'
    SAAIA_SOURCE_BACKED_AGENT_V2_CONTEXT_TOKENS='4096'
    SAAIA_AGENT_VALIDATION_OUTPUT_DIR=(Join-Path $taskRun 'results')
}
$taskPrevious=@{}
$taskProcess=$null
$taskExit=$null
try {
    foreach($taskEntry in $taskEnv.GetEnumerator()) { $taskPrevious[$taskEntry.Key]=[Environment]::GetEnvironmentVariable($taskEntry.Key,'Process'); [Environment]::SetEnvironmentVariable($taskEntry.Key,[string]$taskEntry.Value,'Process') }
    $taskProcess=Start-Process -FilePath $taskProfile.runtimePath -ArgumentList $taskProfile.arguments -WorkingDirectory (Split-Path -Parent $taskProfile.runtimePath) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskRun 'runtime.stdout.log') -RedirectStandardError (Join-Path $taskRun 'runtime.stderr.log')
    $taskDeadline=[DateTimeOffset]::UtcNow.AddSeconds(120)
    $taskHealthy=$false
    while([DateTimeOffset]::UtcNow -lt $taskDeadline) {
        $taskProcess.Refresh(); if($taskProcess.HasExited){throw 'Owned model exited'}
        try { if((Invoke-WebRequest -Uri 'http://127.0.0.1:1234/health' -TimeoutSec 2).StatusCode -eq 200){$taskHealthy=$true; break} } catch {}
        Start-Sleep -Milliseconds 500
    }
    if(-not $taskHealthy){throw 'Local model readiness deadline'}
    $taskOutput=& dotnet test (Join-Path $taskRepo 'client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj') -c Release -p:Platform=x64 --no-build --no-restore --filter 'FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveQuestionBankAgentValidationTests.Live_question_bank_agent_validation_when_enabled' --results-directory $taskRun --logger 'trx;LogFileName=local-diagnostics.trx' 2>&1
    $taskExit=$LASTEXITCODE
    $taskOutput | Set-Content -LiteralPath (Join-Path $taskRun 'test-output.log') -Encoding utf8
    if($taskExit -ne 0){throw 'Local diagnostic runner failed'}
} finally {
    if($null -ne $taskProcess){$taskProcess.Refresh(); if(-not $taskProcess.HasExited){Stop-Process -Id $taskProcess.Id -Force}; $null=$taskProcess.WaitForExit(15000)}
    foreach($taskEntry in $taskEnv.GetEnumerator()){[Environment]::SetEnvironmentVariable($taskEntry.Key,$taskPrevious[$taskEntry.Key],'Process')}
    $taskUnchanged=(Get-FileHash -LiteralPath $taskLedger).Hash -eq $taskLedgerBefore
    [ordered]@{candidateCommit=$taskCandidate;candidateCommitUnchanged=((& git -C $taskRepo rev-parse HEAD).Trim() -eq $taskCandidate);executedAssemblySha256=$taskAssemblyHash;executedAssemblyUnchanged=((Get-FileHash -LiteralPath $taskAssemblyPath).Hash -eq $taskAssemblyHash);diagnosticOnly=$true;holdoutAcceptance=$false;expectedCases=$taskIds.Count;localModelOnThisPc=$true;advancedServerEnabled=$false;externalPolicy='ProductionLocal';providerLedgerUnchanged=$taskUnchanged;localPort1234Listeners=@(Get-NetTCPConnection -LocalPort 1234 -State Listen -ErrorAction SilentlyContinue).Count;environmentRestored=$true;exitCode=$taskExit;endedAt=(Get-Date -Format o)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRun 'resource-shutdown.json') -Encoding utf8
}
Get-Content -LiteralPath (Join-Path $taskRun 'resource-shutdown.json') -Raw
