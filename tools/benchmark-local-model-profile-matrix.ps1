[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaBenchPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$RunName = "model-profile-matrix",
    [string]$DeviceId = "CUDA0",
    [int]$GpuLayers = 65,
    [int]$PromptTokens = 256,
    [int]$GenerationTokens = 64,
    [int]$Repetitions = 1,
    [int]$SampleIntervalMilliseconds = 500,
    [int]$CooldownTemperatureC = 72,
    [int]$MaxCooldownSeconds = 90,
    [string[]]$ProfileIds = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Limit-Integer {
    param(
        [int]$Value,
        [int]$Minimum,
        [int]$Maximum
    )

    return [math]::Min($Maximum, [math]::Max($Minimum, $Value))
}

function New-Profile {
    param(
        [string]$Id,
        [int]$Batch,
        [int]$Ubatch,
        [int]$Threads,
        [string]$FlashAttention,
        [string]$CacheTypeK,
        [string]$CacheTypeV
    )

    [pscustomobject]@{
        id = $Id
        batch = $Batch
        ubatch = $Ubatch
        threads = $Threads
        flashAttention = $FlashAttention
        cacheTypeK = $CacheTypeK
        cacheTypeV = $CacheTypeV
    }
}

function Read-NvidiaTelemetry {
    $command = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    try {
        $line = & $command.Source `
            --query-gpu=memory.used,memory.free,utilization.gpu,temperature.gpu,pstate `
            --format=csv,noheader,nounits 2>$null |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($line)) {
            return $null
        }

        $parts = @($line -split "," | ForEach-Object { $_.Trim() })
        if ($parts.Count -lt 5) {
            return $null
        }

        return [pscustomobject]@{
            memoryUsedMiB = [int]$parts[0]
            memoryFreeMiB = [int]$parts[1]
            utilizationPercent = [int]$parts[2]
            temperatureC = [int]$parts[3]
            performanceState = $parts[4]
        }
    }
    catch {
        return $null
    }
}

function Get-BenchmarkMeasurement {
    param(
        [object[]]$Items,
        [int]$ExpectedPromptTokens,
        [int]$ExpectedGenerationTokens
    )

    $prompt = $Items |
        Where-Object {
            [int]$_.n_prompt -eq $ExpectedPromptTokens -and
            [int]$_.n_gen -eq 0
        } |
        Select-Object -First 1
    $generation = $Items |
        Where-Object {
            [int]$_.n_prompt -eq 0 -and
            [int]$_.n_gen -eq $ExpectedGenerationTokens
        } |
        Select-Object -First 1

    if ($null -eq $prompt -or $null -eq $generation) {
        throw "Expected prompt and generation measurements were not produced."
    }

    return [pscustomobject]@{
        promptTokensPerSecond = [double]$prompt.avg_ts
        promptStdDev = [double]$prompt.stddev_ts
        generationTokensPerSecond = [double]$generation.avg_ts
        generationStdDev = [double]$generation.stddev_ts
        rawPrompt = $prompt
        rawGeneration = $generation
    }
}

$resolvedBenchPath = [System.IO.Path]::GetFullPath($LlamaBenchPath)
$resolvedModelPath = [System.IO.Path]::GetFullPath($ModelPath)
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $resolvedBenchPath -PathType Leaf)) {
    throw "llama-bench not found: $resolvedBenchPath"
}
if (-not (Test-Path -LiteralPath $resolvedModelPath -PathType Leaf)) {
    throw "Model not found: $resolvedModelPath"
}
if ($PromptTokens -lt 1 -or $GenerationTokens -lt 1) {
    throw "PromptTokens and GenerationTokens must be positive."
}
if ($Repetitions -lt 1 -or $Repetitions -gt 10) {
    throw "Repetitions must be between 1 and 10."
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeRunName = (($RunName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeRunName)) {
    $safeRunName = "model-profile-matrix"
}

$profiles = @(
    (New-Profile "reference-t4" 1024 256 4 "on" "f16" "f16"),
    (New-Profile "reference-t6" 1024 256 6 "on" "f16" "f16"),
    (New-Profile "compact-t4" 512 128 4 "on" "f16" "f16"),
    (New-Profile "compact-t6" 512 128 6 "on" "f16" "f16"),
    (New-Profile "q8-kv-t4" 1024 256 4 "on" "q8_0" "q8_0"),
    (New-Profile "flash-off-t4" 1024 256 4 "off" "f16" "f16")
)
if ($ProfileIds.Count -gt 0) {
    $requestedProfileIds = @(
        $ProfileIds |
            ForEach-Object { $_ -split "," } |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $profiles = @($profiles | Where-Object { $_.id -in $requestedProfileIds })
    if ($profiles.Count -eq 0) {
        throw "None of the requested ProfileIds matched a known profile."
    }
}

$startedAt = Get-Date
$results = [System.Collections.Generic.List[object]]::new()
foreach ($profile in $profiles) {
    $cooldownStartedAt = Get-Date
    while (((Get-Date) - $cooldownStartedAt).TotalSeconds -lt [math]::Max(0, $MaxCooldownSeconds)) {
        $cooldownTelemetry = Read-NvidiaTelemetry
        if ($null -eq $cooldownTelemetry -or
            $cooldownTelemetry.temperatureC -le (Limit-Integer $CooldownTemperatureC 40 95)) {
            break
        }

        Write-Output (
            "PROFILE_COOLDOWN id={0} temperatureC={1}" -f
            $profile.id,
            $cooldownTelemetry.temperatureC)
        Start-Sleep -Seconds 2
    }

    $profileName = "$safeRunName-$($profile.id)"
    $stdoutPath = Join-Path $resolvedOutputDirectory "$profileName.stdout.json"
    $stderrPath = Join-Path $resolvedOutputDirectory "$profileName.stderr.log"
    $arguments = @(
        "-m", $resolvedModelPath,
        "-p", [string]$PromptTokens,
        "-n", [string]$GenerationTokens,
        "-b", [string]$profile.batch,
        "-ub", [string]$profile.ubatch,
        "-t", [string]$profile.threads,
        "-ngl", [string]$GpuLayers,
        "-sm", "none",
        "-dev", $DeviceId,
        "-fa", $profile.flashAttention,
        "-ctk", $profile.cacheTypeK,
        "-ctv", $profile.cacheTypeV,
        "-r", [string]$Repetitions,
        "--progress",
        "-o", "json"
    )

    Write-Output "PROFILE_START id=$($profile.id)"
    $profileStartedAt = Get-Date
    $process = Start-Process `
        -FilePath $resolvedBenchPath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path $resolvedBenchPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    $peakWorkingSetMiB = 0
    $peakGpuMemoryUsedMiB = $null
    $minimumGpuMemoryFreeMiB = $null
    $peakGpuUtilizationPercent = $null
    $peakGpuTemperatureC = $null
    while (-not $process.HasExited) {
        $process.Refresh()
        $peakWorkingSetMiB = [math]::Max(
            $peakWorkingSetMiB,
            [math]::Round($process.WorkingSet64 / 1MB, 2))
        $telemetry = Read-NvidiaTelemetry
        if ($null -ne $telemetry) {
            $peakGpuMemoryUsedMiB = if ($null -eq $peakGpuMemoryUsedMiB) {
                $telemetry.memoryUsedMiB
            } else {
                [math]::Max($peakGpuMemoryUsedMiB, $telemetry.memoryUsedMiB)
            }
            $minimumGpuMemoryFreeMiB = if ($null -eq $minimumGpuMemoryFreeMiB) {
                $telemetry.memoryFreeMiB
            } else {
                [math]::Min($minimumGpuMemoryFreeMiB, $telemetry.memoryFreeMiB)
            }
            $peakGpuUtilizationPercent = if ($null -eq $peakGpuUtilizationPercent) {
                $telemetry.utilizationPercent
            } else {
                [math]::Max($peakGpuUtilizationPercent, $telemetry.utilizationPercent)
            }
            $peakGpuTemperatureC = if ($null -eq $peakGpuTemperatureC) {
                $telemetry.temperatureC
            } else {
                [math]::Max($peakGpuTemperatureC, $telemetry.temperatureC)
            }
        }

        Start-Sleep -Milliseconds (Limit-Integer $SampleIntervalMilliseconds 100 5000)
    }

    $process.WaitForExit()
    $process.Refresh()
    $durationMs = [int]((Get-Date) - $profileStartedAt).TotalMilliseconds
    $rawItems = $null
    if ((Test-Path -LiteralPath $stdoutPath) -and
        (Get-Item -LiteralPath $stdoutPath).Length -gt 0) {
        try {
            $rawItems = Get-Content -LiteralPath $stdoutPath -Raw |
                ConvertFrom-Json
        }
        catch {
            $rawItems = $null
        }
    }

    $exitCode = $null
    try {
        $exitCode = $process.ExitCode
    }
    catch {
        $exitCode = $null
    }
    if ($null -eq $exitCode) {
        $exitCode = if ($null -ne $rawItems) { 0 } else { -1 }
    }

    if ($exitCode -ne 0) {
        $diagnostic = if (Test-Path -LiteralPath $stderrPath) {
            (Get-Content -LiteralPath $stderrPath -Tail 20) -join " | "
        } else {
            "llama-bench failed without a stderr log."
        }
        $results.Add([pscustomobject]@{
            profile = $profile
            succeeded = $false
            exitCode = $exitCode
            durationMs = $durationMs
            peakWorkingSetMiB = $peakWorkingSetMiB
            peakGpuMemoryUsedMiB = $peakGpuMemoryUsedMiB
            minimumGpuMemoryFreeMiB = $minimumGpuMemoryFreeMiB
            peakGpuUtilizationPercent = $peakGpuUtilizationPercent
            peakGpuTemperatureC = $peakGpuTemperatureC
            promptTokensPerSecond = $null
            promptStdDev = $null
            generationTokensPerSecond = $null
            generationStdDev = $null
            diagnostic = $diagnostic
            stdoutPath = $stdoutPath
            stderrPath = $stderrPath
        })
        Write-Output "PROFILE_FAILED id=$($profile.id) exitCode=$exitCode"
        continue
    }

    if ($null -eq $rawItems) {
        throw "llama-bench exited successfully but did not produce valid JSON: $stdoutPath"
    }
    $measurement = Get-BenchmarkMeasurement `
        -Items $rawItems `
        -ExpectedPromptTokens $PromptTokens `
        -ExpectedGenerationTokens $GenerationTokens
    $results.Add([pscustomobject]@{
        profile = $profile
        succeeded = $true
        exitCode = $exitCode
        durationMs = $durationMs
        peakWorkingSetMiB = $peakWorkingSetMiB
        peakGpuMemoryUsedMiB = $peakGpuMemoryUsedMiB
        minimumGpuMemoryFreeMiB = $minimumGpuMemoryFreeMiB
        peakGpuUtilizationPercent = $peakGpuUtilizationPercent
        peakGpuTemperatureC = $peakGpuTemperatureC
        promptTokensPerSecond = $measurement.promptTokensPerSecond
        promptStdDev = $measurement.promptStdDev
        generationTokensPerSecond = $measurement.generationTokensPerSecond
        generationStdDev = $measurement.generationStdDev
        diagnostic = $null
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
    })
    Write-Output (
        "PROFILE_DONE id={0} promptTokSec={1:0.###} generationTokSec={2:0.###} peakVramMiB={3} peakTempC={4}" -f
        $profile.id,
        $measurement.promptTokensPerSecond,
        $measurement.generationTokensPerSecond,
        $peakGpuMemoryUsedMiB,
        $peakGpuTemperatureC)
}

$successful = @($results | Where-Object { $_.succeeded })
$ranked = @($successful | Sort-Object @{
    Expression = {
        ($PromptTokens / $_.promptTokensPerSecond) +
        ($GenerationTokens / $_.generationTokensPerSecond)
    }
})
$artifact = [ordered]@{
    schemaVersion = 1
    capturedAt = (Get-Date).ToUniversalTime().ToString("o")
    runName = $safeRunName
    runtime = [ordered]@{
        llamaBenchPath = $resolvedBenchPath
        modelPath = $resolvedModelPath
        deviceId = $DeviceId
        gpuLayers = $GpuLayers
        promptTokens = $PromptTokens
        generationTokens = $GenerationTokens
        repetitions = $Repetitions
        sampleIntervalMilliseconds = $SampleIntervalMilliseconds
        cooldownTemperatureC = $CooldownTemperatureC
        maxCooldownSeconds = $MaxCooldownSeconds
    }
    durationMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    profileCount = $profiles.Count
    successfulProfileCount = $successful.Count
    winnerProfileId = if ($ranked.Count -gt 0) { $ranked[0].profile.id } else { $null }
    results = $results
}
$artifactPath = Join-Path $resolvedOutputDirectory "$safeRunName-summary.json"
$artifact | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
$artifact | ConvertTo-Json -Depth 20
