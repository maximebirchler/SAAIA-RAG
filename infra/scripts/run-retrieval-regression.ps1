param(
  [switch]$WithIntegration,
  [switch]$SkipClient,
  [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..\\..")).Path
Set-Location $RootDir

function Invoke-Step {
  param(
    [string]$Title,
    [scriptblock]$Action
  )

  Write-Host ""
  Write-Host "== $Title ==" -ForegroundColor Cyan
  & $Action
}

if ($WithIntegration -and [string]::IsNullOrWhiteSpace($env:SAAIA_TEST_PG_CONN)) {
  throw "SAAIA_TEST_PG_CONN must be set when -WithIntegration is used."
}

if (-not $NoBuild) {
  Invoke-Step "Build backend tests" {
    dotnet build ".\\backend\\SAAIA.Backend.Tests\\SAAIA.Backend.Tests.csproj" -nologo
  }

  if (-not $SkipClient) {
    Invoke-Step "Build WinUI client" {
      dotnet build ".\\client\\SAAIA.Client.WinUI\\SAAIA.Client.WinUI.csproj" -nologo -nodeReuse:false -p:UseSharedCompilation=false
    }

    Invoke-Step "Build client tool-agent tests" {
      dotnet build ".\\client\\SAAIA.Client.ToolAgent.Tests\\SAAIA.Client.ToolAgent.Tests.csproj" -nologo -nodeReuse:false -p:UseSharedCompilation=false
    }
  }
}

Invoke-Step "Run backend retrieval regression tests" {
  dotnet test ".\\backend\\SAAIA.Backend.Tests\\SAAIA.Backend.Tests.csproj" --no-build -nologo --filter "FullyQualifiedName~Retrieval|FullyQualifiedName~DocumentFoundationIntegrationTests"
}

if (-not $SkipClient) {
  Invoke-Step "Run client tool-agent tests" {
    dotnet vstest ".\\client\\SAAIA.Client.ToolAgent.Tests\\bin\\Debug\\net8.0-windows10.0.19041.0\\SAAIA.Client.ToolAgent.Tests.dll"
  }
}

Invoke-Step "Check diff hygiene" {
  git diff --check
}

Write-Host ""
Write-Host "Retrieval regression campaign completed." -ForegroundColor Green
