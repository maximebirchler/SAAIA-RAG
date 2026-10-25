#requires -Version 7.4
# These helpers are called only after the runner's explicit Execute gate.
function Get-OwnedPaidEnvironment {
    param([Parameter(Mandatory)][string]$OutputDirectory)
    $root = [IO.Path]::GetFullPath($OutputDirectory)
    $owner = Get-Content -LiteralPath (Join-Path $root 'environment.json') -Raw | ConvertFrom-Json
    $data = [IO.Path]::GetFullPath((Join-Path $root 'runtime/postgres/data'))
    if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or
        [IO.Path]::GetFullPath($owner.outputDirectory) -ine $root -or
        [IO.Path]::GetFullPath($owner.postgresDataDirectory) -ine $data -or [int]$owner.pgPort -eq 5432) {
        throw 'The paid runner requires its own fresh loopback PostgreSQL cluster.'
    }
    $pg = @($owner.processes | Where-Object name -eq 'postgres')
    if ($pg.Count -ne 1) { throw 'Exactly one owned PostgreSQL process is required.' }
    $actual = Get-Process -Id $pg[0].pid -ErrorAction Stop
    if ($actual.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$pg[0].startedAtUtc).UtcTicks -or
        [IO.Path]::GetFullPath($actual.Path) -ine [IO.Path]::GetFullPath($pg[0].path)) { throw 'PostgreSQL process identity changed.' }
    $pidLines = [IO.File]::ReadAllLines((Join-Path $data 'postmaster.pid'))
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $owner.pgPort -ErrorAction Stop)
    if ($pidLines.Count -lt 4 -or [int]$pidLines[0] -ne $actual.Id -or [int]$pidLines[3] -ne $owner.pgPort -or
        [IO.Path]::GetFullPath($pidLines[1]) -ine $data -or $listeners.Count -eq 0 -or
        @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') -or $_.OwningProcess -ne $actual.Id }).Count -gt 0) {
        throw 'PostgreSQL path, PID, port or loopback ownership mismatch.'
    }
    return $owner
}

function Invoke-OwnedPaidSql {
    param([Parameter(Mandatory)][string]$OutputDirectory, [string]$Query = '', [switch]$SeedRealCorpus)
    $owner = Get-OwnedPaidEnvironment $OutputDirectory
    $private = Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
    $previous = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $private.pgPassword
        $psql = Join-Path $owner.postgresBinDirectory 'psql.exe'
        $sqlArgs = @('-w','-X','-qAt','-h','127.0.0.1','-p',"$($owner.pgPort)",'-U','saaia_winui','-d','postgres','-v','ON_ERROR_STOP=1')
        $actual = (& $psql @sqlArgs -c "SELECT current_setting('data_directory');" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($owner.postgresDataDirectory)) {
            throw 'Connected PostgreSQL data_directory is not owned by this campaign.'
        }
        $result = if ($SeedRealCorpus) {
            & $psql @sqlArgs -f (Join-Path $OutputDirectory 'runtime/seed-real-corpus.sql') 2>&1 | Out-String
        } else {
            & $psql @sqlArgs -c ("BEGIN READ ONLY;" + $Query + ';COMMIT;') 2>&1 | Out-String
        }
        if ($LASTEXITCODE -ne 0) { throw 'Owned campaign SQL failed; private diagnostics must be reviewed.' }
        return $result.Trim()
    } finally { $env:PGPASSWORD = $previous }
}

function Start-OwnedPaidBackend {
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][hashtable]$Profile, [Parameter(Mandatory)][string]$ApiKey, [bool]$WorkerEnabled=$true)
    $owner = Get-OwnedPaidEnvironment $OutputDirectory
    $port = [int]$Profile.execution.paidBackendPort
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw 'Paid backend port is occupied.' }
    $runtime = Join-Path $OutputDirectory 'runtime/paid-worker'
    if (Test-Path -LiteralPath $runtime) { throw 'A paid worker may only be started once in a fresh campaign.' }
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    $config = Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime/deployment.config.json') -Raw | ConvertFrom-Json -AsHashtable
    $remaining = [decimal]$Profile.budget.authorizedUsd - [decimal]$Profile.budget.priorCampaignCostUsd
    $config.AdvancedAnalysis = @{
        Provider='openai-dev'; LlmLocation='external-service'; LlmBaseUrl=$Profile.provider.baseUrl; LlmModel=$Profile.provider.model;
        LlmApiKeyRef='ENV:SAAIA_ADVANCED_LLM_API_KEY'; ReasoningEffort=$Profile.provider.reasoningEffort; WorkerEnabled=$WorkerEnabled;
        AdaptiveResearchEnabled=$true; NativeResearchToolsEnabled=$false; SemanticCriticEnabled=$false;
        PlannerMaxTokens=[int]$Profile.execution.plannerMaxTokens; WriterMaxTokens=[int]$Profile.execution.writerMaxTokens;
        LlmMaximumHttpAttempts=1; MaximumAttempts=1; LlmTimeoutSeconds=[int]$Profile.execution.timeoutSeconds;
        MaximumQueuedJobsPerUser=1; PollDelayMilliseconds=250; LeaseSeconds=30; HeartbeatMilliseconds=1000;
        AllowExternalProviderContent=$true; AllowExternalProviderMetadata=$true;
        ExternalBudgetAuthorizedUsd=$remaining; ExternalBudgetSoftLimitUsd=$remaining; ExternalBudgetHardLimitUsd=$remaining;
        ExternalMaximumCostPerJobUsd=[decimal]$Profile.budget.maximumCostPerJobUsd;
        ExternalMaximumCallsPerJob=[int]$Profile.execution.maximumCallsPerJob;
        ExternalInputUsdPerMillionTokens=[decimal]$Profile.pricing.inputUsdPerMillion;
        ExternalCachedInputUsdPerMillionTokens=[decimal]$Profile.pricing.cachedInputUsdPerMillion;
        ExternalCacheWriteInputUsdPerMillionTokens=[decimal]$Profile.pricing.cacheWriteUsdPerMillion;
        ExternalOutputUsdPerMillionTokens=[decimal]$Profile.pricing.outputUsdPerMillion;
        ExternalUsageLedgerPath=(Join-Path $OutputDirectory 'runtime/usage.jsonl');
        MaximumPlanQueries=4; MaximumEvidencePromptCharacters=24000; MaximumEvidenceCharactersTotal=120000;
        MaximumToolCalls=12; MaximumToolElapsedMilliseconds=60000; RetentionDays=1;
        DevelopmentTraceDirectory=(Join-Path $OutputDirectory 'runtime/private-traces'); DevelopmentTraceStrict=$true
    }
    $configPath = Join-Path $runtime 'deployment.config.json'
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 20))
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'runtime/Db') -Destination (Join-Path $runtime 'Db') -Recurse
    $backendDll = Join-Path $RepositoryRoot 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
    if ((Get-FileHash -LiteralPath $backendDll -Algorithm SHA256).Hash -ine $Profile.execution.backendAssemblySha256) {
        throw 'The production backend changed after the paid profile was frozen.'
    }
    $spec = @{executable=(Get-Command dotnet).Source; arguments=@($backendDll,'--contentRoot',$runtime,'--urls',"http://127.0.0.1:$port");
        stdout=(Join-Path $OutputDirectory 'runtime/paid-backend.stdout.log'); stderr=(Join-Path $OutputDirectory 'runtime/paid-backend.stderr.log')}
    $specPath = Join-Path $runtime 'backend-process.json'
    [IO.File]::WriteAllText($specPath, ($spec | ConvertTo-Json))
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$runtime
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -match 'OPENAI|RUNPOD|AZURE|SAAIA_|ConfigSignature|ConnectionString|QDRANT') { [void]$info.Environment.Remove($name) }
    }
    foreach ($arg in @('-NoProfile','-File',(Join-Path $RepositoryRoot 'tools/validation-winui/run-helper.ps1'),'-Specification',$specPath)) { $info.ArgumentList.Add($arg) }
    $info.Environment['ASPNETCORE_ENVIRONMENT']='Development'; $info.Environment['DOTNET_ENVIRONMENT']='Development'
    $info.Environment['ConfigSignature__ConfigPath']=$configPath; $info.Environment['ConfigSignature__SignaturePath']=(Join-Path $runtime 'missing.sig')
    $info.Environment['ConfigSignature__AllowUnsignedInDevelopment']='true'; $info.Environment['SAAIA_ADVANCED_LLM_API_KEY']=$ApiKey
    $process=[Diagnostics.Process]::Start($info)
    $owner.processes += @{name='advanced-backend-runner';pid=$process.Id;path=$info.FileName;port=$port;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o')}
    $owner.externalCallsPossible=$WorkerEnabled
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'environment.json'), ($owner | ConvertTo-Json -Depth 12))
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        try { $ready=Invoke-RestMethod -Uri "http://127.0.0.1:$port/ready" -TimeoutSec 3; if ($ready.ok) { break } } catch { }
        if ($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'The owned paid worker failed readiness; no job may be submitted.' }
        Start-Sleep -Milliseconds 250
    } while ($true)
    return "http://127.0.0.1:$port"
}
