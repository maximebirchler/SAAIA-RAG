param()

$ErrorActionPreference = "Stop"

function Backup-File($path) {
  if (Test-Path $path) {
    $ts = Get-Date -Format "yyyyMMdd_HHmmss"
    Copy-Item $path "$path.bak_$ts" -Force
    Write-Host "Backup created: $path.bak_$ts"
  }
}

$target = "client\SAAIA.Client.WinUI\Services\LlamaCppProcessManager.cs"
if (!(Test-Path $target)) { throw "File not found: $target" }

Backup-File $target

$content = Get-Content $target -Raw
$fixed = $content

# Fix common bad injection:
# return $"... --model "{modelPath}" ..."
# -> escape quotes inside interpolated string:
# return $"... --model \"{modelPath}\" ..."
$fixed = $fixed -replace '(\$"--host\s*\{host\}.*?--model)\s*"\s*\{modelPath\}\s*"(.*?";)', '$1 \"{modelPath}\"$2'

# More permissive: any return $"... --model "{modelPath}" ...
$fixed = $fixed -replace '(return\s+\$".*?--model)\s*"\s*(\{modelPath\})\s*"(.*?";)', '$1 \"$2\"$3'

if ($fixed -eq $content) {
  Write-Warning "No pattern matched. Attempting a direct token replace."
  $fixed = $fixed -replace '--model\s*"\{modelPath\}"', '--model \"{modelPath}\"'
}

Set-Content -Path $target -Value $fixed -Encoding UTF8
Write-Host "OK - Fixed quote escaping in BuildArgsAutoTune (if present)."
