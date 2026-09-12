[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [Parameter(Mandatory = $true)]
    [string]$ResultExportPath,
    [Parameter(Mandatory = $true)]
    [string]$SourceDocumentsRoot,
    [ValidateRange(1, 1000)]
    [int]$ResultJobIndex = 1,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [ValidateRange(10, 180)]
    [int]$ClientObservationTimeoutSeconds = 60,
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ("System.Security.Cryptography.ProtectedData" -as [type])) {
    Add-Type -AssemblyName System.Security
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

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

function Start-TestClient {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$DocumentsRoot
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.Environment['SAAIA_LLM_PROVIDER_MODE'] = 'Local'
    $startInfo.Environment['SAAIA_LLM_EXTERNAL_POLICY'] = 'ProductionLocal'
    $startInfo.Environment['SAAIA_DOCUMENTS_ROOT'] = $DocumentsRoot
    return [System.Diagnostics.Process]::Start($startInfo)
}

function Get-WindowElement {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $Process.Id)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-OpenButtons {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $buttonCondition)
    $matches = @()
    foreach ($button in $buttons) {
        $name = [string]$button.Current.Name
        if ($name -in @('Open', 'Ouvrir', 'Abrir', 'Oeffnen', 'Öffnen', 'Apri')) {
            $matches += $button
        }
    }
    return @($matches)
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $bounds = $Window.Current.BoundingRectangle
    $left = [Math]::Max(0, [int][Math]::Floor($bounds.Left))
    $top = [Math]::Max(0, [int][Math]::Floor($bounds.Top))
    $width = [Math]::Max(1, [int][Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($bounds.Height))
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Wait-ForTerminalSourceUi {
    param(
        [Parameter(Mandatory = $true)][string]$ClientLogPath,
        [Parameter(Mandatory = $true)][Guid]$JobId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1, 100)][int]$ExpectedSourceCount,
        [ValidateRange(10, 180)][int]$TimeoutSeconds
    )

    $terminalPattern = [regex]::Escape(
        "Advanced analysis snapshot persisted: jobId=$($JobId.ToString('D'))|status=succeeded")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "The WinUI client exited before rendering the terminal source cards (exit code $($Process.ExitCode))."
        }
        if (Test-Path -LiteralPath $ClientLogPath -PathType Leaf) {
            $clientText = Get-Content -LiteralPath $ClientLogPath -Raw
            if ($clientText -match $terminalPattern) {
                $window = Get-WindowElement -Process $Process
                if ($null -ne $window) {
                    $buttons = @(Find-OpenButtons -Window $window)
                    if ($buttons.Count -ge $ExpectedSourceCount) {
                        return [ordered]@{
                            window = $window
                            openButtons = $buttons
                            observedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                        }
                    }
                }
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The terminal advanced-analysis source cards were not visible within $TimeoutSeconds seconds."
}

function Wait-ForSourceOpenSuccess {
    param(
        [Parameter(Mandatory = $true)][string]$ClientLogPath,
        [Parameter(Mandatory = $true)][string]$RevisionId,
        [Parameter(Mandatory = $true)][string]$ChunkId,
        [ValidateRange(5, 60)][int]$TimeoutSeconds = 20
    )

    $successPattern = [regex]::Escape(
        "DocumentLauncher.Open succeeded; revision=$RevisionId; chunk=$ChunkId; exactHashRequired=True")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $ClientLogPath -PathType Leaf) {
            $text = Get-Content -LiteralPath $ClientLogPath -Raw
            if ($text -match $successPattern) {
                return [DateTimeOffset]::UtcNow.ToString('o')
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "The exact source did not report a successful open within $TimeoutSeconds seconds."
}

function Invoke-TerminalJobUpdate {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][Guid]$JobId,
        [Parameter(Mandatory = $true)][string]$ResultJson,
        [Parameter(Mandatory = $true)][string]$ProviderKey,
        [Parameter(Mandatory = $true)][string]$ProviderModel
    )

    $projectPath = Join-Path $WorkingDirectory 'TerminalJobUpdate.csproj'
    $programPath = Join-Path $WorkingDirectory 'Program.cs'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Npgsql" Version="8.0.3" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $projectPath -Encoding utf8
    @'
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("SAAIA_WINUI_PROOF_DB")
    ?? throw new InvalidOperationException("Database connection is missing.");
if (args.Length != 4) throw new ArgumentException("Expected job id, result path, provider key and model.");
var jobId = Guid.Parse(args[0]);
var resultJson = await File.ReadAllTextAsync(args[1]);
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await using var command = new NpgsqlCommand("""
    UPDATE advanced_analysis_jobs
    SET status='succeeded', revision=revision+1, attempt_count=1,
        provider_key=@provider_key, provider_model=@provider_model,
        result=CAST(@result AS jsonb), last_error_code=NULL,
        started_at=COALESCE(started_at, now()), finished_at=now(), updated_at=now(),
        lease_owner=NULL, lease_expires_at=NULL
    WHERE job_id=@job_id AND status='queued' AND cancel_requested_at IS NULL;
    """, connection);
command.Parameters.AddWithValue("job_id", jobId);
command.Parameters.AddWithValue("provider_key", args[2]);
command.Parameters.AddWithValue("provider_model", args[3]);
command.Parameters.AddWithValue("result", resultJson);
var changed = await command.ExecuteNonQueryAsync();
if (changed != 1) throw new InvalidOperationException($"Expected one queued job, changed {changed}.");
Console.WriteLine("Terminal test job updated.");
'@ | Set-Content -LiteralPath $programPath -Encoding utf8

    $resultPath = Join-Path $WorkingDirectory 'accepted-result.json'
    Set-Content -LiteralPath $resultPath -Value $ResultJson -Encoding utf8
    $previousConnection = [Environment]::GetEnvironmentVariable(
        'SAAIA_WINUI_PROOF_DB', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'SAAIA_WINUI_PROOF_DB', $ConnectionString, 'Process')
        $output = & dotnet run --project $projectPath -c Release -- `
            $JobId.ToString('D') $resultPath $ProviderKey $ProviderModel 2>&1
        $exitCode = $LASTEXITCODE
        $output | Set-Content -LiteralPath (
            Join-Path $WorkingDirectory 'terminal-job-update.log') -Encoding utf8
        if ($exitCode -ne 0) {
            throw "Terminal job update failed with exit code $exitCode."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'SAAIA_WINUI_PROOF_DB', $previousConnection, 'Process')
        $ConnectionString = $null
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve the repository commit for the WinUI terminal-source seal."
}
$repositoryTrackedDirty = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
if ($repositoryTrackedDirty) {
    throw "The WinUI terminal-source proof requires a clean tracked worktree."
}

$ServerEnvPath = [System.IO.Path]::GetFullPath($ServerEnvPath)
$ResultExportPath = [System.IO.Path]::GetFullPath($ResultExportPath)
$SourceDocumentsRoot = [System.IO.Path]::GetFullPath($SourceDocumentsRoot)
foreach ($requiredPath in @($ServerEnvPath, $ResultExportPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required file not found: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $SourceDocumentsRoot -PathType Container)) {
    throw "Source documents root not found: $SourceDocumentsRoot"
}
if (@(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Port $BackendPort is already in use."
}
if (@(Get-Process -Name 'SAAIA.Client.WinUI' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "A WinUI client is already running; refusing to disturb the user's process."
}

$export = Get-Content -LiteralPath $ResultExportPath -Raw | ConvertFrom-Json
$jobs = @($export.jobs)
if ($ResultJobIndex -gt $jobs.Count) {
    throw "ResultJobIndex $ResultJobIndex exceeds the $($jobs.Count) exported jobs."
}
$sourceJob = $jobs[$ResultJobIndex - 1]
if ($sourceJob.status -ne 'succeeded' -or $null -eq $sourceJob.result) {
    throw "The selected exported job is not a successful terminal result."
}
$acceptedResult = $sourceJob.result
$providerKey = [string]$acceptedResult.providerKey
$providerModel = [string]$acceptedResult.providerModel
$evidence = @($acceptedResult.evidence)
if ($evidence.Count -eq 0) {
    throw "The selected exported result has no evidence."
}

$sourceChecks = @()
foreach ($item in $evidence) {
    $relativePath = ([string]$item.docPath).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $sourcePath = Join-Path $SourceDocumentsRoot $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Exact source file is missing below SourceDocumentsRoot: $relativePath"
    }
    $observedHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$item.sourceHash).ToLowerInvariant()
    if ($observedHash -ne $expectedHash) {
        throw "Exact source hash mismatch for $relativePath"
    }
    $sourceChecks += [ordered]@{
        docPath = [string]$item.docPath
        pageStart = [int]$item.pageStart
        sourceHash = $expectedHash
        exactHashMatch = $true
    }
}

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactDirectory = Join-Path $repositoryRoot (
        "artifacts\reprise-pc-20260908\a763-winui-terminal-sources-$stamp")
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $ArtifactDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactDirectory must stay below the repository artifacts directory."
}
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null

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
$backendStdout = Join-Path $ArtifactDirectory 'backend.stdout.log'
$backendStderr = Join-Path $ArtifactDirectory 'backend.stderr.log'
$unsignedConfigPath = Join-Path $ArtifactDirectory 'unsigned-development.config.json'
$missingSignaturePath = Join-Path $ArtifactDirectory 'unsigned-development.config.missing.sig'
$baseUrl = "http://127.0.0.1:$BackendPort"

$serverEnvironment = Read-EnvFile -Path $ServerEnvPath
$postgresDatabase = Require-EnvValue $serverEnvironment 'POSTGRES_DB'
$postgresUser = Require-EnvValue $serverEnvironment 'POSTGRES_USER'
$postgresPassword = Require-EnvValue $serverEnvironment 'POSTGRES_PASSWORD'
$authPepper = Require-EnvValue $serverEnvironment 'SAAIA_AUTH_PEPPER'
$serverApiKey = Require-EnvValue $serverEnvironment 'SAAIA_BOOTSTRAP_API_KEY'
$qdrantApiKey = Require-EnvValue $serverEnvironment 'QDRANT_API_KEY'
$teiModel = Require-EnvValue $serverEnvironment 'TEI_MODEL_ID'
$headers = @{ 'X-Api-Key' = $serverApiKey }

$localConfigExisted = Test-Path -LiteralPath $localConfigPath -PathType Leaf
$localConfigBackup = if ($localConfigExisted) { [System.IO.File]::ReadAllBytes($localConfigPath) } else { $null }
$clientSettingsExisted = Test-Path -LiteralPath $clientSettingsPath -PathType Leaf
$clientSettingsBackup = if ($clientSettingsExisted) { [System.IO.File]::ReadAllBytes($clientSettingsPath) } else { $null }
$clientSecureExisted = Test-Path -LiteralPath $clientSecurePath -PathType Leaf
$clientSecureBackup = if ($clientSecureExisted) { [System.IO.File]::ReadAllBytes($clientSecurePath) } else { $null }
$clientLogExisted = Test-Path -LiteralPath $clientLogPath -PathType Leaf
$clientLogBackup = if ($clientLogExisted) { [System.IO.File]::ReadAllBytes($clientLogPath) } else { $null }

$trackedEnvironment = @(
    'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT',
    'ConfigSignature__ConfigPath', 'ConfigSignature__SignaturePath',
    'ConfigSignature__AllowUnsignedInDevelopment', 'QDRANT_API_KEY')
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$backendProcess = $null
$clientProcess = $null
$sessionId = [Guid]::Empty
$jobId = [Guid]::Empty
$messageId = [Guid]::Empty
$handoffId = [Guid]::Empty
$userId = ''
$clientClosedGracefully = $false
$clientLogSecretRedacted = $false
$failure = $null
$completed = $false

try {
    $referenceHeaders = @{ 'X-Admin-Key' = $serverApiKey }
    $referenceJobsResponse = Invoke-WebRequest `
        -Uri ($ReferenceBackendUrl.TrimEnd('/') + '/ingestion/jobs') `
        -Headers $referenceHeaders -UseBasicParsing -TimeoutSec 15
    $referenceJobs = @((($referenceJobsResponse.Content | ConvertFrom-Json).items))
    if (@($referenceJobs | Where-Object { $_.status -in @('queued', 'running') }).Count -gt 0) {
        throw "Reference backend reports active ingestion jobs; refusing to start a second backend."
    }

    (& dotnet build $backendProject -c $Configuration --no-restore 2>&1) |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'backend-build.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed with exit code $LASTEXITCODE." }
    (& dotnet build $clientProject -c $Configuration -p:Platform=$Platform --no-restore 2>&1) |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'client-build.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "WinUI build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $clientExecutable -PathType Leaf)) {
        throw "WinUI executable not found after build: $clientExecutable"
    }

    $connectionString = "Host=saaia-server;Port=5432;Database=$postgresDatabase;Username=$postgresUser;Password=$postgresPassword;Pooling=true;Maximum Pool Size=20"
    $localConfig = [ordered]@{
        Database = [ordered]@{ ConnectionString = $connectionString }
        Auth = [ordered]@{ ApiKeyHeaderName='X-Api-Key'; AdminKeyHeaderName='X-Admin-Key'; Pepper=$authPepper }
        Bootstrap = [ordered]@{ Enabled = $false }
        Rag = [ordered]@{
            QdrantBaseUrl='http://saaia-server:6333'; QdrantCollection='knowledge_base'
            QdrantApiKeyRef='ENV:QDRANT_API_KEY'; RequireQdrantAuthInProd=$true
            EmbeddingsBaseUrl='http://saaia-server:8081'; EmbeddingsModel=$teiModel
            EnableRerank=$false; RerankBaseUrl=''
        }
        Ingestion = [ordered]@{
            DocumentsRoot=(Join-Path $ArtifactDirectory 'empty-documents')
            WorkerEnabled=$false; ScannerEnabled=$false; WatcherEnabled=$false
        }
        CatalogSnapshot = [ordered]@{ Enabled = $false }
        RuntimeGovernance = [ordered]@{
            CapabilityBWorkerEnabled=$false
            CapabilityBAutoEnqueueWhenIngestionIdleEnabled=$false
        }
        License = [ordered]@{ Seats=1; AdvancedAnalysisEnabled=$true }
        AdvancedAnalysis = [ordered]@{
            Provider='disabled'; WorkerEnabled=$false; RetentionDays=1; MaximumQueuedJobsPerUser=4
        }
        OpenTelemetry = [ordered]@{ Enabled=$false }
        Logging = [ordered]@{ LogLevel=[ordered]@{ Default='Information'; 'Microsoft.AspNetCore'='Warning' } }
    }
    New-Item -ItemType Directory -Path $localConfig.Ingestion.DocumentsRoot -Force | Out-Null
    $localConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $localConfigPath -Encoding utf8

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    '{}' | Set-Content -LiteralPath $unsignedConfigPath -Encoding utf8
    $env:ConfigSignature__ConfigPath = $unsignedConfigPath
    $env:ConfigSignature__SignaturePath = $missingSignaturePath
    $env:ConfigSignature__AllowUnsignedInDevelopment = 'true'
    $env:QDRANT_API_KEY = $qdrantApiKey

    [ordered]@{
        schemaVersion='saaia-advanced-winui-terminal-sources-preflight.v1'
        startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit=$repositoryCommit
        repositoryTrackedDirty=$repositoryTrackedDirty
        selectedExportJobId=[string]$sourceJob.jobId
        resultJobIndex=$ResultJobIndex
        sourceChecks=$sourceChecks
        sourceCount=$evidence.Count
        provider=$providerKey
        providerModel=$providerModel
        providerCallCount=0
        externalContentTransmitted=$false
        productStatus='TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'preflight-seal.json') -Encoding utf8

    $arguments = @('run','--project',('"' + $backendProject + '"'),'-c',$Configuration,'--no-build','--no-restore','--','--urls',$baseUrl)
    $backendProcess = Start-Process -FilePath 'dotnet' -ArgumentList $arguments `
        -WorkingDirectory $repositoryRoot -NoNewWindow `
        -RedirectStandardOutput $backendStdout -RedirectStandardError $backendStderr -PassThru
    $ready = Wait-BackendReady -BaseUrl $baseUrl -Process $backendProcess
    $ready | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'backend-ready.json') -Encoding utf8

    $userId = [Guid]::NewGuid().ToString('D')
    $sessionBody = [ordered]@{userId=$userId;title='A763 WinUI terminal sources proof';clientUser='automated-validation'} | ConvertTo-Json -Compress
    $session = Invoke-RestMethod -Method Post -Uri "$baseUrl/chat/sessions" -Headers $headers -ContentType 'application/json' -Body $sessionBody
    $sessionId = [Guid]$session.sessionId
    $handoffId = [Guid]::NewGuid()
    $jobBody = [ordered]@{
        userId=$userId; sessionId=$sessionId
        handoff=[ordered]@{
            schemaVersion='saaia.advanced-analysis-handoff.v1';handoffId=$handoffId
            createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
            requestText='Render and open the exact sources of an accepted advanced result.'
            language='fr';originIntent='rag.answer';reasonCode='winui_terminal_sources_validation';transferStage='before_retrieval'
            load=[ordered]@{
                planKind='validation';deliverable='accepted_terminal_sources';answerUnitCount=@($acceptedResult.claims).Count
                atomicEvidenceCount=$evidence.Count;rowCount=0;columnCount=0;structuredLayout=$false
                atomicEvidenceType='content_claim';atomicEvidenceMode='content_claim';selectionPolicy='source_backed'
                questionFocus='terminal source cards';requestedDocumentName='';boundedNamedDocumentExtraction=$false
                candidateScopePaths=@();rowLabels=@();columns=@()
            }
            researchState=[ordered]@{memoryIsEvidence=$false;evidenceRevalidationRequired=$true;executedTools=@();executedQueries=@();attempts=@();evidenceReferences=@()}
            dataPolicy=[ordered]@{externalProviderContentAuthorized=$false;externalProviderMetadataAuthorized=$false;authorizationSource='replay_of_sealed_local_result'}
        }
    } | ConvertTo-Json -Depth 12 -Compress
    $job = Invoke-RestMethod -Method Post -Uri "$baseUrl/advanced-analysis/jobs" -Headers $headers -ContentType 'application/json' -Body $jobBody
    $jobId = [Guid]$job.jobId
    if ($job.status -ne 'queued' -or [int]$job.revision -ne 1) { throw 'The test job was not durably queued at revision 1.' }

    $sources = [ordered]@{
        intent='advanced_analysis.answer'; sources=@()
        advancedAnalysis=[ordered]@{
            schemaVersion='saaia.advanced-analysis-client-state.v1';jobId=$jobId;handoffId=$handoffId;sessionId=$sessionId
            status='queued';revision=1;attemptCount=0;cancelRequested=$false
            updatedAtUtc=$job.updatedAtUtc;expiresAtUtc=$job.expiresAtUtc
            providerKey=$null;providerModel=$null;providerCallCount=$null;claimCount=$null;evidenceCount=$null
            inputTokens=$null;outputTokens=$null;cachedInputTokens=$null;estimatedCostUsd=$null
            resultOutcome='job_created';lastErrorCode=$null
        }
    }
    $messageBody = [ordered]@{
        userId=$userId;role='assistant';content='L analyse avancee est en attente sur le serveur.'
        sourcesJson=($sources | ConvertTo-Json -Depth 12 -Compress)
        statusNote='En attente';progressText='Attente du resultat serveur';trackingMetaJson=$null
    } | ConvertTo-Json -Depth 5 -Compress
    $message = Invoke-RestMethod -Method Post -Uri "$baseUrl/chat/sessions/$($sessionId.ToString('D'))/messages" `
        -Headers $headers -ContentType 'application/json' -Body $messageBody
    $messageId = [Guid]$message.messageId

    Invoke-TerminalJobUpdate -RepositoryRoot $repositoryRoot -WorkingDirectory $ArtifactDirectory `
        -ConnectionString $connectionString -JobId $jobId `
        -ResultJson ($acceptedResult | ConvertTo-Json -Depth 30 -Compress) `
        -ProviderKey $providerKey -ProviderModel $providerModel

    $provisioningHash = $null
    $provisioningCandidates = @(
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'SAAIA\provisioning.json'),
        (Join-Path $env:LOCALAPPDATA 'SAAIA\provisioning.json'))
    $provisioningPath = $provisioningCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($provisioningPath) {
        $provisioningText = Get-Content -LiteralPath $provisioningPath -Raw
        $hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($provisioningText))
        $provisioningHash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $clientSettingsPath) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $clientLogPath) -Force | Out-Null
    [ordered]@{
        BackendUrl=$baseUrl;BackendUrlAlternates='';ShowAdvancedUi=$false;AutoConnect=$true
        UiLanguage='fr';UiTheme='dark';ProvisioningHash=$provisioningHash;LlmAutoInstallAttemptedHash=$null
        LlmMode='external';UseLocalLlm=$false;ManageLocalLlmProcess=$false;AutoStartOnConnect=$false;EagerLoad=$false
        LlamaExePath='';ModelPath='';Host='127.0.0.1';Port=1234;ModelId='qwen3-4b-instruct-2507';ExtraArgs=''
        UbatchSize=128;ThreadsBatch=4;FlashAttn=$null;QualifiedProfile=$null;StartupTimeoutSeconds=10
        LlmTemperature=0.1;LlmMaxOutputTokens=900;RagQualityPreset='deep';LastSessionId=$sessionId.ToString('D')
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $clientSettingsPath -Encoding utf8

    $existingSecure = if ($clientSecureExisted) { Get-Content -LiteralPath $clientSecurePath -Raw | ConvertFrom-Json } else { $null }
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7')
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($serverApiKey)
    try {
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainBytes, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $protectedServerApiKey = [Convert]::ToBase64String($protectedBytes)
        [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
    }
    finally { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    [ordered]@{
        UserId=$userId;ServerApiKeyProtected=$protectedServerApiKey;LegacyApiKeyPlain=$null
        OpenAiApiKeyProtected=Get-OptionalPropertyValue -InputObject $existingSecure -Name 'OpenAiApiKeyProtected'
        RunPodApiKeyProtected=Get-OptionalPropertyValue -InputObject $existingSecure -Name 'RunPodApiKeyProtected'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $clientSecurePath -Encoding utf8
    Set-Content -LiteralPath $clientLogPath -Value '' -Encoding utf8

    $clientProcess = Start-TestClient -ExecutablePath $clientExecutable -WorkingDirectory $clientProjectRoot -DocumentsRoot $SourceDocumentsRoot
    $observation = Wait-ForTerminalSourceUi -ClientLogPath $clientLogPath -JobId $jobId `
        -Process $clientProcess -ExpectedSourceCount $evidence.Count -TimeoutSeconds $ClientObservationTimeoutSeconds
    $window = $observation.window
    $openButtons = @($observation.openButtons)
    Save-WindowScreenshot -Window $window -Path (Join-Path $ArtifactDirectory 'terminal-source-cards.png')

    $firstEvidence = $evidence[0]
    $invokePattern = $openButtons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$invokePattern).Invoke()
    $sourceOpenedAtUtc = Wait-ForSourceOpenSuccess -ClientLogPath $clientLogPath `
        -RevisionId ([string]$firstEvidence.revisionId) -ChunkId ([string]$firstEvidence.chunkId)

    $clientLogText = Get-Content -LiteralPath $clientLogPath -Raw
    if ($clientLogText.Contains($serverApiKey, [StringComparison]::Ordinal)) {
        throw 'The WinUI log unexpectedly contains the server API key.'
    }
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'client-terminal-sources.log') -Value $clientLogText -Encoding utf8

    $clientClosedGracefully = Stop-OwnedProcess -Process $clientProcess -PreferGraceful
    [ordered]@{
        schemaVersion='saaia-advanced-winui-terminal-sources-result.v1'
        completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit=$repositoryCommit
        sourceResultJobId=[string]$sourceJob.jobId
        replayJobId=$jobId;sessionId=$sessionId;messageId=$messageId
        providerKey=$providerKey;providerModel=$providerModel
        sourceCount=$evidence.Count;visibleOpenButtonCount=$openButtons.Count
        terminalSourcesObservedAtUtc=$observation.observedAtUtc
        exactSourceOpenedAtUtc=$sourceOpenedAtUtc
        openedEvidence=[ordered]@{
            evidenceId=[string]$firstEvidence.evidenceId;docId=[string]$firstEvidence.docId
            revisionId=[string]$firstEvidence.revisionId;chunkId=[string]$firstEvidence.chunkId
            docPath=[string]$firstEvidence.docPath;pageStart=[int]$firstEvidence.pageStart
            sourceHash=[string]$firstEvidence.sourceHash;exactHashMatch=$true
        }
        providerCallCount=0;externalContentTransmitted=$false
        verdict='PASS_REAL_WINUI_TERMINAL_SOURCE_CARDS_AND_EXACT_OPEN'
        productStatus='TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'result.v1.json') -Encoding utf8
    $completed = $true
}
catch {
    $failure = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
    throw
}
finally {
    $clientClosedGracefully = Stop-OwnedProcess -Process $clientProcess -PreferGraceful
    if ($backendProcess -and -not $backendProcess.HasExited) {
        if ($sessionId -ne [Guid]::Empty) {
            try {
                Invoke-RestMethod -Method Delete `
                    -Uri "$baseUrl/chat/sessions/$($sessionId.ToString('D'))?userId=$([Uri]::EscapeDataString($userId))" `
                    -Headers $headers | Out-Null
            }
            catch {}
        }
    }
    Stop-OwnedProcess -Process $backendProcess | Out-Null

    if (Test-Path -LiteralPath $clientLogPath -PathType Leaf) {
        $diagnosticLog = Get-Content -LiteralPath $clientLogPath -Raw
        if (-not [string]::IsNullOrWhiteSpace($serverApiKey) -and $diagnosticLog.Contains($serverApiKey, [StringComparison]::Ordinal)) {
            $diagnosticLog = $diagnosticLog.Replace($serverApiKey, '[REDACTED_SERVER_API_KEY]', [StringComparison]::Ordinal)
            $clientLogSecretRedacted = $true
        }
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'client-terminal-sources-diagnostic.log') -Value $diagnosticLog -Encoding utf8
    }

    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    if ($localConfigExisted) { [System.IO.File]::WriteAllBytes($localConfigPath, $localConfigBackup) }
    elseif (Test-Path -LiteralPath $localConfigPath) { Remove-Item -LiteralPath $localConfigPath -Force }
    if ($clientSettingsExisted) { [System.IO.File]::WriteAllBytes($clientSettingsPath, $clientSettingsBackup) }
    elseif (Test-Path -LiteralPath $clientSettingsPath) { Remove-Item -LiteralPath $clientSettingsPath -Force }
    if ($clientSecureExisted) { [System.IO.File]::WriteAllBytes($clientSecurePath, $clientSecureBackup) }
    elseif (Test-Path -LiteralPath $clientSecurePath) { Remove-Item -LiteralPath $clientSecurePath -Force }
    if ($clientLogExisted) { [System.IO.File]::WriteAllBytes($clientLogPath, $clientLogBackup) }
    elseif (Test-Path -LiteralPath $clientLogPath) { Remove-Item -LiteralPath $clientLogPath -Force }

    $serverApiKey=$null;$postgresPassword=$null;$authPepper=$null;$qdrantApiKey=$null
    [ordered]@{
        endedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');completed=$completed;failure=$failure
        providerCallCount=0;externalContentTransmitted=$false
        temporaryBackendStopped=($null -eq $backendProcess -or $backendProcess.HasExited)
        clientStopped=($null -eq $clientProcess -or $clientProcess.HasExited)
        localConfigRestored=$true;userSettingsRestored=$true;userSecureStoreRestored=$true;userLogRestored=$true
        backendPortListenersAfterRun=@(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue).Count
        localLlmPortListenersAfterRun=@(Get-NetTCPConnection -State Listen -LocalPort 1234 -ErrorAction SilentlyContinue).Count
        winUiProcessesAfterRun=@(Get-Process -Name 'SAAIA.Client.WinUI' -ErrorAction SilentlyContinue).Count
        secretPersistedInArtifact=$false;clientLogSecretRedacted=$clientLogSecretRedacted
        productStatus='TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'resource-shutdown.json') -Encoding utf8
}

Write-Output "Advanced WinUI terminal-source proof completed. Artifact: $ArtifactDirectory"
