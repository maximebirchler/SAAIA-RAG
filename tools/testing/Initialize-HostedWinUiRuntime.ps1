#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$AssetsPath = '',
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Package registration is restricted to an ephemeral hosted runner. A local
# invocation can inspect the exact restored packages, but cannot install them.
if ($Execute -and ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $env:RUNNER_OS -cne 'Windows')) {
    throw 'Runtime registration is only supported on an explicit Windows GitHub-hosted runner.'
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The WinUI test host requires Windows x64.'
}
if (-not $AssetsPath) {
    $AssetsPath = Join-Path $repo 'client/SAAIA.Client.ToolAgent.Tests/obj/project.assets.json'
}
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Preserve previous runtime observations; choose a new output path.' }
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$runtimeLibraries = @($assets.libraries.Keys | Where-Object { $_ -match '^Microsoft\.WindowsAppSDK\.Runtime/[^/]+$' })
if ($runtimeLibraries.Count -ne 1) { throw 'Restore must identify exactly one Windows App SDK runtime package.' }
$packageRelative = $assets.libraries[$runtimeLibraries[0]].path
$roots = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $packageRelative } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Container })
if ($roots.Count -ne 1) { throw 'The exact restored runtime package directory is missing or ambiguous.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @('Microsoft.WindowsAppRuntime.1.8.msix', 'Microsoft.WindowsAppRuntime.DDLM.1.8.msix',
    'Microsoft.WindowsAppRuntime.Main.1.8.msix', 'Microsoft.WindowsAppRuntime.Singleton.1.8.msix')
$observations = [Collections.Generic.List[object]]::new()
foreach ($name in $packages) {
    $path = Join-Path $roots[0] ('tools/MSIX/win10-x64/' + $name)
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
    if ($identity.ProcessorArchitecture -cne 'x64' -or
        $identity.Publisher -cne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
        $identity.Name -notmatch '^(Microsoft\.WindowsAppRuntime\.1\.8|Microsoft\.WinAppRuntime\.DDLM\.[0-9.]+-x6|MicrosoftCorporationII\.WinAppRuntime\.(Main\.1\.8|Singleton))$') {
        throw 'Unexpected publisher, architecture or identity in the restored Microsoft runtime.'
    }
    $expectedVersion = [version]$identity.Version
    $installedBefore = @(Get-AppxPackage -Name $identity.Name | Where-Object {
        $_.Architecture -eq 'X64' -and $_.Publisher -ceq $identity.Publisher -and
            [version]$_.Version -ge $expectedVersion -and $_.Status -eq 'Ok'
    })
    $registered = $false
    if ($Execute -and $installedBefore.Count -eq 0) {
        # Add-AppxPackage verifies the signed package; do not allow unsigned
        # packages, global provisioning, downgrade, or forced process shutdown.
        Add-AppxPackage -Path $path -ErrorAction Stop
        $registered = $true
    }
    $installedAfter = @(Get-AppxPackage -Name $identity.Name | Where-Object {
        $_.Architecture -eq 'X64' -and $_.Publisher -ceq $identity.Publisher -and
            [version]$_.Version -ge $expectedVersion -and $_.Status -eq 'Ok'
    })
    $observations.Add([ordered]@{
        package = $name; identity = [string]$identity.Name; expectedVersion = $expectedVersion.ToString()
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        registeredThisRun = $registered; matchingPackageBefore = ($installedBefore.Count -gt 0)
        matchingPackageAfter = ($installedAfter.Count -gt 0)
        observedVersions = @($installedAfter | ForEach-Object { [string]$_.Version })
    })
    if ($Execute -and $installedAfter.Count -eq 0) { throw 'The restored WinUI runtime was not registered for the current test user.' }
}
$result = [ordered]@{
    schemaVersion = 'saaia-hosted-winui-runtime.v1'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    executed = [bool]$Execute; runtimeLibrary = $runtimeLibraries[0]
    assetsSha256 = (Get-FileHash -LiteralPath $AssetsPath -Algorithm SHA256).Hash
    packages = $observations; passed = ($Execute -and @($observations | Where-Object { -not $_.matchingPackageAfter }).Count -eq 0)
    userMachineRegistrationAllowed = $false
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
[IO.File]::WriteAllText($output, ($result | ConvertTo-Json -Depth 10))
Write-Output "WinUI runtime packages inspected: $($observations.Count); registration executed: $([bool]$Execute)."
