using SAAIA.Client.WinUI;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ManualStartupConnectionContractTests
{
    [Fact]
    public void Explicit_connection_calls_the_real_connect_path_without_changing_the_automatic_connection_preference()
    {
        var calls = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "ConnectFromStartupOverlayAsync");
        Assert.Contains(calls, call => call.DeclaringType == typeof(MainWindow) && call.Name == "ConnectAsync");
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(AppSettings) && call.Name == "set_AutoConnect");
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(MainWindow) && call.Name == "InitializeUserModeAsync");
    }

    [Fact]
    public void Startup_and_theme_rebuild_keep_an_explicit_disconnected_state_available()
    {
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "InitializeUserModeAsync"),
            call => call.DeclaringType == typeof(MainWindow) && call.Name == "ShowStartupOverlayDisconnected");
        var rebuild = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "RebuildStartupOverlayForTheme");
        Assert.Contains(rebuild, call => call.DeclaringType == typeof(MainWindow) && call.Name == "ShowStartupOverlayDisconnected");
        Assert.Contains(rebuild, call => call.DeclaringType == typeof(MainWindow) && call.Name == "ShowStartupOverlayError");
    }

    [Fact]
    public void Disconnected_presentation_uses_manual_copy_and_not_the_error_presentation()
    {
        var strings = CompiledArchitectureContract.StringsForMethod(typeof(MainWindow), "ShowStartupOverlayDisconnected");
        Assert.Contains("startup.manual.title", strings);
        Assert.Contains("startup.manual.detail", strings);
        Assert.Contains("startup.manual.setup_required", strings);
        Assert.Contains("startup.connect", strings);
        Assert.Contains("startup.configure", strings);
        Assert.DoesNotContain(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "ShowStartupOverlayDisconnected"),
            call => call.DeclaringType == typeof(MainWindow) && call.Name == "ShowStartupOverlayError");
    }

    [Theory]
    [InlineData("fr", "Se connecter", "Client déconnecté")]
    [InlineData("en", "Connect", "Client disconnected")]
    [InlineData("es", "Conectar", "Cliente desconectado")]
    [InlineData("pt", "Ligar", "Cliente desligado")]
    [InlineData("de", "Verbinden", "Client nicht verbunden")]
    [InlineData("it", "Connetti", "Client disconnesso")]
    public void Manual_connection_actions_are_localized_in_all_supported_languages(string language, string connect, string title)
    {
        Assert.Equal(connect, ClientUiText.Get("startup.connect", language));
        Assert.Equal(title, ClientUiText.Get("startup.manual.title", language));
        Assert.NotEqual("startup.manual.detail", ClientUiText.Get("startup.manual.detail", language));
        Assert.NotEqual("startup.manual.setup_required", ClientUiText.Get("startup.manual.setup_required", language));
    }
}
