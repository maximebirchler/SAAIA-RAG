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

    [Fact]
    public void MainWindow_apply_ui_language_covers_core_visible_labels()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "HelpAndLocalization.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("ChatsHeaderText.Text = ClientUiText.Get(\"panel.chats\", lang)", source);
        Assert.Contains("NewChatButton.Content = ClientUiText.Get(\"button.new\", lang)", source);
        Assert.Contains("JumpBottomButton.Content = ClientUiText.Get(\"button.jump_bottom\", lang)", source);
        Assert.Contains("TypingText.Text = ClientUiText.Get(\"typing\", lang)", source);
        Assert.Contains("InputBox.PlaceholderText = ClientUiText.Get(\"input.placeholder\", lang)", source);
        Assert.Contains("ConnectButton.Content = ClientUiText.Get(\"button.connect\", lang)", source);
        Assert.Contains("UserSettingsButton.Content = ClientUiText.Get(\"header.settings\", lang)", source);
        Assert.Contains("SourcesToggleButton.Content = ClientUiText.Get(\"panel.sources\", lang)", source);
        Assert.Contains("SourcesPanelTitleText.Text = ClientUiText.Get(\"panel.sources\", lang)", source);
        Assert.Contains("LocalLlmRuntimeDiagnosticsButton.Content = ClientUiText.Get(\"button.runtime_diagnostics\", lang)", source);
    }

    [Fact]
    public void Session_menu_labels_are_localized_on_open()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "Sessions.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("rename.Text = ClientUiText.Get(\"session.menu.rename\", _appSettings.UiLanguage)", source);
        Assert.Contains("delete.Text = ClientUiText.Get(\"session.menu.delete\", _appSettings.UiLanguage)", source);
    }

    [Fact]
    public void Setup_wizard_applies_localized_placeholders_from_code()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "SetupWizardDialog.xaml.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("ApiKeyBox.PlaceholderText = \"saaia_…\"", source);
        Assert.Contains("LlamaExeBox.PlaceholderText = SZ(", source);
        Assert.Contains("ModelPathBox.PlaceholderText = SZ(", source);
        Assert.Contains("HostBox.PlaceholderText = SZ(", source);
        Assert.Contains("PortBox.PlaceholderText = SZ(", source);
        Assert.Contains("ModelIdBox.PlaceholderText = SZ(", source);
    }

    [Fact]
    public void Setup_wizard_applies_localized_title_buttons_and_advanced_labels_from_code()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "Controls", "SetupWizardDialog.xaml.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("HeroTitleText.Text = SZ(", source);
        Assert.Contains("WizardApplyButton.Content = SZ(", source);
        Assert.Contains("WizardCancelButton.Content = SZ(", source);
        Assert.Contains("TestReadyButton.Content = SZ(", source);
        Assert.Contains("TestApiKeyButton.Content = SZ(", source);
        Assert.Contains("UseLocalLlmCheck.Content = SZ(", source);
        Assert.Contains("AutoStartCheck.Content = SZ(", source);
        Assert.Contains("TestModelsButton.Content = SZ(", source);
        Assert.Contains("StartLocalLlmButton.Content = SZ(", source);
        Assert.Contains("StopLocalLlmButton.Content = SZ(", source);
    }

    [Fact]
    public void Admin_runtime_overlay_uses_localized_runtime_summary_labels()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(repoRoot, "client", "SAAIA.Client.WinUI", "MainWindow", "AdminRuntimeOpsPanel.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("LocalRuntimeText(\"Evenements runtime recents :\"", source);
        Assert.Contains("LocalRuntimeText(\"Evenements runtime recents : aucun\"", source);
        Assert.Contains("ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)", source);
        Assert.Contains("ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)", source);
        Assert.Contains("ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)", source);
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
