[CmdletBinding()]
param(
    [string[]]$SearchRoots,
    [string]$OutputPath,
    [switch]$IncludeCatalogSnippet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq $SearchRoots -or $SearchRoots.Count -eq 0) {
    $scriptRoot = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
        $scriptRoot = (Get-Location).Path
    }

    $SearchRoots = @(
        (Join-Path $scriptRoot "..\models"),
        (Join-Path $env:LOCALAPPDATA "SAAIA\Models")
    )
}

$knownModels = @(
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q4-k-m"
        FileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf"
        Family = "qwen2.5"
        Quantization = "Q4_K_M"
    },
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q4-k-s"
        FileName = "Qwen2.5-3B-Instruct-Q4_K_S.gguf"
        Family = "qwen2.5"
        Quantization = "Q4_K_S"
    },
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q4-0"
        FileName = "Qwen2.5-3B-Instruct-Q4_0.gguf"
        Family = "qwen2.5"
        Quantization = "Q4_0"
    },
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q6-k"
        FileName = "Qwen2.5-3B-Instruct-Q6_K.gguf"
        Family = "qwen2.5"
        Quantization = "Q6_K"
    },
    [pscustomobject]@{
        ModelId = "mistral-7b-instruct-v0.3-q4-k-m"
        FileName = "Mistral-7B-Instruct-v0.3-Q4_K_M.gguf"
        Family = "mistral"
        Quantization = "Q4_K_M"
    },
    [pscustomobject]@{
        ModelId = "mistral-7b-instruct-v0.3-q6-k"
        FileName = "Mistral-7B-Instruct-v0.3-Q6_K.gguf"
        Family = "mistral"
        Quantization = "Q6_K"
    }
)

function Resolve-SearchRoots {
    param([string[]]$Roots)

    $resolved = New-Object System.Collections.Generic.List[string]
    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $expanded = [Environment]::ExpandEnvironmentVariables($root)
        try {
            $full = [System.IO.Path]::GetFullPath($expanded)
        }
        catch {
            continue
        }

        if ((Test-Path -LiteralPath $full) -and -not $resolved.Contains($full)) {
            $resolved.Add($full)
        }
    }

    return $resolved
}

function Find-ModelPath {
    param(
        [string]$FileName,
        [string[]]$Roots
    )

    foreach ($root in $Roots) {
        $candidate = Join-Path $root $FileName
        if (Test-Path -LiteralPath $candidate) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    return $null
}

function ConvertTo-CSharpConstName {
    param(
        [string]$FileName
    )

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    $segments = @($stem) |
        ForEach-Object { ($_ -replace '[^A-Za-z0-9]+', ' ') } |
        ForEach-Object { $_.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) } |
        ForEach-Object {
            $_ | ForEach-Object {
                if ($_.Length -eq 0) { return }
                $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
            }
        }

    return (($segments -join '') + "Sha256")
}

function Format-MarkdownSummary {
    param([object[]]$Items)

    $lines = @(
        "| ModelId | FileName | Status | SHA256 |",
        "|---|---|---|---|"
    )

    foreach ($item in $Items) {
        $sha = if ($item.sha256) { $item.sha256 } else { "-" }
        $lines += "| $($item.modelId) | $($item.fileName) | $($item.status) | $sha |"
    }

    return ($lines -join [Environment]::NewLine)
}

$roots = Resolve-SearchRoots -Roots $SearchRoots
$entries = @()
$catalogSnippetLines = New-Object System.Collections.Generic.List[string]

foreach ($model in $knownModels) {
    $path = Find-ModelPath -FileName $model.FileName -Roots $roots
    if ($null -eq $path) {
        $entries += [pscustomobject]@{
            modelId = $model.ModelId
            fileName = $model.FileName
            family = $model.Family
            quantization = $model.Quantization
            status = "missing"
            path = $null
            sizeBytes = $null
            sha256 = $null
            csharpConstName = (ConvertTo-CSharpConstName -FileName $model.FileName)
            catalogPatch = $null
        }
        continue
    }

    $file = Get-Item -LiteralPath $path
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    $constName = ConvertTo-CSharpConstName -FileName $model.FileName
    $catalogPatch = "ChecksumSha256: $constName.ToLowerInvariant(), ChecksumStatus: ""verified_reference_hash"""

    $entries += [pscustomobject]@{
        modelId = $model.ModelId
        fileName = $model.FileName
        family = $model.Family
        quantization = $model.Quantization
        status = "found"
        path = $file.FullName
        sizeBytes = $file.Length
        sha256 = $hash.Hash.ToLowerInvariant()
        csharpConstName = $constName
        catalogPatch = $catalogPatch
    }

    if ($IncludeCatalogSnippet) {
        $catalogSnippetLines.Add("private const string $constName = ""$($hash.Hash.ToLowerInvariant())"";")
    }
}

$markdownSummary = Format-MarkdownSummary -Items $entries
$catalogSnippet = if ($IncludeCatalogSnippet) {
    ($catalogSnippetLines -join [Environment]::NewLine)
} else {
    $null
}

$report = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString("o")
    searchRoots = $roots
    foundCount = @($entries | Where-Object { $_.status -eq "found" }).Count
    missingCount = @($entries | Where-Object { $_.status -eq "missing" }).Count
    items = $entries
    markdownSummary = $markdownSummary
    catalogSnippet = $catalogSnippet
}

$json = $report | ConvertTo-Json -Depth 5

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $target = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputPath))
    $directory = Split-Path -Parent $target
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -LiteralPath $target -Value $json -Encoding UTF8
    Write-Host "Checksum report written to $target"
}

$json
