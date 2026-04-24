using System.IO;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolScriptsSafetyNetTests
{
    [Fact]
    public void Repo_contains_all_critical_tool_scripts()
    {
        var repoRoot = FindRepoRoot();
        var toolsRoot = Path.Combine(repoRoot, "tools");

        Assert.True(File.Exists(Path.Combine(toolsRoot, "compute-model-reference-checksums.ps1")));
        Assert.True(File.Exists(Path.Combine(toolsRoot, "regenerate-governance-artifacts.ps1")));
        Assert.True(File.Exists(Path.Combine(toolsRoot, "repair-governance-artifacts.ps1")));
        Assert.True(File.Exists(Path.Combine(toolsRoot, "runtime-ci-harness.ps1")));
        Assert.True(File.Exists(Path.Combine(toolsRoot, "verify-governance-artifacts.ps1")));
    }

    [Fact]
    public void Runtime_ci_harness_exposes_smoke_and_runtime_probe_controls()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "tools", "runtime-ci-harness.ps1");
        var source = File.ReadAllText(file);

        Assert.Contains("[string]$RuntimeBaseUrl = $env:SAAIA_RUNTIME_CI_BASE_URL", source);
        Assert.Contains("[switch]$SkipDotnet", source);
        Assert.Contains("[switch]$StrictRuntime", source);
        Assert.Contains("SAAIA_RUNTIME_CI_BASE_URL not set", source);
        Assert.Contains("runtime_probes", source);
    }

    [Fact]
    public void Governance_tool_scripts_cover_verify_repair_and_regeneration_flows()
    {
        var repoRoot = FindRepoRoot();
        var verify = File.ReadAllText(Path.Combine(repoRoot, "tools", "verify-governance-artifacts.ps1"));
        var repair = File.ReadAllText(Path.Combine(repoRoot, "tools", "repair-governance-artifacts.ps1"));
        var regenerate = File.ReadAllText(Path.Combine(repoRoot, "tools", "regenerate-governance-artifacts.ps1"));

        Assert.Contains("[switch]$AsJson", verify);
        Assert.Contains("active-runtime.json", verify);
        Assert.Contains("[switch]$Apply", repair);
        Assert.Contains("Dry run only", repair);
        Assert.Contains("[switch]$QualifyLocalRuntime", regenerate);
        Assert.Contains("--governance-init-only", regenerate);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "TODO.md")))
                return current;

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root not found from test output directory.");
    }
}
