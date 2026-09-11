using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private sealed record PendingClarificationPreparation(string EffectiveUserMessage, DocumentRefResolver.AnalysisResult? AnalysisOverride, bool Consumed);

    private PendingClarificationPreparation PrepareUserMessageForPendingClarification(
        IReadOnlyList<(string role, string content)> chatHistory,
        string? userMessage)
    {
        var safeUserMessage = userMessage ?? string.Empty;
        var pending = _mem.PendingClarification;
        if ((pending is null || string.IsNullOrWhiteSpace(pending.OriginalUserMessage))
            && TryInferPendingClarificationFromHistory(chatHistory, out var inferredPending))
        {
            pending = inferredPending;
        }

        if (pending is null || string.IsNullOrWhiteSpace(pending.OriginalUserMessage))
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        var current = safeUserMessage.Trim();
        if (current.Length == 0)
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        if (DocumentRefResolver.IsRepairMessage(current) || TryDetectExplicitLanguageSwitch(current, out _))
        {
            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var expectsDocumentAnswer = string.Equals(pending.Kind, "doc_reference", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pending.Kind, "document_reference", StringComparison.OrdinalIgnoreCase);
        var expectsTreeScope = string.Equals(pending.Kind, "tree_scope", StringComparison.OrdinalIgnoreCase);
        var expectsRagProbeRefinement = string.Equals(pending.Kind, "rag_probe", StringComparison.OrdinalIgnoreCase);
        var expectsBroadenedSourceSearch = string.Equals(pending.Kind, "source_backed_broaden_search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pending.Kind, "broader_source_search", StringComparison.OrdinalIgnoreCase);
        var expectsBackendRagGuidance = string.Equals(pending.Kind, "rag_guidance", StringComparison.OrdinalIgnoreCase);
        var expectsRouterClarification = string.Equals(
            pending.Kind,
            "llm_router",
            StringComparison.OrdinalIgnoreCase);
        var expectsGenericClarification = IsGenericClarificationKind(pending.Kind);
        var isBroadenedSourceSearchConfirmation = LooksLikeBroadenedSourceSearchConfirmation(current);
        var shouldConfirmBroadenedSourceSearch =
            isBroadenedSourceSearchConfirmation
            && (expectsBroadenedSourceSearch || expectsRagProbeRefinement || expectsBackendRagGuidance);

        var isExpectedAnswer = expectsDocumentAnswer
            ? DocumentRefResolver.LooksLikeDocumentReferenceAnswer(current, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef)
            : expectsTreeScope
                ? DocumentRefResolver.LooksLikeTreeScopeAnswer(current)
                : shouldConfirmBroadenedSourceSearch
                    ? true
                    : expectsRagProbeRefinement
                    ? current.Length >= 2
                    : expectsBroadenedSourceSearch
                        ? isBroadenedSourceSearchConfirmation
                    : expectsBackendRagGuidance
                            ? LooksLikeGenericClarificationAnswer(current)
                            : (expectsRouterClarification || expectsGenericClarification)
                              && LooksLikeGenericClarificationAnswer(current);

        if (!isExpectedAnswer)
        {
            if (LooksLikeClarificationMetaAck(current))
                return new PendingClarificationPreparation(safeUserMessage, null, false);

            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var effectiveUserMessage = shouldConfirmBroadenedSourceSearch
            ? $@"PREVIOUS_USER_REQUEST:
{pending.OriginalUserMessage}

USER_CONFIRMED_BROADER_SOURCE_SEARCH:
{current}

RESOLVED_REQUEST:
Continue the previous source-backed request by running a broader retrieval exploration across the relevant indexed corpus or category before answering. Use catalog/category hints, document profiles, content cards, title anchors and navigation/table-of-contents entries when useful. Keep the final answer grounded in concrete source hits and do not treat this confirmation as a new topic."
            : expectsRagProbeRefinement
            ? !string.IsNullOrWhiteSpace(pending.Question)
                ? BuildRouterClarificationResolutionContext(pending, current)
                : $@"PREVIOUS_DOCUMENTARY_REQUEST:
{pending.OriginalUserMessage}

RETRIEVAL_REFINEMENT:
{current}"
            : expectsBroadenedSourceSearch
                ? $@"PREVIOUS_USER_REQUEST:
{pending.OriginalUserMessage}

USER_CONFIRMED_BROADER_SOURCE_SEARCH:
{current}

RESOLVED_REQUEST:
Continue the previous source-backed request by running a broader retrieval exploration across the relevant indexed corpus or category before answering. Use catalog/category hints, document profiles, title anchors and navigation/table-of-contents entries when useful. Keep the final answer grounded in sources and do not treat this confirmation as a new topic."
            : expectsGenericClarification
                ? $@"PREVIOUS_USER_REQUEST:
{pending.OriginalUserMessage}

USER_CLARIFICATION:
{current}

RESOLVED_REQUEST:
Continue the previous request using the clarification as the intended topic or scope."
            : expectsRouterClarification
                ? BuildRouterClarificationResolutionContext(pending, current)
            : $@"PREVIOUS_AMBIGUOUS_REQUEST:
{pending.OriginalUserMessage}

CLARIFICATION_ANSWER:
{current}";

        var analysisOverride = expectsRagProbeRefinement || expectsGenericClarification || expectsRouterClarification || expectsBroadenedSourceSearch || expectsBackendRagGuidance
            ? null
            : DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        ClearPendingClarification();
        return new PendingClarificationPreparation(effectiveUserMessage, analysisOverride, true);
    }

    private static bool IsGenericClarificationKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return true;

        return string.Equals(kind, "generic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "clarification", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic_scope", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic_refinement", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeGenericClarificationAnswer(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length < 2)
            return false;

        return s.Length <= 800;
    }

    private static string BuildRouterClarificationResolutionContext(
        ToolMemory.PendingClarificationState pending,
        string currentUserTurn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PREVIOUS_USER_REQUEST:");
        sb.AppendLine(pending.OriginalUserMessage);
        if (!string.IsNullOrWhiteSpace(pending.Question))
        {
            sb.AppendLine();
            sb.AppendLine("CLARIFICATION_ASKED:");
            sb.AppendLine(pending.Question);
        }
        if (pending.Options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("OPTIONS_OFFERED:");
            foreach (var option in pending.Options.Take(4))
                sb.Append("- ").AppendLine(option);
        }
        if (!string.IsNullOrWhiteSpace(pending.ExecutionImpact))
        {
            sb.AppendLine();
            sb.AppendLine("EXECUTION_IMPACT:");
            sb.AppendLine(pending.ExecutionImpact);
        }
        if (!string.IsNullOrWhiteSpace(pending.ResumeRoute))
        {
            sb.AppendLine();
            sb.AppendLine("EXPECTED_RESUME_ROUTE:");
            sb.AppendLine(pending.ResumeRoute);
        }
        sb.AppendLine();
        sb.AppendLine("CURRENT_USER_TURN:");
        sb.AppendLine(currentUserTurn);
        sb.AppendLine();
        sb.AppendLine("ROUTING_INSTRUCTION:");
        sb.AppendLine(
            "Decide whether CURRENT_USER_TURN answers the clarification or starts a new request. "
            + "If it answers, route the resolved previous request. If it starts a new request, route only the new request.");
        return sb.ToString().Trim();
    }

    private static bool TryInferPendingClarificationFromHistory(
        IReadOnlyList<(string role, string content)> chatHistory,
        out ToolMemory.PendingClarificationState? pending)
    {
        pending = null;
        if (chatHistory is null || chatHistory.Count < 2)
            return false;

        for (var assistantIndex = chatHistory.Count - 1; assistantIndex >= 1; assistantIndex--)
        {
            var assistantTurn = chatHistory[assistantIndex];
            if (!string.Equals(assistantTurn.role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(assistantTurn.content))
            {
                continue;
            }

            if (LooksLikeAssistantBroadenedSearchOffer(assistantTurn.content))
            {
                for (var userIndex = assistantIndex - 1; userIndex >= 0; userIndex--)
                {
                    var userTurn = chatHistory[userIndex];
                    if (!string.Equals(userTurn.role, "user", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(userTurn.content))
                    {
                        continue;
                    }

                    pending = new ToolMemory.PendingClarificationState
                    {
                        Kind = "source_backed_broaden_search",
                        OriginalUserMessage = userTurn.content.Trim(),
                        Hint = "recovered_from_broadened_search_offer",
                        Language = DetectMessageLanguage(assistantTurn.content),
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    };
                    return true;
                }

                return false;
            }

            if (!LooksLikeAssistantClarificationQuestion(assistantTurn.content))
                return false;

            for (var userIndex = assistantIndex - 1; userIndex >= 0; userIndex--)
            {
                var userTurn = chatHistory[userIndex];
                if (!string.Equals(userTurn.role, "user", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(userTurn.content))
                {
                    continue;
                }

                pending = new ToolMemory.PendingClarificationState
                {
                    Kind = InferClarificationKindFromQuestion(assistantTurn.content),
                    OriginalUserMessage = userTurn.content.Trim(),
                    Hint = "recovered_from_chat_history",
                    Language = DetectMessageLanguage(assistantTurn.content),
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool LooksLikeAssistantClarificationQuestion(string? assistantMessage)
    {
        var s = (assistantMessage ?? string.Empty).Trim();
        if (s.Length == 0 || !s.Contains('?', StringComparison.Ordinal))
            return false;

        return Regex.IsMatch(
            s,
            @"(?i)(clarif|precis|pr[ÃƒÂ©e]cis|quel\s+sujet|quelle\s+sujet|which\s+topic|what\s+topic|quel\s+document|which\s+document|scope|perimetre|p[ÃƒÂ©e]rim[ÃƒÂ¨e]tre|category|cat[ÃƒÂ©e]gorie)");
    }

    private static string InferClarificationKindFromQuestion(string? assistantMessage)
    {
        var s = assistantMessage ?? string.Empty;
        if (Regex.IsMatch(s, @"(?i)(document|doc\b|pdf|file|fichier)"))
            return "doc_reference";
        if (Regex.IsMatch(s, @"(?i)(scope|perimetre|p[ÃƒÂ©e]rim[ÃƒÂ¨e]tre|category|cat[ÃƒÂ©e]gorie|dossier|folder|arborescence)"))
            return "tree_scope";

        return "generic";
    }

    private void RememberPendingClarification(
        string kind,
        string originalUserMessage,
        string? hint,
        string? language,
        RouterPlan.ClarificationDecisionPlan? decision = null)
    {
        _mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "generic" : kind.Trim(),
            OriginalUserMessage = originalUserMessage ?? string.Empty,
            Hint = hint,
            Language = language,
            Question = decision?.Message,
            Options = decision?.Options
                ?.Where(static option => !string.IsNullOrWhiteSpace(option))
                .Select(static option => option.Trim())
                .Take(4)
                .ToList() ?? new List<string>(),
            ExecutionImpact = decision?.ExecutionImpact,
            ResumeRoute = decision?.ResumeRoute,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string RenderRouterClarification(RouterPlan plan)
    {
        var decision = plan.Clarification;
        if (decision is not null
            && !string.IsNullOrWhiteSpace(decision.Message))
        {
            var sb = new StringBuilder(decision.Message.Trim());
            foreach (var option in decision.Options
                         .Where(static option => !string.IsNullOrWhiteSpace(option))
                         .Select(static option => option.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(4))
            {
                sb.AppendLine();
                sb.Append("- ").Append(option);
            }
            return sb.ToString().Trim();
        }

        return string.Join(
            Environment.NewLine,
            plan.ClarificationQuestions
                .Where(static question => !string.IsNullOrWhiteSpace(question))
                .Select(static question => "- " + question.Trim())
                .Take(2));
    }

    private void ClearPendingClarification()
        => _mem.PendingClarification = null;

    private static bool LooksLikeClarificationMetaAck(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"^(?:yes|yeah|yep|oui|ok|okay|d['\u2019]accord|exactly|exact|precisely|exactement|pr[ÃƒÂ©e]cis[eÃƒÂ©]ment|correct|c['\u2019]est\s+ÃƒÂ§a|that['\u2019]?s\s+right)$", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeBroadenedSourceSearchConfirmation(string? userMessage)
    {
        var s = CollapseWhitespace(userMessage ?? string.Empty);
        if (s.Length == 0 || s.Length > 160)
            return false;

        var normalized = NormalizeLexicalLookup(s);
        return Regex.IsMatch(
            normalized,
            @"^(?:yes|yeah|yep|ok|okay|go|do it|continue|broaden(?: the search)?|search broader|run(?: the)? broader search|oui|vas[- ]?y|lance(?: la)?(?: recherche)?|d accord|recherche plus large|elargis(?: la)?(?: recherche)?|cherche plus large|si|adelante|continua|sigue|haz(?:lo)?|busqueda mas amplia|amplia(?: la)? busqueda|busca mas amplio|sim|podes|pode|vai|continua|pesquisa mais ampla|amplia(?: a)? pesquisa|procura mais ampla|ja|weiter|mach weiter|breitere suche|suche breiter|erweitere(?: die)? suche|si|vai|continua|ricerca piu ampia|allarga(?: la)? ricerca|cerca piu ampio)(?:\b.*)?$",
            RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeAssistantBroadenedSearchOffer(string? assistantMessage)
        => ContainsBroadenedSourceSearchOffer(assistantMessage);

    private static bool ContainsBroadenedSourceSearchOffer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer ?? string.Empty);
        if (normalized.Length == 0)
            return false;

        foreach (var language in new[] { "fr", "en", "es", "pt", "de", "it" })
        {
            var offer = NormalizeLexicalLookup(DeterministicAgentText.SourceBackedExpandedSearchOffer(language));
            if (offer.Length > 0 && normalized.Contains(offer, StringComparison.Ordinal))
                return true;
        }

        return normalized.Contains("recherche plus large", StringComparison.Ordinal)
            || normalized.Contains("lancer une recherche plus large", StringComparison.Ordinal)
            || normalized.Contains("elargir la recherche", StringComparison.Ordinal)
            || normalized.Contains("broader corpus search", StringComparison.Ordinal)
            || normalized.Contains("broader search", StringComparison.Ordinal)
            || normalized.Contains("run a broader search", StringComparison.Ordinal)
            || normalized.Contains("busqueda mas amplia", StringComparison.Ordinal)
            || normalized.Contains("busqueda amplia", StringComparison.Ordinal)
            || normalized.Contains("pesquisa mais ampla", StringComparison.Ordinal)
            || normalized.Contains("pesquisa ampla", StringComparison.Ordinal)
            || normalized.Contains("breitere suche", StringComparison.Ordinal)
            || normalized.Contains("suche erweitern", StringComparison.Ordinal)
            || normalized.Contains("ricerca piu ampia", StringComparison.Ordinal);
    }

}
