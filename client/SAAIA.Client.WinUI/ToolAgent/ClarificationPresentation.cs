namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal static class ClarificationPresentation
{
    internal static string BuildChoiceMessage(
        string understanding,
        IReadOnlyList<string> options,
        string? language)
    {
        var cleanUnderstanding = understanding.Trim();
        var repeatsDisplayedOption = options.Any(option =>
            !string.IsNullOrWhiteSpace(option)
            && cleanUnderstanding.Contains(
                option.Trim(),
                StringComparison.OrdinalIgnoreCase));
        return repeatsDisplayedOption || cleanUnderstanding.Length == 0
            ? ChoiceQuestion(language)
            : cleanUnderstanding
              + Environment.NewLine
              + Environment.NewLine
              + ChoiceQuestion(language);
    }

    private static string ChoiceQuestion(string? language)
        => (language ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "en" => "Which option would you like me to use?",
            "es" => "¿Qué opción quieres que utilice?",
            "pt" => "Qual opção você quer que eu use?",
            "de" => "Welche Option soll ich verwenden?",
            "it" => "Quale opzione vuoi che utilizzi?",
            _ => "Quelle option souhaitez-vous que j’utilise ?"
        };
}
