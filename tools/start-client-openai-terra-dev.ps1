[CmdletBinding()]
param(
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$env:SAAIA_LLM_PROVIDER_MODE = "OpenAiDev"
$env:SAAIA_LLM_EXTERNAL_POLICY = "DevelopmentExternalAllowed"
$env:SAAIA_OPENAI_MODEL = "gpt-5.6-terra"

& (Join-Path $PSScriptRoot "..\client\scripts\run-client-x64.ps1") -Platform $Platform
exit $LASTEXITCODE
