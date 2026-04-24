using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StartupMaintenanceModeTests
{
    [Fact]
    public void Parse_returns_null_when_no_maintenance_flag_is_present()
    {
        var command = StartupMaintenanceMode.Parse(new[] { "SAAIA.Client.WinUI.exe" });

        Assert.Null(command);
    }

    [Fact]
    public void Parse_recognizes_governance_init_only_flag()
    {
        var command = StartupMaintenanceMode.Parse(new[]
        {
            "SAAIA.Client.WinUI.exe",
            "--governance-init-only"
        });

        Assert.NotNull(command);
        Assert.True(command!.GovernanceInitOnly);
        Assert.False(command.QualifyLocalRuntimeOnly);
        Assert.Null(command.GovernanceRoot);
    }

    [Fact]
    public void Parse_recognizes_qualify_local_runtime_only_flag()
    {
        var command = StartupMaintenanceMode.Parse(new[]
        {
            "SAAIA.Client.WinUI.exe",
            "--qualify-local-runtime-only"
        });

        Assert.NotNull(command);
        Assert.False(command!.GovernanceInitOnly);
        Assert.True(command.QualifyLocalRuntimeOnly);
        Assert.Null(command.GovernanceRoot);
    }

    [Fact]
    public void Parse_recognizes_optional_governance_root()
    {
        var command = StartupMaintenanceMode.Parse(new[]
        {
            "SAAIA.Client.WinUI.exe",
            "--governance-init-only",
            "--governance-root",
            @"C:\temp\saaia-governance"
        });

        Assert.NotNull(command);
        Assert.True(command!.GovernanceInitOnly);
        Assert.False(command.QualifyLocalRuntimeOnly);
        Assert.Equal(@"C:\temp\saaia-governance", command.GovernanceRoot);
    }

    [Fact]
    public void Parse_allows_qualification_and_custom_root_together()
    {
        var command = StartupMaintenanceMode.Parse(new[]
        {
            "SAAIA.Client.WinUI.exe",
            "--qualify-local-runtime-only",
            "--governance-root",
            @"C:\temp\saaia-governance"
        });

        Assert.NotNull(command);
        Assert.False(command!.GovernanceInitOnly);
        Assert.True(command.QualifyLocalRuntimeOnly);
        Assert.Equal(@"C:\temp\saaia-governance", command.GovernanceRoot);
    }

    [Fact]
    public void Parse_throws_when_governance_root_value_is_missing()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            StartupMaintenanceMode.Parse(new[]
            {
                "SAAIA.Client.WinUI.exe",
                "--governance-init-only",
                "--governance-root"
            }));

        Assert.Contains("--governance-root", ex.Message);
    }
}
