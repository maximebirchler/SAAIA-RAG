Set-StrictMode -Version Latest

function Invoke-ProbeHttpJson {
    param ([string]$Url, [int]$TimeoutSeconds = 30)
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    try {
        $response = $client.GetAsync($Url, $cancellation.Token).GetAwaiter().GetResult()
        try {
            return @{
                ok = $response.IsSuccessStatusCode
                statusCode = [int]$response.StatusCode
                elapsedMs = $timer.Elapsed.TotalMilliseconds
            }
        } finally {
            $response.Dispose()
        }
    } catch {
        return @{
            ok = $false
            error = $_.Exception.GetType().Name
            elapsedMs = $timer.Elapsed.TotalMilliseconds
        }
    } finally {
        $client.Dispose()
        $cancellation.Dispose()
    }
}

function Read-CompletionEvent {
    param ([string]$Line)
    if (-not $Line.StartsWith('data:')) {
        return $null
    }

    $data = $Line.Substring(5).Trim()
    if (-not $data -or $data -eq '[DONE]') {
        return $null
    }

    return $data | ConvertFrom-Json
}

function Invoke-RuntimeCompletionProbe {
    param ([string]$BaseUrl, [int]$TimeoutSeconds = 30, [string]$Model = 'local', [switch]$NonStreaming)
    $streaming = -not $NonStreaming
    $body = @{
        model = $Model
        stream = $streaming
        max_tokens = 24
        temperature = 0
        messages = @(@{
                role = 'user'
                content = 'Reply with exactly: SAAIA_RUNTIME_CI_OK'
            })
    }

    if ($streaming) {
        $body.stream_options = @{
            include_usage = $true
        }
    }

    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $BaseUrl.TrimEnd('/')+'/v1/chat/completions')
    $request.Content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8, 'application/json')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $result = [ordered]@{
        ok = $false
        statusCode = $null
        elapsedMs = $null
        ttftMs = $null
        ttftState = 'unavailable'
        ttftDefinition = 'First non-empty streamed content delta received by this client'
        tokPerSec = $null
        throughputState = 'unavailable'
        throughputDefinition = 'Reported output tokens minus first token, divided by first content delta to stream end, including transport'
        outputTokens = $null
        model = $null
        streaming = $streaming
        error = $null
    }

    $response = $null
    try {
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancellation.Token).GetAwaiter().GetResult()
        $result.statusCode = [int]$response.StatusCode
        if (-not $response.IsSuccessStatusCode) {
            $result.error = 'http_error'
            return $result
        }

        if (-not $streaming) {
            $parsed = $response.Content.ReadAsStringAsync($cancellation.Token).GetAwaiter().GetResult() | ConvertFrom-Json
            if ($parsed.PSObject.Properties['model']) {
                $result.model = $parsed.model
            }

            $result.ok = $parsed.PSObject.Properties['choices'] -and @($parsed.choices).Count -gt 0
            # Buffered response cannot provide TTFT or decoding throughput.
            return $result
        }

        $reader = [IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        $hasContent = $false
        $finished = $false
        try {
            while ($null -ne ($line = $reader.ReadLineAsync($cancellation.Token).GetAwaiter().GetResult())) {
                if ($line.Trim() -eq 'data: [DONE]') {
                    $finished = $true
                    break
                }

                $event = Read-CompletionEvent $line
                if ($null -eq $event) {
                    continue
                }

                if ($event.PSObject.Properties['model']) {
                    $result.model = $event.model
                }

                if ($event.PSObject.Properties['usage'] -and $null -ne $event.usage -and $event.usage.PSObject.Properties['completion_tokens']) {
                    $result.outputTokens = [int]$event.usage.completion_tokens
                }

                if (-not $event.PSObject.Properties['choices']) {
                    continue
                }

                foreach ($choice in @($event.choices)) {
                    if ($choice.PSObject.Properties['finish_reason'] -and $null -ne $choice.finish_reason) {
                        $finished = $true
                    }

                    if ($choice.PSObject.Properties['delta'] -and $choice.delta.PSObject.Properties['content'] -and -not [string]::IsNullOrEmpty([string]$choice.delta.content)) {
                        $hasContent = $true
                        if ($null -eq $result.ttftMs) {
                            $result.ttftMs = $timer.Elapsed.TotalMilliseconds
                            $result.ttftState = 'available'
                        }
                    }
                }
            }
        } finally {
            $reader.Dispose()
        }

        $result.ok = $hasContent -and $finished
        $result.elapsedMs = $timer.Elapsed.TotalMilliseconds
        if ($result.ok -and $null -ne $result.outputTokens -and $result.outputTokens -gt 1 -and $result.elapsedMs -gt $result.ttftMs) {
            $result.tokPerSec = ($result.outputTokens-1)*1000.0/($result.elapsedMs-$result.ttftMs)
            $result.throughputState = 'available'
        }

        if (-not $result.ok) {
            $result.error = 'incomplete_stream'
        }

        return $result
    } catch {
        $result.error = $_.Exception.GetType().Name
        return $result
    } finally {
        $result.elapsedMs = $timer.Elapsed.TotalMilliseconds
        if ($null -ne $response) {
            $response.Dispose()
        }

        $request.Dispose()
        $client.Dispose()
        $cancellation.Dispose()
    }
}

function Test-Reproducibility {
    param ([AllowEmptyCollection()][object[]]$Runs = @(), [double]$TtftTolerancePercent = 25, [double]$TokPerSecTolerancePercent = 25)
    $result = [ordered]@{
        ok = $false
        reason = $null
        sampleCount = @($Runs).Count
        ttftDriftPercent = $null
        tokPerSecDriftPercent = $null
        ttftTolerancePercent = $TtftTolerancePercent
        tokPerSecTolerancePercent = $TokPerSecTolerancePercent
    }

    if (@($Runs).Count -lt 2) {
        $result.reason = 'insufficient_samples'
        return $result
    }

    if (@($Runs | Where-Object {
                -not (Get-ProbeField $_ 'ok')
            }
        ).Count -gt 0) {
        $result.reason = 'unsuccessful_sample'
        return $result
    }

    $values = @{}

    foreach ($metric in @('ttftMs', 'tokPerSec')) {
        $values[$metric] = @()
        foreach ($run in $Runs) {
            $raw = Get-ProbeField $run $metric
            $number = [double]0
            if ($null -eq $raw -or -not [double]::TryParse([string]$raw, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -le 0 -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) {
                $result.reason = 'unavailable_metric'
                return $result
            }

            $values[$metric]+=$number
        }
    }

    $ttft = $values.ttftMs
    $tok = $values.tokPerSec
    $avgTtft = ($ttft | Measure-Object -Average).Average
    $avgTok = ($tok | Measure-Object -Average).Average
    $result.ttftDriftPercent = [Math]::Round(((($ttft | Measure-Object -Maximum).Maximum-$avgTtft)*100.0/$avgTtft), 2)
    $result.tokPerSecDriftPercent = [Math]::Round((($avgTok-($tok | Measure-Object -Minimum).Minimum)*100.0/$avgTok), 2)
    $result.ok = $result.ttftDriftPercent -le $TtftTolerancePercent -and $result.tokPerSecDriftPercent -le $TokPerSecTolerancePercent
    $result.reason = if ($result.ok) {
        'within_tolerance'
    } else {
        'tolerance_exceeded'
    }

    return $result
}

function Get-ProbeField {
    param ($Value, [string]$Name)
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Contains($Name)) {
            return $Value[$Name]
        }

        return $null
    }

    if ($null -ne $Value -and $Value.PSObject.Properties[$Name]) {
        return $Value.$Name
    }

    return $null
}

Export-ModuleMember -Function Invoke-ProbeHttpJson, Invoke-RuntimeCompletionProbe, Test-Reproducibility, Read-CompletionEvent
