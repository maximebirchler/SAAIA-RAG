using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string? TryExtractRequestedItemTitle(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        if (LooksLikeUnresolvedSourceBackedDeicticFollowup(s))
            return null;

        if (LooksLikeBroadSourceBackedCompositionRequest(s))
            return null;

        if (LooksLikeGenericCollectionOrOptionRequestWithoutExactTitle(s))
            return null;

        var explicitPdfTitle = TryExtractPdfFileNameRequestedTitle(s);
        if (!string.IsNullOrWhiteSpace(explicitPdfTitle))
            return explicitPdfTitle;

        var availableItemRequest = Regex.Match(
            s,
            @"(?i)\b(?:j['\u2019]ai|je\s+dispose\s+de|i\s+have|tengo|tenho|ich\s+habe|ho)\s+(?:du|de\s+la|de\s+l['\u2019]|des|un|une|some|a|an|el|la|los|las|o|a|os|as|ein|eine|einen|del|della|dei|delle)?\s*(?<title>[\p{L}0-9'\u2019 \-]{3,70}?)(?:[,;?.!]|$).{0,100}\b(?:tu\s+as|vous\s+avez|as[-\s]?tu|avez[-\s]?vous|do\s+you\s+have|can\s+you|could\s+you|puedes|podes|pode|kannst|puoi|hai|hast)\b",
            RegexOptions.CultureInvariant);
        if (availableItemRequest.Success)
        {
            var title = CleanupRequestedItemTitle(availableItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var quoted = Regex.Match(s, "[\\u00ab\"'`](?<title>[^\\u00bb\"'`]{3,90})[\\u00bb\"'`]", RegexOptions.CultureInvariant);
        if (quoted.Success)
        {
            if (LooksLikeCorpusClaimVerificationRequest(s))
                return null;

            var title = CleanupQuotedRequestedItemTitle(quoted.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
            {
                if (LooksLikeShortTechnicalEvidenceTopic(title) && !LooksLikeCompactTechnicalIdentifier(title))
                    return null;

                return title;
            }
        }

        if (LooksLikeShortTechnicalEvidenceTopic(s))
            return null;

        if (LooksLikeSoftChoiceRecommendationRequest(s))
            return null;

        var actionConnectorTarget = Regex.Match(
            s,
            @"(?i)(?:^|[,.!?;]\s*)(?:\b(?:peux(?:[-\s]+tu)?|pourrais(?:[-\s]+tu)?|tu\s+peux|vous\s+pouvez|can\s+you|could\s+you)\s+(?:me\s+|m['\u2019]|nous\s+)?)?(?:donner|donne|donnes|donnez|montrer|montre|montres|montrez|afficher|affiche|affiches|affichez|faire|fais|faites|give|show|make|mostrar|hacer|haz|fazer|mostra|machen|zeigen|fare)\b[^:?.!,;]{0,70}?\b(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|Ã¼ber|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (actionConnectorTarget.Success)
        {
            var title = CleanupRequestedItemTitle(actionConnectorTarget.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var metricForItemRequest = Regex.Match(
            s,
            @"(?i)\b(?:combien|quel|quelle|quels|quelles|what|which|how)\b.+?\b(?:pour|for|sur|about|on)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)?\s*(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (metricForItemRequest.Success)
        {
            var title = CleanupRequestedItemTitle(metricForItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var politeItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:peux(?:[-\s]+tu)?|pourrais(?:[-\s]+tu)?|tu\s+peux|vous\s+pouvez|can\s+you|could\s+you)\s+(?:me\s+|m['\u2019]|nous\s+)?(?:donner|montrer|afficher|chercher|trouver|faire|give|show|find|make)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match politeItemRequest in politeItemRequests)
        {
            var title = CleanupRequestedItemTitle(politeItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var leadingPurposeItem = Regex.Match(
            s,
            @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{4,70}?)(?:\s+(?:pour|for|para|per)\s+[^:?.!,;]{2,90})(?:[:?.!,;]|$)",
            RegexOptions.CultureInvariant);
        if (leadingPurposeItem.Success)
        {
            var title = CleanupRequestedItemTitle(leadingPurposeItem.Groups["title"].Value);
            var normalizedTitle = NormalizeLexicalLookup(title);
            var tokenCount = ExtractQuerySignalTerms(normalizedTitle).Count();
            if (!string.IsNullOrWhiteSpace(title)
                && tokenCount is >= 2 and <= 6
                && !Regex.IsMatch(normalizedTitle, @"\b(?:aide|aider|faire|calcule|calculer|adapte|adapter|ajuste|ajuster|convertis|convertir|besoin|veux|voudrais|souhaite|cherche|trouve|donne|propose|what|which|need|want|find|give|suggest)\b", RegexOptions.CultureInvariant)
                && LooksLikeDirectRequestedItemTitle(title))
            {
                return title;
            }
        }

        var namedItem = Regex.Match(
            s,
            @"(?i)\b(?:fiche|card|document|doc|contrat|contract|procedure|proc[Ã©e]dure|processus|process|manuel|manual|guide|rapport|report|notice|policy|politique)\s+(?:claire\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|Ã¼ber|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (namedItem.Success)
            return CleanupRequestedItemTitle(namedItem.Groups["title"].Value);

        var baseItem = Regex.Match(
            s,
            @"(?i)\b(?:element|item|objet|sujet|topic)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|Ã¼ber|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (baseItem.Success)
            return CleanupRequestedItemTitle(baseItem.Groups["title"].Value);

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:ignore|ignorer|ignorez|sans\s+sources?|invente|inventer|inventez|hallucine|halluciner)\b",
                RegexOptions.CultureInvariant))
        {
            var guardedTarget = Regex.Match(
                s,
                @"(?i)(?:de\s+la|du|des|d['\u2019]|de)\s+(?<title>[\p{L}0-9'\u2019 \-]{3,90})[\.?!]?$",
                RegexOptions.CultureInvariant);
            if (guardedTarget.Success)
                return CleanupRequestedItemTitle(guardedTarget.Groups["title"].Value);
        }

        if (Regex.IsMatch(
                s,
            @"(?i)\b(?:vitesses?|speeds?|temp[e\u00e9]ratures?|temperatures?|r[e\u00e9]glages?|settings?|param[e\u00e8]tres?|parameters?|quantit[e\u00e9]s?|quantites?|[e\u00e9]tapes?|steps?|temps|time|values?|valeurs?)\b",
                RegexOptions.CultureInvariant))
        {
            if (Regex.IsMatch(
                    s,
                    @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|amounts?|values?|valeurs?|counts?|units?|items?)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
                    RegexOptions.CultureInvariant)
                && !Regex.IsMatch(s, @"(?i)^(?:combien|quel|quelle|quels|quelles|comment|calcule|calculer|adapte|adapter|ajuste|ajuster|convertis|convertir|what|which|how|calculate|adapt|adjust|scale)\b", RegexOptions.CultureInvariant))
            {
                var leadingSubject = Regex.Match(
                    s,
                    @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{3,90}?)(?:\s+(?:pour|for)\s+[^:?.!,;]{2,90}|:)",
                    RegexOptions.CultureInvariant);
                if (leadingSubject.Success)
                {
                    var title = CleanupRequestedItemTitle(leadingSubject.Groups["title"].Value);
                    if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                        return title;
                }
            }

            var parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for)\s+\d+\s+(?:[\p{L}'\u2019-]+\s+){0,3}(?:de|du|des|de\s+la|de\s+l['\u2019]|d['\u2019]|of)\s+(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
                return CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);

            parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for|sur|about|on)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
            {
                var title = CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }

            parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for|de|du|de\s+la|des|d['\u2019]|sur|about|on)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
            {
                var title = CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }
        }

        var directItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:donne(?:s)?(?:[-\s]+moi)?|montre(?:[-\s]+moi)?|affiche(?:[-\s]+moi)?|cherche|recherche|trouve|adapte|adapter|ajuste|ajuster|convertis|convertir|calcule|calculer|find|search|give(?:\s+me)?|show(?:\s+me)?|adapt|adjust|scale|resize)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match directItemRequest in directItemRequests)
        {
            var title = CleanupRequestedItemTitle(directItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var directSearchRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+cherche|je\s+recherche|i\s+(?:am\s+)?(?:looking\s+for|searching\s+for))\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match directSearchRequest in directSearchRequests)
        {
            var title = CleanupRequestedItemTitle(directSearchRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var wantedExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+(?:veux|voudrais|souhaite)|j['\u2019]aimerais|i\s+(?:want|would\s+like|need)|quiero|quisiera|quero|gostaria|ich\s+(?:will|moechte|mÃ¶chte|brauche)|vorrei)\s+(?:trouver\s+|retrouver\s+|avoir\s+|voir\s+|find\s+|get\s+|see\s+|ver\s+|encontrar\s+)?(?:le|la|les|l['\u2019]|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match wantedExactItemRequest in wantedExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(wantedExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var needExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:il\s+me\s+faut|j['\u2019]ai\s+besoin\s+de|i\s+need|necesito|preciso\s+de|ich\s+brauche|ho\s+bisogno\s+di)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match needExactItemRequest in needExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(needExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var doingExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+(?:fais|prepare|prÃ©pare)|nous\s+(?:faisons|preparons|prÃ©parons)|i\s+(?:am\s+)?(?:making|preparing)|we\s+(?:are\s+)?(?:making|preparing))\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90}?)(?:\s+(?:avec|pour|for|with)\b|[:?.!,;]|$)",
            RegexOptions.CultureInvariant);
        foreach (Match doingExactItemRequest in doingExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(doingExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var whatIsExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:c['\u2019]est\s+quoi|qu['\u2019]est\s+ce\s+que|qu['\u2019]est-ce\s+que|what\s+is|what['\u2019]?s|que\s+es|o\s+que\s+e|was\s+ist|cos['\u2019]e|cosa\s+e)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some|el|los|las|o|a|os|as|der|die|das|il|lo|gli)?\s*(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match whatIsExactItemRequest in whatIsExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(whatIsExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var quantitySubject = Regex.Match(
            s,
            @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|values?|valeurs?)\s+(?:pour|for)\s+\d+\s+(?:[\p{L}'\u2019-]+\s+){0,3}(?:de|du|des|de\s+la|de\s+l['\u2019]|d['\u2019]|of)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (quantitySubject.Success)
        {
            var title = CleanupRequestedItemTitle(quantitySubject.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|amounts?|values?|valeurs?|counts?|units?|items?)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
                RegexOptions.CultureInvariant))
        {
            var leadingSubject = Regex.Match(
                s,
                @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{3,90}?)(?:\s+(?:pour|for)\s+[^:?.!,;]{2,90}|:)",
                RegexOptions.CultureInvariant);
            if (leadingSubject.Success)
            {
                var title = CleanupRequestedItemTitle(leadingSubject.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }
        }

        return null;
    }

    private static bool LooksLikeGenericCollectionOrOptionRequestWithoutExactTitle(string? query)
    {
        if (LooksLikeGenericCollectionOrListRequest(query))
            return true;

        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        if (!Regex.IsMatch(
                s,
                @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|opti(?:on|ons)|suggest(?:ion|ions)?|idee|idees|idea|ideas|choix|choice|choices|auswahl|scelta)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var collectionTargets = Regex.Matches(
                s,
                @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|options?|suggestions?|idees?|ideas?|choix|choices?)\b\s+(?:de|des|d['\u2019]?|du|de\s+la|de\s+l['\u2019]?|of|for|pour|sobre|su)\s+(?<target>[^:?.!,;]{2,90})",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => CleanupRequestedItemTitle(match.Groups["target"].Value))
            .Where(static target => !string.IsNullOrWhiteSpace(target))
            .ToArray();

        if (collectionTargets.Any(target => LooksLikeGenericCollectionTarget(target, s)))
            return true;

        var qualifiedListTarget = Regex.Match(
            s,
            @"\b(?:donne|donner|montre|montrer|trouve|trouver|cherche|chercher|give|show|find|suggest|propose|proposer)\b.{0,80}\b(?:liste|list|selection|s[e\u00e9]lection|options?|suggestions?|idees?|ideas?)\b.{0,40}\b(?:de|des|d['\u2019]?|of|for)\s+(?<target>[^:?.!,;]{2,90})",
            RegexOptions.CultureInvariant);
        return qualifiedListTarget.Success
            && LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(qualifiedListTarget.Groups["target"].Value), s);
    }

    private static bool LooksLikeGenericCollectionOrListRequest(string? query)
    {
        var raw = CollapseWhitespace(query ?? string.Empty);
        var s = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractPdfFileNameRequestedTitle(raw)))
            return false;

        if (Regex.IsMatch(raw, "[\\u00ab\"'`](?<title>[^\\u00bb\"'`]{3,90})[\\u00bb\"'`]", RegexOptions.CultureInvariant))
            return false;

        var hasCollectionCue = Regex.IsMatch(
            s,
            @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|options?|suggestions?|idees?|ideas?|choix|choices?|disponibles?|available|existent|exist|existe|trouve(?:s|es)?|found|corpus|documents?|docs?|sources?)\b",
            RegexOptions.CultureInvariant);
        if (!hasCollectionCue)
            return false;

        if (LooksLikeMultipleCandidateSynthesisRequest(query)
            && !Regex.IsMatch(s, @"\b(?:liste|lister|list|listing)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                s,
                @"\b(?:quels|quelles|quel|quelle|which|what|cuales|cuais|welche|quali)\s+(?:documents?|docs?|sources?|items?|elements?|entries?|procedures?|proc[e\u00e9]dures?)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                s,
                @"\b(?:documents?|docs?|sources?|corpus|dossier|category|categorie|cat[e\u00e9]gorie)\b.{0,40}\b(?:items?|elements?|entries?|procedures?|proc[e\u00e9]dures?|documents?|sources?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var articleCollection = Regex.Match(
            s,
            @"\b(?:donne|donner|montre|montrer|trouve|trouver|cherche|chercher|propose|proposer|suggest|give|show|find|list|lister)\b.{0,70}\b(?:les|des|some|the|los|las|os|as|die|gli)\s+(?<target>[\p{L}'\u2019 \-]{3,70}?)\s+(?:disponibles?|available|exist(?:ent|s)?|existe(?:nt)?|trouve(?:s|es)?|found|dans|in|du|de\s+la|des|of|from)\b",
            RegexOptions.CultureInvariant);
        if (articleCollection.Success
            && LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(articleCollection.Groups["target"].Value), s))
        {
            return true;
        }

        var questionCollection = Regex.Match(
            s,
            @"\b(?:quels|quelles|quel|quelle|which|what|cuales|cuais|welche|quali)\s+(?<target>[\p{L}'\u2019 \-]{3,70}?)\s+(?:sont|sont\s+disponibles|existent|exist|available|disponibles?|dans|in|du|de\s+la|des|of|from)\b",
            RegexOptions.CultureInvariant);
        if (questionCollection.Success
            && LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(questionCollection.Groups["target"].Value), s))
        {
            return true;
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|options?|suggestions?|idees?|ideas?|choix|choices?)\b\s+(?:de|des|d['\u2019]?|du|de\s+la|de\s+l['\u2019]?|of|for|pour|sobre|su)?\s*(?<target>[^:?.!,;]{2,90})",
                     RegexOptions.CultureInvariant))
        {
            if (LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(match.Groups["target"].Value), s))
                return true;
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"\b(?:donne|donner|montre|montrer|trouve|trouver|cherche|chercher|propose|proposer|suggest|give|show|find|list|lister)\b.{0,60}\b(?:les|des|some|the|los|las|os|as|die|gli)\s+(?<target>[^:?.!,;]{2,70}?)(?:\s+(?:disponibles?|available|exist(?:ent|s)?|existe(?:nt)?|trouve(?:s|es)?|found|dans|in|du|de\s+la|des|of|from)\b|[?.!,;]|$)",
                     RegexOptions.CultureInvariant))
        {
            if (LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(match.Groups["target"].Value), s))
                return true;
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"\b(?:quels|quelles|quel|quelle|which|what|cuales|cuais|welche|quali)\s+(?<target>[^:?.!,;]{2,70}?)(?:\s+(?:sont|sont\s+disponibles|existent|exist|available|disponibles?|dans|in|du|de\s+la|des|of|from)\b|[?.!,;]|$)",
                     RegexOptions.CultureInvariant))
        {
            if (LooksLikeGenericCollectionTarget(CleanupRequestedItemTitle(match.Groups["target"].Value), s))
                return true;
        }

        return false;
    }

    private static bool TryExtractGenericCollectionTarget(string? query, out string target)
    {
        target = string.Empty;
        var raw = CollapseWhitespace(query ?? string.Empty);
        var s = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(s)
            || !string.IsNullOrWhiteSpace(TryExtractPdfFileNameRequestedTitle(raw)))
        {
            return false;
        }

        foreach (var pattern in new[]
                 {
                     @"\b(?:liste|lister|list|listing|selection|s[e\u00e9]lection|seleccion|selecao|options?|suggestions?|idees?|ideas?|choix|choices?)\b\s+(?:de|des|d['\u2019]?|du|de\s+la|de\s+l['\u2019]?|of|for|pour|sobre|su)?\s*(?<target>[^:?.!,;]{2,90})",
                     @"\b(?:donne|donner|montre|montrer|trouve|trouver|cherche|chercher|propose|proposer|suggest|give|show|find|list|lister)\b.{0,70}\b(?:les|des|some|the|los|las|os|as|die|gli)\s+(?<target>[^:?.!,;]{2,70}?)(?:\s+(?:disponibles?|available|exist(?:ent|s)?|existe(?:nt)?|trouve(?:s|es)?|found|dans|in|du|de\s+la|des|of|from)\b|[?.!,;]|$)",
                     @"\b(?:quels|quelles|quel|quelle|which|what|cuales|cuais|welche|quali)\s+(?<target>[^:?.!,;]{2,70}?)(?:\s+(?:sont|sont\s+disponibles|existent|exist|available|disponibles?|dans|in|du|de\s+la|des|of|from)\b|[?.!,;]|$)"
                 })
        {
            foreach (Match match in Regex.Matches(s, pattern, RegexOptions.CultureInvariant))
            {
                var candidate = CleanupRequestedItemTitle(match.Groups["target"].Value);
                if (LooksLikeGenericCollectionTarget(candidate, s))
                {
                    target = candidate!;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeGenericCollectionTarget(string? target, string normalizedQuery)
    {
        var normalizedTarget = NormalizeLexicalLookup(target);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return false;

        if (normalizedTarget.Contains('.', StringComparison.Ordinal)
            || Regex.IsMatch(normalizedTarget, @"\b(?:pdf|docx?|xlsx?|pptx?|csv|txt|md)\b", RegexOptions.CultureInvariant)
            || LooksLikeCompactTechnicalIdentifier(normalizedTarget))
        {
            return false;
        }

        var terms = ExtractQuerySignalTerms(normalizedTarget).Take(5).ToArray();
        if (terms.Length is < 1 or > 4)
            return false;

        if (terms.All(IsGenericCollectionTargetFieldNoise))
            return false;

        if (terms.All(static term => IsGenericOptionSurfaceTerm(term) || IsGenericCollectionTargetFieldNoise(term)))
            return false;

        if (terms.Any(static term => term.Length <= 1 || Regex.IsMatch(term, @"\d", RegexOptions.CultureInvariant)))
            return false;

        if (terms.Length == 1)
            return true;

        var hasScopeCue = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:disponibles?|available|trouve(?:s|es)?|found|sources?|documents?|docs?|corpus|dossier|category|categorie|cat[e\u00e9]gorie)\b",
            RegexOptions.CultureInvariant);
        var hasClassLikeEnding = terms.All(static term =>
            term.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("es", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("en", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("i", StringComparison.OrdinalIgnoreCase));

        return hasScopeCue || hasClassLikeEnding;
    }

    private static bool IsGenericCollectionTargetFieldNoise(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        return normalized is "source" or "sources" or "page" or "pages" or "document" or "documents"
            or "details" or "detail" or "info" or "infos" or "information" or "informations"
            or "etape" or "etapes" or "step" or "steps"
            or "temps" or "time" or "duration" or "duree" or "quantite" or "quantites"
            or "quantity" or "quantities" or "valeur" or "valeurs" or "value" or "values"
            or "extrait" or "extraits" or "excerpt" or "excerpts"
            or "partir" or "part" or "from" or "available" or "disponible" or "disponibles";
    }

    private static bool IsGenericOptionSurfaceTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        return normalized is "option" or "options" or "suggestion" or "suggestions"
            or "idee" or "idees" or "idea" or "ideas" or "choix" or "choice" or "choices"
            or "utile" or "utiles" or "useful" or "available" or "disponible" or "disponibles"
            or "plusieurs" or "multiple" or "multiples" or "several" or "many";
    }

    private static string? TryExtractPdfFileNameRequestedTitle(string? query)
    {
        var s = NormalizeDocumentFileExtractionInput(query);
        if (s.Length == 0 || !s.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        var quoted = Regex.Matches(
                s,
                @"(?i)(?:`|""|\u00ab|\u201c)(?<title>[^`""\u00ab\u00bb\u201c\u201d]{2,300}?\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?:`|""|\u00bb|\u201d)",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => CleanupExplicitDocumentFileTitle(match.Groups["title"].Value))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .OrderByDescending(static title => title!.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(quoted))
            return quoted;

        var introduced = Regex.Matches(
                s,
                @"(?i)\b(?:de|du|des|d['\u2019]|dans|sur|pour|concernant|from|of|for|in|about|regarding|sobre|ueber|Ã¼ber|su)\s+[`""'\u00ab\u201c]?(?<title>[\p{L}\p{N}][^?;`""\u00ab\u00bb\u201c\u201d]{2,260}?\.pdf)[`""'\u00bb\u201d]?\b",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => CleanupExplicitDocumentFileTitle(match.Groups["title"].Value))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title!);

        var bare = Regex.Matches(
                s,
                @"(?i)[`""'\u00ab\u201c]?(?<title>[\p{L}\p{N}][\p{L}\p{N}'\u2019 .,+_()&/\-\u2010-\u2015]{2,260}?\.pdf)[`""'\u00bb\u201d]?\b",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => CleanupExplicitDocumentFileTitle(match.Groups["title"].Value))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title!);

        return FilterExplicitDocumentFileTitleCandidates(introduced.Concat(bare)).FirstOrDefault();
    }

    private static int CountExplicitDocumentFileReferences(string? query)
        => ExtractExplicitDocumentFileReferenceQueries(query).Count;

    private static IReadOnlyList<string> ExtractExplicitDocumentFileReferenceQueries(string? query)
    {
        var s = NormalizeDocumentFileExtractionInput(query);
        if (s.Length == 0)
            return Array.Empty<string>();

        var candidates = new List<string>();
        var introducedPdfTitle = TryExtractPdfFileNameRequestedTitle(query);
        if (!string.IsNullOrWhiteSpace(introducedPdfTitle))
            candidates.Add(introducedPdfTitle!);

        candidates.AddRange(Regex.Matches(
                s,
                @"(?i)(?<title>[\p{L}\p{N}][\p{L}\p{N}'\u2019 .,+_()&/\-\u2010-\u2015]{2,260}?\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv))\b",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => CleanupExplicitDocumentFileTitle(match.Groups["title"].Value))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title!));

        return FilterExplicitDocumentFileTitleCandidates(candidates);
    }

    private static IReadOnlyList<string> FilterExplicitDocumentFileTitleCandidates(IEnumerable<string> candidates)
    {
        var distinctCandidates = candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .GroupBy(NormalizeDocumentLookupText, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        return distinctCandidates
            .Where(candidate => !distinctCandidates.Any(other =>
                !string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase)
                && ((IsSuffixDocumentFileTitle(other, candidate)
                        && !ShouldPreferSuffixDocumentFileTitle(other, candidate))
                    || (IsSuffixDocumentFileTitle(candidate, other)
                        && ShouldPreferSuffixDocumentFileTitle(candidate, other)))))
            .ToArray();
    }

    private static bool IsSuffixDocumentFileTitle(string longer, string shorter)
    {
        var longerCollapsed = CollapseWhitespace(longer);
        var shorterCollapsed = CollapseWhitespace(shorter);
        if (shorterCollapsed.Length == 0 || longerCollapsed.Length <= shorterCollapsed.Length + 2)
            return false;

        return longerCollapsed.EndsWith(shorterCollapsed, StringComparison.OrdinalIgnoreCase)
            || NormalizeDocumentLookupText(longerCollapsed).EndsWith(NormalizeDocumentLookupText(shorterCollapsed), StringComparison.Ordinal);
    }

    private static bool ShouldPreferSuffixDocumentFileTitle(string longer, string suffix)
    {
        var longerCollapsed = CollapseWhitespace(longer);
        var suffixCollapsed = CollapseWhitespace(suffix);
        if (!longerCollapsed.EndsWith(suffixCollapsed, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = longerCollapsed[..^suffixCollapsed.Length].Trim();
        if (prefix.Length == 0)
            return false;

        return Regex.IsMatch(
                prefix,
                @"(?i)\b(?:affiche|afficher|combien|comment|donne|donner|extrais|extraire|explique|expliquer|fais|faire|montre|montrer|prepare|preparer|pr[e\u00e9]pare|pr[e\u00e9]parer|quel|quelle|quels|quelles|resume|r[e\u00e9]sume|resumer|r[e\u00e9]sumer|retrouve|retrouver|show|what|which|how|give|extract|explain|prepare|summarize|find)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                prefix,
                @"(?i)\b(?:de|du|des|d['\u2019]|dans|sur|pour|concernant|from|of|for|in|about|regarding|sobre|ueber|Ã¼ber|su)\s*$",
                RegexOptions.CultureInvariant);
    }

    private static string? CleanupExplicitDocumentFileTitle(string? value)
    {
        var title = NormalizeDocumentFileExtractionInput(value)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';', '"', '\'', '`', '\u00ab', '\u00bb', '\u201c', '\u201d');
        if (title.Length == 0)
            return null;

        title = Regex.Replace(
            title,
            @"(?i)^(?:compare|comparer|comparaison|comparatif|comparative|confronta|confrontare|vergleiche|vergleichen|paragona|paragonare)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:et|and|y|e|und|vs|versus)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = TrimLeadingExplicitDocumentRequestWrapper(title);

        title = Regex.Replace(
            title,
            @"(?i)^(?:dans|sur|pour|concernant|from|of|for|in|about|regarding|sobre|ueber|Ã¼ber|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim(' ', ':', '-', '.', '?', '!', ',', ';', '"', '\'', '`', '\u00ab', '\u00bb', '\u201c', '\u201d');

        return Regex.IsMatch(title, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? title
            : null;
    }

    private static string TrimLeadingExplicitDocumentRequestWrapper(
        string title)
    {
        const string pattern =
            @"(?ix)^
              (?:
                (?:please|bitte|por\s+favor|per\s+favore)\s+
              )?
              (?:
                affiche|afficher|cherche|chercher|trouve|trouver|retrouve|retrouver|
                ouvre|ouvrir|lis|lire|montre|montrer|r[e\u00e9]sume|r[e\u00e9]sumer|
                find|locate|open|read|show|retrieve|display|summarize|
                \u00f6ffne|\u00f6ffnen|lies|lese|finde|zeige|
                abre|abrir|lee|leer|busca|encuentra|muestra|
                abra|leia|ler|procure|encontre|mostre|
                apri|aprire|leggi|leggere|trova|mostra
              )
              (?:-[\p{L}]+)?
              (?:\s+[\p{L}'\u2019-]{2,30}){0,4}?
              \s+
              (?:
                le|la|les|un|une|ce|cette|
                the|a|an|this|that|
                der|die|das|den|ein|eine|einen|
                el|los|las|o|os|as|um|uma|
                il|lo|i|gli
              )
              \s+
              (?:
                fiche|fichier|file|document|doc|pdf|
                datei|dokument|archivo|ficheiro|documento
              )
              \s*[:\-]?\s+";

        return Regex.Replace(
                title,
                pattern,
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
    }

    private static string NormalizeDocumentFileExtractionInput(string? value)
        => Regex.Replace(value ?? string.Empty, @"[\r\n\t]+", " ").Trim();

    private static bool LooksLikeSoftChoiceRecommendationRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:vitesses?|speeds?|temperatures?|temperatures?|reglages?|settings?|parametres?|parameters?|quantites?|quantites?|etapes?|steps?|temps|time|values?|valeurs?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var hasChoiceCue = Regex.IsMatch(
            normalized,
            @"\b(?:choisir|choisis|choix|conseille|conseiller|recommande|recommander|propose|proposer|suggest|suggestion|recommend|choose|select|choice|which|what|quel|quelle|quels|quelles|cual|cuales|que|elegir|elige|escoger|escoge|recomienda|recomendar|aconseja|aconsejar|propone|proponer|sugiere|sugerir|qual|quais|escolher|escolhe|recomenda|recomendar|aconselha|aconselhar|propoe|propor|sugere|sugerir|welche|welcher|welches|was|waehlen|waehle|wahlen|wahle|empfiehl|empfehlen|rate|raten|vorschlag|quale|quali|cosa|scegliere|scegli|consiglia|consigliare|proponi|proporre|suggerisci|suggerire)\b",
            RegexOptions.CultureInvariant);
        if (!hasChoiceCue)
            return false;

        var hasRequestedOptionKind = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|qual|welche|welcher|welches|quale)\s+[\p{L}][\p{L}'\u2019-]{2,30}\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:option|options|idee|idees|idea|ideas|item|items|element|elements|solution|solutions|methode|methodes|method|methods|approche|approaches|opcion|opciones|opcao|opcoes|solucion|soluciones|solucao|solucoes|alternativa|alternativas|auswahl|vorschlag|vorschlaege|vorschlage|losung|loesung|losungen|loesungen|opzione|opzioni|scelta|scelte|soluzione|soluzioni|alternative)\b",
                RegexOptions.CultureInvariant);
        if (!hasRequestedOptionKind)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:pour|for|para|per|avec|with|con|com|mit|adapte|adaptee|adapted|adaptado|adaptada|adequado|adequada|adatto|adatta|geeignet|passend|suitable|compatible|compatibile|compatibel|conseille|recommend|recommendation|recomienda|recomendar|recomenda|recomendar|empfiehl|empfehlen|consiglia|consigliare)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeBroadSourceBackedCompositionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:document|fiche|card|procedure|process)\s+(?:de|du|des|pour|about|on)\b", RegexOptions.CultureInvariant))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|which|what|cual|qual|welche|welcher|welches|quale)\s+(?:option|opcion|opcao|opzione)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var hasBroadIntent = Regex.IsMatch(
            normalized,
            @"\b(?:plan\s+complet|planning\s+complet|complete\s+plan|composition|compose|composer|propose|proposes|options?|idees?|suggestions?|selection|quoi\s+faire|what\s+to\s+use|which\s+option|proponer|propone|sugerir|sugiere|recomendar|recomienda|opciones|ideas|seleccion|composicion|o\s+que\s+fazer|propor|propoe|sugere|sugerir|opcoes|ideias|selecao|composicao|vorschlag|vorschlaege|vorschlage|empfehlen|optionen|ideen|auswahl|zusammenstellen|proponi|proporre|suggerisci|suggerire|opzioni|idee|scelta|composizione)\b",
            RegexOptions.CultureInvariant);

        return hasBroadIntent
            || Regex.IsMatch(
                normalized,
                @"\b(?:plan|planning|programme|program|selection|composition|programa|seleccion|composicion|plano|selecao|composicao|programm|auswahl|piano|programma|scelta|composizione)\s+(?:de|du|des|pour|for|about|para|per|fuer|fur)\b.*\b(?:avec|with|con|com|mit)\b",
                RegexOptions.CultureInvariant);
    }

    private static string? CleanupRequestedItemTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';');
        if (title.Length == 0)
            return null;
        var originalTitle = title;

        if (title.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            title = Regex.Replace(
                title,
                @"(?i)^(?:compare|comparer|comparaison|comparatif|comparative|confronta|confrontare|vergleiche|vergleichen|paragona|paragonare)\s+",
                string.Empty,
                RegexOptions.CultureInvariant).Trim();
        }

        title = Regex.Replace(
            title,
            @"(?i)^(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|methode|method)\s+(?:claire\s+|detaillee\s+|detaill[eÃ©]e\s+|complete\s+|compl[eÃ¨]te\s+|sourcee\s+|sourc[eÃ©]e\s+|clear\s+|detailed\s+)?(?:de\s+base\s+|base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|Ã¼ber|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();


        title = Regex.Replace(
            title,
            @"(?i)\s+(?:en\s+mode|mode|version|variante|pour\s+(?:\d+|un|une|des|le|la|les|l['\u2019]|the|a|an|some)\b|dans\s+(?:le|la|les|l['\u2019]|un|une|des|the|a|an)\b|du\s+(?:guide|pdf|document|manuel|livre|book|manual|file|document|corpus|dossier)\b|de\s+la\s+(?:base|page|fiche|notice|section)\b|des\s+(?:sources|documents|docs|fichiers|files)\b).*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        title = Regex.Replace(
            title,
            @"(?i)^(?:[\p{L}][\p{L}'\u2019-]{2,24}\s+){1,4}(?:de\s+base\s+|base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|Ã¼ber|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:element|item|objet|sujet|topic)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|Ã¼ber|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^base\s+(?:des|du|de\s+la|de\s+l['\u2019]|de|pour|about|on|d['\u2019]|sur|sobre|ueber|Ã¼ber|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:etapes?|[Ã©e]tapes?|steps?|temps|time|source|sources|quantites?|values?|valeurs?|reglages?|r[Ã©e]glages?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:disponibles?|available|exist(?:ent|s)?|existe(?:nt)?|trouve(?:s|es)?|found)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        var preservedTitle = CleanupQuotedRequestedItemTitle(originalTitle);
        if (!string.IsNullOrWhiteSpace(preservedTitle)
            && LooksLikeConnectorInsideNaturalTitle(preservedTitle)
            && !LooksLikeGenericTitleDescriptorPrefix(preservedTitle)
            && LooksLikeDirectRequestedItemTitle(preservedTitle))
        {
            title = preservedTitle;
        }

        return title.Length >= 3 ? title : null;
    }

    private static string? CleanupQuotedRequestedItemTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';', '"', '\'', '\u00ab', '\u00bb');
        if (title.Length == 0)
            return null;

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:en\s+mode|mode|version|variante|pour\s+(?:\d+|un|une|des|le|la|les|l['\u2019]|the|a|an|some)\b|dans\s+(?:le|la|les|l['\u2019]|un|une|des|the|a|an)\b|du\s+(?:guide|pdf|document|manuel|livre|book|manual|file|document|corpus|dossier)\b|de\s+la\s+(?:base|page|fiche|notice|section)\b|des\s+(?:sources|documents|docs|fichiers|files)\b).*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:etapes?|[Ã©e]tapes?|steps?|temps|time|source|sources|quantites?|values?|valeurs?|reglages?|r[Ã©e]glages?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        return title.Length >= 3 ? title : null;
    }

    private static bool LooksLikeConnectorInsideNaturalTitle(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"^[\p{L}\p{N}'\u2019-]{3,}(?:\s+[\p{L}\p{N}'\u2019-]{2,}){0,4}\s+(?:de|du|des|d|of|with|a|au|aux|al|alla|di|con|mit)\s+[\p{L}\p{N}'\u2019-]{3,}",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeGenericTitleDescriptorPrefix(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"^(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|methode|method|option|idee|idea|preparation|rapport|report|guide|manuel|manual|base)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDirectRequestedItemTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (normalized.Length < 3 || normalized.Length > 90)
            return false;

        if (Regex.IsMatch(normalized, @"^(?:corpus|documents?|sources?|fichiers?|files?|pdf)$", RegexOptions.CultureInvariant))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"^(?:quantites?|quantit[e\u00e9]s?|quantities?|amounts?|values?|valeurs?|counts?|units?)$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(normalized, @"^\d+\s+(?:items?|elements?|units?|pieces?)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d+\s+[\p{L}'\u2019.\-]{2,30}$", RegexOptions.CultureInvariant))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"^(?:que|qu|tu|vous|me|moi|mets?|mettre|put|documents?|sources?|fichiers?|files?|what|which|how|comment)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:ca|cela|ceci|this|that|it|eso|esto|isso|isto|das|questo|quello)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:documents?|sources?|fichiers?|files?)\s+(?:qui|that)\s+(?:parle|parlent|mentionne|mentionnent|traite|traitent|talk|mentions?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return ExtractQuerySignalTerms(normalized).Any()
            || ExtractRequestedTitleSignalTerms(normalized).Any();
    }
}
