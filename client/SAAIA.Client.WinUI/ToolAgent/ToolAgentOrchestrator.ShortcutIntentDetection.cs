using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;
using System.Diagnostics;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static readonly RegexOptions ShortcutRegexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static bool MatchesExplicitShortcut(string normalizedMessage, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(normalizedMessage, pattern, ShortcutRegexOptions))
                return true;
        }

        return false;
    }

    private static bool ContainsConversationalContentCue(string normalizedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        return Regex.IsMatch(normalizedMessage,
            @"\b(?:pourquoi|comment|peux\s+tu|peux-tu|pouvez\s+vous|explique(?: moi)?|de quoi parle|vue d ensemble|qu est ce que .* signifie|que signifie|what is|what does|why|can you|could you|would you|how\s+(?:do|does|did|can|could|would|to|is|are)|overview|useful|important|business\s+questions?|source\s+grounded|source-grounded|which\s+(?:pdfs?|documents?|sources?)|about this category|about this document|explain|meaning|worum geht|warum|wie\s+(?:funktioniert|kann|ist)|erklar(?:e|en)?|de que trata|por que|como\s+(?:funciona|puedo|se)|explica(?:me)?|que significa|do que trata|porque|como\s+(?:funciona|posso)|explica(?:r)?|o que significa|di cosa parla|perche|come\s+(?:funziona|posso)|spiega)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeShortCourtesyMessage(string? message, out string language)
    {
        var s = NormalizeShortcutToken(message);
        language = string.Empty;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var fr = new HashSet<string>(StringComparer.Ordinal)
        {
            "merci", "merci beaucoup", "ok merci", "super merci", "parfait merci"
        };
        var en = new HashSet<string>(StringComparer.Ordinal)
        {
            "thanks", "thank you", "thanks a lot", "many thanks", "ok thanks"
        };
        var es = new HashSet<string>(StringComparer.Ordinal)
        {
            "gracias", "muchas gracias", "ok gracias"
        };
        var pt = new HashSet<string>(StringComparer.Ordinal)
        {
            "obrigado", "obrigada", "muito obrigado", "muito obrigada"
        };
        var de = new HashSet<string>(StringComparer.Ordinal)
        {
            "danke", "vielen dank", "danke schon", "danke schoen"
        };
        var it = new HashSet<string>(StringComparer.Ordinal)
        {
            "grazie", "grazie mille", "prego"
        };

        if (fr.Contains(s)) { language = "fr"; return true; }
        if (en.Contains(s)) { language = "en"; return true; }
        if (es.Contains(s)) { language = "es"; return true; }
        if (pt.Contains(s)) { language = "pt"; return true; }
        if (de.Contains(s)) { language = "de"; return true; }
        if (it.Contains(s)) { language = "it"; return true; }
        return false;
    }

    private static string BuildCourtesyReply(string language)
        => NormalizeLanguageCode(language) switch
        {
            "en" => "You're welcome.",
            "es" => "De nada.",
            "pt" => "De nada.",
            "de" => "Gern geschehen.",
            "it" => "Prego.",
            _ => "Avec plaisir."
        };

    private static bool LooksLikeExplicitLiveSummaryFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return MatchesExplicitShortcut(s,
            @"^(?:fais|faites|donne(?:s)?|montre|affiche)(?:[-\s]+moi)?\s+(?:un\s+)?resume\s+live$",
            @"^(?:make|do|give\s+me|show)\s+(?:a\s+)?live\s+summary$",
            @"^(?:haz|dame|muestrame)\s+(?:un\s+)?resumen\s+en\s+vivo$",
            @"^(?:faz|mostra|da\s+me)\s+(?:um\s+)?resumo\s+ao\s+vivo$",
            @"^(?:mach|zeige|gib\s+mir)\s+(?:eine\s+)?live\s+zusammenfassung$",
            @"^(?:fai|mostra|dammi)\s+(?:un\s+)?riassunto\s+live$",
            @"^(?:resume|summarize|resumen|resumo|riassunto|zusammenfassung)\s+(?:live|en\s+vivo|ao\s+vivo)$");
    }

    private static bool LooksLikeReplayLastAnswerRequest(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s,
            @"\b(?:what did you (?:just )?(?:list|say)|repeat that|show that again|remind me|qu est ce que tu viens de me lister|qu est ce que tu viens de lister|qu est ce que tu viens de dire|rappelle moi|reaffiche|redis moi|was hast du gerade gesagt|wiederhole das|que acabas de listar|repitelo|o que voce acabou de listar|repete isso|cosa hai appena detto|ripeti|combien j ai dit)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeSummaryStatusListFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        return MatchesExplicitShortcut(s,
            @"^(?:oui\s+)?(?:donne(?:s)?|envoie(?:s)?|envoye|liste|montre|affiche)(?:[-\s]+moi)?\s+la\s+liste(?:\s+des\s+documents)?$",
            @"^(?:je\s+veux|j\s+veux|je\s+voudrais)\s+la\s+liste(?:\s+des\s+documents)?$",
            @"^(?:list|show|display|give\s+me|send\s+me)\s+(?:the\s+)?list(?:\s+of\s+documents)?$",
            @"^(?:si\s+)?(?:lista|dame|muestrame|ensename|enviame)\s+(?:la\s+)?lista(?:\s+de\s+documentos)?$",
            @"^(?:sim\s+)?(?:lista|mostra|envia(?:me)?|manda(?:me)?|da\s+me)\s+(?:a\s+)?lista(?:\s+de\s+documentos)?$",
            @"^(?:ja\s+)?(?:liste|zeige(?:\s+mir)?|gib\s+mir|sende\s+mir)\s+(?:die\s+)?liste(?:\s+der\s+dokumente)?$",
            @"^(?:si\s+)?(?:lista|dammi|mostra(?:mi)?|invia(?:mi)?|manda(?:mi)?)\s+(?:la\s+)?lista(?:\s+dei\s+documenti)?$",
            @"^(?:which\s+ones|what\s+are\s+they|quels\s+sont\s+ils|cu[aÃ¡]les\s+son|welche\s+sind\s+das|quali\s+sono)$");
    }

    private static bool LooksLikeDirectDocumentsByCategoryRequest(string? message)
        => TryExtractExactCategoryDocumentsRef(message, out _);

    private static bool LooksLikeDirectCategoryStatsRequest(string? message)
        => TryExtractExactCategoryStatsRef(message, out _);

    private static bool LooksLikeDirectAllDocumentsRequest(string? message)
        => false;

    private static bool LooksLikeDirectCategoriesRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCategories);

    private static bool LooksLikeDirectCatalogStatsRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCatalogStats);

    private static bool LooksLikeDirectTreeRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCatalogTree);

    private bool LastAnswerRequiresStructuredReplay()
    {
        if (_mem.LastDeterministicRender is not null)
            return true;

        if (IsInventoryIntent(_mem.LastRouterIntent))
            return true;

        return _mem.LastToolNames.Any(IsInventoryLikeToolName);
    }

    private static bool LooksLikeDirectSummaryStatusRequest(string? message, out string mode, out string state)
    {
        mode = string.Empty;
        state = string.Empty;

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryMissingCount))
        {
            mode = "count";
            state = "missing";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryMissingList))
        {
            mode = "list";
            state = "missing";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryPresentCount))
        {
            mode = "count";
            state = "present";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryPresentList))
        {
            mode = "list";
            state = "present";
            return true;
        }

        return false;
    }

    private bool TryExtractFollowUpCategoryRef(string? message, out string categoryRef)
    {
        categoryRef = string.Empty;
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0)
            return false;

        var match = Regex.Match(s, @"\b(?:categorie|category|categoria|kategorie)\s+(?<ref>[a-z0-9_\-/]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            categoryRef = match.Groups["ref"].Value.Trim();
            return categoryRef.Length > 0;
        }

        if (TryExtractPresentedCategoryByOrdinalToken(s, out categoryRef))
            return true;

        if (_mem.LastResolvedCategory is not null
            && Regex.IsMatch(s, @"\b(?:cette\s+categorie|this\s+category|that\s+category|esta\s+categoria|esa\s+categoria|essa\s+categoria|diese\s+kategorie|questa\s+categoria|ses\s+documents|its\s+documents|sus\s+documentos|seus\s+documentos|ihre\s+dokumente|i\s+suoi\s+documenti|celle\s+de\s+la\s+categorie)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            categoryRef = !string.IsNullOrWhiteSpace(_mem.LastResolvedCategory.CategoryRef)
                ? _mem.LastResolvedCategory.CategoryRef
                : _mem.LastResolvedCategory.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        foreach (var category in _mem.LastPresentedCategories)
        {
            if (Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(category.DisplayName))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(category.CategoryPath))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || category.Aliases.Any(alias => Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(alias))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
                return true;
            }
        }

        return false;
    }

    private bool TryExtractPresentedCategoryByOrdinalToken(string normalizedMessage, out string categoryRef)
    {
        categoryRef = string.Empty;
        if (_mem.LastPresentedCategories is null || _mem.LastPresentedCategories.Count == 0)
            return false;

        var ordinalPatterns = new[]
        {
            @"\b(?:pour\s+la|pour\s+le|et\s+la|et\s+le|dans\s+la|dans\s+le|la|le|categorie|category|categoria|kategorie|fur\s+die|fur\s+den|for\s+the|para\s+la|para\s+el|para\s+a|per\s+la|per\s+il)\s+(?<ord>\d{1,2})(?:ere|eme|er|e|o|a)?\b",
            @"\b(?<ord>\d{1,2})(?:ere|eme|er|e|o|a)\b"
        };

        foreach (var pattern in ordinalPatterns)
        {
            var match = Regex.Match(normalizedMessage, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["ord"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                continue;

            var category = _mem.LastPresentedCategories.FirstOrDefault(x => x.Ordinal == ordinal);
            if (category is null)
                continue;

            categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        var ordinalWordMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["premiere"] = 1,
            ["premier"] = 1,
            ["first"] = 1,
            ["primera"] = 1,
            ["primer"] = 1,
            ["primeira"] = 1,
            ["primeiro"] = 1,
            ["erste"] = 1,
            ["ersten"] = 1,
            ["prima"] = 1,
            ["primo"] = 1,
            ["deuxieme"] = 2,
            ["second"] = 2,
            ["seconde"] = 2,
            ["2nde"] = 2,
            ["segunda"] = 2,
            ["segundo"] = 2,
            ["zweite"] = 2,
            ["zweiten"] = 2,
            ["seconda"] = 2,
            ["secondo"] = 2,
            ["third"] = 3,
            ["troisieme"] = 3,
            ["tercera"] = 3,
            ["tercero"] = 3,
            ["terceira"] = 3,
            ["terceiro"] = 3,
            ["dritte"] = 3,
            ["dritten"] = 3,
            ["terza"] = 3,
            ["terzo"] = 3,
            ["quatrieme"] = 4,
            ["fourth"] = 4,
            ["cuarta"] = 4,
            ["cuarto"] = 4,
            ["quarta"] = 4,
            ["quarto"] = 4,
            ["vierte"] = 4,
            ["vierten"] = 4,
            ["cinquieme"] = 5,
            ["fifth"] = 5,
            ["quinta"] = 5,
            ["quinto"] = 5,
            ["funfte"] = 5,
            ["funften"] = 5
        };

        foreach (var pair in ordinalWordMap)
        {
            if (!Regex.IsMatch(normalizedMessage, $@"\b{Regex.Escape(pair.Key)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            var category = _mem.LastPresentedCategories.FirstOrDefault(x => x.Ordinal == pair.Value);
            if (category is null)
                continue;

            categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        return false;
    }


    private static bool LooksLikeRecentAdminOperationStatusFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(s, @"\b(?:c est fait|c est fini|c est termine|fini|termine|ou en est|statut|status|done yet|is it done|is it finished|still running|toujours en cours|toujours en train|est ce termine|est ce fini)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeHelpOnlyAdminReindexDisplayText(string? message)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        if (rawMessage.Length == 0)
            return false;

        var normalizedExact = NormalizeExactPromptText(rawMessage);
        if (normalizedExact.Length == 0 || ContainsConversationalContentCue(normalizedExact))
            return false;

        if (MatchesCanonicalDynamicDisplayPrompt(rawMessage, ClientUiText.BuildPromptAdminReindexDisplay))
            return true;

        if (TryMatchCanonicalDynamicPrompt(rawMessage, ClientUiText.BuildPromptAdminReindex, out _))
            return true;

        var normalizedShortcut = NormalizeShortcutToken(rawMessage);
        if (normalizedShortcut.Length == 0)
            return false;

        return Regex.IsMatch(
            normalizedShortcut,
            @"^(?:cible de reindexation|action aide reindexer le document|help action reindex document|reindex target|reindex the document|relance l ingestion du document|objetivo de reindexacion|accion de ayuda reindexar documento|reindexa el documento|destino da reindexacao|acao da ajuda reindexar documento|reindexa o documento|neuindexierungsziel|hilfeaktion dokument neu indexieren|reindiziere das dokument|destinazione reindicizzazione|azione guida reindicizza documento|reindicizza il documento)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedGuidedCommandRequest(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0 || ContainsConversationalContentCue(s))
            return false;

        if (LooksLikeDirectCategoriesRequest(s)
            || LooksLikeDirectCatalogStatsRequest(s)
            || LooksLikeDirectDocumentsByCategoryRequest(s)
            || LooksLikeDirectCategoryStatsRequest(s)
            || LooksLikeDirectSummaryStatusRequest(s, out _, out _)
            || LooksLikeDirectTreeRequest(s)
            || LooksLikeDirectAdminRescanRequest(s))
        {
            return false;
        }

        if (LooksLikeDocumentaryContentQuestionBeyondCatalogCommand(s))
            return false;

        return LooksLikeMalformedCategoriesCommand(s)
            || LooksLikeMalformedCatalogStatsCommand(s)
            || LooksLikeMalformedTreeCommand(s)
            || LooksLikeMalformedCategoryScopedCommand(s)
            || LooksLikeMalformedSummaryStatusCommand(s)
            || LooksLikeMalformedAdminCatalogRescanCommand(s);
    }

    private static bool LooksLikeDocumentaryContentQuestionBeyondCatalogCommand(string normalizedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        var mentionsDocumentarySource = Regex.IsMatch(
            normalizedMessage,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|corpus|file|files|fichier|fichiers)\b",
            ShortcutRegexOptions);
        if (!mentionsDocumentarySource)
            return false;

        return Regex.IsMatch(
            normalizedMessage,
            @"\b(?:parle|parlent|contient|contiennent|traite|traitent|about|cover|covers|overview|useful|important|business|meilleur|meilleure|meilleurs|meilleures|best|pire|pires|worst|tester|test|robustesse|robustness|preuve|preuves|evidence|evidences|limite|limites|risk|risque|risques|compare|comparer|comparaison|resume|resumer|synthese|synthese|explique|expliquer)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedCategoriesCommand(string normalizedMessage)
    {
        var hasCategory = Regex.IsMatch(normalizedMessage, @"\b(?:categorie|categories|category|categoria|categorias|kategorie|kategorien|hauptordner|top level|top-level)\b", ShortcutRegexOptions);
        if (!hasCategory)
            return false;

        return Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|montres|affiche|quels|quelles|what are|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedCatalogStatsCommand(string normalizedMessage)
    {
        var hasStats = Regex.IsMatch(normalizedMessage, @"\b(?:stat|stats|statistique|statistiques|statistics|estadisticas|estatisticas|statistiken|statistiche)\b", ShortcutRegexOptions);
        if (!hasStats)
            return false;

        if (Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|documento|documentos|dokument|dokumente|riassunto|summary|resume)\b", ShortcutRegexOptions))
            return false;

        var hasCommandVerb = Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|montres|affiche|dame|muestrame|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
        return hasCommandVerb || normalizedMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;
    }

    private static bool LooksLikeMalformedTreeCommand(string normalizedMessage)
    {
        var hasTree = Regex.IsMatch(normalizedMessage, @"(?:tree|arborescence|arbre|Ã¡rbol|baum|albero)", ShortcutRegexOptions);
        if (!hasTree)
            return false;

        return Regex.IsMatch(normalizedMessage, @"(?:show|display|give|list|donne|montre|affiche|dame|muestrame|mostra|zeige|gib)", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedCategoryScopedCommand(string normalizedMessage)
    {
        var hasCategory = Regex.IsMatch(normalizedMessage, @"\b(?:categorie|category|categoria|kategorie)\b", ShortcutRegexOptions);
        if (!hasCategory)
            return false;

        var hasDocuments = Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|fichier|fichiers|file|files|documentos|archivos|dokumente|documenti)\b", ShortcutRegexOptions);
        var hasStats = Regex.IsMatch(normalizedMessage, @"\b(?:stat|stats|statistique|statistiques|statistics|estadisticas|estatisticas|statistiken|statistiche)\b", ShortcutRegexOptions);
        if (!hasDocuments && !hasStats)
            return false;

        var hasCommandVerb = Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|affiche|muestre|muestrame|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
        return hasCommandVerb || Regex.IsMatch(normalizedMessage, @"^(?:documents?|files?|fichiers?|documentos|dokumente|documenti|stats?|statistics|statistiques)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedSummaryStatusCommand(string normalizedMessage)
    {
        var hasSummary = Regex.IsMatch(normalizedMessage, @"\b(?:resume|resumen|resumo|zusammenfassung|riassunto|summary|summaries)\b", ShortcutRegexOptions);
        var hasDocuments = Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|documentos|dokumente|documenti)\b", ShortcutRegexOptions);
        if (!hasSummary || !hasDocuments)
            return false;

        var hasState = Regex.IsMatch(normalizedMessage, @"\b(?:stocke|stored|almacenad|armazenad|gespeichert|salvat|sans|without|sin|sem|missing|manquant|mancant|avec|with|con|com|present|presents|presenti|vorhanden)\b", ShortcutRegexOptions);
        if (!hasState)
            return false;

        return Regex.IsMatch(normalizedMessage, @"\b(?:combien|how many|count|liste|list|show|display|give|donne|montre|affiche|cuantos|quantos|wie viele|quanti)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedAdminCatalogRescanCommand(string normalizedMessage)
    {
        return Regex.IsMatch(normalizedMessage, @"\b(?:rescan|rescann|re scan|scan)\b", ShortcutRegexOptions)
            && Regex.IsMatch(normalizedMessage, @"\b(?:catalogue|catalog|catalogo|katalog)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeDirectAdminRescanRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptAdminRescan);

    private static bool LooksLikeDirectAdminReindexRequest(string? message)
        => false;

    private static bool TryExtractExactCategoryDocumentsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryDocuments, out categoryRef);

    private static bool TryExtractExactCategoryStatsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryStats, out categoryRef);

    private static bool TryExtractExactDocumentSearchQuery(string? message, out string query)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptSearchDocuments, out query);


    private static bool TryExtractExactAdminReindexDocumentRef(string? message, out string documentRef)
    {
        documentRef = string.Empty;
        return false;
    }
}
