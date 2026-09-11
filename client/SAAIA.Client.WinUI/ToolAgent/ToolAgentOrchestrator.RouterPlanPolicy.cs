using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private RouterPlan SanitizeRouterPlan(RouterPlan plan, string detectedMessageLanguage, bool disallowMetaSetLanguage)
    {
        plan ??= new RouterPlan();

        plan.Mode = NormalizePlanMode(plan.Mode);
        plan.Language = NormalizeLanguageCode(string.IsNullOrWhiteSpace(plan.Language) ? detectedMessageLanguage : plan.Language);
        plan.Intent = NormalizeRouterIntent(plan.Intent);
        plan.ResponseFormat = NormalizeResponseFormat(plan.ResponseFormat);
        plan.MemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        plan.RouterConfidence = ClampConfidence(plan.RouterConfidence);
        plan.ClarificationQuestions = plan.ClarificationQuestions
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList() ?? new List<string>();
        if (plan.Clarification is not null)
        {
            plan.Clarification.Message =
                (plan.Clarification.Message ?? string.Empty).Trim();
            plan.Clarification.Options = plan.Clarification.Options
                ?.Where(static option => !string.IsNullOrWhiteSpace(option))
                .Select(static option => option.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList() ?? new List<string>();
            plan.Clarification.ExecutionImpact =
                (plan.Clarification.ExecutionImpact ?? string.Empty).Trim();
            plan.Clarification.ResumeRoute =
                (plan.Clarification.ResumeRoute ?? string.Empty).Trim();
            plan.Clarification.AmbiguityKind =
                (plan.Clarification.AmbiguityKind ?? string.Empty).Trim();

            if (!plan.NeedClarification
                || plan.Clarification.Message.Length == 0)
            {
                plan.Clarification = null;
            }
            else
            {
                plan.ClarificationQuestions =
                    new List<string> { plan.Clarification.Message };
            }
        }
        plan.ReasoningTracePublic = plan.ReasoningTracePublic
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Take(3)
            .ToList() ?? new List<string>();
        plan.RiskFlags = plan.RiskFlags
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList() ?? new List<string>();
        plan.ToolCalls = SanitizeToolCalls(plan.ToolCalls).ToList();

        plan.ToolCalls = plan.ToolCalls
            .Where(call => !ToolManifest.IsAdminTool(call.Name))
            .Take(MaxToolCalls)
            .ToList();

        if (string.IsNullOrWhiteSpace(plan.Intent))
            plan.Intent = InferIntentFromToolCalls(plan.ToolCalls) ?? "chat.general";

        if (disallowMetaSetLanguage && string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase))
            plan.Intent = "chat.general";

        if (IsAdminOnlyIntent(plan.Intent))
        {
            plan.Intent = "meta.help";
            plan.NeedClarification = false;
            plan.ClarificationQuestions.Clear();
            plan.Clarification = null;
            plan.ToolCalls.Clear();
            plan.MemoryUpdate = null;
        }

        if (plan.NeedClarification && plan.ClarificationQuestions.Count == 0)
            plan.ClarificationQuestions = new List<string>();

        if (plan.NeedClarification)
        {
            // Waiting for user input and executing a tool are mutually exclusive
            // protocol states. This is a mechanical invariant, not a semantic
            // judgment about whether clarification was appropriate.
            plan.ToolCalls.Clear();
            plan.SourceBackedMission = null;
        }
        else
        {
            plan.Clarification = null;
        }

        return plan;
    }

    private static string NormalizeRouterIntent(string? intent)
    {
        var normalized = (intent ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized switch
        {
            "general" or "chat" or "conversation" => "chat.general",
            "set_language" or "change_language" or "meta.change_language" => "meta.set_language",
            "set_style" or "change_style" or "meta.change_style" => "meta.set_style",
            "set_mode" or "change_mode" or "meta.change_mode" => "meta.set_mode",
            "translate_last" or "translate.last" or "meta.translate_last" => "meta.translate_last_answer",
            "repair" or "rewrite_last" or "meta.rewrite_last" => "meta.rewrite_last",
            "count_documents" or "documents.count" => "inventory.count",
            "list_documents" or "documents.list" => "inventory.list",
            "documents.search" or "inventory.find" or "find_documents" => "inventory.find",
            "inventory.changed_since" or "changed_since" or "documents.changed_since" => "inventory.changed_since",
            "categories" or "documents.categories" => "inventory.categories",
            "tree" or "documents.tree" or "inventory.tree_sub" => "inventory.tree",
            "stats" or "documents.stats" => "inventory.stats",
            "inventory.health" or "catalog.health" => "inventory.health",
            "summary.status" or "summary_status" or "inventory.summary_status" or "admin.summary.missing" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" => "inventory.summary_status",
            "document.about" or "document_about" or "rag.about_doc" => "rag.summarize_doc",
            "summary.exists" or "check_summary" => "summary.exists",
            "summary.store" or "refresh_summary" => "admin.summary.generate",
            _ => normalized
        };
    }

    private static bool IsRagToolName(string toolName)
        => toolName.StartsWith("rag.", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldForceRagForStandaloneTopic(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (ShouldRespectLlmRouterGeneralWithoutTools(plan))
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;
        if (plan.Origin == RouterPlanOrigin.LocalFallback
            && LooksLikeLocalFallbackGeneralChatMessage(effectiveUserMessage))
            return false;

        return LooksLikeStandaloneDocumentaryTopic(effectiveUserMessage)
            || !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(effectiveUserMessage))
            || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
            || LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            || LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage)
            || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
            || LooksLikeDocumentaryContentRequest(effectiveUserMessage)
            || LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);
    }

    private static bool LooksLikeLocalFallbackGeneralChatMessage(string? message)
    {
        var normalized = NormalizeLexicalLookup(CollapseWhitespace(message ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length > 8)
            return false;

        return Regex.IsMatch(
                normalized,
                @"^(?:bonjour|salut|coucou|hello|hi|hey|merci|thanks?|ok|okay|d\s*accord|oui|non)(?:\s|$)",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:dis|dit|reponds|repond|ecris|ecrit|say|answer|write)\b.{0,40}\b(?:bonjour|salut|hello|hi)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^(?:qui\s+es\s+tu|who\s+are\s+you|comment\s+ca\s+va|ca\s+va|what\s+can\s+you\s+do)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool IsLowValueRouterRagQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var meaningfulTokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return meaningfulTokens.Length == 0;
    }

    private static bool IsWeakRouterRagQueryToken(string token)
        => token is
            "avec" or "sans" or "pour" or "dans" or "les" or "des" or "une" or "the" or "and" or "with"
            or "documents" or "document" or "sources" or "source" or "fichiers" or "fichier"
            or "cherche" or "recherche" or "trouve" or "trouver" or "repond" or "reponds" or "answer"
            or "respond" or "mentionne" or "mentionnes" or "mentionnent"
            or "peux" or "peut" or "faire" or "proposer" or "propose" or "partir"
            or "disponible" or "disponibles" or "utile" or "utiles" or "sais" or "quoi"
            or "pas" or "cette" or "cela" or "repa";
}
