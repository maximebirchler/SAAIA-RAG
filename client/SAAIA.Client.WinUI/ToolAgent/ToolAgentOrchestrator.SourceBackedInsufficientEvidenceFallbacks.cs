using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildBroadEvidenceStillInsufficientAnswer(
        string language,
        string originalQuery,
        string intentQuery,
        int nearbyHitCount,
        bool searchAlreadyExpanded = false)
    {
        language = NormalizeLanguageCode(language);
        var alreadyConfirmed = IsBroadenedSourceSearchConfirmationEnvelope(originalQuery);
        var answer = (language, nearbyHitCount > 0, searchAlreadyExpanded || alreadyConfirmed) switch
        {
            ("en", true, true) =>
                "The pages found are still too narrow for a solid answer. They give useful clues, but I need broader or more varied sources to produce something reliable.",
            ("en", true, false) =>
                "The pages found are too narrow for a solid answer. They give useful clues, but I need broader or more varied sources to produce something reliable.",
            ("en", false, _) =>
                "I have not found enough useful source material yet to answer reliably.",
            ("es", true, true) =>
                "Las pÃ¡ginas encontradas siguen siendo demasiado limitadas para una respuesta sÃ³lida. Dan pistas Ãºtiles, pero necesito fuentes mÃ¡s amplias o variadas para producir algo fiable.",
            ("es", true, false) =>
                "Las pÃ¡ginas encontradas son demasiado limitadas para una respuesta sÃ³lida. Dan pistas Ãºtiles, pero necesito fuentes mÃ¡s amplias o variadas para producir algo fiable.",
            ("es", false, _) =>
                "TodavÃ­a no he encontrado suficiente material fuente Ãºtil para responder de forma fiable.",
            ("pt", true, true) =>
                "As pÃ¡ginas encontradas continuam demasiado limitadas para uma resposta sÃ³lida. DÃ£o pistas Ãºteis, mas preciso de fontes mais amplas ou variadas para produzir algo fiÃ¡vel.",
            ("pt", true, false) =>
                "As pÃ¡ginas encontradas sÃ£o demasiado limitadas para uma resposta sÃ³lida. DÃ£o pistas Ãºteis, mas preciso de fontes mais amplas ou variadas para produzir algo fiÃ¡vel.",
            ("pt", false, _) =>
                "Ainda nÃ£o encontrei material fonte Ãºtil suficiente para responder de forma fiÃ¡vel.",
            ("de", true, true) =>
                "Die gefundenen Seiten sind weiterhin zu eng fÃ¼r eine belastbare Antwort. Sie geben nÃ¼tzliche Hinweise, aber ich brauche breitere oder vielfÃ¤ltigere Quellen.",
            ("de", true, false) =>
                "Die gefundenen Seiten sind zu eng fÃ¼r eine belastbare Antwort. Sie geben nÃ¼tzliche Hinweise, aber ich brauche breitere oder vielfÃ¤ltigere Quellen.",
            ("de", false, _) =>
                "Ich habe noch nicht genug nÃ¼tzliches Quellenmaterial gefunden, um zuverlÃ¤ssig zu antworten.",
            ("it", true, true) =>
                "Le pagine trovate sono ancora troppo limitate per una risposta solida. Offrono spunti utili, ma servono fonti piÃ¹ ampie o varie per produrre qualcosa di affidabile.",
            ("it", true, false) =>
                "Le pagine trovate sono troppo limitate per una risposta solida. Offrono spunti utili, ma servono fonti piÃ¹ ampie o varie per produrre qualcosa di affidabile.",
            ("it", false, _) =>
                "Non ho ancora trovato abbastanza materiale fonte utile per rispondere in modo affidabile.",
            (_, true, true) =>
                "Les pages trouv\u00e9es restent trop limit\u00e9es pour une r\u00e9ponse solide. Elles donnent des pistes utiles, mais il me faut des sources plus larges ou plus vari\u00e9es pour produire quelque chose de fiable.",
            (_, true, false) =>
                "Les pages trouv\u00e9es sont trop limit\u00e9es pour une r\u00e9ponse solide. Elles donnent des pistes utiles, mais il me faut des sources plus larges ou plus vari\u00e9es pour produire quelque chose de fiable.",
            _ =>
                "Je n'ai pas encore trouv\u00e9 assez d'\u00e9l\u00e9ments sources utiles pour r\u00e9pondre de mani\u00e8re fiable."
        };

        return alreadyConfirmed || searchAlreadyExpanded
            ? SuppressBroadenedSearchOfferIfAlreadyConfirmed(answer, originalQuery, language)
            : AppendBroadenedSearchOfferIfHelpful(answer, intentQuery, language);
    }

    private static bool LooksLikeBroadEvidenceStillInsufficientAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var normalized = NormalizeLooseLookup(answer);
        return (normalized.Contains("pages trouvees", StringComparison.Ordinal)
                && (normalized.Contains("trop limitees", StringComparison.Ordinal)
                    || normalized.Contains("restent trop limitees", StringComparison.Ordinal)))
            || normalized.Contains("pas encore trouve assez d elements sources utiles", StringComparison.Ordinal)
            || normalized.Contains("pages found are too narrow", StringComparison.Ordinal)
            || normalized.Contains("pages found are still too narrow", StringComparison.Ordinal)
            || normalized.Contains("not found enough useful source material", StringComparison.Ordinal)
            || (normalized.Contains("paginas encontradas", StringComparison.Ordinal)
                && normalized.Contains("demasiado limitadas", StringComparison.Ordinal))
            || normalized.Contains("suficiente material fuente util", StringComparison.Ordinal)
            || normalized.Contains("material fonte util suficiente", StringComparison.Ordinal)
            || (normalized.Contains("gefundenen seiten", StringComparison.Ordinal)
                && normalized.Contains("zu eng", StringComparison.Ordinal))
            || normalized.Contains("nicht genug nutzliches quellenmaterial", StringComparison.Ordinal)
            || (normalized.Contains("pagine trovate", StringComparison.Ordinal)
                && normalized.Contains("troppo limitate", StringComparison.Ordinal))
            || normalized.Contains("abbastanza materiale fonte utile", StringComparison.Ordinal);
    }

    private static bool ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
    {
        if (!LooksLikeBroadEvidenceStillInsufficientAnswer(answer))
            return false;

        if (string.IsNullOrWhiteSpace(query))
            return true;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery))
            return false;

        return true;
    }

    private static bool HasExpandedSourceBackedSearchEvidence(ToolResults toolResults)
    {
        var successfulRagItems = toolResults.Items.Count(static item =>
            item.ToolName is "rag.search" or "rag.multi_search"
            && string.IsNullOrWhiteSpace(item.Error));
        if (successfulRagItems > 1)
            return true;

        if (successfulRagItems <= 0)
            return false;

        return toolResults.Items.Any(static item =>
            string.IsNullOrWhiteSpace(item.Error)
            && item.ToolName is "documents.tree" or "documents.navigation" or "documents.context");
    }

    private static string SuppressBroadenedSearchOfferIfAlreadyConfirmed(string answer, string originalQuery, string language)
    {
        if (string.IsNullOrWhiteSpace(answer) || !IsBroadenedSourceSearchConfirmationEnvelope(originalQuery))
            return answer;

        var offer = DeterministicAgentText.SourceBackedExpandedSearchOffer(language);
        return answer
            .Replace($"{Environment.NewLine}{Environment.NewLine}{offer}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(offer, string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd();
    }
}
