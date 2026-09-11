[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$RuntimeProfilePath = "",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($RuntimeProfilePath)) {
    $RuntimeProfilePath = Join-Path $repositoryRoot "artifacts\reprise-pc-20260908\a658-preparation\runtime-profile-frozen.json"
}
$RuntimeProfilePath = [System.IO.Path]::GetFullPath($RuntimeProfilePath)
if (-not (Test-Path -LiteralPath $RuntimeProfilePath -PathType Leaf)) {
    throw "Frozen runtime profile was not found: $RuntimeProfilePath"
}

$profile = Get-Content -LiteralPath $RuntimeProfilePath -Raw | ConvertFrom-Json
$runtimePath = [System.IO.Path]::GetFullPath([string]$profile.runtimePath)
$modelPath = [System.IO.Path]::GetFullPath([string]$profile.modelPath)
if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
    throw "Frozen runtime executable was not found: $runtimePath"
}
if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "Frozen model was not found: $modelPath"
}
if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash -ne $profile.runtimeSha256) {
    throw "Frozen runtime hash mismatch."
}
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $profile.modelSha256) {
    throw "Frozen model hash mismatch."
}

$existingListeners = @(Get-NetTCPConnection -LocalPort 1234 -State Listen -ErrorAction SilentlyContinue)
if ($existingListeners.Count -gt 0) {
    throw "Port 1234 is already occupied; refusing to disturb an unowned process."
}

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\local-provider-smoke-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null

$process = $null
$testExit = $null
$failure = $null
try {
    $process = Start-Process `
        -FilePath $runtimePath `
        -ArgumentList ([string]$profile.arguments) `
        -WorkingDirectory (Split-Path -Parent $runtimePath) `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput (Join-Path $ArtifactDirectory "runtime.stdout.log") `
        -RedirectStandardError (Join-Path $ArtifactDirectory "runtime.stderr.log")

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    $healthy = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "Owned Qwen process exited before readiness."
        }
        try {
            if ((Invoke-WebRequest -Uri "http://127.0.0.1:1234/health" -TimeoutSec 2).StatusCode -eq 200) {
                $healthy = $true
                break
            }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) {
        throw "Frozen Qwen did not become ready within 120 seconds."
    }

    $env:SAAIA_LOCAL_PROVIDER_HOST = "127.0.0.1"
    $env:SAAIA_LOCAL_PROVIDER_PORT = "1234"
    $env:SAAIA_LOCAL_PROVIDER_MODEL = "local"
    & (Join-Path $repositoryRoot "tools\test-local-llm-provider.ps1") `
        -Configuration $Configuration `
        -Platform $Platform `
        -ArtifactDirectory $ArtifactDirectory
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) {
        throw "Local provider test returned exit code $testExit."
    }
} catch {
    $failure = $_.Exception.GetType().Name + ": " + $_.Exception.Message
    throw
} finally {
    $ownedPid = if ($null -ne $process) { $process.Id } else { $null }
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        $null = $process.WaitForExit(15000)
    }
    Remove-Item `
        Env:SAAIA_LOCAL_PROVIDER_HOST, `
        Env:SAAIA_LOCAL_PROVIDER_PORT, `
        Env:SAAIA_LOCAL_PROVIDER_MODEL `
        -ErrorAction SilentlyContinue
    $remainingListeners = @(Get-NetTCPConnection -LocalPort 1234 -State Listen -ErrorAction SilentlyContinue).Count
    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        profileVersion = $profile.version
        runtimeSha256 = $profile.runtimeSha256
        modelSha256 = $profile.modelSha256
        ownedPid = $ownedPid
        testExit = $testExit
        failure = $failure
        remainingLocalQwenListeners = $remainingListeners
    } | ConvertTo-Json -Depth 6 | Set-Content `
        -LiteralPath (Join-Path $ArtifactDirectory "resource-shutdown.json") `
        -Encoding utf8
}

Write-Output "Frozen local provider probe completed. Artifact: $ArtifactDirectory"
