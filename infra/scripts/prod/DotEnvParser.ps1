# Deliberately bounded Compose dotenv subset. No interpolation or inherited values;
# errors contain only line numbers, never the input line or credential contents.
function ConvertFrom-DeploymentEnvValue([string]$Raw, [int]$LineNumber) {
  $value = $Raw.TrimStart()
  $singleQuoted = $value.StartsWith("'")
  $doubleQuoted = $value.StartsWith('"')
  if ($singleQuoted -or $doubleQuoted) {
    $quote = $value[0]
    $builder = [Text.StringBuilder]::new()
    $closed = $false
    for ($i=1; $i -lt $value.Length; $i++) {
      $character = $value[$i]
      if ($character -eq $quote) {
        $tail = $value.Substring($i+1).Trim()
        if ($tail -and -not $tail.StartsWith('#')) { throw "Unsupported .env trailing text at line $LineNumber." }
        $closed = $true
        break
      }
      if ($character -eq [char]92 -and $i+1 -lt $value.Length) {
        $next = $value[$i+1]
        if ($next -eq $quote) { [void]$builder.Append($next); $i++; continue }
        if ($singleQuoted) {
          # Backslashes remain literal in single quotes; pair them so the next
          # closing quote is not incorrectly considered escaped by an even pair.
          [void]$builder.Append($character)
          if ($next -eq [char]92) { [void]$builder.Append($next); $i++ }
          continue
        }
        switch ([string]$next) {
          'n' { [void]$builder.Append("`n") }
          'r' { [void]$builder.Append("`r") }
          't' { [void]$builder.Append("`t") }
          'a' { [void]$builder.Append([char]7) }
          'b' { [void]$builder.Append([char]8) }
          'f' { [void]$builder.Append([char]12) }
          'v' { [void]$builder.Append([char]11) }
          '\' { [void]$builder.Append([char]92) }
          '0' { throw "Unsupported .env octal escape at line $LineNumber." }
          default { [void]$builder.Append($character).Append($next) }
        }
        $i++
        continue
      }
      [void]$builder.Append($character)
    }
    if (-not $closed) { throw "Unterminated or unsupported multiline .env value at line $LineNumber." }
    $value = $builder.ToString()
  } else {
    # Compose's unquoted inline comment delimiter is a space followed by '#'.
    $comment = $value.IndexOf(' #', [StringComparison]::Ordinal)
    if ($comment -ge 0) { $value=$value.Substring(0,$comment) }
    $value=$value.TrimEnd()
  }
  if (-not $singleQuoted) {
    # A final '$' is literal (common in generated passwords). Other forms may
    # trigger Compose interpolation/escape behavior, which this parser rejects.
    $dollar=$value.IndexOf('$')
    if ($dollar -ge 0 -and $dollar -ne $value.Length-1) {
      throw "Unsupported .env interpolation at line $LineNumber; use a literal single-quoted value."
    }
  }
  return $value
}

function Read-DotEnv {
  param([Parameter(Mandatory=$true)][Alias('Path')][string]$EnvPath)
  if ([string]::IsNullOrWhiteSpace($EnvPath) -or -not (Test-Path -LiteralPath $EnvPath -PathType Leaf)) {
    throw 'Missing .env file.'
  }
  $map=@{}
  $lineNumber=0
  # Strict UTF8 avoids the PowerShell 5.1 ANSI fallback silently changing text.
  foreach ($line in [IO.File]::ReadAllLines($EnvPath, [Text.UTF8Encoding]::new($false,$true))) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    if ($line.IndexOf([char]0) -ge 0) { throw "Invalid .env byte at line $lineNumber." }
    $match=[regex]::Match($line, '^\s*(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)[ \t]*(?:=|:)(?<value>.*)$')
    if (-not $match.Success) { throw "Unsupported .env assignment at line $lineNumber." }
    $map[$match.Groups['key'].Value]=ConvertFrom-DeploymentEnvValue $match.Groups['value'].Value $lineNumber
  }
  return $map
}
