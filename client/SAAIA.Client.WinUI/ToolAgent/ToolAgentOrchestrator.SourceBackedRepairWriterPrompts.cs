using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedRepairWriterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults rawToolResults,
        ToolResults writerToolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        WriterPromptBudget? promptBudget = null,
        string? candidateAdjudicationJson = null,
        string? repairFeedback = null)
    {
        var budget = promptBudget ?? CreateWriterPromptBudget(AppSettings.DefaultCtxSize, 900);
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        var structuredPlanningWriter = ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage);
        var evidenceInventoryChars = ResolveRepairEvidenceInventoryPromptChars(budget, structuredPlanningWriter);
        var sourceReferenceIndexChars = ResolveRepairSourceReferenceIndexPromptChars(
            evidenceInventoryChars,
            structuredPlanningWriter);
        var coverageNoteChars = Math.Min(budget.CoverageNoteChars, structuredPlanningWriter ? 600 : 700);
        var writingBriefChars = Math.Min(budget.WritingBriefChars, structuredPlanningWriter ? 750 : 900);
        var researchMapChars = Math.Min(budget.ResearchMapChars, structuredPlanningWriter ? 800 : 900);
        var candidateAdjudicationForPrompt = BuildCompactSourceBackedCandidateAdjudicationForRepairPrompt(
            candidateAdjudicationJson,
            writerUserMessage,
            plan.Language,
            structuredPlanningWriter);
        var repairFeedbackChars = structuredPlanningWriter ? 1200 : 1800;
        var evidenceToolResults = rawToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search")
            ? rawToolResults
            : writerToolResults;
        var toolResultsPromptBlock = BuildWriterToolResultsPromptBlock(
            writerToolResults,
            writerUserMessage,
            plan.Language,
            useCleanSourceBrief: true,
            sourceReferenceIndexChars: sourceReferenceIndexChars,
            sourceReferenceToolResults: evidenceToolResults);
        return $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 6)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language)}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

STRUCTURED_PLANNING_WRITER_CONTRACT:
{BuildStructuredPlanningWriterContractForWriter(writerUserMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{TruncateForPrompt(BuildSourceBackedCoverageHintsForWriter(writerToolResults, writerUserMessage, plan.Language), coverageNoteChars)}

PRIVATE_SOURCE_WRITING_BRIEF:
{TruncateForPrompt(BuildSourceBackedWritingBriefForWriter(writerToolResults, writerUserMessage, plan.Language), writingBriefChars)}

PRIVATE_SOURCE_RESEARCH_MAP:
{TruncateForPrompt(BuildSourceBackedResearchMapForWriter(rawToolResults, lastSourcesUsed, writerUserMessage, plan.Language), researchMapChars)}

PRIVATE_SOURCE_CANDIDATE_ADJUDICATION (json):
{candidateAdjudicationForPrompt}

PRIVATE_REPAIR_FEEDBACK:
{(string.IsNullOrWhiteSpace(repairFeedback) ? "none" : TruncateForPrompt(repairFeedback, repairFeedbackChars))}

CITATION_COPY_TASK:
Every concrete bullet or cell that uses an EVIDENCE_ITEM must end with that item's exact citation value.
For structured plans, write the short id form [E1] after every filled cell/item you use; it will be converted to the citation value before final display. Leave a cell plainly missing instead of filling it without [E#] or a visible source citation.
Use exactly one useful citation per filled structured-plan cell. Do not add a repeated generic source/page after a specific item citation.
Never describe the plan as random, illustrative, fictive or only an example of the format; if support is partial, mark unsupported cells plainly.
Valid line: ""- Monday - Slot A : item text (source: file.pdf p.12)"".
Preferred before conversion: ""- Monday - Slot A : item text [E1]"".
Invalid line: ""- Monday - Slot A : item text"".
Invalid citation-only line: ""- Monday - Slot A : [E1]"" or ""- Monday - Slot A : (source: file.pdf p.12)"". The cell needs item text before the citation.
If you cannot attach a visible citation to a concrete item, mark that place as ""a completer avec une source utile"" instead of writing an uncited item.

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{BuildSourceBackedCandidateLeadsForWriter(evidenceToolResults, writerUserMessage, plan.Language, maxItems: ResolveEvidenceInventoryItemLimitForPromptChars(evidenceInventoryChars, writerUserMessage), maxChars: evidenceInventoryChars)}

{toolResultsPromptBlock}
";
    }

    private static string BuildStructuredPlanningWriterContractForWriter(string? query, string language)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return "none";

        language = NormalizeLanguageCode(language);
        var dayLabels = DetectRequestedDayAxisLabels(query, language);
        var slotLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        var targetItemCount = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        var dayAxis = dayLabels.Count == 0 ? "requested day/section axis" : string.Join(" | ", dayLabels);
        var slotAxis = slotLabels.Count == 0 ? "requested slot/type axis" : string.Join(" | ", slotLabels);

        return $@"
This is a structured source-backed planning request.
- Mirror the requested visible axes: days/sections = {dayAxis}; slots/types = {slotAxis}; requested filled places = {targetItemCount}.
- PRIVATE_SOURCE_EVIDENCE_INVENTORY and PRIVATE_SOURCE_REFERENCE_INDEX contain EVIDENCE_ITEM rows. Use their title/evidence fields as the source-backed candidate bank.
- If an EVIDENCE_ITEM title is already a concrete item, action or option, you may use that title as the cell text, cleaned for readability, then append its id like [E7].
- For structured cells, preserve the EVIDENCE_ITEM title closely. You may only make small readability cleanup; do not replace it with a new invented label.
- Every filled concrete cell must end with exactly one valid [E#] from the current EVIDENCE_ITEM rows. Do not write a concrete cell without [E#].
- Prefer distinct useful source pages for distinct cells before repeating any file/page. If enough useful rows exist, fill every requested place.
- If fewer useful source-backed rows exist, do not pretend that a full grid is complete: keep the requested axes compact and mark unsupported cells as needing a useful source.
- A placeholder cell such as ""a completer avec une source utile"" is allowed only for unsupported places; it is not a concrete item and must not carry a citation.
- Treat candidate_adjudication diagnostics and codeDecision/codeReason as advice, not a veto. You remain responsible for judging whether the visible title/evidence/page actually supports the item.
- Never use navigation, index, profile, generic advice, OCR fragments, procedure-only verbs, isolated components or timing statements as planning cells unless the same EVIDENCE_ITEM also contains concrete item evidence.
";
    }

    private static string BuildCompactSourceBackedCandidateAdjudicationForRepairPrompt(
        string? candidateAdjudicationJson,
        string query,
        string language,
        bool structuredPlanningWriter)
    {
        if (string.IsNullOrWhiteSpace(candidateAdjudicationJson))
            return "null";

        if (!structuredPlanningWriter)
            return TruncateForPrompt(candidateAdjudicationJson, 1800);

        if (TryBuildSourceBackedCandidateAdjudicationSignal(
                candidateAdjudicationJson,
                query,
                language,
                out var signal))
        {
            return JsonSerializer.Serialize(new
            {
                decision = signal.Decision,
                usefulCandidateCount = signal.UsefulCandidateCount,
                missingCount = signal.MissingCount,
                missingSlots = signal.MissingSlots.Take(8).ToArray(),
                requestsMoreRetrieval = signal.RequestsMoreRetrieval,
                note = "Full adjudication omitted from repair prompt; use PRIVATE_SOURCE_EVIDENCE_INVENTORY as the current evidence bundle."
            });
        }

        return TruncateForPrompt(candidateAdjudicationJson, 1200);
    }

    private static int ResolveRepairSourceReferenceIndexPromptChars(
        int evidenceInventoryChars,
        bool structuredPlanningWriter)
        => Math.Min(
            evidenceInventoryChars,
            structuredPlanningWriter
                ? Math.Clamp(evidenceInventoryChars / 4, 1200, 2400)
                : 1000);

    private static string LimitEvidenceRosterForPrompt(string roster, int maxItems, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(roster))
            return string.Empty;

        maxItems = Math.Max(1, maxItems);
        maxChars = Math.Max(800, maxChars);
        var sb = new StringBuilder();
        var itemCount = 0;
        foreach (var rawLine in roster.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase))
            {
                if (itemCount >= maxItems)
                    break;
                itemCount++;
            }

            var separatorChars = sb.Length == 0 ? 0 : Environment.NewLine.Length;
            if (sb.Length + separatorChars + line.Length > maxChars)
                break;

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(line);
        }

        return sb.Length == 0
            ? TruncateForPrompt(roster, maxChars)
            : sb.ToString();
    }

    private static string ResolveSourceBackedWriterUserMessage(string? userMessage)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage ?? string.Empty);
        return string.IsNullOrWhiteSpace(intentQuery) ? userMessage ?? string.Empty : intentQuery;
    }

    private static string BuildSourceBackedWriterFollowupContextNote(string? userMessage, string language)
    {
        if (!IsBroadenedSourceSearchConfirmationEnvelope(userMessage))
            return SourceBackedLabel(
                language,
                "Aucun suivi utilisateur particulier.",
                "No special user follow-up context.",
                "Sin contexto especial de seguimiento del usuario.",
                "Sem contexto especial de seguimento do utilizador.",
                "Kein besonderer Folgekontext des Nutzers.",
                "Nessun contesto di follow-up particolare.");

        return SourceBackedLabel(
            language,
            "L'utilisateur a confirme qu'il faut elargir la recherche autour de la demande precedente. Redige la reponse finale a partir de USER_MESSAGE, pas a partir du bloc technique de confirmation. Ne repropose pas la meme recherche elargie; si les sources restent insuffisantes, explique simplement ce qui manque.",
            "The user confirmed that the search should be broadened around the previous request. Write the final answer from USER_MESSAGE, not from the technical confirmation block. Do not offer the same broadened search again; if sources are still insufficient, explain plainly what is missing.",
            "El usuario confirmo que hay que ampliar la busqueda alrededor de la solicitud anterior. Redacta la respuesta final a partir de USER_MESSAGE, no del bloque tecnico de confirmacion. No vuelvas a ofrecer la misma busqueda ampliada; si las fuentes siguen siendo insuficientes, explica claramente que falta.",
            "O utilizador confirmou que a pesquisa deve ser alargada em torno do pedido anterior. Redige a resposta final a partir de USER_MESSAGE, nao do bloco tecnico de confirmacao. Nao voltes a propor a mesma pesquisa alargada; se as fontes continuarem insuficientes, explica claramente o que falta.",
            "Der Nutzer hat bestaetigt, dass die Suche rund um die vorherige Anfrage erweitert werden soll. Schreibe die finale Antwort aus USER_MESSAGE, nicht aus dem technischen Bestaetigungsblock. Biete dieselbe erweiterte Suche nicht erneut an; wenn die Quellen weiterhin nicht ausreichen, erklÃƒÂ¤re klar, was fehlt.",
            "L'utente ha confermato che la ricerca va ampliata attorno alla richiesta precedente. Scrivi la risposta finale da USER_MESSAGE, non dal blocco tecnico di conferma. Non proporre di nuovo la stessa ricerca ampliata; se le fonti restano insufficienti, spiega chiaramente cosa manca.");
    }
}