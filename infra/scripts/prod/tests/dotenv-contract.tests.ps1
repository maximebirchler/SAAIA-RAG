param([Parameter(Mandatory=$true)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
. (Join-Path $repo 'infra/scripts/prod/_common.ps1')
$out=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $out | Out-Null
$work=Join-Path $out 'private-synthetic-work'
New-Item -ItemType Directory -Force $work | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$value,[string]$reason){if(-not $value){throw $reason}}
function Case([string]$name,[scriptblock]$body){try{& $body;$results.Add(@{name=$name;passed=$true;error=$null})}catch{$results.Add(@{name=$name;passed=$false;error=$_.Exception.Message})}}
function ReadFixture([string]$text){
  $path=Join-Path $work ([guid]::NewGuid().ToString('N')+'.env')
  [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
  return Read-DotEnv -EnvPath $path
}
function Reject([string]$text){$rejected=$false;try{ReadFixture $text|Out-Null}catch{$rejected=$true};Check $rejected 'unsupported_env_form_was_silently_accepted'}
Case 'single_and_double_quoted_values_are_decoded' {
  $map=ReadFixture "ONE='synthetic=a#b'`nTWO=`"synthetic=a#b`""
  Check ($map.ONE -ceq 'synthetic=a#b' -and $map.TWO -ceq $map.ONE) 'quoted_value_differs_from_compose'
}
Case 'comments_whitespace_equals_and_literal_hash_are_distinguished' {
  $map=ReadFixture " # comment`n  ONE = alpha=beta # ignored`nTWO=alpha#beta`nTHREE=`"alpha # beta`" # ignored"
  Check ($map.ONE -ceq 'alpha=beta' -and $map.TWO -ceq 'alpha#beta' -and $map.THREE -ceq 'alpha # beta') 'comment_or_separator_changed_literal_value'
}
Case 'unquoted_and_single_quoted_windows_paths_keep_backslashes' {
  $map=ReadFixture "ONE=C:\Users\synthetic\models`nTWO='C:\Users\synthetic\models'"
  Check ($map.ONE -ceq 'C:\Users\synthetic\models' -and $map.TWO -ceq $map.ONE) 'windows_backslashes_changed'
}
Case 'double_quoted_backslashes_and_escaped_quotes_follow_compose_subset' {
  $map=ReadFixture 'ONE="C:\\Cache\\models"'
  Check ($map.ONE -ceq 'C:\Cache\models') 'double_quoted_backslashes_not_decoded'
  $map=ReadFixture 'ONE="{\"key\": \"value\"}"'
  Check ($map.ONE -ceq '{"key": "value"}') 'escaped_double_quotes_not_decoded'
  $map=ReadFixture "ONE='Let\'s go'"
  Check ($map.ONE -ceq "Let's go") 'escaped_single_quote_not_decoded'
}
Case 'colon_export_empty_and_duplicate_last_value_are_explicit' {
  $map=ReadFixture "export EXPORTED=first`nCOLON: value`nONE=first`nONE=last`nEMPTY="
  Check ($map.EXPORTED -ceq 'first' -and $map.COLON -ceq 'value' -and $map.ONE -ceq 'last' -and $map.ContainsKey('EMPTY') -and $map.EMPTY -ceq '') 'supported_assignment_syntax_changed'
}
Case 'plain_unquoted_value_keeps_all_equals' {
  $map=ReadFixture 'ONE=synthetic=a=b'
  Check ($map.ONE -ceq 'synthetic=a=b') 'plain_assignment_regressed'
}
Case 'unquoted_variable_interpolation_is_rejected' { Reject 'ONE=$OTHER' }
Case 'double_quoted_variable_interpolation_is_rejected' { Reject 'ONE="${OTHER:-fallback}"' }
Case 'single_quoted_variable_expression_is_literal' {
  $map=ReadFixture 'ONE=''${OTHER}'''
  Check ($map.ONE -ceq '${OTHER}') 'literal_single_quoted_expression_changed'
}
Case 'inherited_bare_key_is_rejected' { Reject 'INHERITED' }
Case 'unterminated_quote_is_rejected' { Reject 'ONE="not closed' }
Case 'quoted_trailing_junk_is_rejected' { Reject 'ONE="closed" extra' }
Case 'multiline_value_is_explicitly_unsupported' { Reject "ONE='line1`nline2'" }
Case 'invalid_assignment_does_not_disappear_silently' { Reject 'NOT A KEY=value' }
Case 'decoded_credential_is_the_one_rendered_for_signing' {
  $key=Join-Path $work 'synthetic-signing-input.txt'
  [IO.File]::WriteAllText($key,'SYNTHETIC_NOT_A_PRIVATE_KEY')
  $map=ReadFixture "POSTGRES_PASSWORD=`"synthetic=A#B`" # ignored`nSAAIA_AUTH_PEPPER='synthetic-pepper'`nSAAIA_BOOTSTRAP_ENABLED=`"false`"`nSAAIA_CONFIG_PRIVATE_KEY_PATH='$key'"
  function dotnet { [IO.File]::WriteAllText([string]$args[-1],'SYNTHETIC_NOT_A_SIGNATURE');$global:LASTEXITCODE=0 }
  $rendered=New-SignedConfig -RepoRoot $repo -env $map -TemplateRel 'infra/config/deployment.config.prod.template.json' -DeployDirRel (Join-Path $work 'rendered')
  $config=Get-Content -LiteralPath $rendered.ConfigPath -Raw|ConvertFrom-Json
  Check ($config.Database.ConnectionString.Contains('Password=synthetic=A#B;')) 'rendered_database_password_differs_from_compose_value'
  Check ($config.Auth.Pepper -ceq 'synthetic-pepper' -and -not $config.Bootstrap.Enabled) 'quoted_auth_or_boolean_not_decoded'
}
Case 'decoded_control_escapes_survive_json_rendering' {
  $map=ReadFixture 'ONE="before\rafter\tend"'
  $json='"'+(Escape-JsonString $map.ONE)+'"'
  $roundtrip=ConvertFrom-Json -InputObject $json
  Check ($roundtrip -ceq "before`rafter`tend") 'json_escaping_dropped_decoded_characters'
}
Case 'utf8_text_and_final_literal_dollar_are_preserved' {
  $map=ReadFixture 'ONE=école Zürich café$'
  Check ($map.ONE -ceq 'école Zürich café$') 'utf8_or_final_literal_dollar_changed'
}
Case 'errors_do_not_repeat_the_rejected_input' {
  $sentinel='SYNTHETIC_PRIVATE_VALUE_NOT_A_SECRET'
  $message=''
  try { ReadFixture ('ONE="${OTHER:-'+$sentinel+'}"')|Out-Null } catch { $message=$_.Exception.Message }
  Check ($message -ne '' -and -not $message.Contains($sentinel) -and -not $message.Contains('${OTHER')) 'parse_error_repeated_input_contents'
}
$failed=@($results|Where-Object{-not $_.passed}).Count
[ordered]@{schema='saaia-dotenv-subset-tests.v1';syntheticInputsOnly=$true;actualSecretsRead=$false;dotnetBuilds=0;networkCalls=0;cryptographicSignatureValidated=$false;passed=($failed-eq 0);total=$results.Count;failed=$failed;tests=$results}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding utf8
$results|Format-Table -AutoSize
if($failed-gt 0){exit 1}
