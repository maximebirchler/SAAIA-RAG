using System.IO;
using SAAIA.Client.WinUI.Controls;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class UiLocalizationSafetyNetTests
{
    [Theory]
    [InlineData("fr", "Sources", "Ouvrir")]
    [InlineData("en", "Sources", "Open")]
    [InlineData("de", "Quellen", "Oeffnen")]
    public void Sources_cards_labels_are_localized(string language, string expectedHeader, string expectedButton)
    {
        Assert.Equal(expectedHeader, SourcesCardsControl.GetSourcesHeaderText(language));
        Assert.Equal(expectedButton, SourcesCardsControl.GetOpenButtonText(language));
    }

    [Fact]
    public void MainWindow_apply_ui_language_updates_sources_cards_control()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "HelpAndLocalization.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("SourcesCards.ApplyUiLanguage(lang)", source);
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
