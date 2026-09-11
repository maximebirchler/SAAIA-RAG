using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedPlanningAnswer(ToolResults toolResults, string language, int minItems = 1, string? query = null)
    {
        var draft = BuildSourceBackedPlanningDraft(toolResults, language, minItems, query);
        if (!string.IsNullOrWhiteSpace(draft.Answer))
            return draft.Answer;

        var requiresStrictStructuredCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var mayBuildPartialDraft = requiresStrictStructuredCoverage
            ? ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language)
            : LooksLikeAnyDocumentaryPlanningRequest(query);
        if (minItems <= 1 && mayBuildPartialDraft)
        {
            var partialDraft = BuildSourceBackedPlanningDraft(
                toolResults,
                language,
                minItems: 1,
                query: query,
                allowPartialStructuredPlanningDraft: true);
            if (!string.IsNullOrWhiteSpace(partialDraft.Answer))
                return partialDraft.Answer;
        }

        return string.Empty;
    }

    private static bool ShouldAllowSourceBackedWriterRepairForCurrentTurn(string? query)
        => !LooksLikeStrictCertificationOrExactProofRequest(query);

    private static SourceBackedPlanningDraft BuildSourceBackedPlanningDraft(
        ToolResults toolResults,
        string language,
        int minItems = 1,
        string? query = null,
        bool allowPartialStructuredPlanningDraft = false,
        bool allowSourcedRotationForPartialStructuredPlanning = false)
    {
        language = NormalizeLanguageCode(language);
        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var wantsWeeklyPlan = LooksLikeWeeklyPlanningRequest(query);
        var wantsVerificationChecklist = LooksLikeSourceBackedVerificationChecklistRequest(query);
        var requestedDayLabels = DetectRequestedDayAxisLabels(query, language);
        var requestedPeriodLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        var hasStructuredPlanningAxes = !wantsVerificationChecklist
            && requestedDayLabels.Count > 0
            && requestedPeriodLabels.Count > 0;
        var requiresStrictStructuredEvidence = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var requestedCandidateCount = Math.Max(targetItemCount, minItems);
        var candidatePoolSize = ResolveSourceBackedPlanningCandidatePoolSize(query, requestedCandidateCount);
        ClientLog.Info(
            $"ToolAgent planning draft build: stage=candidate_pool.start|targetItems={targetItemCount}|requestedCandidates={requestedCandidateCount}|poolSize={candidatePoolSize}|strict={requiresStrictStructuredEvidence}|structuredAxes={hasStructuredPlanningAxes}");
        var draftStopwatch = Stopwatch.StartNew();
        var candidatePool = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                candidatePoolSize,
                language,
                requireStrictStructuredEvidence: requiresStrictStructuredEvidence)
            .ToList();
        ClientLog.Info(
            $"ToolAgent planning draft build: stage=candidate_pool.end|candidates={candidatePool.Count}|ms={draftStopwatch.ElapsedMilliseconds}|topTitles={string.Join("; ", candidatePool.Take(10).Select(static candidate => candidate.Title))}");
        var itemSelectionStopwatch = Stopwatch.StartNew();
        var planItems = (requiresStrictStructuredEvidence
                ? SelectPageDiverseSourceBackedPlanningCandidates(candidatePool, targetItemCount, query)
                : RankDistinctSourceBackedPlanningLeadCandidates(candidatePool, query).Take(targetItemCount))
            .ToList();
        ClientLog.Info(
            $"ToolAgent planning draft build: stage=item_selection.end|selected={planItems.Count}|mode={(requiresStrictStructuredEvidence ? "page_diverse" : "ranked")}|ms={itemSelectionStopwatch.ElapsedMilliseconds}|titles={string.Join("; ", planItems.Take(20).Select(static candidate => candidate.Title))}");

        if (planItems.Count == 0 || (!allowPartialStructuredPlanningDraft && planItems.Count < minItems))
            return SourceBackedPlanningDraft.Empty;

        if (requiresStrictStructuredEvidence && !hasStructuredPlanningAxes)
        {
            var requiredDistinctItems = ResolveMinimumSourceBackedPlanningCandidateCount(
                query,
                targetItemCount,
                hasStructuredAxes: false);
            if (!HasEnoughSourceBackedCandidatesForStructuredPlan(planItems, requiredDistinctItems))
            {
                if (!allowPartialStructuredPlanningDraft)
                    return SourceBackedPlanningDraft.Empty;

                var partialItems = planItems
                    .Take(Math.Min(planItems.Count, Math.Clamp(requiredDistinctItems, 1, targetItemCount)))
                    .ToArray();
                var partialAnswer = BuildStructuredSourceBackedCandidateBankAnswer(partialItems, requiredDistinctItems, language, query);
                return CreateSourceBackedPlanningDraft(partialAnswer, partialItems, query);
            }
        }

        if (hasStructuredPlanningAxes)
        {
            var requiredSlots = requestedDayLabels.Count * requestedPeriodLabels.Count;
            var requiredDistinctItems = ResolveMinimumSourceBackedPlanningCandidateCount(
                query,
                requiredSlots,
                hasStructuredAxes: true);
            var routeAwareGrid = BuildStructuredSourceBackedSlotAwareGrid(
                planItems,
                requestedPeriodLabels,
                requiredSlots,
                query,
                allowSourcedRotationForPartialStructuredPlanning && ShouldAllowSourcedStructuredPlanningRotation(query),
                requireDistinctItems: !allowSourcedRotationForPartialStructuredPlanning,
                out var routeAwareFit);
            var requiresExplicitSlotEvidence = RequiresExplicitStructuredPlanningSlotEvidence(query);
            var hasRouteAwareShortage = (routeAwareFit.HasRouteEvidence && routeAwareGrid.Count < requiredSlots)
                || (requiresExplicitSlotEvidence && !routeAwareFit.HasRouteEvidence);
            if (!HasEnoughSourceBackedCandidatesForStructuredPlan(planItems, requiredDistinctItems)
                || hasRouteAwareShortage)
            {
                if (!allowPartialStructuredPlanningDraft)
                    return SourceBackedPlanningDraft.Empty;

                var unscopedPartialLimit = hasRouteAwareShortage && routeAwareGrid.Count == 0
                    ? Math.Clamp(requestedPeriodLabels.Count * 2, 4, 8)
                    : Math.Clamp(requiredSlots, 8, 24);
                var partialItems = routeAwareGrid.Count > 0
                    ? routeAwareGrid
                        .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => group.First())
                        .ToArray()
                    : planItems
                        .Take(Math.Min(planItems.Count, unscopedPartialLimit))
                        .ToArray();
                var partialAnswer = BuildStructuredSourceBackedPlanAnswer(
                    partialItems,
                    requestedDayLabels,
                    requestedPeriodLabels,
                    language,
                    query,
                    requiredDistinctItems,
                    allowSourcedRotation: allowSourcedRotationForPartialStructuredPlanning
                        && ShouldAllowSourcedStructuredPlanningRotation(query));
                return CreateSourceBackedPlanningDraft(partialAnswer, partialItems, query);
            }

            var usedItems = routeAwareGrid.Count > 0
                ? routeAwareGrid
                    .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .Take(requiredSlots)
                    .ToArray()
                : planItems.Take(requiredSlots).ToArray();
            var structuredAnswer = BuildStructuredSourceBackedPlanAnswer(
                planItems,
                requestedDayLabels,
                requestedPeriodLabels,
                language,
                query,
                requiredDistinctItems,
                allowSourcedRotation: allowSourcedRotationForPartialStructuredPlanning
                    && ShouldAllowSourcedStructuredPlanningRotation(query));
            return CreateSourceBackedPlanningDraft(structuredAnswer, usedItems, query);
        }

        var header = wantsVerificationChecklist
            ? language switch
            {
                "en" => "Here are the checks I can support with the available documents:",
                "es" => "Estas son las verificaciones que puedo apoyar con los documentos disponibles:",
                "pt" => "Estas são as verificações que posso apoiar com os documentos disponíveis:",
                "de" => "Hier sind die Prüfpunkte, die ich mit den verfügbaren Dokumenten belegen kann:",
                "it" => "Ecco i controlli che posso sostenere con i documenti disponibili:",
                _ => "Voici les vérifications que je peux appuyer avec les documents disponibles :"
            }
            : language switch
            {
                "en" => "Here is a practical proposal based on the available documents:",
                "es" => "Aquí tienes una propuesta práctica basada en los documentos disponibles:",
                "pt" => "Aqui está uma proposta prática baseada nos documentos disponíveis:",
                "de" => "Hier ist ein praktischer Vorschlag auf Basis der verfügbaren Dokumente:",
                "it" => "Ecco una proposta pratica basata sui documenti disponibili:",
                _ => "Voici une proposition pratique appuyée sur les documents disponibles :"
            };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        if (wantsWeeklyPlan && planItems.Count < targetItemCount)
        {
            var coverageNote = language switch
            {
                "en" => "The documents do not cover every requested slot yet. I keep this as a reliable starting point and leave the missing parts to complete with better sources.",
                "es" => "Los documentos aún no cubren todos los huecos solicitados. Mantengo esto como un punto de partida fiable y dejo las partes que faltan para completarlas con mejores fuentes.",
                "pt" => "Os documentos ainda não cobrem todos os espaços pedidos. Mantenho isto como um ponto de partida fiável e deixo as partes em falta para completar com melhores fontes.",
                "de" => "Die Dokumente decken noch nicht alle angefragten Plätze ab. Ich behandle dies als verlässlichen Ausgangspunkt und lasse fehlende Teile für bessere Quellen offen.",
                "it" => "I documenti non coprono ancora tutti gli spazi richiesti. Mantengo questa proposta come punto di partenza affidabile e lascio le parti mancanti da completare con fonti migliori.",
                _ => "Les documents ne couvrent pas encore tous les créneaux demandés. Je garde donc cette proposition comme point de départ fiable, à compléter avec de meilleures sources."
            };
            sb.AppendLine(coverageNote);
        }

        var useDayLabels = wantsWeeklyPlan && planItems.Count >= targetItemCount;
        for (var i = 0; i < planItems.Count; i++)
        {
            var label = wantsVerificationChecklist
                ? language switch
                {
                    "en" => $"Check {i + 1}",
                    "es" => $"Verificación {i + 1}",
                    "pt" => $"Verificação {i + 1}",
                    "de" => $"Prüfpunkt {i + 1}",
                    "it" => $"Controllo {i + 1}",
                    _ => $"Vérification {i + 1}"
                }
                : useDayLabels
                ? language switch
                {
                    "en" => $"Day {i + 1}",
                    "es" => $"Dia {i + 1}",
                    "pt" => $"Dia {i + 1}",
                    "de" => $"Tag {i + 1}",
                    "it" => $"Giorno {i + 1}",
                    _ => $"Jour {i + 1}"
                }
                : language switch
                {
                    "es" => $"Opción {i + 1}",
                    "pt" => $"Opção {i + 1}",
                    "it" => $"Opzione {i + 1}",
                    _ => $"Option {i + 1}"
                };

            var hit = planItems[i].Hit;
            sb.Append("- ");
            sb.Append(label);
            sb.Append(" : ");
            sb.Append(FormatSourceBackedCandidateDisplayTitle(planItems[i]));
            sb.Append(' ');
            sb.Append(FormatSourceBackedCandidateOpenToken(planItems[i], language));
            sb.AppendLine();
        }

        var note = wantsVerificationChecklist
            ? language switch
            {
                "en" => "Note: treat this as a source-backed checklist, then validate exceptions, approvals and legal/compliance impact with the responsible humans before acting.",
                "es" => "Nota: tratalo como una lista de control con fuente y valida excepciones, aprobaciones e impacto legal/conformidad con las personas responsables antes de actuar.",
                "pt" => "Nota: trata isto como uma checklist com fonte e valida exceções, aprovações e impacto legal/conformidade com as pessoas responsáveis antes de agir.",
                "de" => "Hinweis: Nutze dies als quellenbasierte Checkliste und prüfe Ausnahmen, Freigaben sowie rechtliche/Compliance-Auswirkungen mit den Verantwortlichen vor Umsetzung.",
                "it" => "Nota: trattala come checklist con fonte e valida eccezioni, approvazioni e impatti legali/compliance con i responsabili prima di agire.",
                _ => "Note : traite ceci comme une checklist sourcée, puis valide les exceptions, approbations et impacts juridiques/conformité avec les responsables avant d'agir."
            }
            : language switch
            {
                "en" => "Note: adapt quantities, timing, and constraints from the source pages before acting.",
                "es" => "Nota: adapta cantidades, tiempos y restricciones a partir de las paginas fuente antes de actuar.",
                "pt" => "Nota: adapta quantidades, tempos e restrições a partir das páginas fonte antes de agir.",
                "de" => "Hinweis: Mengen, Zeiten und Einschränkungen vor der Umsetzung anhand der Quellseiten anpassen.",
                "it" => "Nota: adatta quantità, tempi e vincoli dalle pagine fonte prima di agire.",
                _ => "Note : adapte les quantités, délais et contraintes à partir des pages source avant d'agir."
            };
        sb.AppendLine(note);

        var answer = sb.ToString().TrimEnd();
        var finalAnswer = wantsWeeklyPlan && planItems.Count < targetItemCount
            ? AppendBroadenedSearchOfferIfHelpful(answer, query, language)
            : answer;
        return CreateSourceBackedPlanningDraft(finalAnswer, planItems, query);
    }

    private static SourceBackedPlanningDraft CreateSourceBackedPlanningDraft(
        string? answer,
        IReadOnlyList<SourceBackedOptionCandidate> usedItems,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(answer) || usedItems.Count == 0)
            return SourceBackedPlanningDraft.Empty;

        var sources = BuildPlanningSourcesFromCandidates(usedItems, query);
        return sources.Count == 0
            ? SourceBackedPlanningDraft.Empty
            : new SourceBackedPlanningDraft(answer.TrimEnd(), usedItems.ToArray(), sources);
    }

    private static IReadOnlyList<ToolMemory.SourceRef> BuildPlanningSourcesFromCandidates(
        IReadOnlyList<SourceBackedOptionCandidate> usedItems,
        string? query)
    {
        if (usedItems.Count == 0)
            return Array.Empty<ToolMemory.SourceRef>();

        var sourceLimit = Math.Clamp(
            Math.Max(Math.Max(8, usedItems.Count), ResolveSourceBackedPlanningTargetItemCount(query)),
            8,
            24);
        var sources = usedItems
            .Select(static candidate => candidate.Hit)
            .Where(static hit => !string.IsNullOrWhiteSpace(BuildRagHitVisiblePageMergeKey(hit)))
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(sourceLimit)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return MergeSourceRefsByPagePreservingOrder(sources).Take(sourceLimit).ToArray();
    }

    private static string BuildStructuredSourceBackedPlanAnswer(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        IReadOnlyList<string> dayLabels,
        IReadOnlyList<string> periodLabels,
        string language,
        string? query,
        int requiredDistinctItems,
        bool allowSourcedRotation = false)
    {
        if (planItems.Count == 0 || dayLabels.Count == 0 || periodLabels.Count == 0)
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var requiredSlots = dayLabels.Count * periodLabels.Count;
        requiredDistinctItems = Math.Clamp(requiredDistinctItems, 1, requiredSlots);
        var hasEnoughDistinctItems = planItems.Count >= requiredDistinctItems;
        var canBuildDistinctGrid = hasEnoughDistinctItems && planItems.Count >= requiredSlots;
        var canUseSourcedRotation = allowSourcedRotation && ShouldAllowSourcedStructuredPlanningRotation(query);
        var routeAwareGrid = BuildStructuredSourceBackedSlotAwareGrid(
            planItems,
            periodLabels,
            requiredSlots,
            query,
            canUseSourcedRotation,
            requireDistinctItems: canBuildDistinctGrid && !canUseSourcedRotation,
            out var routeAwareFit);
        var requiresExplicitSlotEvidence = RequiresExplicitStructuredPlanningSlotEvidence(query);
        var gridItems = routeAwareGrid.Count > 0
            ? routeAwareGrid
            : routeAwareFit.HasRouteEvidence || requiresExplicitSlotEvidence
                ? Array.Empty<SourceBackedOptionCandidate>()
                : canBuildDistinctGrid
                    ? planItems.Take(requiredSlots).ToArray()
                    : canUseSourcedRotation
                        ? BuildStructuredSourceBackedRotatingGrid(planItems, periodLabels, requiredSlots, query)
                        : Array.Empty<SourceBackedOptionCandidate>();
        var usesSourcedRotationGrid = routeAwareGrid.Count > 0
            && routeAwareFit.HasRouteEvidence
            && canUseSourcedRotation;
        var labels = language switch
        {
            "en" => (
                Header: "Here is a structured proposal based on the retrieved sources.",
                Partial: $"The retrieved sources cover {planItems.Count} of {requiredSlots} requested place(s). I keep only supported items instead of filling the missing places by guesswork.",
                Complete: "The organization below is proposed by the assistant; each concrete item remains tied to a cited source.",
                Rotation: $"I found {planItems.Count} distinct sourced option(s) for {requiredSlots} requested slot(s), so I rotate the documented options instead of inventing missing ones.",
                Verify: "Before using it as a final plan, check the cited pages for quantities, timing, constraints and substitutions."),
            "es" => (
                Header: "Aquí tienes una propuesta estructurada basada en las fuentes recuperadas.",
                Partial: $"Las fuentes recuperadas cubren {planItems.Count} de {requiredSlots} lugar(es) pedido(s). Mantengo solo los elementos respaldados, sin rellenar las partes faltantes por suposición.",
                Complete: "La organización siguiente es una propuesta del asistente; cada elemento concreto sigue ligado a una fuente citada.",
                Rotation: $"He encontrado {planItems.Count} opción/opciones distintas con fuente para {requiredSlots} huecos pedidos, así que las hago rotar en lugar de inventar las que faltan.",
                Verify: "Antes de usarlo como plan final, revisa las páginas citadas para cantidades, horarios, restricciones y sustituciones."),
            "pt" => (
                Header: "Aqui está uma proposta estruturada baseada nas fontes recuperadas.",
                Partial: $"As fontes recuperadas cobrem {planItems.Count} de {requiredSlots} lugar(es) pedido(s). Mantenho apenas os itens sustentados, sem preencher as partes em falta por suposição.",
                Complete: "A organização abaixo é uma proposta do assistente; cada item concreto continua ligado a uma fonte citada.",
                Rotation: $"Encontrei {planItems.Count} opção/opções distintas com fonte para {requiredSlots} espaços pedidos, por isso faço uma rotação das opções documentadas sem inventar as restantes.",
                Verify: "Antes de usar isto como plano final, verifica as páginas citadas para quantidades, horários, restrições e substituições."),
            "de" => (
                Header: "Hier ist ein strukturierter Vorschlag auf Basis der gefundenen Quellen.",
                Partial: $"Die gefundenen Quellen decken {planItems.Count} von {requiredSlots} gewünschten Stelle(n) ab. Ich nutze nur belegte Punkte, statt fehlende Stellen zu erraten.",
                Complete: "Die folgende Organisation ist ein Vorschlag des Assistenten; jeder konkrete Punkt bleibt mit einer Quelle verbunden.",
                Rotation: $"Ich habe {planItems.Count} unterschiedliche belegte Option(en) für {requiredSlots} gewünschte Plätze gefunden und rotiere sie, statt fehlende Punkte zu erfinden.",
                Verify: "Prüfe vor der finalen Nutzung die zitierten Seiten zu Mengen, Zeiten, Einschränkungen und Alternativen."),
            "it" => (
                Header: "Ecco una proposta strutturata basata sulle fonti recuperate.",
                Partial: $"Le fonti recuperate coprono {planItems.Count} di {requiredSlots} punto/i richiesto/i. Mantengo solo gli elementi supportati, senza riempire le parti mancanti per supposizione.",
                Complete: "L'organizzazione seguente è una proposta dell'assistente; ogni elemento concreto resta collegato a una fonte citata.",
                Rotation: $"Ho trovato {planItems.Count} opzione/i distinte con fonte per {requiredSlots} spazi richiesti, quindi le alterno senza inventare quelle mancanti.",
                Verify: "Prima di usarlo come piano finale, controlla le pagine citate per quantità, tempi, vincoli e sostituzioni."),
            _ => (
                Header: "Voici une proposition structurée à partir des sources récupérées.",
                Partial: $"Les sources récupérées couvrent {planItems.Count} case(s) sur {requiredSlots}. Je garde uniquement les éléments appuyés par les documents, sans remplir les parties manquantes au hasard.",
                Complete: "L'organisation ci-dessous est proposée par l'assistant ; chaque élément concret reste relié à une source citée.",
                Rotation: $"J'ai trouvé {planItems.Count} option(s) distincte(s) sourcée(s) pour {requiredSlots} créneaux demandés ; je les fais donc tourner sans inventer les éléments manquants.",
                Verify: "Avant d'en faire un planning définitif, vérifie les pages citées pour les quantités, horaires, contraintes et remplacements.")
        };

        if (gridItems.Count < requiredSlots)
        {
            return BuildStructuredSourceBackedCandidateBankAnswer(planItems, requiredSlots, language, query);
        }

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(canBuildDistinctGrid && !usesSourcedRotationGrid ? labels.Complete : labels.Rotation);

        var slotIndex = 0;
        foreach (var day in dayLabels)
        {
            sb.AppendLine();
            sb.AppendLine($"{day} :");
            foreach (var period in periodLabels)
            {
                var candidate = gridItems[slotIndex];
                sb.Append("  - ");
                sb.Append(FormatStructuredPlanningAxisDisplayLabel(period));
                sb.Append(" : ");
                sb.Append(FormatSourceBackedCandidateDisplayTitle(candidate));
                sb.Append(' ');
                sb.Append(FormatSourceBackedCandidateOpenToken(candidate, language));
                sb.AppendLine();
                slotIndex++;
            }
        }

        sb.AppendLine();
        sb.Append(labels.Verify);
        return sb.ToString().TrimEnd();
    }

}
