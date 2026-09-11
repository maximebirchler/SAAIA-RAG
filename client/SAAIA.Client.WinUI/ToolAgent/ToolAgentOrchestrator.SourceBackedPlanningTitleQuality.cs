using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedTitleQualityCacheMaxEntries = 4096;
    private static readonly int SourceBackedRegexCacheSize = EnsureSourceBackedRegexCacheCapacity();
    private static readonly object SourceBackedTitleQualityCacheGate = new();
    private static readonly Dictionary<string, bool> SourceBackedTitleQualityCache = new(StringComparer.Ordinal);

    private static int EnsureSourceBackedRegexCacheCapacity()
    {
        const int requiredCapacity = 512;
        if (Regex.CacheSize < requiredCapacity)
            Regex.CacheSize = requiredCapacity;
        return Regex.CacheSize;
    }

    private static bool LooksLikeReferenceAttributionSourceTitle(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (LooksLikePersonNameListAttributionTitle(normalizedTitle))
            return true;

        var evidence = NormalizeLexicalLookup(BuildPrimarySourceBackedPlanningEvidenceText(candidate.Hit));
        if (evidence.Length < normalizedTitle.Length + 12)
            return false;

        var escapedTitle = Regex.Escape(normalizedTitle);
        return Regex.IsMatch(
            evidence,
            $@"\b(?:references?|source|sources|bibliographie|bibliography|credits?)\s*:\s*.{{0,120}}\b{escapedTitle}\b\s*[.:\-]?\s*[Â«â€œ""]",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikePersonNameListAttributionTitle(string normalizedTitle)
    {
        var value = CollapseWhitespace(normalizedTitle);
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOf(',', StringComparison.Ordinal) < 0
            || value.Length > 96)
        {
            return false;
        }

        var segments = Regex.Split(value, @"\s*,\s*")
            .Select(CollapseWhitespace)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length < 3 || segments.Length > 6)
            return false;

        if (segments.Any(static segment =>
                IsGenericStructuredPlanningEvidenceBridgeTerm(segment)
                || ExtractPlanningAnswerSupportTerms(segment).Count() > 2
                || !Regex.IsMatch(segment, @"^[\p{L}'\-]{2,32}(?:\s+[\p{L}'\-]{2,32})?$", RegexOptions.CultureInvariant)))
        {
            return false;
        }

        return segments.Count(static segment => ExtractPlanningAnswerSupportTerms(segment).Any()) >= 3;
    }

    private static bool LooksLikeConcreteStructuredPlanningCandidateTitle(string? title)
    {
        var cleaned = CleanSourceBackedOptionTitle(title);
        var normalized = NormalizeLexicalLookup(cleaned);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!IsUsableSourceBackedOptionTitle(cleaned)
            || LooksLikeGenericCadenceOrTimingStatement(normalized)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
            || LooksLikeProcedureSentenceTitle(normalized))
        {
            return false;
        }

        if (LooksLikeStandaloneStructuredPlanningFieldLabel(normalized)
            || LooksLikeStructuredPlanningFieldOrOcrFragment(normalized))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:guide|guides|conseils?|tips?|astuces?|principes?|principles?|organisation|organization|organiser|organize|planning|planification|calendrier|schedule|horaires?|timing|overview|vue\s+d\s+ensemble|introduction|summary|resume|r[eÃ©]sum[eÃ©]|methode|m[eÃ©]thode|method|cadre|framework|recommandations?|recommendations?|bonnes?\s+pratiques?|best\s+practices?|faq|glossaire|glossary|sommaire|table\s+des\s+matieres|contents?|index|liste\s+des|source|sources|document|documents|page|pages)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:collecter|collectez|organiser|organize|planifier|planifiez|schedule|utiliser|use|using|choisir|choose|verifier|verify|check|lire|read|inspecter|inspect|documenter|document)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sert\s+a|sert\s+Ã |serves?\s+to|permet\s+de|helps?\s+to|aide\s+a|aide\s+Ã |doit\s+etre|doit\s+Ãªtre|should\s+be)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var terms = ExtractPlanningAnswerSupportTerms(normalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0 || terms.Length > 8)
            return false;

        if (terms.Any(static term => term.Length >= 5))
            return true;

        return terms.Length >= 2
            && normalized.Length >= 7
            && terms.All(static term => term.Length >= 4);
    }

    private static bool LooksLikeConcreteStructuredPlanningCandidateNormalizedTitle(string? normalizedTitle)
    {
        var normalized = NormalizeLexicalLookup(normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Length is < 4 or > 90
            || LooksLikeGenericCadenceOrTimingStatement(normalized)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(normalized)
            || LooksLikeProcedureSentenceTitle(normalized)
            || LooksLikeWeakSourceBackedOptionTitle(normalized))
        {
            return false;
        }

        if (LooksLikeStandaloneStructuredPlanningFieldLabel(normalized)
            || LooksLikeStructuredPlanningFieldOrOcrFragment(normalized))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:guide|guides|conseils?|tips?|astuces?|principes?|principles?|organisation|organization|organiser|organize|planning|planification|calendrier|schedule|horaires?|timing|overview|vue\s+d\s+ensemble|introduction|summary|resume|r[eÃ©]sum[eÃ©]|methode|m[eÃ©]thode|method|cadre|framework|recommandations?|recommendations?|bonnes?\s+pratiques?|best\s+practices?|faq|glossaire|glossary|sommaire|table\s+des\s+matieres|contents?|index|liste\s+des|source|sources|document|documents|page|pages)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:collecter|collectez|organiser|organize|planifier|planifiez|schedule|utiliser|use|using|choisir|choose|verifier|verify|check|lire|read|inspecter|inspect|documenter|document)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sert\s+a|sert\s+Ã |serves?\s+to|permet\s+de|helps?\s+to|aide\s+a|aide\s+Ã |doit\s+etre|doit\s+Ãªtre|should\s+be)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var terms = ExtractPlanningAnswerSupportTerms(normalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0 || terms.Length > 8)
            return false;

        if (terms.Any(static term => term.Length >= 5))
            return true;

        return terms.Length >= 2
            && normalized.Length >= 7
            && terms.All(static term => term.Length >= 4);
    }

    private static bool LooksLikeNoisyStructuredPlanningCandidateTitle(string? title)
    {
        var cacheKey = CollapseWhitespace(title ?? string.Empty);
        lock (SourceBackedTitleQualityCacheGate)
        {
            if (SourceBackedTitleQualityCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = LooksLikeNoisyStructuredPlanningCandidateTitleUncached(cacheKey);
        lock (SourceBackedTitleQualityCacheGate)
        {
            if (SourceBackedTitleQualityCache.Count >= SourceBackedTitleQualityCacheMaxEntries)
                SourceBackedTitleQualityCache.Clear();
            SourceBackedTitleQualityCache[cacheKey] = result;
        }

        return result;
    }

    private static bool LooksLikeNoisyStructuredPlanningCandidateTitleUncached(string? title)
    {
        var raw = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (LooksLikeFusedStructuredPlanningFieldLabelFragment(normalized))
            return true;

        if (LooksLikeStructuredFieldLabelActionFragment(normalized))
        {
            return true;
        }

        if (!raw.Any(char.IsLower)
            && !Regex.IsMatch(raw, @"\s", RegexOptions.CultureInvariant)
            && Regex.IsMatch(raw, @"\d$", RegexOptions.CultureInvariant)
            && Regex.Matches(raw, @"\p{L}", RegexOptions.CultureInvariant).Count >= 10)
        {
            return true;
        }

        if (LooksLikeGenericStructuredInventoryTitle(normalized))
            return true;

        if (Regex.IsMatch(raw, @"!{2,}", RegexOptions.CultureInvariant)
            && ExtractQuerySignalTerms(normalized).Take(4).Count() <= 3)
        {
            return true;
        }

        if (Regex.IsMatch(
                raw,
                @"^\s*(?:[\p{L}]{1,5}\s+)?\d{2,5}\s*(?:[+=]{1,3}\s*)+\d{1,5}",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                raw,
                @"^\s*[\p{L}]{1,5}\s+\d{2,5}\s*=\s*[\p{L}0-9]{1,5}\s*$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"\b\d{2,4}\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                normalized,
                @"\b(?:g|kg|mg|ml|cl|l|oz|lb|lbs|min|minutes?|h|heures?|portions?|personnes?|pers)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"\bsuite$", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(normalized, @"\b(?:vers|towards|around|between|entre|avant|before|after)$", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(normalized, @"\b\d{1,3}$", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                normalized,
                @"\b(?:g|kg|mg|ml|cl|l|oz|lb|lbs|min|minutes?|h|heures?|portions?|personnes?|pers)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (raw.Contains(',', StringComparison.Ordinal)
            && Regex.Matches(raw, @"\p{Lu}[\p{Ll}]+", RegexOptions.CultureInvariant).Count >= 2)
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:\d+\s*/\s*)?\d{1,4}\s+(?:fiches?|cards?|records?|entries?|items?|elements?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^\d{1,4}\s+(?:ce|cet|cette|ces|this|that|these|those)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeAudienceOrCollectionSourceBackedHeadingTitle(normalized))
            return true;

        if (LooksLikeDescriptiveSentenceAttachedToStructuredPlanningTitle(normalized)
            || LooksLikeContactOrAddressStructuredPlanningTitle(raw, normalized))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:si|quand|lorsqu|lorsque|when|if|whenever)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeSectionTaxonomyOrInventoryHeadingTitle(raw, normalized))
            return true;

        if (LooksLikeLiveStructuredPlanningInventoryOrActionNoise(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:planning|planification|overview|vue\s+d\s+ensemble|guide|presentation|pr[eÃƒÂ©]sentation|references?|r[eÃƒÂ©]f[eÃƒÂ©]rences?|foundation|fondat(?:i)?on|association|organisation|organization|company|copyright|credits?|imprime|printed)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:possibilit[eÃƒÂ©]|possibility|possibilities|evolution|fonction|function|policy|politique|exactitude|accuracy|utilise\s+si|used\s+if|temps\s+de|time\s+to|nombre\s+de|number\s+of|quantites?\s+donnees?|given\s+quantit(?:y|ies))\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^\d{1,4}\s+(?:references?|r[eÃƒÂ©]f[eÃƒÂ©]rences?|page|pages|section|chapter|chapitre|annexe|appendix)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^(?:a|ÃƒÂ |Ã )\s+partir\s+de\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"(?:components?|composants?|materials?|mat[eÃƒÂ©]riel|requirements?|exigences?)(?:\s*|[a-z0-9]{0,8})(?:preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?)",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"(?:preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?)(?:\s*|[a-z0-9]{0,8})(?:components?|composants?|materials?|mat[eÃƒÂ©]riel|requirements?|exigences?)",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeGlossaryOrDefinitionStructuredPlanningCandidate(normalized))
            return true;

        if (LooksLikeShortOcrContinuationStructuredPlanningTitle(raw, normalized))
            return true;

        if (LooksLikeSplitOcrWordStructuredPlanningTitle(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"(?:components?|composants?|materials?|materiel|requirements?|exigences?)(?:\s*|[a-z0-9]{0,12})(?:preparation|procedure|instructions?|method|methode|steps?|etapes?)",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"(?:preparation|procedure|instructions?|method|methode|steps?|etapes?)(?:\s*|[a-z0-9]{0,12})(?:components?|composants?|materials?|materiel|requirements?|exigences?)",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"^(?:ps|p\s*s)\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(
                raw,
                @"^[A-Z]{2,}\d{3,}[A-Za-z0-9_\-]*",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(raw, @"[_\-\d]", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(raw, @"^[A-Z0-9\s]{3,}COM$", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:votre|vos|your|mon|ma|mes|my|notre|nos|our)\b.{0,90}\b(?:lendemain|next\s+day|tomorrow)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:fondation|foundation|association|company|copyright|credits?|imprime|printed)\b.{0,90}\b(?:" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:puis|then|ensuite|ouvrir|fermer|remplir|remplissez|etaler|etalez|placer|mettre|retirer|fill|spread|place|put|remove|open|close|inspect|record|validate|check|verify|use|add)\b|^(?:\p{L}{4,}ez|[a-z]{4,}(?:er|ir|re))\b(?=.{0,90}\b(?:le|la|les|l|un|une|des|du|de|dans|avec|au|aux|chaque|chacun|chacune|the|a|an|this|these|each|it|them|si|if|quand|when|apres|apr[eÃƒÂ¨]s|after|avant|before|puis|then|ensuite|et|and|ou|or)\b)|^(?:[a-z]{4,}(?:er|ir|re|ez))\s+[a-z]{4,}(?:er|ir|re|ez)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeEmbeddedOperationalActionFragment(normalized))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:le|la|les|l|the|a|an)\s+\p{L}{2,4}\s+\p{L}{2,16}\b.{0,80}\b(?:et|and)\b.{0,48}\b(?:faire|make|do|execute|run|apply)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:pour|to)\s+(?:accompagner|accompany|servir|serve|utiliser|use|complete|completer)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:noirs?|blancs?|rouges?|verts?|bleus?|jaunes?|black|white|red|green|blue|yellow|individuels?|individuals?)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var endsWithSingleUppercaseCode = Regex.IsMatch(raw, @"\b[A-Z]$", RegexOptions.CultureInvariant);
        if (!endsWithSingleUppercaseCode
            && Regex.IsMatch(
                normalized,
                @"\b(?:en|de|du|des|a|au|aux|avec|of|with|and|et|ou|or)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:de|du|des|a|au|aux|avec)\s+(?:de|du|des|a|au|aux|avec)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var commaSegments = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commaSegments.Length >= 3
            && Regex.IsMatch(
                normalized,
                @"^\p{L}{3,24},\s+(?:des|de|du|of|the)\b.{0,120},\s+(?:des|de|du|of|the)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:je|j|tu|il|elle|on|nous|vous|ils|elles|we|you|they|one)\s+(?:peux|peut|pouvons|pouvez|peuvent|can|could|should|must|doit|doivent)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^(?:il\s+faut|vous\s+pouvez|on\s+peut|we\s+can|you\s+can)\b.{0,100}\b(?:" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:le|la|les|l|the|a|an)\s+\p{L}{2,4}\s+\p{L}{3,12}\b.{0,100}\b(?:" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:le|la|les|l|ce|cette|cet|ces|the|this|that|these|those)\s+\p{L}{3,}(?:\s+(?:de|du|des|d|of|for|with|avec|a|to|en)\s+\p{L}{2,}){0,4}\s+(?:en\s+)?(?:fait|makes?|rend|becomes?|devient|est|is|are|allows?|permet|peut|can|should)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:en\s+fait\s+un(?:e)?|makes?\s+(?:it\s+)?a|est\s+un(?:e)?|is\s+a)\s+\p{L}{4,}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^\d+\s+\p{L}{3,}(?:\s+\p{L}{2,}){2,18}\b.{0,100}\b(?:pour|for|to)\s+(?:accompagner|accompany|servir|serve|utiliser|use|complete|completer)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var supportTerms = ExtractPlanningAnswerSupportTerms(normalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (raw.Contains(',', StringComparison.Ordinal)
            && supportTerms.Length is >= 2 and <= 4
            && Regex.IsMatch(
                normalized,
                @"^(?:le|la|les|l|the|a|an)\b.{0,64},\s*(?:(?:le|la|les|l|the|a|an)\b.{0,32})?$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:conservation|conserve|steriliser|st[eÃƒÂ©]riliser|congeler|freez(?:e|er|ing)|storage|stored?|stockage|rangement|frigo|refrigerateur|r[eÃƒÂ©]frig[eÃƒÂ©]rateur|refrigerator|comptoir|counter|shelf|shelves)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalized,
                @"^(?:[a-z]\s+)?(?:ou|or)\b|\b(?:semaine|weeks?|place|placer|mettre|put|bottes?|bundle|bundles|pendant|during|avant|after|apres|apr[eÃƒÂ¨]s)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:congeler|steriliser|st[eÃƒÂ©]riliser|freeze|store|stocker|ranger|conserver)\b|\bpour\s+faire\s+une\s+conserve\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:[ivxlcdm]{1,4}|[a-z])\s+(?:g|kg|mg|ml|cl|l|oz|lb|lbs|mm|cm|m|%)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:[ivxlcdm]{1,6}|[a-z])\s+\p{L}{3,}(?:\s+\p{L}{3,}){0,3}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }


        if (Regex.IsMatch(
                normalized,
                @"^(?:je|j|tu|il|elle|on|nous|vous|ils|elles)\s+\p{L}{3,}(?:\s+\p{L}{2,20}){0,2}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:\d+[a-z]?|[ivxlcdm]{1,6})\s+(?:(?:a\s+){0,2}voir|see|refer|consulter|page|section|chapter|part|partie|annexe|appendix|table|index)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeLeadingConnectorStructuredPlanningFragment(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:le|la|les|l|the)\s+\p{L}{4,}\s+(?:cuit|cuite|cuits|cuites|cooked|ready|pret|prete|pr[eÃƒÂª]t|pr[eÃƒÂª]te)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:idees?|ideas?|suggestions?|conseils?|tips?)\s+(?:de|for|pour)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:(?:un|une|des|du|de\s+la|le|la|les|the|a|an|some|another|other|autre|d\s+autres?)\s+){0,2}(?:exemples?|examples?|suggestions?|propositions?|options?)\b(?:\s+(?:parmi|among|between|de|of|pour|for|d\s+autres?)\b.*)?$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:parmi\s+d\s+autres?|among\s+others?)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(normalized, @"\b(?:exemples?|examples?|suggestions?|propositions?|options?)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }


        if (Regex.IsMatch(
                normalized,
                @"^(?:cette|ce|this|esta|essa|questa)\s+option$|\bet\s+al\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:voici|here\s+(?:are|is)|aqui\s+(?:hay|esta)|eis|hier\s+(?:sind|ist)|ecco)\b.{0,90}\b(?:idees?|ideas?|suggestions?|conseils?|tips?|recommandations?|recommendations?|alternatives?|remplacer|replace|instead)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:voici|here\s+(?:are|is)|aqui\s+(?:hay|esta)|eis|hier\s+(?:sind|ist)|ecco)\s+(?:quelques|some|several|various)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (normalized.Contains("capitulatif", StringComparison.Ordinal)
            && Regex.IsMatch(normalized, @"^(?:voici|here\s+(?:are|is)|aqui\s+(?:hay|esta)|eis|hier\s+(?:sind|ist)|ecco)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:voici|here\s+(?:are|is)|aqui\s+(?:hay|esta)|eis|hier\s+(?:sind|ist)|ecco)\b.{0,90}\b(?:recapitulatif|r[eÃ©]capitulatif|summary|resume|r[eÃ©]sum[eÃ©]|overview)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^(?:voici|here\s+(?:are|is)|aqui\s+(?:hay|esta)|eis|hier\s+(?:sind|ist)|ecco)\b.{0,80}\b(?:qui|que|which|that)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }





        if (Regex.IsMatch(
                normalized,
                @"^(?:puis|then|ensuite|preparer|preparez|faire|faites|ajouter|ajoutez|laisser|laissez|placer|placez|retirer|retirez|deposer|deposez|remuer|remuez|remplir|remplissez|etaler|etalez|place|put|add|remove|fill|spread)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:apres\s+operation|aprÃ¨s\s+operation|jusqu\s+a\s+operation|jusqu\s+Ã \s+operation)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeWeakStructuredPlanningAnchorFollowupTitle(string? title)
    {
        var raw = CollapseWhitespace(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (LooksLikeNoisyStructuredPlanningCandidateTitle(raw))
            return true;

        if (Regex.IsMatch(raw, @"(?:^|[\\/])[^\\/]{0,160}\.pdf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b(?:docpath|document|documents?|fichier|fichiers?|pdf)\b.{0,80}\bpdf\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:profil\s+documentaire|documentary\s+profile|profile\s+documentaire)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:\p{L}{3,24}\s+co|\p{L}{3,24}\s+vers|\p{L}{3,24}\s+entre\s+\p{L}{3,24}\s+et|lorsqu\s+ils\s+\p{L}{4,24})\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:[a-z]\s+)?(?:\p{L}{2,24}\s+)?(?:isolee?s?|isolated|fragment|fragments?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"^(?:position\s+\p{L}{3,24}|laisser\s+de\s+\p{L}{3,24}|adapt[eÃ©]e?\s+la\s+\p{L}{3,24}|\p{L}{3,24}\s+aux\s+horaires)$",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDescriptiveSentenceAttachedToStructuredPlanningTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return Regex.IsMatch(
            normalizedTitle,
            @"\b(?:ce|cet|cette|ces|this|that|these|those|este|esta|estos|estas|questo|questa)\s+\p{L}{4,}(?:\s+\p{L}{4,}){0,3}\s+(?:se\s+\p{L}{4,}|est|sont|is|are|was|were|contains?|contient|permet|allows?|requires?|requiert|devient|becomes?)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeLiveStructuredPlanningInventoryOrActionNoise(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (LooksLikeObservedLiveStructuredPlanningNoiseTitle(normalizedTitle))
            return true;

        return Regex.IsMatch(
                normalizedTitle,
                @"^(?:references?|r[eÃƒÆ’Ã‚Â©]f[eÃƒÆ’Ã‚Â©]rences?|bibliographie|bibliography|credits?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:les?\s+)?etapes?\s+de\s+la\s+(?:prepa|pr[eÃƒÆ’Ã‚Â©]paration)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:au\s+final|finalement)\b.{0,56}\b(?:vaisselle|cleanup|cleaning|rangement)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:occupez|occuper|occupe)\b.{0,80}\b(?:preparation|organisation|planning|planification)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:ce\s+sont|c\s+est|il\s+s\s+agit|these\s+are|this\s+is)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:quels?|quelles?|quoi|pourquoi|comment|when|what|why|how)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:par\s+quoi\s+commencer|quel\s+refrigerateur|quelle\s+refrigerateur)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:avant|apres|before|after)\s+(?:de\s+)?(?:ranger|stocker|conserver|mettre|placer|rangez|stockez|conservez|placez)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:collages?(?:\s+quelques\s+idees?)?|prepares?\s+de\s+temps\s+en\s+temps|on\s+se\s+lance|l\s+avance\s+et\s+sans\s+tracas)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:ge\s+)?flora(?:\s+lycee\s+professionnel)?$|\blycee\s+professionnel\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeObservedLiveStructuredPlanningNoiseTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        return Regex.IsMatch(
                normalizedTitle,
                @"^(?:\p{L}{3,24}\s+et\s+\p{L}{3,24}|l\s+\p{L}{3,24}\s+bien\s+\p{L}{3,24})$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^\d+\s+on\s+(?:prepare|pr[eÃƒÂ©]pare|commence|se\s+lance)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeContactOrAddressStructuredPlanningTitle(string rawTitle, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (rawTitle.Contains('@', StringComparison.Ordinal)
            || Regex.IsMatch(
                normalizedTitle,
                @"\b(?:email|e-mail|courriel|mailto|telephone|phone|mobile|fax|https?|www)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
                normalizedTitle,
                @"\b\d{2,6}\b.{0,80}\b(?:rue|street|avenue|ave|road|rd|boulevard|blvd|route|chemin|lane|drive|dr|square|place|plaza|postcode|postal|zip)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"\b(?:rue|street|avenue|ave|road|rd|boulevard|blvd|route|chemin|lane|drive|dr|square|place|plaza)\b.{0,80}\b\d{2,6}\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeEmbeddedOperationalActionFragment(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:\p{L}{2,18}\s+){2,5}(?:" + OperationalActionLeadPattern + @")\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalizedTitle,
            @"^(?:\p{L}{2,18}\s+){2,5}[a-z]{4,}(?:er|ir|re|ez)\s+(?:le|la|les|l|un|une|des|du|de|dans|avec|au|aux|the|a|an|this|these|each|it|them|with|in|into|on|to|for)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSectionTaxonomyOrInventoryHeadingTitle(
        string rawTitle,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var terms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 2)
            .Take(8)
            .ToArray();
        if (terms.Length is < 2 or > 5)
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:[ivxlcdm]{1,6}|[a-z])\s+\p{L}{3,}(?:\s+(?:de|du|des|d|of|with|and|et|a|au|aux|\p{L}{3,})){0,5}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^\p{L}{4,}s\s+(?:faciles?|easy|simple|basic|rapides?|quick|generales?|general|pratiques?|practical)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^\p{L}{4,}s\s+d[\s'\u2019]*eau\s+\p{L}{3,}$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:sections?|rubriques?|parts?|parties)\s+au\s+\p{L}{3,}$",
                RegexOptions.CultureInvariant)
            || (Regex.IsMatch(
                    normalizedTitle,
                    @"^\p{L}{5,}s\s+\p{L}{4,}$",
                    RegexOptions.CultureInvariant)
                && !Regex.IsMatch(normalizedTitle, @"\p{L}s\s+\p{L}{4,}s$", RegexOptions.CultureInvariant)))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^\p{L}{5,}s\s+\p{L}{4,}\s+(?:modes?|methods?|types?|categories?|classes?|families|familles|sections?|topics?|rubriques?)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^\p{L}{6,}(?:erie|ery)\s+\p{L}{4,}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:container|basket|tray|rack|bin|box|case|panier|bac|plateau|grille|support)\s+\p{L}{4,}$|\b(?:steam|vapeur|equipment|tool|tools|materiel|materials?)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeSplitOcrWordStructuredPlanningTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        return Regex.IsMatch(
            normalizedTitle,
            @"\b(?:le|la|les|du|de|des|au|aux)\s+con\s+[a-z]{4,}\b|\bcon\s+(?:servation|combre)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeShortOcrContinuationStructuredPlanningTitle(string rawTitle, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:p|pg|page|pp|etape|step|part|section)\s*\d{1,4}\s+(?:pendant|during|while|avant|before|apres|aprÃ¨s|after|puis|then)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var rawTokens = CollapseWhitespace(rawTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length != 2)
            return false;

        var normalizedTokens = rawTokens
            .Select(NormalizeLexicalLookup)
            .ToArray();
        if (normalizedTokens[0].Length < 5
            || normalizedTokens[1].Length is < 1 or > 2
            || IsLowercaseSourceBackedDisplayParticle(normalizedTokens[1]))
        {
            return false;
        }

        var firstLetters = rawTokens[0].Where(char.IsLetter).ToArray();
        if (firstLetters.Length < 5)
            return false;

        var firstUpperRatio = firstLetters.Count(char.IsUpper) / (double)firstLetters.Length;
        return firstUpperRatio >= 0.75;
    }

    private static bool LooksLikeGlossaryOrDefinitionStructuredPlanningCandidate(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:termes?|terms?|glossaire|glossary|lexique|lexicon|vocabulaire|vocabulary)(?:\b|[a-z])",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:definition|d[eÃ©]finition|definir|d[eÃ©]finir|define|meaning|signification)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var terms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Take(10)
            .ToArray();
        if (terms.Length is < 4 or > 9)
            return false;

        return Regex.IsMatch(
            normalizedTitle,
            @"^(?:\p{L}{3,}\s+){1,3}(?:divide|separer|s[eÃ©]parer|separate|reduire|r[eÃ©]duire|reduce|mettre|place|put|adjust|ajuster|regler|r[eÃ©]gler|calibrate|inspect|verify)\b.{0,90}\b(?:un|une|des|les|le|la|a|an|the|item|element|[eÃ©]l[eÃ©]ment|objet|object|device|dispositif|matiere|mati[eÃ¨]re)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStandaloneStructuredPlanningFieldLabel(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        return Regex.IsMatch(
                normalizedTitle,
                @"^(?:components?|preparation|pr[eÃ©]paration|etapes?|[eÃ©]tapes?|steps?|m[eÃ©]thode|methode|method|procedure|proc[eÃ©]dure|instructions?|quantites?|quantit[eÃ©]s?|quantities?|temps|dur[eÃ©]e|duration|time|materiel|mat[eÃ©]riel|materials?|components?|composants?|requirements?|exigences?|constraints?|contraintes?|notes?|observations?|criteria|criteres|crit[eÃ¨]res|conditions?|parameters?|param[eÃ¨]tres?|checklist|controle|contr[oÃ´]le|verification|v[eÃ©]rification|validation|review|revue)$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:nombre|number|quantite|quantity|quantit[eÃ©])\s+(?:de\s+|of\s+)?(?:portions?|units?|elements?|[eÃ©]l[eÃ©]ments?|items?|pieces?|pi[eÃ¨]ces?|parts?)$",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStructuredPlanningFieldOrOcrFragment(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (normalizedTitle.Length > 64)
            return false;

        if (LooksLikeFusedStructuredPlanningFieldLabelFragment(normalizedTitle))
            return true;

        var hasFieldLabel = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:components?|preparation|pr[eÃ©]paration|nombre|number|quantite|quantity|quantit[eÃ©]|portions?|units?|temps|dur[eÃ©]e|duration|time|materiel|mat[eÃ©]riel|materials?)\b",
            RegexOptions.CultureInvariant);
        if (!hasFieldLabel)
            return false;

        var words = ExtractQuerySignalTerms(normalizedTitle).ToArray();
        var hasNumericNoise = Regex.IsMatch(normalizedTitle, @"(?:^|\s)(?:\d+|[ivxlcdm]{1,4})(?:\s|$)", RegexOptions.CultureInvariant);
        var startsWithDurationField = Regex.IsMatch(normalizedTitle, @"^(?:temps|dur[eÃ©]e|duration|time)\b", RegexOptions.CultureInvariant);
        var hasDurationActionCue = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:signal|minutes?|seconds?|secondes?|heures?|hours?|\d+)\b",
            RegexOptions.CultureInvariant);
        var hasOcrPrefix = Regex.IsMatch(
            normalizedTitle,
            @"^(?:[a-z]{1,3}|[ivxlcdm]{1,4})\s+\d+\s+\p{L}{1,3}\s+",
            RegexOptions.CultureInvariant);

        return hasOcrPrefix
            || (hasNumericNoise && words.Length <= 5)
            || (startsWithDurationField && hasDurationActionCue);
    }

    private static bool LooksLikeFusedStructuredPlanningFieldLabelFragment(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var compact = Regex.Replace(normalizedTitle, @"[^a-z0-9]+", string.Empty, RegexOptions.CultureInvariant);
        if (compact.Length is < 10 or > 72)
            return false;

        const string dataField =
            @"(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|quantites?|quantite|quantities?|nombre|number)";
        const string processField =
            @"(?:preparation|procedure|procedures?|instructions?|method|methode|steps?|etapes?|workflow|operation|operations?|process|processus|execution|validation|controle|verification|review|notes?)";
        const string shortBridge = @"[a-z0-9]{0,8}";

        return Regex.IsMatch(
                compact,
                $"^(?:[a-z]{{0,4}})?{dataField}{shortBridge}{processField}\\d{{0,4}}$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                compact,
                $"^(?:[a-z]{{0,4}})?{processField}{shortBridge}{dataField}\\d{{0,4}}$",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStructuredFieldLabelActionFragment(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        const string dataFieldLead =
            @"(?:components?|composants?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)";
        const string structureSeparator =
            @"(?:[*:;\u2022]|\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|workflow|operation|process|execution)\b)";
        var actionLead = @"(?:" + OperationalActionLeadPattern + @")|[a-z]{4,}(?:er|ir|re|ez)";

        return Regex.IsMatch(
                normalizedTitle,
                @"^(?:" + dataFieldLead + @")\b.{0,36}\b(?:" + actionLead + @")\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalizedTitle,
                @"^(?:" + dataFieldLead + @")\b.{0,32}" + structureSeparator,
                RegexOptions.CultureInvariant);
    }
}
