param([Parameter(Mandatory=$true)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path $out 'private-synthetic-work'
New-Item -ItemType Directory -Force $work | Out-Null
. (Join-Path $repo 'infra/scripts/prod/_common.ps1')
$script:results = [Collections.Generic.List[object]]::new()
$global:SaaiaDeploymentTestState = @{ ready=$false; dockerRevision=''; signerCalls=0; readyCalls=0; sandbox='' }
# Transport boundaries only are mocked: render/sign invocation, actual installer and
# production helpers still run. No real credential, process, service or HTTP call.
function global:dotnet {
  $global:SaaiaDeploymentTestState.signerCalls++
  [IO.File]::WriteAllText([string]$args[-1], 'SYNTHETIC_SIGNATURE_NOT_CRYPTOGRAPHIC')
  $global:LASTEXITCODE = 0
}
function global:docker { $global:SaaiaDeploymentTestState.dockerRevision = $env:SAAIA_CODE_REVISION; $global:LASTEXITCODE = 0 }
function global:Invoke-WebRequest { $global:SaaiaDeploymentTestState.readyCalls++; if (-not $global:SaaiaDeploymentTestState.ready) { throw 'synthetic_unavailable' }; return @{ StatusCode = 200 } }
function global:Start-Sleep { }
function Check([bool]$condition, [string]$reason) { if (-not $condition) { throw $reason } }
function Case([string]$name, [scriptblock]$body) {
  try { & $body; $script:results.Add([pscustomobject]@{name=$name;passed=$true;error=$null}) }
  catch { $script:results.Add([pscustomobject]@{name=$name;passed=$false;error=$_.Exception.Message}) }
}
function Config([hashtable]$overrides = @{}) {
  $key = Join-Path $work 'synthetic-signing-input.txt'
  [IO.File]::WriteAllText($key, 'SYNTHETIC_NOT_A_PRIVATE_KEY')
  $values = @{ POSTGRES_PASSWORD='synthetic-db'; SAAIA_AUTH_PEPPER='synthetic-pepper'; SAAIA_BOOTSTRAP_ENABLED='false'; SAAIA_CONFIG_PRIVATE_KEY_PATH=$key }
  foreach ($k in $overrides.Keys) { $values[$k] = $overrides[$k] }
  $rendered = New-SignedConfig -RepoRoot $repo -env $values -TemplateRel 'infra/config/deployment.config.prod.template.json' -DeployDirRel (Join-Path $work ([guid]::NewGuid().ToString('N')))
  return Get-Content -LiteralPath $rendered.ConfigPath -Raw | ConvertFrom-Json
}
function Installer([bool]$isReady) {
  $global:SaaiaDeploymentTestState.ready = $isReady
  $global:SaaiaDeploymentTestState.readyCalls = 0
  $global:SaaiaDeploymentTestState.dockerRevision = ''
  $sandbox = Join-Path $work ([guid]::NewGuid().ToString('N'))
  $global:SaaiaDeploymentTestState.sandbox = $sandbox
  foreach ($dir in @('infra/scripts/prod','infra/config','backend/SAAIA.Backend','contracts/SAAIA.Contracts','infra/docling-sidecar/saaia_docling')) {
    New-Item -ItemType Directory -Force (Join-Path $sandbox $dir) | Out-Null
  }
  Get-ChildItem -LiteralPath (Join-Path $repo 'infra/scripts/prod') -Filter '*.ps1' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sandbox 'infra/scripts/prod') }
  foreach ($relative in @('infra/config/deployment.config.prod.template.json','infra/docker-compose.prod.yml','backend/SAAIA.Backend/Dockerfile','backend/SAAIA.Backend/SAAIA.Backend.csproj','contracts/SAAIA.Contracts/SAAIA.Contracts.csproj','RAG.sln','global.json','Directory.Build.props','.dockerignore','infra/docling-sidecar/Dockerfile','infra/docling-sidecar/entrypoint.sh','infra/docling-sidecar/requirements-parser.txt')) {
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination (Join-Path $sandbox $relative)
  }
  [IO.File]::WriteAllText((Join-Path $sandbox 'backend/SAAIA.Backend/Program.cs'), 'synthetic source input')
  $key = Join-Path $sandbox 'synthetic-signing-input.txt'
  [IO.File]::WriteAllText($key, 'SYNTHETIC_NOT_A_PRIVATE_KEY')
  $lines = @('POSTGRES_PASSWORD=synthetic-db','SAAIA_AUTH_PEPPER=synthetic-pepper','SAAIA_BOOTSTRAP_ENABLED=false',"SAAIA_CONFIG_PRIVATE_KEY_PATH=$key", "SAAIA_INSTALL_ROOT=$sandbox")
  [IO.File]::WriteAllLines((Join-Path $sandbox 'infra/.env'), $lines)
  $oldInstall = $env:SAAIA_INSTALL_ROOT
  $oldRevision = $env:SAAIA_CODE_REVISION
  $oldDoclingRevision = $env:SAAIA_DOCLING_SOURCE_REVISION
  $oldTeiRevision = $env:TEI_RUNTIME_REVISION
  $oldQdrantRevision = $env:QDRANT_RUNTIME_REVISION
  try {
    $env:SAAIA_INSTALL_ROOT = $sandbox
    $env:SAAIA_CODE_REVISION = ''
    & (Join-Path $sandbox 'infra/scripts/prod/install.ps1')
  } finally { $env:SAAIA_INSTALL_ROOT=$oldInstall; $env:SAAIA_CODE_REVISION=$oldRevision; $env:SAAIA_DOCLING_SOURCE_REVISION=$oldDoclingRevision; $env:TEI_RUNTIME_REVISION=$oldTeiRevision; $env:QDRANT_RUNTIME_REVISION=$oldQdrantRevision }
}
function ArchiveInputFixture {
  Installer $true
  $sandbox=$global:SaaiaDeploymentTestState.sandbox
  foreach ($relative in @('backend/SAAIA.Backend/.env','backend/SAAIA.Backend/appsettings.Local.json','backend/SAAIA.Backend/deployment.config.json','backend/SAAIA.Backend/keys/synthetic.pem','backend/SAAIA.Backend/obj/generated.cs')) {
    $path=Join-Path $sandbox $relative
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllText($path, 'SYNTHETIC_EXCLUDED_INPUT_NOT_A_SECRET')
  }
  return $sandbox
}
Case 'signed_default_chunking_matches_compose_200_0_25' {
  $c = Config
  Check ($c.Ingestion.ChunkMaxWords -eq 200 -and $c.Ingestion.ChunkOverlapWords -eq 0 -and $c.Ingestion.ChunkMinWords -eq 25) 'signed_chunking_differs_from_compose'
}
Case 'signed_runtime_revisions_follow_effective_images' {
  $tei='sha256:'+('a'*64); $qd='sha256:'+('b'*64)
  $c=Config @{ TEI_IMAGE="registry.invalid/tei@$tei"; QDRANT_IMAGE="registry.invalid/qdrant@$qd"; TEI_RUNTIME_REVISION=$tei; QDRANT_RUNTIME_REVISION=$qd }
  Check ($c.Rag.EmbeddingsRuntimeRevision -ceq $tei -and $c.Rag.QdrantRuntimeRevision -ceq $qd) 'signed_runtime_does_not_match_effective_image'
}
Case 'image_digest_runtime_mismatch_rejected_before_signing' {
  $before=$global:SaaiaDeploymentTestState.signerCalls; $rejected=$false
  try { Config @{ TEI_IMAGE=('registry.invalid/tei@sha256:'+('a'*64)); TEI_RUNTIME_REVISION=('sha256:'+('c'*64)) } | Out-Null } catch { $rejected=$true }
  Check ($rejected -and $global:SaaiaDeploymentTestState.signerCalls -eq $before) 'mismatched_runtime_was_signed'
}
Case 'mutable_image_without_attested_revision_is_rejected' {
  $rejected=$false
  try { Config @{ QDRANT_IMAGE='registry.invalid/qdrant:mutable' } | Out-Null } catch { $rejected=$true }
  Check $rejected 'mutable_image_inherited_unrelated_runtime_identity'
}
Case 'chunk_min_above_max_rejected' {
  $rejected=$false
  try { Config @{ SAAIA_INGESTION_CHUNK_MAX_WORDS='200'; SAAIA_INGESTION_CHUNK_MIN_WORDS='201' } | Out-Null } catch { $rejected=$true }
  Check $rejected 'incoherent_chunk_min_was_signed'
}
Case 'chunk_overlap_at_max_rejected' {
  $rejected=$false
  try { Config @{ SAAIA_INGESTION_CHUNK_MAX_WORDS='200'; SAAIA_INGESTION_CHUNK_OVERLAP_WORDS='200' } | Out-Null } catch { $rejected=$true }
  Check $rejected 'incoherent_chunk_overlap_was_signed'
}
Case 'valid_overrides_and_disabled_paid_capability_preserved' {
  $c=Config @{ SAAIA_INGESTION_CHUNK_MAX_WORDS='180'; SAAIA_INGESTION_CHUNK_MIN_WORDS='20'; SAAIA_INGESTION_CHUNK_OVERLAP_WORDS='12' }
  Check ($c.Ingestion.ChunkMaxWords -eq 180 -and $c.Ingestion.ChunkMinWords -eq 20 -and $c.Ingestion.ChunkOverlapWords -eq 12) 'valid_chunk_overrides_lost'
  Check (-not $c.License.AdvancedAnalysisEnabled -and $c.AdvancedAnalysis.Provider -eq 'disabled') 'paid_capability_unexpectedly_enabled'
}
Case 'installer_unready_is_failure' {
  $rejected=$false
  try { Installer $false } catch { $rejected=$true }
  Check ($rejected -and $global:SaaiaDeploymentTestState.readyCalls -eq 40) 'installer_succeeded_without_ready_or_failed_before_probe'
}
Case 'installer_ready_supplies_content_revision_to_build' {
  Installer $true
  Check ($global:SaaiaDeploymentTestState.dockerRevision -match '^source-sha256:[0-9a-f]{64}$') 'installer_did_not_supply_content_revision'
}
Case 'docker_build_uses_checkout_sdk_and_common_properties' {
  $docker=Get-Content -LiteralPath (Join-Path $repo 'backend/SAAIA.Backend/Dockerfile') -Raw
  $ignore=Get-Content -LiteralPath (Join-Path $repo '.dockerignore') -Raw
  $restore=$docker.IndexOf('RUN dotnet restore')
  Check ($docker.IndexOf('COPY global.json Directory.Build.props ./') -ge 0 -and $docker.IndexOf('COPY global.json Directory.Build.props ./') -lt $restore) 'docker_restore_missing_checkout_build_contract'
  Check ($ignore -match '(?m)^!Directory\.Build\.props\s*$') 'root_build_properties_excluded_from_context'
  $sdk=(Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
  Check ($docker -match ('sdk:'+ [regex]::Escape($sdk))) 'docker_sdk_does_not_follow_repository_sdk'
}
Case 'docker_rejects_unknown_build_identity' {
  $docker=Get-Content -LiteralPath (Join-Path $repo 'backend/SAAIA.Backend/Dockerfile') -Raw
  $compose=Get-Content -LiteralPath (Join-Path $repo 'infra/docker-compose.prod.yml') -Raw
  Check ($compose -notmatch '\$\{SAAIA_CODE_REVISION:-unknown\}') 'compose_invents_unknown_code_revision'
  Check ($docker -notmatch 'ARG SAAIA_CODE_REVISION=unknown' -and $docker -match 'test.+SAAIA_CODE_REVISION') 'docker_accepts_unknown_code_revision'
}
Case 'compose_inspection_does_not_require_a_build_identity' {
  $compose=Get-Content -LiteralPath (Join-Path $repo 'infra/docker-compose.prod.yml') -Raw
  Check ($compose -notmatch '\$\{SAAIA_CODE_REVISION:\?') 'inspection_would_fail_before_accessing_existing_containers'
}
Case 'source_revision_changes_with_source_and_shared_build_properties_not_generated_files' {
  Installer $true
  $sandbox=$global:SaaiaDeploymentTestState.sandbox
  $first=Get-DeploymentSourceRevision $sandbox backend
  [IO.File]::AppendAllText((Join-Path $sandbox 'backend/SAAIA.Backend/Program.cs'), ' modified')
  $second=Get-DeploymentSourceRevision $sandbox backend
  Check ($first -cne $second) 'source_change_not_identified'
  [IO.File]::AppendAllText((Join-Path $sandbox 'Directory.Build.props'), '<!-- synthetic change -->')
  $third=Get-DeploymentSourceRevision $sandbox backend
  Check ($second -cne $third) 'shared_properties_change_not_identified'
  New-Item -ItemType Directory -Force (Join-Path $sandbox 'backend/SAAIA.Backend/obj') | Out-Null
  [IO.File]::WriteAllText((Join-Path $sandbox 'backend/SAAIA.Backend/obj/cache.json'), 'synthetic generated output')
  Check ((Get-DeploymentSourceRevision $sandbox backend) -ceq $third) 'generated_output_changed_source_identity'
}
Case 'process_image_override_has_same_priority_as_compose' {
  $oldImage=$env:TEI_IMAGE; $oldRevision=$env:TEI_RUNTIME_REVISION
  try {
    $env:TEI_IMAGE='registry.invalid/process@sha256:'+('d'*64)
    $env:TEI_RUNTIME_REVISION='sha256:'+('d'*64)
    $c=Config @{ TEI_IMAGE=('registry.invalid/file@sha256:'+('a'*64)); TEI_RUNTIME_REVISION=('sha256:'+('a'*64)) }
    Check ($c.Rag.EmbeddingsRuntimeRevision -ceq ('sha256:'+('d'*64))) 'process_priority_differed_from_compose'
  } finally { $env:TEI_IMAGE=$oldImage; $env:TEI_RUNTIME_REVISION=$oldRevision }
}
Case 'remote_source_archive_contains_the_checkout_build_properties' {
  $sandbox=ArchiveInputFixture
  $global:SaaiaDeploymentTarInputs=@()
  function global:tar {
    $listIndex=[Array]::IndexOf([object[]]$args, '-T')
    if ($listIndex -lt 0 -or $args -notcontains '--null') { throw 'Archive must use a NUL-separated explicit file list.' }
    $global:SaaiaDeploymentTarInputs=@([IO.File]::ReadAllText([string]$args[$listIndex+1], [Text.Encoding]::UTF8).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    $global:LASTEXITCODE=0
  }
  New-BackendSourceArchive -RepoRoot $sandbox -ArchivePath (Join-Path $work 'synthetic-not-created.tar')
  Check ($global:SaaiaDeploymentTarInputs -contains 'Directory.Build.props') 'remote_archive_omits_required_build_properties'
  Check ($global:SaaiaDeploymentTarInputs -contains 'infra/docling-sidecar/requirements-parser.txt') 'remote_archive_omits_required_parser_dependency'
  Check (@($global:SaaiaDeploymentTarInputs | Where-Object { Test-Path -LiteralPath (Join-Path $sandbox $_) -PathType Container }).Count -eq 0) 'recursive_directory_found_in_archive_input'
}
Case 'remote_archive_has_only_explicit_source_files_no_recursive_directory_transfer' {
  $tokens=$null; $parseErrors=$null
  $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'infra/scripts/prod/deploy-remote-backend.ps1'), [ref]$tokens, [ref]$parseErrors)
  $directTar=@($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'tar' }, $true))
  Check ($directTar.Count -eq 0) 'remote_transfer_still_recursively_packages_unfiltered_backend_directories'
}
Case 'archive_selection_excludes_synthetic_private_and_generated_files_without_hashing_them' {
  $sandbox=ArchiveInputFixture
  $inputs=@(Get-BackendArchiveSourceFiles -RepoRoot $sandbox)
  foreach ($relative in @('backend/SAAIA.Backend/.env','backend/SAAIA.Backend/appsettings.Local.json','backend/SAAIA.Backend/deployment.config.json','backend/SAAIA.Backend/keys/synthetic.pem','backend/SAAIA.Backend/obj/generated.cs')) {
    Check (-not ($inputs -contains $relative)) 'excluded_input_selected_for_transfer'
  }
  foreach ($relative in @('backend/SAAIA.Backend/Program.cs','Directory.Build.props','infra/docker-compose.prod.yml','infra/docling-sidecar/Dockerfile','infra/docling-sidecar/requirements-parser.txt')) {
    Check ($inputs -contains $relative) 'required_build_input_missing_from_transfer'
  }
}

Case 'parser_dependency_mutation_changes_docling_source_identity' {
  Installer $true
  $sandbox=$global:SaaiaDeploymentTestState.sandbox
  $first=Get-DeploymentSourceRevision $sandbox docling
  $backend=Get-DeploymentSourceRevision $sandbox backend
  [IO.File]::AppendAllText((Join-Path $sandbox 'infra/docling-sidecar/requirements-parser.txt'), "`npydantic-core==2.46.5`n")
  Check ((Get-DeploymentSourceRevision $sandbox docling) -cne $first) 'parser_dependency_change_not_identified'
  Check ((Get-DeploymentSourceRevision $sandbox backend) -ceq $backend) 'parser_dependency_changed_backend_identity'
}
Case 'missing_parser_dependency_rejects_identity_and_archive_selection' {
  Installer $true
  $sandbox=$global:SaaiaDeploymentTestState.sandbox
  Remove-Item -LiteralPath (Join-Path $sandbox 'infra/docling-sidecar/requirements-parser.txt')
  $identityRejected=$false; $archiveRejected=$false
  try { Get-DeploymentSourceRevision $sandbox docling | Out-Null } catch { $identityRejected=$true }
  try { Get-BackendArchiveSourceFiles -RepoRoot $sandbox | Out-Null } catch { $archiveRejected=$true }
  Check ($identityRejected -and $archiveRejected) 'missing_parser_dependency_not_rejected'
}

$failures=@($script:results | Where-Object { -not $_.passed })
$proof=[ordered]@{schema='saaia-deployment-offline-tests.v1';transportMocked=$true;cryptographicSignatureValidated=$false;servicesStarted=0;networkCalls=0;dotnetBuilds=0;passed=($failures.Count-eq 0);total=$script:results.Count;failed=$failures.Count;tests=$script:results}
$proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding utf8
$script:results | Format-Table name,passed,error -AutoSize
if ($failures.Count -gt 0) { exit 1 }
