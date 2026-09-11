using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildReadableSourceBackedCandidateListFallbackAnswer(ToolResults toolResults, string query, string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var shouldListCandidates = LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query);
        if (!shouldListCandidates)
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var structuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var maxItems = structuredPlanning
            ? Math.Clamp(ResolveSourceBackedPlanningTargetItemCount(query), 8, 24)
            : LooksLikeAnyDocumentaryPlanningRequest(query) ? 10 : 8;
        var candidates = structuredPlanning
            ? SelectSourceBackedPlanningCandidates(toolResults, query, maxItems, language)
                .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                    .ThenByDescending(static candidate => candidate.Hit.Score)
                    .First())
                .Take(maxItems)
                .ToList()
            : SelectSourceBackedOptionCandidates(
                    toolResults,
                    query,
                    keepOverRequestedDuration: true,
                    language: language)
                .Where(static candidate => !LooksLikePageReferenceOnlyHit(candidate.Hit))
                .Where(static candidate => !LooksLikeLowSignalContentCandidateHit(candidate.Hit))
                .Select(candidate => NormalizeReadableSourceBackedCandidate(candidate, query, language))
                .OfType<SourceBackedOptionCandidate>()
                .GroupBy(BuildSourceBackedOptionCandidateKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                    .ThenByDescending(static candidate => candidate.Hit.Score)
                    .First())
                .Take(maxItems)
                .ToList();
        if (candidates.Count == 0)
            return string.Empty;

        var minimumUsefulCandidateCount = structuredPlanning
            ? Math.Min(maxItems, 2)
            : LooksLikeAnyDocumentaryPlanningRequest(query)
            ? 3
            : LooksLikeGenericCollectionOrListRequest(query)
                || LooksLikeMultipleCandidateSynthesisRequest(query)
                || LooksLikeBroadSourceBackedCompositionRequest(query)
                    ? 2
                    : 1;
        if (candidates.Count < minimumUsefulCandidateCount)
            return string.Empty;

        var labels = language switch
        {
            "en" => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "I can already suggest a few usable options, but I need more varied pages before turning them into a full plan."
                    : "Here is a first selection I can justify from the documents.",
                Intro: "These items are worth checking first:",
                Shortage: "To complete the answer properly, I would still need more usable pages so the same options are not repeated."),
            "es" => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "Ya puedo sugerir algunas opciones utilizables, pero necesito pÃ¡ginas mÃ¡s variadas antes de convertirlas en un plan completo."
                    : "AquÃ­ tienes una primera selecciÃ³n que puedo justificar con los documentos.",
                Intro: "Conviene revisar primero estos elementos:",
                Shortage: "Para completar bien la respuesta, todavÃ­a necesitarÃ­a mÃ¡s pÃ¡ginas Ãºtiles para no repetir las mismas opciones."),
            "pt" => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "JÃ¡ consigo sugerir algumas opÃ§Ãµes utilizÃ¡veis, mas preciso de pÃ¡ginas mais variadas antes de as transformar num plano completo."
                    : "Aqui estÃ¡ uma primeira seleÃ§Ã£o que consigo justificar com os documentos.",
                Intro: "Vale a pena verificar primeiro estes itens:",
                Shortage: "Para completar bem a resposta, ainda preciso de mais pÃ¡ginas Ãºteis para nÃ£o repetir as mesmas opÃ§Ãµes."),
            "de" => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "Ich kann bereits einige nutzbare Optionen vorschlagen, brauche aber vielfÃ¤ltigere Seiten, bevor daraus ein vollstÃ¤ndiger Plan wird."
                    : "Hier ist eine erste Auswahl, die ich mit den Dokumenten belegen kann.",
                Intro: "Diese Punkte solltest du zuerst prÃ¼fen:",
                Shortage: "FÃ¼r eine gute vollstÃ¤ndige Antwort brauche ich noch mehr nutzbare Seiten, damit sich die Optionen nicht wiederholen."),
            "it" => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "Posso giÃ  suggerire alcune opzioni utilizzabili, ma servono pagine piÃ¹ varie prima di trasformarle in un piano completo."
                    : "Ecco una prima selezione che posso giustificare con i documenti.",
                Intro: "Controllerei prima questi elementi:",
                Shortage: "Per completare bene la risposta servono altre pagine utili, cosÃ¬ le stesse opzioni non vengono ripetute."),
            _ => (
                Header: LooksLikeAnyDocumentaryPlanningRequest(query)
                    ? "Je peux dÃ©jÃ  proposer quelques options utilisables, mais il me faut des pages plus variÃ©es avant d'en faire un plan complet."
                    : "Voici une premiÃ¨re sÃ©lection que je peux justifier avec les documents.",
                Intro: "Je commencerais par vÃ©rifier ces Ã©lÃ©ments :",
                Shortage: "Pour complÃ©ter correctement la rÃ©ponse, il me faut encore d'autres pages utiles afin d'Ã©viter de rÃ©pÃ©ter les mÃªmes options.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Intro);
        foreach (var candidate in candidates)
        {
            var hit = candidate.Hit;
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(candidate.Title);
            sb.Append(" (");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.AppendLine(").");
        }

        if (candidates.Count < (LooksLikeAnyDocumentaryPlanningRequest(query) ? 6 : 4))
        {
            sb.Append(labels.Shortage);
            return AppendBroadenedSearchOfferIfHelpful(sb.ToString(), query, language);
        }

        return sb.ToString().TrimEnd();
    }

    private static SourceBackedOptionCandidate? NormalizeReadableSourceBackedCandidate(
        SourceBackedOptionCandidate candidate,
        string query,
        string language)
    {
        var title = ResolveReadableSourceBackedCandidateTitle(candidate.Hit, candidate.Title, query, language);
        return IsUsableSourceBackedOptionTitle(title)
            ? candidate with { Title = title }
            : null;
    }

    private static string ResolveReadableSourceBackedCandidateTitle(
        RagHitSummary hit,
        string? currentTitle,
        string query,
        string language)
    {
        var candidates = new List<string>();
        void AddCandidate(string? value)
        {
            var cleaned = CleanSourceBackedOptionTitle(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
                candidates.Add(cleaned);
        }

        AddCandidate(currentTitle);
        foreach (var title in ExtractSourceBackedTitleCandidates(hit))
            AddCandidate(title);
        AddCandidate(ExtractReadablePartialPlanningLeadTitle(hit, query));
        AddCandidate(ExtractEvidenceLeadTitle(hit));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsUsableSourceBackedOptionTitle)
            .OrderByDescending(title => ComputeReadableCandidateTitleQualityScore(title, hit, query))
            .ThenBy(title => title.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ExtractEvidenceLeadTitle(RagHitSummary hit)
    {
        var evidence = CollapseWhitespace(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return string.Empty;

        var match = Regex.Match(
            evidence,
            @"^(?<title>[\p{L}\p{N}][\p{L}\p{N} '&/\-,]{3,88})\s+(?:[:\-â€“]|(?:PREPARATION|PR[Ã‰E]PARATION|ETAPES?|[Ã‰E]TAPES?|STEPS?|METHOD|M[Ã‰E]THODE|PROCEDURE|PROC[Ã‰E]DURE|MATERIALS?|MATERIEL)\b)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["title"].Value : string.Empty;
    }

    private static int ComputeReadableCandidateTitleQualityScore(string title, RagHitSummary hit, string query)
    {
        var score = 0;
        var normalized = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return score;

        if (hit.MatchedContentCards is { Count: > 0 }
            && hit.MatchedContentCards.Any(card => string.Equals(
                NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title)),
                normalized,
                StringComparison.Ordinal)))
        {
            score += 8;
        }

        if (!LooksLikeWeakSourceBackedOptionTitle(title))
            score += 5;
        var titleSignalTermCount = ExtractQuerySignalTerms(normalized).Take(7).Count();
        if (titleSignalTermCount is >= 2 and <= 6)
            score += 3;
        if (ComputeRagHitLexicalRelevance(query, $"{title} {GetRagHitLookupText(hit)}") > 0)
            score += 2;
        if (CountNumericFactMarkers(NormalizeStructuredScanText(title)) > 0)
            score -= 6;
        if (LooksLikeProcedureSentenceTitle(normalized))
            score -= 8;

        return score;
    }

    private static string BuildReadablePartialPlanningEvidenceAnswer(IReadOnlyList<RagHitSummary> hits, string query, string language)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var leads = hits
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(hit => BuildReadablePartialPlanningEvidenceLead(hit, query, language))
            .Where(static lead => !string.IsNullOrWhiteSpace(lead.Title))
            .Where(lead => !strictStructuredPlanning || lead.IsConcreteOption)
            .Take(6)
            .ToList();
        if (leads.Count == 0)
            return string.Empty;

        if (!leads.Any(static lead => lead.IsConcreteOption))
        {
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query,
                query,
                nearbyHitCount: leads.Count);
        }

        var structuredAnswer = BuildStructuredReadablePartialPlanningEvidenceAnswer(leads, query, language);
        if (!string.IsNullOrWhiteSpace(structuredAnswer))
            return structuredAnswer;

        var labels = language switch
        {
            "en" => (
                Header: "Here is a practical draft based on the pages I can cite.",
                Intro: "I would start with:",
                Missing: "Some parts still need another cited page before I can fill them confidently.",
                Verify: "Check the cited pages before using exact quantities, timing or constraints."),
            "es" => (
                Header: "AquÃ­ tienes un borrador prÃ¡ctico basado en las pÃ¡ginas que puedo citar.",
                Intro: "Yo empezarÃ­a por:",
                Missing: "Algunas partes todavÃ­a necesitan otra pÃ¡gina citada antes de poder completarlas con confianza.",
                Verify: "Consulta las pÃ¡ginas citadas antes de usar cantidades, tiempos o restricciones exactas."),
            "pt" => (
                Header: "Aqui estÃ¡ um rascunho prÃ¡tico baseado nas pÃ¡ginas que consigo citar.",
                Intro: "Eu comeÃ§aria por:",
                Missing: "Algumas partes ainda precisam de outra pÃ¡gina citada antes de poderem ser preenchidas com confianÃ§a.",
                Verify: "Consulta as pÃ¡ginas citadas antes de usar quantidades, tempos ou restriÃ§Ãµes exatas."),
            "de" => (
                Header: "Hier ist ein praktischer Entwurf auf Basis der Seiten, die ich zitieren kann.",
                Intro: "Ich wÃ¼rde damit beginnen:",
                Missing: "Einige Teile brauchen noch eine weitere zitierte Seite, bevor ich sie sicher ausfÃ¼llen kann.",
                Verify: "PrÃ¼fe die zitierten Seiten, bevor du genaue Mengen, Zeiten oder EinschrÃ¤nkungen Ã¼bernimmst."),
            "it" => (
                Header: "Ecco una bozza pratica basata sulle pagine che posso citare.",
                Intro: "Io partirei da:",
                Missing: "Alcune parti richiedono ancora un'altra pagina citata prima di poterle completare con sicurezza.",
                Verify: "Controlla le pagine citate prima di usare quantitÃ , tempi o vincoli precisi."),
            _ => (
                Header: "Voici une proposition pratique Ã  partir des pages que je peux citer.",
                Intro: "Je commencerais par :",
                Missing: "Certaines parties doivent encore Ãªtre complÃ©tÃ©es avec une autre page citÃ©e avant d'Ãªtre fiables.",
                Verify: "VÃ©rifie les pages citÃ©es avant de reprendre des quantitÃ©s, horaires ou contraintes exactes.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Intro);
        var sections = new[]
        {
            (Title: PartialPlanningSectionTitle(language, "frame"), Items: leads.Where(static lead => lead.IsPlanningFrame).ToList()),
            (Title: PartialPlanningSectionTitle(language, "options"), Items: leads.Where(static lead => !lead.IsPlanningFrame && lead.IsConcreteOption).ToList()),
            (Title: PartialPlanningSectionTitle(language, "context"), Items: leads.Where(static lead => !lead.IsPlanningFrame && !lead.IsConcreteOption).ToList())
        };

        foreach (var section in sections.Where(static section => section.Items.Count > 0))
        {
            sb.AppendLine(section.Title);
            foreach (var lead in section.Items)
            {
                sb.Append("- ");
                sb.Append(lead.Title);
                sb.Append(" (");
                sb.Append(lead.SourceLabel);
                sb.Append(' ');
                sb.Append(SourceBackedPagePrefix(language));
                sb.Append(lead.Page);
                sb.AppendLine(").");
            }
        }

        var needsAdditionalEvidence = leads.Count < ResolveSourceBackedPlanningTargetItemCount(query);
        if (needsAdditionalEvidence)
            sb.AppendLine(labels.Missing);
        sb.Append(labels.Verify);
        return needsAdditionalEvidence
            ? AppendBroadenedSearchOfferIfHelpful(sb.ToString(), query, language)
            : sb.ToString().TrimEnd();
    }

    private static string BuildStructuredReadablePartialPlanningEvidenceAnswer(
        IReadOnlyList<PartialPlanningEvidenceLead> leads,
        string query,
        string language)
    {
        var dayLabels = DetectRequestedDayAxisLabels(query, language);
        var periodLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        if (dayLabels.Count == 0 || periodLabels.Count == 0)
            return string.Empty;

        var slotLeads = leads
            .Where(static lead => lead.IsConcreteOption)
            .GroupBy(static lead => $"{lead.Title}|{lead.SourceLabel}|{lead.Page}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (slotLeads.Count == 0)
            return string.Empty;

        var requiredSlots = dayLabels.Count * periodLabels.Count;
        var shouldKeepSparse = RequiresFullyDistinctStructuredPlanningItems(query)
            ? slotLeads.Count < requiredSlots
            : slotLeads.Count < Math.Min(requiredSlots, Math.Max(1, periodLabels.Count));
        if (shouldKeepSparse || slotLeads.Count < requiredSlots)
            return string.Empty;

        var labels = NormalizeLanguageCode(language) switch
        {
            "en" => (
                Header: "Here is a draft plan using only the cited options.",
                Partial: "I can already fill part of the requested structure with cited options; the remaining parts still need to be checked against other pages.",
                MissingSlot: "to verify with another cited option",
                Remaining: "The remaining parts stay open until another cited page confirms them.",
                Verify: "Before using it as a final plan, check the cited pages for quantities, timing, constraints and substitutions."),
            "es" => (
                Header: "AquÃ­ tienes un borrador de plan usando solo opciones citadas.",
                Partial: "Ya puedo completar parte de la estructura solicitada con opciones citadas; las demÃ¡s partes todavÃ­a deben verificarse con otras pÃ¡ginas.",
                MissingSlot: "verificar con otra opciÃ³n citada",
                Remaining: "Las demÃ¡s partes quedan abiertas hasta que otra pÃ¡gina citada las confirme.",
                Verify: "Antes de usarlo como plan final, revisa las pÃ¡ginas citadas para cantidades, horarios, restricciones y sustituciones."),
            "pt" => (
                Header: "Aqui estÃ¡ um rascunho de plano usando apenas opÃ§Ãµes citadas.",
                Partial: "JÃ¡ consigo preencher parte da estrutura pedida com opÃ§Ãµes citadas; as restantes partes ainda precisam de ser verificadas noutras pÃ¡ginas.",
                MissingSlot: "verificar com outra opÃ§Ã£o citada",
                Remaining: "As restantes partes ficam em aberto atÃ© outra pÃ¡gina citada as confirmar.",
                Verify: "Antes de usar isto como plano final, verifica as pÃ¡ginas citadas para quantidades, horÃ¡rios, restriÃ§Ãµes e substituiÃ§Ãµes."),
            "de" => (
                Header: "Hier ist ein Planentwurf nur mit zitierten Optionen.",
                Partial: "Ich kann bereits einen Teil der gewÃ¼nschten Struktur mit zitierten Optionen fÃ¼llen; die Ã¼brigen Teile mÃ¼ssen noch auf anderen Seiten geprÃ¼ft werden.",
                MissingSlot: "mit einer weiteren zitierten Option prÃ¼fen",
                Remaining: "Die Ã¼brigen Teile bleiben offen, bis eine weitere zitierte Seite sie bestÃ¤tigt.",
                Verify: "PrÃ¼fe vor der finalen Nutzung die zitierten Seiten zu Mengen, Zeiten, EinschrÃ¤nkungen und Alternativen."),
            "it" => (
                Header: "Ecco una bozza di piano usando solo opzioni citate.",
                Partial: "Posso giÃ  compilare una parte della struttura richiesta con opzioni citate; il resto deve ancora essere verificato su altre pagine.",
                MissingSlot: "verificare con un'altra opzione citata",
                Remaining: "Le altre parti restano aperte finchÃ© un'altra pagina citata non le conferma.",
                Verify: "Prima di usarlo come piano finale, controlla le pagine citate per quantitÃ , tempi, vincoli e sostituzioni."),
            _ => (
                Header: "Voici une Ã©bauche de planning avec uniquement les options citÃ©es.",
                Partial: "Je peux dÃ©jÃ  remplir une partie de la structure demandÃ©e avec des options citÃ©es ; les autres parties doivent encore Ãªtre vÃ©rifiÃ©es dans d'autres pages.",
                MissingSlot: "Ã  vÃ©rifier avec une autre option citÃ©e",
                Remaining: "Les autres parties restent ouvertes tant qu'une autre page citÃ©e ne les confirme pas.",
                Verify: "Avant d'en faire un planning dÃ©finitif, vÃ©rifie les pages citÃ©es pour les quantitÃ©s, horaires, contraintes et remplacements.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Partial);

        var slotIndex = 0;
        foreach (var day in dayLabels)
        {
            sb.AppendLine();
            sb.AppendLine($"{day} :");
            foreach (var period in periodLabels)
            {
                sb.Append("  - ");
                sb.Append(period);
                sb.Append(" : ");
                if (slotIndex < slotLeads.Count)
                {
                    var lead = slotLeads[slotIndex];
                    sb.Append(lead.Title);
                    sb.Append(" (");
                    sb.Append(lead.SourceLabel);
                    sb.Append(' ');
                    sb.Append(SourceBackedPagePrefix(language));
                    sb.Append(lead.Page);
                    sb.AppendLine(").");
                }
                else
                {
                    sb.AppendLine(labels.MissingSlot + ".");
                }
                slotIndex++;
            }
        }

        sb.AppendLine();
        sb.Append(labels.Verify);
        return slotLeads.Count < requiredSlots
            ? AppendBroadenedSearchOfferIfHelpful(sb.ToString(), query, language)
            : sb.ToString().TrimEnd();
    }

    private static string PartialPlanningSectionTitle(string language, string kind)
    {
        language = NormalizeLanguageCode(language);
        return kind switch
        {
            "frame" => language switch
            {
                "en" => "Organization notes",
                "es" => "Notas de organizaciÃ³n",
                "pt" => "Notas de organizaÃ§Ã£o",
                "de" => "Planungsrahmen",
                "it" => "Base organizzativa",
                _ => "RepÃ¨res d'organisation"
            },
            "options" => language switch
            {
                "en" => "Directly usable options",
                "es" => "Opciones directamente utilizables",
                "pt" => "OpÃ§Ãµes diretamente utilizÃ¡veis",
                "de" => "Direkt nutzbare Optionen",
                "it" => "Opzioni direttamente utilizzabili",
                _ => "Options directement utilisables"
            },
            _ => language switch
            {
                "en" => "Useful context",
                "es" => "Contexto Ãºtil",
                "pt" => "Contexto Ãºtil",
                "de" => "NÃ¼tzlicher Kontext",
                "it" => "Contesto utile",
                _ => "Contexte utile"
            }
        };
    }

    private static PartialPlanningEvidenceLead BuildReadablePartialPlanningEvidenceLead(RagHitSummary hit, string query, string language)
    {
        var requiresStrictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var title = requiresStrictStructuredPlanning
            ? ExtractStrictSourceBackedOptionTitle(hit, query)
            : ExtractReadablePartialPlanningLeadTitle(hit, query);
        var normalizedTitle = NormalizeLexicalLookup(title);
        var hasConcreteTitle = !string.IsNullOrWhiteSpace(title)
            && !LooksLikeWeakPartialPlanningLeadTitle(title)
            && !LooksLikeProcedureSentenceTitle(normalizedTitle);
        var hasConcreteCardTitle = !requiresStrictStructuredPlanning && hit.MatchedContentCards?.Any(card =>
        {
            var cardTitle = CleanSourceBackedOptionTitle(card.Title);
            return IsUsableSourceBackedOptionTitle(cardTitle);
        }) == true;
        var hasConcreteProfileTitle = !requiresStrictStructuredPlanning && HasSourceBackedProfileTitle(hit);

        var evidence = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var isPlanningLead = !requiresStrictStructuredPlanning && Regex.IsMatch(
            evidence,
            @"\b(?:plan|planning|planification|organisation|organiser|horaire|horaires|calendrier|programme|temps\s+disponible|schedule|calendar|organize|organise|time\s+available|wochenplan|programma)\b",
            RegexOptions.CultureInvariant);
        var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
        var candidateProbe = new SourceBackedOptionCandidate(
            hit,
            title,
            Score: 0,
            VisibleMinutes: visibleMinutes);
        var looksLikeGenericPlanningContext = LooksLikeGenericPlanningContextCandidate(candidateProbe);
        var hasStrictConcreteEvidence = requiresStrictStructuredPlanning
            && hasConcreteTitle
            && HasStrictStructuredPlanningCandidateEvidence(candidateProbe);
        var isConcreteOption = requiresStrictStructuredPlanning
            ? hasStrictConcreteEvidence
            : (hasConcreteTitle || hasConcreteCardTitle || hasConcreteProfileTitle) && !looksLikeGenericPlanningContext;

        var fallbackTitle = language switch
        {
            "en" when isPlanningLead => "Planning frame",
            "en" when isConcreteOption => "Useful option",
            "en" => "Useful note",
            "es" when isPlanningLead => "Marco de organizaciÃ³n",
            "es" when isConcreteOption => "OpciÃ³n Ãºtil",
            "es" => "Nota Ãºtil",
            "pt" when isPlanningLead => "Base de organizaÃ§Ã£o",
            "pt" when isConcreteOption => "OpÃ§Ã£o Ãºtil",
            "pt" => "Nota Ãºtil",
            "de" when isPlanningLead => "Planungsrahmen",
            "de" when isConcreteOption => "NÃ¼tzliche Option",
            "de" => "NÃ¼tzlicher Hinweis",
            "it" when isPlanningLead => "Base organizzativa",
            "it" when isConcreteOption => "Opzione utile",
            "it" => "Nota utile",
            _ when isPlanningLead => "RepÃ¨re d'organisation",
            _ when isConcreteOption => "Option utile",
            _ => "Note utile"
        };

        var guidance = language switch
        {
            "en" when isPlanningLead => "use it to frame timing and organization before choosing the concrete items",
            "en" when isConcreteOption => "use it as one concrete option in the rotation",
            "en" => "keep it as supporting context, not as a complete answer",
            "es" when isPlanningLead => "sirve para encuadrar tiempos y organizaciÃ³n antes de elegir los elementos concretos",
            "es" when isConcreteOption => "puede usarse como opciÃ³n concreta dentro de la rotaciÃ³n",
            "es" => "mantenla como contexto de apoyo, no como respuesta completa",
            "pt" when isPlanningLead => "serve para enquadrar tempos e organizaÃ§Ã£o antes de escolher os itens concretos",
            "pt" when isConcreteOption => "pode ser usada como opÃ§Ã£o concreta na rotaÃ§Ã£o",
            "pt" => "mantÃ©m isto como contexto de apoio, nÃ£o como resposta completa",
            "de" when isPlanningLead => "nutze ihn fÃ¼r Zeitrahmen und Organisation, bevor konkrete Elemente gewÃ¤hlt werden",
            "de" when isConcreteOption => "nutze sie als konkrete Option in der Rotation",
            "de" => "nutze ihn als Kontext, nicht als vollstÃ¤ndige Antwort",
            "it" when isPlanningLead => "usala per definire tempi e organizzazione prima di scegliere gli elementi concreti",
            "it" when isConcreteOption => "usala come opzione concreta nella rotazione",
            "it" => "tienila come contesto di supporto, non come risposta completa",
            _ when isPlanningLead => "sert Ã  cadrer les horaires et l'organisation avant de choisir les Ã©lÃ©ments concrets",
            _ when isConcreteOption => "peut servir d'option concrÃ¨te dans la rotation",
            _ => "sert de contexte d'appui, pas de rÃ©ponse complÃ¨te Ã  elle seule"
        };

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return new PartialPlanningEvidenceLead(
            hasConcreteTitle ? title : fallbackTitle,
            guidance,
            string.IsNullOrWhiteSpace(docLabel) ? hit.DocPath : docLabel,
            hit.PageStart,
            isPlanningLead,
            isConcreteOption);
    }

    private static string ExtractReadablePartialPlanningLeadTitle(RagHitSummary hit, string query)
    {
        var title = ExtractSourceBackedOptionTitle(hit, query);
        if (!LooksLikeWeakPartialPlanningLeadTitle(title))
            return title;

        var profileTitle = ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)
            .Select(CleanSourceBackedOptionTitle)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Where(IsUsefulSourceBackedDisplayTitle)
            .FirstOrDefault(static candidate => !LooksLikeWeakPartialPlanningLeadTitle(candidate));
        if (!string.IsNullOrWhiteSpace(profileTitle))
            return profileTitle;

        var evidence = CollapseWhitespace(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return string.Empty;

        var headingMatch = Regex.Match(
            evidence,
            @"^(?<title>[\p{Lu}\p{N}\u00c0-\u017f '&/\-,]{4,90})\s+(?:PREPARATION|PR[Ã‰E]PARATION|ETAPES?|[Ã‰E]TAPES?|STEPS?|METHOD|M[Ã‰E]THODE|PROCEDURE|PROC[Ã‰E]DURE)\b",
            RegexOptions.CultureInvariant);
        if (!headingMatch.Success)
            return string.Empty;

        var headingTitle = CleanSourceBackedOptionTitle(headingMatch.Groups["title"].Value);
        return !LooksLikeWeakPartialPlanningLeadTitle(headingTitle) && IsUsefulSourceBackedDisplayTitle(headingTitle)
            ? headingTitle
            : string.Empty;
    }

    private static bool LooksLikeWeakPartialPlanningLeadTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 70
            || normalized.Contains(".pdf", StringComparison.Ordinal)
            || LooksLikeWeakSourceBackedOptionTitle(title)
            || Regex.IsMatch(normalized, @"\b(?:document|documents|source|sources|pistes?|trouvees?|disponibles?|couvre|comprend|sections?|conseils?|page|pages)\b", RegexOptions.CultureInvariant);
    }

}
