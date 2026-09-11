using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildRagNeighborFallbackAnswer(IReadOnlyList<RagHitSummary> hits, string language)
    {
        var labels = NormalizeLanguageCode(language) switch
        {
            "en" => (
                Header: "I did not find a direct source for the request. These are only nearby leads to check:",
                Caveat: "I prefer not to turn these leads into a final answer without a clearer source."),
            "es" => (
                Header: "No he encontrado una fuente directa para la solicitud. Estas son solo pistas cercanas que hay que comprobar:",
                Caveat: "Prefiero no convertir estas pistas en una respuesta final sin una fuente mÃƒÂ¡s clara."),
            "pt" => (
                Header: "NÃƒÂ£o encontrei uma fonte direta para o pedido. Estas sÃƒÂ£o apenas pistas prÃƒÂ³ximas a verificar:",
                Caveat: "Prefiro nÃƒÂ£o transformar estas pistas numa resposta final sem uma fonte mais clara."),
            "de" => (
                Header: "Ich habe keine direkte Quelle fÃƒÂ¼r die Anfrage gefunden. Das sind nur nahe Hinweise zum PrÃƒÂ¼fen:",
                Caveat: "Ich mache daraus lieber keine endgÃƒÂ¼ltige Antwort ohne eine klarere Quelle."),
            "it" => (
                Header: "Non ho trovato una fonte diretta per la richiesta. Questi sono solo spunti vicini da verificare:",
                Caveat: "Preferisco non trasformarli in una risposta finale senza una fonte piÃƒÂ¹ chiara."),
            _ => (
                Header: "Je n'ai pas trouvÃƒÂ© de source directe pour la demande. Voici seulement des pistes proches ÃƒÂ  vÃƒÂ©rifier :",
                Caveat: "Je prÃƒÂ©fÃƒÂ¨re ne pas transformer ces pistes en rÃƒÂ©ponse dÃƒÂ©finitive sans source plus claire.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var evidenceCue = BuildWriterEvidenceCueForPrompt(hit, query: null, maxLength: 220);
            if (string.IsNullOrWhiteSpace(evidenceCue))
                evidenceCue = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 180));
            if (string.IsNullOrWhiteSpace(evidenceCue))
                continue;

            sb.Append("- ");
            sb.Append(evidenceCue);
            sb.Append(" (");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.AppendLine(").");
        }

        sb.Append(labels.Caveat);
        return sb.ToString().TrimEnd();
    }

    private static bool RagFallbackHasAtLeastOneQueryAnchor(IReadOnlyList<RagHitSummary> hits, string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var anchorTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !SourceBackedPairingAnchorNoiseTerms.Contains(term))
            .Where(static term => !BroadCompositionGenericAnchorTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (anchorTerms.Length == 0)
            return true;

        return anchorTerms.Any(term => hits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)));
    }

    private static string TryBuildBackendGuidanceClarificationAnswer(ToolResults toolResults, string query, string language)
    {
        foreach (var item in toolResults.Items.Where(static x => x.ToolName is "rag.search" or "rag.multi_search"))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("guidance", out var guidance)
                || guidance.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var behavior = TryGetString(guidance, "behavior") ?? TryGetString(guidance, "Behavior");
            var responseShape = TryGetString(guidance, "responseShape") ?? TryGetString(guidance, "ResponseShape");
            var asksClarification =
                string.Equals(behavior, "ask_clarification", StringComparison.OrdinalIgnoreCase)
                || string.Equals(responseShape, "clarify", StringComparison.OrdinalIgnoreCase);
            if (!asksClarification)
                continue;

            if (ShouldPreferSourceBackedAnswerOverBackendClarification(toolResults, query))
                return string.Empty;

            var question =
                TryGetString(guidance, "clarifyingQuestion")
                ?? TryGetString(guidance, "ClarifyingQuestion")
                ?? TryGetString(guidance, "question")
                ?? TryGetString(guidance, "Question");
            if (!string.IsNullOrWhiteSpace(question))
                return CollapseWhitespace(question);

            return SourceBackedLabel(
                language,
                "J'ai besoin d'une prÃƒÂ©cision pour chercher dans les bonnes sources. Peux-tu prÃƒÂ©ciser le document, le standard ou le pÃƒÂ©rimÃƒÂ¨tre concernÃƒÂ© ?",
                "I need one clarification to search the right sources. Could you specify the document, standard, or scope?",
                "Necesito una precision para buscar en las fuentes correctas. Puedes especificar el documento, la norma o el alcance?",
                "Preciso de uma clarificacao para procurar nas fontes certas. Podes especificar o documento, a norma ou o ambito?",
                "Ich brauche eine Praezisierung, um in den richtigen Quellen zu suchen. Kannst du Dokument, Norm oder Umfang nennen?",
                "Ho bisogno di una precisazione per cercare nelle fonti giuste. Puoi specificare documento, standard o ambito?");
        }

        return string.Empty;
    }

    private static bool ShouldPreferSourceBackedAnswerOverBackendClarification(ToolResults toolResults, string? query)
    {
        var queryText = query ?? string.Empty;
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(queryText);
        if (string.IsNullOrWhiteSpace(intentQuery)
            || LooksLikeExactPassageOrCitationRequest(intentQuery)
            || LooksLikeStrictCertificationOrExactProofRequest(intentQuery)
            || LooksLikeCorpusClaimVerificationRequest(intentQuery)
            || LooksLikeSourceBackedCountdownPlanningRequest(intentQuery)
            || LooksLikeSourceBackedVerificationChecklistRequest(intentQuery))
        {
            return false;
        }

        var hasActionOrExactIntent =
            LooksLikeSourceBackedActionRequest(intentQuery)
            || LooksLikeComparativeDocumentaryRequest(intentQuery)
            || LooksLikeSourceBackedAdaptationRequest(intentQuery)
            || LooksLikeDocumentaryContentRequest(intentQuery)
            || ShouldUseSourceBackedExtractiveAnswer(intentQuery, toolResults);
        var hasBroadSynthesisIntent =
            IsBroadenedSourceSearchConfirmationEnvelope(queryText)
            || ShouldOfferBroadenedSourceSearch(intentQuery)
            || LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
            || LooksLikeGenericCollectionOrListRequest(intentQuery)
            || LooksLikeBroadSynthesisRequestShape(intentQuery)
            || LooksLikeBroadSourceBackedCompositionRequest(intentQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(intentQuery)
            || LooksLikeSoftChoiceRecommendationRequest(intentQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(intentQuery)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery);
        if (!hasActionOrExactIntent && !hasBroadSynthesisIntent)
            return false;

        var hits = SelectSourceBackedExtractiveHits(toolResults, intentQuery, maxHits: 5).ToList();
        if (hits.Count == 0)
        {
            hits = EnumerateRagHitSummaries(toolResults)
                .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .Take(5)
                .ToList();
        }

        if (hits.Count == 0)
            return false;

        if (IsBroadenedSourceSearchConfirmationEnvelope(queryText))
            return true;

        if (hasBroadSynthesisIntent)
        {
            if (ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, intentQuery)
                || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, intentQuery)
                || ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, intentQuery))
            {
                return true;
            }

            var broadCoverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, intentQuery);
            if (broadCoverage.UsableHitCount > 0
                && (broadCoverage.RichEvidenceCount > 0
                    || broadCoverage.DistinctSourcePageCount >= 2
                    || broadCoverage.DistinctDocumentCount >= 2))
            {
                return true;
            }
        }

        if (LooksLikeSourceBackedActionRequest(intentQuery))
            return true;

        var requestedTitle = TryExtractRequestedItemTitle(intentQuery);
        if (!string.IsNullOrWhiteSpace(requestedTitle)
            && RagFallbackHasAtLeastOneQueryAnchor(hits, requestedTitle!))
        {
            return true;
        }

        if (LooksLikeSourceBackedActionRequest(intentQuery))
        {
            foreach (var actionQuery in BuildSourceBackedActionRetrievalQueries(intentQuery).Take(4))
            {
                if (RagFallbackHasAtLeastOneQueryAnchor(hits, actionQuery))
                    return true;
            }
        }

        return RagFallbackHasAtLeastOneQueryAnchor(hits, intentQuery);
    }

}
