#requires -Version 7.4
[CmdletBinding()]
param (
    [ValidateSet('Unit', 'IntegrationPg', 'LiveLocal', 'LivePaid')][string]$Profile = 'Unit',
    [string]$Filter = '',
    [string]$OutputPath = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('Backend', 'Client', 'Contracts', 'ClientCore')][string[]]$Projects = @('Backend', 'Contracts', 'ClientCore', 'Client'),
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$PlanOnly,
    [string]$CampaignProfilePath = '',
    [switch]$ExecutePaidCampaign,
    [switch]$AuthorizePaid,
    [switch]$AuthorizeCorpusTransmission,
    [decimal]$PaidCapUsd = 0,
    [string]$PaidSecretStorePath = '',
    [string]$PaidPythonExecutable = '',
    [string]$PaidProjectionAssemblyPath = '',
    [ValidateSet('Free', 'Tier1', 'Tier2', 'Tier3', 'Tier4', 'Tier5')][string]$ObservedOrganizationTier = 'Free',
    [string]$TierObservedAtUtc = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
if (-not $OutputPath) {
    $OutputPath = Join-Path $repo "artifacts/test-project/$runId/manifest.json"
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    throw 'OutputPath already exists; use a new manifest path to preserve campaign evidence.'
}
$outDir = Split-Path -Parent $OutputPath
[IO.Directory]::CreateDirectory($outDir) | Out-Null
$projectPaths = @{
    Backend = 'backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj'
    Client = 'client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj'
    Contracts = 'contracts/SAAIA.Contracts.Tests/SAAIA.Contracts.Tests.csproj'
    ClientCore = 'client/SAAIA.Client.Core.Tests/SAAIA.Client.Core.Tests.csproj'
}

$previous = @{}

$cleared = [Collections.Generic.List[string]]::new()
$results = [Collections.Generic.List[object]]::new()
$exitCode = 0
function Get-WorkingTreeFingerprint([string]$Phase) {
    $files = @(& git -C $repo ls-files --cached --others --exclude-standard | Sort-Object -Unique | Where-Object {
        $_ -notmatch '(^|/)(artifacts|bin|obj|node_modules|packages)/' -and
        ($_ -match '\.(cs|xaml|csproj|fsproj|sln|props|targets|ps1|psm1|psd1|py|js|ts|sql)$' -or
            $_ -match '^config/.*\.json$' -or $_ -match '^\.github/workflows/.*\.ya?ml$' -or $_ -eq '.editorconfig')
    })
    $inventory = @($files | ForEach-Object {
        $sourcePath = Join-Path $repo $_
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash }
        }
    })
    $serialized = $inventory | ConvertTo-Json -Compress -Depth 3
    $digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($serialized))
    $inventoryName = "source-files-$runId-$Phase.json"
    $inventory | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outDir $inventoryName) -Encoding utf8
    return [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        sha256 = [Convert]::ToHexString($digest)
        fileCount = $inventory.Count
        inventory = $inventoryName
    }
}

function Get-TestedAssemblyFingerprints([xml]$Trx) {
    $testAssemblies = @($Trx.SelectNodes("//*[local-name()='TestMethod']") | ForEach-Object { $_.codeBase } | Sort-Object -Unique)
    $paths = @($testAssemblies | ForEach-Object {
        $_
        if (Test-Path -LiteralPath $_ -PathType Leaf) {
            Get-ChildItem -LiteralPath (Split-Path -Parent $_) -Filter 'SAAIA.*.dll' -File | ForEach-Object FullName
        }
    } | Sort-Object -Unique)
    return @($paths | ForEach-Object {
        if (Test-Path -LiteralPath $_ -PathType Leaf) {
            $file = Get-Item -LiteralPath $_
            [ordered]@{
                path = [IO.Path]::GetRelativePath($repo, $file.FullName)
                role = $(if ($_ -in $testAssemblies) { 'test_assembly' } else { 'local_product_dependency' })
                sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
                sizeBytes = $file.Length
                lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o')
                capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            }
        } else {
            [ordered]@{ path = $_; role = 'test_assembly'; state = 'unavailable_after_run' }
        }
    })
}

$manifest = [ordered]@{
    schemaVersion = 'saaia-test-campaign-v1'
    runId = $runId
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    finishedAtUtc = $null
    gitCommit = (& git -C $repo rev-parse HEAD)
    gitDirty = [bool](& git -C $repo status --porcelain)
    profile = $Profile
    filter = $Filter
    requestedProjects = $Projects
    configuration = $Configuration
    buildRequested = (-not $NoBuild)
    restoreRequested = (-not $NoRestore)
    sourceBefore = Get-WorkingTreeFingerprint 'before'
    sourceAfter = $null
    sourceChangedDuringRun = $null
    provenanceLimit = 'Binary hashes are captured after execution from TRX test paths and their local SAAIA dependencies. Source fingerprints do not prove that a previous no-build compilation used those sources.'
    state = 'preparing'
    prerequisites = @()
    environment = @{
        neutralizedVariableNames = $cleared
        clientDataIsolation = @{
            requiredForClientTests = $true
            enabled = $false
            source = 'not_selected'
            rootId = $null
            ownedRelativePath = $null
            environmentRestored = $null
        }
    }

    results = $results
    model = @{
        state = 'not_applicable'
        reason = 'Unit and PostgreSQL profiles do not invoke a model.'
    }

    corpus = @{
        state = 'synthetic_fixtures'
        reason = 'IntegrationPg fixtures create isolated databases; no production corpus is assumed.'
    }

    validationProfilesSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'config/validation-profiles.json') -Algorithm SHA256).Hash
    selectionPolicy = 'TRX covers selected tests; excluded profiles were not executed.'
    excludedProfiles = @(@('Unit', 'IntegrationPg', 'LiveLocal', 'LivePaid') | Where-Object {
            $_ -ne $Profile
        })
}

function Set-ScopedEnvironment([string]$Name, [AllowNull()][string]$Value) {
    if (-not $previous.ContainsKey($Name)) {
        $previous[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }

    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

try {
    Set-ScopedEnvironment 'SAAIA_TEST_PROFILE' $Profile
    if ($Profile -in @('Unit', 'IntegrationPg')) {
        foreach ($item in @(Get-ChildItem Env: | Where-Object Name -Match '^SAAIA_(RUN_|LIVE_|VALIDATION_)')) {
            $cleared.Add($item.Name)
            Set-ScopedEnvironment $item.Name $null
        }

        foreach ($name in @('SAAIA_TEST_PAID_AUTHORIZED', 'SAAIA_TEST_TRANSMISSION_AUTHORIZED', 'SAAIA_TEST_PAID_CAP_USD')) {
            Set-ScopedEnvironment $name $null
        }
    }

    if ($Profile -eq 'Unit') {
        Set-ScopedEnvironment 'SAAIA_TEST_PG_CONN' $null
        $selection = 'Profile!=IntegrationPg&Profile!=LiveLocal&Profile!=LivePaid'
    } else {
        $selection = "Profile=$Profile"
    }

    if ($Filter) {
        $selection = "($selection)&($Filter)"
    }

    $manifest.filter = $selection
    if ($Profile -eq 'IntegrationPg') {
        $connection = [Environment]::GetEnvironmentVariable('SAAIA_TEST_PG_CONN', 'Process')
        if (-not $connection) {
            throw 'IntegrationPg requires SAAIA_TEST_PG_CONN; no integration test was executed.'
        }

        $settings = [Data.Common.DbConnectionStringBuilder]::new()
        # PowerShell's dictionary adapter treats .ConnectionString assignment as
        # an entry named ConnectionString, so call the CLR setter explicitly.
        try { $settings.set_ConnectionString($connection) }
        catch { throw 'IntegrationPg requires a valid PostgreSQL fixture connection string.' }
        # The isolated fixture uses Host only. Reject alternative aliases rather
        # than guessing Npgsql's precedence across different spellings.
        $hostKeys = @($settings.get_Keys() | Where-Object { $_ -eq 'Host' })
        $hostAliases = @($settings.get_Keys() | Where-Object {
            ($_ -replace '\s', '') -in @('Server', 'DataSource', 'Address', 'Addr', 'NetworkAddress')
        })
        if ($hostAliases.Count -gt 0 -or $hostKeys.Count -eq 0 -or @($hostKeys | Where-Object {
            [string]$settings[$_] -notin @('localhost', '127.0.0.1', '::1')
        }).Count -gt 0) {
            throw 'IntegrationPg requires a loopback PostgreSQL fixture host.'
        }

        $manifest.prerequisites = @(@{
                name = 'isolated_postgresql'
                state = 'configured'
                connectionStringRecorded = $false
            })
        $Projects = @($Projects | Where-Object {
                $_ -eq 'Backend'
            })
        if ($Projects.Count -eq 0) {
            throw 'IntegrationPg requires the Backend project.'
        }
    }

    if ($Profile -eq 'LiveLocal') {
        if (-not $Filter) {
            throw 'LiveLocal requires an explicit test filter; a whole live bank is never selected implicitly.'
        }

        foreach ($item in @(Get-ChildItem Env: | Where-Object Name -Match 'SAAIA_.*(?:LLM_BASE_URL|PROVIDER_HOST|BACKEND_URL)$')) {
            $uri = $null
            if ([Uri]::TryCreate($item.Value, [UriKind]::Absolute, [ref]$uri) -and -not $uri.IsLoopback) {
                throw "LiveLocal rejects a non-loopback endpoint in $($item.Name)."
            }
        }

        $manifest.model = @{
            state = 'local_configuration_required'
            requestedModel = $env:SAAIA_LOCAL_PROVIDER_MODEL
            actualModel = 'Recorded by the selected live test artifact, not inferred from the filename.'
        }

        $manifest.corpus = @{
            state = 'selected_test_artifact'
            reason = 'The live test must record its effective corpus/model; this launcher does not infer their identity.'
        }
    }

    if ($Profile -eq 'LivePaid') {
        if (-not $AuthorizePaid -or -not $AuthorizeCorpusTransmission -or $PaidCapUsd -le 0 -or -not $CampaignProfilePath) {
            throw 'LivePaid requires explicit paid/transmission authorization, positive PaidCapUsd, and a guarded CampaignProfilePath.'
        }

        $campaign = Get-Content -LiteralPath $CampaignProfilePath -Raw | ConvertFrom-Json
        $isLocalLuna = $campaign.schemaVersion -eq 'saaia-local-openai-paid-campaign.v1'
        if (-not $isLocalLuna -and $campaign.schemaVersion -ne 'saaia-openai-terra-final-campaign-v1') {
            throw 'Unsupported guarded paid campaign profile. No model call was made.'
        }
        if ($isLocalLuna) {
            . (Join-Path $PSScriptRoot 'validation-paid/PaidCampaign.Common.ps1')
            $validatedCampaign = Get-ValidatedLunaCampaignProfile -ProfilePath $CampaignProfilePath -RepositoryRoot $repo
        }

        if ([decimal]$campaign.budget.hardStopUsd -gt $PaidCapUsd) {
            throw 'Campaign hard-stop exceeds the explicitly authorized cap.'
        }

        $manifest.prerequisites = @(@{
                name = 'paid_campaign'
                state = 'guarded_runner_required'
                profileSha256 = (Get-FileHash -LiteralPath $CampaignProfilePath -Algorithm SHA256).Hash
                authorizedCapUsd = $PaidCapUsd
            })
        $manifest.model = @{
            state = 'campaign_profile'
            provider = $campaign.provider.mode
            model = $campaign.provider.model
        }

        $manifest.corpus = if ($isLocalLuna) { @{
            state = 'local_pdf_campaign_profile'; path = $campaign.corpus.path; sha256 = $campaign.corpus.sha256
            retrievalAdapterLimit = 'Local TEI/Qdrant protocol substitutes; canonical SQL tools and PDF text are real.'
        } } else { @{
            state = 'campaign_profile'; bank = $campaign.bank.path; bankSha256 = $campaign.bank.sha256
        } }

        Set-ScopedEnvironment 'SAAIA_TEST_PAID_AUTHORIZED' '1'
        Set-ScopedEnvironment 'SAAIA_TEST_TRANSMISSION_AUTHORIZED' '1'
        Set-ScopedEnvironment 'SAAIA_TEST_PAID_CAP_USD' $PaidCapUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
        if ($PlanOnly) {
            $manifest.state = 'prepared'
        } elseif (-not $ExecutePaidCampaign) {
            throw 'Execution requires ExecutePaidCampaign; preparing a profile is not execution authorization.'
        } else {
            $campaignDirectory = Join-Path $outDir "paid-campaign-$runId"
            if ($isLocalLuna) {
                & (Join-Path $PSScriptRoot 'validation-paid/invoke-local-luna-campaign.ps1') `
                    -ProfilePath $CampaignProfilePath -OutputDirectory $campaignDirectory -Execute `
                    -AuthorizePaid:$AuthorizePaid -AuthorizeCorpusTransmission:$AuthorizeCorpusTransmission `
                    -PaidCapUsd $PaidCapUsd -SecretStorePath $PaidSecretStorePath -PythonExecutable $PaidPythonExecutable `
                    -ProjectionAssemblyPath $PaidProjectionAssemblyPath
                $executionProof = Get-Content -LiteralPath (Join-Path $campaignDirectory 'campaign-result.json') -Raw | ConvertFrom-Json
                if (-not $executionProof.executed -or -not $executionProof.completed -or
                    -not $executionProof.cleanup.zeroActiveJobs -or -not $executionProof.cleanup.ownedEnvironmentStopped) {
                    throw 'The local paid campaign lacks durable execution and cleanup proof.'
                }
                $exitCode = 0
            } else {
                & (Join-Path $PSScriptRoot 'test-openai-terra-campaign-profile.ps1') -ProfilePath $CampaignProfilePath -Execute -ArtifactDirectory $campaignDirectory -ObservedOrganizationTier $ObservedOrganizationTier -TierObservedAtUtc $TierObservedAtUtc
                $exitCode = $LASTEXITCODE
                $shutdown = Get-Content -LiteralPath (Join-Path $campaignDirectory 'run/resource-shutdown.json') -Raw | ConvertFrom-Json
                if (-not $shutdown.completed) { throw 'The historical campaign did not record completed execution.' }
            }
            $results.Add(@{
                    project = 'guarded_paid_campaign'
                    exitCode = $exitCode
                    artifactDirectory = $campaignDirectory
                    status = $(if ($exitCode -eq 0) {
                            'passed'
                        } else {
                            'failed'
                        })
                })
            $manifest.state = if ($exitCode -eq 0) {
                'passed'
            } else {
                'failed'
            }
        }
    } else {
        if (@($Projects | Where-Object { $_ -in @('Client', 'ClientCore') }).Count -gt 0) {
            $explicitClientRoot = [Environment]::GetEnvironmentVariable('SAAIA_CLIENT_DATA_ROOT', 'Process')
            $hasExplicitClientRoot = -not [string]::IsNullOrWhiteSpace($explicitClientRoot)
            if ($hasExplicitClientRoot -and -not [IO.Path]::IsPathFullyQualified($explicitClientRoot)) {
                throw 'Client test isolation requires an absolute SAAIA_CLIENT_DATA_ROOT; no test was executed.'
            }
            $clientRoot = if ($hasExplicitClientRoot) {
                [IO.Path]::GetFullPath($explicitClientRoot)
            } else {
                [IO.Path]::GetFullPath((Join-Path $outDir 'client-data'))
            }
            if (-not $PlanOnly) { [IO.Directory]::CreateDirectory($clientRoot) | Out-Null }
            Set-ScopedEnvironment 'SAAIA_CLIENT_DATA_ROOT' $clientRoot
            $manifest.environment.clientDataIsolation.enabled = $true
            $manifest.environment.clientDataIsolation.source = if ($hasExplicitClientRoot) { 'explicit_environment' } else { 'owned_campaign_output' }
            $manifest.environment.clientDataIsolation.rootId = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($clientRoot.ToLowerInvariant())))
            $manifest.environment.clientDataIsolation.ownedRelativePath = if ($hasExplicitClientRoot) { $null } else { 'client-data' }
        }
        foreach ($project in $Projects) {
            $path = Join-Path $repo $projectPaths[$project]
            if (-not (Test-Path -LiteralPath $path)) {
                throw "Required test project is absent: $project"
            }

            if ($project -eq 'Client' -and -not $IsWindows) {
                throw 'The WinUI client suite requires Windows; select Backend,Contracts,ClientCore explicitly on other platforms.'
            }

            $trxName = "$project-$runId.trx"
            $arguments = @('test', $path, '-c', $Configuration, '--filter', $selection, '--results-directory', $outDir, '--logger', "trx;LogFileName=$trxName")
            if ($NoRestore) {
                $arguments+='--no-restore'
            }

            if ($NoBuild) {
                $arguments+='--no-build'
            }

            $step = [ordered]@{
                project = $project
                path = $projectPaths[$project]
                status = 'prepared'
                exitCode = $null
                trx = $trxName
                trxSha256 = $null
                assemblies = @()
                counts = $null
                cases = @()
            }

            $results.Add($step)
            if ($PlanOnly) {
                continue
            }

            & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $outDir "$project-$runId.log") | Out-Host
            $step.exitCode = $LASTEXITCODE
            $trxPath = Join-Path $outDir $trxName
            if (Test-Path -LiteralPath $trxPath) {
                $step.trxSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
                [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
                $step.assemblies = @(Get-TestedAssemblyFingerprints $trx)
                $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
                $step.cases = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
                        @{
                            name = $_.testName
                            outcome = $_.outcome
                            duration = $_.duration
                        }
                    })
                # VSTest's xUnit v2 adapter can report Counters.notExecuted=0
                # while UnitTestResult correctly says NotExecuted. Count outcomes.
                $step.counts = @{
                    total = $step.cases.Count
                    executed = @($step.cases | Where-Object outcome -ne 'NotExecuted').Count
                    passed = @($step.cases | Where-Object outcome -eq 'Passed').Count
                    failed = @($step.cases | Where-Object outcome -eq 'Failed').Count
                    notExecuted = @($step.cases | Where-Object outcome -eq 'NotExecuted').Count
                    adapterReportedExecuted = [int]$counters.executed
                }
            }

            $step.status = if ($step.exitCode -eq 0 -and $null -ne $step.counts -and $step.counts.executed -gt 0) {
                'passed'
            } else {
                'failed'
            }

            if ($step.status -eq 'failed') {
                $exitCode = 1
            }
        }

        $manifest.state = if ($PlanOnly) {
            'prepared'
        } elseif ($exitCode -eq 0) {
            'passed'
        } else {
            'failed'
        }
    }
} catch {
    $manifest.state = 'failed'
    $manifest.prerequisites+=@{
        name = 'campaign_preflight'
        state = 'failed'
        reason = $_.Exception.Message
    }

    $exitCode = 1
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
} finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    if ($previous.ContainsKey('SAAIA_CLIENT_DATA_ROOT')) {
        $manifest.environment.clientDataIsolation.environmentRestored = [string]::Equals(
            [Environment]::GetEnvironmentVariable('SAAIA_CLIENT_DATA_ROOT', 'Process'),
            $previous['SAAIA_CLIENT_DATA_ROOT'], [StringComparison]::Ordinal)
    }

    $manifest.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $manifest.sourceAfter = Get-WorkingTreeFingerprint 'after'
    $manifest.sourceChangedDuringRun = $manifest.sourceBefore.sha256 -ne $manifest.sourceAfter.sha256
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}

Write-Host "test-project: $($manifest.state); manifest: $OutputPath"
exit $exitCode
