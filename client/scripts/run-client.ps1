param(
  [ValidateSet("x64","x86","ARM64")]
  [string]$Platform = "x64",
  [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Proj = Join-Path $RepoRoot "client\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj"

Write-Host "[INFO] RepoRoot: $RepoRoot"
Write-Host "[INFO] Project:  $Proj"
Write-Host "[INFO] Platform: $Platform"

dotnet build $Proj -c $Configuration -p:Platform=$Platform

# dotnet run needs the unpackaged profile (Package profile is for VS/MSIX tooling)
dotnet run --no-build --project $Proj -c $Configuration -p:Platform=$Platform --launch-profile "SAAIA.Client.WinUI (Unpackaged)"
