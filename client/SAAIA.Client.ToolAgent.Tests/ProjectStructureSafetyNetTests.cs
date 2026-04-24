using System.IO;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ProjectStructureSafetyNetTests
{
    [Fact]
    public void WinUi_project_keeps_legacy_user_settings_xaml_out_of_compilation()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "SAAIA.Client.WinUI.csproj");
        var source = File.ReadAllText(csprojPath);

        Assert.Contains("<Page Remove=\"Controls\\UserSettingsDialog.xaml\" />", source);
        Assert.Contains("<Compile Remove=\"Controls\\UserSettingsDialog.xaml.cs\" />", source);
        Assert.Contains("<None Include=\"Controls\\UserSettingsDialog.xaml\" />", source);
        Assert.Contains("<None Include=\"Controls\\UserSettingsDialog.xaml.cs\" />", source);
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
