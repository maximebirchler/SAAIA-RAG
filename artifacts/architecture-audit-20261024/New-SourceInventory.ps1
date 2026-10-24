param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$SourceRevision = '5131dee30131c1206f6a2881d615ce53a0764682',
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$paths = @(& git -C $Repository ls-tree -r --name-only $SourceRevision)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the source revision.' }
$extensions = @('.cs', '.xaml', '.ps1', '.py', '.ts', '.js')
$rows = @(
    foreach ($relative in $paths) {
        if ([IO.Path]::GetExtension($relative) -notin $extensions) { continue }
        if ($relative -match '(^|/)(bin|obj|node_modules)/') { continue }
        $absolute = Join-Path $Repository $relative
        if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
            throw "Source file missing from working tree: $relative"
        }
        [pscustomobject]@{
            path = $relative
            lines = [IO.File]::ReadAllLines($absolute).Length
            area = ($relative -split '/')[0]
            test = $relative -match 'Tests/|\.Tests/|/tests/'
        }
    }
)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'tracked-source-metrics.json') -Encoding utf8
$hashes = @($rows | ForEach-Object {
    [pscustomobject]@{ path = $_.path; sha256 = (Get-FileHash -LiteralPath (Join-Path $Repository $_.path) -Algorithm SHA256).Hash }
})
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-hashes.json') -Encoding utf8
[pscustomobject]@{
    sourceRevision = $SourceRevision
    observedHead = (& git -C $Repository rev-parse HEAD)
    sourceCount = $rows.Count
    content = 'Working-tree files for the tracked paths in SourceRevision. Verify source-hashes.json before comparing snapshots.'
    classification = 'Path-based test classification; an inventory, not an exact build or reachability graph.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'inventory-manifest.json') -Encoding utf8
Write-Output "Inventoried $($rows.Count) tracked source files."
