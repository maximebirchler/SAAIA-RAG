param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$OutputDirectory = $PSScriptRoot
)

# Execute only two extracted function definitions, never the harness entry point.
# All HTTP is stubbed locally. These are counterexamples, not runtime acceptance.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $Repository 'tools/runtime-ci-harness.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Harness parse failed.' }
foreach ($name in @('Test-Reproducibility', 'Invoke-RuntimeCompletionProbe')) {
    $function = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) |
        Where-Object Name -EQ $name | Select-Object -First 1
    if ($null -eq $function) { throw "Function missing: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Invoke-HttpJson {
    param($Method, $Url, $Body, $TimeoutSeconds)
    $script:observedRequest = $Body
    return @{ ok = $true; statusCode = 200; body = '{"choices":[{"message":{"content":"stub"}}]}' }
}
function Get-TextTail { param($Text, $Lines) return 'stub response omitted' }

$noSamples = Test-Reproducibility -Runs @() -TtftTolerancePercent 10 -TokPerSecTolerancePercent 10
$missingThroughput = Test-Reproducibility -Runs @(
    @{ ttftMs = 100; tokPerSec = $null }, @{ ttftMs = 100; tokPerSec = $null }
) -TtftTolerancePercent 10 -TokPerSecTolerancePercent 10
$completion = Invoke-RuntimeCompletionProbe -BaseUrl 'http://offline.invalid' -TimeoutSeconds 1
$cases = @(
    [pscustomobject]@{ id = 'no_samples'; result = $noSamples; counterexampleObserved = ($noSamples.ok -eq $true) },
    [pscustomobject]@{ id = 'no_throughput'; result = $missingThroughput; counterexampleObserved = ($missingThroughput.ok -eq $true -and $missingThroughput.tokPerSecDriftPercent -eq 0) },
    [pscustomobject]@{
        id = 'non_streaming_ttft'; stream = $script:observedRequest.stream
        result = $completion
        counterexampleObserved = ($script:observedRequest.stream -eq $false -and $completion.ttftMs -eq $completion.elapsedMs)
    }
)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[pscustomobject]@{
    subject = 'tools/runtime-ci-harness.ps1'
    subjectSha256 = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash
    executionMode = 'AST-extracted functions, local HTTP stub, no model or network'
    cases = $cases
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'harness-counterexamples.json') -Encoding utf8
if (@($cases | Where-Object { -not $_.counterexampleObserved }).Count -ne 0) {
    throw 'A documented counterexample was not reproduced. Re-audit the harness.'
}
Write-Output 'Three diagnostic counterexamples reproduced offline. This confirms defects; it does not validate the product.'
