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

$bad = 'LastCommandLine = $""{s.LlamaExePath}" {args}";'
$good = 'LastCommandLine = $"\"{s.LlamaExePath}\" {args}";'

if ($content -notmatch [regex]::Escape($bad)) {
  Write-Warning "Exact pattern not found. Trying a tolerant regex fix..."
  # Replace: LastCommandLine = $""{...}" {args}";
  $content2 = [regex]::Replace(
    $content,
    'LastCommandLine\s*=\s*\$""\{(?<expr>[^}]+)\}""\s*\{(?<rest>[^}]+)\}";',
    'LastCommandLine = $"\"{$1}\" {$2}";'
  )
  if ($content2 -eq $content) {
    throw "Could not patch LastCommandLine. Please open the file and search for LastCommandLine to confirm current content."
  }
  $content = $content2
} else {
  $content = $content.Replace($bad, $good)
}

Set-Content -Path $target -Value $content -Encoding UTF8
Write-Host "OK - Fixed LastCommandLine quoting."
