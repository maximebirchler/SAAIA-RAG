using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string L(string key, string? language) => LocalizedStrings.Get(key, language);
    private static string LF(string key, string? language, params object[] args) => LocalizedStrings.Format(key, language, args);
    private string DetectLanguage(string? userMessage) => LocalizedStrings.DetectLanguage(userMessage, _mem.LastLanguage);
    private static string NormalizeLanguageCode(string? language) => LocalizedStrings.NormalizeLanguage(language);
    private static string LocalizedLanguageName(string language, string? uiLanguage) => LocalizedStrings.LocalizedLanguageName(language, uiLanguage);
    private static bool TryDetectLanguagePreferenceChange(string? text, out string language) => LocalizedStrings.TryDetectLanguagePreferenceChange(text, out language);
}
