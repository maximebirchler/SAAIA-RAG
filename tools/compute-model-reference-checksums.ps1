[CmdletBinding()]
param(
    [string[]]$SearchRoots,
    [string]$OutputPath,
    [switch]$IncludeCatalogSnippet,
    [string]$CatalogArtifactPath,
    [switch]$VerifyAgainstCatalog,
    [switch]$UpdateLocalCatalog,
    [switch]$BootstrapCatalogIfMissing
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

if ([string]::IsNullOrWhiteSpace($CatalogArtifactPath)) {
    $CatalogArtifactPath = Join-Path $env:LOCALAPPDATA "SAAIA\governance\model_catalog.json"
}

$knownModels = @(
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q4-k-m"
        FileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf"
        Family = "qwen2.5"
        Quantization = "Q4_K_M"
        DisplayName = "Qwen2.5 3B Instruct Q4_K_M"
        SourceRef = "hf-bartowski-qwen25-3b"
        Architecture = "qwen2"
        BlockCount = 36
        HeadCount = 16
        HeadCountKv = 2
        EmbeddingLength = 2048
        ContextLength = 32768
        FeedForwardLength = 11008
    },
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q6-k-l"
        FileName = "Qwen2.5-3B-Instruct-Q6_K_L.gguf"
        Family = "qwen2.5"
        Quantization = "Q6_K_L"
    },
    [pscustomobject]@{
        ModelId = "qwen2.5-3b-instruct-q8-0"
        FileName = "Qwen2.5-3B-Instruct-Q8_0.gguf"
        Family = "qwen2.5"
        Quantization = "Q8_0"
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
        ModelId = "mistral-7b-instruct-v0.3-iq3-m"
        FileName = "Mistral-7B-Instruct-v0.3-IQ3_M.gguf"
        Family = "mistral"
        Quantization = "IQ3_M"
    },
    [pscustomobject]@{
        ModelId = "mistral-7b-instruct-v0.3-q6-k"
        FileName = "Mistral-7B-Instruct-v0.3-Q6_K.gguf"
        Family = "mistral"
        Quantization = "Q6_K"
    },
    [pscustomobject]@{
        ModelId = "gemma-4-e2b-it-q4-k-m"
        FileName = "gemma-4-E2B-it-Q4_K_M.gguf"
        Family = "gemma4-e2b"
        Quantization = "Q4_K_M"
    },
    [pscustomobject]@{
        ModelId = "gemma-4-e2b-it-q8-0"
        FileName = "gemma-4-E2B-it-Q8_0.gguf"
        Family = "gemma4-e2b"
        Quantization = "Q8_0"
    },
    [pscustomobject]@{
        ModelId = "gemma-4-e4b-it-q4-k-m"
        FileName = "gemma-4-E4B-it-Q4_K_M.gguf"
        Family = "gemma4-e4b"
        Quantization = "Q4_K_M"
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

function Get-OptionalValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
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

function Load-CatalogChecksums {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{}
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not (Test-Path -LiteralPath $expanded)) {
        return @{}
    }

    $json = Get-Content -LiteralPath $expanded -Raw | ConvertFrom-Json
    $map = @{}
    foreach ($item in @($json.items)) {
        if ($null -eq $item) {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($item.modelId)) {
            $map[$item.modelId.ToLowerInvariant()] = $item.checksumSha256
        }

        if (-not [string]::IsNullOrWhiteSpace($item.fileName)) {
            $map[$item.fileName.ToLowerInvariant()] = $item.checksumSha256
        }
    }

    return $map
}

function Load-CatalogDocument {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not (Test-Path -LiteralPath $expanded)) {
        return $null
    }

    return Get-Content -LiteralPath $expanded -Raw | ConvertFrom-Json
}

function Compute-TextSha256 {
    param([string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
        return [System.BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Save-CatalogDocument {
    param(
        [string]$Path,
        [object]$Document
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    $directory = Split-Path -Parent $expanded
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $Document | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath $expanded -Value $json -Encoding UTF8
    Set-Content -LiteralPath ($expanded + ".sha256") -Value (Compute-TextSha256 -Text $json) -Encoding Ascii
}

function New-BootstrapCatalogDocument {
    param([object[]]$Items)

    $catalogItems = @()
    foreach ($item in $Items) {
        if ($item.status -ne "found") {
            continue
        }

        if ($item.modelId -ne "qwen2.5-3b-instruct-q4-k-m") {
            continue
        }

        $catalogItems += [pscustomobject]@{
            modelId = $item.modelId
            displayName = $item.DisplayName
            family = $item.Family
            quantization = $item.Quantization
            fileName = $item.FileName
            sourceRef = $item.SourceRef
            checksumSha256 = $item.sha256
            checksumStatus = "verified_reference_hash"
            license = [pscustomobject]@{
                licenseFamily = "qwen"
                licenseDisplayName = "Qwen Research License"
                commercialUseAllowed = $true
                commercialUseConditions = "Commercial use allowed; separate license required above 100,000,000 monthly active users."
                commercialUseThresholdMau = 100000000
                requiresSeparateCommercialLicenseAboveThreshold = $true
            }
            gguf = [pscustomobject]@{
                architecture = $item.Architecture
                blockCount = $item.BlockCount
                headCount = $item.HeadCount
                headCountKv = $item.HeadCountKv
                embeddingLength = $item.EmbeddingLength
                contextLength = $item.ContextLength
                feedForwardLength = $item.FeedForwardLength
            }
            approvedRuntimeRefs = @("llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-cpu")
            supportedScopes = @("client", "capability_b_backoffice")
            businessStates = @("known", "authorized", "installable")
            artifactStates = @("downloaded_pending", "verification_required")
            supportTier = "client-baseline"
        }
    }

    return [pscustomobject]@{
        artifact = "model_catalog.json"
        cdcAlignment = "v3.1"
        version = "2026-04-22.phase0"
        generatedAt = [DateTimeOffset]::UtcNow.ToString("o")
        items = $catalogItems
    }
}

$roots = Resolve-SearchRoots -Roots $SearchRoots
$entries = @()
$catalogSnippetLines = New-Object System.Collections.Generic.List[string]
$catalogArtifactResolvedPath = if ([string]::IsNullOrWhiteSpace($CatalogArtifactPath)) { $null } else { [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($CatalogArtifactPath)) }
$catalogChecksums = if ($VerifyAgainstCatalog) { Load-CatalogChecksums -Path $CatalogArtifactPath } else { @{} }
$catalogDocument = if ($UpdateLocalCatalog) { Load-CatalogDocument -Path $CatalogArtifactPath } else { $null }
$catalogExists = $null
if ($VerifyAgainstCatalog -or $UpdateLocalCatalog) {
    $catalogExists = -not [string]::IsNullOrWhiteSpace($catalogArtifactResolvedPath) -and (Test-Path -LiteralPath $catalogArtifactResolvedPath)
}
$catalogUpdated = $false
$catalogMatchedCount = 0
$catalogMismatchCount = 0
$catalogMissingCount = 0

foreach ($model in $knownModels) {
    $path = Find-ModelPath -FileName $model.FileName -Roots $roots
    $catalogChecksum = $null
    $verificationStatus = $null
    if ($VerifyAgainstCatalog) {
        $catalogChecksum = $catalogChecksums[$model.ModelId.ToLowerInvariant()]
        if ([string]::IsNullOrWhiteSpace($catalogChecksum)) {
            $catalogChecksum = $catalogChecksums[$model.FileName.ToLowerInvariant()]
        }
    }

    if ($null -eq $path) {
        if ($VerifyAgainstCatalog) {
            $verificationStatus = if ([string]::IsNullOrWhiteSpace($catalogChecksum)) { "catalog_missing" } else { "file_missing" }
            if ($verificationStatus -eq "catalog_missing") { $catalogMissingCount++ }
        }

        $entries += [pscustomobject]@{
            modelId = $model.ModelId
            fileName = $model.FileName
            family = $model.Family
            quantization = $model.Quantization
            DisplayName = (Get-OptionalValue -Object $model -Name "DisplayName")
            SourceRef = (Get-OptionalValue -Object $model -Name "SourceRef")
            Architecture = (Get-OptionalValue -Object $model -Name "Architecture")
            BlockCount = (Get-OptionalValue -Object $model -Name "BlockCount")
            HeadCount = (Get-OptionalValue -Object $model -Name "HeadCount")
            HeadCountKv = (Get-OptionalValue -Object $model -Name "HeadCountKv")
            EmbeddingLength = (Get-OptionalValue -Object $model -Name "EmbeddingLength")
            ContextLength = (Get-OptionalValue -Object $model -Name "ContextLength")
            FeedForwardLength = (Get-OptionalValue -Object $model -Name "FeedForwardLength")
            status = "missing"
            path = $null
            sizeBytes = $null
            sha256 = $null
            csharpConstName = (ConvertTo-CSharpConstName -FileName $model.FileName)
            catalogPatch = $null
            catalogChecksum = $catalogChecksum
            verificationStatus = $verificationStatus
        }
        continue
    }

    $file = Get-Item -LiteralPath $path
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    $constName = ConvertTo-CSharpConstName -FileName $model.FileName
    $catalogPatch = "ChecksumSha256: $constName.ToLowerInvariant(), ChecksumStatus: ""verified_reference_hash"""

    if ($UpdateLocalCatalog -and $null -ne $catalogDocument) {
        foreach ($catalogItem in @($catalogDocument.items)) {
            if ($null -eq $catalogItem) {
                continue
            }

            $matchesModel =
                ([string]::Equals($catalogItem.modelId, $model.ModelId, [System.StringComparison]::OrdinalIgnoreCase)) -or
                ([string]::Equals($catalogItem.fileName, $model.FileName, [System.StringComparison]::OrdinalIgnoreCase))
            if (-not $matchesModel) {
                continue
            }

            if ([string]::IsNullOrWhiteSpace($catalogItem.checksumSha256) -or
                [string]::Equals($catalogItem.checksumStatus, "pending_reference_hash", [System.StringComparison]::OrdinalIgnoreCase))
            {
                $catalogItem.checksumSha256 = $hash.Hash.ToLowerInvariant()
                $catalogItem.checksumStatus = "verified_reference_hash"
                $catalogUpdated = $true
            }
        }
    }

    if ($VerifyAgainstCatalog) {
        if ([string]::IsNullOrWhiteSpace($catalogChecksum)) {
            $verificationStatus = "catalog_missing"
            $catalogMissingCount++
        }
        elseif ($catalogChecksum.ToLowerInvariant() -eq $hash.Hash.ToLowerInvariant()) {
            $verificationStatus = "catalog_match"
            $catalogMatchedCount++
        }
        else {
            $verificationStatus = "catalog_mismatch"
            $catalogMismatchCount++
        }
    }

    $entries += [pscustomobject]@{
        modelId = $model.ModelId
        fileName = $model.FileName
        family = $model.Family
        quantization = $model.Quantization
        DisplayName = (Get-OptionalValue -Object $model -Name "DisplayName")
        SourceRef = (Get-OptionalValue -Object $model -Name "SourceRef")
        Architecture = (Get-OptionalValue -Object $model -Name "Architecture")
        BlockCount = (Get-OptionalValue -Object $model -Name "BlockCount")
        HeadCount = (Get-OptionalValue -Object $model -Name "HeadCount")
        HeadCountKv = (Get-OptionalValue -Object $model -Name "HeadCountKv")
        EmbeddingLength = (Get-OptionalValue -Object $model -Name "EmbeddingLength")
        ContextLength = (Get-OptionalValue -Object $model -Name "ContextLength")
        FeedForwardLength = (Get-OptionalValue -Object $model -Name "FeedForwardLength")
        status = "found"
        path = $file.FullName
        sizeBytes = $file.Length
        sha256 = $hash.Hash.ToLowerInvariant()
        csharpConstName = $constName
        catalogPatch = $catalogPatch
        catalogChecksum = $catalogChecksum
        verificationStatus = $verificationStatus
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

if ($BootstrapCatalogIfMissing -and -not $catalogExists) {
    $catalogDocument = New-BootstrapCatalogDocument -Items $entries
    if (@($catalogDocument.items).Count -gt 0) {
        Save-CatalogDocument -Path $CatalogArtifactPath -Document $catalogDocument
        $catalogUpdated = $true
        $catalogExists = $true
        $catalogChecksums = Load-CatalogChecksums -Path $CatalogArtifactPath
    }
}

$report = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString("o")
    searchRoots = $roots
    foundCount = @($entries | Where-Object { $_.status -eq "found" }).Count
    missingCount = @($entries | Where-Object { $_.status -eq "missing" }).Count
    catalogArtifactPath = if ($VerifyAgainstCatalog -or $UpdateLocalCatalog) { $catalogArtifactResolvedPath } else { $null }
    catalogStatus = if ($VerifyAgainstCatalog -or $UpdateLocalCatalog) {
        if (-not $catalogExists) { "missing" }
        elseif ($catalogUpdated) { "updated" }
        else { "loaded" }
    } else {
        $null
    }
    catalogMatchedCount = if ($VerifyAgainstCatalog) { $catalogMatchedCount } else { $null }
    catalogMismatchCount = if ($VerifyAgainstCatalog) { $catalogMismatchCount } else { $null }
    catalogMissingCount = if ($VerifyAgainstCatalog) { $catalogMissingCount } else { $null }
    catalogUpdated = if ($UpdateLocalCatalog) { $catalogUpdated } else { $null }
    items = $entries
    markdownSummary = $markdownSummary
    catalogSnippet = $catalogSnippet
}

$json = $report | ConvertTo-Json -Depth 5

if ($UpdateLocalCatalog -and $catalogUpdated -and $null -ne $catalogDocument) {
    Save-CatalogDocument -Path $CatalogArtifactPath -Document $catalogDocument
}

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

if (($UpdateLocalCatalog -or $BootstrapCatalogIfMissing) -and -not $catalogExists) {
    exit 3
}

if ($VerifyAgainstCatalog -and $catalogMismatchCount -gt 0) {
    exit 2
}
