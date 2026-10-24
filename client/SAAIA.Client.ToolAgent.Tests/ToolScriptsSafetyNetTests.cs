using System.IO;
using System.Diagnostics;
using System.Text.Json;
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

    [Theory]
    [InlineData(false, 0, "not_executed")]
    [InlineData(true, 1, "failed")]
    public async Task Runtime_ci_harness_reports_unavailable_runtime_and_honors_strict_mode(
        bool strict, int expectedExitCode, string expectedStatus)
    {
        var repoRoot = FindRepoRoot();
        var report = Path.Combine(Path.GetTempPath(), "saaia-harness-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = repoRoot
            };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(repoRoot, "tools", "runtime-ci-harness.ps1"),
                         "-SkipDotnet", "-RuntimeBaseUrl", "", "-OutputPath", report })
                start.ArgumentList.Add(argument);
            if (strict) start.ArgumentList.Add("-StrictRuntime");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == expectedExitCode, (await stdout) + (await stderr));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.Equal(expectedStatus, manifest.RootElement.GetProperty("summary").GetProperty("status").GetString());
            var probe = Assert.Single(manifest.RootElement.GetProperty("steps").EnumerateArray());
            Assert.Equal("runtime_probes", probe.GetProperty("name").GetString());
            Assert.Equal("skipped", probe.GetProperty("status").GetString());
        }
        finally { if (File.Exists(report)) File.Delete(report); }
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
