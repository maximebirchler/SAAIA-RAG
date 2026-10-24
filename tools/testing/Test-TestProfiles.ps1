#requires -Version 7.4
param ([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('artifacts/test-profile-checks/'+[Guid]::NewGuid().ToString('N'))
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Use a new evidence directory.'
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$pwsh = (Get-Process -Id $PID).Path
$checks = [Collections.Generic.List[object]]::new()
function Assert-Profile([string]$Name, [bool]$Condition) {
    $checks.Add(@{
            name = $Name
            passed = $Condition
        })
    if (-not $Condition) {
        throw "Profile check failed: $Name"
    }
}

function Invoke-Child([string[]]$Arguments, [hashtable]$Environment = @{}
) {
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $repo
    foreach ($argument in @('-NoProfile')+$Arguments) {
        $start.ArgumentList.Add($argument)
    }

    foreach ($name in $Environment.Keys) {
        if ($null -eq $Environment[$name]) {
            $null = $start.Environment.Remove($name)
        } else {
            $start.Environment[$name] = $Environment[$name]
        }
    }

    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) {
        $process.Kill($true)
        throw 'Profile fixture timed out.'
    }

    return @{
        exitCode = $process.ExitCode
        stdout = $stdout.GetAwaiter().GetResult()
        stderr = $stderr.GetAwaiter().GetResult()
    }
}

function Run-Plan([string]$Name, [string[]]$Arguments, [hashtable]$Environment = @{}
) {
    $path = Join-Path $OutputDirectory "$Name.json"
    $child = Invoke-Child (@('-File', (Join-Path $repo 'tools/test-project.ps1'), '-OutputPath', $path)+$Arguments) $Environment
    $child.stdout | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Name.stdout.log")
    $child.stderr | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Name.stderr.log")
    return @{
        exitCode = $child.exitCode
        manifest = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    }
}

try {
    $unit = Run-Plan 'unit-inherited-live' @('-Profile', 'Unit', '-Projects', 'Contracts', '-PlanOnly') @{
        SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST = '1'
        SAAIA_LIVE_AGENT_BANK = '1'
        SAAIA_TEST_PAID_AUTHORIZED = '1'
        SAAIA_TEST_PG_CONN = 'invalid_fixture_sentinel'
    }

    Assert-Profile 'unit plan is prepared, never executed' ($unit.exitCode -eq 0 -and $unit.manifest.state -eq 'prepared')
    Assert-Profile 'inherited provider flags are neutralized' ($unit.manifest.environment.neutralizedVariableNames -contains 'SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST' -and $unit.manifest.environment.neutralizedVariableNames -contains 'SAAIA_LIVE_AGENT_BANK')
    $pg = Run-Plan 'pg-missing' @('-Profile', 'IntegrationPg', '-PlanOnly') @{
        SAAIA_TEST_PG_CONN = $null
    }

    Assert-Profile 'requested PG profile fails without prerequisite' ($pg.exitCode -eq 1 -and $pg.manifest.state -eq 'failed' -and @($pg.manifest.results).Count -eq 0)
    $validPg = Run-Plan 'pg-standard-loopback' @('-Profile', 'IntegrationPg', '-PlanOnly') @{
        SAAIA_TEST_PG_CONN = 'Host=127.0.0.1;Port=5432;Database=postgres;Username=fixture;Password=synthetic-fixture-only;Pooling=false;Timeout=10;Command Timeout=120;Include Error Detail=false'
    }
    Assert-Profile 'standard isolated loopback connection prepares without network access' ($validPg.exitCode -eq 0 -and $validPg.manifest.state -eq 'prepared')
    $invalidConnections = @{
        'duplicate-host' = 'Host=127.0.0.1;Host=remote.invalid;Database=fixture'
        'conflicting-host-alias' = 'Host=127.0.0.1;Server=remote.invalid;Database=fixture'
        'conflicting-data-source-alias' = 'Host=127.0.0.1;Data Source=remote.invalid;Database=fixture'
        'conflicting-datasource-alias' = 'Host=127.0.0.1;DataSource=remote.invalid;Database=fixture'
        'duplicate-case-insensitive-host' = 'Host=127.0.0.1;HOST=remote.invalid;Database=fixture'
        'multi-host-list' = 'Host=127.0.0.1,remote.invalid;Database=fixture'
        'missing-host' = 'Database=fixture;Username=fixture'
        'malformed-connection' = 'Host=127.0.0.1;bare-key'
    }
    foreach ($case in $invalidConnections.GetEnumerator()) {
        $invalidPg = Run-Plan "pg-$($case.Key)" @('-Profile', 'IntegrationPg', '-PlanOnly') @{
            SAAIA_TEST_PG_CONN = $case.Value
        }
        Assert-Profile "PG connection $($case.Key) is rejected before execution" ($invalidPg.exitCode -eq 1 -and @($invalidPg.manifest.results).Count -eq 0)
    }
    $quotedPg = Run-Plan 'pg-quoted-loopback' @('-Profile', 'IntegrationPg', '-PlanOnly') @{
        SAAIA_TEST_PG_CONN = "Host='localhost';Database=fixture;Password='semicolon;in;password'"
    }
    Assert-Profile 'quoted loopback fixture connection prepares without network access' ($quotedPg.exitCode -eq 0 -and $quotedPg.manifest.state -eq 'prepared')
    $paid = Run-Plan 'paid-missing' @('-Profile', 'LivePaid', '-PlanOnly')
    Assert-Profile 'paid profile requires current explicit authorization' ($paid.exitCode -eq 1 -and @($paid.manifest.results).Count -eq 0)
    $local = Run-Plan 'local-remote' @('-Profile', 'LiveLocal', '-Filter', 'FullyQualifiedName~NeverExecute', '-PlanOnly') @{
        SAAIA_VALIDATION_LLM_BASE_URL = 'https://example.invalid'
    }

    Assert-Profile 'local profile rejects remote endpoint before execution' ($local.exitCode -eq 1 -and @($local.manifest.results).Count -eq 0)
    $empty = Run-Plan 'no-matched-tests' @('-Profile', 'Unit', '-Projects', 'Contracts', '-Filter', 'FullyQualifiedName~SAAIA_NO_SUCH_TEST', '-NoBuild', '-NoRestore')
    Assert-Profile 'empty selection does not report a successful suite' ($empty.exitCode -eq 1 -and $empty.manifest.state -eq 'failed')
    $executed = Run-Plan 'contracts-provenance' @('-Profile', 'Unit', '-Projects', 'Contracts', '-NoBuild', '-NoRestore')
    $executedStep = $executed.manifest.results[0]
    $actualTrxHash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $executedStep.trx) -Algorithm SHA256).Hash
    $binaryHashesMatch = @($executedStep.assemblies).Count -ge 2
    foreach ($assembly in $executedStep.assemblies) {
        $binaryHashesMatch = $binaryHashesMatch -and $assembly.sha256 -eq
            (Get-FileHash -LiteralPath (Join-Path $repo $assembly.path) -Algorithm SHA256).Hash
    }
    Assert-Profile 'executed suite identifies actual TRX binaries and working-tree inventories' (
        $executed.exitCode -eq 0 -and $binaryHashesMatch -and $actualTrxHash -eq $executedStep.trxSha256 -and
        $executed.manifest.sourceBefore.fileCount -gt 0 -and $executed.manifest.sourceAfter.fileCount -gt 0 -and
        (Test-Path -LiteralPath (Join-Path $OutputDirectory $executed.manifest.sourceBefore.inventory)) -and
        (Test-Path -LiteralPath (Join-Path $OutputDirectory $executed.manifest.sourceAfter.inventory)))
    $existingPath = Join-Path $OutputDirectory 'contracts-provenance.json'
    $existingHash = (Get-FileHash -LiteralPath $existingPath -Algorithm SHA256).Hash
    $overwrite = Invoke-Child @('-File', (Join-Path $repo 'tools/test-project.ps1'), '-Profile', 'Unit', '-Projects', 'Contracts', '-PlanOnly', '-OutputPath', $existingPath)
    Assert-Profile 'existing campaign evidence is never overwritten' ($overwrite.exitCode -ne 0 -and
        (Get-FileHash -LiteralPath $existingPath -Algorithm SHA256).Hash -eq $existingHash)

    # A disposable framework fixture proves actual xUnit v2 NotExecuted outcomes.
    # It has no HTTP client, database connection or provider dependency.
    $probe = Join-Path $OutputDirectory 'discovery-fixture'
    [IO.Directory]::CreateDirectory($probe) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $probe 'empty-feed')) | Out-Null
    $profilesPath = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'TestProfiles.cs'))
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsTestProject>true</IsTestProject><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/><PackageReference Include="xunit" Version="2.9.2"/><PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/><Compile Include="$profilesPath" Link="TestProfiles.cs"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $probe 'DiscoveryFixture.csproj')
    @'
using Xunit;
public sealed class ProfileDiscoveryFixture
{
    [Fact] public void Unit_contract_executes() { }
    [ProfileFact("IntegrationPg")] public void Missing_pg_prerequisite_fails_if_requested() => throw new InvalidOperationException("Synthetic missing prerequisite, not a PostgreSQL connection.");
    [ProfileFact("LivePaid")] public void Paid_body_must_never_execute()
    {
        var marker=Environment.GetEnvironmentVariable("SAAIA_TEST_BODY_MARKER");
        if(marker is not null) File.WriteAllText(marker,"body executed");
        throw new InvalidOperationException("No provider was invoked.");
    }
}
'@ | Set-Content -LiteralPath (Join-Path $probe 'ProfileDiscoveryFixture.cs')
    $restore = Invoke-Child @('-Command', "& dotnet restore '$($probe.Replace("'","''"))/DiscoveryFixture.csproj' --source '$($probe.Replace("'","''"))/empty-feed' --verbosity quiet; exit `$LASTEXITCODE")
    if ($restore.exitCode -ne 0) {
        throw 'Discovery fixture requires the existing xUnit/.NET package cache; run the Unit suite once first.'
    }

    $trx = 'discovery-unit.trx'
    $run = Invoke-Child @('-Command', "& dotnet test '$($probe.Replace("'","''"))/DiscoveryFixture.csproj' --no-restore --results-directory '$($probe.Replace("'","''"))' --logger 'trx;LogFileName=$trx'; exit `$LASTEXITCODE") @{
        SAAIA_TEST_PROFILE = 'Unit'
        SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST = '1'
        SAAIA_LIVE_AGENT_BANK = '1'
    }

    [xml]$result = Get-Content -LiteralPath (Join-Path $probe $trx) -Raw
    $counts = $result.SelectSingleNode("//*[local-name()='Counters']")
    $outcomes = @($result.SelectNodes("//*[local-name()='UnitTestResult']"))
    Assert-Profile 'xUnit discovery reports true skips despite inherited live flags' ($run.exitCode -eq 0 -and [int]$counts.passed -eq 1 -and @($outcomes | Where-Object outcome -eq 'NotExecuted').Count -eq 2)
    $runPg = Invoke-Child @('-Command', "& dotnet test '$($probe.Replace("'","''"))/DiscoveryFixture.csproj' --no-build --no-restore --results-directory '$($probe.Replace("'","''"))' --logger 'trx;LogFileName=discovery-pg.trx'; exit `$LASTEXITCODE") @{
        SAAIA_TEST_PROFILE = 'IntegrationPg'
    }

    [xml]$resultPg = Get-Content -LiteralPath (Join-Path $probe 'discovery-pg.trx') -Raw
    Assert-Profile 'selected profile does not swallow missing prerequisites' ($runPg.exitCode -eq 1 -and [int]$resultPg.SelectSingleNode("//*[local-name()='Counters']").failed -eq 1)
    $marker = Join-Path $probe 'paid-body-must-not-run.txt'
    $runPaid = Invoke-Child @('-Command', "& dotnet test '$($probe.Replace("'","''"))/DiscoveryFixture.csproj' --no-build --no-restore --results-directory '$($probe.Replace("'","''"))' --logger 'trx;LogFileName=discovery-paid.trx'; exit `$LASTEXITCODE") @{
        SAAIA_TEST_PROFILE = 'LivePaid'
        SAAIA_TEST_PAID_AUTHORIZED = $null
        SAAIA_TEST_BODY_MARKER = $marker
    }

    Assert-Profile 'missing paid authorization fails before the live test body' ($runPaid.exitCode -eq 1 -and -not (Test-Path -LiteralPath $marker))
} finally {
    @{
        schemaVersion = 'saaia-test-profile-checks-v1'
        executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        providerCalls = 0
        checks = $checks
    }
    | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'checks.json') -Encoding utf8
}

Write-Host "$($checks.Count) test-profile checks passed; provider calls: 0; artifacts: $OutputDirectory"
