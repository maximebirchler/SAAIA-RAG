[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("OpenAI", "RunPod")]
    [string]$Provider,

    [string]$StorePath = "",

    [string]$SourceEnvironmentVariable = "",

    [switch]$KeepClipboard
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ("System.Security.Cryptography.ProtectedData" -as [type])) {
    Add-Type -AssemblyName System.Security
}

if ([string]::IsNullOrWhiteSpace($StorePath)) {
    $StorePath = Join-Path $env:LOCALAPPDATA "SAAIA\client\secure.json"
}
$StorePath = [System.IO.Path]::GetFullPath($StorePath)

$fromClipboard = [string]::IsNullOrWhiteSpace($SourceEnvironmentVariable)
$secret = if ($fromClipboard) {
    Get-Clipboard -Raw
} else {
    [Environment]::GetEnvironmentVariable($SourceEnvironmentVariable, "Process")
}
if ([string]::IsNullOrWhiteSpace($secret)) {
    throw $(if ($fromClipboard) {
        "The clipboard does not contain a secret. Copy the API key, then run this script again."
    } else {
        "The selected process environment variable does not contain a secret."
    })
}
$secret = $secret.Trim()

$propertyName = switch ($Provider) {
    "OpenAI" { "OpenAiApiKeyProtected" }
    "RunPod" { "RunPodApiKeyProtected" }
}
$entropy = [System.Text.Encoding]::UTF8.GetBytes("SAAIA.Client.WinUI|CDC-v2.7")
$plainBytes = [System.Text.Encoding]::UTF8.GetBytes($secret)
try {
    $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        $plainBytes,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $protectedBase64 = [Convert]::ToBase64String($protectedBytes)

    $store = [ordered]@{}
    if (Test-Path -LiteralPath $StorePath) {
        $parsed = Get-Content -LiteralPath $StorePath -Raw | ConvertFrom-Json
        foreach ($entry in $parsed.PSObject.Properties) {
            $store[$entry.Name] = $entry.Value
        }
    }
    $store[$propertyName] = $protectedBase64

    $directory = Split-Path -Parent $StorePath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporaryPath = "$StorePath.tmp-$PID"
    try {
        $store | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
        Move-Item -LiteralPath $temporaryPath -Destination $StorePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    Write-Output "$Provider API key saved with DPAPI for the current Windows user."
    Write-Output "Store: $StorePath"
}
finally {
    [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    $secret = $null
    if ($fromClipboard -and -not $KeepClipboard) {
        # Windows PowerShell 5.1 rejects an empty string. A single whitespace
        # character removes the secret while remaining compatible with both
        # Windows PowerShell and PowerShell 7.
        Set-Clipboard -Value " "
    }
}
