[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [ValidateRange(5, 120)]
    [int]$ClientObservationTimeoutSeconds = 45,
    [string]$Configuration = "Release",
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

function Get-OptionalPropertyValue {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Wait-BackendReady {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1, 180)][int]$TimeoutSeconds = 90
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
    param(
        [System.Diagnostics.Process]$Process,
        [switch]$PreferGraceful
    )

    if ($null -eq $Process -or $Process.HasExited) { return $true }
    $graceful = $false
    if ($PreferGraceful) {
        try {
            $Process.Refresh()
            if ($Process.CloseMainWindow()) {
                $graceful = $Process.WaitForExit(15000)
            }
        }
        catch {
            $graceful = $false
        }
    }
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction Stop
        $Process.WaitForExit(15000) | Out-Null
    }
    return $graceful
}

function Wait-ForClientResume {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][Guid]$JobId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1, 10000)][int]$MinimumResumeCount,
        [ValidateRange(1, 10000)][int]$MinimumStartCount,
        [ValidateRange(5, 120)][int]$TimeoutSeconds
    )

    $jobPattern = [regex]::Escape("jobId=$($JobId.ToString('D'))") +
        '.*outcome=job_resumed'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "The WinUI client exited before resuming the queued job (exit code $($Process.ExitCode))."
        }
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $text = Get-Content -LiteralPath $LogPath -Raw
            $resumeCount = [regex]::Matches($text, $jobPattern).Count
            $startCount = [regex]::Matches(
                $text,
                [regex]::Escape('=== App starting ===')).Count
            if ($resumeCount -ge $MinimumResumeCount -and
                $startCount -ge $MinimumStartCount) {
                return [ordered]@{
                    resumeCount = $resumeCount
                    startCount = $startCount
                    observedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                }
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The WinUI client did not persist the resumed queued job within $TimeoutSeconds seconds."
}

function Start-TestClient {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.Environment['SAAIA_LLM_PROVIDER_MODE'] = 'Local'
    $startInfo.Environment['SAAIA_LLM_EXTERNAL_POLICY'] = 'ProductionLocal'
    return [System.Diagnostics.Process]::Start($startInfo)
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve the repository commit for the WinUI restart seal."
}
$repositoryTrackedDirty = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
if ($repositoryTrackedDirty) {
    throw "The WinUI restart proof requires a clean tracked worktree."
}

$ServerEnvPath = [System.IO.Path]::GetFullPath($ServerEnvPath)
if (-not (Test-Path -LiteralPath $ServerEnvPath -PathType Leaf)) {
    throw "Server environment file not found: $ServerEnvPath"
}
if (@(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Port $BackendPort is already in use."
}
if (@(Get-Process -Name 'SAAIA.Client.WinUI' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "A WinUI client is already running; refusing to disturb the user's process."
}

$backendProject = Join-Path $repositoryRoot 'backend\SAAIA.Backend\SAAIA.Backend.csproj'
$backendContentRoot = Join-Path $repositoryRoot 'backend\SAAIA.Backend'
$clientProject = Join-Path $repositoryRoot 'client\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj'
$clientProjectRoot = Split-Path -Parent $clientProject
$clientExecutable = Join-Path $clientProjectRoot (
    "bin\{0}\{1}\net8.0-windows10.0.19041.0\win-{0}\SAAIA.Client.WinUI.exe" -f
        $Platform,
        $Configuration)
$localConfigPath = Join-Path $backendContentRoot 'appsettings.Local.json'
$clientSettingsPath = Join-Path $env:LOCALAPPDATA 'SAAIA\client\settings.json'
$clientSecurePath = Join-Path $env:LOCALAPPDATA 'SAAIA\client\secure.json'
$clientLogPath = Join-Path $env:LOCALAPPDATA 'SAAIA\logs\client_startup.log'

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactDirectory = Join-Path $repositoryRoot (
        "artifacts\reprise-pc-20260908\a763-winui-restart-resume-$stamp")
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (-not $ArtifactDirectory.StartsWith(
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactDirectory must stay below the repository artifacts directory."
}
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$serverEnvironment = Read-EnvFile -Path $ServerEnvPath
$postgresDatabase = Require-EnvValue $serverEnvironment 'POSTGRES_DB'
$postgresUser = Require-EnvValue $serverEnvironment 'POSTGRES_USER'
$postgresPassword = Require-EnvValue $serverEnvironment 'POSTGRES_PASSWORD'
$authPepper = Require-EnvValue $serverEnvironment 'SAAIA_AUTH_PEPPER'
$serverApiKey = Require-EnvValue $serverEnvironment 'SAAIA_BOOTSTRAP_API_KEY'
$qdrantApiKey = Require-EnvValue $serverEnvironment 'QDRANT_API_KEY'
$teiModel = Require-EnvValue $serverEnvironment 'TEI_MODEL_ID'

$localConfigExisted = Test-Path -LiteralPath $localConfigPath -PathType Leaf
$localConfigBackup = if ($localConfigExisted) {
    [System.IO.File]::ReadAllBytes($localConfigPath)
} else { $null }
$clientSettingsExisted = Test-Path -LiteralPath $clientSettingsPath -PathType Leaf
$clientSettingsBackup = if ($clientSettingsExisted) {
    [System.IO.File]::ReadAllBytes($clientSettingsPath)
} else { $null }
$clientSecureExisted = Test-Path -LiteralPath $clientSecurePath -PathType Leaf
$clientSecureBackup = if ($clientSecureExisted) {
    [System.IO.File]::ReadAllBytes($clientSecurePath)
} else { $null }
$clientLogExisted = Test-Path -LiteralPath $clientLogPath -PathType Leaf
$clientLogBackup = if ($clientLogExisted) {
    [System.IO.File]::ReadAllBytes($clientLogPath)
} else { $null }

$trackedEnvironment = @(
    'ASPNETCORE_ENVIRONMENT',
    'DOTNET_ENVIRONMENT',
    'ConfigSignature__ConfigPath',
    'ConfigSignature__SignaturePath',
    'ConfigSignature__AllowUnsignedInDevelopment',
    'QDRANT_API_KEY'
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$backendProcess = $null
$firstClientProcess = $null
$secondClientProcess = $null
$firstClientClosedGracefully = $false
$secondClientClosedGracefully = $false
$sessionId = [Guid]::Empty
$jobId = [Guid]::Empty
$handoffId = [Guid]::Empty
$userId = ''
$failure = $null
$completed = $false
$baseUrl = "http://127.0.0.1:$BackendPort"
$backendStdout = Join-Path $ArtifactDirectory 'backend.stdout.log'
$backendStderr = Join-Path $ArtifactDirectory 'backend.stderr.log'
$unsignedConfigPath = Join-Path $ArtifactDirectory 'unsigned-development.config.json'
$missingSignaturePath = Join-Path $ArtifactDirectory 'unsigned-development.config.missing.sig'
$headers = @{ 'X-Api-Key' = $serverApiKey }

try {
    $referenceHeaders = @{ 'X-Admin-Key' = $serverApiKey }
    $referenceJobsResponse = Invoke-WebRequest `
        -Uri ($ReferenceBackendUrl.TrimEnd('/') + '/ingestion/jobs') `
        -Headers $referenceHeaders `
        -UseBasicParsing `
        -TimeoutSec 15
    $referenceJobs = @((($referenceJobsResponse.Content | ConvertFrom-Json).items))
    $activeIngestionJobs = @($referenceJobs | Where-Object {
        $_.status -in @('queued', 'running')
    })
    if ($activeIngestionJobs.Count -gt 0) {
        throw "Reference backend reports active ingestion jobs; refusing to start a second backend."
    }

    $backendBuildOutput = & dotnet build $backendProject `
        -c $Configuration --no-restore 2>&1
    $backendBuildOutput | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'backend-build.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "Backend build failed with exit code $LASTEXITCODE."
    }

    $clientBuildOutput = & dotnet build $clientProject `
        -c $Configuration -p:Platform=$Platform --no-restore 2>&1
    $clientBuildOutput | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'client-build.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "WinUI build failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $clientExecutable -PathType Leaf)) {
        throw "WinUI executable not found after build: $clientExecutable"
    }

    $connectionString = "Host=saaia-server;Port=5432;Database=$postgresDatabase;Username=$postgresUser;Password=$postgresPassword;Pooling=true;Maximum Pool Size=20"
    $localConfig = [ordered]@{
        Database = [ordered]@{ ConnectionString = $connectionString }
        Auth = [ordered]@{
            ApiKeyHeaderName = 'X-Api-Key'
            AdminKeyHeaderName = 'X-Admin-Key'
            Pepper = $authPepper
        }
        Bootstrap = [ordered]@{ Enabled = $false }
        Rag = [ordered]@{
            QdrantBaseUrl = 'http://saaia-server:6333'
            QdrantCollection = 'knowledge_base'
            QdrantApiKeyRef = 'ENV:QDRANT_API_KEY'
            RequireQdrantAuthInProd = $true
            EmbeddingsBaseUrl = 'http://saaia-server:8081'
            EmbeddingsModel = $teiModel
            EnableRerank = $false
            RerankBaseUrl = ''
        }
        Ingestion = [ordered]@{
            DocumentsRoot = (Join-Path $ArtifactDirectory 'empty-documents')
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
            Provider = 'disabled'
            WorkerEnabled = $false
            RetentionDays = 1
            MaximumQueuedJobsPerUser = 4
        }
        OpenTelemetry = [ordered]@{ Enabled = $false }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = 'Information'
                'Microsoft.AspNetCore' = 'Warning'
            }
        }
    }
    New-Item -ItemType Directory -Path $localConfig.Ingestion.DocumentsRoot -Force | Out-Null
    $localConfig | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $localConfigPath -Encoding utf8

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    '{}' | Set-Content -LiteralPath $unsignedConfigPath -Encoding utf8
    $env:ConfigSignature__ConfigPath = $unsignedConfigPath
    $env:ConfigSignature__SignaturePath = $missingSignaturePath
    $env:ConfigSignature__AllowUnsignedInDevelopment = 'true'
    $env:QDRANT_API_KEY = $qdrantApiKey

    [ordered]@{
        schemaVersion = 'saaia-advanced-winui-restart-preflight.v1'
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit = $repositoryCommit
        repositoryTrackedDirty = $repositoryTrackedDirty
        referenceBackendHost = ([Uri]$ReferenceBackendUrl).Host
        temporaryBackendUrl = $baseUrl
        remoteDatabaseHost = 'saaia-server'
        workerEnabled = $false
        provider = 'disabled'
        externalProviderCallsPossible = $false
        userSettingsBackedUp = $clientSettingsExisted
        userSecureStoreBackedUp = $clientSecureExisted
        userLogBackedUp = $clientLogExisted
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'preflight-seal.json') -Encoding utf8

    $arguments = @(
        'run',
        '--project', ('"' + $backendProject + '"'),
        '-c', $Configuration,
        '--no-build',
        '--no-restore',
        '--',
        '--urls', $baseUrl
    )
    $backendProcess = Start-Process `
        -FilePath 'dotnet' `
        -ArgumentList $arguments `
        -WorkingDirectory $repositoryRoot `
        -NoNewWindow `
        -RedirectStandardOutput $backendStdout `
        -RedirectStandardError $backendStderr `
        -PassThru
    $ready = Wait-BackendReady -BaseUrl $baseUrl -Process $backendProcess
    $ready | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'backend-ready.json') -Encoding utf8

    $userId = [Guid]::NewGuid().ToString('D')
    $sessionBody = [ordered]@{
        userId = $userId
        title = 'A763 WinUI restart resume proof'
        clientUser = 'automated-validation'
    } | ConvertTo-Json -Compress
    $session = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/chat/sessions" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $sessionBody
    $sessionId = [Guid]$session.sessionId
    $handoffId = [Guid]::NewGuid()

    $jobBody = [ordered]@{
        userId = $userId
        sessionId = $sessionId
        handoff = [ordered]@{
            schemaVersion = 'saaia.advanced-analysis-handoff.v1'
            handoffId = $handoffId
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            requestText = 'Validate durable WinUI restart tracking without executing a provider.'
            language = 'en'
            originIntent = 'rag.answer'
            reasonCode = 'winui_restart_resume_validation'
            transferStage = 'before_retrieval'
            load = [ordered]@{
                planKind = 'lifecycle_validation'
                deliverable = 'queued_job_resume'
                answerUnitCount = 1
                atomicEvidenceCount = 0
                rowCount = 0
                columnCount = 0
                structuredLayout = $false
                atomicEvidenceType = ''
                atomicEvidenceMode = ''
                selectionPolicy = ''
                questionFocus = 'durable client tracking'
                requestedDocumentName = ''
                boundedNamedDocumentExtraction = $false
                candidateScopePaths = @()
                rowLabels = @()
                columns = @()
            }
            researchState = [ordered]@{
                memoryIsEvidence = $false
                evidenceRevalidationRequired = $true
                executedTools = @()
                executedQueries = @()
                attempts = @()
                evidenceReferences = @()
            }
            dataPolicy = [ordered]@{
                externalProviderContentAuthorized = $false
                externalProviderMetadataAuthorized = $false
                authorizationSource = 'server_policy_required'
            }
        }
    } | ConvertTo-Json -Depth 12 -Compress
    $job = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/advanced-analysis/jobs" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $jobBody
    $jobId = [Guid]$job.jobId
    if ($job.status -ne 'queued' -or [int]$job.revision -ne 1) {
        throw "The lifecycle test job was not durably queued at revision 1."
    }

    $sources = [ordered]@{
        intent = 'advanced_analysis.answer'
        sources = @()
        advancedAnalysis = [ordered]@{
            schemaVersion = 'saaia.advanced-analysis-client-state.v1'
            jobId = $jobId
            handoffId = $handoffId
            sessionId = $sessionId
            status = 'queued'
            revision = 1
            attemptCount = 0
            cancelRequested = $false
            updatedAtUtc = $job.updatedAtUtc
            expiresAtUtc = $job.expiresAtUtc
            providerKey = $null
            providerModel = $null
            providerCallCount = $null
            claimCount = $null
            evidenceCount = $null
            inputTokens = $null
            outputTokens = $null
            cachedInputTokens = $null
            estimatedCostUsd = $null
            resultOutcome = 'job_created'
            lastErrorCode = $null
        }
    }
    $messageBody = [ordered]@{
        userId = $userId
        role = 'assistant'
        content = 'The advanced analysis is queued on the server.'
        sourcesJson = ($sources | ConvertTo-Json -Depth 12 -Compress)
        statusNote = 'Queued'
        progressText = 'Waiting for the server job'
        trackingMetaJson = $null
    } | ConvertTo-Json -Depth 5 -Compress
    $message = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/chat/sessions/$($sessionId.ToString('D'))/messages" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $messageBody
    if ([string]::IsNullOrWhiteSpace([string]$message.messageId)) {
        throw "The durable assistant message was not created."
    }

    $provisioningHash = $null
    $provisioningCandidates = @(
        (Join-Path ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonApplicationData)) 'SAAIA\provisioning.json'),
        (Join-Path $env:LOCALAPPDATA 'SAAIA\provisioning.json')
    )
    $provisioningPath = $provisioningCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ($provisioningPath) {
        $provisioningText = Get-Content -LiteralPath $provisioningPath -Raw
        $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($provisioningText))
        $provisioningHash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $clientSettingsPath) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $clientLogPath) -Force | Out-Null
    $testSettings = [ordered]@{
        BackendUrl = $baseUrl
        BackendUrlAlternates = ''
        ShowAdvancedUi = $false
        AutoConnect = $true
        UiLanguage = 'en'
        UiTheme = 'dark'
        ProvisioningHash = $provisioningHash
        LlmAutoInstallAttemptedHash = $null
        LlmMode = 'external'
        UseLocalLlm = $false
        ManageLocalLlmProcess = $false
        AutoStartOnConnect = $false
        EagerLoad = $false
        LlamaExePath = ''
        ModelPath = ''
        Host = '127.0.0.1'
        Port = 1234
        ModelId = 'qwen3-4b-instruct-2507'
        ExtraArgs = ''
        UbatchSize = 128
        ThreadsBatch = 4
        FlashAttn = $null
        QualifiedProfile = $null
        StartupTimeoutSeconds = 10
        LlmTemperature = 0.1
        LlmMaxOutputTokens = 900
        RagQualityPreset = 'deep'
        LastSessionId = $sessionId.ToString('D')
    }
    $testSettings | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $clientSettingsPath -Encoding utf8

    $existingSecure = if ($clientSecureExisted) {
        Get-Content -LiteralPath $clientSecurePath -Raw | ConvertFrom-Json
    } else { $null }
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7')
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($serverApiKey)
    try {
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $protectedServerApiKey = [Convert]::ToBase64String($protectedBytes)
        [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
    }
    finally {
        [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    }
    $testSecure = [ordered]@{
        UserId = $userId
        ServerApiKeyProtected = $protectedServerApiKey
        LegacyApiKeyPlain = $null
        OpenAiApiKeyProtected = Get-OptionalPropertyValue `
            -InputObject $existingSecure `
            -Name 'OpenAiApiKeyProtected'
        RunPodApiKeyProtected = Get-OptionalPropertyValue `
            -InputObject $existingSecure `
            -Name 'RunPodApiKeyProtected'
    }
    $testSecure | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $clientSecurePath -Encoding utf8
    Set-Content -LiteralPath $clientLogPath -Value '' -Encoding utf8

    $firstClientProcess = Start-TestClient `
        -ExecutablePath $clientExecutable `
        -WorkingDirectory $clientProjectRoot
    $firstObservation = Wait-ForClientResume `
        -LogPath $clientLogPath `
        -JobId $jobId `
        -Process $firstClientProcess `
        -MinimumResumeCount 1 `
        -MinimumStartCount 1 `
        -TimeoutSeconds $ClientObservationTimeoutSeconds
    $firstClientClosedGracefully = Stop-OwnedProcess `
        -Process $firstClientProcess -PreferGraceful

    $jobAfterFirstClose = Invoke-RestMethod `
        -Method Get `
        -Uri "$baseUrl/advanced-analysis/jobs/$($jobId.ToString('D'))?userId=$([Uri]::EscapeDataString($userId))" `
        -Headers $headers
    if ($jobAfterFirstClose.status -ne 'queued' -or $jobAfterFirstClose.cancelRequested) {
        throw "Closing the first WinUI process changed or canceled the durable server job."
    }

    $resumeCountBeforeSecondStart = [int]$firstObservation.resumeCount
    $secondClientProcess = Start-TestClient `
        -ExecutablePath $clientExecutable `
        -WorkingDirectory $clientProjectRoot
    $secondObservation = Wait-ForClientResume `
        -LogPath $clientLogPath `
        -JobId $jobId `
        -Process $secondClientProcess `
        -MinimumResumeCount ($resumeCountBeforeSecondStart + 1) `
        -MinimumStartCount 2 `
        -TimeoutSeconds $ClientObservationTimeoutSeconds
    $secondClientClosedGracefully = Stop-OwnedProcess `
        -Process $secondClientProcess -PreferGraceful

    $jobAfterSecondClose = Invoke-RestMethod `
        -Method Get `
        -Uri "$baseUrl/advanced-analysis/jobs/$($jobId.ToString('D'))?userId=$([Uri]::EscapeDataString($userId))" `
        -Headers $headers
    if ($jobAfterSecondClose.status -ne 'queued' -or $jobAfterSecondClose.cancelRequested) {
        throw "Closing the restarted WinUI process changed or canceled the durable server job."
    }

    $clientLogText = Get-Content -LiteralPath $clientLogPath -Raw
    if ($clientLogText.Contains($serverApiKey, [StringComparison]::Ordinal)) {
        throw "The WinUI log unexpectedly contains the server API key."
    }
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'client-lifecycle.log') `
        -Value $clientLogText -Encoding utf8

    [ordered]@{
        schemaVersion = 'saaia-advanced-winui-restart-result.v1'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit = $repositoryCommit
        jobId = $jobId
        handoffId = $handoffId
        sessionId = $sessionId
        messageId = $message.messageId
        firstLaunch = $firstObservation
        firstWindowClosedGracefully = $firstClientClosedGracefully
        jobAfterFirstClose = [ordered]@{
            status = $jobAfterFirstClose.status
            revision = $jobAfterFirstClose.revision
            cancelRequested = $jobAfterFirstClose.cancelRequested
        }
        secondLaunch = $secondObservation
        secondWindowClosedGracefully = $secondClientClosedGracefully
        jobAfterSecondClose = [ordered]@{
            status = $jobAfterSecondClose.status
            revision = $jobAfterSecondClose.revision
            cancelRequested = $jobAfterSecondClose.cancelRequested
        }
        provider = 'disabled'
        providerCallCount = 0
        verdict = if ($firstClientClosedGracefully -and $secondClientClosedGracefully) {
            'PASS_REAL_WINUI_RESTART_RESUME'
        } else {
            'PASS_REAL_WINUI_PROCESS_RESTART_RESUME_CLOSE_FALLBACK_USED'
        }
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'result.v1.json') -Encoding utf8
    $completed = $true
}
catch {
    $failure = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
    throw
}
finally {
    $secondClientClosedGracefully = Stop-OwnedProcess `
        -Process $secondClientProcess -PreferGraceful
    $firstClientClosedGracefully = Stop-OwnedProcess `
        -Process $firstClientProcess -PreferGraceful

    if ($backendProcess -and -not $backendProcess.HasExited) {
        if ($jobId -ne [Guid]::Empty -and $sessionId -ne [Guid]::Empty) {
            try {
                Invoke-RestMethod `
                    -Method Post `
                    -Uri "$baseUrl/advanced-analysis/jobs/$($jobId.ToString('D'))/cancel?userId=$([Uri]::EscapeDataString($userId))" `
                    -Headers $headers | Out-Null
            }
            catch {}
        }
        if ($sessionId -ne [Guid]::Empty) {
            try {
                Invoke-RestMethod `
                    -Method Delete `
                    -Uri "$baseUrl/chat/sessions/$($sessionId.ToString('D'))?userId=$([Uri]::EscapeDataString($userId))" `
                    -Headers $headers | Out-Null
            }
            catch {}
        }
    }

    Stop-OwnedProcess -Process $backendProcess | Out-Null

    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousEnvironment[$name],
            'Process')
    }

    if ($localConfigExisted) {
        [System.IO.File]::WriteAllBytes($localConfigPath, $localConfigBackup)
    }
    elseif (Test-Path -LiteralPath $localConfigPath) {
        Remove-Item -LiteralPath $localConfigPath -Force
    }
    if ($clientSettingsExisted) {
        [System.IO.File]::WriteAllBytes($clientSettingsPath, $clientSettingsBackup)
    }
    elseif (Test-Path -LiteralPath $clientSettingsPath) {
        Remove-Item -LiteralPath $clientSettingsPath -Force
    }
    if ($clientSecureExisted) {
        [System.IO.File]::WriteAllBytes($clientSecurePath, $clientSecureBackup)
    }
    elseif (Test-Path -LiteralPath $clientSecurePath) {
        Remove-Item -LiteralPath $clientSecurePath -Force
    }
    if ($clientLogExisted) {
        [System.IO.File]::WriteAllBytes($clientLogPath, $clientLogBackup)
    }
    elseif (Test-Path -LiteralPath $clientLogPath) {
        Remove-Item -LiteralPath $clientLogPath -Force
    }

    $serverApiKey = $null
    $postgresPassword = $null
    $authPepper = $null
    $qdrantApiKey = $null

    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        completed = $completed
        failure = $failure
        providerCallCount = 0
        externalContentTransmitted = $false
        temporaryBackendStopped = $(
            $null -eq $backendProcess -or $backendProcess.HasExited)
        firstClientStopped = $(
            $null -eq $firstClientProcess -or $firstClientProcess.HasExited)
        secondClientStopped = $(
            $null -eq $secondClientProcess -or $secondClientProcess.HasExited)
        localConfigRestored = $true
        userSettingsRestored = $true
        userSecureStoreRestored = $true
        userLogRestored = $true
        backendPortListenersAfterRun = @(
            Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count
        localLlmPortListenersAfterRun = @(
            Get-NetTCPConnection -State Listen -LocalPort 1234 -ErrorAction SilentlyContinue).Count
        winUiProcessesAfterRun = @(
            Get-Process -Name 'SAAIA.Client.WinUI' -ErrorAction SilentlyContinue).Count
        secretPersistedInArtifact = $false
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'resource-shutdown.json') -Encoding utf8
}

Write-Output "Advanced WinUI restart-resume proof completed. Artifact: $ArtifactDirectory"
