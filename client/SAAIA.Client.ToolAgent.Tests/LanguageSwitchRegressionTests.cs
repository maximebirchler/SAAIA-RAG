using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LanguageSwitchRegressionTests
{
    [Theory]
    [InlineData("antworte auf deutsch", "de")]
    [InlineData("rispondi in italiano", "it")]
    [InlineData("responde en español", "es")]
    [InlineData("fale em português", "pt")]
    public void Explicit_language_switch_is_detected_in_supported_languages(string input, string expectedLanguage)
    {
        Assert.True(LocalizedStrings.TryDetectLanguagePreferenceChange(input, out var language));
        Assert.Equal(expectedLanguage, language);
    }

    [Theory]
    [InlineData("cuántos documentos hay en el servidor ?", "es")]
    [InlineData("Quali documenti sono presenti sul server?", "it")]
    public void Query_language_detection_does_not_stick_to_previous_language(string input, string expectedLanguage)
    {
        Assert.Equal(expectedLanguage, LocalizedStrings.DetectLanguage(input, "de"));
    }

    [Fact]
    public void Empty_folder_replay_renders_the_full_previous_inventory_in_the_requested_language()
    {
        using var doc = JsonDocument.Parse("""
        {
          "total": 5,
          "items": [
            { "path": "Programmation/Allen-Bradley" },
            { "path": "Programmation/Allen-Bradley/FactoryTalk" },
            { "path": "Programmation/Allen-Bradley/Logix Designer" },
            { "path": "Programmation/Beckhoff" },
            { "path": "Programmation/Siemens" }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("empty_list", doc.RootElement, "de");

        Assert.Contains("Leere Ordner auf dem Server:", rendered);
        Assert.Contains("1. Programmation/Allen-Bradley", rendered);
        Assert.Contains("5. Programmation/Siemens", rendered);
    }
}
