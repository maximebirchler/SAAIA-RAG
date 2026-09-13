namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    // The LLM chooses the semantic family. Code renders that typed decision;
    // it does not infer a business domain or invent required technical fields.
    private static RouterPlan BuildMissingInstanceFactsRouterPlan(string language)
    {
        var question = SourceBackedLabel(NormalizeLanguageCode(language),
            "Quelle est la configuration, l’état actuel et la phase de votre projet pour ce cas ?",
            "What are the configuration, current state and project phase for your situation?",
            "¿Cuáles son la configuración, el estado actual y la fase de su proyecto en este caso?",
            "Quais são a configuração, o estado atual e a fase do seu projeto neste caso?",
            "Wie sind die Konfiguration, der aktuelle Zustand und die Projektphase in Ihrem Fall?",
            "Quali sono la configurazione, lo stato attuale e la fase del progetto nel suo caso?");
        return new RouterPlan
        {
            Intent = "clarification", Language = NormalizeLanguageCode(language),
            NeedClarification = true, ClarificationQuestions = [question],
            Origin = RouterPlanOrigin.Llm,
            Clarification = new RouterPlan.ClarificationDecisionPlan
            {
                Message = question, Options = [], ResumeRoute = "source_backed",
                AmbiguityKind = "constraints",
                ExecutionImpact = "Actual instance facts cannot be supplied by corpus evidence."
            }
        };
    }

    private static string IncludeUserInstanceContext(
        IReadOnlyList<(string role, string content)> history, string request)
    {
        var userContext = history.TakeLast(2)
            .Where(turn => string.Equals(turn.role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(turn => TruncateForPrompt(turn.content, 1_200))
            .Where(content => !string.IsNullOrWhiteSpace(content)
                && !string.Equals(content, request, StringComparison.Ordinal)).ToArray();
        return userContext.Length == 0 ? request
            : request + "\n\nUSER_INSTANCE_CONTEXT (user statements, not documentary evidence):\n"
                + string.Join("\n", userContext);
    }
}
