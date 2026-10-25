#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnvironmentDirectory,
    [Parameter(Mandatory)][string]$EvidenceName,
    [ValidatePattern('^[A-Za-z0-9_-]{1,48}$')][string]$ProfileName = 'primary'
)
$ErrorActionPreference = 'Stop'
if ($EvidenceName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'EvidenceName must be a simple identifier.' }
$directory = [IO.Path]::GetFullPath($EnvironmentDirectory)
$owner = Get-Content -LiteralPath (Join-Path $directory 'environment.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $directory) { throw 'An owned local validation environment is required.' }
$expected = [IO.Path]::GetFullPath((Join-Path $directory 'runtime/client-data'))
if ([IO.Path]::GetFullPath($owner.clientDataRoot) -ine $expected) { throw 'Unexpected client profile root.' }
$profileRoot = if ($ProfileName -eq 'primary') { $expected } else {
    [IO.Path]::GetFullPath((Join-Path $directory ('runtime/client-profiles/' + $ProfileName)))
}
if ($ProfileName -ne 'primary' -and -not (Test-Path -LiteralPath (Join-Path $profileRoot 'client/settings.json'))) {
    throw 'Prepare the named isolated profile before launch.'
}
if (Get-Process SAAIA.Client.WinUI -ErrorAction SilentlyContinue) { throw 'An existing client is running; close its window before starting another validation instance.' }
$proofPath = Join-Path (Split-Path -Parent $directory) ($EvidenceName + '.json')
if (Test-Path -LiteralPath $proofPath) { throw 'Evidence already exists.' }
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$executable = Join-Path $repo 'client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/SAAIA.Client.WinUI.exe'
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.WorkingDirectory = Split-Path -Parent $executable
$start.UseShellExecute = $false
foreach ($name in @($start.Environment.Keys)) {
    if ($name -match '^(SAAIA_|OPENAI_|RUNPOD_|AZURE_OPENAI_)') { $start.Environment.Remove($name) | Out-Null }
}
$start.Environment['SAAIA_CLIENT_DATA_ROOT'] = $profileRoot
$start.Environment['SAAIA_DOCUMENTS_ROOT'] = $owner.documentsRoot
$start.Environment['SAAIA_LLM_PROVIDER_MODE'] = 'Local'
$start.Environment['SAAIA_LLM_EXTERNAL_POLICY'] = 'ProductionLocal'
$client = [Diagnostics.Process]::Start($start)
$proof = [ordered]@{
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    pid = $client.Id
    executable = $executable
    assemblySha256 = (Get-FileHash -LiteralPath (Join-Path $start.WorkingDirectory 'SAAIA.Client.WinUI.dll') -Algorithm SHA256).Hash
    clientDataRoot = $profileRoot
    profileName = $ProfileName
    documentsRoot = $owner.documentsRoot
    providerMode = 'Local'
    externalPolicy = 'ProductionLocal'
    inheritedProviderEnvironmentRemoved = $true
    purpose = 'Launch only; no UI automation. Native interactions are performed separately through Computer Use.'
}
$proof | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output ('Client launched with isolated profile; pid=' + $client.Id)
