param(
  [string]$Project = "$PSScriptRoot\..\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj",
  [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

Write-Host "== Preflight: Windows App Runtime ==" -ForegroundColor Cyan
& "$PSScriptRoot\check-winappruntime.ps1" | Write-Host

# Clean startup log for an unambiguous run
$logDir = Join-Path $env:LOCALAPPDATA "SAAIA\logs"
$logFile = Join-Path $logDir "client_startup.log"
if (Test-Path $logFile) {
  try { Remove-Item $logFile -Force } catch {}
}

Write-Host "== Build WinUI client ($Platform) ==" -ForegroundColor Cyan
dotnet build $Project -p:Platform=$Platform

$exe = Join-Path (Split-Path $Project -Parent) ("bin\{0}\Debug\net8.0-windows10.0.19041.0\win-{0}\SAAIA.Client.WinUI.exe" -f $Platform)
Write-Host "== Run ==" -ForegroundColor Cyan
Write-Host $exe

if (!(Test-Path $exe)) { throw "Exe not found: $exe" }

# Start process and wait a bit; WinUI apps can spawn and then fail-fast quickly
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 2

if ($p.HasExited) {
  Write-Warning "Client exited quickly (code=$($p.ExitCode)). Showing startup log tail (if any):"
  if (Test-Path $logFile) { Get-Content $logFile -Tail 200 }
  else { Write-Warning "No startup log found at $logFile" }
  exit $p.ExitCode
}

Write-Host "[ OK ] Client process running (pid=$($p.Id))." -ForegroundColor Green
Write-Host "Tip: if the window is still not visible, check $logFile" -ForegroundColor DarkGray
