param(
  [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

Write-Host "== Test patch M6/M7 BIG v2 ==" -ForegroundColor Cyan
Write-Host "RepoRoot: $RepoRoot"

Set-Location $RepoRoot

# Stop client if running
Get-Process "SAAIA.Client.WinUI" -ErrorAction SilentlyContinue | Stop-Process -Force

# Clean build outputs
Remove-Item .\client\SAAIA.Client.WinUI\bin -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\client\SAAIA.Client.WinUI\obj -Recurse -Force -ErrorAction SilentlyContinue

# Build + run
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client-x64.ps1

Write-Host "\nManual smoke tests inside the app:" -ForegroundColor Yellow
Write-Host "  1) 'liste des documents' => pas de chemins fantômes, pas de doublons"
Write-Host "  2) 'suite' => pagination ou 'Fin de la liste.'"
Write-Host "  3) 'source du PDF01' => chip source (p.1)"
Write-Host "  4) question technique => rag.search + chips sources"
