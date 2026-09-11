[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet("OpenAI", "RunPod")]
    [string]$Provider,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$Ids = "A755-ADV-01-meal-grid-5x4",
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [string]$BaseUrl = "",
    [string]$ModelId = "",
    [decimal]$AuthorizedBudgetUsd = 0,
    [decimal]$SoftLimitUsd = 0,
    [decimal]$HardLimitUsd = 0,
    [decimal]$MaximumCostPerJobUsd = 0,
    [int]$MaximumCallsPerJob = 4,
    [decimal]$InputUsdPerMillionTokens = 0,
    [decimal]$CachedInputUsdPerMillionTokens = 0,
    [decimal]$OutputUsdPerMillionTokens = 0,
    [string]$LocalLlmExePath = "",
    [string]$LocalModelPath = "",
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ("System.Security.Cryptography.ProtectedData" -as [type])) {
    Add-Type -AssemblyName System.Security
}

function Read-EnvFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*(#|$)' -or $line -notmatch '=') { continue }
        $separator = $line.IndexOf('=')
        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}

function Require-EnvValue {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Values,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Values.ContainsKey($Name) -or
        [string]::IsNullOrWhiteSpace([string]$Values[$Name])) {
        throw "Required setting is missing from the server environment file: $Name"
    }
    return [string]$Values[$Name]
}

function Wait-BackendReady {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 90
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "The temporary backend exited before becoming ready (exit code $($Process.ExitCode))."
        }
        try {
            $response = Invoke-WebRequest -Uri "$BaseUrl/ready" -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) {
                return ($response.Content | ConvertFrom-Json)
            }
        }
        catch {
            # Startup can legitimately refuse connections while migrations run.
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The temporary backend did not become ready within $TimeoutSeconds seconds."
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) { return }
    try {
        Stop-Process -Id $Process.Id -Force -ErrorAction Stop
        $Process.WaitForExit(15000) | Out-Null
    }
    catch {
        if (-not $Process.HasExited) { throw }
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$backendProject = Join-Path $repositoryRoot "backend\SAAIA.Backend\SAAIA.Backend.csproj"
$backendContentRoot = Join-Path $repositoryRoot "backend\SAAIA.Backend"
$localConfigPath = Join-Path $backendContentRoot "appsettings.Local.json"
$agentBankScript = Join-Path $PSScriptRoot "test-advanced-analysis-agent-bank.ps1"
$secretStorePath = Join-Path $env:LOCALAPPDATA "SAAIA\client\secure.json"
$providerMode = if ($Provider -eq "OpenAI") { "openai-dev" } else { "runpod-bench" }
$secretProperty = if ($Provider -eq "OpenAI") {
    "OpenAiApiKeyProtected"
} else {
    "RunPodApiKeyProtected"
}
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    if ($Provider -eq "OpenAI") {
        $BaseUrl = "https://api.openai.com/v1"
    } else {
        throw "BaseUrl is required for RunPod."
    }
}
if ([string]::IsNullOrWhiteSpace($ModelId)) {
    if ($Provider -eq "OpenAI") {
        $ModelId = "gpt-5.6-terra"
    } else {
        throw "ModelId is required for RunPod."
    }
}
if (-not [Uri]::IsWellFormedUriString($BaseUrl, [UriKind]::Absolute) -or
    ([Uri]$BaseUrl).Scheme -ne "https") {
    throw "External provider BaseUrl must be an absolute HTTPS URI."
}
if ($Provider -eq "OpenAI") {
    if ($AuthorizedBudgetUsd -le 0) { $AuthorizedBudgetUsd = 25 }
    if ($InputUsdPerMillionTokens -le 0 -or
        $CachedInputUsdPerMillionTokens -le 0 -or
        $OutputUsdPerMillionTokens -le 0) {
        switch ($ModelId) {
            "gpt-5.6-terra" {
                $InputUsdPerMillionTokens = 2
                $CachedInputUsdPerMillionTokens = 0.20
                $OutputUsdPerMillionTokens = 12
            }
            "gpt-5.6-luna" {
                $InputUsdPerMillionTokens = 0.20
                $CachedInputUsdPerMillionTokens = 0.02
                $OutputUsdPerMillionTokens = 1.20
            }
            default {
                throw "Explicit token pricing is required for OpenAI model '$ModelId'."
            }
        }
    }
} else {
    if ($AuthorizedBudgetUsd -le 0) {
        throw "AuthorizedBudgetUsd is required for RunPod; no RunPod spend is assumed."
    }
    if ($InputUsdPerMillionTokens -le 0 -or
        $CachedInputUsdPerMillionTokens -le 0 -or
        $OutputUsdPerMillionTokens -le 0) {
        throw "Explicit input, cached-input and output token pricing is required for RunPod."
    }
}
if ($SoftLimitUsd -le 0) {
    $SoftLimitUsd = [decimal]::Round($AuthorizedBudgetUsd * 0.80, 2)
}
if ($HardLimitUsd -le 0) {
    $HardLimitUsd = [decimal]::Round($AuthorizedBudgetUsd * 0.96, 2)
}
if ($MaximumCostPerJobUsd -le 0) {
    $MaximumCostPerJobUsd = [decimal]::Min(0.50, $HardLimitUsd)
}
if ($MaximumCallsPerJob -le 0 -or
    $SoftLimitUsd -le 0 -or
    $HardLimitUsd -le 0 -or
    $SoftLimitUsd -gt $HardLimitUsd -or
    $HardLimitUsd -gt $AuthorizedBudgetUsd -or
    $MaximumCostPerJobUsd -gt $HardLimitUsd) {
    throw "The external provider budget envelope is invalid."
}
$ledgerSlug = ($providerMode + "-" + $ModelId) -replace '[^a-zA-Z0-9._-]', '-'
$usageLedgerPath = if ($Provider -eq "OpenAI" -and
    -not [string]::IsNullOrWhiteSpace($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)) {
    [System.IO.Path]::GetFullPath($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)
} elseif (-not [string]::IsNullOrWhiteSpace(
    $env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH)) {
    [System.IO.Path]::GetFullPath($env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH)
} elseif ($Provider -eq "OpenAI") {
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\openai-terra-usage.jsonl"
} else {
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\$ledgerSlug-usage.jsonl"
}
$ServerEnvPath = [System.IO.Path]::GetFullPath($ServerEnvPath)

if (-not (Test-Path -LiteralPath $ServerEnvPath -PathType Leaf)) {
    throw "Server environment file not found: $ServerEnvPath"
}
if (-not (Test-Path -LiteralPath $secretStorePath -PathType Leaf)) {
    throw "Protected SAAIA secret store not found. Import the $Provider key first."
}
if (-not (Test-Path -LiteralPath $agentBankScript -PathType Leaf)) {
    throw "Advanced-analysis bank runner not found: $agentBankScript"
}
if ([string]::IsNullOrWhiteSpace($LocalLlmExePath)) {
    $runtimeRoot = Join-Path $env:LOCALAPPDATA "SAAIA\llm\runtime"
    $runtime = Get-ChildItem -LiteralPath $runtimeRoot -Filter "llama-server.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $runtime) {
        throw "No installed llama-server.exe was found below $runtimeRoot."
    }
    $LocalLlmExePath = $runtime.FullName
}
$LocalLlmExePath = [System.IO.Path]::GetFullPath($LocalLlmExePath)
if (-not (Test-Path -LiteralPath $LocalLlmExePath -PathType Leaf)) {
    throw "Local LLM runtime not found: $LocalLlmExePath"
}
if ([string]::IsNullOrWhiteSpace($LocalModelPath)) {
    $LocalModelPath = Join-Path $env:LOCALAPPDATA "SAAIA\Models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"
}
$LocalModelPath = [System.IO.Path]::GetFullPath($LocalModelPath)
if (-not (Test-Path -LiteralPath $LocalModelPath -PathType Leaf)) {
    throw "Qualified local model not found: $LocalModelPath"
}
if (@(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Port $BackendPort is already in use."
}

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\advanced-product-path-$($Provider.ToLowerInvariant())-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$serverEnvironment = Read-EnvFile -Path $ServerEnvPath
$postgresDatabase = Require-EnvValue $serverEnvironment "POSTGRES_DB"
$postgresUser = Require-EnvValue $serverEnvironment "POSTGRES_USER"
$postgresPassword = Require-EnvValue $serverEnvironment "POSTGRES_PASSWORD"
$authPepper = Require-EnvValue $serverEnvironment "SAAIA_AUTH_PEPPER"
$serverApiKey = Require-EnvValue $serverEnvironment "SAAIA_BOOTSTRAP_API_KEY"
$qdrantApiKey = Require-EnvValue $serverEnvironment "QDRANT_API_KEY"
$teiModel = Require-EnvValue $serverEnvironment "TEI_MODEL_ID"

$secretStore = Get-Content -LiteralPath $secretStorePath -Raw | ConvertFrom-Json
$protectedProviderKey = $secretStore.$secretProperty
if ([string]::IsNullOrWhiteSpace($protectedProviderKey)) {
    throw "$Provider key is absent from the protected SAAIA secret store."
}
$entropy = [System.Text.Encoding]::UTF8.GetBytes("SAAIA.Client.WinUI|CDC-v2.7")
$protectedBytes = [Convert]::FromBase64String($protectedProviderKey)
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protectedBytes,
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$providerApiKey = [System.Text.Encoding]::UTF8.GetString($plainBytes)

$localConfigBackup = $null
$localConfigExisted = Test-Path -LiteralPath $localConfigPath -PathType Leaf
if ($localConfigExisted) {
    $localConfigBackup = [System.IO.File]::ReadAllBytes($localConfigPath)
}

$trackedEnvironment = @(
    "ASPNETCORE_ENVIRONMENT",
    "DOTNET_ENVIRONMENT",
    "ConfigSignature__ConfigPath",
    "ConfigSignature__SignaturePath",
    "ConfigSignature__AllowUnsignedInDevelopment",
    "QDRANT_API_KEY",
    "SAAIA_ADVANCED_LLM_API_KEY",
    "SAAIA_VALIDATION_BACKEND_URL",
    "SAAIA_API_KEY"
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$backendProcess = $null
$failure = $null
$ready = $null
$bankArtifactDirectory = Join-Path $ArtifactDirectory "agent-bank"
$backendStdout = Join-Path $ArtifactDirectory "backend.stdout.log"
$backendStderr = Join-Path $ArtifactDirectory "backend.stderr.log"
$baseUrl = "http://127.0.0.1:$BackendPort"
$unsignedConfigPath = Join-Path $ArtifactDirectory "unsigned-development.config.json"
$missingSignaturePath = Join-Path $ArtifactDirectory "unsigned-development.config.missing.sig"

try {
    $referenceHeaders = @{ "X-Admin-Key" = $serverApiKey }
    $referenceJobsResponse = Invoke-WebRequest `
        -Uri ($ReferenceBackendUrl.TrimEnd('/') + "/ingestion/jobs") `
        -Headers $referenceHeaders `
        -UseBasicParsing `
        -TimeoutSec 15
    $referenceJobs = @((($referenceJobsResponse.Content | ConvertFrom-Json).items))
    $activeIngestionJobs = @($referenceJobs | Where-Object {
        $_.status -in @("queued", "running")
    })
    if ($activeIngestionJobs.Count -gt 0) {
        throw "Reference backend reports active ingestion jobs; refusing to start a second backend."
    }

    $connectionString = "Host=saaia-server;Port=5432;Database=$postgresDatabase;Username=$postgresUser;Password=$postgresPassword;Pooling=true;Maximum Pool Size=20"
    $localConfig = [ordered]@{
        Database = [ordered]@{ ConnectionString = $connectionString }
        Auth = [ordered]@{
            ApiKeyHeaderName = "X-Api-Key"
            AdminKeyHeaderName = "X-Admin-Key"
            Pepper = $authPepper
        }
        Bootstrap = [ordered]@{ Enabled = $false }
        Rag = [ordered]@{
            QdrantBaseUrl = "http://saaia-server:6333"
            QdrantCollection = "knowledge_base"
            QdrantApiKeyRef = "ENV:QDRANT_API_KEY"
            RequireQdrantAuthInProd = $true
            EmbeddingsBaseUrl = "http://saaia-server:8081"
            EmbeddingsModel = $teiModel
            EnableRerank = $false
            RerankBaseUrl = ""
            DefaultTopK = 5
            MaxTopK = 60
            SearchMaxConcurrency = 2
            SearchQueueLimit = 4
        }
        Ingestion = [ordered]@{
            DocumentsRoot = (Join-Path $ArtifactDirectory "empty-documents")
            WorkerEnabled = $false
            ScannerEnabled = $false
            WatcherEnabled = $false
        }
        CatalogSnapshot = [ordered]@{ Enabled = $false }
        RuntimeGovernance = [ordered]@{
            CapabilityBWorkerEnabled = $false
            CapabilityBAutoEnqueueWhenIngestionIdleEnabled = $false
        }
        License = [ordered]@{
            Seats = 1
            AdvancedAnalysisEnabled = $true
        }
        AdvancedAnalysis = [ordered]@{
            Provider = $providerMode
            ProviderKey = ""
            LlmLocation = "external-service"
            LlmBaseUrl = $BaseUrl.TrimEnd('/')
            LlmModel = $ModelId
            LlmApiKeyRef = "ENV:SAAIA_ADVANCED_LLM_API_KEY"
            ReasoningEffort = "low"
            LlmTimeoutSeconds = 600
            LlmMaximumHttpAttempts = 3
            LlmRetryBaseDelayMilliseconds = 15000
            LlmMaximumRetryDelayMilliseconds = 60000
            PlannerMaxTokens = 512
            WriterMaxTokens = 2400
            MaximumPlanQueries = 8
            MaximumEvidencePromptCharacters = 14000
            ExternalBudgetAuthorizedUsd = $AuthorizedBudgetUsd
            ExternalBudgetSoftLimitUsd = $SoftLimitUsd
            ExternalBudgetHardLimitUsd = $HardLimitUsd
            ExternalMaximumCostPerJobUsd = $MaximumCostPerJobUsd
            ExternalMaximumCallsPerJob = $MaximumCallsPerJob
            ExternalInputUsdPerMillionTokens = $InputUsdPerMillionTokens
            ExternalCachedInputUsdPerMillionTokens = $CachedInputUsdPerMillionTokens
            ExternalOutputUsdPerMillionTokens = $OutputUsdPerMillionTokens
            ExternalUsageLedgerPath = $usageLedgerPath
            RetentionDays = 30
            MaximumQueuedJobsPerUser = 4
            WorkerEnabled = $true
            PollDelayMilliseconds = 500
            LeaseSeconds = 120
            HeartbeatMilliseconds = 5000
            RetryDelayMilliseconds = 5000
            MaximumAttempts = 1
            MaximumEvidenceCharactersPerItem = 24000
            MaximumEvidenceCharactersTotal = 256000
            AllowExternalProviderContent = $true
            AllowExternalProviderMetadata = $true
            MaximumToolCalls = 32
            MaximumSearchTopK = 60
            MaximumAccumulatedEvidenceItems = 256
            MaximumToolElapsedMilliseconds = 300000
        }
        OpenTelemetry = [ordered]@{ Enabled = $false }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = "Information"
                "Microsoft.AspNetCore" = "Warning"
            }
        }
    }
    New-Item -ItemType Directory -Path $localConfig.Ingestion.DocumentsRoot -Force | Out-Null
    $localConfig | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $localConfigPath -Encoding utf8

    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:DOTNET_ENVIRONMENT = "Development"
    '{}' | Set-Content -LiteralPath $unsignedConfigPath -Encoding utf8
    $env:ConfigSignature__ConfigPath = $unsignedConfigPath
    $env:ConfigSignature__SignaturePath = $missingSignaturePath
    $env:ConfigSignature__AllowUnsignedInDevelopment = "true"
    $env:QDRANT_API_KEY = $qdrantApiKey
    $env:SAAIA_ADVANCED_LLM_API_KEY = $providerApiKey
    $env:SAAIA_VALIDATION_BACKEND_URL = $baseUrl
    $env:SAAIA_API_KEY = $serverApiKey

    [ordered]@{
        schemaVersion = "saaia-advanced-product-path-preflight-v1"
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        topology = "local-qwen-router-to-current-local-backend-to-$($Provider.ToLowerInvariant())"
        referenceBackendHost = ([Uri]$ReferenceBackendUrl).Host
        temporaryBackendUrl = $baseUrl
        remoteDatabaseHost = "saaia-server"
        remoteQdrantHost = "saaia-server"
        remoteEmbeddingHost = "saaia-server"
        activeIngestionJobsBeforeStart = $activeIngestionJobs.Count
        configurationSignatureMode = "unsigned-development-test-override"
        ingestionWorkersEnabled = $false
        expectedAdvancedProvider = $providerMode
        expectedAdvancedModel = $ModelId
        externalEndpointHost = ([Uri]$BaseUrl).Host
        localLlmRuntimeSha256 = (Get-FileHash -LiteralPath $LocalLlmExePath -Algorithm SHA256).Hash
        localModelSha256 = (Get-FileHash -LiteralPath $LocalModelPath -Algorithm SHA256).Hash
        selectedIds = @($Ids -split '[,;]' | ForEach-Object Trim | Where-Object { $_ })
        repetitions = $Repetitions
        authorizedBudgetUsd = $AuthorizedBudgetUsd
        softLimitUsd = $SoftLimitUsd
        hardStopUsd = $HardLimitUsd
        maximumCostPerJobUsd = $MaximumCostPerJobUsd
        maximumCallsPerJob = $MaximumCallsPerJob
        inputUsdPerMillionTokens = $InputUsdPerMillionTokens
        cachedInputUsdPerMillionTokens = $CachedInputUsdPerMillionTokens
        outputUsdPerMillionTokens = $OutputUsdPerMillionTokens
        usageLedgerPath = $usageLedgerPath
        productStatus = "TESTE_NON_APPROUVE"
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory "preflight-seal.json") -Encoding utf8

    $buildOutput = & dotnet build $backendProject -c $Configuration --no-restore 2>&1
    $buildOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "backend-build.log") -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Backend build failed with exit code $LASTEXITCODE."
    }

    $argumentList = @(
        "run",
        "--project", ('"' + $backendProject + '"'),
        "-c", $Configuration,
        "--no-build",
        "--no-restore",
        "--",
        "--urls", $baseUrl
    )
    $backendProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $argumentList `
        -WorkingDirectory $repositoryRoot `
        -NoNewWindow `
        -RedirectStandardOutput $backendStdout `
        -RedirectStandardError $backendStderr `
        -PassThru

    $ready = Wait-BackendReady -BaseUrl $baseUrl -Process $backendProcess
    $ready | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory "backend-ready.json") -Encoding utf8

    & $agentBankScript `
        -ExpectedAdvancedProvider $providerMode `
        -ExpectedAdvancedModel $ModelId `
        -LocalLlmExePath $LocalLlmExePath `
        -LocalModelPath $LocalModelPath `
        -Ids $Ids `
        -Repetitions $Repetitions `
        -Configuration $Configuration `
        -Platform $Platform `
        -ArtifactDirectory $bankArtifactDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Advanced product-path bank failed with exit code $LASTEXITCODE."
    }
}
catch {
    $failure = $_.Exception.GetType().Name + ": " + $_.Exception.Message
    throw
}
finally {
    Stop-OwnedProcess -Process $backendProcess

    $portReleaseDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while (@(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count -gt 0 -and
           [DateTimeOffset]::UtcNow -lt $portReleaseDeadline) {
        Start-Sleep -Milliseconds 250
    }

    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    if ($localConfigExisted) {
        [System.IO.File]::WriteAllBytes($localConfigPath, $localConfigBackup)
    }
    elseif (Test-Path -LiteralPath $localConfigPath) {
        Remove-Item -LiteralPath $localConfigPath -Force
    }
    if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    $providerApiKey = $null
    $serverApiKey = $null
    $postgresPassword = $null
    $authPepper = $null
    $qdrantApiKey = $null

    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        failure = $failure
        environmentRestored = $true
        localConfigRestored = $true
        secretPersistedInArtifact = $false
        temporaryBackendStopped = $(
            $null -eq $backendProcess -or $backendProcess.HasExited)
        localPortListenerCountAfterRun = @(
            Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count
        localLlmPortListenerCountAfterRun = @(
            Get-NetTCPConnection -State Listen -LocalPort 1234 -ErrorAction SilentlyContinue).Count
        productStatus = "TESTE_NON_APPROUVE"
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory "resource-shutdown.json") -Encoding utf8
}

Write-Output "Advanced product-path campaign completed. Artifact: $ArtifactDirectory"
