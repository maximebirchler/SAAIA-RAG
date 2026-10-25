#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$BackendPort = 5127,
    [int]$FixturePort = 18087,
    [string]$PgBinPath = '',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new evidence directory; existing runs are never overwritten.' }
foreach ($port in @($BackendPort, $FixturePort)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is already in use." }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$runtime = Join-Path $output 'runtime'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$runId = [Guid]::NewGuid().ToString('N')
if (-not $PgBinPath) {
    $choices = @(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory -Filter 'Odoo*' | ForEach-Object {
        Join-Path $_.FullName 'PostgreSQL/bin'
    } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'postgres.exe') })
    if ($choices.Count -ne 1) { throw 'Specify PgBinPath; exactly one PostgreSQL installation is required.' }
    $PgBinPath = $choices[0]
}
$version = (& (Join-Path $PgBinPath 'postgres.exe') --version | Out-String).Trim()
if ($version -notmatch 'PostgreSQL\) 16\.') { throw 'PostgreSQL 16 is required.' }
$reserve = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$reserve.Start()
$pgPort = $reserve.LocalEndpoint.Port
$reserve.Stop()
$cluster = Join-Path $runtime 'postgres'
$data = Join-Path $cluster 'data'
[IO.Directory]::CreateDirectory($cluster) | Out-Null
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$apiKey = 'saaia_winui_' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$passwordFile = Join-Path $runtime 'bootstrap-password.txt'
[IO.File]::WriteAllText($passwordFile, $password)
$owner = [ordered]@{ schemaVersion = 'saaia-winui-local-runtime.v1'; runId = $runId; state = 'starting';
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); outputDirectory = $output; postgresDataDirectory = $data;
    postgresBinDirectory = $PgBinPath; pgPort = $pgPort; backendPort = $BackendPort; fixturePort = $FixturePort;
    backendUrl = "http://127.0.0.1:$BackendPort"; fixtureUrl = "http://127.0.0.1:$FixturePort";
    clientDataRoot = (Join-Path $runtime 'client-data'); documentsRoot = (Join-Path $runtime 'documents');
    processes = @(); externalCallsPossible = $false; existingDatabaseModified = $false;
    adapters = @('Real production backend', 'Real PostgreSQL16 isolated cluster', 'Deterministic loopback TEI/Qdrant/LLM fixtures');
    productStatus = 'TESTE_NON_APPROUVE' }
function Save-Owner { [IO.File]::WriteAllText((Join-Path $output 'environment.json'), ($owner | ConvertTo-Json -Depth 10)) }
function Start-Helper([string]$File, [string[]]$Arguments, [string]$Name, [hashtable]$Environment = @{}) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $runtime
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($environmentName in @($info.Environment.Keys)) {
        if ($environmentName -match 'OPENAI|RUNPOD|AZURE|SAAIA_LLM|SAAIA_ADVANCED|ConfigSignature|ConnectionString|QDRANT') {
            $info.Environment.Remove($environmentName) | Out-Null
        }
    }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key] = [string]$entry.Value }
    # A tiny worker redirects output to files, avoiding anonymous pipe lifetime after this script exits.
    $process = [Diagnostics.Process]::Start($info)
    $ownedPort = switch ($Name) { 'postgres' { $pgPort }; 'fixture-http' { $FixturePort }; 'backend-runner' { $BackendPort } }
    $owner.processes += @{ name = $Name; pid = $process.Id; path = $File; port = $ownedPort; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o') }
    Save-Owner
    return $process
}
Save-Owner
try {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repo 'backend/SAAIA.Backend/SAAIA.Backend.csproj') -c Release --no-restore *> (Join-Path $output 'backend-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed; inspect backend-build.log.' }
    }
    & (Join-Path $PgBinPath 'initdb.exe') -D $data --username=saaia_winui --encoding=UTF8 --locale=C --auth=scram-sha-256 "--pwfile=$passwordFile" *> (Join-Path $output 'initdb.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated initdb failed.' }
    Remove-Item -LiteralPath $passwordFile
    # postgres owns its dedicated log file and never opens a visible console.
    $pg = Start-Helper (Join-Path $PgBinPath 'postgres.exe') @('-D', $data, '-h', '127.0.0.1', '-p', "$pgPort", '-c', 'logging_collector=on', '-c', 'log_directory=log') 'postgres'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        & (Join-Path $PgBinPath 'pg_isready.exe') -h 127.0.0.1 -p $pgPort -U saaia_winui -d postgres *> $null
        if ($LASTEXITCODE -eq 0) { break }
        if ($pg.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'Owned PostgreSQL failed readiness.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $password
        $actual = (& (Join-Path $PgBinPath 'psql.exe') -w -h 127.0.0.1 -p $pgPort -U saaia_winui -d postgres -Atc "SELECT current_setting('data_directory');" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($data)) { throw 'Unexpected data_directory; refusing fixture.' }
    } finally { $env:PGPASSWORD = $previousPassword }
    $owner['verifiedDataDirectory'] = $true
    $control = Join-Path $runtime 'fixture-control.json'
    [IO.File]::WriteAllText($control, '{"mode":"success","delaySeconds":0,"chunkDelaySeconds":0.03}')
    $fixture = Start-Helper (Get-Command python).Source @((Join-Path $PSScriptRoot 'fixture_http.py'), '--port', "$FixturePort", '--control', $control, '--events', (Join-Path $output 'fixture-events.jsonl')) 'fixture-http'
    [IO.Directory]::CreateDirectory($owner.documentsRoot) | Out-Null
    [IO.Directory]::CreateDirectory($owner.clientDataRoot) | Out-Null
    $backendConfig = [ordered]@{
        Database = @{ ConnectionString = "Host=127.0.0.1;Port=$pgPort;Database=postgres;Username=saaia_winui;Password=$password;Pooling=true;Maximum Pool Size=20" }
        Auth = @{ Pepper = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) }
        Bootstrap = @{ Enabled = $true; TenantId = '77777777-7777-4777-8777-777777777777'; TenantName = 'Validation WinUI locale'; ApiKeyPlain = $apiKey }
        Rag = @{ QdrantBaseUrl = $owner.fixtureUrl; QdrantCollection = 'winui_fixture'; EmbeddingsBaseUrl = $owner.fixtureUrl; EmbeddingsModel = 'fixture-3d'; EnableRerank = $false; RequireQdrantAuthInProd = $false }
        Ingestion = @{ DocumentsRoot = $owner.documentsRoot; WorkerEnabled = $false; WatcherEnabled = $false; ScannerEnabled = $false }
        CatalogSnapshot = @{ Enabled = $false }
        RuntimeGovernance = @{ CapabilityBWorkerEnabled = $false; CapabilityBAutoEnqueueWhenIngestionIdleEnabled = $false }
        License = @{ Seats = 2; AdvancedAnalysisEnabled = $true }
        AdvancedAnalysis = @{ Provider = 'disabled'; WorkerEnabled = $false; AllowExternalProviderContent = $false; AllowExternalProviderMetadata = $false; RetentionDays = 1 }
        OpenTelemetry = @{ Enabled = $false }
        Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
    }
    [IO.File]::WriteAllText((Join-Path $runtime 'deployment.config.json'), ($backendConfig | ConvertTo-Json -Depth 10))
    # Keep synthetic credentials private and separate from the versionable environment manifest.
    [IO.File]::WriteAllText((Join-Path $runtime 'fixture-secrets.json'), (@{ apiKey = $apiKey; pgPassword = $password } | ConvertTo-Json))
    $backendDll = Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $backendDll) 'Db') -Destination (Join-Path $runtime 'Db') -Recurse
    $owner['backendAssemblySha256'] = (Get-FileHash -LiteralPath $backendDll -Algorithm SHA256).Hash
    # Logging goes to dedicated files through a managed child runner.
    $runnerSpec = @{ executable = (Get-Command dotnet).Source; arguments = @($backendDll, '--contentRoot', $runtime, '--urls', $owner.backendUrl);
        stdout = (Join-Path $output 'backend.stdout.log'); stderr = (Join-Path $output 'backend.stderr.log') }
    [IO.File]::WriteAllText((Join-Path $runtime 'backend-process.json'), ($runnerSpec | ConvertTo-Json))
    $backend = Start-Helper (Get-Process -Id $PID).Path @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-helper.ps1'), '-Specification', (Join-Path $runtime 'backend-process.json')) 'backend-runner' @{
        ASPNETCORE_ENVIRONMENT = 'Development'; DOTNET_ENVIRONMENT = 'Development';
        ConfigSignature__ConfigPath = (Join-Path $runtime 'deployment.config.json');
        ConfigSignature__SignaturePath = (Join-Path $runtime 'missing-development.sig');
        ConfigSignature__AllowUnsignedInDevelopment = 'true'
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        try {
            $ready = Invoke-RestMethod -Uri ($owner.backendUrl + '/ready') -TimeoutSec 5
            if ($ready.ok) { break }
        } catch { }
        if ($backend.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'Local backend readiness failed; inspect dedicated logs.' }
        Start-Sleep -Milliseconds 500
    } while ($true)
    [IO.File]::WriteAllText((Join-Path $output 'ready.json'), ($ready | ConvertTo-Json -Depth 12))
    $owner.state = 'ready'
    Save-Owner
    Write-Output "Isolated environment ready: $($owner.backendUrl). Client has not been started or configured."
} catch {
    $owner.state = 'failed'
    $owner['failureType'] = $_.Exception.GetType().Name
    Save-Owner
    throw
} finally {
    if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile }
}
