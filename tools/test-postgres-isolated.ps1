#requires -Version 7.4
[CmdletBinding()]
param (
    [string]$PgBinPath = '',
    [string]$OutputDirectory = '',
    [ValidateRange(0, 65535)][int]$Port = 0,
    [string]$Filter = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$NoRestore,
    [switch]$KeepCluster
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo "artifacts/postgres-isolated/$runId"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Choose a new output directory; existing evidence is never replaced.'
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
if (-not $PgBinPath) {
    $installed = @(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory -Filter 'Odoo*' | ForEach-Object {
            Join-Path $_.FullName 'PostgreSQL/bin'
        }
        | Where-Object {
            Test-Path -LiteralPath (Join-Path $_ 'postgres.exe')
        })
    if ($installed.Count -ne 1) {
        throw 'Provide PgBinPath explicitly; exactly one local PostgreSQL installation was not found.'
    }

    $PgBinPath = $installed[0]
}

$postgres = Join-Path $PgBinPath 'postgres.exe'
$version = (& $postgres --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'PostgreSQL\) 16\.') {
    throw 'This isolated integration fixture requires installed PostgreSQL 16 binaries.'
}

if ($Port -eq 0) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $Port = $listener.LocalEndpoint.Port
    $listener.Stop()
}

$clusterParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'SAAIA/isolated-integration-postgres'))
$cluster = [IO.Path]::GetFullPath((Join-Path $clusterParent "run-$runId"))
if (-not $cluster.StartsWith($clusterParent+[IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Owned cluster must stay inside the dedicated fixture parent.'
}

if (Test-Path -LiteralPath $cluster) {
    throw 'Owned cluster already exists.'
}

[IO.Directory]::CreateDirectory($cluster) | Out-Null
[IO.File]::WriteAllText((Join-Path $cluster 'owner.json'), (@{
            runId = $runId
            purpose = 'SAAIA isolated integration fixture'
        }
        | ConvertTo-Json))
$data = Join-Path $cluster 'data'
$passwordFile = Join-Path $cluster 'bootstrap-password.txt'
$secret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
[IO.File]::WriteAllText($passwordFile, $secret, [Text.UTF8Encoding]::new($false))
$old = @{}

foreach ($name in @('SAAIA_TEST_PG_CONN', 'PGPASSWORD', 'SAAIA_TEST_CONTRACT_EXPORT_DIR')) {
    $old[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$managed = $null
$exitCode = 1
$failure = $null
$verified = $false
$clusterRemoved = $false
$manifest = [ordered]@{
    schemaVersion = 'saaia-isolated-postgres-v1'
    runId = $runId
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    finishedAtUtc = $null
    gitCommit = (& git -C $repo rev-parse HEAD)
    postgresVersion = $version
    postgresBinarySha256 = (Get-FileHash -LiteralPath $postgres -Algorithm SHA256).Hash
    port = $Port
    ownedCluster = $data
    verifiedDataDirectory = $false
    existingClusterModified = $false
    externalCorpusAccess = $false
    state = 'preparing'
    failure = $null
    environmentRestored = $false
    clusterRemoved = $false
    bootstrapPasswordRemoved = $false
    remainingListener = $null
}

function Start-OwnedProcess([string]$File, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($start)
    return @{
        process = $process
        stdout = $process.StandardOutput.ReadToEndAsync()
        stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Save-OwnedProcessLogs($Owned, [string]$Name) {
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.stdout.log"), $Owned.stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.stderr.log"), $Owned.stderr.GetAwaiter().GetResult())
}

try {
    $init = Start-OwnedProcess (Join-Path $PgBinPath 'initdb.exe') @('-D', $data, '--username=saaia_test', '--encoding=UTF8', '--locale=C', '--auth=scram-sha-256', "--pwfile=$passwordFile")
    if (-not $init.process.WaitForExit(60000)) {
        $init.process.Kill($true)
        throw 'Isolated initdb timed out.'
    }

    Save-OwnedProcessLogs $init 'initdb'
    if ($init.process.ExitCode -ne 0) {
        throw 'Isolated initdb failed; inspect fixture stderr.'
    }

    Remove-Item -LiteralPath $passwordFile
    $managed = Start-OwnedProcess $postgres @('-D', $data, '-h', '127.0.0.1', '-p', "$Port")
    $ready = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        if ($managed.process.HasExited) {
            throw 'Owned PostgreSQL exited before readiness.'
        }

        & (Join-Path $PgBinPath 'pg_isready.exe') -h 127.0.0.1 -p $Port -U saaia_test -d postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }

        Start-Sleep -Milliseconds 200
    }

    while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) {
        throw 'Isolated PostgreSQL readiness timed out.'
    }

    [Environment]::SetEnvironmentVariable('PGPASSWORD', $secret, 'Process')
    $actual = (& (Join-Path $PgBinPath 'psql.exe') -w -h 127.0.0.1 -p $Port -U saaia_test -d postgres -Atc "SELECT current_setting('data_directory');" | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($data)) {
        throw 'Unexpected data_directory; refusing tests.'
    }

    $verified = $true
    $manifest.verifiedDataDirectory = $true
    [Environment]::SetEnvironmentVariable('PGPASSWORD', $old.PGPASSWORD, 'Process')
    [Environment]::SetEnvironmentVariable('SAAIA_TEST_CONTRACT_EXPORT_DIR', $OutputDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('SAAIA_TEST_PG_CONN', "Host=127.0.0.1;Port=$Port;Database=postgres;Username=saaia_test;Password=$secret;Pooling=false;Timeout=10;Command Timeout=120;Include Error Detail=false", 'Process')
    & (Join-Path $PSScriptRoot 'test-project.ps1') -Profile IntegrationPg -Projects Backend -Filter $Filter -Configuration $Configuration -NoBuild:$NoBuild -NoRestore:$NoRestore -OutputPath (Join-Path $OutputDirectory 'tests/manifest.json')
    $exitCode = $LASTEXITCODE
    $manifest.state = if ($exitCode -eq 0) {
        'passed'
    } else {
        'failed'
    }
} catch {
    $failure = $_.Exception.GetType().Name
    $exitCode = 1
    $manifest.state = 'failed'
    Write-Error 'Isolated PostgreSQL fixture failed; inspect its manifest and logs.' -ErrorAction Continue
} finally {
    foreach ($name in $old.Keys) {
        [Environment]::SetEnvironmentVariable($name, $old[$name], 'Process')
    }

    $manifest.environmentRestored = $true
    if (Test-Path -LiteralPath $passwordFile) {
        Remove-Item -LiteralPath $passwordFile
    }

    if ($null -ne $managed) {
        if (-not $managed.process.HasExited) {
            & (Join-Path $PgBinPath 'pg_ctl.exe') -D $data -m fast -w -t 20 stop *> (Join-Path $OutputDirectory 'pg-stop.log')
            if (-not $managed.process.WaitForExit(10000)) {
                $managed.process.Kill($true)
                $managed.process.WaitForExit()
            }
        }

        Save-OwnedProcessLogs $managed 'postgres'
    }

    $probe = [Net.Sockets.TcpClient]::new()
    try {
        $task = $probe.ConnectAsync('127.0.0.1', $Port)
        $null = $task.Wait(300)
        $remaining = $probe.Connected
    } catch {
        $remaining = $false
    } finally {
        $probe.Dispose()
    }

    if ($remaining) {
        $failure = 'Owned port still has a listener'
        $exitCode = 1
        $manifest.state = 'failed'
    }

    $manifest.remainingListener = $remaining
    # Only remove a stopped, ownership-verified directory under our dedicated parent.
    if (-not $KeepCluster -and -not $remaining -and ($null -eq $managed -or $managed.process.HasExited)) {
        $resolved = [IO.Path]::GetFullPath($cluster)
        $owner = Get-Content -LiteralPath (Join-Path $resolved 'owner.json') -Raw | ConvertFrom-Json
        if (-not $resolved.StartsWith($clusterParent+[IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $owner.runId -ne $runId) {
            throw 'Fixture ownership mismatch; refusing recursive cleanup.'
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
        $clusterRemoved = $true
    }

    $manifest.failure = $failure
    $manifest.clusterRemoved = $clusterRemoved
    $manifest.bootstrapPasswordRemoved = (-not (Test-Path -LiteralPath $passwordFile))
    $manifest.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8
}

Write-Host "isolated-postgres: $($manifest.state); artifacts: $OutputDirectory"
exit $exitCode
