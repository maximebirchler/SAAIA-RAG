#requires -Version 7.4
param([Parameter(Mandatory)][string]$Specification)
$ErrorActionPreference = 'Stop'
$spec = Get-Content -LiteralPath $Specification -Raw | ConvertFrom-Json
$info = [Diagnostics.ProcessStartInfo]::new($spec.executable)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($argument in $spec.arguments) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$stdoutFile = [IO.FileStream]::new($spec.stdout, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
$stderrFile = [IO.FileStream]::new($spec.stderr, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, $true)
try {
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderr = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    $process.WaitForExit()
    $stdout.GetAwaiter().GetResult()
    $stderr.GetAwaiter().GetResult()
} finally {
    $stdoutFile.Dispose()
    $stderrFile.Dispose()
}
exit $process.ExitCode
