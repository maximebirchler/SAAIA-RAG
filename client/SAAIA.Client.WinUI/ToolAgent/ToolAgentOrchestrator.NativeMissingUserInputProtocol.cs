using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string RequestMissingUserInputToolName = "request_missing_user_input";
    private const string ExplicitDocumentIdentityAlreadySuppliedFailure =
        "native_missing_user_input_explicit_document_identity_already_supplied";

    private static SourceBackedAgentToolDefinition BuildNativeMissingUserInputTool()
        => new(RequestMissingUserInputToolName,
            "Ask one open question for essential user input missing from the request and conversation. Do not ask the user to supply facts or choose answers that corpus evidence can establish. Do not invent options.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    question = BoundedNativeRouterString(2, 300,
                        "One direct question in the user language. Ask for the missing information without suggesting invented values."),
                    userTextAnchor = BoundedNativeRouterString(2, 180,
                        "Exact span copied from the current request containing the unbound reference or the need for the missing input."),
                    missingInformation = BoundedNativeRouterString(2, 240,
                        "What essential user-provided information is missing and why the corpus cannot supply it."),
                    resumeRoute = new
                    {
                        type = "string",
                        @enum = new[] { "source_backed", "source_backed_grid", "operational" }
                    }
                },
                required = new[] { "question", "userTextAnchor", "missingInformation", "resumeRoute" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private bool TryBuildNativeMissingUserInputPlan(
        JsonElement arguments, string sourceRequest, string detectedLanguage,
        out RouterPlan plan, out string failureReason)
    {
        plan = new RouterPlan();
        failureReason = "native_missing_user_input_contract_invalid";
        var required = new[] { "question", "userTextAnchor", "missingInformation", "resumeRoute" };
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Count() != required.Length
            || required.Any(name => !arguments.TryGetProperty(name, out var value)
                                    || value.ValueKind != JsonValueKind.String))
            return false;

        var question = arguments.GetProperty("question").GetString()!;
        var anchor = arguments.GetProperty("userTextAnchor").GetString()!;
        var missingInformation = arguments.GetProperty("missingInformation").GetString()!;
        var resumeRoute = arguments.GetProperty("resumeRoute").GetString()!;
        if (question.Length is < 2 or > 300 || anchor.Length is < 2 or > 180
            || missingInformation.Length is < 2 or > 240
            || question.Trim().Length < 2 || missingInformation.Trim().Length < 2
            || string.IsNullOrWhiteSpace(anchor)
            || !sourceRequest.Contains(anchor, StringComparison.Ordinal)
            || resumeRoute is not ("source_backed" or "source_backed_grid" or "operational"))
            return false;

        if (RedundantlyRequestsExplicitDocumentIdentity(
                sourceRequest,
                question,
                anchor))
        {
            failureReason = ExplicitDocumentIdentityAlreadySuppliedFailure;
            return false;
        }

        question = question.Trim();
        missingInformation = missingInformation.Trim();
        plan = SanitizeRouterPlan(new RouterPlan
        {
            Intent = "clarification",
            Language = NormalizeLanguageCode(detectedLanguage),
            NeedClarification = true,
            ClarificationQuestions = new List<string> { question },
            Clarification = new RouterPlan.ClarificationDecisionPlan
            {
                Message = question,
                Options = new List<string>(),
                ExecutionImpact = missingInformation,
                ResumeRoute = resumeRoute,
                AmbiguityKind = "other"
            },
            Origin = RouterPlanOrigin.Llm
        }, detectedLanguage, disallowMetaSetLanguage: false);
        plan.Origin = RouterPlanOrigin.Llm;
        failureReason = string.Empty;
        return true;
    }

    private static bool RedundantlyRequestsExplicitDocumentIdentity(
        string sourceRequest,
        string question,
        string userTextAnchor)
    {
        var explicitDocument = TryExtractPdfFileNameRequestedTitle(sourceRequest);
        if (string.IsNullOrWhiteSpace(explicitDocument))
            return false;

        var fileName = Path.GetFileName(explicitDocument.Trim());
        if (fileName.Length == 0
            || !userTextAnchor.Contains(
                fileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var asksForDocumentIdentity = Regex.IsMatch(
                question,
                @"\b(?:titre|nom|r[ée]f[ée]rence|title|name|reference|referencia|referenz)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                question,
                @"\b(?:document|fichier|source|file|pdf|dokument|datei|archivo|ficheiro)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!asksForDocumentIdentity)
            return false;

        return !Regex.IsMatch(
            question,
            @"\b(?:second|seconde|deuxi[eè]me|autre|other|another|zweite|ander|segundo|segunda)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
