using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private enum UserDocumentBindingState { None, Identified, Unbound, Unconfirmed }

    private const string UserDocumentBindingPrompt =
        "Extract copiedDocumentIdentity: an exact name/title/code/filename supplied in the request, or empty. Then userRequiresParticularDocument is true when the user asks about a particular document they have selected, named or unnamed. It is false for any suitable document or topic search, general information, decisions about their own setup, and social/app operations. Do not infer whether a document exists. Return these two JSON fields.";
    private const string DocumentTargetPrompt =
        "Extract the object whose information the user asks for. copiedIdentity is a verbatim document name/title/code/filename supplied, otherwise empty. objectKind is document when one document selected by the user is the object, corpus_candidates for a subject lookup or any suitable documents, user_setup for their real installation/device/project, other for general rules, hypothetical examples, social/app operations. Do not infer availability or missingness. Return these two fields.";

    private static async Task<UserDocumentBindingState> CompleteUserDocumentBindingAsync(
        ISourceBackedAgentStructuredLlmClient llm, IReadOnlyList<(string role, string content)> history,
        string request, CancellationToken ct)
    {
        var suppliedTurns = history.Where(turn => turn.role == "user").TakeLast(2).ToArray();
        var input = "CHAT_TAIL:\n" + SerializeTail(suppliedTurns, 2) + "\n\nUSER_MESSAGE:\n" + request;
        var groundingText = request + "\n" + string.Join("\n", suppliedTurns.Select(turn => turn.content));
        var completion = await llm.CompleteStructuredAsync(
            [SourceBackedAgentMessage.System(UserDocumentBindingPrompt), SourceBackedAgentMessage.User(input)],
            new("saaia_user_document_binding_v1", JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new
                { copiedDocumentIdentity = new { type = "string" }, userRequiresParticularDocument = new { type = "boolean" } },
                required = new[] { "copiedDocumentIdentity", "userRequiresParticularDocument" }, additionalProperties = false
            })), 128, ct, temperatureOverride: 0).ConfigureAwait(false);
        if (!TryReadDocumentBinding(completion, "copiedDocumentIdentity", "userRequiresParticularDocument",
                groundingText, out var identity, out var particular, out _)) return UserDocumentBindingState.Unconfirmed;
        if (!particular) return UserDocumentBindingState.None;
        if (identity.Length > 0) return UserDocumentBindingState.Identified;

        // Requiring a particular document and targeting one document are distinct
        // semantic decisions. Agreement is required before declaring its identity missing.
        var target = await llm.CompleteStructuredAsync(
            [SourceBackedAgentMessage.System(DocumentTargetPrompt), SourceBackedAgentMessage.User(input)],
            new("saaia_document_target_v1", JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new
                { copiedIdentity = new { type = "string" }, objectKind = new { type = "string", @enum = new[] { "document", "corpus_candidates", "user_setup", "other" } } },
                required = new[] { "copiedIdentity", "objectKind" }, additionalProperties = false
            })), 128, ct, temperatureOverride: 0).ConfigureAwait(false);
        if (!TryReadDocumentBinding(target, "copiedIdentity", "objectKind", groundingText,
                out identity, out _, out var kind)) return UserDocumentBindingState.Unconfirmed;
        return kind == "document" ? identity.Length > 0 ? UserDocumentBindingState.Identified : UserDocumentBindingState.Unbound
            : UserDocumentBindingState.None;
    }

    private static bool TryReadDocumentBinding(SourceBackedAgentCompletion completion, string identityField,
        string decisionField, string groundingText, out string identity, out bool particular, out string kind)
    {
        identity = kind = ""; particular = false;
        if (completion.ToolCalls.Count != 0 || completion.ProtocolError is { Length: > 0 } || completion.FinishReason != "stop") return false;
        try
        {
            using var doc = JsonDocument.Parse(completion.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty(identityField, out var copied) || copied.ValueKind != JsonValueKind.String
                || !root.TryGetProperty(decisionField, out var decision)) return false;
            identity = (copied.GetString() ?? "").Trim();
            if (identity.Length > 400 || identity.Length > 0 && !groundingText.Contains(identity, StringComparison.OrdinalIgnoreCase)) return false;
            if (decisionField == "userRequiresParticularDocument")
            {
                if (decision.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                particular = decision.GetBoolean();
            }
            else
            {
                if (decision.ValueKind != JsonValueKind.String) return false;
                kind = decision.GetString() ?? "";
                if (kind is not ("document" or "corpus_candidates" or "user_setup" or "other")) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static RouterPlan BuildMissingDocumentReferenceRouterPlan(string language)
    {
        var question = SourceBackedLabel(NormalizeLanguageCode(language),
            "Quel document souhaitez-vous vérifier (nom ou référence) ?", "Which document do you want to check (name or reference)?",
            "¿Qué documento desea comprobar (nombre o referencia)?", "Qual documento deseja verificar (nome ou referência)?",
            "Welches Dokument möchten Sie prüfen (Name oder Referenz)?", "Quale documento vuole verificare (nome o riferimento)?");
        return new RouterPlan { Intent = "clarification", Language = NormalizeLanguageCode(language), NeedClarification = true,
            ClarificationQuestions = [question], Origin = RouterPlanOrigin.Llm,
            Clarification = new RouterPlan.ClarificationDecisionPlan { Message = question, Options = [], ResumeRoute = "source_backed",
                AmbiguityKind = "reference", ExecutionImpact = "The user-selected document must be identified before retrieval." } };
    }
}
