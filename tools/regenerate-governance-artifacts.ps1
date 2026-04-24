[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$GovernanceRoot = "",
    [switch]$QualifyLocalRuntime,
    [switch]$SkipBuild,
    [switch]$RunAuditAfter
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$clientProject = Join-Path $ProjectRoot "client\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj"
$publishDir = Join-Path $ProjectRoot "client\SAAIA.Client.WinUI\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
$exePath = Join-Path $publishDir "SAAIA.Client.WinUI.exe"

if (-not $SkipBuild) {
    dotnet build $clientProject -p:NuGetAudit=false -p:Platform=$Platform -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Client build failed."
    }
}

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Client executable not found: $exePath"
}

$arguments = @("--governance-init-only")
if ($QualifyLocalRuntime) {
    $arguments += "--qualify-local-runtime-only"
}
if (-not [string]::IsNullOrWhiteSpace($GovernanceRoot)) {
    $arguments += @("--governance-root", $GovernanceRoot)
}

Write-Host "Running client maintenance mode..."
Write-Host "Exe: $exePath"
Write-Host "Args: $($arguments -join ' ')"

$process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru -Wait -NoNewWindow
if ($process.ExitCode -ne 0) {
    throw "Client maintenance mode failed with exit code $($process.ExitCode)."
}

Write-Host "Client maintenance mode completed."

if ($RunAuditAfter) {
    $auditScript = Join-Path $PSScriptRoot "verify-governance-artifacts.ps1"
    & $auditScript
    if ($LASTEXITCODE -ne 0) {
        throw "Governance artifact audit failed after regeneration."
    }
}
