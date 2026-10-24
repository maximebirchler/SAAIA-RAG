using System.IO;
using System.Xml.Linq;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ProjectStructureSafetyNetTests
{
    [Fact]
    public void WinUi_project_has_one_active_user_settings_dialog_implementation()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "SAAIA.Client.WinUI.csproj");
        var project = System.Xml.Linq.XDocument.Load(csprojPath);
        Assert.Equal("true", project.Descendants("UseWinUI").Single().Value);
        var controls = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Controls");
        Assert.True(File.Exists(Path.Combine(controls, "UserSettingsDialog.cs")));
        Assert.False(File.Exists(Path.Combine(controls, "UserSettingsDialog.xaml")));
        Assert.False(File.Exists(Path.Combine(controls, "UserSettingsDialog.xaml.cs")));
        Assert.DoesNotContain(project.Descendants().Attributes("Include"),
            item => item.Value.Contains("UserSettingsDialog.xaml", StringComparison.OrdinalIgnoreCase));
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
