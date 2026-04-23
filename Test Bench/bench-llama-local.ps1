param(
    [string]$LlamaServerPath = "$env:LOCALAPPDATA\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
    [string]$ModelPath       = "C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG\models\Qwen2.5-3B-Instruct-Q4_K_M.gguf",
    [string]$ServerHost      = "127.0.0.1",
    [int]$BasePort           = 18234,
    [int]$MaxTokens          = 120,
    [switch]$KeepLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

# =========================
# Helpers
# =========================

function Write-Section([string]$Text) {
    Write-Host ""
    Write-Host ("=" * 80) -ForegroundColor DarkGray
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ("=" * 80) -ForegroundColor DarkGray
}

function Wait-ForReady {
    param([string]$BaseUrl, [int]$TimeoutSeconds = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-RestMethod -Method GET -Uri "$BaseUrl/v1/models" -TimeoutSec 5 -ErrorAction Stop
            if ($resp -and $resp.data -and $resp.data.Count -ge 1) { return $resp }
        } catch { Start-Sleep -Milliseconds 800 }
    }
    throw "Timeout: serveur non pret dans ${TimeoutSeconds}s."
}

function Stop-LlamaProcess {
    param([System.Diagnostics.Process]$Process)
    if (-not $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
        }
    } catch { Write-Warning "Stop echoue PID=$($Process.Id): $($_.Exception.Message)" }
}

function Start-LlamaServerProcess {
    param([string]$ExePath, [string]$ModelPath, [string]$ServerHost, [int]$Port, [hashtable]$Profile, [string]$LogDir)

    $stdoutLog = Join-Path $LogDir "$($Profile.Name)-stdout.log"
    $stderrLog = Join-Path $LogDir "$($Profile.Name)-stderr.log"

    $argList = [System.Collections.Generic.List[string]]::new()
    $argList.Add("--model");    $argList.Add($ModelPath)
    $argList.Add("--host");     $argList.Add($ServerHost)
    $argList.Add("--port");     $argList.Add([string]$Port)
    $argList.Add("--threads");  $argList.Add([string]$Profile.Threads)
    $argList.Add("--ctx-size"); $argList.Add([string]$Profile.Ctx)
    $argList.Add("--batch-size"); $argList.Add([string]$Profile.Batch)
    $argList.Add("--n-gpu-layers"); $argList.Add([string]$Profile.Ngl)

    if ($Profile.ContainsKey("UBatch") -and $null -ne $Profile.UBatch) {
        $argList.Add("--ubatch-size"); $argList.Add([string]$Profile.UBatch)
    }
    if ($Profile.ContainsKey("ThreadsBatch") -and $null -ne $Profile.ThreadsBatch) {
        $argList.Add("--threads-batch"); $argList.Add([string]$Profile.ThreadsBatch)
    }
    if ($Profile.ContainsKey("FlashAttn") -and $null -ne $Profile.FlashAttn) {
        $argList.Add("--flash-attn")
        $argList.Add($(if ($Profile.FlashAttn) { "on" } else { "off" }))
    }
    if ($Profile.ContainsKey("Mlock") -and $Profile.Mlock) {
        $argList.Add("--mlock")
    }

    $cmdLine = "`"$ExePath`" " + ((@($argList | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join " "))
    Set-Content -Path (Join-Path $LogDir "$($Profile.Name)-command.txt") -Value $cmdLine -Encoding UTF8
    Write-Host "  CMD: $cmdLine" -ForegroundColor DarkYellow

    $proc = Start-Process -FilePath $ExePath -ArgumentList $argList -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
    return @{ Process = $proc; StdOutLog = $stdoutLog; StdErrLog = $stderrLog; Command = $cmdLine }
}

function Get-ModelId {
    param($ModelsResp)
    if ($ModelsResp.data -and $ModelsResp.data.Count -ge 1 -and $ModelsResp.data[0].id) {
        return [string]$ModelsResp.data[0].id
    }
    throw "Impossible de lire model id depuis /v1/models."
}

function Invoke-StreamingTtft {
    param([string]$BaseUrl, [string]$ModelId, [string]$Prompt, [int]$MaxTokens)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client  = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)

    try {
        $payload = @{
            model       = $ModelId
            stream      = $true
            temperature = 0
            max_tokens  = $MaxTokens
            messages    = @(
                @{ role = "system"; content = "Reponds en francais, de facon concise et factuelle." }
                @{ role = "user";   content = $Prompt }
            )
        } | ConvertTo-Json -Depth 8 -Compress

        $content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, "application/json")
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/v1/chat/completions")
        $request.Content = $content

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resp = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        [void]$resp.EnsureSuccessStatusCode()

        $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)

        $ttftMs  = $null
        $fullTxt = [System.Text.StringBuilder]::new()

        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if (-not $line.StartsWith("data: ")) { continue }
            $data = $line.Substring(6).Trim()
            if ($data -eq "[DONE]") { break }
            try {
                $obj = $data | ConvertFrom-Json
                foreach ($choice in @($obj.choices)) {
                    $delta = $choice.delta
                    if (-not $delta) { continue }

                    $contentProperty = $delta.PSObject.Properties["content"]
                    if (-not $contentProperty -or $null -eq $contentProperty.Value) { continue }

                    $piece = $null
                    $contentValue = $contentProperty.Value
                    if ($contentValue -is [string]) {
                        $piece = [string]$contentValue
                    } elseif ($contentValue -is [System.Array]) {
                        $piece = (($contentValue | ForEach-Object {
                            $textProperty = $_.PSObject.Properties["text"]
                            if ($textProperty -and $null -ne $textProperty.Value) { [string]$textProperty.Value }
                        }) -join "")
                    }

                    if ($piece) {
                        if ($null -eq $ttftMs) { $ttftMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 0) }
                        [void]$fullTxt.Append($piece)
                    }
                }
            } catch { }
        }
        $sw.Stop()
        return [pscustomobject]@{ TtftMs = $ttftMs; StreamMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 0); Text = $fullTxt.ToString() }
    } finally { $client.Dispose() }
}

function Invoke-NonStreamingCompletion {
    param([string]$BaseUrl, [string]$ModelId, [string]$Prompt, [int]$MaxTokens)

    $body = @{
        model       = $ModelId
        stream      = $false
        temperature = 0
        max_tokens  = $MaxTokens
        messages    = @(
            @{ role = "system"; content = "Reponds en francais, de facon concise et factuelle." }
            @{ role = "user";   content = $Prompt }
        )
    } | ConvertTo-Json -Depth 8 -Compress

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = Invoke-RestMethod -Method POST -Uri "$BaseUrl/v1/chat/completions" -ContentType "application/json" -Body $body -TimeoutSec 300
    $sw.Stop()

    $text = ""
    if ($resp.choices -and $resp.choices.Count -gt 0 -and $resp.choices[0].message -and $resp.choices[0].message.content) {
        $text = [string]$resp.choices[0].message.content
    }
    $completionTokens = $null
    $promptTokens     = $null
    if ($resp.usage) {
        if ($resp.usage.completion_tokens) { $completionTokens = [int]$resp.usage.completion_tokens }
        if ($resp.usage.prompt_tokens)     { $promptTokens     = [int]$resp.usage.prompt_tokens }
    }
    if ($null -eq $completionTokens -or $completionTokens -le 0) {
        $completionTokens = [math]::Max(1, [int][math]::Round($text.Length / 4.0, 0))
    }
    $elapsedSec = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
    $tokPerSec  = [math]::Round($completionTokens / $elapsedSec, 2)
    return [pscustomobject]@{
        CompletionMs     = [math]::Round($sw.Elapsed.TotalMilliseconds, 0)
        CompletionSec    = [math]::Round($elapsedSec, 3)
        CompletionTokens = $completionTokens
        PromptTokens     = $promptTokens
        TokPerSec        = $tokPerSec
        Text             = $text
    }
}

function New-TestPrompt {
    $base = @"
Tu es un assistant technique pour documentation industrielle.

Je vais te donner un contexte long afin de simuler un vrai prompt RAG avec un prefill non trivial. Lis tout attentivement, puis reponds en 6 puces maximum.

Contexte :
- Une application WinUI utilise un LLM local via llama.cpp.
- Le but est de comparer plusieurs profils runtime sur une machine Windows avec GPU discret 4 Go VRAM.
- Les parametres cles a comparer sont : n-gpu-layers, batch-size, ubatch-size, threads, threads-batch, ctx-size et flash-attn.
- Le systeme doit rester fluide et ne pas bloquer l'interface.
- On veut une reponse courte, structuree et strictement liee au contexte.
- Il ne faut pas inventer de chiffres absents du contexte.
- Les priorites produit sont : stabilite, TTFT bas, bon debit, marge memoire, profil degrade propre, fallback propre.

Rappel :
- Ne fais pas de roman.
- Ne donne pas d'avertissement generique.
- Reponds en francais.
- Donne d'abord le diagnostic, puis l'action recommandee.
"@
    return (($base + "`n`n") * 8) + "`nQuestion finale : quel compromis recommandes-tu pour une machine 4 Go VRAM et pourquoi ?"
}

function Run-BenchmarkProfile {
    param([hashtable]$Profile, [string]$ExePath, [string]$ModelPath, [string]$ServerHost,
          [int]$Port, [string]$Prompt, [int]$MaxTokens, [string]$OutputDir)

    $baseUrl     = "http://$ServerHost`:$Port"
    $server      = $null
    $processInfo = $null

    try {
        Write-Section "Profil: $($Profile.Name)"

        $startTime   = Get-Date
        $processInfo = Start-LlamaServerProcess -ExePath $ExePath -ModelPath $ModelPath -ServerHost $ServerHost -Port $Port -Profile $Profile -LogDir $OutputDir
        $server      = $processInfo.Process

        Write-Host "  En attente que le serveur soit pret (max 180s)..." -ForegroundColor Gray
        $models  = Wait-ForReady -BaseUrl $baseUrl -TimeoutSeconds 180
        $loadMs  = [math]::Round(((Get-Date) - $startTime).TotalMilliseconds, 0)
        $modelId = Get-ModelId -ModelsResp $models
        Write-Host "  PRET. modelId=$modelId | loadTime=${loadMs}ms" -ForegroundColor Green

        # VRAM apres chargement
        $vramAfter = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>&1
        Write-Host "  VRAM utilisee apres chargement: ${vramAfter} MiB" -ForegroundColor Magenta

        Write-Host "  Test TTFT (streaming)..." -ForegroundColor Gray
        $streamRes = Invoke-StreamingTtft -BaseUrl $baseUrl -ModelId $modelId -Prompt $Prompt -MaxTokens $MaxTokens
        Write-Host "  TTFT = $($streamRes.TtftMs) ms | Duree totale stream = $($streamRes.StreamMs) ms" -ForegroundColor Green

        Write-Host "  Test tok/s (non-streaming, usage tokens)..." -ForegroundColor Gray
        $completionRes = Invoke-NonStreamingCompletion -BaseUrl $baseUrl -ModelId $modelId -Prompt $Prompt -MaxTokens $MaxTokens
        Write-Host "  Completion = $($completionRes.CompletionMs) ms | tok/s = $($completionRes.TokPerSec) | tokens = $($completionRes.CompletionTokens)" -ForegroundColor Green

        $samplePath = Join-Path $OutputDir "$($Profile.Name)-sample-response.txt"
        Set-Content -Path $samplePath -Value $completionRes.Text -Encoding UTF8

        return [pscustomobject]@{
            Profile          = $Profile.Name
            LoadMs           = $loadMs
            VramUsedMiB      = ($vramAfter -as [int])
            TtftMs           = $streamRes.TtftMs
            StreamMs         = $streamRes.StreamMs
            CompletionMs     = $completionRes.CompletionMs
            TokPerSec        = $completionRes.TokPerSec
            CompletionTokens = $completionRes.CompletionTokens
            PromptTokens     = $completionRes.PromptTokens
            Ngl              = $Profile.Ngl
            Batch            = $Profile.Batch
            UBatch           = $Profile.UBatch
            Ctx              = $Profile.Ctx
            Threads          = $Profile.Threads
            ThreadsBatch     = $Profile.ThreadsBatch
            FlashAttn        = $Profile.FlashAttn
            Error            = $null
        }
    } catch {
        Write-Warning "  ECHEC profil $($Profile.Name): $($_.Exception.Message)"
        $errMsg = $_.Exception.Message
        # Afficher les dernieres lignes stderr si disponible
        if ($processInfo -and (Test-Path $processInfo.StdErrLog)) {
            $lastLines = Get-Content $processInfo.StdErrLog -Tail 20 -ErrorAction SilentlyContinue
            if ($lastLines) {
                Write-Host "  --- Dernieres lignes stderr ---" -ForegroundColor Red
                $lastLines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkRed }
            }
        }
        return [pscustomobject]@{
            Profile          = $Profile.Name
            LoadMs           = $null
            VramUsedMiB      = $null
            TtftMs           = $null
            StreamMs         = $null
            CompletionMs     = $null
            TokPerSec        = $null
            CompletionTokens = $null
            PromptTokens     = $null
            Ngl              = $Profile.Ngl
            Batch            = $Profile.Batch
            UBatch           = $Profile.UBatch
            Ctx              = $Profile.Ctx
            Threads          = $Profile.Threads
            ThreadsBatch     = $Profile.ThreadsBatch
            FlashAttn        = $Profile.FlashAttn
            Error            = $errMsg
        }
    } finally {
        if ($server) {
            Write-Host "  Arret du serveur..." -ForegroundColor Gray
            Stop-LlamaProcess -Process $server
        }
    }
}

# =========================
# Main
# =========================

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDir = Join-Path (Split-Path $PSCommandPath -Parent) "llm-bench-$timestamp"
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Section "SAAIA LLM Benchmark -- 3.5"
Write-Host "llama-server : $LlamaServerPath"
Write-Host "Modele       : $ModelPath"
Write-Host "Output       : $outputDir"

if (-not (Test-Path $LlamaServerPath)) { throw "llama-server introuvable: $LlamaServerPath" }
if (-not (Test-Path $ModelPath))       { throw "Modele introuvable: $ModelPath" }

# VRAM initiale
$vramFree0 = & nvidia-smi --query-gpu=memory.free,memory.total --format=csv,noheader 2>&1
Write-Host "VRAM initiale: $vramFree0"

$prompt = New-TestPrompt
$promptLen = $prompt.Length
Write-Host "Longueur prompt test: $promptLen chars"

$profiles = @(
    @{
        Name         = "A_baseline_ngl24_b192"
        Ngl          = 24
        Batch        = 192
        UBatch       = $null
        Threads      = 6
        ThreadsBatch = $null
        Ctx          = 4096
        FlashAttn    = $false
        Mlock        = $false
    },
    @{
        Name         = "B_candidate_ngl36_b1024_fa"
        Ngl          = 36
        Batch        = 1024
        UBatch       = 256
        Threads      = 6
        ThreadsBatch = 6
        Ctx          = 3072
        FlashAttn    = $true
        Mlock        = $false
    },
    @{
        Name         = "C_candidate_ngl36_b1024_nofa"
        Ngl          = 36
        Batch        = 1024
        UBatch       = 256
        Threads      = 6
        ThreadsBatch = 6
        Ctx          = 3072
        FlashAttn    = $false
        Mlock        = $false
    }
)

$results = [System.Collections.Generic.List[object]]::new()

for ($i = 0; $i -lt $profiles.Count; $i++) {
    $profile = $profiles[$i]
    $port    = $BasePort + $i
    $res = Run-BenchmarkProfile `
        -Profile $profile `
        -ExePath $LlamaServerPath `
        -ModelPath $ModelPath `
        -ServerHost $ServerHost `
        -Port $port `
        -Prompt $prompt `
        -MaxTokens $MaxTokens `
        -OutputDir $outputDir
    $results.Add($res)

    if ($i -lt ($profiles.Count - 1)) {
        Write-Host "  Pause 3s entre profils..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 3
    }
}

Write-Section "RESULTATS"
$results | Format-Table Profile, LoadMs, VramUsedMiB, TtftMs, TokPerSec, CompletionTokens, Ngl, Batch, UBatch, Ctx, FlashAttn, Error -AutoSize

$jsonPath = Join-Path $outputDir "bench-results.json"
$csvPath  = Join-Path $outputDir "bench-results.csv"
$results | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath -Encoding UTF8
$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "JSON: $jsonPath" -ForegroundColor Yellow
Write-Host "CSV : $csvPath"  -ForegroundColor Yellow
Write-Host "Logs: $outputDir" -ForegroundColor DarkGray

# Analyse rapide
Write-Section "ANALYSE RAPIDE"
$a = $results | Where-Object { $_.Profile -like "A_*" }
$b = $results | Where-Object { $_.Profile -like "B_*" }
$c = $results | Where-Object { $_.Profile -like "C_*" }

if ($a -and $b -and $a.TtftMs -and $b.TtftMs) {
    $ttftDelta = [math]::Round(($a.TtftMs - $b.TtftMs) / $a.TtftMs * 100, 1)
    $tokDelta  = if ($a.TokPerSec -and $b.TokPerSec) { [math]::Round(($b.TokPerSec - $a.TokPerSec) / $a.TokPerSec * 100, 1) } else { "N/A" }
    Write-Host "Baseline vs Candidat (avec flash-attn):"
    Write-Host "  TTFT:   $($a.TtftMs) ms  ->  $($b.TtftMs) ms  (delta: ${ttftDelta}%)" -ForegroundColor $(if ($ttftDelta -gt 0) { "Green" } else { "Red" })
    Write-Host "  tok/s:  $($a.TokPerSec)  ->  $($b.TokPerSec)  (delta: ${tokDelta}%)" -ForegroundColor $(if ($tokDelta -gt 0) { "Green" } else { "Red" })
}
if ($c -and $a -and $a.TtftMs -and $c.TtftMs) {
    $ttftDeltaC = [math]::Round(($a.TtftMs - $c.TtftMs) / $a.TtftMs * 100, 1)
    $tokDeltaC  = if ($a.TokPerSec -and $c.TokPerSec) { [math]::Round(($c.TokPerSec - $a.TokPerSec) / $a.TokPerSec * 100, 1) } else { "N/A" }
    Write-Host "Baseline vs Candidat (sans flash-attn):"
    Write-Host "  TTFT:   $($a.TtftMs) ms  ->  $($c.TtftMs) ms  (delta: ${ttftDeltaC}%)" -ForegroundColor $(if ($ttftDeltaC -gt 0) { "Green" } else { "Red" })
    Write-Host "  tok/s:  $($a.TokPerSec)  ->  $($c.TokPerSec)  (delta: ${tokDeltaC}%)" -ForegroundColor $(if ($tokDeltaC -gt 0) { "Green" } else { "Red" })
}
if ($b -and $c -and $b.TtftMs -and $c.TtftMs) {
    Write-Host "Impact flash-attn seul (B vs C, memes autres params):"
    $faImpact = [math]::Round(($c.TtftMs - $b.TtftMs) / $c.TtftMs * 100, 1)
    Write-Host "  TTFT avec FA: $($b.TtftMs) ms  |  sans FA: $($c.TtftMs) ms  (FA gagne ${faImpact}%)" -ForegroundColor $(if ($faImpact -gt 0) { "Green" } else { "Yellow" })
}
if ($b -and $null -ne $b.Error) {
    Write-Host "Note: profil B (avec flash-attn) a echoue -> voir log. Profil C (sans FA) est le vrai candidat." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Bench termine. Consultez $outputDir pour les logs complets." -ForegroundColor Cyan
