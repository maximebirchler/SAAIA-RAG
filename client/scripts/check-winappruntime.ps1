param()

$ErrorActionPreference = "Stop"

$pkgs = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime*" -ErrorAction SilentlyContinue
if ($null -eq $pkgs -or $pkgs.Count -eq 0) {
  Write-Host "[FAIL] Windows App Runtime not detected. Install WinAppRuntime 1.8 (x64) with winget:" -ForegroundColor Red
  Write-Host "  winget install Microsoft.WindowsAppRuntime.1.8 --accept-source-agreements --accept-package-agreements"
  exit 2
}

Write-Host "[ OK ] Windows App Runtime package(s) detected:" -ForegroundColor Green
$pkgs | Sort-Object Name, Version | ForEach-Object {
  "  - {0} {1}" -f $_.Name, $_.Version
}
