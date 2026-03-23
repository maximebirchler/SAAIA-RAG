using System;
using System.Reflection;
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
    public void Short_english_follow_up_with_thanks_and_category_is_detected_as_english()
    {
        Assert.Equal("en", LocalizedStrings.DetectLanguage("Thanks, and category 3?", "fr"));
    }


    [Fact]
    public void French_short_question_is_not_forced_to_previous_spanish_translation_language()
    {
        Assert.Equal("fr", LocalizedStrings.DetectLanguage("qui es tu ?", "es"));
    }

    [Fact]
    public void French_follow_up_replay_question_is_not_forced_to_previous_spanish_translation_language()
    {
        Assert.Equal("fr", LocalizedStrings.DetectLanguage("qu'est-ce que tu viens de me lister ?", "es"));
    }

    [Theory]
    [InlineData("traduis en anglais", "en")]
    [InlineData("translate in english", "en")]
    [InlineData("traduis en portugais", "pt")]
    public void Translation_commands_are_detected_as_one_shot_language_requests(string input, string expectedLanguage)
    {
        Assert.True(LocalizedStrings.TryDetectLanguagePreferenceChange(input, out var language));
        Assert.Equal(expectedLanguage, language);
    }


    [Theory]
    [InlineData("ça donne quoi en espagnol ?", "es")]
    [InlineData("what about in italian?", "it")]
    [InlineData("et en anglais", "en")]
    public void Informal_translation_follow_ups_are_detected_as_one_shot_language_requests(string input, string expectedLanguage)
    {
        Assert.True(LocalizedStrings.TryDetectLanguagePreferenceChange(input, out var language));
        Assert.Equal(expectedLanguage, language);
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

    [Fact]
    public void One_shot_translation_updates_last_answer_language_without_persisting_session_reply_language()
    {
        var mem = new ToolMemory
        {
            LastLanguage = "fr",
            LastAnswerLanguage = "fr"
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("RememberTurnState", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(sut, new object?[]
        {
            "traduis en anglais",
            "I am SAAIA.",
            "meta.translate_last_answer",
            new[] { "meta.translate_last_answer" },
            Array.Empty<string>()
        });

        Assert.Equal("fr", mem.LastLanguage);
        Assert.Equal("en", mem.LastAnswerLanguage);
        Assert.Equal("I am SAAIA.", mem.LastAssistantAnswer);
    }

    [Fact]
    public void English_question_language_is_detected_from_the_current_message_even_after_french_history()
    {
        Assert.Equal("en", LocalizedStrings.DetectLanguage("Who are you?", "fr"));
    }

}
