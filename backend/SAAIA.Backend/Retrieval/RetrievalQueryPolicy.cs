using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalQueryPolicy
{
    internal const double NavigationRouteMinimumConfidence = 0.699;

    internal const double FuzzyTitleSimilarityMinimum = 0.68;

    internal const double FuzzyTitleLeadSimilarityMinimum = 0.66;


    internal static async Task<string?> ResolveRagCategoryPathAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string? categoryPath,
        string? categoryRef,
        CancellationToken ct)
    {
        var normalizedPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        var normalizedRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        if (string.IsNullOrWhiteSpace(normalizedPath) && string.IsNullOrWhiteSpace(normalizedRef))
            return null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var resolved = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(
            conn,
            tenantId,
            normalizedPath,
            normalizedRef,
            ct);

        return DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(resolved ?? normalizedPath);
    }


    internal static string? NormalizeRagCategoryPathForSql(string? categoryPath)
        => DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);


    internal static bool RagDocPathMatchesCategoryPath(string? docPath, string? categoryPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
            return true;
        if (string.IsNullOrWhiteSpace(docPath))
            return false;

        var normalizedDocPath = docPath.Trim().Replace('\\', '/').Trim('/');
        return string.Equals(normalizedDocPath, categoryPath, StringComparison.OrdinalIgnoreCase)
            || normalizedDocPath.StartsWith(categoryPath + "/", StringComparison.OrdinalIgnoreCase);
    }


    internal static string ResolveEffectiveSearchMode(string? requestedMode, string query)
    {
        var mode = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(mode, "focused", StringComparison.Ordinal)
            && ShouldPromoteFocusedSearchForBroadIntent(query))
        {
            return ShouldPreferDocumentDiversity(query) ? "broad" : "balanced";
        }

        if (mode is "focused" or "balanced" or "broad")
            return mode;

        return ShouldPreferDocumentDiversity(query)
            ? "broad"
            : "balanced";
    }


    internal static bool ShouldUseResearchSurfaces(RagSearchRequestDto req)
    {
        if (req.IncludeResearchSurfaces == true)
            return true;

        var mode = (req.ResearchMode ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "research"
            or "exploration"
            or "broad_exploration"
            or "source_exploration"
            or "evidence_exploration";
    }


    internal static int ResolveInternalSelectionTopK(
        string query,
        int requestedTopK,
        int maxTopK,
        bool hasDocScope,
        bool hasExplicitFileDocumentHint,
        string mode)
    {
        if (requestedTopK <= 0)
            return requestedTopK;
        if (hasDocScope || hasExplicitFileDocumentHint)
            return requestedTopK;
        if (string.Equals(mode, "focused", StringComparison.Ordinal))
            return requestedTopK;

        var needsTitleOrDiversityHeadroom =
            ExtractTitleLookupPhrases(query).Count > 0
            || ShouldPreferComparativeDocumentDiversity(query);
        if (!needsTitleOrDiversityHeadroom)
            return requestedTopK;

        return Math.Min(
            Math.Max(requestedTopK, 16),
            Math.Max(requestedTopK, maxTopK));
    }


    internal static bool ShouldPreferDocumentDiversity(string query)
        => ShouldPreferComparativeDocumentDiversity(query)
           || ContainsDocumentOverviewIntent(query);


    internal static bool ShouldPreferItemInventorySelection(
        RagSearchRequestDto req,
        string mode,
        bool includeResearchSurfaces)
    {
        if (!includeResearchSurfaces
            || string.Equals(mode, "focused", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(req.Query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(req.Query)).ToLowerInvariant() + " ";
        var asksForInventory = ContainsAny(
            normalized,
            " liste ",
            " listado ",
            " lista ",
            " list ",
            " options ",
            " opciones ",
            " opcoes ",
            " opzioni ",
            " idees ",
            " ideas ",
            " idee ",
            " propositions ",
            " propuestas ",
            " sugestoes ",
            " suggerimenti ",
            " suggestions ",
            " choix ",
            " selection ",
            " selecao ",
            " selezione ",
            " candidates ",
            " candidats ",
            " elements ",
            " items ",
            " catalog ",
            " catalogue ",
            " inventaire ",
            " inventory ");
        var asksForPlanning = ContainsPlanningOrScheduleIntent(normalized);
        var hasMultiSlotShape = Regex.IsMatch(
            normalized,
            @"\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|miércoles|jueves|viernes|sabado|sábado|domingo|segunda|terca|terça|quarta|quinta|sexta|sabado|sábado|domingo|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|lunedì|martedi|martedì|mercoledi|mercoledì|giovedi|giovedì|venerdi|venerdì|sabato|domenica|semaine|weekly|semana|settimanale|woche|jour|jours|days?|matin|midi|soir|morning|afternoon|evening|night)\b",
            RegexOptions.CultureInvariant);
        var explicitQuantity = Regex.IsMatch(
            normalized,
            @"\b\d{1,2}\s+(?:elements?|items?|options?|candidats?|candidates?|propositions?|suggestions?|jours?|days?|pages?)\b",
            RegexOptions.CultureInvariant);

        return (asksForInventory || asksForPlanning || ContainsSituationalBroadSelectionIntent(normalized))
            && (hasMultiSlotShape || explicitQuantity || asksForInventory);
    }


    internal static bool ShouldSkipChunkRetrieversForDocumentOverview(string query, bool hasDocScope, string mode)
    {
        if (ShouldTreatQuotedLookupAsTopic(query))
            return false;

        if (ExtractQuotedLookupPhrases(query).Count > 0
            && !ShouldSkipPreciseTitleLookupForDocumentOverview(query, hasDocScope, mode))
        {
            return false;
        }

        return !hasDocScope
           && !string.Equals(mode, "focused", StringComparison.Ordinal)
           && ContainsDocumentOverviewIntent(query)
           && !ShouldRequireDocumentOverviewProfileMatch(query);
    }


    internal static bool ShouldSkipPreciseTitleLookupForDocumentOverview(string query, bool hasDocScope, string mode)
    {
        if (hasDocScope || string.Equals(mode, "focused", StringComparison.Ordinal))
            return false;

        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return false;
        if (quotedPhrases.Any(static phrase => LooksLikeExplicitFileLookupPhrase(phrase)))
            return false;
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        var hasQuotedDocumentOverviewIntent = ContainsDocumentOverviewIntent(query)
            || (ContainsAny(normalized, " documents ", " document ", " sources ", " source ", " pdf ")
                && ContainsAny(
                    normalized,
                    " qui parle ",
                    " qui parlent ",
                    " parle de ",
                    " parlent de ",
                    " mentionne ",
                    " mentionnent ",
                    " contient ",
                    " contiennent ",
                    " about ",
                    " mention ",
                    " mentions ",
                    " contain ",
                    " contains "));
        if (!hasQuotedDocumentOverviewIntent)
            return false;

        return true;
    }


    private static bool LooksLikeExplicitFileLookupPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase)).ToLowerInvariant();
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !tokens.Any(IsFileExtensionToken))
            return false;

        return tokens.Any(IsDocumentHintSignalToken);
    }


    internal static bool ShouldTreatQuotedLookupAsTopic(string query)
    {
        if (ExtractQuotedLookupPhrases(query).Count == 0)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        var hasTopicRelation = ContainsAny(
            normalized,
            " sur ",
            " about ",
            " on ",
            " concernant ",
            " en lien avec ",
            " associe a ",
            " associes a ",
            " associated with ",
            " related to ",
            " parle de ",
            " parle d ",
            " parlent de ",
            " parlent d ",
            " impose ",
            " imposent ",
            " documente ",
            " documentent ",
            " documented ",
            " documents ",
            " require ",
            " requires ",
            " requires that ",
            " obligation ",
            " obligations ",
            " recommandation ",
            " recommendation ",
            " recommendations ",
            " demontre ",
            " demonstrated ",
            " verifie si ",
            " verify whether ");
        if (!hasTopicRelation)
            return false;

        return ContainsAny(
            normalized,
            " oui non ",
            " yes no ",
            " documents ",
            " pdf ",
            " corpus ",
            " passages ",
            " question client ",
            " refuse la simplification ",
            " no clear yes no ",
            " if the pdf ",
            " if the documents ",
            " cannot confirm ");
    }


    internal static bool ShouldSkipSparseRetrieverForBroadDiversity(string query, string mode)
        => !string.Equals(mode, "focused", StringComparison.Ordinal)
           && ExtractQuotedLookupPhrases(query).Count == 0
           && ShouldTreatAsBroadDiversityQuery(query)
           && !ShouldAllowSparseAssistForBroadDiversity(query);


    internal static bool ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(string query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasExplicitDocumentNameHint(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var shouldSkipBroadComparativeSynthesis =
            ShouldSkipUnanchoredTitleAnchorRouteForBroadComparativeSynthesis(query, normalized);
        if (HasReferenceLikeQueryToken(query) && !shouldSkipBroadComparativeSynthesis)
            return false;

        if (ContainsAny(normalized, " vs ", " versus ") && !shouldSkipBroadComparativeSynthesis)
            return false;

        if (shouldSkipBroadComparativeSynthesis)
            return true;

        if (ShouldPreferComparativeDocumentDiversity(query)
            && ContainsAny(normalized, " choisir entre ", " choisis entre ", " choisissez entre ", " choix entre ", " choose between ", " select between "))
        {
            return true;
        }

        if (!ShouldTreatAsBroadDiversityQuery(query))
            return false;

        return true;
    }


    internal static bool ShouldAllowNavigationCatalogRouteForBroadExploration(string query, bool hasScope)
    {
        if (!hasScope
            || string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasExplicitDocumentNameHint(query)
            || ContainsQuantityComputationIntent(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ShouldPreferComparativeDocumentDiversity(query)
            && !ContainsPlanningOrScheduleIntent(normalized)
            && !ContainsBroadScopedSynthesisIntent(normalized)
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized)
            && !ContainsSituationalBroadSelectionIntent(normalized))
        {
            return false;
        }

        return ContainsPlanningOrScheduleIntent(normalized)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsDocumentOverviewIntent(query)
            || IsRecommendationSelectionQuery(normalized);
    }


    private static bool ShouldSkipUnanchoredTitleAnchorRouteForBroadComparativeSynthesis(string query, string normalized)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query))
            return false;

        var hasComparativeShape = ContainsAny(
            normalized,
            " vs ",
            " versus ",
            " compare ",
            " comparer ",
            " comparaison ",
            " comparative ",
            " comparatif ",
            " difference entre ",
            " differences entre ",
            " difference between ",
            " differences between ",
            " avant apres ",
            " before after ");
        if (!hasComparativeShape)
            return false;

        var hasSynthesisSurface = ContainsAny(
            normalized,
            " tableau ",
            " table ",
            " colonne ",
            " colonnes ",
            " ancienne source ",
            " nouvelle source ",
            " source ancienne ",
            " source nouvelle ",
            " old source ",
            " new source ",
            " older source ",
            " newer source ",
            " changement ",
            " changements ",
            " change ",
            " changes ",
            " evolue ",
            " evolution ",
            " evolutions ",
            " impact ",
            " impacts ",
            " mise a jour ",
            " mises a jour ",
            " updated ",
            " update ",
            " perimetre ",
            " scope ",
            " recouvrement ",
            " overlap ");
        if (!hasSynthesisSurface)
            return false;

        return ContainsAny(
                normalized,
                " document ",
                " documents ",
                " pdf ",
                " pdfs ",
                " corpus ",
                " source ",
                " sources ",
                " version ",
                " versions ")
            || ContainsAny(normalized, " vs ", " versus ");
    }


    internal static bool ShouldUseChoiceBetweenLexicalRoute(string query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasExplicitDocumentNameHint(query)
            || HasReferenceLikeQueryToken(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsAny(normalized, " choisir entre ", " choisis entre ", " choisissez entre ", " choix entre ", " choose between ", " select between ")
            && !ContainsAny(normalized, " vs ", " versus ");
    }


    internal static string BuildChoiceBetweenRetrievalQuery(string query)
    {
        if (!ShouldUseChoiceBetweenLexicalRoute(query))
            return ResolvePrimaryRetrievalQuery(query, category: null);

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var folded = FoldDiacritics(normalized);
        var betweenMatch = Regex.Match(
            folded,
            @"\b(?:entre|between)\s+(?<target>[\p{L}\p{Nd}\s\-:;,]{8,180}?)(?=\s+(?:comment|how|pourquoi|why|explique|explain)\b|[\?\r\n]|$)",
            RegexOptions.CultureInvariant);
        if (!betweenMatch.Success)
            return ResolvePrimaryRetrievalQuery(query, category: null);

        var subject = betweenMatch.Groups["target"].Value.Trim();
        return string.IsNullOrWhiteSpace(subject)
            ? ResolvePrimaryRetrievalQuery(query, category: null)
            : subject;
    }


    internal static bool ShouldAllowSparseAssistForBroadDiversity(string query)
    {
        var hasReferenceLikeQuery = HasReferenceLikeQueryToken(query);
        if (!ShouldTreatAsBroadDiversityQuery(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || (hasReferenceLikeQuery && !ShouldAllowReferenceLikeComparativeDiversity(query, $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ")))
        {
            return false;
        }

        var concreteTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !BroadDiversityGenericSubjectTokens.Contains(token))
            .Where(static token => !BroadDiversityAbstractAssistTokens.Contains(token))
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !ComparativeSubjectStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return concreteTokens.Length >= 3;
    }


    internal static bool ShouldSkipSparseRetrieverForQuantityLookup(string query, string mode)
        => false;


    internal static bool ShouldSkipSparseProfileCardAssist(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasReferenceLikeQueryToken(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ShouldUseDocumentProfileSearchForBroadSynthesis(query, mode, normalized))
            return false;

        return ContainsDocumentOverviewIntent(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsUnsupportedClaimVerificationIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsQuantityComputationIntent(normalized)
            || (string.Equals(mode, "broad", StringComparison.Ordinal) && ShouldPreferDocumentDiversity(query));
    }


    internal static bool ShouldSkipDocumentProfileSearchForLowCostBroadQuery(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasReferenceLikeQueryToken(query)
            || ContainsDocumentOverviewIntent(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ShouldAllowDocumentProfileAssistForComparativeLookup(query))
            return false;
        if (ShouldUseDocumentProfileSearchForBroadSynthesis(query, mode, normalized))
            return false;

        return ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsQuantityComputationIntent(normalized)
            || (string.Equals(mode, "broad", StringComparison.Ordinal) && ShouldPreferDocumentDiversity(query));
    }


    internal static bool ShouldUseDocumentProfileSearchForBroadSynthesis(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ShouldUseDocumentProfileSearchForBroadSynthesis(query, mode, normalized);
    }


    private static bool ShouldUseDocumentProfileSearchForBroadSynthesis(string query, string mode, string normalized)
    {
        if (string.IsNullOrWhiteSpace(query)
            || string.Equals(mode, "focused", StringComparison.Ordinal)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || ContainsUnsupportedClaimVerificationIntent(normalized)
            || ContainsQuantityComputationIntent(normalized)
            || ContainsAny(
                normalized,
                " traduis ",
                " traduire ",
                " translate ",
                " traduce ",
                " traducir ",
                " traduz ",
                " traduzir ",
                " ubersetze ",
                " uebersetze "))
        {
            return false;
        }

        if (HasReferenceLikeQueryToken(query)
            && !ShouldAllowReferenceLikeComparativeDiversity(query, normalized))
        {
            return false;
        }

        var hasBroadSynthesisIntent = ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || IsRecommendationSelectionQuery(normalized);
        if (!hasBroadSynthesisIntent)
            return false;

        var concreteTokenCount = CountConcreteDocumentProfileSynthesisTokens(query);
        if (concreteTokenCount >= 2)
            return true;

        return concreteTokenCount >= 1
            && (ContainsPlanningOrScheduleIntent(normalized) || IsRecommendationSelectionQuery(normalized));
    }


    private static int CountConcreteDocumentProfileSynthesisTokens(string query)
        => BuildDocumentProfileSpecificityTokens(query)
            .Where(static token => !BroadDiversityGenericSubjectTokens.Contains(token))
            .Where(static token => !BroadDiversityAbstractAssistTokens.Contains(token))
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !ComparativeSubjectStopwords.Contains(token))
            .Where(static token => !IsGenericDocumentProfileSpecificityToken(token))
            .Where(static token => !IsGenericBroadSynthesisToken(token))
            .Distinct(StringComparer.Ordinal)
            .Count();


    private static bool ContainsPlanningOrScheduleIntent(string normalized)
        => ContainsAny(
            normalized,
            " plan ",
            " planning ",
            " planifier ",
            " programme ",
            " programmer ",
            " organiser ",
            " organise ",
            " schedule ",
            " agenda ",
            " weekly ",
            " week ",
            " semaine ",
            " semanal ",
            " semana ",
            " settimanale ",
            " settimana ",
            " wochenplan ",
            " woche ",
            " rotation ",
            " wochenplan ");


    private static bool IsGenericBroadSynthesisToken(string token)
        => token is "adapte" or "adaptee" or "adaptees" or "adapter" or "adapted" or "adaptar" or "adequado" or "adequada"
            or "option" or "options" or "opcion" or "opciones" or "opcao" or "opcoes" or "opzione" or "opzioni"
            or "dossier" or "dossiers" or "personne" or "personnes" or "people" or "usuarios" or "utilisateurs"
            or "plan" or "planning" or "programme" or "schedule" or "agenda" or "rotation"
            or "semaine" or "weekly" or "semana" or "settimanale" or "woche"
            or "complet" or "complete"
            or "mode" or "modes" or "utilisant" or "using" or "concus" or "concu" or "designed"
            or "rends" or "robuste" or "pretendre" or "officiel" or "official"
            or "propose" or "proposer" or "recommend" or "recommande" or "conseille" or "choisir" or "choice"
            or "utile" or "utiles" or "useful" or "disponible" or "disponibles" or "available";


    internal static bool ShouldTreatAsBroadDiversityQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ShouldTreatAsBroadDiversityQuery(query, normalized);
    }


    internal static bool ShouldTreatAsBroadDiversityQuery(string query, string normalized)
    {
        var hasReferenceLikeQuery = HasReferenceLikeQueryToken(query);
        if (!ShouldPreferComparativeDocumentDiversity(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || (hasReferenceLikeQuery
                && ExtractComparativeVersionOperandLookupTerms(query).Count == 0
                && !ShouldAllowReferenceLikeComparativeDiversity(query, normalized)))
        {
            return false;
        }

        var hasComparisonMarker = ContainsAny(normalized,
            " compare ",
            " comparer ",
            " compares ",
            " comparing ",
            " comparative ",
            " comparaison ",
            " comparatif ",
            " compara ",
            " comparar ",
            " comparacion ",
            " comparacao ",
            " confronto ",
            " difference entre ",
            " differences entre ",
            " different between ",
            " difference between ",
            " differences between ",
            " distinction ",
            " distinguer ",
            " distinguish between ",
            " distinguish ",
            " vs ",
            " versus ",
            " contre ",
            " choisir entre ",
            " choisis entre ",
            " choose between ",
            " vergleiche ",
            " vergleichen ");
        if (ContainsGuidanceOrAdviceSelectionIntent(normalized)
            && !hasComparisonMarker)
        {
            return false;
        }

        var hasExplicitDiversityMarker = ContainsAny(normalized,
                " deux ",
                " trois ",
                " plusieurs ",
                " multiple ",
                " several ",
                " versions ",
                " variantes ",
                " styles ",
                " famille ",
                " familles ",
                " family ",
                " series ",
                " serie ",
                " parties ",
                " partie ",
                " parts ",
                " part ",
                " entre ",
                " between ",
                " corpus ",
                " documents ",
                " docs ",
                " pdf ",
                " pdfs ");
        return hasExplicitDiversityMarker
            || (hasComparisonMarker && HasSpecificComparativeSubject(query))
            || ShouldUseBareVsComparativeOperandRoute(query)
            || (ContainsAny(normalized, " le plus ", " la plus ", " les plus ", " most ", " mas ", " mais ", " am meisten ")
                && ContainsAny(
                    normalized,
                    " quel ",
                    " quelle ",
                    " quels ",
                    " quelles ",
                    " lequel ",
                    " laquelle ",
                    " lesquels ",
                    " lesquelles ",
                    " which ",
                    " what ",
                    " cual ",
                    " qual ",
                    " quale ",
                    " welches ",
                    " welche "));
    }


    internal static bool ShouldAllowReferenceLikeComparativeDiversity(string query, string normalized)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractStandardReferenceTitleLookupPhrases(query).Count == 0)
        {
            return false;
        }

        var hasFamilyScope = ContainsAny(
            normalized,
            " famille ",
            " family ",
            " serie ",
            " series ",
            " gamme ",
            " collection ",
            " partie ",
            " parties ",
            " part ",
            " parts ",
            " volume ",
            " volumes ",
            " documents lies ",
            " related documents ",
            " documents connexes ",
            " normes liees ",
            " related standards ");
        if (!hasFamilyScope)
            return false;

        return ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparaison ",
            " comparative ",
            " comparatif ",
            " vs ",
            " versus ",
            " difference entre ",
            " differences entre ",
            " difference between ",
            " differences between ",
            " distinction ",
            " distinguer ",
            " distinguish ",
            " distinguish between ");
    }


    private static bool HasSpecificComparativeSubject(string query)
        => ExtractComparativeLookupPhrases(query)
            .SelectMany(ExtractLexicalQueryTokens)
            .Any(static token => token.Length >= 5 && !BroadDiversityGenericSubjectTokens.Contains(token));


    internal static readonly HashSet<string> BroadDiversityGenericSubjectTokens = new(StringComparer.Ordinal)
    {
        "option",
        "options",
        "available",
        "disponible",
        "disponibles",
        "choix",
        "choice",
        "choices"
    };


    internal static readonly HashSet<string> BroadDiversityAbstractAssistTokens = new(StringComparer.Ordinal)
    {
        "adapte",
        "adaptee",
        "adaptees",
        "adaptes",
        "best",
        "choose",
        "choisir",
        "choisis",
        "choix",
        "comparez",
        "comparatif",
        "comparaison",
        "confronta",
        "confronto",
        "familles",
        "laquelle",
        "lequel",
        "lesquelles",
        "lesquels",
        "present",
        "presentes",
        "procedure",
        "procedures",
        "select",
        "selection",
        "style",
        "styles",
        "three",
        "trois",
        "versions"
    };


    internal static bool ShouldUseScopedProfileFallback(string query, bool hasCategoryFilter, string mode)
    {
        if (!hasCategoryFilter
            || string.IsNullOrWhiteSpace(query)
            || string.Equals(mode, "focused", StringComparison.Ordinal))
        {
            return false;
        }
        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var hasEvidenceQualitySynthesis = ContainsEvidenceQualityScopedSynthesisIntent(normalized);
        if (ExtractQuotedLookupPhrases(query).Count > 0
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }

        if (ShouldPreferComparativeDocumentDiversity(query)
            && ShouldTreatAsBroadDiversityQuery(query, normalized))
        {
            return false;
        }

        var hasFocusedLookup = ExtractFocusedLookupPhrases(query).Count > 0;
        var hasExplicitBroadScopedSynthesis = ContainsExplicitBroadScopedSynthesisIntent(normalized);
        var hasBroadScopedSynthesis = ContainsBroadScopedSynthesisIntent(normalized);
        var hasSituationalBroadSelection = ContainsSituationalBroadSelectionIntent(normalized);
        var hasDocumentRoleClassification = ContainsDocumentRoleClassificationIntent(normalized);
        var hasParameterDetailLookup = ContainsParameterDetailLookupIntent(normalized);
        var hasReferenceLikeQuery = HasReferenceLikeQueryToken(query)
            || ContainsAny(normalized, " access mode ", " mode acces ");
        if (hasDocumentRoleClassification)
            return true;

        var allowFocusedBroadProfileFallback = (hasBroadScopedSynthesis
            || hasEvidenceQualitySynthesis)
            && !hasParameterDetailLookup
            && !hasReferenceLikeQuery;
        if (hasParameterDetailLookup
            && !hasExplicitBroadScopedSynthesis
            && !hasSituationalBroadSelection
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }
        if (hasFocusedLookup
            && ShouldConstrainPreciseTitleLookup(query)
            && !hasExplicitBroadScopedSynthesis
            && !hasSituationalBroadSelection)
        {
            return false;
        }

        var hasCorpusExistenceOverview = ContainsCorpusExistenceIntent(normalized)
            && ContainsDocumentCorpusCountIntent(normalized);
        if (hasFocusedLookup
            && !hasExplicitBroadScopedSynthesis
            && !allowFocusedBroadProfileFallback
            && !hasCorpusExistenceOverview
            && (!hasSituationalBroadSelection || hasParameterDetailLookup))
        {
            return false;
        }

        if (hasSituationalBroadSelection
            && ExtractQuotedLookupPhrases(query).Count == 0
            && !HasReferenceLikeQueryToken(query))
        {
            return true;
        }

        if (hasReferenceLikeQuery
            && !hasExplicitBroadScopedSynthesis
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }

        return hasCorpusExistenceOverview
            || hasExplicitBroadScopedSynthesis
            || hasBroadScopedSynthesis
            || hasSituationalBroadSelection
            || hasEvidenceQualitySynthesis;
    }


    internal static bool ShouldPromoteFocusedSearchForBroadIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsDocumentOverviewIntent(query))
            return true;

        var hasFocusedLookup = ExtractFocusedLookupPhrases(query).Count > 0;
        var hasExplicitBroadScopedSynthesis = ContainsExplicitBroadScopedSynthesisIntent(normalized);
        var hasBroadScopedSynthesis = ContainsBroadScopedSynthesisIntent(normalized);
        var hasSituationalBroadSelection = ContainsSituationalBroadSelectionIntent(normalized);
        var hasEvidenceQualitySynthesis = ContainsEvidenceQualityScopedSynthesisIntent(normalized);
        var hasParameterDetailLookup = ContainsParameterDetailLookupIntent(normalized);
        var hasReferenceLikeQuery = HasReferenceLikeQueryToken(query)
            || ContainsAny(normalized, " access mode ", " mode acces ");
        var allowFocusedBroadProfileFallback = (hasBroadScopedSynthesis
            || hasEvidenceQualitySynthesis)
            && !hasParameterDetailLookup
            && !hasReferenceLikeQuery;
        if (hasParameterDetailLookup
            && !hasExplicitBroadScopedSynthesis
            && !hasSituationalBroadSelection
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }

        if (hasFocusedLookup
            && !hasExplicitBroadScopedSynthesis
            && !allowFocusedBroadProfileFallback
            && (!hasSituationalBroadSelection || hasParameterDetailLookup))
        {
            return false;
        }

        if (hasSituationalBroadSelection
            && ExtractQuotedLookupPhrases(query).Count == 0
            && !HasReferenceLikeQueryToken(query))
        {
            return true;
        }

        if (ExtractQuotedLookupPhrases(query).Count > 0
            && !hasExplicitBroadScopedSynthesis
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }

        if (hasReferenceLikeQuery
            && !hasExplicitBroadScopedSynthesis
            && !hasEvidenceQualitySynthesis)
        {
            return false;
        }

        if (ShouldPreferComparativeDocumentDiversity(query))
            return ShouldTreatAsBroadDiversityQuery(query, normalized);

        return hasExplicitBroadScopedSynthesis
            || hasBroadScopedSynthesis
            || hasSituationalBroadSelection
            || hasEvidenceQualitySynthesis;
    }


    internal static bool ContainsExplicitBroadScopedSynthesisIntent(string normalized)
        => ContainsAny(normalized,
               " plusieurs ",
               " multiple ",
               " several ",
               " options ",
               " versions ",
               " versiones ",
               " versoes ",
               " versioni ",
               " versionen ",
               " selection ",
               " selectionne ",
               " selectionner ",
               " select ",
               " selected ",
               " liste ",
               " list ",
               " json ",
               " markdown ",
               " checklist ",
               " questions de verification ",
               " verification questions ",
               " familles ",
               " families ",
               " manquent ",
               " manque ",
               " missing ",
               " inventaire ",
               " inventory ",
               " pour chaque ",
               " chaque ",
               " for each ",
               " each ",
               " tableau ",
               " table ",
               " matrice ",
               " matrix ",
               " format ",
               " formatte ",
               " formatted ",
               " complet utilisant ",
               " complete using ",
               " complete with ",
               " cout ",
               " couts ",
               " cost ",
               " costs ",
               " budget ",
               " chf ",
               " rends ",
               " rendre ",
               " transforme ",
               " transformer ",
               " convertis ",
               " convertir ",
               " planning ",
               " planifier ",
               " organiser ",
               " organise ",
               " faq ",
               " retroplanning ",
               " synthese ",
               " synthetise ",
                " synthetiser ",
                " quoi faire ",
                " que faire ",
                " pas couvert ",
                " non couvert ",
                " n est pas couvert ",
                " est couvert ",
                " c est couvert ",
                " conseil juridique ",
                " sinistre ",
                " a partir de ",
               " depuis le corpus ",
               " dans le corpus ",
               " depuis les documents ",
               " dans les documents ",
               " from this corpus ",
               " from the corpus ",
               " from documents ",
               " from the documents ",
               " aus dem corpus ",
               " aus den dokumenten ")
           || Regex.IsMatch(
               normalized,
               @"\b(?:fais moi|fais nous|cree|creer|make me|create|build|draft|hazme|crea|crie|criar|erstelle|mach mir)\s+\d+\b",
               RegexOptions.CultureInvariant);


    internal static bool ContainsBroadScopedSynthesisIntent(string normalized)
        => ContainsAny(normalized,
            " propose ",
            " proposer ",
            " proposes ",
            " cherche ",
            " chercher ",
            " search ",
            " searching ",
            " busca ",
            " buscar ",
            " busco ",
            " procura ",
            " procurar ",
            " procuro ",
            " cerca ",
            " cercare ",
            " cerco ",
            " suche ",
            " suchen ",
            " suggere ",
            " suggerer ",
            " recommande ",
            " recommander ",
            " conseille ",
            " conseiller ",
            " aide moi ",
            " aider ",
            " je ne sais pas quel document ",
            " quel document utiliser ",
            " selectionne ",
            " selectionner ",
            " selection ",
            " versions ",
            " versiones ",
            " versoes ",
            " versioni ",
            " versionen ",
            " select ",
            " selected ",
            " adapte ",
            " adapter ",
            " adaptes ",
            " adaptation ",
            " rends ",
            " rendre ",
            " transforme ",
            " transformer ",
            " priorise ",
            " prioriser ",
            " privilegie ",
            " privilegier ",
            " favorise ",
            " favoriser ",
            " evite ",
            " eviter ",
            " attention ",
            " risque ",
            " risques ",
            " budget ",
            " cout ",
            " couts ",
            " chf ",
            " manquent ",
            " manque ",
            " missing ",
            " prepare ",
            " preparer ",
            " plan ",
            " planning ",
            " planifier ",
            " organiser ",
            " organise ",
            " pour chaque ",
            " chaque ",
            " fais moi ",
            " fais nous ",
            " fais un ",
            " fais une ",
            " fais des ",
            " cree ",
            " creer ",
            " cree un ",
            " cree une ",
            " cree des ",
            " construis ",
            " construire ",
            " elabore ",
            " elaborer ",
            " compose ",
            " composer ",
            " complet utilisant ",
            " complete using ",
            " complete with ",
            " synthese ",
            " synthetise ",
            " synthetiser ",
            " resume actionnable ",
            " resumee actionnable ",
            " pas d un copier coller ",
            " pas de copier coller ",
            " retroplanning ",
            " faq ",
            " questions de verification ",
            " verification questions ",
            " a partir de ",
            " depuis le corpus ",
            " dans le corpus ",
            " depuis les documents ",
            " dans les documents ",
            " il me faut ",
            " suggest ",
            " suggests ",
            " recommend ",
            " recommends ",
            " help me ",
            " adapt ",
            " adapted ",
            " adaptation ",
            " transform ",
            " transform into ",
            " for each ",
            " each ",
            " prioritize ",
            " prioritise ",
            " prefer ",
            " favor ",
            " favour ",
            " make me ",
            " create ",
            " build ",
            " draft ",
            " synthesize ",
            " synthesis ",
            " what to do ",
            " not covered ",
            " is covered ",
            " legal advice ",
            " claim ",
            " from this corpus ",
            " from the corpus ",
            " from documents ",
            " from the documents ",
            " prepare ",
            " plan ",
            " planning ",
            " organize ",
            " organise ",
            " need ",
            " propone ",
            " proponer ",
            " sugiere ",
            " sugerir ",
            " recomienda ",
            " recomendar ",
            " ayudame ",
            " hazme ",
            " crea ",
            " crear ",
            " elabora ",
            " elaborar ",
            " sintetiza ",
            " preparar ",
            " planificar ",
            " organizar ",
            " preciso ",
            " sugere ",
            " sugerir ",
            " recomenda ",
            " recomendar ",
            " ajuda me ",
            " crie ",
            " criar ",
            " elabore ",
            " elaborar ",
            " sintetiza ",
            " sintetizar ",
            " preparar ",
            " planear ",
            " organizar ",
            " consiglia ",
            " consigliare ",
            " suggerisci ",
            " suggerire ",
            " aiutami ",
            " adatta ",
            " adattare ",
            " preferisci ",
            " crea ",
            " creare ",
            " componi ",
            " comporre ",
            " sintetizza ",
            " preparare ",
            " pianifica ",
            " organizzare ",
            " vorschlag ",
            " vorschlagen ",
            " empfiehl ",
            " empfehlen ",
            " hilf mir ",
            " anpassen ",
            " bevorzuge ",
            " bevorzugen ",
            " erstelle ",
            " erstellen ",
            " mach mir ",
            " aus dem corpus ",
            " aus den dokumenten ",
            " vorbereiten ",
            " plane ",
            " planen ",
            " organisieren ");


    internal static bool ContainsEvidenceQualityScopedSynthesisIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsSourceReliabilityGuidanceIntent(normalized)
            || ContainsUnsupportedClaimVerificationIntent(normalized)
            || ContainsCorpusOrientationGuidanceIntent(normalized)
            || ContainsAny(normalized,
                " classer ",
                " classe ",
                " classes ",
                " classement ",
                " trier ",
                " trie ",
                " tries ",
                " tri ",
                " rank ",
                " ranked ",
                " ranking ",
                " sort ",
                " sorted ",
                " sorting ",
                " priorise ",
                " prioriser ",
                " priorisation ",
                " prioritize ",
                " prioritise ",
                " priorisieren ",
                " priorisierung ",
                " extraction ",
                " extrait ",
                " extraite ",
                " extraits ",
                " extraites ",
                " extracted ",
                " extrahiert ",
                " illisible ",
                " illisibles ",
                " unreadable ",
                " unlesbar ",
                " incomplet ",
                " incomplete ",
                " incomplete ",
                " incomplets ",
                " incompletes ",
                " manual review ",
                " revue manuelle ",
                " validation visuelle ",
                " visual validation ",
                " differente ",
                " differentes ",
                " differents ",
                " different statements ",
                " unterschiedliche aussagen ")
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|welche)\b.{0,100}\b(?:documents?|pdf|sources?|files?|dokumente)\b.{0,120}\b(?:tableaux?|tables?|symboles?|symbols?|pictogrammes?|pictograms?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:texte|text|inhalt)\b.{0,80}\b(?:extrait|extracted|extrahiert|illisible|unreadable|unlesbar|incomplet|incomplete)\b",
                RegexOptions.CultureInvariant);
    }


    private static bool ContainsCorpusOrientationGuidanceIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsAny(normalized,
                " perdu dans ce dossier ",
                " perdue dans ce dossier ",
                " perdu dans les documents ",
                " perdue dans les documents ",
                " decouvre le sujet ",
                " decouvrir le sujet ",
                " guider comme si ",
                " guide moi ",
                " guider ",
                " par ou commencer ",
                " ou commencer ",
                " premiers reperes ",
                " vue d ensemble ",
                " folder overview ",
                " guide me ",
                " where to start ",
                " first landmarks ")
            && ContainsAny(normalized,
                " dossier ",
                " documents ",
                " pdf ",
                " corpus ",
                " sujet ",
                " topic ",
                " folder ",
                " files ");
    }


    private static bool ContainsSourceReliabilityGuidanceIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsAny(normalized,
                " sans me tromper ",
                " sans se tromper ",
                " confiance ",
                " source scannee ",
                " source scanne ",
                " pdf scanne ",
                " scan ",
                " scanne ",
                " ocr ",
                " images et pas de texte ",
                " image et pas de texte ",
                " pas de texte ",
                " reponse honnete ",
                " reponse sure ",
                " supposition ",
                " verifie ou ",
                " verifies ou ",
                " verifier ou ",
                " ou chercher ",
                " quoi chercher ",
                " mal compris ",
                " mauvaise reponse ",
                " reponse prudente ",
                " repondre proprement ",
                " citer ses sources ",
                " citer les sources ",
                " instruction documentaire ",
                " document instruction ",
                " source quality ",
                " scanned source ",
                " scanned pdf ",
                " scanned document ",
                " honest answer ",
                " safe answer ",
                " confidence ",
                " where to look ",
                " misunderstood ",
                " wrong answer ",
                " bad answer ",
                " cite sources ")
            || Regex.IsMatch(
                normalized,
                @"\b(?:comment|how)\b.{0,120}\b(?:repondre|answer|indiquer|state|citer|cite|verifier|verify)\b.{0,120}\b(?:source|sources|confiance|confidence|scan|scanned|ocr|supposition|guess)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:si|if)\b.{0,120}\b(?:texte|text|images?|ocr|source|sources?)\b.{0,120}\b(?:pas|not|absent|missing|empty|confond|confuses?|illisible|unreadable)\b",
                RegexOptions.CultureInvariant);
    }


    private static bool ContainsUnsupportedClaimVerificationIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasScopedCorpus = ContainsAny(normalized,
            " corpus ",
            " documents ",
            " document ",
            " sources ",
            " source ",
            " pdf ",
            " pdfs ",
            " fichiers ",
            " fichier ",
            " files ",
            " file ",
            " category ",
            " categorie ",
            " base ");
        if (!hasScopedCorpus)
            return false;

        var asksVerification = ContainsAny(normalized,
                " verifie si ",
                " verifier si ",
                " verifie que ",
                " verifier que ",
                " confirme si ",
                " confirmer si ",
                " est ce prouve ",
                " est prouve ",
                " c est prouve ",
                " est demontre ",
                " c est demontre ",
                " verify whether ",
                " verify if ",
                " check whether ",
                " check if ",
                " confirm whether ",
                " confirm if ",
                " is it proved ",
                " is it proven ",
                " is proved ",
                " is proven ",
                " is demonstrated ",
                " is documented ")
            || Regex.IsMatch(
                normalized,
                @"\bv\W*rifie\w*\s+si\b|\bverif\w*\s+si\b|\bverify\s+(?:whether|if)\b|\bcheck\s+(?:whether|if)\b|\bconfirm\s+(?:whether|if)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:prouv\w*|proved|proven|d\W*montr\w*|demonstrated|documented)\b.{0,120}\b(?:limit\w*|limited|recommand\w*|recommended|non\s+d\W*montr\w*|not\s+demonstrated|unsupported|unproved|unproven)\b",
                RegexOptions.CultureInvariant);
        if (!asksVerification)
            return false;

        return ContainsAny(normalized,
                " toujours ",
                " always ",
                " universel ",
                " universelle ",
                " universal ",
                " general ",
                " generale ",
                " complete ",
                " complet ",
                " tout ",
                " toute ",
                " toutes ",
                " all ",
                " prouv ",
                " prouve ",
                " prouvee ",
                " prouvees ",
                " proven ",
                " proved ",
                " d montre ",
                " demontre ",
                " demontree ",
                " demontrees ",
                " demonstrated ",
                " documente ",
                " documented ",
                " recommande ",
                " recommandee ",
                " recommended ",
                " non demontre ",
                " not demonstrated ",
                " unsupported ")
            || ExtractQuotedLookupPhrases(normalized).Count > 0;
    }


    internal static bool ContainsSituationalBroadSelectionIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
                normalized,
                @"\b(?:je\s+(?:veux|dois|voudrais)|j\s+aimerais|i\s+(?:want|need|would\s+like|have\s+to)|quiero|necesito|gostaria|preciso|quero|voglio|ich\s+(?:mochte|muss|brauche))\b.{0,120}\b(?:avec|pour|con|com|per|mit|adapte|adapter|adaptes|adapted|adapt|adaptar|adequad\w*|adattare|adatt\w*|anpassen|geeignet|suitable|options?|choisir|choix|choose|select|selection|recommande|recommend|plan|planning|organis|prepare|preparer|preparar|priorise|prioriser|privilegie|privilegier|prioritize|prioritise|prefer|favor|favour|bevorzugen|vorbereiten)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,100}\b(?:choisir|choix|choose|select|selection|recommande|recommend|adapte|adapter|adapt|adaptar|adequad\w*|adattare|adatt\w*|anpassen|geeignet|suitable|priorise|prioriser|privilegie|privilegier|prioritize|prioritise|prefer|favor|favour|bevorzugen)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,120}\b(?:peuvent|peut|can|could|possono|pueden|podem|konnen)\b.{0,120}\b(?:prepar\w*|prepared|advance|avance|adapt|adapte|adapter|adequad\w*|adatt\w*|geeignet|suitable|priorise|prioriser|privilegie|privilegier|prefer|favor|favour)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,120}\b(?:utilisent|utilise|uses?|contiennent|contient|contains?|mentionnent|mentionne|mentions?)\b.{0,120}\b(?:gerer|gere|handle|manage|eviter|avoid|sans|without)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:trouve|trouver|retrouve|retrouver|cherche|chercher|identifie|identifier|find|search|identify)\b.{0,100}\b(?:deux|two|plusieurs|several|multiple)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:regroupe|regrouper|group|group\s+by|combine|traduis|traduire|translate|ubersetze|traduce|traduz)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:ajoute|ajouter|liste|lister|extrais|extraire|resume|resumer|add|list|extract|summarize)\b.{0,80}\b(?:points?\s+critiques?|critical\s+points?|a\s+surveiller|to\s+watch|watch\s+outs?|risques?|risks?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:points?\s+critiques?|critical\s+points?|a\s+surveiller|to\s+watch|watch\s+outs?)\b.{0,80}\b(?:eviter|avoid|rater|ratage|failure|fail)\b",
                RegexOptions.CultureInvariant);
    }


    internal static bool ShouldAllowSparseAssistForScopedProfileFallback(string query)
    {
        var tokens = BuildDocumentProfileSpecificityTokens(query)
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        var concreteTokenCount = tokens.Count(static token =>
            token.Length >= 5
            && !token.Any(char.IsDigit)
            && !BroadDiversityGenericSubjectTokens.Contains(token)
            && !BroadDiversityAbstractAssistTokens.Contains(token)
            && !DocumentOverviewTopicStopwords.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token)
            && !SpecificAnchorStopwords.Contains(token)
            && !IsGenericDocumentProfileSpecificityToken(token)
            && token is not "dossier" and not "dossiers" and not "personne" and not "personnes");
        if (concreteTokenCount >= 2
            && ContainsAny(
                normalized,
                " parle de ",
                " parle d ",
                " parlent de ",
                " parlent d ",
                " passages ",
                " associe a ",
                " associes a ",
                " associated with ",
                " documents qui ",
                " documents that ",
                " cherche dans le corpus ",
                " retrouve les documents "))
        {
            return true;
        }

        return tokens.Length >= 2
            && (tokens.Any(static token => SpecificAnchorStopwords.Contains(token))
                || HasConcreteScopedSelectionSparseAssistSubject(query, tokens));
    }


    private static bool HasConcreteScopedSelectionSparseAssistSubject(string query, IReadOnlyList<string> tokens)
    {
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (!ContainsSituationalBroadSelectionIntent(normalized))
            return false;

        var concreteTokenCount = tokens.Count(static token =>
            token.Length >= 5
            && !token.Any(char.IsDigit)
            && !BroadDiversityGenericSubjectTokens.Contains(token)
            && !BroadDiversityAbstractAssistTokens.Contains(token)
            && !DocumentOverviewTopicStopwords.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token)
            && !SpecificAnchorStopwords.Contains(token)
            && !IsGenericDocumentProfileSpecificityToken(token)
            && token is not "dossier" and not "dossiers" and not "personne" and not "personnes");

        return concreteTokenCount >= 2;
    }


    internal static bool ShouldRequireDocumentOverviewProfileMatch(string query)
    {
        if (!ContainsDocumentOverviewIntent(query))
            return false;
        if (ShouldUseCatalogDocumentOverviewFallback(query))
            return false;

        var topicTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .ToArray();
        if (topicTokens.Length == 0)
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsAny(normalized,
            " qui parle ",
            " qui parlent ",
            " parle de ",
            " parle d ",
            " parlent de ",
            " parlent d ",
            " mentionne ",
            " mentionnent ",
            " contient ",
            " contiennent ",
            " traite de ",
            " traite d ",
            " traitent de ",
            " traitent d ",
            " au sujet de ",
            " en lien avec ",
            " concernant ",
            " sur ",
            " about ",
            " related to ",
            " concerning ",
            " mention ",
            " mentions ",
            " mentioning ",
            " contain ",
            " contains ",
            " containing ",
            " cover ",
            " covers ",
            " covering ",
            " hablan de ",
            " habla de ",
            " mencionan ",
            " menciona ",
            " contienen ",
            " contiene ",
            " sobre ",
            " falam de ",
            " fala de ",
            " mencionam ",
            " menciona ",
            " contem ",
            " sobre ",
            " parlano di ",
            " parla di ",
            " menzionano ",
            " menziona ",
            " contengono ",
            " contiene ",
            " riguard ",
            " su ",
            " sprechen uber ",
            " sprechen daruber ",
            " sprechen darueber ",
            " sprechen ueber ",
            " erwahnt ",
            " erwahnen ",
            " enthalt ",
            " enthalten ",
            " uber ",
            " ueber ");
    }


    internal static bool ShouldPreferComparativeDocumentDiversity(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(normalized,
                " compare ",
                " comparer ",
                " compares ",
                " comparing ",
                " comparative ",
                " comparaison ",
                " comparatif ",
                " compara ",
                " comparar ",
                " comparacion ",
                " comparacao ",
                " confronto ",
                " confronta ",
                " confronto ",
                " difference entre ",
                " differences entre ",
                " difference between ",
                " differences between ",
                " distinction ",
                " distinguer ",
                " distinguish ",
                " distinguish between ",
                " vs ",
                " versus ",
                " vergleich ",
                " vergleichen ",
                " choisir ",
                " choisis ",
                " choix entre ",
                " choose ",
                " choose between ",
                " escoge ",
                " escolher ",
                " scegliere ",
                " wahlen "))
            return true;

        if (CountVersionLikeQueryTokens(normalized) >= 2
            && ContainsAny(
                normalized,
                " cite ",
                " citer ",
                " cited ",
                " contient aussi ",
                " contains also ",
                " also contains ",
                " dois je citer ",
                " should i cite ",
                " quelle version ",
                " which version ",
                " version correcte ",
                " correct version ",
                " mettre a jour ",
                " update ",
                " mise a jour ",
                " differences ",
                " difference ",
                " verifier ",
                " verify ",
                " invalide ",
                " invalidate ",
                " invalidates ",
                " totalement ",
                " exactly ",
                " exactement ",
                " meme identifiant ",
                " same identifier ",
                " contenu ",
                " content ",
                " fusionner ",
                " fusionne ",
                " merge ",
                " merged ",
                " sans indiquer ",
                " without citing ",
                " sans les sources ",
                " without sources ",
                " conflit ",
                " conflict "))
        {
            return true;
        }

        if (ContainsAny(normalized, " ancienne version ", " old version ", " older version ")
            && ContainsAny(normalized, " nouvelle ", " nouveau ", " plus recente ", " plus recent ", " newer ", " more recent "))
        {
            return true;
        }

        if (ContainsAny(normalized, " deux rapports ", " two reports ", " deux documents ", " two documents ")
            && ContainsAny(
                normalized,
                " ne disent pas pareil ",
                " disent pas pareil ",
                " pas pareil ",
                " ne disent pas la meme chose ",
                " do not say the same thing ",
                " don't say the same thing "))
        {
            return true;
        }

        if (ContainsAny(normalized, " deux documents ", " deux pdf ", " deux fichiers ", " two documents ", " two pdfs ", " two files ")
            && ContainsAny(normalized,
                " meme chose ",
                " memes choses ",
                " meme information ",
                " nuance ",
                " contradiction ",
                " different ",
                " differente ",
                " differentes ",
                " same thing ",
                " same information ",
                " nuance ",
                " contradiction ",
                " differ ",
                " differs "))
        {
            return true;
        }

        if (ContainsAny(normalized, " le plus ", " la plus ", " les plus ", " most ", " mas ", " mais ", " am meisten "))
            return ContainsAny(normalized,
                " quel ",
                " quelle ",
                " quels ",
                " quelles ",
                " lequel ",
                " laquelle ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welches ",
                " welcher ",
                " welche ");

        return false;
    }


    private static int CountVersionLikeQueryTokens(string normalizedQuery)
        => normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Replace(" ", string.Empty, StringComparison.Ordinal))
            .Where(IsComparativeVersionDocumentOperand)
            .Distinct(StringComparer.Ordinal)
            .Count();


    internal static bool ShouldUseGenericVersionConflictOverviewRecovery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return CountVersionLikeQueryTokens(normalized) == 0
               && ContainsAny(normalized, " ancienne version ", " old version ", " older version ")
               && ContainsAny(normalized, " nouvelle ", " nouveau ", " plus recente ", " plus recent ", " newer ", " more recent ");
    }


    internal static bool ContainsDocumentOverviewIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsDocumentRoleClassificationIntent(normalized))
            return true;

        if (ContainsAny(normalized,
                " quels documents ",
                " quelles sources ",
                " quels fichiers ",
                " quels pdf ",
                " quels livres ",
                " quels rapports ",
                " quels manuels ",
                " quels documents sont ",
                " quels documents tu ",
                " quels livres tu ",
                " quels pdf tu ",
                " which documents ",
                " which sources ",
                " which files ",
                " which pdfs ",
                " which books ",
                " which reports ",
                " which manuals ",
                " what documents ",
                " what sources ",
                " what files ",
                " que documentos ",
                " que fuentes ",
                " que archivos ",
                " que pdf ",
                " que libros ",
                " quais documentos ",
                " quais fontes ",
                " quais ficheiros ",
                " quais arquivos ",
                " quais pdf ",
                " quais livros ",
                " welche dokumente ",
                " welche quellen ",
                " welche dateien ",
                " welche pdf ",
                " welche bucher ",
                " welche berichte ",
                " welche handbucher ",
                " wichtigsten dokumente ",
                " wichtigste dokumente ",
                " wichtigsten pdf ",
                " wichtigste pdf ",
                " quali documenti ",
                " quali fonti ",
            " quali file ",
            " quali pdf ",
            " quali libri ",
            " quali manuali "))
        {
            return true;
        }

        if (ContainsCorpusExistenceIntent(normalized) && ContainsDocumentCorpusCountIntent(normalized))
            return true;

        var hasOverviewIntent = ContainsAny(normalized,
            " vue d ensemble ",
            " vue globale ",
            " inventaire ",
            " liste ",
            " disponibles ",
            " disponible ",
            " a quoi ils servent ",
            " a quoi elles servent ",
            " par grands themes ",
            " qu il faut retenir ",
            " ce qu il faut retenir ",
            " que faut il retenir ",
            " points cles ",
            " principaux points ",
            " sans rentrer dans tous les details ",
            " overview ",
            " inventory ",
            " key takeaways ",
            " takeaways ",
            " main points ",
            " without going into every detail ",
            " available ",
            " what they are for ",
            " what each is for ",
            " high level view ",
            " vista general ",
            " panorama ",
            " inventario ",
            " disponibles ",
            " visao geral ",
            " inventario ",
            " disponiveis ",
            " uberblick ",
            " verfugbar ",
            " dieser kategorie ",
            " nach thema gruppieren ",
            " thematisch gruppieren ",
            " nach themen gruppieren ",
            " wofur sie nutzlich ",
            " wofuer sie nutzlich ",
            " wofur sie nuetzlich ",
            " wofuer sie nuetzlich ",
            " nutzlich sind ",
            " nuetzlich sind ",
            " einordnen ",
            " panoramica ",
            " disponibili ");
        if (!hasOverviewIntent)
            return false;

        if (ContainsAny(normalized,
            " vue d ensemble ",
            " vue globale ",
            " par grands themes ",
            " high level view ",
            " overview ",
            " inventory ",
            " panorama ",
            " uberblick ",
            " panoramica "))
        {
            return true;
        }

        return ContainsAny(normalized,
            " document ",
            " documents ",
            " source ",
            " sources ",
            " fichier ",
            " fichiers ",
            " file ",
            " files ",
            " pdf ",
            " livre ",
            " livres ",
            " book ",
            " books ",
            " rapport ",
            " rapports ",
            " report ",
            " reports ",
            " manuel ",
            " manuels ",
            " manual ",
            " manuals ",
            " categorie ",
            " category ",
            " kategorie ",
            " corpus ",
            " base ",
            " knowledge base ",
            " documentos ",
            " fuentes ",
            " archivos ",
            " libros ",
            " ficheiros ",
            " arquivos ",
            " livros ",
            " dokumente ",
            " quellen ",
            " dateien ",
            " bucher ",
            " berichte ",
            " handbucher ",
            " documenti ",
            " fonti ",
            " libri ",
            " manuali ");
    }


    private static bool ContainsDocumentRoleClassificationIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasDocumentSubject = ContainsAny(normalized,
            " quels documents ",
            " quelles sources ",
            " quels fichiers ",
            " quels pdf ",
            " lesquels ",
            " lesquelles ",
            " ces documents ",
            " ces sources ",
            " which documents ",
            " what documents ",
            " which files ",
            " these documents ",
            " these files ",
            " welche dokumente ",
            " quali documenti ",
            " quais documentos ",
            " que documentos ");
        if (!hasDocumentSubject)
            return false;

        var hasClassificationAction = ContainsAny(normalized,
            " semblent ",
            " semble ",
            " paraissent ",
            " parait ",
            " classer ",
            " classe ",
            " classifier ",
            " categoriser ",
            " lesquels sont ",
            " lesquelles sont ",
            " which are ",
            " seem ",
            " seems ",
            " appear ",
            " appears ",
            " classify ",
            " categorise ",
            " categorize ",
            " gruppieren ",
            " einordnen ");
        if (!hasClassificationAction)
            return false;

        return ContainsAny(normalized,
            " guide ",
            " guides ",
            " manuel ",
            " manuels ",
            " manual ",
            " manuals ",
            " condition ",
            " conditions ",
            " exigence ",
            " exigences ",
            " requirement ",
            " requirements ",
            " support technique ",
            " supports techniques ",
            " technical support ",
            " technical supports ",
            " role ",
            " roles ",
            " type ",
            " types ",
            " categorie ",
            " categories ",
            " category ",
            " categories ");
    }


    internal static bool ContainsDocumentRoleClassificationSurface(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (ContainsDelimitedDocumentRoleClassificationSurface(query))
            return true;

        var normalized = $" {FoldDiacritics(query).ToLowerInvariant()
            .Replace('/', ' ')
            .Replace('\\', ' ')} ";
        normalized = SafeRegexReplace(
            normalized,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        return ContainsDocumentRoleClassificationIntent(normalized);
    }


    internal static bool ContainsDelimitedDocumentRoleClassificationSurface(string query)
    {
        var raw = FoldDiacritics(query).ToLowerInvariant();
        if (!raw.Contains('/', StringComparison.Ordinal)
            && !raw.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (!ContainsAny($" {raw} ",
                " document ",
                " documents ",
                " source ",
                " sources ",
                " fichier ",
                " fichiers ",
                " file ",
                " files ",
                " pdf ",
                " pdfs "))
        {
            return false;
        }

        var roleFamilies = 0;
        if (raw.Contains("guide", StringComparison.Ordinal)
            || raw.Contains("manual", StringComparison.Ordinal)
            || raw.Contains("manuel", StringComparison.Ordinal))
        {
            roleFamilies++;
        }
        if (raw.Contains("condition", StringComparison.Ordinal)
            || raw.Contains("exigence", StringComparison.Ordinal)
            || raw.Contains("requirement", StringComparison.Ordinal))
        {
            roleFamilies++;
        }
        if (raw.Contains("support", StringComparison.Ordinal)
            || raw.Contains("technique", StringComparison.Ordinal)
            || raw.Contains("technical", StringComparison.Ordinal))
        {
            roleFamilies++;
        }
        if (raw.Contains("role", StringComparison.Ordinal)
            || raw.Contains("type", StringComparison.Ordinal)
            || raw.Contains("categorie", StringComparison.Ordinal)
            || raw.Contains("category", StringComparison.Ordinal))
        {
            roleFamilies++;
        }

        return roleFamilies >= 2;
    }


    internal static bool ShouldUseCatalogDocumentOverviewFallback(string query)
    {
        if (!ContainsDocumentOverviewIntent(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(
                normalized,
                " je ne sais pas ",
                " i do not know ",
                " i don't know ",
                " ma question ",
                " my question ",
                " clarification ",
                " clarifier ",
                " precise ma demande ",
                " source inexistante ",
                " fichier inexistant ",
                " nonexistent "))
        {
            return false;
        }

        var hasCatalogScope = ContainsAny(
            normalized,
            " cette categorie ",
            " la categorie ",
            " this category ",
            " the category ",
            " category ",
            " categorie ",
            " kategorie ",
            " dieser kategorie ",
            " dossier ",
            " folder ",
            " corpus ");
        if (!hasCatalogScope)
            return false;

        return ContainsAny(
            normalized,
            " quels documents ",
            " quels pdf ",
            " quels fichiers ",
            " which documents ",
            " which pdfs ",
            " which files ",
            " what documents ",
            " what pdfs ",
            " what files ",
            " documents contient ",
            " contient cette categorie ",
            " contains this category ",
            " overview ",
            " vue d ensemble ",
            " inventaire ",
            " inventory ",
            " qu il faut retenir ",
            " ce qu il faut retenir ",
            " que faut il retenir ",
            " points cles ",
            " principaux points ",
            " key takeaways ",
            " takeaways ",
            " main points ",
            " wichtigsten dokumente ",
            " wichtigste dokumente ",
            " wichtigsten pdf ",
            " nach thema gruppieren ",
            " thematisch gruppieren ",
            " nutzlich sind ",
            " nuetzlich sind ",
            " cartographie ",
            " map ",
            " classer ",
            " classify ",
            " a quoi servent ",
            " what they are for ",
            " useful for real business questions ",
            " useful documents ");
    }


    internal static bool ShouldUseScopedCatalogDocumentOverviewFallback(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query)
            || string.Equals(mode, "focused", StringComparison.Ordinal)
            || ExtractQuotedLookupPhrases(query).Count > 0)
        {
            return false;
        }

        var raw = query.ToLowerInvariant();
        var hasRawVagueCategoryGuidanceIntent =
            raw.Contains("actionnable", StringComparison.Ordinal)
            || raw.Contains("copier-coller", StringComparison.Ordinal)
            || raw.Contains("copier coller", StringComparison.Ordinal)
            || raw.Contains("quel document utiliser", StringComparison.Ordinal)
            || raw.Contains("quelle source utiliser", StringComparison.Ordinal);
        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var hasVagueCategoryGuidanceIntent = ContainsAny(normalized,
            " je ne sais pas quel document ",
            " quel document utiliser ",
            " quelle source utiliser ",
            " quelles sources utiliser ",
            " document utiliser ",
            " source utiliser ",
            " je suis perdu ",
            " je suis perdue ",
            " guide moi ",
            " guider ",
            " decouvre le sujet ",
            " resume actionnable ",
            " resumee actionnable ",
            " synthese actionnable ",
            " pas d un copier coller ",
            " pas de copier coller ",
            " points importants ",
            " vraiment sources ",
            " dossier dense ",
            " what document to use ",
            " which document to use ",
            " which source to use ",
            " actionable summary ",
            " not a copy paste ",
            " not copy paste ",
            " key points ",
            " important points ");
        if (!hasRawVagueCategoryGuidanceIntent && !hasVagueCategoryGuidanceIntent)
            return false;

        return !HasReferenceLikeQueryToken(query);
    }


    internal static bool ShouldRunScopedCatalogEmptySelectionRecovery(string query, bool hasCategoryFilter, string mode)
    {
        var normalized = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var hasUnsupportedClaimVerification = ContainsUnsupportedClaimVerificationIntent(normalized);
        if (!hasCategoryFilter
            || string.IsNullOrWhiteSpace(query)
            || string.Equals(mode, "focused", StringComparison.Ordinal)
            || (ExtractQuotedLookupPhrases(query).Count > 0 && !hasUnsupportedClaimVerification)
            || HasReferenceLikeQueryToken(query))
        {
            return false;
        }

        return ContainsDocumentOverviewIntent(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || hasUnsupportedClaimVerification
            || ContainsSituationalBroadSelectionIntent(normalized)
            || (ShouldPreferComparativeDocumentDiversity(query)
                && ShouldTreatAsBroadDiversityQuery(query, normalized));
    }


    internal static int ResolveDefaultCandidateCount(string mode, bool preferComparativeDiversity, int topK)
        => mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * (preferComparativeDiversity ? 15 : 12),
            "balanced" when preferComparativeDiversity => topK * 15,
            _ => topK * 10
        };


    internal static bool ShouldRunBroadDiversityCandidateBackfill(string query, int selectedCount, int topK)
    {
        if (selectedCount >= topK || topK <= 0 || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return (ShouldPreferComparativeDocumentDiversity(query)
                && ShouldTreatAsBroadDiversityQuery(query, normalized))
            || IsRecommendationSelectionQuery(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized);
    }


    internal static bool ShouldRunBroadDiversityCandidateCoverage(string query, int topK)
    {
        if (topK < 4 || string.IsNullOrWhiteSpace(query))
            return false;
        if (ExtractQuotedLookupPhrases(query).Count > 0 || HasReferenceLikeQueryToken(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsSituationalBroadSelectionIntent(normalized)
            || (ShouldPreferComparativeDocumentDiversity(query)
                && ShouldTreatAsBroadDiversityQuery(query, normalized));
    }


    internal static int ResolveDocumentProfileCandidateCount(
        int candidates,
        int topK,
        bool profileOnly,
        bool comparativeProfileAssist = false)
    {
        if (topK <= 0 || candidates <= 0)
            return 0;

        var desired = comparativeProfileAssist
            ? Math.Clamp(Math.Max(topK + 8, topK * 2), topK, 48)
            : Math.Max(topK, 12);
        return Math.Min(candidates, desired);
    }


    internal static int ResolveSqlHeavyRetrieverCandidateLimit(int candidates, int topK)
    {
        if (topK <= 0 || candidates <= 0)
            return 0;

        var desired = Math.Max(topK, Math.Max(topK * 6, 64));
        return Math.Min(candidates, desired);
    }


    internal static int ResolveExactMatchCandidateLimit(int topK)
        => Math.Clamp(topK * 6, Math.Max(topK, 12), 96);


    internal static int ResolveLocalTitleTokenCandidateLimit(int topK, bool hasDocScope)
        => hasDocScope
            ? topK
            : Math.Clamp(topK * 4, Math.Max(topK, 32), 128);


    internal static int ResolveTitleAnchorNavigationCatalogLimit(int topK)
    {
        if (topK <= 0)
            return 0;

        // Internal recovery windows can be larger than this catalog-route cap.
        // Saturate explicitly instead of using topK as Math.Clamp's lower bound.
        return topK >= 16 ? 48 : topK * 3;
    }


    internal static int ResolveLocalTitleTokenSqlCandidateLimit(
        int routeLimit,
        bool hasDocScope,
        bool hasCategoryScope = false,
        bool fullFallback = true)
    {
        if (routeLimit <= 0)
            return 0;

        var maxCandidateLimit = hasDocScope
            ? 320
            : hasCategoryScope && !fullFallback
                ? 320
                : 1024;
        maxCandidateLimit = Math.Max(routeLimit, maxCandidateLimit);
        return Math.Clamp(routeLimit * 16, routeLimit, maxCandidateLimit);
    }


    internal static bool ShouldRetryLocalTitleTokenSqlCoverage(
        int returnedRowCount,
        int initialCandidateLimit,
        int fullCandidateLimit,
        int matchCount,
        int topK,
        bool hasCategoryScope = false)
    {
        if (topK <= 0
            || initialCandidateLimit <= 0
            || fullCandidateLimit <= initialCandidateLimit
            || returnedRowCount < initialCandidateLimit)
        {
            return false;
        }

        if (hasCategoryScope)
            return matchCount == 0;

        return matchCount < topK;
    }


    internal static int ResolveDefaultMaxPerDoc(string mode, bool preferComparativeDiversity, int topK)
        => preferComparativeDiversity && mode != "focused"
            ? 1
            : mode switch
            {
                "focused" => Math.Min(topK, 3),
                "broad" => 2,
                _ => Math.Min(3, Math.Max(2, topK / 2))
            };


    internal static bool ShouldAllowMultipleChunksForComparativeSubject(string query)
        => ShouldAllowSparseAssistForBroadDiversity(query)
           && !ShouldUseBareVsComparativeOperandRoute(query)
           && BuildSupplementalSparseLexicalFallbackTerms(query).Any(static term => term.Contains(' '));


    internal static IReadOnlyList<string> BuildComparativeSubjectBackfillQueries(string query)
    {
        if (!ShouldAllowMultipleChunksForComparativeSubject(query))
            return Array.Empty<string>();

        var terms = BuildSupplementalSparseLexicalFallbackTerms(query);
        var phraseQueries = terms
            .Where(static term => term.Contains(' '))
            .Where(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length is >= 2 and <= 3)
            .OrderByDescending(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(IsInfinitiveLikeSubjectToken))
            .ThenByDescending(static term => StartsWithInfinitiveLikeSubjectToken(term))
            .ThenBy(static term => EndsWithPluralSubjectToken(term))
            .ThenByDescending(static term => term.Length)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Take(1)
            .ToArray();
        var phraseHeadTokens = phraseQueries
            .SelectMany(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static token => IsInfinitiveLikeSubjectToken(token) || token.Length >= 7);
        var singleQueries = phraseHeadTokens
            .Concat(terms.Where(static term => !term.Contains(' ')))
            .Where(static term => !term.Contains(' '))
            .Where(static term => IsInfinitiveLikeSubjectToken(term) || term.Length >= 7)
            .Where(static term => !LexicalStopwords.Contains(term))
            .Where(static term => !PrimaryAnchorStopwords.Contains(term))
            .Where(static term => !SpecificAnchorStopwords.Contains(term))
            .OrderByDescending(IsInfinitiveLikeSubjectToken)
            .ThenByDescending(static term => term.Length)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Take(1);

        return phraseQueries
            .Concat(singleQueries)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }


    internal static bool ShouldRunFinalComparativeSparseDiversityBackfill(string query, IReadOnlyList<RagMatch> selected)
        => selected.Count > 0
           && ShouldAllowMultipleChunksForComparativeSubject(query)
           && BuildComparativeSubjectBackfillQueries(query).Count > 0;


    internal static bool ShouldRunComparativeTitleBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query))
            return false;
        if (ShouldSkipDocumentProfileSearchForComparativeLookup(query))
            return true;
        if (!ShouldAllowDocumentProfileAssistForComparativeLookup(query))
            return false;

        if (BuildComparativeOperandTitleBackfillQueries(query).Count > 0)
            return true;

        var comparisonPhrases = ExtractComparativeLookupPhrases(query);
        if (comparisonPhrases.Count == 0 && string.IsNullOrWhiteSpace(BuildFocusedLexicalBackfillQuery(query)))
            return false;

        var leadingProfiles = selected
            .Where(IsDocumentProfileMatch)
            .Take(4)
            .ToArray();
        if (leadingProfiles.Length == 0)
            return true;
        if (!selected.Any(static match => match.MatchedContentCards is { Count: > 0 }))
            return true;

        var profileConstraintTokens = BuildDocumentProfileSpecificityTokens(query);
        return leadingProfiles.All(match =>
            match.MatchedContentCards is not { Count: > 0 }
            && ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, match) <= 0.0);
    }


    internal static IReadOnlyList<string> BuildComparativeTitleBackfillQueries(
        string query,
        bool allowProfileAssistBackfill = false)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query)
            || (!allowProfileAssistBackfill && !ShouldSkipDocumentProfileSearchForComparativeLookup(query)))
        {
            return Array.Empty<string>();
        }

        var queries = new List<string>();
        var operandBackfillQueries = BuildComparativeOperandTitleBackfillQueries(query);
        if (operandBackfillQueries.Count > 0)
        {
            foreach (var focusedQuery in operandBackfillQueries)
                AddComparativeTitleBackfillQuery(queries, focusedQuery);
            foreach (var operand in ExtractBareVsComparativeOperands(query)
                         .Where(IsLikelyCompactDocumentCodeOperand)
                         .Take(4))
            {
                AddComparativeTitleBackfillQuery(queries, operand);
            }

            return queries
                .Where(static phrase => ExtractTitlePruneTokens(phrase).Length is >= 2 and <= 6
                                        || IsLikelyCompactDocumentCodeOperand(phrase))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
        }

        foreach (var phrase in ExtractComparativeLookupPhrases(query))
        {
            AddComparativeTitleBackfillQuery(queries, phrase);
            foreach (var segment in SplitComparativeTitleBackfillSegments(phrase))
                AddComparativeTitleBackfillQuery(queries, segment);
        }

        AddComparativeTitleBackfillQuery(queries, BuildFocusedLexicalBackfillQuery(query));
        foreach (var operand in ExtractBareVsComparativeOperands(query)
                     .Where(IsLikelyCompactDocumentCodeOperand)
                     .Take(4))
        {
            AddComparativeTitleBackfillQuery(queries, operand);
        }

        return queries
            .Where(static phrase => ExtractTitlePruneTokens(phrase).Length is >= 2 and <= 6
                                    || IsLikelyCompactDocumentCodeOperand(phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    internal static bool ShouldUseBareVsComparativeOperandRoute(string query)
    {
        var operands = ExtractBareVsComparativeOperands(query);
        if (operands.Count < 2)
            return false;

        return BuildComparativeOperandConceptPhrases(query).Count > 0
               || operands.Count(IsComparativeVersionDocumentOperand) >= 2;
    }


    internal static bool ShouldUseReferenceVersionOperandRoute(string query)
        => ExtractReferenceVersionComparisonOperands(query).Count >= 2
           && ShouldPreferComparativeDocumentDiversity(query);


    internal static IReadOnlyList<string> BuildComparativeOperandTitleBackfillQueries(string query)
    {
        var operands = ExtractBareVsComparativeOperands(query);
        if (operands.Count < 2)
            return Array.Empty<string>();

        var conceptPhrases = BuildComparativeOperandConceptPhrases(query);
        if (conceptPhrases.Count == 0)
        {
            if (operands.Any(IsLikelyCompactDocumentCodeOperand))
            {
                var codeQueries = new List<string>();
                foreach (var operand in operands.Take(4))
                {
                    AddComparativeOperandTitleBackfillQuery(codeQueries, operand);
                    AddComparativeOperandTitleBackfillQuery(codeQueries, BuildComparativeOperandProfileVariantQuery(operand));
                }

                return codeQueries
                    .Distinct(StringComparer.Ordinal)
                    .Take(6)
                    .ToArray();
            }

            return Array.Empty<string>();
        }

        var queries = new List<string>();
        foreach (var operand in operands.Take(4))
        {
            AddComparativeOperandTitleBackfillQuery(queries, operand);
            AddComparativeOperandTitleBackfillQuery(queries, BuildComparativeOperandProfileVariantQuery(operand));
        }
        AddCanonicalComparativeSafetyBackfillQueries(queries, query, operands);
        foreach (var conceptPhrase in conceptPhrases.Where(IsHighConfidenceComparativeOperandConceptPhrase).Take(3))
            AddComparativeOperandTitleBackfillQuery(queries, conceptPhrase);

        if (queries.Distinct(StringComparer.Ordinal).Count() >= 2)
        {
            return queries
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToArray();
        }

        var primaryConcept = conceptPhrases[0];
        AddComparativeOperandTitleBackfillQuery(queries, primaryConcept);
        foreach (var operand in operands.Take(4))
            AddComparativeOperandTitleBackfillQuery(queries, $"{operand} {primaryConcept}");
        foreach (var operand in operands.Take(4))
            AddComparativeOperandTitleBackfillQuery(queries, $"{primaryConcept} {operand}");
        foreach (var conceptPhrase in conceptPhrases.Skip(1).Take(2))
            AddComparativeOperandTitleBackfillQuery(queries, conceptPhrase);

        return queries
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }


    internal static IReadOnlyList<string> BuildDocumentStatusOperandCompanionBackfillQueries(
        string query,
        IReadOnlyList<string> statusOperandTerms,
        IReadOnlyList<RagMatch> statusMatches)
    {
        if (statusOperandTerms.Count == 0 || statusMatches.Count == 0)
            return Array.Empty<string>();

        var queries = new List<string>();
        if (ShouldPreferComparativeDocumentDiversity(query))
        {
            foreach (var backfillQuery in BuildComparativeOperandTitleBackfillQueries(query)
                         .Concat(BuildComparativeTitleBackfillQueries(query, allowProfileAssistBackfill: true)))
            {
                AddDocumentStatusCompanionBackfillQuery(queries, backfillQuery);
            }
        }

        var hasExplicitComparativeCompanionQueries = queries.Count > 0;
        if (!hasExplicitComparativeCompanionQueries)
        {
            foreach (var match in statusMatches.Where(match => CountDocumentStatusOperandTitleHits(statusOperandTerms, match) > 0))
            {
                foreach (var phrase in BuildReferenceFamilyCompanionBackfillQueries(match))
                    AddDocumentStatusCompanionBackfillQuery(queries, phrase);
            }
        }

        return queries
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }


    private static void AddDocumentStatusCompanionBackfillQuery(List<string> queries, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = NormalizeForLexicalSignal(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return;
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 8)
            return;

        queries.Add(normalized);
    }


    private static IReadOnlyList<string> BuildReferenceFamilyCompanionBackfillQueries(RagMatch match)
    {
        if (!TryExtractReferenceVersionGroupKey(match, out var groupKey))
            return Array.Empty<string>();

        var tokens = groupKey
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 3)
            return Array.Empty<string>();

        if (tokens.Length == 3 && IsReferenceVersionYearToken(tokens[^1]))
            return [string.Join(' ', tokens.Take(tokens.Length - 1))];

        var familyPart = NormalizeReferenceFamilyPartToken(tokens[^1]);
        if (string.IsNullOrWhiteSpace(familyPart)
            || string.Equals(familyPart, tokens[^1], StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        tokens[^1] = familyPart;
        var phrases = new List<string> { string.Join(' ', tokens) };
        var titleSignal = NormalizeForLexicalSignal(string.Join(' ', new[] { match.DocName, match.DocPath }
            .Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (tokens[0] is "sp"
            && titleSignal.Contains("nist sp " + tokens[1], StringComparison.Ordinal))
        {
            phrases.Insert(0, "nist " + string.Join(' ', tokens));
        }

        return phrases
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
    }


    private static string NormalizeReferenceFamilyPartToken(string part)
    {
        var normalized = NormalizeForLexicalSignal(part).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var match = SafeRegexMatches(
                normalized,
                @"^(?<root>\d{1,5})(?:[a-z]|r\d+)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100))
            .FirstOrDefault();
        return match is { Success: true }
            ? match.Groups["root"].Value
            : normalized;
    }


    internal static bool ShouldRunComparativeOperandProfileBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        if (selected.Count == 0
            || !ShouldAllowDocumentProfileAssistForComparativeLookup(query)
            || BuildComparativeOperandProfileBackfillQueries(query).Count < 2)
        {
            return false;
        }

        return BuildComparativeOperandTitleBackfillQueries(query).Count == 0
               || !SelectionsCoverComparativeOperandTitleAnchors(query, selected);
    }


    internal static bool SelectionsCoverComparativeOperandTitleAnchors(string query, IReadOnlyList<RagMatch> selected)
    {
        var operands = ExtractBareVsComparativeOperands(query)
            .Select(static operand => NormalizeFocusedLookupPhrase(operand) ?? NormalizeForLexicalSignal(operand))
            .Where(static operand => !string.IsNullOrWhiteSpace(operand))
            .Where(static operand =>
            {
                var tokenCount = operand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                return tokenCount >= 2 || IsLikelyCompactDocumentCodeOperand(operand);
            })
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (operands.Length < 2)
            return true;

        var documentSignals = selected
            .Select(BuildComparativeOperandDocumentSignalText)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .ToArray();
        if (documentSignals.Length == 0)
            return false;

        return operands.All(operand =>
            documentSignals.Any(signal => ContainsComparativeOperandTitleAnchor(operand, signal)));
    }


    private static string BuildComparativeOperandDocumentSignalText(RagMatch match)
        => NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            match.DocName,
            match.DocPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));


    internal static IReadOnlyList<string> BuildComparativeOperandProfileBackfillQueries(string query)
    {
        var operands = ExtractBareVsComparativeOperands(query);
        if (operands.Count < 2)
            return Array.Empty<string>();

        var queries = new List<string>();
        foreach (var operand in operands
            .Select(static operand => NormalizeFocusedLookupPhrase(operand) ?? string.Empty)
            .Where(static operand =>
            {
                if (string.IsNullOrWhiteSpace(operand))
                    return false;

                var tokens = operand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return tokens.Length is >= 1 and <= 5
                       && tokens.Any(IsComparativeOperandConceptToken);
            }))
        {
            queries.Add(operand);
            var variantQuery = BuildComparativeOperandProfileVariantQuery(operand);
            if (!string.IsNullOrWhiteSpace(variantQuery))
                queries.Add(variantQuery);
        }

        return queries
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }


    private static string? BuildComparativeOperandProfileVariantQuery(string operand)
    {
        var phraseVariant = SelectCrossLanguageTechnicalPhraseVariant(operand);
        if (!string.IsNullOrWhiteSpace(phraseVariant)
            && !string.Equals(phraseVariant, operand, StringComparison.Ordinal))
        {
            return phraseVariant;
        }

        var tokens = operand
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsComparativeOperandConceptToken)
            .Select(SelectDocumentProfileVariantToken)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        var variant = string.Join(' ', tokens);
        if (tokens.Any(static token => token is "label" or "labels" or "sign" or "signs")
            && tokens.Any(static token => token is "safety" or "security"))
        {
            variant = "safety labels";
        }

        return string.Equals(variant, operand, StringComparison.Ordinal)
            ? null
            : variant;
    }


    private static string SelectDocumentProfileVariantToken(string token)
    {
        var variants = BuildLexicalTokenVariants(token);
        if (variants.Count == 0)
            return token;

        var preferred = variants.FirstOrDefault(static variant => variant switch
        {
            "cleanroom" or "electrical" or "machinery" or "equipment" or "requirement" or "safety" or "standard" => true,
            _ => false
        });
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return variants
            .OrderBy(static variant => ContainsNonAscii(variant) ? 1 : 0)
            .ThenBy(static variant => variant.Length)
            .ThenBy(static variant => variant, StringComparer.Ordinal)
            .First();
    }


    private static void AddComparativeOperandTitleBackfillQuery(List<string> queries, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = NormalizeFocusedLookupPhrase(value);
        if (string.IsNullOrWhiteSpace(normalized) && IsLikelyCompactDocumentCodeOperand(value))
            normalized = NormalizeForLexicalSignal(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (tokenCount is >= 2 and <= 6)
        {
            queries.Add(normalized);
            return;
        }

        if (tokenCount == 1 && IsLikelyCompactDocumentCodeOperand(normalized))
        {
            queries.Add(normalized);
            return;
        }

        if (tokenCount == 1
            && normalized.Length >= 7
            && !string.IsNullOrWhiteSpace(SelectCrossLanguageTechnicalPhraseVariant(normalized)))
        {
            queries.Add(normalized);
        }
    }


    internal static int ComputeComparativeOperandTitleSignal(string query, string? candidateTitleText)
    {
        if (string.IsNullOrWhiteSpace(candidateTitleText))
            return 0;

        var operands = ExtractBareVsComparativeOperands(query);
        if (operands.Count < 2)
            return 0;

        var normalizedCandidate = NormalizeForLexicalSignal(candidateTitleText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0;

        var matchedOperandCount = operands
            .Count(operand => ContainsComparativeOperandTitleAnchor(operand, normalizedCandidate));
        if (matchedOperandCount == 0)
            return 0;

        var phraseMatches = BuildComparativeOperandConceptPhrases(query)
            .Take(8)
            .Count(phrase => ContainsOrderedPhraseWindowInNormalizedText(normalizedCandidate, phrase, maxGapChars: 18)
                             || NormalizeForLexicalSignal(phrase)
                                 .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .All(token => ContainsExactNormalizedToken(normalizedCandidate, token)));

        var tokenMatches = BuildComparativeOperandConceptTokens(query)
            .Take(16)
            .Count(token => BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Any(variant =>
                {
                    var normalizedVariant = NormalizeForLexicalSignal(variant);
                    return normalizedVariant.Contains(' ')
                        ? ContainsOrderedPhraseWindowInNormalizedText(normalizedCandidate, normalizedVariant, maxGapChars: 18)
                        : ContainsExactNormalizedToken(normalizedCandidate, normalizedVariant);
                }));

        if (phraseMatches == 0 && tokenMatches < 2)
            return 0;

        return Math.Min(8, (matchedOperandCount * 3) + Math.Min(2, phraseMatches) + Math.Min(3, tokenMatches));
    }


    private static bool ContainsComparativeOperandTitleAnchor(string operand, string normalizedCandidate)
    {
        if (string.IsNullOrWhiteSpace(operand) || string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        var normalizedOperand = NormalizeForLexicalSignal(operand);
        if (string.IsNullOrWhiteSpace(normalizedOperand))
            return false;

        var operandTokens = normalizedOperand
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (operandTokens.Length == 0)
            return false;

        if (operandTokens.Length > 1)
            return operandTokens.All(token => ContainsExactNormalizedToken(normalizedCandidate, token));

        var token = operandTokens[0];
        if (ContainsExactNormalizedToken(normalizedCandidate, token))
            return true;

        if (!IsShortComparativeOperandToken(token))
            return false;

        return normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidateToken =>
                candidateToken.Length > token.Length
                && candidateToken.Length <= token.Length + 4
                && (candidateToken.StartsWith(token, StringComparison.Ordinal)
                    || candidateToken.EndsWith(token, StringComparison.Ordinal)));
    }


    internal static IReadOnlyList<string> ExtractBareVsComparativeOperands(string query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !ShouldPreferComparativeDocumentDiversity(query))
        {
            return Array.Empty<string>();
        }

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var operands = new List<string>();
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (!IsBareVsComparativeMarker(tokens[i]))
                continue;

            var left = ExtractLeftBareVsOperand(tokens, i);
            var right = ExtractRightBareVsOperand(tokens, i);
            if (!string.IsNullOrWhiteSpace(left))
                operands.Add(left);
            if (!string.IsNullOrWhiteSpace(right))
                operands.Add(right);
        }

        if (HasExplicitComparativeConjunctionOperandIntent(normalized))
        {
            for (var i = 1; i < tokens.Length - 1; i++)
            {
                if (!IsConjunctionComparativeMarker(tokens[i]))
                    continue;

                var left = ExtractLeftBareVsOperand(tokens, i);
                var right = ExtractRightBareVsOperand(tokens, i);
                if (!ShouldKeepConjunctionComparativeOperands(left, right))
                    continue;

                if (!string.IsNullOrWhiteSpace(left))
                    operands.Add(left);
                if (!string.IsNullOrWhiteSpace(right))
                    operands.Add(right);
            }
        }

        return operands
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    internal static IReadOnlyList<string> ExtractReferenceVersionComparisonOperands(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length == 0)
            return Array.Empty<string>();

        var operands = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = NormalizeReferenceVersionOperandToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (IsReferenceVersionYearToken(token))
            {
                operands.Add(token);
                if (i + 1 < tokens.Length)
                {
                    var next = NormalizeReferenceVersionOperandToken(tokens[i + 1]);
                    if (!string.IsNullOrWhiteSpace(next) && IsReferenceVersionAmendmentToken(next))
                        operands.Add($"{token} {next}");
                }
                continue;
            }

            if (IsReferenceVersionAmendmentToken(token))
                operands.Add(NormalizeReferenceVersionAmendmentPhrase(token));
        }

        return operands
            .Where(static operand => !string.IsNullOrWhiteSpace(operand))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    private static string NormalizeReferenceVersionOperandToken(string token)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(token)).ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Length <= 16 ? normalized : string.Empty;
    }


    internal static bool IsReferenceVersionYearToken(string token)
        => token.Length == 4
           && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
           && year is >= 1900 and <= 2099;


    private static bool IsReferenceVersionAmendmentToken(string token)
        => SafeRegexIsMatch(
            token,
            @"^(?:pr)?a\d{1,2}$|^amd\d{0,2}$|^amdt\d{0,2}$|^ac\d{0,2}$|^corr(?:igendum)?\d{0,2}$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));


    private static string NormalizeReferenceVersionAmendmentPhrase(string token)
    {
        var normalized = NormalizeReferenceVersionOperandToken(token);
        if (normalized.StartsWith("pr", StringComparison.Ordinal) && normalized.Length > 2)
            return $"{normalized[..2]} {normalized[2..]}";

        return normalized;
    }


    internal static int ComputeReferenceVersionOperandTitleSignal(string query, string? candidateTitleText)
    {
        if (string.IsNullOrWhiteSpace(candidateTitleText))
            return 0;

        var operands = ExtractReferenceVersionComparisonOperands(query);
        if (operands.Count < 2)
            return 0;

        var normalizedCandidate = NormalizeForLexicalSignal(candidateTitleText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0;

        var matchedCount = operands.Count(operand => ContainsReferenceVersionOperandAnchor(operand, normalizedCandidate));
        if (matchedCount == 0)
            return 0;

        return Math.Min(12, (matchedCount * 3) + (matchedCount >= Math.Min(operands.Count, 3) ? 2 : 0));
    }


    private static bool ContainsReferenceVersionOperandAnchor(string operand, string normalizedCandidate)
    {
        if (string.IsNullOrWhiteSpace(operand) || string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        var normalizedOperand = NormalizeForLexicalSignal(operand);
        if (string.IsNullOrWhiteSpace(normalizedOperand))
            return false;

        var operandTokens = normalizedOperand
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (operandTokens.Length == 0)
            return false;

        if (operandTokens.All(token => ContainsExactNormalizedToken(normalizedCandidate, token)))
            return true;

        var compactOperand = string.Concat(operandTokens);
        if (compactOperand.Length < 2)
            return false;

        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compactCandidate.Contains(compactOperand, StringComparison.Ordinal);
    }


    internal static IReadOnlyList<string> InferReferenceVersionOperandGroupKeys(
        string query,
        IReadOnlyList<RagMatch> matches)
    {
        if (matches.Count == 0 || !ShouldUseReferenceVersionOperandRoute(query))
            return Array.Empty<string>();

        var queryGroupKeys = ExtractReferenceVersionGroupKeysFromQuery(query);
        var ranked = matches
            .Select((match, index) =>
            {
                var hasGroup = TryExtractReferenceVersionGroupKey(match, out var groupKey);
                var signalText = string.Join(' ', new[] { match.DocName, match.DocPath }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
                return new
                {
                    Match = match,
                    Index = index,
                    GroupKey = hasGroup ? groupKey : null,
                    OperandSignal = ComputeReferenceVersionOperandTitleSignal(query, signalText)
                };
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.GroupKey))
            .Where(item => queryGroupKeys.Count == 0 || queryGroupKeys.Contains(item.GroupKey!))
            .Where(static item => item.OperandSignal > 0 || item.Index < 4)
            .GroupBy(static item => item.GroupKey!, StringComparer.Ordinal)
            .Select(static group => new
            {
                GroupKey = group.Key,
                OperandSignal = group.Sum(static item => item.OperandSignal),
                BestIndex = group.Min(static item => item.Index),
                BestScore = group.Max(static item => item.Match.Score)
            })
            .Where(static item => item.OperandSignal > 0)
            .OrderByDescending(static item => item.OperandSignal)
            .ThenBy(static item => item.BestIndex)
            .ThenByDescending(static item => item.BestScore)
            .Select(static item => item.GroupKey)
            .Take(3)
            .ToArray();

        return ranked;
    }


    internal static IReadOnlyList<string> ExtractReferenceVersionGroupKeysFromQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var signal = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(signal))
            return Array.Empty<string>();

        var keys = ExtractSlashSeparatedStandardPartLookupPhrases(query)
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(LooksLikeStandardReferenceTitleLookupPhrase)
            .ToList();

        keys.AddRange(SafeRegexMatches(
                signal,
                @"\b(?<prefix>en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp)\s+(?<number>\d{2,6})(?:\s+(?<part>\d{1,4}[a-z0-9]*))?",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100))
            .Select(static match =>
            {
                var tokens = new List<string>
                {
                    match.Groups["prefix"].Value,
                    match.Groups["number"].Value
                };
                var part = match.Groups["part"].Value;
                if (!string.IsNullOrWhiteSpace(part))
                    tokens.Add(part);

                return string.Join(' ', tokens);
            })
            .Where(static key => !string.IsNullOrWhiteSpace(key)));

        return keys
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    private static string? ExtractLeftBareVsOperand(IReadOnlyList<string> tokens, int markerIndex)
    {
        for (var i = markerIndex - 1; i >= 0; i--)
        {
            var token = NormalizeBareVsOperandToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (IsBareVsOperandBoundaryToken(token))
                break;
            if (!IsBareVsOperandToken(token))
                continue;

            if (IsShortComparativeOperandToken(token) || i == 0)
            {
                if (i > 0)
                {
                    var previousCandidate = NormalizeBareVsOperandToken(tokens[i - 1]);
                    if (ShouldKeepLeftSharedSuffixBareVsOperand(tokens, markerIndex, previousCandidate, token))
                        return $"{previousCandidate} {token}";
                }

                return token;
            }

            var previous = NormalizeBareVsOperandToken(tokens[i - 1]);
            var phrase = !string.IsNullOrWhiteSpace(previous) ? $"{previous} {token}" : token;
            return !string.IsNullOrWhiteSpace(previous)
                   && IsBareVsOperandToken(previous)
                   && !IsBareVsOperandBoundaryToken(previous)
                   && (!IsShortComparativeOperandToken(previous)
                       || !string.IsNullOrWhiteSpace(SelectCrossLanguageTechnicalPhraseVariant(phrase)))
                ? phrase
                : token;
        }

        return null;
    }


    private static string? ExtractRightBareVsOperand(IReadOnlyList<string> tokens, int markerIndex)
    {
        for (var i = markerIndex + 1; i < tokens.Count; i++)
        {
            var token = NormalizeBareVsOperandToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (IsBareVsOperandBoundaryToken(token))
                break;
            if (!IsBareVsOperandToken(token))
                continue;

            var next = i < tokens.Count - 1 ? NormalizeBareVsOperandToken(tokens[i + 1]) : string.Empty;
            if (IsShortComparativeOperandToken(token) || i >= tokens.Count - 1)
            {
                if (ShouldKeepRightSharedSuffixBareVsOperand(tokens, markerIndex, token, next))
                    return $"{token} {next}";

                return token;
            }

            if (!string.IsNullOrWhiteSpace(next) && IsComparativeVersionDocumentOperand(next))
                return next;
            if (IsComparativeOperandDocumentDescriptorToken(token)
                && !string.IsNullOrWhiteSpace(next)
                && IsBareVsOperandToken(next))
            {
                return next;
            }

            if (string.IsNullOrWhiteSpace(next)
                || !IsBareVsOperandToken(next)
                || IsBareVsOperandBoundaryToken(next))
            {
                return token;
            }

            if (IsShortComparativeOperandToken(next)
                && !ShouldKeepRightSharedSuffixBareVsOperand(tokens, markerIndex, token, next))
            {
                return token;
            }

            return $"{token} {next}";
        }

        return null;
    }


    private static bool ShouldKeepLeftSharedSuffixBareVsOperand(
        IReadOnlyList<string> tokens,
        int markerIndex,
        string previous,
        string suffix)
    {
        if (!IsBareVsOperandToken(previous)
            || IsBareVsOperandBoundaryToken(previous)
            || IsComparativeOperandDocumentDescriptorToken(previous)
            || !IsBareVsOperandToken(suffix))
        {
            return false;
        }

        var rightTokens = CollectRightBareVsOperandTokens(tokens, markerIndex, maxTokens: 2);
        return rightTokens.Count >= 2
               && string.Equals(rightTokens[^1], suffix, StringComparison.Ordinal);
    }


    private static bool ShouldKeepRightSharedSuffixBareVsOperand(
        IReadOnlyList<string> tokens,
        int markerIndex,
        string prefix,
        string suffix)
    {
        if (!IsBareVsOperandToken(prefix)
            || IsBareVsOperandBoundaryToken(prefix)
            || IsComparativeOperandDocumentDescriptorToken(prefix)
            || !IsBareVsOperandToken(suffix)
            || IsBareVsOperandBoundaryToken(suffix)
            || IsComparativeOperandDocumentDescriptorToken(suffix))
        {
            return false;
        }

        var leftTokens = CollectLeftBareVsOperandTokens(tokens, markerIndex, maxTokens: 2);
        return leftTokens.Count >= 2
               && string.Equals(leftTokens[^1], suffix, StringComparison.Ordinal);
    }


    private static IReadOnlyList<string> CollectLeftBareVsOperandTokens(
        IReadOnlyList<string> tokens,
        int markerIndex,
        int maxTokens)
    {
        var collected = new List<string>();
        for (var i = markerIndex - 1; i >= 0 && collected.Count < maxTokens; i--)
        {
            var token = NormalizeBareVsOperandToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (IsBareVsOperandBoundaryToken(token))
                break;
            if (!IsBareVsOperandToken(token))
            {
                if (collected.Count > 0)
                    break;
                continue;
            }

            collected.Insert(0, token);
        }

        return collected;
    }


    private static IReadOnlyList<string> CollectRightBareVsOperandTokens(
        IReadOnlyList<string> tokens,
        int markerIndex,
        int maxTokens)
    {
        var collected = new List<string>();
        for (var i = markerIndex + 1; i < tokens.Count && collected.Count < maxTokens; i++)
        {
            var token = NormalizeBareVsOperandToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (IsBareVsOperandBoundaryToken(token))
                break;
            if (!IsBareVsOperandToken(token))
            {
                if (collected.Count > 0)
                    break;
                continue;
            }

            collected.Add(token);
        }

        return collected;
    }


    private static string NormalizeBareVsOperandToken(string token)
        => FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(token)).Trim();


    private static bool IsBareVsComparativeMarker(string token)
        => token is "vs" or "versus" or "contre";


    private static bool IsConjunctionComparativeMarker(string token)
        => token is "et" or "and" or "avec" or "with";


    private static bool HasExplicitComparativeConjunctionOperandIntent(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var normalized = $" {normalizedQuery.ToLowerInvariant()} ";
        return ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparez ",
            " comparaison ",
            " comparison ",
            " comparar ",
            " compara ",
            " confronta ",
            " vergleiche ");
    }


    private static bool ShouldKeepConjunctionComparativeOperands(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftIsCode = IsLikelyCompactDocumentCodeOperand(left);
        var rightIsCode = IsLikelyCompactDocumentCodeOperand(right);
        if (leftIsCode && rightIsCode)
            return true;

        var leftTokenCount = NormalizeForLexicalSignal(left)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        var rightTokenCount = NormalizeForLexicalSignal(right)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        return (leftIsCode && rightTokenCount >= 2) || (rightIsCode && leftTokenCount >= 2);
    }


    private static bool IsBareVsOperandToken(string token)
        => token.Length is >= 2 and <= 24
           && token.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(token)
           && !TitleConnectorTokens.Contains(token)
           && !ComparativeOperandConceptStopwords.Contains(token);


    private static bool IsBareVsOperandBoundaryToken(string token)
        => token is "comment" or "how" or "pourquoi" or "why" or "explique" or "explain"
            or "sans" or "without" or "contradiction" or "nuance"
            or "et" or "and" or "ou" or "or";


    private static bool IsComparativeOperandDocumentDescriptorToken(string token)
        => token is "document" or "documents" or "doc" or "docs" or "fichier" or "fichiers"
            or "pdf" or "rapport" or "report" or "annexe" or "annexes" or "appendix" or "appendices"
            or "guide" or "manual" or "manuel";


    private static bool IsShortComparativeOperandToken(string token)
        => token.Length is >= 2 and <= 5
           && token.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(token)
           && !TitleConnectorTokens.Contains(token)
           && !ComparativeOperandConceptStopwords.Contains(token);


    internal static bool IsLikelyCompactDocumentCodeOperand(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalized = NormalizeForLexicalSignal(phrase).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Length is < 2 or > 8)
            return false;
        if (!normalized.All(char.IsLetterOrDigit))
            return false;
        if (!normalized.Any(char.IsLetter))
            return false;
        if (normalized is "pdf" or "doc" or "docs" or "rag" or "llm" or "dis" or "dit" or "dire" or "tell")
            return false;
        if (normalized.Any(char.IsDigit))
            return true;

        var vowelCount = normalized.Count(static ch => ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'y');
        return normalized.Length >= 3 && vowelCount <= 1;
    }


    internal static bool IsComparativeVersionDocumentOperand(string operand)
    {
        if (string.IsNullOrWhiteSpace(operand))
            return false;

        var normalized = NormalizeForLexicalSignal(operand).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Length is < 2 or > 8)
            return false;
        if (!normalized.Any(char.IsDigit))
            return false;
        if (!normalized.Any(char.IsLetter))
            return false;
        if (LexicalStopwords.Contains(normalized)
            || TitleConnectorTokens.Contains(normalized)
            || ComparativeOperandConceptStopwords.Contains(normalized))
        {
            return false;
        }

        return SafeRegexIsMatch(
            normalized,
            @"^[a-z]{1,8}\d{1,4}[a-z]?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }


    internal static IReadOnlyList<string> ExtractComparativeVersionOperandLookupTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !ShouldPreferComparativeDocumentDiversity(query))
            return Array.Empty<string>();

        var terms = new List<string>();
        terms.AddRange(ExtractBareVsComparativeOperands(query)
            .Select(static operand => NormalizeForLexicalSignal(operand).Replace(" ", string.Empty, StringComparison.Ordinal)));

        var normalized = NormalizeForLexicalSignal(query);
        terms.AddRange(normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Replace(" ", string.Empty, StringComparison.Ordinal)));

        return terms
            .Where(IsComparativeVersionDocumentOperand)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    internal static IReadOnlyList<string> BuildComparativeVersionCounterpartBackfillQueries(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || ExtractComparativeVersionOperandLookupTerms(query).Count == 0)
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var queries = new List<string>();
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (!IsBareVsComparativeMarker(tokens[i]))
                continue;

            var left = tokens.Take(i).ToArray();
            var right = tokens.Skip(i + 1).ToArray();
            var leftHasVersion = left.Any(IsComparativeVersionDocumentOperand);
            var rightHasVersion = right.Any(IsComparativeVersionDocumentOperand);
            if (leftHasVersion == rightHasVersion)
                continue;

            var counterpart = leftHasVersion
                ? BuildComparativeVersionCounterpartPhrase(right, preferTailAfterScopeMarker: false)
                : BuildComparativeVersionCounterpartPhrase(left, preferTailAfterScopeMarker: true);
            if (!string.IsNullOrWhiteSpace(counterpart))
                queries.Add(counterpart);
        }

        return queries
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    private static string? BuildComparativeVersionCounterpartPhrase(
        IReadOnlyList<string> tokens,
        bool preferTailAfterScopeMarker)
    {
        if (tokens.Count == 0)
            return null;

        var start = 0;
        var end = tokens.Count;
        if (preferTailAfterScopeMarker)
        {
            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                if (IsComparativeCounterpartScopeMarker(tokens[i]))
                {
                    start = i + 1;
                    break;
                }
            }
        }
        else
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (IsComparativeCounterpartBoundaryToken(tokens[i]))
                {
                    end = i;
                    break;
                }
            }
        }

        var normalizedTokens = tokens
            .Skip(start)
            .Take(Math.Max(0, end - start))
            .Select(NormalizeComparativeVersionCounterpartToken)
            .ToArray();
        var hasNumericToken = normalizedTokens.Any(static token => token.All(char.IsDigit));
        var phraseTokens = normalizedTokens
            .Where(token => token.Length >= 2
                            || token.All(char.IsDigit)
                            || (hasNumericToken && (token is "c" or "f")))
            .Where(static token => !IsBareVsComparativeMarker(token))
            .Where(static token => !IsComparativeVersionDocumentOperand(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !ComparativeOperandConceptStopwords.Contains(token))
            .Where(static token => !QuotedResponseShapeTokens.Contains(token))
            .TakeLast(8)
            .ToArray();
        if (phraseTokens.Length == 0)
            return null;
        if (!phraseTokens.Any(static token => token.Length >= 4 || token.Any(char.IsDigit)))
            return null;

        var numericUnitPhrase = BuildNumericUnitCounterpartPhrase(phraseTokens);
        if (!string.IsNullOrWhiteSpace(numericUnitPhrase))
            return numericUnitPhrase;

        var phrase = string.Join(' ', phraseTokens);
        return NormalizeFocusedLookupPhrase(phrase);
    }


    private static string? BuildNumericUnitCounterpartPhrase(IReadOnlyList<string> phraseTokens)
    {
        for (var i = 0; i < phraseTokens.Count - 2; i++)
        {
            if (!phraseTokens[i].All(char.IsDigit) || !phraseTokens[i + 1].All(char.IsDigit))
                continue;
            if (phraseTokens[i + 2] is "c" or "f")
                return $"{phraseTokens[i]} {phraseTokens[i + 1]} {phraseTokens[i + 2]}";
        }

        return null;
    }


    private static string NormalizeComparativeVersionCounterpartToken(string token)
    {
        var normalized = NormalizeForLexicalSignal(token).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (SafeRegexIsMatch(
                normalized,
                @"^\d{1,3}a$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
        {
            return normalized[..^1];
        }

        return normalized;
    }


    private static bool IsComparativeCounterpartScopeMarker(string token)
        => token is "sur" or "pour" or "concernant" or "about" or "regarding" or "on" or "sobre" or "su" or "uber" or "ueber";


    private static bool IsComparativeCounterpartBoundaryToken(string token)
        => token is "comment" or "how" or "pourquoi" or "why" or "explique" or "explain"
            or "sans" or "without" or "contradiction" or "nuance" or "avec" or "with";


    private static IReadOnlyList<string> BuildComparativeOperandConceptPhrases(string query)
    {
        var phrases = new List<string>();
        var conceptTokens = ExtractLexicalQueryTokens(query)
            .Where(IsComparativeOperandConceptToken)
            .ToArray();

        foreach (var token in conceptTokens)
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                if (variant.Contains(' '))
                    phrases.Add(variant);
            }
        }

        var operandTokens = ExtractBareVsComparativeOperands(query)
            .SelectMany(static operand => operand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(IsBareVsOperandToken);
        var expandedConceptTokens = conceptTokens
            .Concat(operandTokens)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .ToHashSet(StringComparer.Ordinal);
        if (expandedConceptTokens.Any(static token => token is "label" or "labels" or "sign" or "signs")
            && expandedConceptTokens.Any(static token => token is "safety" or "security"))
        {
            phrases.Add("safety labels");
        }
        if (expandedConceptTokens.Any(static token => token is "color" or "colors" or "colour" or "colours" or "couleur" or "couleurs")
            && expandedConceptTokens.Any(static token => token is "safety" or "security"))
        {
            phrases.Add("safety colors");
        }

        phrases.AddRange(BuildLexicalContentFallbackTerms(query).Where(static term => term.Contains(' ')));

        return phrases
            .Select(static term => NormalizeFocusedLookupPhrase(term) ?? string.Empty)
            .Where(IsValidComparativeOperandConceptPhrase)
            .OrderByDescending(ScoreComparativeOperandConceptPhrase)
            .ThenBy(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
            .ThenBy(static term => term.Length)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    private static void AddCanonicalComparativeSafetyBackfillQueries(
        List<string> queries,
        string query,
        IReadOnlyList<string> operands)
    {
        var signalTokens = ExtractLexicalQueryTokens(query)
            .Concat(operands.SelectMany(static operand => operand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .ToHashSet(StringComparer.Ordinal);

        var hasSafety = signalTokens.Any(static token => token is "safety" or "security" or "securite");
        if (!hasSafety)
            return;

        if (signalTokens.Any(static token => token is "label" or "labels" or "sign" or "signs"))
            AddComparativeOperandTitleBackfillQuery(queries, "safety labels");
        if (signalTokens.Any(static token => token is "color" or "colors" or "colour" or "colours" or "couleur" or "couleurs"))
            AddComparativeOperandTitleBackfillQuery(queries, "safety colors");
    }


    private static bool IsHighConfidenceComparativeOperandConceptPhrase(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 2)
            return false;

        return tokens.Any(static token => token is "safety" or "security")
               && tokens.Any(static token => token is "label" or "labels" or "sign" or "signs"
                   or "color" or "colors" or "colour" or "colours");
    }


    private static bool IsValidComparativeOperandConceptPhrase(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length is >= 2 and <= 4
               && tokens.All(static token => IsComparativeOperandConceptToken(token) || TitleConnectorTokens.Contains(token))
               && tokens.Count(IsComparativeOperandConceptToken) >= 2;
    }


    private static IReadOnlyList<string> BuildComparativeOperandConceptTokens(string query)
        => ExtractLexicalQueryTokens(query)
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .Select(static token => NormalizeFocusedLookupPhrase(token) ?? string.Empty)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Where(static token => !token.Contains(' '))
            .Where(IsComparativeOperandConceptToken)
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();


    private static int ScoreComparativeOperandConceptPhrase(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return 0;

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var score = 0;
        foreach (var token in tokens)
        {
            score += token switch
            {
                "code" or "conduct" => 4,
                "policy" or "policies" or "standard" or "standards" or "safety" or "security" => 3,
                "label" or "labels" or "sign" or "signs" => 3,
                "supplier" or "suppliers" or "vendor" or "vendors" => 2,
                "requirement" or "requirements" or "obligation" or "obligations" => 1,
                _ => 0
            };
        }

        return score;
    }


    private static bool IsComparativeOperandConceptToken(string token)
        => token.Length >= 4
           && token.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(token)
           && !TitleConnectorTokens.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token)
           && !DocumentOverviewTopicStopwords.Contains(token)
           && !ComparativeSubjectStopwords.Contains(token)
           && !BroadDiversityAbstractAssistTokens.Contains(token)
           && !ComparativeOperandConceptStopwords.Contains(token)
           && !IsBareVsComparativeMarker(token);


    private static IEnumerable<string> SplitComparativeTitleBackfillSegments(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            yield break;

        var normalized = NormalizeFocusedLookupPhrase(phrase);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        foreach (var segment in Regex.Split(
                     normalized,
                     @"(?:\s+(?:et|and|e|y|und|vs|versus|contre)\s+|[,;/]+)",
                     RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            var cleaned = TrimLeadingComparativeSegmentArticle(segment);
            if (!string.IsNullOrWhiteSpace(cleaned))
                yield return cleaned;
        }
    }


    private static string TrimLeadingComparativeSegmentArticle(string value)
        => Regex.Replace(
                value.Trim(),
                @"^(?:les|des|deux|plusieurs|the|some|multiple|several|varias|varios|diverses|diversi|mehrere)\s+",
                string.Empty,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            .Trim();


    private static void AddComparativeTitleBackfillQuery(List<string> queries, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return;

        var normalized = NormalizeFocusedLookupPhrase(phrase);
        if (string.IsNullOrWhiteSpace(normalized) && IsLikelyCompactDocumentCodeOperand(phrase))
            normalized = NormalizeForLexicalSignal(phrase);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var withoutContext = TrimTrailingFocusedLookupContext(normalized);
        if (!string.IsNullOrWhiteSpace(withoutContext))
            queries.Add(withoutContext);
        queries.Add(normalized);
    }


    private static bool IsInfinitiveLikeSubjectToken(string token)
        => token.Length >= 6 && token.EndsWith("er", StringComparison.Ordinal);


    private static bool StartsWithInfinitiveLikeSubjectToken(string term)
    {
        var first = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && IsInfinitiveLikeSubjectToken(first);
    }


    private static bool EndsWithPluralSubjectToken(string term)
    {
        var last = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last is not null
            && last.Length >= 5
            && (last.EndsWith("s", StringComparison.Ordinal) || last.EndsWith("x", StringComparison.Ordinal));
    }


    internal static bool ShouldConstrainPreciseTitleLookup(string query)
    {
        if (LooksLikeShortAudienceConstrainedGenericFragment(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if ((ContainsTechnicalEvidenceLookupIntent(normalized) && !HasReferenceLikeQueryToken(query))
            || ContainsCustomerReplyLanguage(normalized))
        {
            return false;
        }

        var tokens = ExtractTitlePruneTokens(query);
        if (tokens.Length is < 2 or > 6)
            return false;
        if (ExtractComparativeSubjectAnchorTokens(query).Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        if (IsRecommendationSelectionQuery(normalized))
            return false;
        if (ContainsEnumerativeLookupIntent(normalized)
            && !LooksLikeBareConnectorTitleLookup(query, normalized))
        {
            return false;
        }

        return !ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparison ",
            " comparaison ",
            " difference ",
            " differences ",
            " differents ",
            " differentes ");
    }


    private static bool LooksLikeBareConnectorTitleLookup(string query, string normalized)
    {
        var tokens = ExtractTitlePruneTokens(query);
        if (tokens.Length is < 3 or > 6)
            return false;

        if (!ContainsAny(normalized, " avec ", " with ", " con ", " com ", " mit "))
            return false;

        return !ContainsAny(
            normalized,
            " document ",
            " documents ",
            " fichier ",
            " fichiers ",
            " pdf ",
            " quels ",
            " quelles ",
            " lequel ",
            " lesquels ",
            " laquelle ",
            " lesquelles ",
            " which ",
            " what ",
            " existe ",
            " existent ",
            " disponibles ",
            " liste ",
            " lister ",
            " montre ",
            " chercher ",
            " cherche ",
            " trouve ",
            " trouver ",
            " propose ",
            " proposer ");
    }


    internal static bool ShouldSkipDocumentProfileSearchForPreciseLookup(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        if (ShouldAllowDocumentProfileAssistForComparativeLookup(query))
            return false;

        var hasFocusedLookup = ExtractFocusedLookupPhrases(query).Count > 0;
        var hasPreciseTitleShape = ShouldConstrainPreciseTitleLookup(query);
        var hasUnquotedTitleAnchorProbe = ShouldProbeUnquotedTitleAnchorRoute(
            query,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false);
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        var hasSourceAttributionTitleShape = HasSourceAttributionTitleLookupShape(query, normalized);
        var hasParameterLookup = ContainsParameterDetailLookupIntent(normalized);
        if (!hasFocusedLookup
            && !hasPreciseTitleShape
            && !hasUnquotedTitleAnchorProbe
            && !hasSourceAttributionTitleShape
            && !hasParameterLookup)
            return false;
        if (ContainsDocumentOverviewIntent(query))
            return false;
        if (ShouldUseScopedProfileFallback(query, hasCategoryFilter: true, mode: "balanced"))
            return false;
        if (ExtractDocumentHintTokens(query).Count > 0)
            return false;
        if (LooksLikeShortAudienceConstrainedGenericFragment(query))
            return false;

        if (ContainsEnumerativeLookupIntent(normalized)
            && !hasParameterLookup
            && !hasSourceAttributionTitleShape
            && !hasUnquotedTitleAnchorProbe)
        {
            return false;
        }
        if (IsRecommendationSelectionQuery(normalized))
            return false;
        if (ContainsGuidanceOrAdviceSelectionIntent(normalized))
            return false;
        if (ShouldKeepDocumentProfileAssistForTechnicalOrReferenceLookup(query, normalized))
            return false;

        return true;
    }


    private static bool ShouldKeepDocumentProfileAssistForTechnicalOrReferenceLookup(string query, string normalized)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (ContainsTechnicalEvidenceLookupIntent(normalized)
            || ContainsTechnicalDocumentTypeLookupIntent(normalized))
        {
            return true;
        }

        return HasReferenceLikeQueryToken(query)
            && ContainsTechnicalReferenceContext(normalized);
    }


    private static bool ContainsTechnicalReferenceContext(string normalized)
        => ContainsAny(
            normalized,
            " reference ",
            " references ",
            " modele ",
            " model ",
            " product ",
            " produit ",
            " variant ",
            " variants ",
            " variante ",
            " variantes ",
            " module ",
            " capteur ",
            " sensor ",
            " moteur ",
            " motor ",
            " vanne ",
            " valve ",
            " filtre ",
            " filter ",
            " terminal ",
            " transmetteur ",
            " transmitter ",
            " equipement ",
            " equipment ",
            " device ",
            " appareil ",
            " document principal ",
            " fichier principal ",
            " main document ",
            " main file ",
            " amendement ",
            " amendment ",
            " corrigendum ",
            " correction ",
            " version ",
            " revision ");


    private static bool ContainsTechnicalEvidenceLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " certificat ",
            " certificats ",
            " certificate ",
            " certificates ",
            " certification ",
            " certifications ",
            " certified ",
            " certifie ",
            " certifiee ",
            " certifies ",
            " certifiees ",
            " conformite ",
            " conformity ",
            " compliance ",
            " compliant ",
            " conforme ",
            " declaration de conformite ",
            " declaration of conformity ",
            " norme ",
            " normes ",
            " standard ",
            " standards ",
            " tuv ",
            " fda ",
            " sds ",
            " msds ");


    private static bool ContainsTechnicalDocumentTypeLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " fiche technique ",
            " fiches techniques ",
            " technical sheet ",
            " technical sheets ",
            " technical data ",
            " datasheet ",
            " datasheets ",
            " data sheet ",
            " data sheets ",
            " specification ",
            " specifications ",
            " spec sheet ",
            " spec sheets ",
            " manuel ",
            " manuels ",
            " manual ",
            " manuals ",
            " mode emploi ",
            " modes emploi ",
            " mode d emploi ",
            " modes d emploi ",
            " operating instruction ",
            " operating instructions ",
            " instructions for use ",
            " reference ",
            " references ",
            " modele ",
            " model ",
            " product ",
            " produit ",
            " produit couvert ",
            " device ",
            " appareil ",
            " equipement ",
            " equipment ");


    internal static bool LooksLikeShortAudienceConstrainedGenericFragment(string query)
    {
        var normalized = NormalizeForLexicalSignal(query);
        if (Regex.IsMatch(
                normalized,
                @"^(?:un|une|des|du|de la|de l|a|an|some|una|unos|unas|um|uma|ein|eine)\s+(?:activite|activites|activity|activities|atelier|ateliers|workshop|workshops|contenu|content|controle|control|guide|guides|option|options|procedure|procedures|fiche|fiches|document|documents)\s+(?:pour\s+)?(?:enfant|enfants|children|child|kids|apprenti|apprentis|apprentice|apprentices|debutant|debutants|beginner|beginners)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        if (tokens.Length is < 2 or > 4)
            return false;
        if (!tokens.Any(IsAudienceConstraintToken))
            return false;

        return tokens.All(static token =>
            IsAudienceConstraintToken(token)
            || IsGenericDocumentProfileSpecificityToken(token)
            || StructuredContentLexicon.IsStructuredItemTypeToken(token));
    }


    internal static bool HasSourceAttributionTitleLookupShape(string query, string normalized)
    {
        if (!ContainsAny(
                normalized,
                " source ",
                " sources ",
                " provenance ",
                " origine ",
                " citation ",
                " citations ",
                " quelle page ",
                " quelles pages ",
                " which page ",
                " which pages "))
        {
            return false;
        }

        if (ContainsBroadSourceMentionLookupIntent(normalized))
            return false;

        var tokens = ExtractTitlePruneTokens(query);
        if (tokens.Length is < 2 or > 6)
            return false;

        var hasPreciseSourceIntent =
            HasTrailingSourceAttributionIntent(normalized)
            || ContainsPageLookupIntent(normalized)
            || ContainsSourceOfLookupIntent(normalized)
            || ContainsProvenancePredicateLookupIntent(normalized)
            || ContainsPreciseLookupVerb(normalized);

        return hasPreciseSourceIntent
            && tokens.Any(static token => token.Length >= 5 || token.Any(char.IsDigit));
    }


    private static bool ContainsBroadSourceMentionLookupIntent(string normalized)
        => ContainsAny(
                normalized,
                " source ",
                " sources ",
                " document ",
                " documents ",
                " doc ",
                " docs ",
                " fichier ",
                " fichiers ",
                " file ",
                " files ")
            && ContainsAny(
                normalized,
                " parle de ",
                " parlent de ",
                " parle d ",
                " parlent d ",
                " mentionne ",
                " mentionnent ",
                " contient ",
                " contiennent ",
                " talk about ",
                " talks about ",
                " mention ",
                " mentions ",
                " contain ",
                " contains ",
                " cover ",
                " covers ");


    private static bool HasTrailingSourceAttributionIntent(string normalized)
    {
        var trimmed = normalized.Trim();
        return trimmed.EndsWith(" source", StringComparison.Ordinal)
            || trimmed.EndsWith(" sources", StringComparison.Ordinal)
            || trimmed.EndsWith(" provenance", StringComparison.Ordinal)
            || trimmed.EndsWith(" origine", StringComparison.Ordinal)
            || trimmed.EndsWith(" citation", StringComparison.Ordinal)
            || trimmed.EndsWith(" citations", StringComparison.Ordinal);
    }


    private static bool ContainsPageLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " quelle page ",
            " quelles pages ",
            " quel page ",
            " quels pages ",
            " page source ",
            " pages source ",
            " which page ",
            " which pages ");


    private static bool ContainsSourceOfLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " source de ",
            " source du ",
            " source des ",
            " source d ",
            " source pour ",
            " source for ",
            " source of ",
            " sources de ",
            " sources du ",
            " sources des ",
            " sources d ",
            " provenance de ",
            " provenance du ",
            " provenance des ",
            " provenance d ",
            " origine de ",
            " origine du ",
            " origine des ",
            " origine d ",
            " citation de ",
            " citation du ",
            " citation des ",
            " citation d ",
            " citation for ",
            " citation of ");


    private static bool ContainsProvenancePredicateLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " vient de ",
            " vient du ",
            " vient des ",
            " provient de ",
            " provient du ",
            " provient des ",
            " sort de ",
            " sort du ",
            " come from ",
            " comes from ",
            " is from ",
            " stammt aus ",
            " stammt von ");


    internal static bool ContainsParameterDetailLookupIntent(string normalized)
        => ContainsAny(normalized,
            " composant ",
            " composants ",
            " component ",
            " components ",
            " material ",
            " materials ",
            " materiel ",
            " materiaux ",
            " etape ",
            " etapes ",
            " step ",
            " steps ",
            " parametre ",
            " parametres ",
            " parameter ",
            " parameters ",
            " reglage ",
            " reglages ",
            " setting ",
            " settings ",
            " temperature ",
            " temperatures ",
            " vitesse ",
            " vitesses ",
            " speed ",
            " speeds ",
            " duree ",
            " duration ",
            " temps ",
            " time ");


    internal static bool ShouldSkipDocumentProfileSearchForComparativeLookup(string query)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query))
            return false;
        if (ShouldAllowDocumentProfileAssistForComparativeLookup(query))
            return false;

        return ExtractComparativeLookupPhrases(query).Count > 0;
    }


    internal static bool ShouldAllowDocumentProfileAssistForComparativeLookup(string query)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (ShouldKeepDocumentProfileAssistForTechnicalOrReferenceLookup(query, normalized))
            return true;
        if (ExtractBareVsComparativeOperands(query).Count >= 2)
            return true;

        if (ContainsAny(
                normalized,
                " styles ",
                " public ",
                " audience ",
                " enfants ",
                " enfant ",
                " children ",
                " child ",
                " kids ",
                " apprentice ",
                " apprentices ",
                " apprentis ",
                " apprenti ",
                " debutant ",
                " debutants ",
                " beginner ",
                " beginners ",
                " simple ",
                " simples ",
                " easier ",
                " easiest ",
                " facile ",
                " faciles ",
                " rapide ",
                " rapides ",
                " quick ",
                " fast ",
                " semaine ",
                " weekly ",
                " travail ",
                " work ",
                " budget ",
                " equilibre ",
                " balanced ",
                " sante ",
                " health ",
                " famille ",
                " family ",
                " risque ",
                " risques ",
                " risk ",
                " risks ",
                " temps ",
                " time ",
                " duree ",
                " duration ",
                " materiel ",
                " material ",
                " materials "))
        {
            return true;
        }

        var phrases = ExtractComparativeLookupPhrases(query);
        if (phrases.Count == 0)
            return false;

        if (phrases.Any(static phrase =>
                ContainsAny(
                    $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase)).ToLowerInvariant()} ",
                    " vs ",
                    " versus ")))
        {
            return true;
        }

        return phrases.Any(static phrase => ExtractTitlePruneTokens(phrase).Length > 4);
    }


    internal static bool ContainsGuidanceOrAdviceSelectionIntent(string normalized)
        => ContainsAny(normalized,
            " comment choisir ",
            " comment selectionner ",
            " comment decider ",
            " que choisir ",
            " lequel choisir ",
            " laquelle choisir ",
            " lesquels choisir ",
            " lesquelles choisir ",
            " conseille ",
            " conseiller ",
            " recommande ",
            " recommander ",
            " how to choose ",
            " how should ",
            " which should ",
            " what should ",
            " choose between ",
            " recommend ",
            " recommendation ",
            " seleccionar ",
            " elegir ",
            " escoger ",
            " scegliere ",
            " scegliere tra ",
            " escolher ",
            " escolher entre ",
            " auswahl ",
            " auswahlen ",
            " waehlen ",
            " wahlen ");


    internal static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }


    internal static IReadOnlyList<string> ExtractDocumentHintTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var hints = new List<string>();
        foreach (var rawHint in ExtractExplicitFileDocumentHintPhrases(query))
            AddExplicitDocumentHintTerms(hints, rawHint);
        foreach (var rawHint in ExtractStrongExtensionlessDocumentHintPhrases(query))
            AddStrongDocumentNameHintTerms(hints, rawHint);

        foreach (var match in SafeRegexMatches(DocumentHintPattern, normalized))
        {
            var rawHint = match.Groups["hint"].Value;
            var tokens = ExtractLexicalQueryTokens(rawHint)
                .Where(IsDocumentHintSignalToken)
                .Take(4);
            hints.AddRange(tokens);
        }
        hints.AddRange(ExtractImplicitDocumentHintTokens(query));

        return hints
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }


    private static readonly Regex DocumentHintPattern = new(
        @"\b(?:(?:(?:dans|depuis|from|in|aus|im|von|nel|nella|del|della|do|da|em)\s+(?:(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an|some|el|la|los|las|o|a|os|as|il|lo|gli|die|der|das)\s+)?(?:document|documents|pdf|fichier|fichiers|file|files|source|sources|livre|livres|book|books|manuel|manuels|manual|manuals|guide|guides|rapport|rapports|report|reports|libro|libros|arquivo|arquivos|documento|documentos|handbuch|handbucher|buch|bucher|manuale|manuali)\s+)|(?:(?:du|de\s+la|des|de\s+l|from\s+the|of\s+the|del|della|do|da|dos|das|vom|aus\s+dem|aus\s+der)\s+(?:document|documents|pdf|fichier|fichiers|file|files|source|sources|livre|livres|book|books|manuel|manuels|manual|manuals|guide|guides|rapport|rapports|report|reports|libro|libros|arquivo|arquivos|documento|documentos|handbuch|handbucher|buch|bucher|manuale|manuali)\s+))(?<hint>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{1,60}?)(?=\s+(?:avec|with|com|con|mit|pour|for|para|per|sur|about|source|sources|page|pages|et|and|y|e|und)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    internal static IEnumerable<string> ExtractExplicitFileDocumentHintPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            yield break;

        foreach (var match in SafeRegexMatches(ExplicitFileDocumentHintPattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(ExplicitFileMentionPattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(ExplicitFileAnyMentionPattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(ExplicitFileOfDocumentHintPattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(ExplicitFileMentionBeforePredicatePattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(ExplicitFileMentionBeforeTopicPattern, query))
        {
            var hint = match.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                yield return hint;
        }
    }


    internal static IEnumerable<string> ExtractStrongExtensionlessDocumentHintPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in SafeRegexMatches(StrongExtensionlessDocumentHintPattern, query))
        {
            var hintGroup = match.Groups["hint"];
            if (LooksLikeTruncatedStrongDocumentHint(query, hintGroup.Index + hintGroup.Length))
                continue;

            var hint = hintGroup.Value;
            if (ShouldTreatStrongExtensionlessHintAsTopic(query, hint))
                continue;
            if (!LooksLikeStrongExtensionlessDocumentHintPhrase(hint))
                continue;

            var normalized = NormalizeStrongDocumentNameHintPhrase(hint);
            if (seen.Add(normalized))
                yield return hint;
        }

        foreach (var match in SafeRegexMatches(StrongExtensionlessDocumentAnyMentionPattern, query))
        {
            var hintGroup = match.Groups["hint"];
            if (LooksLikeTruncatedStrongDocumentHint(query, hintGroup.Index + hintGroup.Length))
                continue;

            var hint = hintGroup.Value;
            if (ShouldTreatStrongExtensionlessHintAsTopic(query, hint))
                continue;
            if (!LooksLikeStrongExtensionlessDocumentHintPhrase(hint))
                continue;

            var normalized = NormalizeStrongDocumentNameHintPhrase(hint);
            if (seen.Add(normalized))
                yield return hint;
        }
    }


    private static bool LooksLikeTruncatedStrongDocumentHint(string query, int hintEndIndex)
        => hintEndIndex >= 0
           && hintEndIndex + 1 < query.Length
           && query[hintEndIndex] == '.'
           && char.IsLetterOrDigit(query[hintEndIndex + 1]);


    private static readonly Regex ExplicitFileDocumentHintPattern = new(
        @"\b(?:dans|depuis|from|in|aus|im|von|nel|nella|del|della|do|da|em)\s+(?:(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an|some|el|los|las|o|os|as|il|lo|gli|die|der|das)\s+)?(?<hint>[\p{L}\p{Nd}\s\-_.,()'’/&+]{2,180}?\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?=\s*(?:[,;:.?]|\b(?:et|and|with|avec|pour|for|trouve|find|cherche|search|explique|explain)\b|$))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplicitFileMentionPattern = new(
        @"(?<![\p{L}\p{Nd}])(?<hint>(?:[\p{Lu}\p{Nd}][\p{L}\p{Nd}\-_.,()'’/&+]*)(?:\s+[\p{L}\p{Nd}\-_.,()'’/&+]+){0,24}?\.(?i:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?=\s*(?:[,;:.?]|\b(?:et|and|y|e|und|si|if|wenn|avec|with|com|con|mit|pour|for|para|per|en|in|dans|from)\b|$))",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplicitFileAnyMentionPattern = new(
        @"[`""'](?<hint>[^`""'\r\n]{1,180}?\.(?i:pdf|docx?|xlsx?|pptx?|md|txt|csv))[`""']",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplicitFileOfDocumentHintPattern = new(
        @"\b(?:de|du|des|d['’â€™]?|of|di)\s+(?:(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an|some|el|los|las|o|os|as|il|lo|gli|die|der|das)\s+)?(?<hint>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-_.,()'â€™/&+]{2,260}?\.(?i:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?=\s*(?:[,;:.?]|\b(?:et|and|with|avec|pour|for|trouve|find|cherche|search|explique|explain|n['’â€™]?est|ne|not|is|isn['’â€™]?t|isnt|does|doesn['’â€™]?t|doesnt)\b|$))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplicitFileMentionBeforePredicatePattern = new(
        @"(?<![\p{L}\p{Nd}])(?<hint>[\p{L}\p{Nd}\s\-_.,()'â€™/&+]{2,180}?\.(?i:pdf|docx?|xlsx?|pptx?|md|txt|csv))\s+(?:n['’â€™]?est|ne|not|is|isn['’â€™]?t|isnt|does|doesn['’â€™]?t|doesnt)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplicitFileMentionBeforeTopicPattern = new(
        @"(?<![\p{L}\p{Nd}])(?<hint>[^\r\n]{2,220}?\.(?i:pdf|docx?|xlsx?|pptx?|md|txt|csv))\s+(?:sur|about|on|vs|versus)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex StrongExtensionlessDocumentHintPattern = new(
        @"\b(?:dans|depuis|from|in|aus|im|von|nel|nella|del|della|do|da|em|de|du|des|d['â€™]|of|di)\s+(?:(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an|some|el|los|las|o|os|as|il|lo|gli|die|der|das)\s+)?[`""']?(?<hint>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-_.,()'â€™/&+]{5,180}?)[`""']?(?=\s*(?:[,;:.?]|\b(?:peux|peux-tu|retrouve|retrouver|trouve|find|cherche|search|extraire|extract|explique|explain|where|what|which|how|comment|ajoute|ajoutent|adds?|apporte|apportent)\b|$))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex StrongExtensionlessDocumentAnyMentionPattern = new(
        @"[`""'](?<hint>[^`""'\r\n]{5,180})[`""']",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));


    private static void AddExplicitDocumentHintTerms(List<string> hints, string rawHint)
    {
        var rawNormalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(rawHint)).ToLowerInvariant();
        var normalized = TrimExplicitFileHintLeadingNoise(rawNormalized);
        foreach (var phrase in SplitExplicitFileHintSegments(normalized))
        {
            var signalTokens = phrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsDocumentHintSignalToken)
                .Take(6)
                .ToArray();
            var withoutExtension = TrimTrailingFileExtensionToken(phrase);
            if (signalTokens.Length == 0
                || (signalTokens.Length < 2 && string.Equals(withoutExtension, phrase, StringComparison.Ordinal)))
            {
                continue;
            }

            hints.Add(phrase);
            if (!string.IsNullOrWhiteSpace(withoutExtension)
                && !string.Equals(withoutExtension, phrase, StringComparison.Ordinal))
            {
                hints.Add(withoutExtension);
            }
        }
    }


    private static void AddStrongDocumentNameHintTerms(List<string> hints, string rawHint)
    {
        if (!LooksLikeStrongExtensionlessDocumentHintPhrase(rawHint))
            return;

        var normalized = NormalizeStrongDocumentNameHintPhrase(rawHint);
        if (!string.IsNullOrWhiteSpace(normalized))
            hints.Add(normalized);
    }


    internal static string NormalizeStrongDocumentNameHintPhrase(string rawHint)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(rawHint)).ToLowerInvariant();
        return TrimStandardReferenceHintLeadingNoise(normalized);
    }


    private static string TrimStandardReferenceHintLeadingNoise(string normalized)
    {
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return normalized;

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!IsStandardReferencePrefixToken(tokens[index]))
                continue;

            var nextWindow = tokens
                .Skip(index + 1)
                .Take(2)
                .ToArray();
            if (nextWindow.Any(static token => token.Any(char.IsDigit)))
                return string.Join(' ', tokens.Skip(index));
        }

        return normalized;
    }


    private static bool LooksLikeStrongExtensionlessDocumentHintPhrase(string rawHint)
    {
        if (string.IsNullOrWhiteSpace(rawHint))
            return false;

        var rawNormalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(rawHint)).ToLowerInvariant();
        var normalized = TrimExplicitFileHintLeadingNoise(rawNormalized);
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is 0 or > 24)
            return false;
        if (tokens.Any(IsFileExtensionToken))
            return false;

        var signalTokenCount = tokens.Count(IsDocumentHintSignalToken);
        if (signalTokenCount == 0)
            return false;

        var surfaceTokens = ExtractDocumentNameSurfaceTokens(rawHint);
        if (surfaceTokens.Count == 0)
            return false;

        var hasUnderscoreSeparator = rawHint.Contains('_');
        var hasPathSeparator = LooksLikeDocumentPathSeparator(rawHint);
        if (!hasUnderscoreSeparator
            && !hasPathSeparator
            && LooksLikeOnlyVersionOperandsAndInstructionWords(normalized, surfaceTokens))
        {
            return false;
        }
        if (!hasUnderscoreSeparator
            && !hasPathSeparator
            && ShouldKeepDocumentProfileAssistForTechnicalOrReferenceLookup(rawHint, $" {rawNormalized} "))
        {
            return false;
        }

        var hasMixedAlphaNumericToken = surfaceTokens.Any(static token =>
            token.Any(char.IsLetter) && token.Any(char.IsDigit));
        var hasNumericToken = surfaceTokens.Any(static token => token.Any(char.IsDigit));
        var acronymTokenCount = surfaceTokens.Count(IsStrongDocumentNameAcronymToken);

        return (hasUnderscoreSeparator && signalTokenCount >= 2)
               || (hasPathSeparator
                   && signalTokenCount >= 2
                   && (hasMixedAlphaNumericToken || hasNumericToken || acronymTokenCount >= 1))
               || hasMixedAlphaNumericToken
               || (acronymTokenCount >= 1 && hasNumericToken)
               || acronymTokenCount >= 2;
    }


    private static bool LooksLikeOnlyVersionOperandsAndInstructionWords(
        string normalizedHint,
        IReadOnlyList<string> surfaceTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedHint) || surfaceTokens.Count == 0)
            return false;

        var normalizedTokens = normalizedHint
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (normalizedTokens.Length == 0)
            return false;

        var versionOperands = normalizedTokens
            .Where(IsComparativeVersionDocumentOperand)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versionOperands.Length < 2)
            return false;

        if (normalizedTokens.Any(static token => token.Contains('_', StringComparison.Ordinal)
                                                 || token.Contains('/', StringComparison.Ordinal)
                                                 || token.Contains('\\', StringComparison.Ordinal)
                                                 || IsFileExtensionToken(token)
                                                 || IsStandardReferencePrefixToken(token)
                                                 || IsStrongDocumentNameAcronymToken(token)))
        {
            return false;
        }

        var surfaceSignalTokens = surfaceTokens
            .Select(static token => FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(token)).ToLowerInvariant())
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (surfaceSignalTokens.Any(static token => token.Contains('_', StringComparison.Ordinal)
                                                   || token.Contains('/', StringComparison.Ordinal)
                                                   || token.Contains('\\', StringComparison.Ordinal)
                                                   || IsFileExtensionToken(token)
                                                   || IsStandardReferencePrefixToken(token)
                                                   || IsStrongDocumentNameAcronymToken(token)))
        {
            return false;
        }

        return normalizedTokens
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .All(static token => IsComparativeVersionDocumentOperand(token)
                                 || IsVersionOperandInstructionToken(token));
    }


    private static bool IsVersionOperandInstructionToken(string token)
        => token is "etape"
            or "etapes"
            or "step"
            or "steps"
            or "procedure"
            or "procedures"
            or "process"
            or "processus"
            or "fusionner"
            or "fusionne"
            or "merge"
            or "merged"
            or "comparer"
            or "compare"
            or "compara"
            or "comparar"
            or "comparaison"
            or "difference"
            or "differences"
            or "indiquer"
            or "indiques"
            or "indicate"
            or "citer"
            or "cite"
            or "source"
            or "sources"
            or "sans"
            or "without"
            or "avec"
            or "with"
            or "dans"
            or "in"
            or "seul"
            or "seule"
            or "single"
            or "meme"
            or "same";


    private static bool ShouldTreatStrongExtensionlessHintAsTopic(string query, string rawHint)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(rawHint))
            return false;
        if (LooksLikeExplicitFileLookupPhrase(rawHint))
            return false;

        var normalizedHint = NormalizeQuotedLookupPhrase(rawHint);
        if (string.IsNullOrWhiteSpace(normalizedHint))
            return false;

        if (ShouldTreatQuotedLookupAsTopic(query)
            && ExtractQuotedLookupPhrases(query).Contains(normalizedHint, StringComparer.Ordinal))
        {
            return true;
        }

        var normalizedQuery = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return HasReferenceLikeQueryToken(rawHint)
            && ContainsAny(
                normalizedQuery,
                " role du fichier ",
                " role de fichier ",
                " role du document ",
                " role de document ",
                " document principal ",
                " fichier principal ",
                " par rapport au document ",
                " par rapport a document ",
                " par rapport au fichier ",
                " par rapport a fichier ",
                " role of the file ",
                " role of the document ",
                " main document ",
                " main file ",
                " compared with the document ",
                " compared to the document ",
                " compared with the file ",
                " compared to the file ");
    }


    private static bool LooksLikeDocumentPathSeparator(string rawHint)
    {
        if (string.IsNullOrWhiteSpace(rawHint))
            return false;
        if (rawHint.Contains('\\'))
            return true;
        if (!rawHint.Contains('/'))
            return false;

        var parts = rawHint.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        return !parts.All(static part =>
        {
            var compact = new string(part.Where(char.IsLetterOrDigit).ToArray());
            return compact.Length is >= 2 and <= 16
                && compact.Any(char.IsLetter)
                && compact.Any(char.IsDigit);
        });
    }


    private static IReadOnlyList<string> ExtractDocumentNameSurfaceTokens(string rawHint)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var c in rawHint)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length == 0)
                return;

            tokens.Add(builder.ToString());
            builder.Clear();
        }
    }


    private static bool IsStrongDocumentNameAcronymToken(string token)
    {
        var letters = token.Where(char.IsLetter).ToArray();
        return letters.Length >= 2
               && letters.All(char.IsUpper)
               && !DocumentHintStopwords.Contains(new string(letters).ToUpperInvariant());
    }


    internal static string TrimTrailingFileExtensionToken(string normalized)
    {
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count == 0)
            return string.Empty;

        if (IsFileExtensionToken(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(' ', tokens);
    }


    internal static IEnumerable<string> SplitExplicitFileHintSegments(string normalized)
    {
        normalized = TrimExplicitFileHintLeadingNoise(normalized);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var start = 0;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!IsFileExtensionToken(tokens[index]))
                continue;

            var segment = TrimExplicitFileHintLeadingNoise(string.Join(' ', tokens[start..(index + 1)]));
            if (!string.IsNullOrWhiteSpace(segment))
                yield return segment;

            start = index + 1;
            while (start < tokens.Length && IsExplicitFileHintSeparatorToken(tokens[start]))
                start++;
            index = start - 1;
        }
    }


    private static bool IsFileExtensionToken(string token)
        => token is "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "md" or "txt" or "csv";


    private static bool IsExplicitFileHintSeparatorToken(string token)
        => token is "et" or "and" or "y" or "e" or "und" or "ou" or "or" or "oder" or "plus";


    internal static string TrimExplicitFileHintLeadingNoise(string normalized)
    {
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 2)
            return normalized;

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (LooksLikeExplicitFileHintStart(tokens, index))
                return string.Join(' ', tokens.Skip(index));
        }

        return normalized;
    }


    private static bool LooksLikeExplicitFileHintStart(IReadOnlyList<string> tokens, int index)
    {
        var token = tokens[index];
        if (string.IsNullOrWhiteSpace(token))
            return false;
        if (token.Any(char.IsDigit))
            return true;
        if (token.Length is < 2 or > 8)
            return false;
        if (index + 1 >= tokens.Count)
            return false;

        return tokens[index + 1].Any(char.IsDigit);
    }


    internal static bool IsDocumentHintSignalToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token);


    internal static IReadOnlyList<string> ExtractImplicitDocumentHintTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var hints = new List<string>();
        foreach (var match in SafeRegexMatches(ImplicitDocumentHintSurfacePattern, query))
        {
            var rawHint = match.Groups["hint"].Value;
            if (!IsAcronymLikeDocumentHintSurface(rawHint)
                || !LooksLikeImplicitDocumentHintContext(query, match.Index, match.Length))
            {
                continue;
            }

            var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(rawHint)).ToLowerInvariant();
            if (IsDocumentHintSignalToken(normalized))
                hints.Add(normalized);
        }

        return hints
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    private static readonly Regex ImplicitDocumentHintSurfacePattern = new(
        @"(?<![\p{L}\p{Nd}])(?<hint>[\p{Lu}\p{Nd}][\p{Lu}\p{Nd}\-]{2,})(?![\p{L}\p{Nd}])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));


    private static bool IsAcronymLikeDocumentHintSurface(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length < 3)
            return false;

        return letters.All(char.IsUpper)
            && !DocumentHintStopwords.Contains(value.ToUpperInvariant());
    }


    private static bool LooksLikeImplicitDocumentHintContext(string query, int hintIndex, int hintLength)
    {
        var before = query[..hintIndex];
        var beforeClause = LastClauseSegment(before);
        var normalizedBefore = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(beforeClause)).ToLowerInvariant();
        if (!StartsWithImplicitDocumentHintCarrier(normalizedBefore))
            return false;

        var subjectTokens = ExtractLexicalQueryTokens(beforeClause)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Take(4)
            .Count();
        if (subjectTokens < 2)
            return false;

        var after = query[(hintIndex + hintLength)..];
        if (string.IsNullOrWhiteSpace(after))
            return true;

        var trimmedAfter = after.TrimStart();
        if (trimmedAfter.Length == 0)
            return true;
        if (trimmedAfter[0] is ',' or ';' or ':' or '?' or '.' or '\r' or '\n')
            return true;

        var normalizedAfter = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(trimmedAfter[..Math.Min(80, trimmedAfter.Length)])).ToLowerInvariant()} ";
        return ContainsAny(normalizedAfter,
            " quel ",
            " quelle ",
            " quels ",
            " quelles ",
            " what ",
            " which ",
            " cuales ",
            " qual ",
            " quais ",
            " quale ",
            " welches ",
            " welche ",
            " reglage ",
            " reglages ",
            " setting ",
            " settings ",
            " parametre ",
            " parametres ");
    }


    private static string LastClauseSegment(string value)
    {
        var index = value.LastIndexOfAny(['.', '?', '!', ';', ':', '\r', '\n']);
        return index < 0 ? value : value[(index + 1)..];
    }


    private static bool StartsWithImplicitDocumentHintCarrier(string normalizedBeforeClause)
        => normalizedBeforeClause.StartsWith("pour ", StringComparison.Ordinal)
           || normalizedBeforeClause.StartsWith("for ", StringComparison.Ordinal)
           || normalizedBeforeClause.StartsWith("para ", StringComparison.Ordinal)
           || normalizedBeforeClause.StartsWith("per ", StringComparison.Ordinal)
           || normalizedBeforeClause.StartsWith("fur ", StringComparison.Ordinal)
           || normalizedBeforeClause.StartsWith("fuer ", StringComparison.Ordinal);


    internal static bool DocumentMatchesHint(IReadOnlyList<string> documentHintTokens, RagMatch match)
    {
        if (documentHintTokens.Count == 0)
            return false;

        var docSignal = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(
            string.Join(' ', new[] { match.DocName, match.DocPath }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
        if (string.IsNullOrWhiteSpace(docSignal))
            return false;

        var compactDocSignal = docSignal.Replace(" ", string.Empty, StringComparison.Ordinal);
        return documentHintTokens.Any(token =>
        {
            var folded = FoldDiacritics(token);
            return docSignal.Contains(folded, StringComparison.Ordinal)
                || (folded.Length >= 4 && compactDocSignal.Contains(folded.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
                || DocumentHintFuzzyMatchesDocSignal(folded, docSignal);
        });
    }


    internal static bool SelectionsCoverExplicitFileLookupGroups(string query, IReadOnlyList<RagMatch> selected)
    {
        var groups = ExtractExplicitFileLookupPhraseGroups(query);
        return groups.Count > 0
               && groups.All(group => selected.Any(match => ExplicitFileLookupGroupMatchesSelection(group, match)));
    }


    private static bool ExplicitFileLookupGroupMatchesSelection(
        IReadOnlyList<string> group,
        RagMatch match)
    {
        if (DocumentMatchesHint(group, match))
            return true;

        var explicitlyNamesFile = group.Any(phrase => phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsFileExtensionToken));
        if (explicitlyNamesFile)
            return false;

        var signalText = GetLexicalSignalText(match);
        return group
            .Where(LooksLikeStandardReferenceTitleLookupPhrase)
            .Any(phrase => StandardReferencePhraseMatchesText(phrase, signalText));
    }


    internal static IReadOnlyList<string> ExpandDocumentHintTokensFromCandidateDocs(
        string query,
        IReadOnlyList<RagMatch> selected,
        IReadOnlyList<string> documentHintTokens)
    {
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(query))
            return documentHintTokens;

        var hints = new List<string>(documentHintTokens);
        foreach (var token in ExtractImplicitCandidateDocumentHintTokens(query)
                     .Distinct(StringComparer.Ordinal))
        {
            if (hints.Contains(token, StringComparer.Ordinal))
                continue;

            if (selected.Any(match => DocumentMatchesHint([token], match)))
                hints.Add(token);
        }

        return hints
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    private static IReadOnlyList<string> ExtractImplicitCandidateDocumentHintTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var tokens = new List<string>();
        foreach (var match in SafeRegexMatches(
                     query,
                     @"(?<![\p{L}\p{Nd}])(?<surface>[\p{L}\p{Nd}][\p{L}\p{Nd}\-]{2,})(?![\p{L}\p{Nd}])",
                     RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var surface = match.Groups["surface"].Value;
            var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(surface)).ToLowerInvariant();
            if (normalized.Length < 4 || !IsDocumentHintSignalToken(normalized))
                continue;

            if (IsAcronymLikeDocumentHintSurface(surface)
                || LooksLikeNamedEntityDocumentHintSurface(query, match.Index, surface))
            {
                tokens.Add(normalized);
            }
        }

        return tokens
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }


    private static bool LooksLikeNamedEntityDocumentHintSurface(string query, int tokenIndex, string surface)
    {
        if (string.IsNullOrWhiteSpace(surface) || !char.IsUpper(surface[0]))
            return false;
        if (!surface.Skip(1).Any(static c => char.IsUpper(c) || char.IsDigit(c)))
        {
            var before = query[..tokenIndex];
            var beforeWindow = before.Length > 40 ? before[^40..] : before;
            var normalizedBefore = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(beforeWindow)).ToLowerInvariant()} ";
            return ContainsAny(
                normalizedBefore,
                " au ",
                " aux ",
                " avec ",
                " via ",
                " sur ",
                " using ",
                " with ",
                " on ",
                " mit ",
                " con ",
                " com ",
                " per ",
                " pour le ",
                " pour la ",
                " pour l ");
        }

        return true;
    }



    private static bool DocumentHintFuzzyMatchesDocSignal(string foldedHint, string docSignal)
    {
        if (foldedHint.Length is < 4 or > 8 || !foldedHint.All(char.IsLetter))
            return false;

        foreach (var docToken in docSignal.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (docToken.Length is < 4 or > 10 || !docToken.All(char.IsLetter))
                continue;
            if (Math.Abs(docToken.Length - foldedHint.Length) > 1)
                continue;
            if (docToken[0] != foldedHint[0] || docToken[^1] != foldedHint[^1])
                continue;
            if (ComputeBoundedEditDistance(foldedHint, docToken, maxDistance: 1) <= 1)
                return true;
        }

        return false;
    }


    internal static bool ShouldRunDocumentHintScopedBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        var documentHintTokens = ExtractDocumentHintTokens(query);
        if (documentHintTokens.Count == 0)
            return false;

        return !selected.Any(match =>
            !IsDocumentProfileMatch(match)
            && IsContentSelectionCandidate(match)
            && DocumentMatchesHint(documentHintTokens, match));
    }


    internal static string BuildDocumentHintScopedBackfillQuery(string query, string retrievalQuery)
    {
        if (ExtractDocumentHintTokens(query).Count == 0)
            return string.Empty;

        var focusedQuery = string.IsNullOrWhiteSpace(retrievalQuery)
            ? BuildFocusedLexicalBackfillQuery(query)
            : retrievalQuery.Trim();
        if (string.IsNullOrWhiteSpace(focusedQuery))
            return string.Empty;

        var hintTokens = ExtractDocumentHintTokens(query);
        var focusedTokens = ExtractDocumentHintBackfillSubjectTokens(focusedQuery)
            .Where(token => !hintTokens.Contains(token, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (focusedTokens.Length < 2)
            return string.Empty;

        return focusedQuery;
    }


    internal static bool ShouldUseExplicitDocumentScopedBackfillRoute(string query, string scopedBackfillQuery)
        => HasExplicitFileLookupIntent(query)
           && !string.IsNullOrWhiteSpace(scopedBackfillQuery);


    internal static bool HasExplicitFileLookupIntent(string query)
        => HasExplicitDocumentNameHint(query)
           || ExtractExplicitFileLookupPhrases(query).Count > 0;


    internal static bool HasExplicitDocumentNameHint(string query)
        => ExtractExplicitFileDocumentHintPhrases(query).Any()
           || ExtractStrongExtensionlessDocumentHintPhrases(query).Any();


    private static IReadOnlyList<string> ExtractDocumentHintBackfillSubjectTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3)
            .Where(static token => token.Any(char.IsLetterOrDigit))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !DocumentHintStopwords.Contains(token.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static async Task<IReadOnlyList<string>> ResolveDocumentHintDocPathsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? categoryPath,
        int limit,
        CancellationToken ct)
    {
        var explicitFileHintTokens = ExtractExplicitFileLookupPhrases(query);
        var documentHintTokens = explicitFileHintTokens.Count > 0
            ? explicitFileHintTokens
            : ExtractDocumentHintTokens(query);
        if (documentHintTokens.Count == 0 || limit <= 0)
            return Array.Empty<string>();

        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH hint_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@hint_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
scoped_docs AS (
    SELECT
        d.doc_path,
        d.doc_name,
        d.updated_at,
        LOWER(regexp_replace(CONCAT_WS(' ', d.doc_path, d.doc_name), '[^[:alnum:]]+', ' ', 'g')) AS doc_signal,
        LOWER(regexp_replace(CONCAT_WS('', d.doc_path, d.doc_name), '[^[:alnum:]]+', '', 'g')) AS compact_doc_signal
    FROM documents d
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
),
ranked_docs AS (
    SELECT
        d.doc_path,
        d.updated_at,
        COUNT(*) AS match_count,
        SUM(CASE
            WHEN d.doc_signal LIKE '%' || hint_terms.term || '%' THEN 2.0
            WHEN length(hint_terms.term) >= 4
                 AND d.compact_doc_signal LIKE '%' || REPLACE(hint_terms.term, ' ', '') || '%' THEN 1.4
            ELSE 0.0
        END) AS match_weight
    FROM scoped_docs d
    CROSS JOIN hint_terms
    WHERE d.doc_signal LIKE '%' || hint_terms.term || '%'
       OR (
            length(hint_terms.term) >= 4
            AND d.compact_doc_signal LIKE '%' || REPLACE(hint_terms.term, ' ', '') || '%'
       )
    GROUP BY d.doc_path, d.updated_at
)
SELECT doc_path
FROM ranked_docs
WHERE match_weight > 0
ORDER BY match_count DESC, match_weight DESC, char_length(doc_path) ASC, updated_at DESC
LIMIT @limit;
""";

        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            hint_terms = documentHintTokens.ToArray(),
            category,
            category_path = normalizedCategoryPath,
            limit
        }, cancellationToken: ct));

        return rows
            .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }


    internal static async Task<List<RagMatch>> SearchDocumentHintScopedBackfillMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string originalQuery,
        string focusedQuery,
        string? category,
        string? categoryPath,
        int topK,
        CancellationToken ct,
        Action<string, string?>? degradedRetrieverRef = null,
        bool includeProfileCardMatches = true,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(focusedQuery) || topK <= 0)
            return [];

        var explicitFileHintGroupCount = ExtractExplicitFileLookupPhraseGroups(originalQuery).Count;
        var docPathLimit = explicitFileHintGroupCount > 0
            ? Math.Min(Math.Max(1, explicitFileHintGroupCount), Math.Min(4, Math.Max(1, topK)))
            : Math.Min(4, Math.Max(1, topK));
        var docPaths = await ResolveDocumentHintDocPathsAsync(
            ds,
            tenantId,
            originalQuery,
            category,
            categoryPath,
            limit: docPathLimit,
            ct);
        if (docPaths.Count == 0)
            return [];

        var matches = new List<RagMatch>();
        foreach (var docPath in docPaths)
        {
            var scopedLimit = Math.Min(Math.Max(4, topK), 12);
            var beforeDocCount = matches.Count;
            if (includeProfileCardMatches)
            {
                var localTitleMatches = await SearchLocalTitleTokenRouteMatchesAsync(
                    ds,
                    tenantId,
                    focusedQuery,
                    category,
                    docId: null,
                    docPath,
                    scopedLimit,
                    ct,
                    categoryPath,
                    degradedRetrieverRef: degradedRetrieverRef);
                matches.AddRange(localTitleMatches);
            }

            var sparseMatches = includeProfileCardMatches
                ? await SearchSparseMatchesAsync(
                    ds,
                    tenantId,
                    focusedQuery,
                    category,
                    docId: null,
                    docPath,
                    scopedLimit,
                    ct,
                    sparseMsRef: static _ => { },
                    lexicalExpansionQuery: focusedQuery,
                    categoryPath: categoryPath,
                    includeProfileCardMatches: true,
                    degradedRetrieverRef: degradedRetrieverRef,
                    commandTimeoutSeconds: commandTimeoutSeconds)
                : await SearchDocumentScopedLexicalContentMatchesAsync(
                    ds,
                    tenantId,
                    focusedQuery,
                    category,
                    docPath,
                    scopedLimit,
                    ct,
                    categoryPath,
                    degradedRetrieverRef);
            matches.AddRange(sparseMatches);

            if (matches.Count == beforeDocCount)
            {
                matches.AddRange(await SearchDocumentOverviewProfileMatchesAsync(
                    ds,
                    tenantId,
                    originalQuery,
                    category,
                    docId: null,
                    docPath,
                    Math.Min(scopedLimit, 4),
                    ct,
                    categoryPath,
                    requireLexicalMatch: false,
                    degradedRetrieverRef: degradedRetrieverRef,
                    commandTimeoutSeconds: commandTimeoutSeconds));
            }
        }

        if (matches.Count == 0)
            return [];

        return CalibrateFusedMatches(focusedQuery, matches, originalQuery)
            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.ChunkIndex)
                .First())
            .Take(Math.Max(topK, 8))
            .ToList();
    }


    internal static async Task<List<RagMatch>> SearchExplicitFileHintProfileRecoveryMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string originalQuery,
        string? category,
        string? categoryPath,
        int topK,
        CancellationToken ct,
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(originalQuery) || topK <= 0)
            return [];

        var explicitFileHintGroupCount = ExtractExplicitFileLookupPhraseGroups(originalQuery).Count;
        if (explicitFileHintGroupCount == 0)
            return [];

        var docPaths = await ResolveDocumentHintDocPathsAsync(
            ds,
            tenantId,
            originalQuery,
            category,
            categoryPath,
            limit: Math.Min(Math.Max(1, explicitFileHintGroupCount), Math.Min(4, Math.Max(1, topK))),
            ct);
        if (docPaths.Count == 0)
            return [];

        var matches = new List<RagMatch>();
        foreach (var docPath in docPaths)
        {
            matches.AddRange(await SearchDocumentOverviewProfileMatchesAsync(
                ds,
                tenantId,
                originalQuery,
                category,
                docId: null,
                docPath,
                Math.Min(Math.Max(4, topK), 12),
                ct,
                categoryPath,
                requireLexicalMatch: false,
                degradedRetrieverRef: degradedRetrieverRef,
                commandTimeoutSeconds: commandTimeoutSeconds));
        }

        return matches
            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.ChunkIndex)
                .First())
            .Take(Math.Max(topK, 8))
            .ToList();
    }


    internal static bool ContainsAny(string? text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

}
