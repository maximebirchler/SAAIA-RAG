using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string UserInstanceContextPresencePrompt =
        "Does request or conversation supply any actual configuration, operating state or project phase of the user's own real setup? A question asking what its state is supplies no state. A hypothetical example is not an actual state. Actual supplied user facts remain supplied even if insufficient for a final decision. Return one JSON boolean actualContextSupplied.";

    private static LlmStructuredOutputContract BuildUserInstanceContextPresenceContract()
        => new("saaia_user_instance_context_v1", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { actualContextSupplied = new { type = "boolean" } },
            required = new[] { "actualContextSupplied" }, additionalProperties = false
        }));

    private static bool TryResolveUserInstanceContextPresence(SourceBackedAgentCompletion completion, out bool supplied)
    {
        supplied = false;
        if (completion.ToolCalls.Count != 0 || completion.ProtocolError is { Length: > 0 }
            || completion.FinishReason != "stop") return false;
        try
        {
            using var doc = JsonDocument.Parse(completion.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("actualContextSupplied", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            supplied = value.GetBoolean();
            return true;
        }
        catch (JsonException) { return false; }
    }
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
