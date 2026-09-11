using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeSourceBackedPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (LooksLikeCorpusClaimVerificationRequest(query))
            return false;
        if (LooksLikeSourceBackedCountdownPlanningRequest(query))
            return false;

        var asksForPlan = LooksLikeWeeklyPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query)
            || Regex.IsMatch(
            s,
                @"\b(?:plan|planning|calendrier|programme|organisation|schedule|calendar|wochenplan|programm|piano|programma|calendario|programa|organizacion|plano|organizacao|organizacao|programma|organizzazione|pianificazione)\b",
                RegexOptions.CultureInvariant);

        return asksForPlan
            && (LooksLikeSourceBackedActionRequest(query)
                || (RequiresStructuredSourceBackedPlanningCoverage(query)
                    && (LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
                        || LooksLikeMultipleCandidateSynthesisRequest(query)
                        || LooksLikeBroadSourceBackedCompositionRequest(query))));
    }

    private static bool LooksLikeAnyDocumentaryPlanningRequest(string? query)
        => LooksLikeSourceBackedPlanningRequest(query) || LooksLikeDocumentaryPlanningRequest(query);

    private static bool UsesSourceBackedPlanningCoverage(string? query)
        => RequiresStructuredSourceBackedPlanningCoverage(query)
           && !LooksLikeSourceBackedPairingRecommendationRequest(query)
           && !LooksLikeSoftChoiceRecommendationRequest(query);

    private static bool LooksLikeSourceBackedVerificationChecklistRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var asksForVerification = Regex.IsMatch(
            s,
            @"\b(?:verifie|verifier|verification|check|verify|controle|controler|valide|valider|validation|audit|points?\s+a\s+valider|points?\s+de\s+controle|checklist)\b",
            RegexOptions.CultureInvariant);
        var asksForExplicitChecklistOrReviewShape = Regex.IsMatch(
            s,
            @"\b(?:verifie|verifier|verification|check|verify|valide|valider|validation|audit|checklist|points?\s+a\s+valider|points?\s+de\s+controle|etapes?|steps?|dois|devrais|faut|should|must)\b",
            RegexOptions.CultureInvariant);
        if (!asksForExplicitChecklistOrReviewShape
            && Regex.IsMatch(
                s,
                @"\b(?:explique|expliquer|expliquez|explain|explains|explica|explicar|erklaere|erklaren|spiega|spiegare)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var asksForActionableShape = Regex.IsMatch(
            s,
            @"\b(?:que|quoi|what|which|comment|how|dois|devrais|faut|should|must|points?|etapes?|steps?|documents?|sources?)\b",
            RegexOptions.CultureInvariant);

        return asksForVerification && asksForActionableShape;
    }

    private static bool LooksLikeCorpusClaimVerificationRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var asksVerification = Regex.IsMatch(
            s,
            @"\b(?:verifie|verifier|verification|check|verify|prouve|prouver|preuve|prove|proof|demontre|demontre|demonstrated|demonstrate)\b",
            RegexOptions.CultureInvariant);
        var hasCorpusScope = Regex.IsMatch(
            s,
            @"\b(?:corpus|documents?|sources?|pdfs?|base\s+de\s+connaissances?|knowledge\s+base)\b",
            RegexOptions.CultureInvariant);
        var hasStrongClaim = Regex.IsMatch(
            s,
            @"\b(?:toujours|jamais|always|never|obligation\s+generale|general\s+obligation|obligatoire|mandatory|required|requires?|impose|imposes?|condition\s+limitee|limited\s+condition|recommandation|recommendation|pas\s+demontre|not\s+demonstrated)\b",
            RegexOptions.CultureInvariant);

        return asksVerification && hasStrongClaim && (hasCorpusScope || s.Contains('`', StringComparison.Ordinal));
    }

    private static bool LooksLikeWeeklyPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|7\s+jours?|sept\s+jours?|week|weekly|semana|semanal|woche|wochen|wochenplan|settimana|settimanale)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDocumentaryPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var hasPlanningCue = Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|jours|journee|plan|planning|calendrier|programme|organisation|parallele|avance|preparation|preparer|week|weekly|schedule|calendar|parallel|advance|prepare|preparation|semana|semanal|dias|calendario|programa|organizacion|paralelo|preparar|preparacion|plano|organizacao|paralelo|preparar|preparacao|woche|wochenplan|kalender|programm|organisation|parallel|vorbereiten|piano|programma|organizzazione|pianificazione|parallelo|preparare|preparazione)\b",
            RegexOptions.CultureInvariant);
        if (!hasPlanningCue)
            return false;

        if (LooksLikeSourceBackedActionRequest(query))
            return true;

        return Regex.IsMatch(
            s,
            @"\b(?:documents?|docs?|sources?|fichiers?|pdfs?|extraits?|corpus|base\s+de\s+connaissances?|knowledge\s+base|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSourceBackedOptionRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s) || LooksLikeSourceBackedPlanningRequest(query))
            return false;

        if (LooksLikeCategoryOverviewOrDocumentOrientationRequest(query))
            return false;

        if (LooksLikeRankingDocumentaryRequest(query) || LooksLikeComparativeDocumentaryRequest(query))
            return false;

        if (!LooksLikeDocumentaryContentRequest(query) && !ExtractQuerySignalTerms(s).Any())
            return false;

        var hasNeedOrConstraintCue = Regex.IsMatch(
            s,
            @"\b(?:il\s+me\s+faut|j\s+ai\s+besoin|besoin|need|needed|necesito|preciso|brauche|serve|mi\s+serve|moins\s+de|sous|within|under|total)\b",
            RegexOptions.CultureInvariant);
        var hasCompositeCue = s.Contains('+', StringComparison.Ordinal)
            || Regex.IsMatch(
                s,
                @"\b(?:et|avec|plus|ensemble|combine|combiner|and|with|plus|together|total|complete|complet|completa|completo)\b",
                RegexOptions.CultureInvariant);
        if (hasNeedOrConstraintCue && hasCompositeCue)
            return true;

        if (Regex.IsMatch(s, @"\b(?:plan|planning|liste|list|options?|suggestions?|selection|sÃ©lection)\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(s, @"\b(?:complet|complete|sans|without|pas\s+de|uniquement|only|avec|with|pour|for|autour|around|total)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                s,
                @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|options?|suggestions?|idees?|ideas?|choix|choices?)\b",
                RegexOptions.CultureInvariant)
            && (LooksLikeSourceBackedActionRequest(query)
                || LooksLikeDocumentaryContentRequest(query)
                || Regex.IsMatch(
                    s,
                    @"\b(?:disponibles?|available|sources?|documents?|docs?|corpus|dossier|category|categorie|cat[e\u00e9]gorie)\b",
                    RegexOptions.CultureInvariant)
                || ExtractQuerySignalTerms(s).Take(2).Any()))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"\b(?:complet|complete|completa|completo|total)\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(s, @"\b(?:sans|without|pas\s+de|excluding|exclude|sauf|except|uniquement|only|avec|with)\b", RegexOptions.CultureInvariant)
            && LooksLikeSourceBackedActionRequest(query))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"\b(?:fais|faire|compose|composer|cree|creer|prepare|preparer|make|compose|create|prepare)\b.{0,80}\b(?:plan|planning|liste|list|options?|suggestions?)\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(s, @"\b(?:fais|faire|compose|composer|cree|creer|prepare|preparer|make|compose|create|prepare)\b.{0,100}\b(?:composition|selection|sÃ©lection)\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(s, @"\b(?:compose|composer|cree|creer|prepare|preparer|make|create|prepare)\b.{0,100}\b(?:options?|idees?|ideas?|items?|elements?|liste|list|plan|planning)\b", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            s,
            @"\b(?:propose|proposes|idee|idees|quoi|choisis|choisir|option|options|composition|suggest|suggestion|suggestions|which|what|cual|qual|welche|quale)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeCategoryOverviewOrDocumentOrientationRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (LooksLikeSourceBackedVerificationChecklistRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query))
        {
            return false;
        }

        var hasScope = Regex.IsMatch(
            s,
            @"\b(?:categorie|category|categoria|kategorie|dossier|folder|corpus|base\s+de\s+connaissances?|knowledge\s+base)\b",
            RegexOptions.CultureInvariant);
        var hasDocumentSet = Regex.IsMatch(
            s,
            @"\b(?:pdfs?|documents?|docs?|sources?|fichiers?|files?)\b",
            RegexOptions.CultureInvariant);
        var hasOrientationCue = Regex.IsMatch(
            s,
            @"\b(?:overview|vue\s+d\s+ensemble|cartographie|orientation|utile|utiles|useful|important|importants|business\s+questions?|real\s+business|lire\s+en\s+premier|read\s+first|used\s+first|role|roles?|familles?|families|classer|group(?:er|ing)?|regrouper)\b",
            RegexOptions.CultureInvariant);

        return hasOrientationCue && (hasScope || hasDocumentSet);
    }

    private static bool LooksLikeSourceBackedPairingRecommendationRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var hasStrongPairingCue = Regex.IsMatch(
            s,
            @"\b(?:irait\s+bien|va\s+bien|vont\s+bien|aller\s+avec|aille\s+avec|aillent\s+avec|accompagne|accompagner|associe|associer|compatible|compatibles|goes?\s+with|pair(?:ing)?|pairs?\s+with|compatible|acompanha|acompanhar|combina|combinar|passt\s+zu|kombinieren|abbinare|abbina|si\s+abbina)\b",
            RegexOptions.CultureInvariant);
        var hasWeakPairingCue = Regex.IsMatch(
            s,
            @"\b(?:avec|con)\b",
            RegexOptions.CultureInvariant);
        if (!hasStrongPairingCue && !hasWeakPairingCue)
            return false;

        if (!hasStrongPairingCue
            && Regex.IsMatch(
                s,
                @"\b(?:avec|con)\s+(?:les|des|de\s+las|las|le|i|gli|the)?\s*(?:sources?|documents?|fuentes?|fonti|quellen|documentos?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (!hasStrongPairingCue
            && Regex.IsMatch(
                s,
                @"\b(?:quoi\s+faire|que\s+faire|what\s+to\s+do|organisation|organiser|organization|organize|organise|planning|plan|procedure|processus|process|workflow|etapes?|steps?|comment|how|dois|devrais|should)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
            s,
            @"\b(?:quel|quelle|quels|quelles|quoi|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale|propose|proposes|suggest|recommend|recommande|recommander|conseille|conseiller)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldUseSourceBackedOptionAnswer(string? requestedItemTitle, string query)
        => LooksLikeSourceBackedOptionRequest(query)
            && (string.IsNullOrWhiteSpace(requestedItemTitle)
                || LooksLikeBroadSourceBackedCompositionRequest(query));

    private static bool ShouldAvoidDeterministicSourceBackedOptionFallback(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return IsBroadenedSourceSearchConfirmationEnvelope(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query);
    }

    private static bool LooksLikeUnresolvedSourceBackedDeicticFollowup(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasDeicticReference = Regex.IsMatch(
            normalized,
            @"\b(?:ca|cela|ceci|this|that|it|eso|esto|isso|isto|das|questo|quello)\b",
            RegexOptions.CultureInvariant);
        if (!hasDeicticReference)
            return false;

        if (LooksLikeCompleteSourceBackedRequestWithDeicticReference(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:apres|aprÃ¨s|precedent|pr[eÃ©]c[eÃ©]dent|document|source|element|item|mets|mettre|adapte|adapter|pour|after|previous|put|scale|adjust|adapt)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeCompleteSourceBackedRequestWithDeicticReference(string normalized)
    {
        var signalTermCount = ExtractQuerySignalTerms(normalized).Take(3).Count();
        if (signalTermCount < 2)
            return false;

        var hasPlanningCue = Regex.IsMatch(
            normalized,
            @"\b(?:plan|planning|calendrier|programme|organisation|schedule|calendar|semaine|hebdo|hebdomadaire|week|weekly|semana|semanal|woche|wochenplan|settimana|settimanale|piano|programma)\b",
            RegexOptions.CultureInvariant);
        var hasSelectionCue = Regex.IsMatch(
            normalized,
            @"\b(?:propose|proposes|proposer|idee|idees|quoi|quel|quelle|quels|quelles|option|options|suggestion|suggestions|choisir|choix|liste|lister|suggest|which|what|list|selection|seleccion|selecao|auswahl|scelta)\b",
            RegexOptions.CultureInvariant);
        var hasActionCue = Regex.IsMatch(
            normalized,
            @"\b(?:fais|faire|prepare|preparer|organise|organiser|structure|structurer|construis|construire|cree|creer|redige|rediger|compose|composer|elabore|elaborer|build|create|make|prepare|organize|organise|draft|compose|structure|prepara|preparar|organiza|organizar|crea|crear|redacta|redactar|prepara|preparar|organiza|organizar|cria|criar|redige|redigir|erstelle|erstellen|bereite|vorbereiten|organisiere|organisieren|verfasse|prepara|preparare|organizza|organizzare|crea|creare|redigi|redigere)\b",
            RegexOptions.CultureInvariant);
        var hasOverviewCue = Regex.IsMatch(
            normalized,
            @"\b(?:overview|vue\s+d\s+ensemble|cartographie|orientation|utile|utiles|important|importants|classer|regrouper|role|roles|famille|familles|documents?|sources?|corpus|base\s+de\s+connaissances?|knowledge\s+base)\b",
            RegexOptions.CultureInvariant);
        var hasSearchableObjectCue = Regex.IsMatch(
            normalized,
            @"\b(?:documents?|sources?|corpus|base\s+de\s+connaissances?|knowledge\s+base|plan|planning|calendrier|programme|procedure|processus|comparaison|compare|comparar|vergleich|confronto|recommandation|recommendation|synthese|summary|resumen|resumo|zusammenfassung|riassunto)\b",
            RegexOptions.CultureInvariant);

        return (hasPlanningCue && (hasSelectionCue || hasActionCue))
            || (hasOverviewCue && (hasSelectionCue || hasActionCue))
            || (hasActionCue && hasSearchableObjectCue && signalTermCount >= 3);
    }

    private static bool LooksLikeMissingStandardIdentifierQuestion(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.Contains("norme xxx", StringComparison.Ordinal)
            || normalized.Contains("standard xxx", StringComparison.Ordinal);
    }

    private static string BuildMissingStandardIdentifierClarification(string language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "Quelle est la référence exacte de la norme, et quel périmètre du projet faut-il vérifier ? Avec ces deux informations, je pourrai rechercher les exigences applicables sans remplacer le placeholder « xxx ».",
            "What is the exact standard reference, and which project scope should be checked? With both details, I can search the applicable requirements without replacing the 'xxx' placeholder.",
            "¿Cuál es la referencia exacta de la norma y qué alcance del proyecto debe verificarse? Con ambos datos podré buscar los requisitos aplicables sin sustituir el marcador «xxx».",
            "Qual é a referência exata da norma e que âmbito do projeto deve ser verificado? Com ambas as informações, poderei procurar os requisitos aplicáveis sem substituir o marcador «xxx».",
            "Wie lautet die genaue Normreferenz und welcher Projektumfang soll geprüft werden? Mit beiden Angaben kann ich die geltenden Anforderungen suchen, ohne den Platzhalter 'xxx' zu ersetzen.",
            "Qual è il riferimento esatto della norma e quale ambito del progetto deve essere verificato? Con entrambe le informazioni potrò cercare i requisiti applicabili senza sostituire il segnaposto «xxx».");

    private static bool LooksLikeVagueVerificationScopeQuestion(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksSafeVerification = Regex.IsMatch(
            normalized,
            @"\b(?:reponse\s+sure|safe\s+answer|pas\s+d\s+une\s+supposition|not\s+a\s+guess|supposition|guess)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalized,
                @"\b(?:verifie|verifier|verifies|verify|check|controle|controler|ou\s+exactement|where\s+exactly)\b",
                RegexOptions.CultureInvariant);
        var asksWhereExactly = Regex.IsMatch(
            normalized,
            @"\b(?:ou\s+exactement|where\s+exactly)\b",
            RegexOptions.CultureInvariant);
        if (!asksSafeVerification && !asksWhereExactly)
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractPdfFileNameRequestedTitle(query)))
            return false;

        return !Regex.IsMatch(
            normalized,
            @"\b(?:iso|iec|en|ul|nfpa|ansi|astm|din)\s*\d{2,}|\b(?:pdf|document|doc|fichier|file)\s+[\p{L}0-9_-]{3,}",
            RegexOptions.CultureInvariant);
    }

    private static string BuildVagueVerificationScopeClarification(string language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "J'ai besoin du document, de la norme, de la catÃ©gorie ou du sujet exact Ã  vÃ©rifier. Sans ce pÃ©rimÃ¨tre, je risquerais de choisir des sources au hasard ; indique ce que je dois contrÃ´ler et je citerai les pages pertinentes.",
            "I need the exact document, standard, category, or topic to verify. Without that scope, I could pick sources at random; tell me what to check and I will cite the relevant pages.",
            "Necesito el documento, la norma, la categoria o el tema exacto que debo verificar. Sin ese alcance podria elegir fuentes al azar; dime que debo comprobar y citare las paginas pertinentes.",
            "Preciso do documento, norma, categoria ou tema exato a verificar. Sem esse ambito eu poderia escolher fontes ao acaso; diz-me o que devo verificar e citarei as paginas relevantes.",
            "Ich brauche das genaue Dokument, die Norm, die Kategorie oder das Thema, das ich pruefen soll. Ohne diesen Rahmen koennte ich Quellen zufaellig waehlen; sag mir, was ich pruefen soll, dann zitiere ich die passenden Seiten.",
            "Mi serve il documento, la norma, la categoria o il tema esatto da verificare. Senza questo perimetro rischierei di scegliere fonti a caso; dimmi cosa devo controllare e citero le pagine pertinenti.");

    private static string BuildUnresolvedSourceBackedDeicticFollowupAnswer(string language, string query)
    {
        language = NormalizeLanguageCode(language);
        var target = TryExtractTargetScaleCount(query, out var count)
            ? language switch
            {
                "en" => $" for {count}",
                "es" => $" para {count}",
                "pt" => $" para {count}",
                "de" => $" fuer {count}",
                "it" => $" per {count}",
                _ => $" pour {count}"
            }
            : string.Empty;
        return SourceBackedLabel(
            language,
            $"Je n'ai pas d'element precedent exploitable dans cette conversation. Donne-moi l'element, le document ou la source concernee, puis je pourrai l'adapter ou la citer{target} sans inventer.",
            $"I do not have an exploitable previous item in this conversation. Give me the item, document, or source involved, then I can adapt or cite it{target} without inventing details.",
            $"No tengo un elemento anterior explotable en esta conversacion. Dame el elemento, el documento o la fuente correspondiente y podre adaptarlo o citarlo{target} sin inventar detalles.",
            $"Nao tenho um elemento anterior utilizavel nesta conversa. Da-me o item, o documento ou a fonte em causa e poderei adapta-lo ou cita-lo{target} sem inventar detalhes.",
            $"Ich habe in dieser Unterhaltung kein nutzbares vorheriges Element. Gib mir den Eintrag, das Dokument oder die betroffene Quelle, dann kann ich ihn{target} anpassen oder zitieren, ohne Details zu erfinden.",
            $"Non ho un elemento precedente utilizzabile in questa conversazione. Dammi l'elemento, il documento o la fonte interessata e potro adattarlo o citarlo{target} senza inventare dettagli.");
    }

    private static bool LooksLikeBroadDocumentaryInformationRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay)\b", RegexOptions.CultureInvariant))
            return false;

        var hasInformationIntent = Regex.IsMatch(
            normalized,
            @"\b(?:info|infos|information|informations|renseignement|renseignements|parle\s+moi|dis\s+moi|dis\s+m\s+en|que\s+sais\s+tu|je\s+veux\s+comprendre|aide\s+moi\s+sur|au\s+sujet\s+de|a\s+propos\s+de|about|tell\s+me\s+about|details|overview|explain|explica|explicame|informacion|informaciones|detalles|sobre|informacao|informacoes|detalhes|erklaere|erklaren|informationen|uber|ueber|spiega|informazioni|dettagli|riguardo)\b",
            RegexOptions.CultureInvariant);
        if (!hasInformationIntent)
            return false;

        var signalTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsGenericDocumentaryProbeTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Take(2)
            .ToArray();

        return signalTerms.Length > 0;
    }

    private static bool LooksLikeDocumentaryContentRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
            return true;
        if (TryExtractDocumentContentSearchTopic(query, out _))
            return true;

        var mentionsCommonContentObject = Regex.IsMatch(
            normalized,
            @"\b(?:fiche|card|cards|document|documents|source|sources|element|elements|item|items|objet|objets|sujet|sujets|topic|topics|procedure|procedures|process|processus|method|methods|methode|methodes|notice|notices|instruction|instructions)\b",
            RegexOptions.CultureInvariant);
        if (mentionsCommonContentObject)
        {
            var hasCommonDirectiveNounPhrase = Regex.IsMatch(
                normalized,
                @"^(?:(?:un|une|des|du|de\s+la|the|a|an|some)\s+)?(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|method|methode|notice|instruction)\b",
                RegexOptions.CultureInvariant);
            var hasCommonRequestOperator = Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|quels|quelles|quoi|peux|pourrais|donne|donner|trouve|trouver|cherche|chercher|fais|faire|faut|besoin|veux|voudrais|souhaite|aimerais|liste|lister|propose|proposer|existe|existent|which|what|can|could|give|find|search|make|need|want|list|suggest|exists|exist)\b",
                RegexOptions.CultureInvariant);
            var mentionsCommonDocumentarySource = Regex.IsMatch(
                normalized,
                @"\b(?:pdf|document|documents|doc|docs|source|sources|base|connaissance|knowledge|corpus|file|files)\b",
                RegexOptions.CultureInvariant);

            if (hasCommonDirectiveNounPhrase || hasCommonRequestOperator || mentionsCommonDocumentarySource)
                return true;
        }

        var mentionsDocumentarySource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|extrait|extraits|page|pages|base|connaissance|knowledge|corpus|file|files|archivo|archivos|documento|documentos|fonte|fontes|quelle|quellen|dokument|dokumente|documento|documenti|fonte|fonti)\b",
            RegexOptions.CultureInvariant);
        var mentionsContentObject = Regex.IsMatch(
            normalized,
            @"\b(?:element|elements|item|items|objet|objets|sujet|sujets|topic|topics|section|sections|extrait|extraits|passage|passages|clause|clauses|table|tables|fiche|card|checklist|liste|list|plan|planning|procedure|proc[eÃ©]dure|procedimento|procedura|process|processus|method|methode|instruction|instructions|etape|etapes|[eÃ©]tape|[eÃ©]tapes|step|steps|quantite|quantites|quantit[eÃ©]|quantit[eÃ©]s|amount|amounts|valeur|valeurs|value|values|temps|time|duration|duree|durees)\b",
            RegexOptions.CultureInvariant);
        var hasRequestOperator = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|quoi|qu est|qu est ce|peux|pourrais|donne|donner|trouve|trouver|cherche|chercher|fais|faire|faut|besoin|veux|voudrais|souhaite|aimerais|compare|comparer|liste|lister|resume|resumer|reponds|repondre|r[eÃ©]ponds|r[eÃ©]pondre|adapte|adapter|transforme|transformer|traduis|traduire|rends|rendre|existe|existent|which|what|can|could|give|find|search|make|need|want|compare|list|summarize|respond|answer|adapt|adjust|transform|translate|render|exists|exist|cual|cu[aÃ¡]l|que|puedes|podrias|dame|busca|encuentra|necesito|responde|traduce|existe|quero|pode|podes|procura|encontra|preciso|responde|traduz|existe|welche|was|kannst|suche|finde|brauche|antworte|uebersetze|ubersetze|existiert|quale|cosa|puoi|cerca|trova|bisogno|rispondi|traduci|esiste)\b",
            RegexOptions.CultureInvariant);
        var hasDirectiveNounPhrase = Regex.IsMatch(
            normalized,
            @"^(?:(?:un|une|des|du|de\s+la|the|a|an|some)\s+)?(?:liste|list|checklist|fiche|card|plan|planning|procedure|process|section|table)\b",
            RegexOptions.CultureInvariant);

        if (mentionsDocumentarySource && (mentionsContentObject || ExtractQuerySignalTerms(normalized).Any()))
            return true;

        if (LooksLikeShortStandaloneContentLookup(normalized))
            return true;

        return mentionsContentObject && (hasRequestOperator || hasDirectiveNounPhrase);
    }

    private static bool LooksLikeShortStandaloneContentLookup(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:bonjour|salut|hello|merci|thanks?|pourquoi|why|comment\s+ca\s+va|how\s+are\s+you)\b", RegexOptions.CultureInvariant))
            return false;

        var words = Regex.Matches(normalized, @"[\p{L}\p{N}]{2,}")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length is < 2 or > 12)
            return false;

        var hasContentShape = Regex.IsMatch(
                normalized,
                @"^(?:un|une|des|du|de\s+la|the|a|an|some)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:options?|suggestions?|idees?|ideas?|classique|classic|simple|rapide|quick|technique|technical)\b",
                RegexOptions.CultureInvariant);

        return hasContentShape && ExtractQuerySignalTerms(normalized).Any();
    }
}
