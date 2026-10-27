# Exercise the actual pure formatting function without starting a runtime.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$parseTokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'invoke-wire-qualification.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Qualification script has syntax errors.' }
$functions = @($ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Get-WireCompletionMessage'
}, $true))
if ($functions.Count -ne 1) { throw 'One actual completion function is required.' }
. ([scriptblock]::Create($functions[0].Extent.Text))
foreach ($variant in @('positive', 'native-raw-sha-correction')) {
    $message = Get-WireCompletionMessage $variant 18
    if ($message -notmatch '^Exact documentary bytes reached' -or $message -notmatch 'owned runtime stopped\.$') {
        throw "Positive transport result was not reported for $variant."
    }
}
foreach ($entry in @(@('source-hash-forged', 0), @('output-limit', 4), @('invented-citation', 20))) {
    $message = Get-WireCompletionMessage $entry[0] $entry[1]
    if ($message -match 'Exact documentary bytes' -or
        $message -notmatch "^Expected refusal validated; $($entry[1]) provider POST\(s\), no persisted answer; owned runtime stopped\.$") {
        throw "Expected refusal was incorrectly reported for $($entry[0])."
    }
}
Write-Output '5 completion-report checks passed without starting any runtime.'
