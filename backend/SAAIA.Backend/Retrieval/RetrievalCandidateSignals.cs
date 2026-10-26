using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalRows;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalCandidateSignals
{

    internal static bool LooksLikeFacetOnlyComparativeContentCard(
        IReadOnlyList<string> queryTokens,
        DocumentProfileContentCard card)
    {
        var titleTokens = ExtractLexicalQueryTokens(card.Title);
        if (titleTokens.Count == 0)
            return true;

        var nonFacetTitleTokens = titleTokens
            .Where(static token => !IsComparativeContentCardFacetToken(token))
            .ToArray();
        if (nonFacetTitleTokens.Length == 0)
            return true;

        if (HasStrongDocumentProfileContentCardEvidence(card))
            return false;

        var titleFacetHits = titleTokens.Count(IsComparativeContentCardFacetToken);
        if (titleFacetHits > 0 && titleTokens.Count <= 5)
        {
            var titleCoverage = ComputeLexicalCoverage(queryTokens, card.Title);
            return titleCoverage > 0.0;
        }

        return false;
    }


    private static bool IsComparativeContentCardFacetToken(string token)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(token));
        return normalized is
            "time" or "temps" or "duration" or "duree" or "minute" or "minutes" or "horaire" or "schedule"
            or "material" or "materials" or "materiel" or "materiaux" or "equipment" or "equipement" or "equipements" or "tool" or "tools" or "outil" or "outils" or "item" or "items" or "part" or "parts" or "supply" or "supplies"
            or "risk" or "risks" or "risque" or "risques" or "failure" or "failures" or "ratage" or "ratages" or "danger" or "dangers" or "erreur" or "erreurs"
            or "difficulty" or "difficulties" or "difficulte" or "difficultes" or "degree" or "degre" or "level" or "niveau"
            or "component" or "components" or "composant" or "composants"
            or "procedure" or "procedures" or "method" or "methods" or "methode" or "methodes" or "step" or "steps" or "etape" or "etapes"
            or "overview" or "summary" or "resume" or "presentation" or "introduction" or "guide" or "guides" or "pictogramme" or "pictogrammes" or "pictogram" or "pictograms"
            or "quantity" or "quantities" or "quantite" or "quantites" or "possibilite" or "possibilites" or "evolution";
    }


    internal static bool LooksLikeProceduralFragmentContentCardTitle(string title)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        if (LooksLikeSubjectlessInstructionFragmentTitle(normalized))
            return true;

        var first = tokens[0];
        if (tokens.Length >= 4
            && ContainsAny(
                $" {normalized} ",
                " avant de ",
                " before ",
                " afin de ",
                " afin d ",
                " apres avoir ",
                " after ",
                " dans le ",
                " dans la ",
                " avec le ",
                " avec la ",
                " without ",
                " sans qu ",
                " pendant ",
                " jusqu ",
                " until "))
        {
            return true;
        }

        if (tokens.Length >= 5 && first is
                "add" or "ajouter" or "ajoutez" or "apply" or "appliquer" or "assemble" or "assembler"
                or "check" or "controlez" or "configure" or "configurer" or "connect" or "connecter"
                or "copy" or "copier" or "delete" or "demarrer" or "install" or "installer"
                or "lancer" or "mettre" or "open" or "ouvrir" or "place" or "placer"
                or "prepare" or "preparer" or "remove" or "remplacer" or "replace" or "retirer"
                or "run" or "select" or "selectionner" or "set" or "start" or "stop"
                or "supprimer" or "update" or "use" or "utiliser" or "validate" or "valider"
                or "verify" or "verifier")
        {
            return true;
        }

        return false;
    }


    private static bool LooksLikeSubjectlessInstructionFragmentTitle(string normalizedTitle)
    {
        var value = CollapseWhitespace(normalizedTitle);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var leadMatch = Regex.Match(
            value,
            @"^(?<lead>\p{L}{4,24})(?:\s+(?<rest>.+))?$",
            RegexOptions.CultureInvariant);
        if (!leadMatch.Success || string.IsNullOrWhiteSpace(leadMatch.Groups["rest"].Value))
            return false;

        var lead = leadMatch.Groups["lead"].Value;
        var rest = CollapseWhitespace(leadMatch.Groups["rest"].Value);
        var looksImperative = Regex.IsMatch(lead, @"ez$", RegexOptions.CultureInvariant);
        var looksInfinitive = Regex.IsMatch(lead, @"(?:er|ir|re)$", RegexOptions.CultureInvariant);
        if (!looksImperative && !looksInfinitive)
            return false;

        var startsWithDirectObject = Regex.IsMatch(
            rest,
            @"^(?:l|le|la|les|un|une|des|du|de\s+la|d|the|a|an|some)\b",
            RegexOptions.CultureInvariant);
        var startsWithPreposition = Regex.IsMatch(
            rest,
            @"^(?:a|au|aux|avec|dans|en|sur|sous|pour|to|into|with|in|on|over|under)\b",
            RegexOptions.CultureInvariant);
        if (!startsWithDirectObject && !startsWithPreposition)
            return false;

        var normalizedRest = NormalizeForLexicalSignal(rest);
        if (string.IsNullOrWhiteSpace(normalizedRest))
            return false;

        var restTokens = normalizedRest
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .Take(5)
            .ToArray();

        return restTokens.Length is >= 1 and <= 4;
    }


    internal static bool LooksLikeGenericContentSectionCardTitle(string title)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is 0 or > 4)
        {
            return normalized.StartsWith("quantite ", StringComparison.Ordinal)
                   || normalized.StartsWith("quantites ", StringComparison.Ordinal)
                   || normalized.StartsWith("quantity ", StringComparison.Ordinal)
                   || normalized.StartsWith("quantities ", StringComparison.Ordinal);
        }

        return tokens.All(static token => StructuredContentLexicon.IsItemizedCueToken(token)
            || token is "quantity" or "quantities" or "quantite" or "quantites"
            or "item" or "items" or "element" or "elements"
            or "requirement" or "requirements" or "supply" or "supplies"
            or "material" or "materials" or "materiel" or "equipment" or "equipement"
            or "technique" or "techniques" or "method" or "methods" or "methode" or "methodes"
            or "preparation" or "preparacion" or "preparo" or "zubereitung"
            or "procedure" or "procedures" or "step" or "steps" or "etape" or "etapes"
            or "realisation" or "realization" or "instructions" or "instruction"
            or "overview" or "summary" or "resume" or "presentation" or "introduction");
    }


    internal static bool LooksLikeTruncatedContentCardTitle(string title)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        var last = tokens[^1];
        return last is
            "a" or "au" or "aux" or "avec" or "de" or "du" or "des" or "d" or "l" or "la" or "le" or "les"
            or "and" or "or" or "of" or "to" or "with" or "for" or "from" or "the"
            or "y" or "e" or "con" or "para" or "por" or "do" or "da" or "dos" or "das"
            or "und" or "oder" or "mit" or "zu" or "zur" or "zum";
    }


    internal static bool LooksLikeQuantityOrOcrFragmentContentCardTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var trimmed = title.Trim();
        var first = trimmed[0];
        if (!char.IsLetter(first))
            return true;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(trimmed));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (Regex.IsMatch(normalized, @"^\d+\b", RegexOptions.CultureInvariant))
            return true;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        return tokens[0] is
            "quantite" or "quantites" or "quantity" or "quantities"
            or "nombre" or "number" or "nombres"
            or "page" or "pages";
    }


    internal static int ComputeWeakDocumentProfileContentCardEvidencePenalty(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return 0;

        var facts = evidence.QuantityFacts ?? [];
        if (facts.Count == 0)
            return 0;

        var weakFacts = facts.Count(IsWeakDocumentProfileQuantityFact);
        if (weakFacts == 0)
            return 0;

        var penalty = 0;
        if (weakFacts == facts.Count)
            penalty += 7;
        else if (weakFacts * 2 >= facts.Count)
            penalty += 4;
        else
            penalty += 1;

        return penalty;
    }


    private static bool IsWeakDocumentProfileQuantityFact(DocumentProfileQuantityFact fact)
    {
        if (fact.Value >= 1000)
            return true;

        var text = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(
            $"{fact.Label} {fact.Unit} {fact.SourceText}"));
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (text.Length >= 90 && fact.Value >= 100)
            return true;

        return ContainsAny(
            $" {text} ",
            " page ",
            " pages ",
            " fiche ",
            " fiches ",
            " livret ",
            " copyright ",
            " imprime ",
            " edition ",
            " tel ",
            " www ",
            " http ",
            " ven ");
    }


    internal static bool IsContentCardSafeToExpose(DocumentProfileContentCard card, int? pageStart, int? pageEnd)
    {
        if (card.PageStart is > 0 || card.PageEnd is > 0)
        {
            return IsDeterministicDocumentProfileContentCardKind(card.Kind)
                   || HasGroundedDocumentProfileContentCardEvidence(card.Evidence)
                   || HasTechnicalIdentifier(card.Title);
        }

        if (HasGroundedDocumentProfileContentCardEvidence(card.Evidence))
            return true;

        return pageStart is > 0 || pageEnd is > 0
            ? false
            : HasTechnicalIdentifier(card.Title);
    }


    private static bool IsDeterministicDocumentProfileContentCardKind(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind)
            ? string.Empty
            : kind.Trim().ToLowerInvariant();
        return normalized is "section" or "exact_lead" or "page_embedded_title" or "unit_lead" or "standard_ref";
    }


    private static bool HasGroundedDocumentProfileContentCardEvidence(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return false;

        if ((evidence.Facts ?? []).Any(static fact =>
                !string.IsNullOrWhiteSpace(fact.SourceText)
                || fact.PageStart is > 0
                || fact.PageEnd is > 0))
        {
            return true;
        }

        return (evidence.QuantityFacts ?? []).Any(static fact => !string.IsNullOrWhiteSpace(fact.SourceText));
    }


    private static bool HasTechnicalIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && ExtractLexicalQueryTokens(value).Any(IsReferenceLikeLookupTerm);


    internal static bool ContentCardOverlapsMatchPageRange(DocumentProfileContentCard card, int? pageStart, int? pageEnd)
    {
        if (pageStart is null or <= 0)
            return true;

        var matchStart = pageStart.Value;
        var matchEnd = pageEnd is > 0 ? Math.Max(pageEnd.Value, matchStart) : matchStart;

        if (card.PageStart is null or <= 0)
            return false;

        var cardStart = card.PageStart.Value;
        var cardEnd = card.PageEnd is > 0 ? Math.Max(card.PageEnd.Value, cardStart) : cardStart;
        return cardStart <= matchEnd && cardEnd >= matchStart;
    }


    internal static double ComputeDocumentProfileContentCardQueryScore(
        string query,
        IReadOnlyList<string> queryTokens,
        DocumentProfileContentCard card)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(card.Title))
            return 0.0;

        var title = card.Title.Trim();
        var signals = string.Join(' ', card.Signals ?? Array.Empty<string>());
        var evidenceText = BuildDocumentProfileContentCardEvidenceLookupText(card.Evidence);
        var candidateText = BuildDocumentProfileContentCardQueryLookupText(card);
        var coverage = ComputeLexicalCoverage(queryTokens, candidateText);
        var titleCoverage = ComputeLexicalCoverage(queryTokens, title);
        var signalCoverage = ComputeLexicalCoverage(queryTokens, signals);
        var nonEvidenceSignalCoverage = ComputeLexicalCoverage(
            queryTokens,
            string.Join(' ', (card.Signals ?? Array.Empty<string>()).Where(static signal => !IsStructuredEvidenceDerivedContentCardSignal(signal))));
        var evidenceCoverage = ComputeLexicalCoverage(queryTokens, evidenceText);
        if (coverage <= 0.0 && titleCoverage <= 0.0 && signalCoverage <= 0.0 && evidenceCoverage <= 0.0)
            return 0.0;
        if (titleCoverage <= 0.0
            && nonEvidenceSignalCoverage <= 0.0
            && (signalCoverage > 0.0 || evidenceCoverage > 0.0)
            && !HasStrongEvidenceOnlyContentCardSupport(queryTokens, card.Evidence))
        {
            return 0.0;
        }

        var score = (coverage * 100.0) + (titleCoverage * 70.0) + (signalCoverage * 18.0) + (evidenceCoverage * 42.0);
        var normalizedQuery = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        var normalizedTitle = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (normalizedTitle.Length >= 4 && normalizedQuery.Length >= 4)
        {
            if (normalizedQuery.Contains(normalizedTitle, StringComparison.Ordinal))
                score += 90.0 + Math.Min(35.0, normalizedTitle.Length / 2.0);
            else if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal) && normalizedQuery.Length >= 8)
                score += 45.0;
        }

        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(24)
            .ToArray();
        if (phraseTerms.Any(term => ContainsOrderedPhraseWindow(title, term, maxGapChars: 30)))
            score += 35.0;
        else if (phraseTerms.Any(term => ContainsOrderedPhraseWindow(candidateText, term, maxGapChars: 45)))
            score += 18.0;
        else if (!string.IsNullOrWhiteSpace(evidenceText)
                 && phraseTerms.Any(term => ContainsOrderedPhraseWindow(evidenceText, term, maxGapChars: 45)))
        {
            score += 24.0;
        }

        var titleAnchorCount = CountSpecificLexicalAnchors(queryTokens, title);
        if (titleAnchorCount > 0)
            score += Math.Min(36.0, titleAnchorCount * 9.0);

        if (titleCoverage <= 0.0 && signalCoverage > 0.0)
            score *= 0.80;
        if (titleCoverage <= 0.0 && signalCoverage <= 0.0 && evidenceCoverage > 0.0)
            score *= 0.95;

        return score;
    }


    private static bool HasStrongEvidenceOnlyContentCardSupport(
        IReadOnlyList<string> queryTokens,
        DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null || queryTokens.Count == 0)
            return false;

        var parts = new List<string>();
        AddMultiTokenEvidenceLabel(parts, evidence.ScaleBasis?.Label);

        foreach (var fact in evidence.QuantityFacts ?? [])
        {
            AddMultiTokenEvidenceLabel(parts, fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
        }

        foreach (var reason in evidence.NonScalableReasons ?? [])
            AddMultiTokenEvidenceLabel(parts, reason);

        foreach (var fact in evidence.Facts ?? [])
        {
            AddMultiTokenEvidenceLabel(parts, fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
        }

        var strongEvidenceText = CollapseWhitespace(string.Join(' ', parts));
        if (string.IsNullOrWhiteSpace(strongEvidenceText))
            return false;

        var coverage = ComputeLexicalCoverage(queryTokens, strongEvidenceText);
        return queryTokens.Count <= 2
            ? coverage >= 1.0
            : coverage >= 0.50;
    }


    private static void AddMultiTokenEvidenceLabel(List<string> parts, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return;

        if (ExtractLexicalQueryTokens(label).Count >= 2)
            parts.Add(label);
    }


    private static bool IsStructuredEvidenceDerivedContentCardSignal(string signal)
    {
        if (string.IsNullOrWhiteSpace(signal))
            return false;

        return signal.StartsWith("scale_basis", StringComparison.OrdinalIgnoreCase)
            || signal.StartsWith("scale_basis_count:", StringComparison.OrdinalIgnoreCase)
            || signal.StartsWith("scale_basis_label:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "quantity_list", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "structured_facts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "non_scalable_quantities", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "scalable_quantities", StringComparison.OrdinalIgnoreCase);
    }


    internal static string BuildDocumentProfileContentCardQueryLookupText(DocumentProfileContentCard card)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            card.Title,
            string.Join(' ', card.Signals ?? Array.Empty<string>()),
            BuildDocumentProfileContentCardEvidenceLookupText(card.Evidence)
        }));


    private static string BuildDocumentProfileContentCardEvidenceLookupText(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.SchemaVersion))
            parts.Add(evidence.SchemaVersion);
        if (evidence.ScaleBasis is not null)
        {
            if (!string.IsNullOrWhiteSpace(evidence.ScaleBasis.Label))
                parts.Add(evidence.ScaleBasis.Label!);
            if (evidence.ScaleBasis.Count > 0)
                parts.Add(evidence.ScaleBasis.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var fact in evidence.QuantityFacts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fact.Label))
                parts.Add(fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.Unit))
                parts.Add(fact.Unit);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
            if (fact.Value > 0)
                parts.Add(fact.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        foreach (var reason in evidence.NonScalableReasons ?? [])
        {
            if (!string.IsNullOrWhiteSpace(reason))
                parts.Add(reason);
        }

        if (!string.IsNullOrWhiteSpace(evidence.Language))
            parts.Add($"language:{evidence.Language}");
        if ((evidence.Facts ?? []).Count > 0)
            parts.Add("structured_facts");
        foreach (var fact in evidence.Facts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fact.Kind))
                parts.Add(fact.Kind);
            if (!string.IsNullOrWhiteSpace(fact.Label))
                parts.Add(fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.Value))
                parts.Add(fact.Value);
            if (!string.IsNullOrWhiteSpace(fact.Unit))
                parts.Add(fact.Unit);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
        }

        return CollapseWhitespace(string.Join(' ', parts));
    }


    internal static string CollapseWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);


    internal static (int? PageStart, int? PageEnd) ResolveDocumentProfileMatchPageRange(string? metadataJson, string query)
    {
        if (ExtractLexicalQueryTokens(query).Count == 0)
            return (null, null);

        var contentCards = SelectDocumentProfileContentCards(metadataJson, query);
        if (contentCards.Count == 0)
            return (null, null);

        var selected = contentCards
            .Where(static card => card.PageStart is > 0)
            .FirstOrDefault();

        return selected is null
            ? (null, null)
            : (selected.PageStart, selected.PageEnd ?? selected.PageStart);
    }


    internal static string BuildContentCuesLabel(string? language)
        => DocumentLanguageResolver.PrimarySubtag(language) switch
        {
            "en" => "Content cues",
            "fr" => "Repères de contenu",
            "es" => "Pistas de contenido",
            "pt" => "Pistas de conteúdo",
            "de" => "Inhaltshinweise",
            "it" => "Indizi di contenuto",
            _ => "Content cues"
        };


    internal static string BuildDocumentProfileSectionTitle(string? language)
        => DocumentLanguageResolver.PrimarySubtag(language) switch
        {
            "fr" => "Profil documentaire",
            "es" => "Perfil documental",
            "pt" => "Perfil documental",
            "de" => "Dokumentprofil",
            "it" => "Profilo documentale",
            _ => "Profile summary"
        };


    internal static string FormatDocumentProfileContentCard(DocumentProfileContentCard card)
    {
        var title = card.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return card.PageStart is > 0
            ? $"{title} (p.{card.PageStart})"
            : title;
    }


    internal static IReadOnlyList<RagItemContentCardDto>? BuildMatchedContentCardDtos(RagMatch match)
    {
        // Some retrieval branches join profile cards before final fusion while
        // others return a direct content chunk afterwards. Apply the same
        // mechanical same-chunk projection at the response boundary so every
        // returned content hit exposes equivalent title/text/page provenance.
        var cards = match.MatchedContentCards
                    ?? BuildChunkSectionTitleFallbackMatchedContentCards(match);
        if (cards is null || cards.Count == 0)
            return null;

        return cards
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new RagItemContentCardDto(
                Title: card.Title.Trim(),
                ContentCardId: card.ContentCardId,
                PageStart: card.PageStart,
                PageEnd: card.PageEnd,
                Kind: card.Kind,
                Signals: card.Signals,
                Evidence: card.Evidence))
            .ToArray();
    }


    internal static RagItemProfileSignalsDto? BuildProfileSignalsDto(RagProfileSignals? signals)
    {
        if (signals is null)
            return null;

        return new RagItemProfileSignalsDto(
            ProfileVersion: NormalizeOptionalDtoText(signals.ProfileVersion),
            Language: NormalizeOptionalDtoText(signals.Language),
            Keywords: NormalizeDtoList(signals.Keywords),
            Entities: NormalizeDtoList(signals.Entities),
            Topics: NormalizeDtoList(signals.Topics),
            HypotheticalQuestions: NormalizeDtoList(signals.HypotheticalQuestions),
            Limits: NormalizeDtoList(signals.Limits),
            MatchedTerms: NormalizeDtoList(signals.MatchedTerms),
            MatchCount: signals.MatchCount);
    }


    private static string? NormalizeOptionalDtoText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "und", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }


    private static IReadOnlyList<string>? NormalizeDtoList(IEnumerable<string>? values, int maxItems = 12)
    {
        if (values is null)
            return null;

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }


    internal static RagItemSelectionHintsDto BuildSelectionHints(
        RagMatch match,
        RagItemExtractionQualityDto? quality = null)
    {
        var retriever = ResolveRetriever(match);
        var actionabilityScore = 0;
        var supportScore = 0;
        var fragmentScore = 0;
        var navigationScore = 0;
        var qualityPenalty = ComputeSelectionQualityPenalty(quality);

        if (LooksLikeStructuredAnswerUnit(match))
            actionabilityScore += 12;
        else if (LooksLikeStructuredAnswerChunk(match))
            actionabilityScore += 8;

        if (match.MatchedContentCards is { Count: > 0 })
        {
            actionabilityScore += match.MatchedContentCards.Any(static card =>
                string.Equals(card.Kind, "unit_lead", StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.Kind, "exact_lead", StringComparison.OrdinalIgnoreCase))
                ? 5
                : 3;
            supportScore += 2;
        }

        if (string.Equals(retriever, "exact_match", StringComparison.Ordinal))
            actionabilityScore += 5;
        if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
            actionabilityScore += 2;
        if (string.Equals(retriever, "standard_reference_document_name", StringComparison.Ordinal))
            actionabilityScore += 3;
        if (string.Equals(retriever, "document_profile", StringComparison.Ordinal))
            supportScore += 6;
        if (string.Equals(retriever, "linked_context", StringComparison.Ordinal))
            fragmentScore += 6;
        if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
            supportScore += 2;

        if (LooksLikeNavigationalChunk(match))
            navigationScore += ComputeNavigationHintScore(match);
        if (LooksLikeGlossaryChunk(match))
            navigationScore += 6;
        if (LooksLikeSourceListChunk(match))
            navigationScore += 8;
        if (LooksLikeDocumentOverviewChunk(match))
            supportScore += 4;

        var textLength = (match.Text ?? string.Empty).Trim().Length;
        if (textLength is > 0 and < 140 && !LooksLikeStructuredAnswerChunk(match))
            fragmentScore += 3;

        ApplySelectionQualityPenalty(
            qualityPenalty,
            ref actionabilityScore,
            ref supportScore,
            ref fragmentScore);

        var evidenceRole =
            qualityPenalty >= 10 ? "low_confidence" :
            navigationScore >= Math.Max(7, actionabilityScore + 2) ? "navigation" :
            fragmentScore >= Math.Max(8, actionabilityScore + 3) ? "fragment" :
            actionabilityScore >= Math.Max(8, supportScore + 1) ? "actionable_item" :
            supportScore >= 5 ? "supporting_context" :
            "advisory";

        return new RagItemSelectionHintsDto(
            EvidenceRole: evidenceRole,
            ActionabilityScore: actionabilityScore,
            SupportScore: supportScore,
            FragmentScore: fragmentScore,
            NavigationScore: navigationScore,
            QualityPenalty: qualityPenalty);
    }


    private static void ApplySelectionQualityPenalty(
        int qualityPenalty,
        ref int actionabilityScore,
        ref int supportScore,
        ref int fragmentScore)
    {
        if (qualityPenalty <= 0)
            return;

        actionabilityScore = Math.Max(0, actionabilityScore - qualityPenalty);
        supportScore = Math.Max(0, supportScore - Math.Max(1, qualityPenalty / 2));
        if (qualityPenalty >= 10)
            fragmentScore += Math.Min(8, qualityPenalty / 2);
    }


    internal static int ComputeSelectionQualityPenalty(RagItemExtractionQualityDto? quality)
        => ComputeRetrievalQualityScore(quality).SelectionPenalty;


    private static int ComputeNavigationHintScore(RagMatch match)
    {
        if (match.NavigationScore is double score)
        {
            if (score >= 0.90)
                return 12;
            if (score >= 0.80)
                return 10;
            if (score >= 0.72)
                return 8;
            if (score >= 0.55)
                return 4;
        }

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return 10;
        if (string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase))
            return 4;

        return 10;
    }


    internal static bool ShouldSupplementSparseWithLexicalFallback(string? category, string query)
        => ShouldSupplementSparseWithLexicalFallback(!string.IsNullOrWhiteSpace(category), query);


    internal static bool ShouldSupplementSparseWithLexicalFallback(bool scoped, string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return false;

        if (ContainsQuantityComputationIntent(query)
            && ExtractQuotedLookupPhrases(query).Count == 0)
        {
            return false;
        }

        if (ContainsBroadOnlySparseQueryIntent(query))
        {
            var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
            return scoped
                && ShouldPreferComparativeDocumentDiversity(query)
                && ContainsAny(
                    normalized,
                    " compare ",
                    " comparer ",
                    " comparez ",
                    " comparaison ",
                    " comparative ",
                    " comparatif ",
                    " comparison ",
                    " comparar ",
                    " compara ",
                    " confronta ",
                    " choisir entre ",
                    " choisis entre ",
                    " choisissez entre ",
                    " choose between ",
                    " select between ",
                    " vergleiche ",
                    " vergleichen ")
                && ContainsAny(
                    normalized,
                    " choisir ",
                    " choix ",
                    " choose ",
                    " select ",
                    " selection ",
                    " recommande ",
                    " recommend ",
                    " prefer ",
                    " favor ",
                    " favour ");
        }

        var hasStrongToken = tokens.Any(static token =>
            token.Length >= 7
            || token.Any(char.IsDigit)
            || IsReferenceLikeLookupTerm(token));

        if (scoped)
            return hasStrongToken || tokens.Count <= 4;

        return hasStrongToken && tokens.Count <= 4;
    }


    internal static bool ContainsBroadOnlySparseQueryIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var hasBroadIntent = ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ShouldPreferDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query);
        if (ExtractQuotedLookupPhrases(query).Count > 0)
        {
            return false;
        }

        if (ExtractFocusedLookupPhrases(query).Count > 0
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized)
            && !ContainsSituationalBroadSelectionIntent(normalized)
            && !ShouldPreferComparativeDocumentDiversity(query))
        {
            return false;
        }

        return hasBroadIntent;
    }


    internal static bool ContainsQuantityComputationIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsAny(normalized,
            " combien ",
            " quantite ",
            " quantites ",
            " how many ",
            " quantity ",
            " quantities ",
            " calculate ",
            " calcula ",
            " calcular ",
            " calcular ",
            " quante ",
            " quanti ",
            " wie viele ",
            " menge ",
            " mengen ");
    }


    internal static bool ShouldSkipDocumentProfileSearchForQuantityLookup(string query)
    {
        if (!ContainsQuantityComputationIntent(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return !HasReferenceLikeQueryToken(query)
            && !ContainsDocumentOverviewIntent(query)
            && !ContainsDocumentCorpusCountIntent(normalized)
            && !ContainsBroadScopedSynthesisIntent(normalized)
            && !ContainsSituationalBroadSelectionIntent(normalized);
    }


    internal static bool ContainsDocumentCorpusCountIntent(string normalized)
        => ContainsAny(normalized,
            " document ",
            " documents ",
            " source ",
            " sources ",
            " fichier ",
            " fichiers ",
            " file ",
            " files ",
            " manual ",
            " manuals ",
            " rapport ",
            " rapports ",
            " report ",
            " reports ",
            " pdf ",
            " pdfs ",
            " documento ",
            " documentos ",
            " documento ",
            " documenti ",
            " dokument ",
            " dokumente ",
            " dokumenten ");


    internal static bool ShouldBackfillEnumerativeSearch(string query, int selectedCount, int topK)
    {
        if (selectedCount >= topK || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsEnumerativeLookupIntent(normalized) || ExtractFocusedLookupPhrases(query).Count > 0)
        {
            return BuildFocusedLexicalBackfillQuery(query).Length > 0;
        }

        return false;
    }


    internal static bool ShouldBackfillFuzzyTitleLead(string query, IReadOnlyList<RagMatch> selected)
    {
        if (string.IsNullOrWhiteSpace(query) || ExtractFocusedLookupPhrases(query).Count == 0)
            return false;
        if (HasExplicitFileLookupIntent(query))
            return false;
        if (ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query))
            return false;

        if (selected.Count == 0 || !selected.Any(static match => IsContentSelectionCandidate(match)))
            return true;
        if (selected.Any(static match => IsResolvedTitleOrNavigationRoute(match)))
            return false;

        var focusedLookupPhrase = BuildFocusedLexicalBackfillQuery(query);
        var lexicalTokens = ExtractTitlePruneTokens(focusedLookupPhrase)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (lexicalTokens.Length < 2)
            return false;

        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        return !selected.Any(match => CountSpecificLexicalAnchors(lexicalTokens, GetPreciseTitleSignalText(match)) >= requiredAnchorCount);
    }


    internal static bool ShouldRunFocusedLookupEmptySelectionRecovery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ContainsDocumentOverviewIntent(query)
            || ShouldPreferComparativeDocumentDiversity(query)
            || HasExplicitFileLookupIntent(query))
        {
            return false;
        }

        var focusedPhrases = ExtractFocusedLookupPhrases(query);
        if (focusedPhrases.Count == 0)
            return false;

        var focusedQuery = BuildFocusedLexicalBackfillQuery(query);
        if (string.IsNullOrWhiteSpace(focusedQuery))
            return false;

        var focusedTokens = ExtractTitlePruneTokens(focusedQuery)
            .Where(IsFocusedLookupSelectionAnchorToken)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return focusedTokens.Length is >= 1 and <= 6;
    }


    internal static bool ContainsEnumerativeLookupIntent(string normalized)
        => ContainsAny(normalized,
            " est ce qu il y a ",
            " est ce qu il existe ",
            " il y a ",
            " y a t il ",
            " existe t il ",
            " quels ",
            " quelles ",
            " lesquels ",
            " lesquelles ",
            " liste ",
            " lister ",
            " trouve ",
            " trouver ",
            " recherche ",
            " chercher ",
            " montre ",
            " afficher ",
            " existent ",
            " disponibles ",
            " qui parlent ",
            " qui mentionnent ",
            " qui contiennent ",
            " avec ",
            " which ",
            " what ",
            " are there ",
            " is there ",
            " do we have ",
            " there ",
            " list ",
            " find ",
            " search ",
            " show ",
            " available ",
            " gibt es ",
            " contain ",
            " contains ",
            " mentioning ",
            " mentions ",
            " welche ",
            " finden ",
            " zeige ",
            " cuales ",
            " lista ",
            " encuentra ",
            " hay ",
            " existe ",
            " existen ",
            " ha ",
            " existem ",
            " ci sono ",
            " esistono ",
            " quais ",
            " elenco ",
            " elenca ",
            " trova ");

    internal static bool ContainsCorpusExistenceIntent(string normalized)
        => ContainsAny(normalized,
            " est ce qu il y a ",
            " est ce qu il existe ",
            " il y a ",
            " y a t il ",
            " existe t il ",
            " are there ",
            " is there ",
            " do we have ",
            " there ",
            " gibt es ",
            " hay ",
            " existe ",
            " existen ",
            " ha ",
            " existem ",
            " ci sono ",
            " esistono ");


    internal static string BuildFocusedLexicalBackfillQuery(string query)
    {
        var focusedLookupPhrases = ExtractFocusedLookupPhrases(query)
            .Select(static phrase =>
            {
                var normalizedPhrase = phrase.Trim();
                var withoutContext = TrimTrailingFocusedLookupContext(normalizedPhrase);
                return string.IsNullOrWhiteSpace(withoutContext) ? normalizedPhrase : withoutContext;
            })
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var focusedLookupPhrase = focusedLookupPhrases
            .FirstOrDefault(phrase => !IsFocusedLookupPhraseDocumentHintOnly(query, phrase))
            ?? focusedLookupPhrases.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(focusedLookupPhrase))
            return focusedLookupPhrase;

        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var foldedLookup = FoldDiacritics(lookup);
        var phrase = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .OrderByDescending(term => PhraseOccursInQuery(term, lookup, foldedLookup))
            .ThenByDescending(ComputeFocusedBackfillTermScore)
            .ThenBy(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
            .ThenBy(static term => term.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            var normalizedPhrase = NormalizeFocusedLookupPhraseForQuery(phrase, query) ?? phrase.Trim();
            var withoutContext = TrimTrailingFocusedLookupContext(normalizedPhrase);
            return string.IsNullOrWhiteSpace(withoutContext) ? normalizedPhrase : withoutContext;
        }

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !FocusedLookupRequestLeadTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(5)
            .ToArray();

        return tokens.Length == 0
            ? string.Empty
            : string.Join(' ', tokens);
    }


    internal static IReadOnlyList<string> ExtractFocusedLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var phrases = new List<string>();
        foreach (var quotedPhrase in ExtractQuotedLookupPhrases(query))
            AddFocusedLookupPhrase(phrases, quotedPhrase, query, allowImplicitDocumentHintTrim: false);

        // Keep casing until the target has been extracted. A terminal upper-case
        // token can be a document identifier (for example "Mode A"), whereas
        // the same lower-case token is often only an incomplete article.
        var surface = FoldDiacritics(query);
        surface = Regex.Replace(surface, @"[\u2010-\u2015_\-]+", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"['\u2019]", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(surface))
            return phrases.Distinct(StringComparer.Ordinal).ToArray();
        if (IsCurrentPassageScopedLookup(surface))
            return phrases.Distinct(StringComparer.Ordinal).ToArray();

        foreach (var pattern in FocusedLookupTargetPatterns)
        {
            foreach (var match in SafeRegexMatches(pattern, surface))
                AddFocusedLookupPhrase(phrases, match.Groups["target"].Value, query);
        }

        var trailingLookupFacetTitle = ExtractTrailingLookupFacetTitlePhrase(query);
        if (!string.IsNullOrWhiteSpace(trailingLookupFacetTitle))
            AddFocusedLookupPhrase(phrases, trailingLookupFacetTitle, query, allowImplicitDocumentHintTrim: false);

        return phrases
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static string? ExtractTrailingLookupFacetTitlePhrase(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || ContainsDocumentOverviewIntent(query))
            return null;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!TryTrimTrailingLookupFacetTokens(normalized, out var withoutTrailingFacet)
            || string.IsNullOrWhiteSpace(withoutTrailingFacet)
            || string.Equals(withoutTrailingFacet, normalized, StringComparison.Ordinal))
            return null;

        var titleTokens = ExtractTitlePruneTokens(withoutTrailingFacet);
        if (titleTokens.Length is < 2 or > 6)
            return null;

        return withoutTrailingFacet;
    }


    private static bool TryTrimTrailingLookupFacetTokens(string normalized, out string trimmed)
    {
        trimmed = string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count < 3)
            return false;

        var removedFacet = false;
        while (tokens.Count > 0)
        {
            var last = tokens[^1];
            if (IsTrailingLookupFacetToken(last))
            {
                tokens.RemoveAt(tokens.Count - 1);
                removedFacet = true;
                continue;
            }

            if (removedFacet && TitleConnectorTokens.Contains(last))
            {
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }

            break;
        }

        if (!removedFacet || tokens.Count < 2)
            return false;

        trimmed = string.Join(' ', tokens);
        return !IsFocusedLookupMetaInstructionPhrase(trimmed);
    }


    private static bool IsTrailingLookupFacetToken(string token)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(token));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return StructuredContentLexicon.IsItemizedCueToken(normalized)
            || normalized is
                "source" or "sources" or "provenance" or "origine" or "citation" or "citations"
                or "page" or "pages"
                or "etape" or "etapes" or "step" or "steps" or "schritt" or "schritte" or "paso" or "pasos" or "passo" or "passos"
                or "temps" or "time" or "duration" or "duree" or "duracion" or "duracao" or "dauer"
                or "setting" or "settings" or "parametre" or "parametres" or "reglage" or "reglages";
    }


    internal static IReadOnlyList<string> ExtractComparativeLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !ShouldPreferComparativeDocumentDiversity(query))
            return Array.Empty<string>();

        var surface = FoldDiacritics(query).ToLowerInvariant();
        surface = Regex.Replace(surface, @"[\u2010-\u2015_\-]+", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"['\u2019]", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(surface))
            return Array.Empty<string>();

        var phrases = new List<string>();
        foreach (var pattern in ComparativeLookupTargetPatterns)
        {
            foreach (var match in SafeRegexMatches(pattern, surface))
                AddFocusedLookupPhrase(phrases, match.Groups["target"].Value);
        }

        return phrases
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private static void AddFocusedLookupPhrase(
        List<string> phrases,
        string candidate,
        string? sourceQuery = null,
        bool allowImplicitDocumentHintTrim = true)
    {
        var phrase = NormalizeFocusedLookupPhraseForQuery(candidate, sourceQuery, allowImplicitDocumentHintTrim);
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            foreach (var variant in BuildFocusedLookupPhraseVariants(phrase, sourceQuery, allowImplicitDocumentHintTrim))
                phrases.Add(variant);
        }
    }


    private static IEnumerable<string> BuildFocusedLookupPhraseVariants(
        string phrase,
        string? sourceQuery,
        bool allowImplicitDocumentHintTrim)
    {
        if (TryTrimLeadingWeakFocusedLookupContext(phrase, out var focusedTail))
            yield return focusedTail;

        if (allowImplicitDocumentHintTrim
            && TryTrimTrailingImplicitDocumentHint(phrase, sourceQuery, out var withoutImplicitHint))
        {
            var withoutImplicitHintContext = TrimTrailingFocusedLookupContext(withoutImplicitHint);
            yield return string.IsNullOrWhiteSpace(withoutImplicitHintContext)
                ? withoutImplicitHint
                : withoutImplicitHintContext;
        }

        var withoutContext = TrimTrailingFocusedLookupContext(phrase);
        if (!string.IsNullOrWhiteSpace(withoutContext)
            && !string.Equals(withoutContext, phrase, StringComparison.Ordinal))
        {
            yield return withoutContext;
        }

        yield return phrase;
    }


    internal static string TrimTrailingFocusedLookupContext(string phrase)
    {
        var tokens = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 4)
            return phrase;

        for (var i = tokens.Length - 2; i >= 1; i--)
        {
            if (!IsFocusedLookupContextPreposition(tokens[i]))
                continue;

            var head = tokens[..i];
            var headSignalCount = head.Count(IsFocusedLookupSignalToken);
            if (headSignalCount < 2)
                continue;
            var tail = tokens[(i + 1)..];
            if (IsAudienceOnlyFocusedLookupContextPreposition(tokens[i])
                && !IsFocusedLookupAudienceContextTail(tail))
            {
                continue;
            }

            if (IsWeakFocusedLookupContextPreposition(tokens[i])
                && (tokens.Length < 5 || headSignalCount < 3))
            {
                if (!IsFocusedLookupAudienceContextTail(tail))
                    continue;
            }

            if (tail.Length == 0 || tail.Length > 4)
                continue;

            if (tail.Any(static token => token.Length <= 1 || token.All(char.IsDigit))
                && !IsFocusedLookupAudienceContextTail(tail))
            {
                continue;
            }

            return string.Join(' ', head);
        }

        return phrase;
    }


    private static bool TryTrimLeadingWeakFocusedLookupContext(string phrase, out string focusedTail)
    {
        focusedTail = string.Empty;
        var tokens = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count < 4)
            return false;

        for (var i = 1; i < tokens.Count - 1; i++)
        {
            if (!IsFocusedLookupTargetPivotPreposition(tokens[i]))
                continue;

            var head = tokens.Take(i).ToArray();
            var tail = tokens.Skip(i + 1).ToList();
            while (tail.Count > 0 && IsFocusedLookupLeadingEdgeToken(tail[0]))
                tail.RemoveAt(0);

            var headSignalCount = head.Count(IsFocusedLookupSignalToken);
            var tailSignalCount = tail.Count(IsFocusedLookupSignalToken);
            if (headSignalCount > 1 || tailSignalCount < 2 || tail.Count > 8)
                continue;

            var candidate = string.Join(' ', tail);
            if (string.IsNullOrWhiteSpace(candidate) || IsFocusedLookupMetaInstructionPhrase(candidate))
                continue;

            focusedTail = candidate;
            return true;
        }

        return false;
    }


    private static bool IsFocusedLookupTargetPivotPreposition(string token)
        => token is "pour" or "for" or "para" or "per" or "fur" or "fuer" or "zu" or "zum" or "zur";


    private static bool IsFocusedLookupPhraseDocumentHintOnly(string query, string phrase)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalizedQuery = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase)).ToLowerInvariant();
        if (normalizedQuery.Length == 0 || normalizedPhrase.Length == 0)
            return false;

        var phraseIndex = normalizedQuery.IndexOf(normalizedPhrase, StringComparison.Ordinal);
        if (phraseIndex < 0)
            return false;

        var before = normalizedQuery[..phraseIndex]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (before.Length == 0)
            return false;

        return before
            .Reverse()
            .Take(3)
            .Any(IsFocusedLookupDocumentCarrierToken);
    }


    private static bool IsFocusedLookupDocumentCarrierToken(string token)
        => token is "pdf" or "document" or "documents" or "doc" or "docs"
            or "fichier" or "fichiers" or "file" or "files"
            or "livre" or "livres" or "book" or "books"
            or "manual" or "manuals" or "manuel" or "manuels"
            or "guide" or "guides";


    private static bool IsFocusedLookupAudienceContextTail(IReadOnlyList<string> tail)
    {
        if (tail.Count is < 2 or > 5)
            return false;

        var signalCount = tail.Count(static token =>
            token is "audience" or "auditoire" or "atelier" or "classe" or "collectif" or "enfant" or "enfants"
                or "group" or "groupe" or "groupes" or "kids" or "children" or "personne" or "personnes"
                or "people" or "participants" or "participant" or "invites" or "guests");
        if (signalCount == 0)
            return false;

        return tail.Any(static token => token is "un" or "une" or "des" or "deux" or "trois" or "plusieurs" or "groupe" or "group" or "classe" or "atelier" or "pour");
    }


    private static bool IsFocusedLookupContextPreposition(string token)
        => token is "a" or "au" or "aux" or "avec" or "chez" or "d" or "dans" or "de" or "du" or "en" or "pour" or "sur"
            or "about" or "for" or "from" or "in" or "on" or "with"
            or "con" or "para" or "sobre"
            or "com" or "em"
            or "mit" or "uber" or "ueber" or "von"
            or "per" or "su";


    private static bool IsWeakFocusedLookupContextPreposition(string token)
        => token is "a" or "au" or "aux" or "d" or "de" or "du" or "en" or "per" or "su";


    private static bool IsAudienceOnlyFocusedLookupContextPreposition(string token)
        => token is "avec" or "com" or "con" or "for" or "mit" or "para" or "pour" or "with";


    private static bool IsCurrentPassageScopedLookup(string normalizedSurface)
        => SafeRegexIsMatch(
            normalizedSurface,
            @"\b(?:dans|depuis|from|in)\s+(?:ce|cet|cette|this|the)\s+(?:passage|extrait|texte|text|snippet|section)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));


    private static string? NormalizeFocusedLookupPhraseForQuery(
        string candidate,
        string? sourceQuery,
        bool allowImplicitDocumentHintTrim = true)
    {
        var phrase = NormalizeFocusedLookupPhrase(candidate);
        if (string.IsNullOrWhiteSpace(phrase))
            return phrase;

        return allowImplicitDocumentHintTrim
               && TryTrimTrailingImplicitDocumentHint(phrase, sourceQuery, out var trimmed)
            ? trimmed
            : phrase;
    }


    internal static string? NormalizeFocusedLookupPhrase(string candidate)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidate)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToList();

        while (tokens.Count > 0 && IsFocusedLookupLeadingEdgeToken(tokens[0]))
            tokens.RemoveAt(0);
        while (tokens.Count > 1 && IsFocusedLookupLeadingDescriptorToken(tokens[0]))
            tokens.RemoveAt(0);
        while (tokens.Count > 0 && IsFocusedLookupLeadingEdgeToken(tokens[0]))
            tokens.RemoveAt(0);
        tokens = TrimFocusedLookupScaffoldPrefix(tokens);
        var alternativeIndex = tokens.FindIndex(static token => IsFocusedLookupAlternativeSeparatorToken(token));
        if (alternativeIndex > 0)
            tokens = tokens.Take(alternativeIndex).ToList();
        while (tokens.Count > 0 && IsFocusedLookupTrailingEdgeToken(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        if (tokens.Count == 0)
            return null;

        var signalCount = tokens.Count(IsFocusedLookupSignalToken);
        if (signalCount < 2
            && !tokens.Any(static token => token.Length >= 6 || token.Any(char.IsDigit))
            && !HasShortSuffixTitleShape(tokens))
        {
            return null;
        }

        var phrase = string.Join(' ', tokens);
        if (IsFocusedLookupMetaInstructionPhrase(phrase)
            && !HasExplicitShortIdentifierSuffix(candidate, tokens))
            return null;

        return phrase.Length is >= 4 and <= 80 ? phrase : null;
    }


    private static bool TryTrimTrailingImplicitDocumentHint(
        string phrase,
        string? sourceQuery,
        out string trimmed)
    {
        trimmed = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceQuery))
            return false;

        var tokens = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 3)
            return false;

        var hintTokens = ExtractImplicitDocumentHintTokens(sourceQuery);
        if (hintTokens.Count == 0 || !hintTokens.Contains(tokens[^1], StringComparer.Ordinal))
            return false;

        var head = tokens[..^1];
        if (head.Count(IsFocusedLookupSignalToken) < 2)
            return false;

        trimmed = string.Join(' ', head);
        return !string.IsNullOrWhiteSpace(trimmed)
            && !IsFocusedLookupMetaInstructionPhrase(trimmed);
    }


    private static List<string> TrimFocusedLookupScaffoldPrefix(List<string> tokens)
    {
        if (tokens.Count < 3)
            return tokens;

        var embeddedTrim = TryTrimEmbeddedFocusedLookupScaffold(tokens);
        if (embeddedTrim is not null)
            return embeddedTrim;

        if (!IsFocusedLookupScaffoldToken(tokens[0]))
            return tokens;

        var index = 0;
        var sawConnector = false;
        while (index < tokens.Count
               && (IsFocusedLookupScaffoldToken(tokens[index]) || IsFocusedLookupScaffoldConnectorToken(tokens[index])))
        {
            sawConnector |= IsFocusedLookupScaffoldConnectorToken(tokens[index]);
            index++;
        }

        if (index <= 0 || index >= tokens.Count || (!sawConnector && index < 2))
            return tokens;

        var tail = tokens.Skip(index).ToList();
        return tail.Count(IsFocusedLookupSignalToken) == 0 ? tokens : tail;
    }


    private static List<string>? TryTrimEmbeddedFocusedLookupScaffold(List<string> tokens)
    {
        var maxScaffoldIndex = Math.Min(tokens.Count - 2, 3);
        for (var scaffoldIndex = 0; scaffoldIndex <= maxScaffoldIndex; scaffoldIndex++)
        {
            if (!IsFocusedLookupScaffoldToken(tokens[scaffoldIndex]))
                continue;

            var maxConnectorIndex = Math.Min(tokens.Count - 2, scaffoldIndex + 4);
            for (var connectorIndex = scaffoldIndex + 1; connectorIndex <= maxConnectorIndex; connectorIndex++)
            {
                if (!IsFocusedLookupScaffoldConnectorToken(tokens[connectorIndex]))
                    continue;

                var tail = tokens.Skip(connectorIndex + 1).ToList();
                while (tail.Count > 0 && IsFocusedLookupLeadingEdgeToken(tail[0]))
                    tail.RemoveAt(0);

                if (tail.Count(IsFocusedLookupSignalToken) > 0)
                    return tail;
            }
        }

        return null;
    }


    private static bool IsFocusedLookupScaffoldToken(string token)
        => token is "base" or "basic" or "basique" or "basica" or "basico"
            or "basis" or "grundlage"
            || token.StartsWith("grund", StringComparison.Ordinal)
            || token.StartsWith("basis", StringComparison.Ordinal);


    private static bool IsFocusedLookupScaffoldConnectorToken(string token)
        => TitleConnectorTokens.Contains(token)
            || token is "of" or "for" or "fur" or "fuer" or "para" or "per" or "pour"
                or "de" or "di" or "da" or "do";


    private static bool IsFocusedLookupMetaInstructionPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalizedCore = NormalizeQuery(FoldDiacritics(phrase).ToLowerInvariant());
        var normalized = $" {normalizedCore} ";
        if (ContainsAny(
            normalized,
            " sans inventer ",
            " sans invention ",
            " without inventing ",
            " without invention ",
            " sin inventar ",
            " sin inventos ",
            " sem inventar ",
            " sem inventar nada ",
            " ohne zu erfinden ",
            " ohne erfindung ",
            " es anpasst ",
            " sie anpasst ",
            " anpasst ",
            " senza inventare ",
            " senza invenzione ",
            " how to adapt it ",
            " how to adapt them ",
            " como adaptarla ",
            " como adaptarlo ",
            " como adaptar ",
            " come adattarla ",
            " come adattarlo ",
            " wie anpassen ",
            " wie man es anpasst ",
            " anpassung "))
        {
            return true;
        }

        var tokens = normalizedCore.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;
        if (tokens[^1] is "un" or "une" or "des" or "du" or "de" or "the" or "a" or "an" or "some")
            return true;

        return tokens.All(static token => FocusedLookupMetaOnlyTokens.Contains(token));
    }


    private static readonly HashSet<string> FocusedLookupMetaOnlyTokens = new(StringComparer.Ordinal)
    {
        "a", "aller", "an", "aussi", "ca", "ce", "cela", "comment", "de", "demander", "des", "du",
        "ecrire", "elle", "elles", "expliquer", "faut", "grand", "grande", "grands", "grandes", "il", "ils",
        "lire", "lui", "leur", "pour", "resumer", "simplement", "some", "the", "un", "une"
    };


    private static bool IsFocusedLookupLeadingEdgeToken(string token)
        => TitleConnectorTokens.Contains(token)
           || FocusedLookupRequestLeadTokens.Contains(token)
           || LexicalStopwords.Contains(token)
           || SpecificAnchorStopwords.Contains(token)
           || PrimaryAnchorStopwords.Contains(token)
           || token is "l" or "s";


    private static bool IsFocusedLookupLeadingDescriptorToken(string token)
        => token is "grand" or "grande" or "grands" or "grandes"
            or "principal" or "principale" or "principaux" or "principales"
            or "etape" or "etapes"
            or "step" or "steps" or "main" or "major"
            or "procedure" or "procedures";


    private static bool IsFocusedLookupAlternativeSeparatorToken(string token)
        => token is "ou" or "or" or "oder" or "oppure";


    private static bool IsFocusedLookupTrailingEdgeToken(string token)
        => !IsShortTitleSuffixToken(token)
           && IsFocusedLookupLeadingEdgeToken(token);


    private static bool IsShortTitleSuffixToken(string token)
        => token.Length == 1 && token.All(char.IsLetterOrDigit);


    internal static bool IsFocusedLookupSignalToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !TitleConnectorTokens.Contains(token)
           && !FocusedLookupRequestLeadTokens.Contains(token)
           && !LexicalStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token);


    private static bool HasShortSuffixTitleShape(IReadOnlyList<string> tokens)
        => tokens.Count is >= 3 and <= 6
           && tokens.Take(tokens.Count - 1).Any(static token => TitleConnectorTokens.Contains(token))
           && tokens.Take(tokens.Count - 1).Any(IsFocusedLookupSignalToken)
           && tokens[^1].Length <= 3
           && tokens[^1].Any(char.IsLetterOrDigit)
           && !TitleConnectorTokens.Contains(tokens[^1])
           && !LexicalStopwords.Contains(tokens[^1])
           && !SpecificAnchorStopwords.Contains(tokens[^1])
           && !PrimaryAnchorStopwords.Contains(tokens[^1]);


    private static bool HasExplicitShortIdentifierSuffix(
        string candidate,
        IReadOnlyList<string> normalizedTokens)
    {
        if (normalizedTokens.Count is < 3 or > 8
            || normalizedTokens.Take(normalizedTokens.Count - 1).Count(IsFocusedLookupSignalToken) < 2)
        {
            return false;
        }

        var rawTokens = Regex.Matches(
                candidate,
                @"[\p{L}\p{Nd}]+",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
            .Select(static match => match.Value)
            .ToArray();
        if (rawTokens.Length == 0)
            return false;

        var suffix = rawTokens[^1];
        return suffix.Length is >= 1 and <= 3
            && suffix.Any(char.IsLetterOrDigit)
            && suffix.All(static value => !char.IsLetter(value) || char.IsUpper(value));
    }


    private const string FocusedLookupArticlePattern =
        @"(?:(?:de\s+la|de\s+l|les|des|the|some|une|un|du|le|la|l|an|a|das|der|die|den|dem|ein|eine|einen|einem|einer)\b|l['\u2019])\s+";


    private const string FocusedLookupTargetPatternText =
        @"(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)";


    private const string FocusedLookupTargetStopLookahead =
        @"(?=\s+(?:dans|depuis|from|in|aus|im|von|vom|source|sources|fonte|fontes|fuente|fuentes|quelle|quellen|materiel|mat[eéèêë]riel|materials?|component|components|composants?|etape|etapes|[eéèêë]tapes|step|steps|schritt|schritte|passo|passos|temps|time|duree|dur[eéèêë]e|duration|duracion|duracao|dauer|pdf|document|documents|doc|docs|fichier|fichiers|arquivo|arquivos|archivo|archivos|file|files|livre|livres|book|books|libro|libros|manuale|manuel|manuels|manual|manuals|guide|guides|guia|guias|handbuch|handbucher|version|reglage|reglages|r[eéèêë]glages|setting|settings|parametre|parametres|param[eéèêë]tres|avec|with|com|con|mit|senza|sans|without|compare|comparer|compara|comparar|vergleiche|si|oui|ja)\b|\s*[\?:;,\.\r\n]|$)";


    private static readonly Regex StructuredCardFocusedLookupTargetPattern = new(
        @"\b(?:fiche|ficha|scheda|karte|card|procedure|procedures|procedimiento|procedimientos|procedimento|procedimentos|section|topic|sujet|process|processus|processo)\b(?:\s+(?:claire|clair|clear|detaillee|detailee|d[eéèêë]taill[eéèêë]e|detailed|simple|complete|completa|completo|klar)){0,3}\s+(?:de|du|des|d['\u2019]?|pour|sur|of|for|about|on|para|sobre|por|per|su|di|del|della|do|da|dos|das|em|zu|zum|zur|uber|ueber)\s+" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex FocusedLookupTargetPattern = new(
        @"\b(?:(?:fiche|ficha|scheda|karte|card|procedure|procedures|procedimiento|procedimientos|procedimento|procedimentos|process|processus|processo|processi|section|seccion|secao|sezione|abschnitt|chapitre|chapter|capitulo|capitolo|kapitel|topic|sujet|subject|tema|assunto|argomento)\b(?:\s+(?:claire|clair|clear|detaillee|detailee|detailed|simple|complete|completa|completo|klar)){0,3}|(?:donne|donner|montre|trouve|chercher|cherche|veux|souhaite|give|show|find|get|want|need|dame|muestra|mostrar|encuentra|encontrar|quero|procura|procurar|mostra|trova|cerca|voglio|zeige|finde|finden|suche|such|mochte|will|brauche)\b(?:\s+[\p{L}\p{Nd}']{1,24}){0,8}?)\s+(?:de|du|des|d['’]?|pour|sur|of|for|about|on|para|sobre|por|per|su|di|del|della|do|da|dos|das|em|zu|zum|zur|uber|ueber)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s+(?:dans|depuis|from|in|aus|im|von|vom|source|sources|fonte|fontes|fuente|fuentes|quelle|quellen|etape|etapes|step|steps|schritt|schritte|passo|passos|temps|time|duree|duration|duracion|duracao|dauer|pdf|document|documents|doc|docs|fichier|fichiers|arquivo|arquivos|archivo|archivos|file|files|livre|livres|book|books|libro|libros|manuale|manuel|manuels|manual|manuals|guide|guides|guia|guias|handbuch|handbucher|avec|with|com|con|mit|senza|sans|without|compare|comparer|compara|comparar|vergleiche|si|oui|ja)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex DirectObjectFocusedLookupTargetPattern = new(
        @"\b(?:il\s+me\s+faut|il\s+nous\s+faut|je\s+veux|j['\u2019]ai\s+besoin\s+de|donne\s+moi|donnez\s+moi|montre\s+moi|montrez\s+moi|trouve\s+moi|trouvez\s+moi|give\s+me|show\s+me|get\s+me|find\s+me|i\s+need|we\s+need|quiero|quero|voglio|ich\s+brauche)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex AvailabilityFocusedLookupTargetPattern = new(
        @"\b(?:tu\s+as|as\s+tu|avez\s+vous|vous\s+avez|est\s+ce\s+que\s+tu\s+as|est\s+ce\s+que\s+vous\s+avez|do\s+you\s+have|have\s+you\s+got|have\s+you|hay|tienes|tem|tens|hai|hast\s+du|haben\s+sie|gibt\s+es)\s+(?:" + FocusedLookupArticlePattern + @")?(?:[\p{L}\p{Nd}]{3,24}\s+(?:de|du|des|d['\u2019]|of|for)\s+)?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex SourceAttributionFocusedLookupTargetPattern = new(
        @"\b(?:est\s+ce\s+que|is|does|viene|vem|stammt)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:vient|provient|come|comes|viene|vem|stammt)\s+(?:de|du|des|d['\u2019]|from|di|da|do|de|aus|von)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ProvenanceFocusedLookupTargetPattern = new(
        @"\b(?:d\s+ou\s+(?:vient|provient|sort|est\s+issu|est\s+issue)|source\s+(?:de|du|des|d['\u2019]|for|of)|origine\s+(?:de|du|des|d['\u2019]|for|of)|where\s+(?:does|is)\s+[\p{L}\p{Nd}\s'’\-/]{0,40}?\s+(?:come|from))\s+(?:" + FocusedLookupArticlePattern + @")?(?:[\p{L}\p{Nd}]{3,24}\s+(?:de|du|des|d['\u2019]|of)\s+)?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex SearchIntentFocusedLookupTargetPattern = new(
        @"\b(?:je\s+cherche|je\s+recherche|cherche|chercher|recherche|rechercher|trouve|trouver|i\s+(?:am\s+)?(?:looking\s+for|searching\s+for|seeking)|find|get|busco|busca|buscar|procuro|procura|procurar|cerco|cerca|cercare|ich\s+suche|suche|such|finde|finden)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex AssociatedTopicFocusedLookupTargetPattern = new(
        @"\b(?:associe(?:s|es)?\s+a|associe(?:s|es)?\s+au|associe(?:s|es)?\s+aux|associated\s+with|related\s+to|en\s+lien\s+avec|lie(?:s|es)?\s+a|lie(?:s|es)?\s+au|lie(?:s|es)?\s+aux)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex MentionFocusedLookupTargetPattern = new(
        @"\b(?:qui|that|which|que|che|welche|welcher|welches|die|der|das)\s+(?:parle|parlent|mentionne|mentionnent|contient|contiennent|cite|citent|covers?|mentions?|contains?|trata|tratan|tratam|menciona|mencionan|mencionam|contem|contiene|contengono|menziona|menzionano|enthalt|enthaelt|erwahnt|erwaehnt)\s+(?:de|du|des|d['\u2019]|about|of|sobre|de|do|da|di|von|uber|ueber)?\s*(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex TrailingMentionFocusedLookupTargetPattern = new(
        @"\b(?:welche|welcher|welches|die|der|das)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"\s+(?:erwahnt|erwaehnt|erwahnen|erwaehnen|enthalt|enthaelt|enthalten)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex PredicateObjectFocusedLookupTargetPattern = new(
        @"\b(?:quels?|quelle|quelles|which|what|cuales?|quais|quali|welche|welcher|welches)\s+[\p{L}\p{Nd}\s\-]{0,64}?\b(?:utilise|utilisent|emploie|emploient|uses?|using|contient|contiennent|contains?|mentionne|mentionnent|mentions?|cite|citent)\s+(?:de|du|des|d['\u2019]|of|about|with|avec|con|com|mit|di|da|do|de|von)?\s*(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex PoliteActionFocusedLookupTargetPattern = new(
        @"\b(?:tu\s+peux|peux\s+tu|pouvez\s+vous|vous\s+pouvez|can\s+you|could\s+you|puedes|podes|pode|puoi|potresti|kannst\s+du|konntest\s+du)\s+(?:me|moi|nous|us|me|mir|uns)?\s*(?:sortir|sors|donner|donne|donnez|montrer|montre|montrez|trouver|trouve|trouvez|chercher|cherche|ouvrir|ouvre|ouvrez|afficher|affiche|give|show|get|find|search|open|display|pull\s+up|mostrar|muestra|encontrar|encuentra|abrir|abre|dar|da|procurar|procura|mostra|trova|cerca|aprire|apri|zeigen|zeige|finden|finde|suchen|suche|offnen|offne)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex LeadingContextFocusedLookupTargetPattern = new(
        @"^(?!" +
        @"(?:compare|comparer|comparez|comparison|comparaison|liste|lister|list|quels|quelles|which|what|comment|how|combien|how\s+many|pourquoi|why)\b" +
        @")(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,50}?)(?=\s+(?:pour|for|para|per|fur|fuer|zu|zum|zur)\s+(?:un|une|des|le|la|les|l|the|a|an|some|mon|ma|mes|notre|nos|my|our|mi|mis|meu|minha|mein|meine)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex PurposeScopedFocusedLookupTargetPattern = new(
        @"^(?:pour|for|para|per|fur|fuer|zu|zum|zur)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex[] ComparativeLookupTargetPatterns =
    [
        new(
            @"\b(?:compare|comparer|comparez|comparison|comparaison|comparar|compara|confronta|vergleiche|vergleichen)\s+(?:(?:les|des|deux|plusieurs|the|some|multiple|several)\s+){0,3}(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s+(?:du|de|des|dans|depuis|from|in|of|del|do|dos|delle|aus)\s+(?:corpus|base|documents?|docs?|pdfs?|fichiers?|files?|category|categorie|categoria)|\s*[:\?;\.,\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:compare|comparer|comparez|comparison|comparaison|comparar|compara|confronta|vergleiche|vergleichen)\s+(?:(?:les|des|deux|plusieurs|the|some|multiple|several)\s+){0,3}(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s+(?:et|and|y|e|und)\s+(?:dis|dites|tell|indique|explique|explain|show|montre)\b)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:choisir|choisis|choisissez|choose|select|escoge|escolher|scegliere)\s+(?:entre|between|tra|zwischen)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s,\-]{2,120}?)(?=\s+(?:et|and|e|und)?\s*(?:justifie|justify|explique|explain)\b|[\?:;\.\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:plusieurs|multiple|several|varias|varios|diverses|diversi|mehrere)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s*[\?;,\.\r\n]\s*(?:compare|comparer|comparez|comparison|comparaison|comparar|compara|confronta|vergleiche|vergleichen)\b)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:deux|two)\s+(?:documents?|docs?|pdfs?|fichiers?|files?)\b.{0,120}?\b(?:sur|about|on)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,140}?)(?=\s*,?\s+(?:comment|how|explique|explain|indique|show|montre)\b|[\?:;\.\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100))
    ];


    private static readonly Regex AlternativeFocusedLookupTargetPattern = new(
        @"\b(?:je\s+cherche|je\s+recherche|cherche|chercher|recherche|rechercher|trouve|trouver|i\s+(?:am\s+)?(?:looking\s+for|need|want)|find|get|want|need|busco|busca|buscar|quero|procuro|procurar|cerco|cerca|cercare|voglio|suche|such|finde|finden|will|brauche)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:ou|or|oder|oppure)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ActionQuestionFocusedLookupTargetPattern = new(
        @"\b(?:comment|how|como|come|wie)\s+[\p{L}]{3,24}\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ShortSuffixDefinitionFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+que|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?|what\s+is|what\s+are|que\s+es|o\s+que\s+e|che\s+cos\s+e|was\s+ist)\s+(?:" + FocusedLookupArticlePattern + @")?(?<target>[\p{L}\p{Nd}]{3,40}\s+(?:a|de|du|d|of|di|da|do|von)\s+[\p{L}\p{Nd}]{1,24})(?=\s*[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex DefinitionThenActionFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+que|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?|what\s+is|what\s+are|que\s+es|o\s+que\s+e|che\s+cos\s+e|was\s+ist)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:et|and|y|e|und)\s+(?:comment|how|como|come|wie|lequel|laquelle|which)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex DefinitionFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+que|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?|quel\s+est|quelle\s+est|quels\s+sont|quelles\s+sont|what\s+is|what\s+are|que\s+es|o\s+que\s+e|che\s+cos\s+e|was\s+ist)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ExplainFocusedLookupTargetPattern = new(
        @"\b(?:explique(?:r|z)?|detaille|details?|resume(?:r)?|resumes?|summarize|explain|describe|decris|descris)\s*(?:moi)?\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex QuantityActionFocusedLookupTargetPattern = new(
        @"\b(?:combien\s+(?:d|de)\s+[\p{L}][\p{L}\p{Mn}\-]{2,24}|how\s+many\s+[\p{L}][\p{L}\p{Mn}\-]{2,24}|cuantos?\s+[\p{L}][\p{L}\p{Mn}\-]{2,24}|quantos?\s+[\p{L}][\p{L}\p{Mn}\-]{2,24}|quanti\s+[\p{L}][\p{L}\p{Mn}\-]{2,24}|wie\s+viele\s+[\p{L}][\p{L}\p{Mn}\-]{2,24})\b[\p{L}\p{Nd}\s'’\-/]{0,80}?\b(?:faire|preparer|fabriquer|produire|make|prepare|build|create|hacer|preparar|fazer|fare|preparare|machen|zubereiten)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:pour|for|para|per|fur|fuer)\s+(?:\d+|[a-z]+)\b|\s+(?:dans|depuis|from|in|aus|im|von|vom|source|sources|fonte|fontes|fuente|fuentes|quelle|quellen|etape|etapes|step|steps|schritt|schritte|passo|passos|temps|time|duree|duration|duracion|duracao|dauer|pdf|document|documents|doc|docs|fichier|fichiers|arquivo|arquivos|archivo|archivos|file|files|livre|livres|book|books|libro|libros|manuale|manuel|manuels|manual|manuals|guide|guides|guia|guias|handbuch|handbucher|version|reglage|reglages|setting|settings|parametre|parametres|avec|with|com|con|mit|senza|sans|without|compare|comparer|compara|comparar|vergleiche|si|oui|ja)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex ParameterFocusedLookupTargetPattern = new(
        @"\b(?:combien\s+de\s+temps|quantites?|quantities?|amounts?|etapes?|temps|reglages?|parametres?|vitesses?|temperatures?|how\s+long|which\s+settings|settings?|parameters?)\b[\p{L}\p{Nd}\s'’\-/]{0,90}?\s+(?:pour|for|de|du|des|d['\u2019]|sur|about|on|para|sobre|per|su)\s+(?:(?:\d+|[a-z]+)\s+[\p{L}]{2,24}\s+(?:de|d['\u2019]|of)\s+)?(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex StructuredInfoFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?\s+faut|what\s+(?:is|are)|which\s+(?:is|are))\b[\p{L}\p{Nd}\s'’\-/]{0,90}?\s+(?:pour|for|de|du|des|d['\u2019]|sur|about|on|of)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static readonly Regex[] FocusedLookupTargetPatterns =
    [
        SourceAttributionFocusedLookupTargetPattern,
        ProvenanceFocusedLookupTargetPattern,
        AvailabilityFocusedLookupTargetPattern,
        MentionFocusedLookupTargetPattern,
        TrailingMentionFocusedLookupTargetPattern,
        AssociatedTopicFocusedLookupTargetPattern,
        PredicateObjectFocusedLookupTargetPattern,
        SearchIntentFocusedLookupTargetPattern,
        PoliteActionFocusedLookupTargetPattern,
        AlternativeFocusedLookupTargetPattern,
        PurposeScopedFocusedLookupTargetPattern,
        LeadingContextFocusedLookupTargetPattern,
        StructuredCardFocusedLookupTargetPattern,
        FocusedLookupTargetPattern,
        DirectObjectFocusedLookupTargetPattern,
        ActionQuestionFocusedLookupTargetPattern,
        ShortSuffixDefinitionFocusedLookupTargetPattern,
        DefinitionThenActionFocusedLookupTargetPattern,
        DefinitionFocusedLookupTargetPattern,
        ExplainFocusedLookupTargetPattern,
        QuantityActionFocusedLookupTargetPattern,
        ParameterFocusedLookupTargetPattern,
        StructuredInfoFocusedLookupTargetPattern
    ];


    private static bool PhraseOccursInQuery(string phrase, string lookup, string foldedLookup)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        return lookup.Contains(phrase, StringComparison.Ordinal)
            || foldedLookup.Contains(FoldDiacritics(phrase), StringComparison.Ordinal);
    }


    private static int ComputeFocusedBackfillTermScore(string term)
    {
        var parts = term
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .ToArray();
        if (parts.Length == 0)
            return 0;

        return (parts.Count(static token => token.Length >= 7) * 4)
            + Math.Min(3, parts.Length)
            + Math.Min(3, parts.Sum(static token => Math.Min(1, token.Count(char.IsDigit))));
    }


    internal static async Task<List<RagMatch>> FilterMatchesAgainstActiveDocumentVersionsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> rawMatches,
        CancellationToken ct,
        string? categoryPath = null)
    {
        if (rawMatches.Count == 0)
            return [];

        var docIds = rawMatches
            .Select(m => Guid.TryParse(m.DocId, out var docId) ? docId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (docIds.Length == 0)
            return [];

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT d.doc_id AS "DocId",
       d.doc_path AS "DocPath",
       d.status AS "Status",
       d.indexed_version AS "IndexedVersion",
       r.ingestion_version AS "CurrentRevisionIngestionVersion",
       LOWER(ENCODE(d.content_hash, 'hex')) AS "ContentHashHex",
       d.ingestion_vector_plan_fingerprint AS "VectorPlanFingerprint"
FROM documents d
LEFT JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
WHERE d.tenant_id=@tenant_id
  AND d.doc_id = ANY(@doc_ids);
""";

        var rows = await conn.QueryAsync<DocVersionRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = docIds
        }, cancellationToken: ct));

        var active = rows.ToDictionary(x => x.DocId, x => x);
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        var filtered = new List<RagMatch>(rawMatches.Count);
        foreach (var match in rawMatches)
        {
            if (!Guid.TryParse(match.DocId, out var docIdValue))
                continue;
            if (!active.TryGetValue(docIdValue, out var row))
                continue;
            if (!string.Equals(row.Status, "indexed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.IndexedVersion <= 0)
                continue;
            if (!RagDocPathMatchesCategoryPath(row.DocPath, normalizedCategoryPath))
                continue;

            if (!IsActiveDenseMatchForRevision(match, row.CurrentRevisionIngestionVersion, row.ContentHashHex, row.VectorPlanFingerprint))
                continue;

            filtered.Add(match);
        }

        return filtered;
    }


    internal static async Task<List<RagMatch>> AttachDocumentProfileContentCardsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        string query,
        CancellationToken ct,
        int perMatchLimit = 12)
    {
        if (matches.Count == 0 || perMatchLimit <= 0)
            return matches.ToList();

        var candidates = matches
            .Where(static match => match.MatchedContentCards is null || match.MatchedContentCards.Count == 0)
            .Select(static match => Guid.TryParse(match.DocId, out var docId) ? docId : Guid.Empty)
            .Where(static docId => docId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (candidates.Length == 0)
            return matches.ToList();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    d.doc_id AS "DocId",
    jsonb_build_object(
        'contentCards',
        COALESCE(
            jsonb_agg(
                jsonb_build_object(
                    'title', pcc.title,
                    'contentCardId', pcc.content_card_id,
                    'pageStart', pcc.page_start,
                    'pageEnd', pcc.page_end,
                    'kind', pcc.kind,
                    'signals', pcc.signals,
                    'evidence', pcc.metadata->'evidence')
                ORDER BY
                    CASE pcc.profile_version
                        WHEN 'llm_backoffice_v1' THEN 0
                        WHEN 'foundation_v1' THEN 1
                        WHEN 'deterministic_v1' THEN 2
                        ELSE 3
                    END,
                    pcc.card_index)
            FILTER (WHERE pcc.content_card_id IS NOT NULL),
            '[]'::jsonb))::text AS "ContentCardsJson"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
LEFT JOIN LATERAL (
    SELECT raw_card.*
    FROM document_profile_content_cards raw_card
    WHERE raw_card.tenant_id = d.tenant_id
      AND raw_card.revision_id = r.revision_id
      AND NULLIF(BTRIM(raw_card.title), '') IS NOT NULL
      AND saaia_is_safe_profile_content_card(raw_card.kind, raw_card.title, raw_card.page_start, raw_card.page_end, raw_card.metadata)
    ORDER BY
        CASE raw_card.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'foundation_v1' THEN 1
            WHEN 'deterministic_v1' THEN 2
            ELSE 3
        END,
        raw_card.card_index
    LIMIT 160
) pcc ON true
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND d.doc_id = ANY(@doc_ids)
GROUP BY d.doc_id;
""";

        var rows = await conn.QueryAsync<DocProfileContentCardsRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = candidates
        }, cancellationToken: ct));
        var byDocId = rows.ToDictionary(static row => row.DocId, static row => row.ContentCardsJson);
        if (byDocId.Count == 0)
            return matches.ToList();

        var parsedContentCardsByDocId = new Dictionary<Guid, IReadOnlyList<DocumentProfileContentCard>>();
        return matches
            .Select(match =>
            {
                if (match.MatchedContentCards is { Count: > 0 }
                    || !Guid.TryParse(match.DocId, out var docId)
                    || !byDocId.TryGetValue(docId, out var metadataJson))
                {
                    return match;
                }

                if (!parsedContentCardsByDocId.TryGetValue(docId, out var contentCards))
                {
                    contentCards = DocumentProfileProjector.ParseContentCards(metadataJson);
                    parsedContentCardsByDocId[docId] = contentCards;
                }

                var selectionQuery = CollapseWhitespace(string.Join(' ', new[]
                {
                    query,
                    match.SectionTitle,
                    match.HeadingPath,
                    match.Text
                }));
                var cards = BuildMatchedContentCardsFromParsed(contentCards, selectionQuery, perMatchLimit, match.PageStart, match.PageEnd);
                if (cards.Count > 0)
                    return match with { MatchedContentCards = cards };

                var routeTitleCards = BuildRouteTitleFallbackMatchedContentCards(match);
                if (routeTitleCards is not null)
                    return match with { MatchedContentCards = routeTitleCards };

                var chunkSectionTitleCards = BuildChunkSectionTitleFallbackMatchedContentCards(match);
                return chunkSectionTitleCards is null
                    ? match
                    : match with { MatchedContentCards = chunkSectionTitleCards };
            })
            .ToList();
    }


    internal static IReadOnlyList<RagMatchedContentCard>? BuildChunkSectionTitleFallbackMatchedContentCards(
        RagMatch match)
    {
        if (IsDocumentProfileMatch(match)
            || LooksLikeNavigationalChunk(match)
            || match.PageStart is not > 0
            || string.IsNullOrWhiteSpace(match.SectionTitle)
            || string.IsNullOrWhiteSpace(match.Text))
        {
            return null;
        }

        var title = CollapseWhitespace(match.SectionTitle);
        if (!LooksLikeConcreteMatchedContentCardTitle(title))
            return null;

        var sourceText = CollapseWhitespace(match.Text);
        if (sourceText.Length < 40)
            return null;
        if (sourceText.Length > 1600)
            sourceText = sourceText[..1600].TrimEnd() + "...";

        // This is a mechanical same-chunk projection only. It makes an already
        // extracted title, source span, document and page available to the
        // downstream LLM; it does not approve the title as a usable answer item.
        var sourceCardId = IdUtil.DeterministicGuid(
            string.Join(
                '|',
                "retrieval-chunk-content-card-v1",
                match.DocId,
                match.DocPath,
                match.PageStart,
                match.PageEnd,
                title));
        var evidence = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "content_card_evidence_v1",
            sourceText
        });
        return
        [
            new RagMatchedContentCard(
                Title: title,
                ContentCardId: sourceCardId.ToString(),
                PageStart: match.PageStart,
                PageEnd: match.PageEnd,
                Kind: "chunk_section_title",
                Signals: ["chunk_section_title", "same_chunk_source_text"],
                Evidence: evidence)
        ];
    }


    internal static IReadOnlyList<RagMatchedContentCard>? BuildRouteTitleFallbackMatchedContentCards(RagMatch match)
    {
        if (!IsResolvedTitleOrNavigationRoute(match)
            || LooksLikeNavigationalChunk(match)
            || !HasUsefulResolvedTitleBody(match))
        {
            return null;
        }

        var title = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(title)
            || !LooksLikeConcreteMatchedContentCardTitle(title))
        {
            return null;
        }

        return
        [
            new RagMatchedContentCard(
                title.Trim(),
                PageStart: match.PageStart,
                PageEnd: match.PageEnd,
                Kind: ResolveRetriever(match),
                Signals: ["route_title"])
        ];
    }


    internal static bool IsActiveDenseMatchForRevision(
        RagMatch match,
        int? currentRevisionIngestionVersion,
        string? currentContentHashHex,
        string? currentVectorPlanFingerprint = null)
    {
        // NULL is the legacy generation, not a wildcard for a plan-specific point.
        if (!string.Equals(match.IngestionPlanFingerprint, currentVectorPlanFingerprint, StringComparison.Ordinal))
            return false;
        var matchHash = NormalizeHashHex(match.HashDoc);
        var currentHash = NormalizeHashHex(currentContentHashHex);
        var hashCanBeChecked = !string.IsNullOrWhiteSpace(matchHash) && !string.IsNullOrWhiteSpace(currentHash);
        var hashMatches = hashCanBeChecked
            && string.Equals(matchHash, currentHash, StringComparison.OrdinalIgnoreCase);

        if (match.IngestionVersion.HasValue)
        {
            if (!currentRevisionIngestionVersion.HasValue)
                return false;
            if (match.IngestionVersion.Value != currentRevisionIngestionVersion.Value)
                return false;

            return !hashCanBeChecked || hashMatches;
        }

        return hashMatches;
    }


    internal static string? NormalizeHashHex(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }


    internal static bool TitleRouteLeadingContinuationNeedsPreviousCompanion(string query, RagMatch match)
    {
        if (!IsResolvedTitleOrNavigationRoute(match))
            return false;
        if (string.IsNullOrWhiteSpace(match.Text))
            return false;
        if (string.IsNullOrWhiteSpace(match.PrevChunkId) && !match.ChunkIndex.HasValue)
            return false;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
        {
            routeTitle = ExtractFocusedLookupPhrases(query)
                .Concat(ExtractQuotedLookupPhrases(query))
                .Append(BuildFocusedLexicalBackfillQuery(query))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        }

        var normalizedTitle = NormalizeForLexicalSignal(routeTitle ?? string.Empty);
        if (normalizedTitle.Length < 5)
            return false;

        var normalizedText = NormalizeForLexicalSignal(match.Text);
        var firstTitle = normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (firstTitle < 0 || firstTitle > 80)
            return false;

        var afterFirstTitle = firstTitle + normalizedTitle.Length;
        var secondTitle = normalizedText.IndexOf(normalizedTitle, afterFirstTitle, StringComparison.Ordinal);
        if (secondTitle <= afterFirstTitle || secondTitle - afterFirstTitle > 420)
            return false;

        var leadingBody = normalizedText[afterFirstTitle..secondTitle];
        if (!Regex.IsMatch(
                leadingBody,
                @"\b(?:ajoutez|appliquez|configurez|connectez|continuez|fermez|installez|lancez|ouvrez|relancez|remplacez|retirez|selectionnez|validez|verifiez)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        var leadingInventoryCueCount = Regex.Matches(
            leadingBody,
            @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|c)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        if (leadingInventoryCueCount >= 2)
            return false;

        return !StructuredContentLexicon.ContainsItemizedCue(leadingBody);
    }


    internal static bool LinkedContextLeadingContinuationNeedsPreviousCompanion(string query, RagMatch match)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(match.PrevChunkId) || string.IsNullOrWhiteSpace(match.Text))
            return false;
        if (!HasStructuredContinuationBackfillIntent(query))
            return false;

        var lead = NormalizeForLexicalSignal(match.Text);
        if (lead.Length > 260)
            lead = lead[..260];

        return Regex.IsMatch(
            lead,
            @"^\s*(?:procedez\s+de\s+la\s+meme\s+facon|de\s+la\s+meme\s+facon|continuer?|continuez|repetez|repeter|[2-9]\d?\s*[\.\)]|ajoutez|appliquez|configurez|connectez|fermez|installez|ouvrez|remplacez|retirez|selectionnez|validez|verifiez)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    internal static bool IsLinkedContextLeadingProcedureContinuation(string query, RagMatch match)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(match.Text))
            return false;

        var lead = NormalizeForLexicalSignal(match.Text);
        if (lead.Length == 0)
            return false;

        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        void AddTitleCandidate(string? value)
        {
            var normalized = NormalizeForLexicalSignal(value);
            if (normalized.Length >= 5)
                seenTitles.Add(normalized);
        }

        AddTitleCandidate(ExtractMatchedRouteOrProfileTitle(match.EmbedText));
        AddTitleCandidate(match.SectionTitle);
        AddTitleCandidate(match.HeadingPath);
        foreach (var phrase in ExtractFocusedLookupPhrases(query))
            AddTitleCandidate(phrase);
        foreach (var phrase in ExtractQuotedLookupPhrases(query))
            AddTitleCandidate(phrase);
        AddTitleCandidate(BuildFocusedLexicalBackfillQuery(query));

        foreach (var normalizedTitle in seenTitles.OrderByDescending(static title => title.Length))
        {
            var titleIndex = lead.IndexOf(normalizedTitle, StringComparison.Ordinal);
            if (titleIndex < 0 || titleIndex > 80)
                continue;

            lead = lead[(titleIndex + normalizedTitle.Length)..].TrimStart();
            break;
        }

        if (lead.Length > 260)
            lead = lead[..260];

        return Regex.IsMatch(
            lead,
            @"^\s*(?:(?:[2-9]\d?\s*(?:[\.\)]|\b))|au\s+bout|a\s+la\s+fin|puis|ensuite|continuer?|continuez|relancez|ajoutez|appliquez|configurez|connectez|fermez|installez|ouvrez|remplacez|retirez|selectionnez|validez|verifiez)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    internal static int ComputeStructuredContinuationAnchorTitleSignal(
        string query,
        IReadOnlyList<string> titleTokens,
        RagMatch match)
    {
        var signal = 0;
        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count > 0 && HasQuotedTitlePlacementEvidence(quotedPhrases, match))
            signal += 4;
        if (ComputeExactTitleCandidateScore(query, match) > 0.0)
            signal += 4;
        if (ComputeQuotedTitleAnchorSignal(query, match) > 0.0)
            signal += 4;
        if (ComputeDirectChunkTitleSignal(query, match) > 0)
            signal += 3;
        if (DirectTitleRouteBridgeNeedsPreviousCompanion(query, match))
            signal += 3;
        if (titleTokens.Count > 0 && HasFullTitleAnchorCoverage(titleTokens, match))
            signal += 3;
        if (IsResolvedTitleOrNavigationRoute(match) && match.Score >= 0.80)
            signal += 2;
        if (HasProfileTitleHint(match))
            signal += 2;
        if (match.MatchedContentCards is { Count: > 0 })
        {
            var lexicalTokens = ExtractLexicalQueryTokens(query)
                .Where(static token => !PrimaryAnchorStopwords.Contains(token))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            signal += Math.Min(2, ComputeMatchedContentCardTitleSignal(
                match,
                lexicalTokens,
                NormalizeForLexicalSignal(query)));
        }

        return signal;
    }


    internal static bool IsUsefulStructuredContinuationCandidate(
        string query,
        RagMatch anchor,
        RagMatch candidate,
        string relation)
    {
        if (!string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(ResolveRetriever(candidate), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(candidate) || LooksLikeGlossaryChunk(candidate))
            return false;
        if (!IsContentSelectionCandidate(candidate))
            return false;
        if (!IsNearbyPage(anchor, candidate, maxDistance: 2))
            return false;

        var sharesSection = SharesSpecificSection(anchor, candidate);
        var samePage = anchor.PageStart.HasValue
            && candidate.PageStart.HasValue
            && anchor.PageStart.Value == candidate.PageStart.Value;
        var directTitleBridgeNeedsPrevious = DirectTitleRouteBridgeNeedsPreviousCompanion(query, anchor);
        var titleRouteLeadingContinuationNeedsPrevious = TitleRouteLeadingContinuationNeedsPreviousCompanion(query, anchor);
        var linkedContextLeadingContinuationNeedsPrevious =
            LinkedContextLeadingContinuationNeedsPreviousCompanion(query, anchor);
        var needsPreviousCompanion =
            directTitleBridgeNeedsPrevious
            || titleRouteLeadingContinuationNeedsPrevious
            || linkedContextLeadingContinuationNeedsPrevious;
        if (HasConflictingSpecificSectionTitle(anchor, candidate))
            return false;
        if (string.Equals(relation, "prev", StringComparison.Ordinal))
        {
            if (!sharesSection && !(needsPreviousCompanion && samePage))
                return false;
            if (IsResolvedTitleOrNavigationRoute(anchor) && !needsPreviousCompanion)
                return false;
        }

        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        var asksInventory = ContainsStructuredInventoryRequest(normalizedQuery);
        var asksProcedure = ContainsStructuredProcedureRequest(normalizedQuery);
        var asksTime = ContainsStructuredTimeRequest(normalizedQuery);
        var asksSheet = ContainsStructuredSheetRequest(normalizedQuery);

        var anchorHasInventory = ContainsStructuredInventoryCue(anchor);
        var anchorHasProcedure = ContainsStructuredProcedureCue(anchor);
        var anchorHasTime = ContainsStructuredTimeCue(anchor);
        var candidateHasInventory = ContainsStructuredInventoryCue(candidate);
        var candidateHasProcedure = ContainsStructuredProcedureCue(candidate);
        var candidateHasTime = ContainsStructuredTimeCue(candidate);

        if (asksProcedure && !anchorHasProcedure && candidateHasProcedure)
            return true;
        if (asksInventory && !anchorHasInventory && candidateHasInventory)
            return true;
        if (asksTime && !anchorHasTime && candidateHasTime)
            return true;
        var isPreviousSamePageBridgeCandidate = string.Equals(relation, "same_page", StringComparison.Ordinal)
            && anchor.ChunkIndex.HasValue
            && candidate.ChunkIndex.HasValue
            && candidate.ChunkIndex.Value < anchor.ChunkIndex.Value;
        if (needsPreviousCompanion
            && asksSheet
            && (string.Equals(relation, "prev", StringComparison.Ordinal) || isPreviousSamePageBridgeCandidate)
            && (candidateHasInventory
                || candidateHasProcedure
                || candidateHasTime
                || ComputeContentEvidencePriority(candidate) >= 0.65))
        {
            return true;
        }
        var anchorIsHighConfidenceTitleOnly =
            IsHighConfidenceTitleOnlyStructuredContinuationAnchor(query, anchor);
        if (asksSheet
            && (IsResolvedTitleOrNavigationRoute(anchor) || anchorIsHighConfidenceTitleOnly)
            && sharesSection
            && (string.Equals(relation, "same_section", StringComparison.Ordinal)
                || string.Equals(relation, "next", StringComparison.Ordinal)
                || string.Equals(relation, "same_page_forward", StringComparison.Ordinal))
            && ComputeContentEvidencePriority(candidate) >= 0.65)
        {
            return true;
        }

        return asksSheet
            && sharesSection
            && ((anchorHasInventory && candidateHasProcedure)
                || (anchorHasProcedure && candidateHasInventory)
                || (!anchorHasTime && candidateHasTime));
    }


    private static bool IsHighConfidenceTitleOnlyStructuredContinuationAnchor(string query, RagMatch anchor)
    {
        if (GetStructuredAnswerPriority(anchor) >= 2)
            return false;
        if (ContainsStructuredInventoryCue(anchor) || ContainsStructuredProcedureCue(anchor))
            return false;
        if ((anchor.Text?.Length ?? 0) > 700)
            return false;

        var titleScoringQuery = BuildFocusedLexicalBackfillQuery(query);
        var titleTokens = ExtractTitleScoringTokens(
            string.IsNullOrWhiteSpace(titleScoringQuery) ? query : titleScoringQuery);
        return ComputeStructuredContinuationAnchorTitleSignal(query, titleTokens, anchor) >= 4;
    }


    internal static string? ResolveStructuredContinuationRelation(RagMatch anchor, RagMatch candidate)
    {
        if (SameChunkId(candidate.ChunkId, anchor.NextChunkId)
            || SameChunkId(candidate.PrevChunkId, anchor.ChunkId))
        {
            return "next";
        }

        if (SameChunkId(candidate.ChunkId, anchor.PrevChunkId)
            || SameChunkId(candidate.NextChunkId, anchor.ChunkId))
        {
            return "prev";
        }

        if (SameChunkId(candidate.ChunkId, anchor.SameSectionChunkId)
            || SameChunkId(candidate.SameSectionChunkId, anchor.ChunkId))
        {
            return "same_section";
        }

        if (string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase)
            && anchor.PageStart.HasValue
            && candidate.PageStart.HasValue
            && anchor.PageStart.Value == candidate.PageStart.Value
            && anchor.ChunkIndex.HasValue
            && candidate.ChunkIndex.HasValue
            && candidate.ChunkIndex.Value != anchor.ChunkIndex.Value
            && Math.Abs(candidate.ChunkIndex.Value - anchor.ChunkIndex.Value) <= 4)
        {
            return candidate.ChunkIndex.Value > anchor.ChunkIndex.Value
                ? "same_page_forward"
                : "same_page";
        }

        return null;
    }


    internal static int StructuredContinuationRelationPriority(string relation)
        => relation switch
        {
            "same_section" => 4,
            "next" => 3,
            "prev" => 2,
            "same_page_forward" => 2,
            "same_page" => 1,
            _ => 0
        };


    internal static int ComputeStructuredContinuationCuePriority(string query, RagMatch candidate)
    {
        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        var priority = 0;
        if (ContainsStructuredProcedureRequest(normalizedQuery) && ContainsStructuredProcedureCue(candidate))
            priority += 4;
        if (ContainsStructuredInventoryRequest(normalizedQuery) && ContainsStructuredInventoryCue(candidate))
            priority += 3;
        if (ContainsStructuredTimeRequest(normalizedQuery) && ContainsStructuredTimeCue(candidate))
            priority += 2;
        if (ComputeContentEvidencePriority(candidate) >= 0.80)
            priority += 1;
        return priority;
    }


    internal static bool ContainsStructuredInventoryRequest(string normalizedQuery)
        => StructuredContentLexicon.ContainsItemizedCue(normalizedQuery)
           || ContainsAny(
            normalizedQuery,
            " material ",
            " materials ",
            " materiel ",
            " materiaux ",
            " equipment ",
            " equipement ",
            " equipements ",
            " tool ",
            " tools ",
            " item ",
            " items ",
            " element ",
            " elements ",
            " requirement ",
            " requirements ",
            " supply ",
            " supplies ",
            " part ",
            " parts ",
            " componentes ",
            " componenti ",
            " components ",
            " composants ");


    internal static bool ContainsStructuredProcedureRequest(string normalizedQuery)
        => ContainsAny(
            normalizedQuery,
            " etape ",
            " etapes ",
            " preparation ",
            " preparacion ",
            " preparo ",
            " preparazione ",
            " zubereitung ",
            " procedure ",
            " procedures ",
            " procedimiento ",
            " procedimentos ",
            " steps ",
            " step ",
            " instructions ",
            " instruction ",
            " mode operatoire ");


    internal static bool ContainsStructuredTimeRequest(string normalizedQuery)
        => ContainsAny(
            normalizedQuery,
            " temps ",
            " duree ",
            " duration ",
            " time ",
            " dauer ",
            " duracion ",
            " duracao ");


    internal static bool ContainsStructuredSheetRequest(string normalizedQuery)
        => StructuredContentLexicon.ContainsStructuredItemTypeCue(normalizedQuery)
           || ContainsAny(
            normalizedQuery,
            " fiche ",
            " ficha ",
            " scheda ",
            " sheet ",
            " card ",
            " section ",
            " topic ",
            " overview ",
            " summary ",
            " procedure ",
            " process ",
            " clear sheet ",
            " fiche claire ");


    internal static bool ContainsStructuredInventoryCue(RagMatch match)
    {
        var normalized = NormalizeForLexicalSignal(GetDirectChunkSignalText(match));
        return StructuredContentLexicon.ContainsItemizedCue(normalized)
            || LooksLikeStructuredQuantityList(normalized)
            || LooksLikeDenseStructuredQuantityRun(normalized);
    }


    private static bool LooksLikeDenseStructuredQuantityRun(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeForLexicalSignal(value);
        var narrowQuantityCount = Regex.Matches(
            normalized,
            @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|units?|unites?|items?|elements?|entries?|parts?|pieces?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        if (narrowQuantityCount >= 2)
            return true;

        var numberedObjectCount = Regex.Matches(
            normalized,
            @"\b\d+(?:[,.]\d+)?\s+(?:\p{L}{1,14}\s+){0,2}\p{L}{3,}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        return narrowQuantityCount >= 1 && numberedObjectCount >= 3;
    }


    internal static bool ContainsStructuredProcedureCue(RagMatch match)
    {
        var normalized = NormalizeForLexicalSignal(GetDirectChunkSignalText(match));
        return normalized.Contains("preparation", StringComparison.Ordinal)
            || normalized.Contains("preparacion", StringComparison.Ordinal)
            || normalized.Contains("preparazione", StringComparison.Ordinal)
            || normalized.Contains("zubereitung", StringComparison.Ordinal)
            || normalized.Contains("procedure", StringComparison.Ordinal)
            || normalized.Contains("procedimiento", StringComparison.Ordinal)
            || normalized.Contains("procedimento", StringComparison.Ordinal)
            || normalized.Contains("instruction", StringComparison.Ordinal)
            || normalized.Contains("etape", StringComparison.Ordinal)
            || normalized.Contains("step", StringComparison.Ordinal)
            || normalized.Contains("technique", StringComparison.Ordinal)
            || LooksLikeNumberedProcedureStructure(normalized)
            || LooksLikeGenericProcedureActionRun(normalized)
            || Regex.IsMatch(
                normalized,
                @$"(?:^|\s)1\s+(?:\w+\s+){{0,18}}?{GenericProcedureActionVerbPattern}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    private const string GenericProcedureActionVerbPattern =
        @"\b(?:add|ajouter|ajoutez|apply|appliquer|appliquez|assemble|assembler|check|controlez|configure|configurer|configurez|connect|connecter|connectez|copy|copier|delete|demarrer|install|installer|installez|lancer|lancez|mettre|mettez|open|ouvrir|ouvrez|place|placer|placez|prepare|preparer|preparez|remove|remplacer|remplacez|replace|retirer|retirez|run|select|selectionner|selectionnez|set|start|stop|supprimer|update|use|utiliser|utilisez|validate|valider|validez|verify|verifier|verifiez)\b";


    private static bool LooksLikeGenericProcedureActionRun(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.Matches(
                normalized,
                GenericProcedureActionVerbPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count >= 2;
    }


    private static bool LooksLikeNumberedProcedureStructure(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"(?:^|\s)1\s+\p{L}.{0,360}(?:^|\s)2\s+\p{L}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"(?:^|\s)(?:pour|for|para|per|fur|fuer)\s+\d{1,3}\s+(?:\p{L}+\s+){0,24}?1\s+\p{L}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    internal static bool ContainsStructuredTimeCue(RagMatch match)
    {
        var normalized = NormalizeForLexicalSignal(GetDirectChunkSignalText(match));
        return Regex.IsMatch(
            normalized,
            @"\b\d{1,3}\s*(?:min|minute|minutes|h|heure|heures|hour|hours|stunden?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    private static bool HasConflictingSpecificSectionTitle(RagMatch left, RagMatch right)
    {
        var leftTitle = GetSpecificSectionTitle(left);
        var rightTitle = GetSpecificSectionTitle(right);
        if (string.IsNullOrWhiteSpace(leftTitle) || string.IsNullOrWhiteSpace(rightTitle))
            return false;

        return !string.Equals(
            NormalizeForLexicalSignal(leftTitle),
            NormalizeForLexicalSignal(rightTitle),
            StringComparison.Ordinal);
    }


    private static string? GetSpecificSectionTitle(RagMatch match)
    {
        foreach (var value in new[] { match.SectionTitle, match.HeadingPath })
        {
            if (!string.IsNullOrWhiteSpace(value) && !IsGenericSectionTitle(value))
                return value.Trim();
        }

        return null;
    }


    private static bool SameChunkId(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);


    internal static int CountPrimarySpecificSelectionAnchors(IReadOnlyList<string> primaryTokens, RagMatch match)
    {
        if (primaryTokens.Count == 0)
            return 0;

        var titleSignalText = GetPreciseTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return 0;

        return CountSpecificLexicalAnchors(primaryTokens, titleSignalText);
    }


    internal static void PruneUnpagedProfileSelectionsForPreciseLookup(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;
        if (ShouldPreferComparativeDocumentDiversity(query)
            && ShouldAllowDocumentProfileAssistForComparativeLookup(query))
        {
            return;
        }

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length is < 2 or > 6)
            return;

        var hasPagedTitleEvidence = selected.Any(match =>
            match.PageStart.HasValue
            && (ComputeExactTitleCandidateScore(query, match) > 0.0
                || ContainsOrderedTitleTokenSubstrings(GetTitleSignalText(match), lexicalTokens, maxGapChars: 60)));
        if (!hasPagedTitleEvidence)
            return;

        selected.RemoveAll(static match =>
            !match.PageStart.HasValue
            && string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal));
    }


    internal static bool IsUsefulLinkedSelectionNearPrimaryAnchor(
        IReadOnlyList<string> primaryTokens,
        RagMatch match,
        IReadOnlyList<RagMatch> primaryAnchors)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        return primaryAnchors.Any(anchor =>
            IsUsefulLinkedContextCompanion(primaryTokens, anchor, match));
    }


    internal static bool IsSameChunk(RagMatch left, RagMatch right)
        => string.Equals(left.ChunkId, right.ChunkId, StringComparison.Ordinal)
           && string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);


    internal static int CountTitleAnchorTokens(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 4
                    && normalized.Contains(foldedVariant, StringComparison.Ordinal))
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }


    internal static bool HasStrictTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetPreciseTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsTitleLikeLexicalSequence(normalized, lexicalTokens);
    }


    internal static string GetPreciseTitleSignalText(RagMatch match)
    {
        return string.Join("\n", new[]
        {
            ExtractMatchedRouteOrProfileTitle(match.EmbedText),
            match.MatchedContentCards is { Count: > 0 }
                ? string.Join("\n", match.MatchedContentCards.Take(MaxCalibrationCardCount).Select(static card => card.Title))
                : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }


    internal static string? ExtractMatchedRouteOrProfileTitle(string? embedText)
    {
        if (string.IsNullOrWhiteSpace(embedText))
            return null;

        string[] prefixes =
        [
            "Matched profile title:",
            "Matched quoted title:",
            "Matched title_anchor_exact_page_route:",
            "Matched title_anchor_route:",
            "Matched linked_anchor_title:",
            "Matched navigation_route:",
            "Matched direct_title_token_route:",
            "Matched fuzzy_title_lead:"
        ];
        var prefix = prefixes.FirstOrDefault(prefix => embedText.StartsWith(prefix, StringComparison.Ordinal));
        if (prefix is null)
            return null;

        var start = prefix.Length;
        var lineEnd = embedText.IndexOf('\n', start);
        if (lineEnd < 0)
        {
            var documentMarker = FindFirstMarker(embedText, start, [" Document:", " document_name:"]);
            lineEnd = documentMarker >= 0 ? documentMarker : embedText.Length;
        }

        return embedText[start..lineEnd].Trim();
    }


    private static int FindFirstMarker(string value, int startIndex, IReadOnlyList<string> markers)
    {
        var first = -1;
        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, startIndex, StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }

        return first;
    }


    internal static string[] ExtractTitlePruneTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => (token.Length >= 4 && token.Any(char.IsLetter)) || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    internal static IReadOnlyList<string> ExtractTitleScoringTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var rawTokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (rawTokens.Length == 0)
            return Array.Empty<string>();

        var selected = new List<string>(capacity: Math.Min(rawTokens.Length, 9));
        for (var i = 0; i < rawTokens.Length; i++)
        {
            var token = rawTokens[i];
            if (IsLongTitleSignalToken(token))
            {
                selected.Add(token);
                continue;
            }

            if ((IsShortTitleSignalToken(token)
                    && selected.Count > 0
                    && rawTokens.Take(i).Any(static previous => TitleConnectorTokens.Contains(previous)))
                || IsTerminalShortTitleDisambiguator(rawTokens, i, selected.Count))
            {
                selected.Add(token);
            }
        }

        if (selected.Count < 2 && HasShortConnectorTitleShape(rawTokens))
        {
            selected.AddRange(rawTokens.Where(IsFocusedLookupSignalToken));
        }

        return selected
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
    }


    private static bool HasShortConnectorTitleShape(IReadOnlyList<string> rawTokens)
        => rawTokens.Count is >= 3 and <= 6
           && rawTokens.Any(static token => TitleConnectorTokens.Contains(token))
           && rawTokens.Count(IsFocusedLookupSignalToken) >= 2;


    private static bool IsLongTitleSignalToken(string token)
        => ((token.Length >= 4 && token.Any(char.IsLetter)) || token.Any(char.IsDigit))
           && !TitleConnectorTokens.Contains(token)
           && !LexicalStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token);


    internal static bool IsShortTitleSignalToken(string token)
        => token.Length is >= 1 and <= 3
           && token.Any(char.IsLetterOrDigit)
           && !TitleConnectorTokens.Contains(token)
           && !LexicalStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token);


    private static bool IsTerminalShortTitleDisambiguator(IReadOnlyList<string> rawTokens, int index, int selectedCount)
    {
        if (index != rawTokens.Count - 1 || selectedCount < 2)
            return false;

        var token = rawTokens[index];
        return token.Length is >= 1 and <= 3
               && token.Any(char.IsLetterOrDigit)
               && !LexicalStopwords.Contains(token)
               && !SpecificAnchorStopwords.Contains(token)
               && !PrimaryAnchorStopwords.Contains(token);
    }


    private static bool IsTitleScoringTokenVariant(string variant)
        => variant.Length >= 4 || IsShortTitleSignalToken(variant) || IsTerminalTitleVariant(variant);


    private static bool IsTerminalTitleVariant(string variant)
        => variant.Length is >= 1 and <= 3
           && variant.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(variant)
           && !SpecificAnchorStopwords.Contains(variant)
           && !PrimaryAnchorStopwords.Contains(variant);


    internal static bool ShouldAllowNavigationalResults(string query)
    {
        var normalized = $" {NormalizeQuery(FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))).ToLowerInvariant()} ";
        return ContainsAny(normalized,
            " index ",
            " sommaire ",
            " table des matieres ",
            " table of contents ",
            " inhaltsverzeichnis ",
            " indice general ",
            " indice de contenido ",
            " indice de contenidos ",
            " indice de materias ",
            " indice analitico ",
            " sumario ",
            " sommario ",
            " toc ",
            " inventaire ",
            " corpus ",
            " vue d ensemble ",
            " overview ",
            " quels livres ",
            " quelles sources ",
            " quels documents ",
            " que documentos ",
            " quais documentos ",
            " quali documenti ",
            " welche dokumente ",
            " que fuentes ",
            " quais fontes ",
            " quali fonti ",
            " welche quellen ")
            || LooksLikeGenericNavigationalRequest(normalized);
    }


    internal static bool ShouldExplicitlyRequestNavigationArtifacts(string query)
    {
        var normalized = $" {NormalizeQuery(FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))).ToLowerInvariant()} ";
        return ContainsAny(normalized,
            " index ",
            " sommaire ",
            " table des matieres ",
            " table of contents ",
            " inhaltsverzeichnis ",
            " indice general ",
            " indice de contenido ",
            " indice de contenidos ",
            " indice de materias ",
            " indice analitico ",
            " sumario ",
            " sommario ",
            " toc ");
    }


    private static bool LooksLikeGenericNavigationalRequest(string normalizedPaddedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedPaddedQuery))
            return false;

        if (ContainsAny(
            normalizedPaddedQuery,
            " inhaltsverzeichnis ",
            " indice general ",
            " indice de contenido ",
            " indice de contenidos ",
            " indice de materias ",
            " indice analitico ",
            " sumario ",
            " sommario ",
            " toc "))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            normalizedPaddedQuery,
            @"\b(?:liste|list|catalogue|catalog|inventaire|inventory|index)\s+(?:des?|de|du|d['’]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:liste|list|catalogue|catalog|inventory|index)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }


    internal static string NormalizeForLexicalSignal(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value));


    internal static bool ContainsOrderedPhraseWindow(string? candidateText, string phrase, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase));
        return ContainsOrderedPhraseWindowInNormalizedText(
            NormalizeForLexicalSignal(candidateText),
            normalizedPhrase,
            maxGapChars);
    }


    internal static bool ContainsOrderedPhraseWindowInNormalizedText(string normalizedCandidate, string normalizedPhrase, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        var tokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 2)
            return false;

        var searchFrom = 0;
        var previousEnd = -1;
        foreach (var token in tokens)
        {
            var index = normalizedCandidate.IndexOf(token, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (previousEnd >= 0 && index - previousEnd > maxGapChars)
                return false;

            previousEnd = index + token.Length;
            searchFrom = previousEnd;
        }

        return true;
    }


    private const int MaxCalibrationProfileSignalChars = 24000;

    private const int MaxCalibrationCardSignalChars = 8000;

    internal const int MaxCalibrationCardCount = 32;


    internal static string GetLexicalSignalText(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return BuildDocumentProfileCalibrationSignal(match, includeEmbedFallback: true);

        return string.IsNullOrWhiteSpace(match.EmbedText)
            ? match.Text ?? string.Empty
            : match.EmbedText!;
    }


    internal static string GetDirectChunkSignalText(RagMatch match)
        => string.Join("\n", new[]
        {
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));


    internal static string GetTitleSignalText(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return BuildDocumentProfileCalibrationSignal(match, includeEmbedFallback: true);

        var includeEmbedText = string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal)
            || HasProfileTitleHint(match);
        return string.Join("\n", new[]
        {
            includeEmbedText ? match.EmbedText : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }


    internal static int ComputeShortTechnicalDirectEvidencePriority(
        string query,
        RagMatch match,
        string normalizedTextForSignals,
        string normalizedTitleSignalText)
    {
        var (queryTerms, hasProperty, hasElectrical, hasThermal) = ClassifyShortTechnicalPropertyLookup(query);
        if (queryTerms.Count == 0)
            return 0;

        if (!hasProperty || (!hasElectrical && !hasThermal))
            return 0;

        _ = normalizedTextForSignals;
        _ = normalizedTitleSignalText;

        var directTitleEvidence = NormalizeForLexicalSignal($"{match.SectionTitle} {match.HeadingPath}");
        var directChunkEvidence = NormalizeForLexicalSignal($"{match.Text} {match.SectionTitle} {match.HeadingPath}");

        if (hasElectrical)
        {
            if (ContainsElectricalPropertyPhrase(directTitleEvidence))
                return 5;
            if (ContainsElectricalPropertyPhrase(directChunkEvidence))
                return 4;
        }

        if (hasThermal)
        {
            if (ContainsThermalPropertyPhrase(directTitleEvidence))
                return 5;
            if (ContainsThermalPropertyPhrase(directChunkEvidence))
                return 4;
        }

        return 0;
    }


    internal static bool ShouldRunShortTechnicalDirectEvidenceLinkedBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        if (selected.Count == 0 || !IsShortTechnicalPropertyLookup(query))
            return false;

        var firstDirectEvidenceRank = selected
            .Take(8)
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                Priority = ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty)
            })
            .FirstOrDefault(static item => item.Priority > 0)?.Index;
        if (firstDirectEvidenceRank == 0)
            return false;

        return selected.Take(8).Any(static match =>
            !string.IsNullOrWhiteSpace(match.PrevChunkId)
            || !string.IsNullOrWhiteSpace(match.NextChunkId)
            || !string.IsNullOrWhiteSpace(match.SameSectionChunkId));
    }


    internal static bool ShouldRunShortTechnicalDirectPhraseBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        if (!IsShortTechnicalPropertyLookup(query))
            return false;

        var limit = Math.Min(selected.Count, 8);
        for (var i = 0; i < limit; i++)
        {
            if (ComputeShortTechnicalDirectEvidencePriority(query, selected[i], string.Empty, string.Empty) > 0)
                return i > 0;
        }

        return true;
    }


    internal static void PrioritizeShortTechnicalDirectEvidenceSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count < 2 || !IsShortTechnicalPropertyLookup(query))
            return;

        var ranked = selected
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                Priority = ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty)
            })
            .OrderByDescending(static item => item.Priority > 0)
            .ThenByDescending(static item => item.Priority)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match)
            .ToList();

        selected.Clear();
        selected.AddRange(ranked);
    }


    internal static bool IsShortTechnicalPropertyLookup(string query)
    {
        var (_, hasProperty, hasElectrical, hasThermal) = ClassifyShortTechnicalPropertyLookup(query);
        return hasProperty && (hasElectrical || hasThermal);
    }


    private static (HashSet<string> Terms, bool HasProperty, bool HasElectrical, bool HasThermal) ClassifyShortTechnicalPropertyLookup(string query)
    {
        var terms = BuildShortTechnicalLookupTerms(query);
        var normalized = $" {NormalizeForLexicalSignal(RepairLikelyMojibakeText(query))} ";
        var hasProperty = terms.Overlaps(new[] { "propriete", "proprietes", "property", "properties", "characteristic", "characteristics" })
            || ContainsAny(
                normalized,
                " propriete ",
                " proprietes ",
                " property ",
                " properties ",
                " characteristic ",
                " characteristics ");
        var hasElectrical = terms.Overlaps(new[] { "electrique", "electriques", "electric", "electrical", "electricity", "dielectric" })
            || ContainsAny(
                normalized,
                " electrique ",
                " electriques ",
                " electric ",
                " electrical ",
                " dielectric ");
        var hasThermal = terms.Overlaps(new[] { "thermique", "thermiques", "thermal", "temperature", "temperatures", "melt point", "melting point" })
            || ContainsAny(
                normalized,
                " thermique ",
                " thermiques ",
                " thermal ",
                " temperature ",
                " temperatures ",
                " melt point ",
                " melting point ");

        if (!hasProperty
            && (ContainsAny(normalized, " thermal properties ", " temperature properties ", " electrical properties ", " dielectric properties ")
                || ContainsAny(normalized, " proprietes thermiques ", " proprietes electriques ")))
        {
            hasProperty = true;
        }

        return (terms, hasProperty, hasElectrical, hasThermal);
    }


    private static HashSet<string> BuildShortTechnicalLookupTerms(string query)
    {
        var normalized = NormalizeForLexicalSignal(RepairLikelyMojibakeText(query));
        var terms = ExtractLexicalQueryTokens(normalized)
            .SelectMany(static token => BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var phrase in BuildQueryExpansionTerms(normalized).Where(static term => term.Contains(' ')))
            terms.Add(phrase);
        return terms;
    }


    internal static IReadOnlyList<string> BuildShortTechnicalDirectPhraseTerms(string query)
    {
        var (queryTerms, hasProperty, hasElectrical, hasThermal) = ClassifyShortTechnicalPropertyLookup(query);
        if (queryTerms.Count == 0)
            return [];

        if (!hasProperty || (!hasElectrical && !hasThermal))
            return [];

        var phrases = new List<string>();
        if (hasElectrical)
        {
            phrases.AddRange([
                "electrical properties",
                "electric properties",
                "dielectric properties",
                "electrical characteristics",
                "dielectric strength",
                "dielectric constant",
                "volume resistivity",
                "surface resistivity",
                "propriete electrique",
                "proprietes electriques",
                "caracteristique electrique",
                "caracteristiques electriques"
            ]);
        }

        if (hasThermal)
        {
            phrases.AddRange([
                "thermal properties",
                "temperature properties",
                "thermal characteristics",
                "melt point",
                "melting point",
                "service temperature",
                "service temperature range",
                "propriete thermique",
                "proprietes thermiques",
                "caracteristique thermique",
                "caracteristiques thermiques"
            ]);
        }

        return phrases
            .Select(static phrase => NormalizeForLexicalSignal(phrase))
            .Where(static phrase => phrase.Contains(' '))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static phrase => phrase.Length)
            .ThenBy(static phrase => phrase, StringComparer.Ordinal)
            .ToArray();
    }


    private static bool ContainsElectricalPropertyPhrase(string normalizedEvidence)
        => Regex.IsMatch(
            normalizedEvidence,
            @"\b(?:electrical\s+properties|electric\s+properties|dielectric\s+properties|properties\s+(?:electrical|electric|dielectric)|electrical\s+characteristics|dielectric\s+strength|proprietes?\s+electriques?|caracteristiques?\s+electriques?)\b",
            RegexOptions.CultureInvariant);


    private static bool ContainsThermalPropertyPhrase(string normalizedEvidence)
        => Regex.IsMatch(
            normalizedEvidence,
            @"\b(?:thermal\s+properties|temperature\s+properties|properties\s+thermal|thermal\s+characteristics|melt\s+point|melting\s+point|service\s+temperature|proprietes?\s+thermiques?|caracteristiques?\s+thermiques?)\b",
            RegexOptions.CultureInvariant);


    internal static string BuildDocumentProfileCalibrationSignal(RagMatch match, bool includeEmbedFallback)
    {
        var sb = new StringBuilder();
        AppendCalibrationSignalPart(sb, match.Text, maxChars: 5000);
        AppendCalibrationSignalPart(sb, match.SectionTitle, maxChars: 500);
        AppendCalibrationSignalPart(sb, match.HeadingPath, maxChars: 500);

        if (match.MatchedContentCards is { Count: > 0 })
        {
            var cardsSb = new StringBuilder();
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
            {
                AppendCalibrationSignalPart(cardsSb, card.Title, maxChars: 500);
                AppendCalibrationSignalPart(cardsSb, card.Kind, maxChars: 120);
                if (card.Signals is { Count: > 0 })
                {
                    foreach (var signal in card.Signals.Take(12))
                        AppendCalibrationSignalPart(cardsSb, signal, maxChars: 240);
                }

                if (cardsSb.Length >= MaxCalibrationCardSignalChars)
                    break;
            }

            AppendCalibrationSignalPart(sb, cardsSb.ToString(), MaxCalibrationCardSignalChars);
        }

        if (includeEmbedFallback)
            AppendCalibrationSignalPart(sb, match.EmbedText, MaxCalibrationProfileSignalChars);

        return sb.ToString();
    }


    private static void AppendCalibrationSignalPart(StringBuilder sb, string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || maxChars <= 0)
            return;

        if (sb.Length > 0)
            sb.Append('\n');

        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            sb.Append(trimmed);
            return;
        }

        var headChars = Math.Max(1, maxChars / 2);
        var tailChars = Math.Max(1, maxChars - headChars);
        sb.Append(trimmed, 0, headChars);
        sb.Append('\n');
        sb.Append(trimmed, trimmed.Length - tailChars, tailChars);
    }


    internal static bool HasProfileTitleHint(RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.EmbedText))
            return false;

        if (match.EmbedText!.StartsWith("Matched navigation_route:", StringComparison.Ordinal))
            return NavigationRouteHasTargetTitleEvidence(match);
        if (match.EmbedText.StartsWith("Matched direct_title_token_route:", StringComparison.Ordinal))
            return DirectTitleTokenRouteHasTargetTitleEvidence(match);
        if (match.EmbedText.StartsWith("Matched quoted title:", StringComparison.Ordinal))
            return MatchedQuotedTitleHasTargetTitleEvidence(match);

        return match.EmbedText.StartsWith("Matched profile title:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched title_anchor_exact_page_route:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched title_anchor_route:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched linked_anchor_title:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched fuzzy_title_lead:", StringComparison.Ordinal);
    }


    internal static bool ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(
        string query,
        RagMatch match)
    {
        if (!string.Equals(ResolveRetriever(match), "sparse_bm25", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(match.EmbedText)
            || !match.EmbedText.StartsWith("Matched profile title:", StringComparison.Ordinal))
        {
            return false;
        }

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count < 2)
            return false;

        var normalizedQuery = NormalizeForLexicalSignal(query);
        var matchedTitles = ExtractMatchedRouteOrProfileTitle(match.EmbedText)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeForLexicalSignal)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .ToArray() ?? [];
        if (string.IsNullOrWhiteSpace(normalizedQuery)
            || !matchedTitles.Contains(normalizedQuery, StringComparer.Ordinal))
        {
            return false;
        }

        var normalizedDirectEvidence = NormalizeForLexicalSignal(GetDirectChunkSignalText(match));
        return !normalizedDirectEvidence.Contains(normalizedQuery, StringComparison.Ordinal);
    }


    internal static bool MatchedQuotedTitleHasTargetTitleEvidence(RagMatch match)
    {
        var matchedTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(matchedTitle))
            return false;

        var phrases = matchedTitle
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static phrase => ExactMatchEntryExtractor.NormalizeForLookup(phrase))
            .Where(static phrase => phrase.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (phrases.Length == 0)
            return false;

        if (HasLocalTitleTokenLeadEvidence(match.Text, match.SectionTitle, match.HeadingPath, phrases))
            return true;

        if (match.MatchedContentCards is not { Count: > 0 })
            return false;

        foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
        {
            var title = card.Title;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (ComputeQuotedLookupCandidateScore(phrases, title) > 0.0)
                return true;
        }

        return false;
    }


    internal static bool NavigationRouteHasTargetTitleEvidence(RagMatch match)
    {
        if (!IsNavigationRouteMatch(match))
            return true;

        if (IsNavigationCatalogRouteMatch(match))
            return !LooksLikeNavigationalChunk(match) && HasUsefulResolvedTitleBody(match);

        return RouteHasTargetTitleEvidence(match);
    }


    internal static bool TitleAnchorRouteHasTargetTitleEvidence(RagMatch match)
    {
        if (!IsTitleAnchorRouteMatch(match))
            return true;

        if (IsExactPageTitleAnchorRouteMatch(match))
            return true;

        return RouteHasTargetTitleEvidence(match);
    }


    internal static bool DirectTitleTokenRouteHasTargetTitleEvidence(RagMatch match)
    {
        if (!IsDirectTitleTokenRouteMatch(match))
            return true;

        return RouteHasTargetTitleEvidence(match);
    }


    internal static bool DirectTitleTokenRouteHasStrongLeadEvidence(RagMatch match)
    {
        if (!IsDirectTitleTokenRouteMatch(match))
            return true;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
            return false;

        var normalizedRouteTitle = NormalizeNavigationRouteEvidenceTitle(routeTitle);
        if (string.IsNullOrWhiteSpace(normalizedRouteTitle))
            return false;

        var normalizedSection = NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (!string.IsNullOrWhiteSpace(normalizedSection)
            && normalizedSection.Contains(normalizedRouteTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var titleTokens = normalizedRouteTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (match.MatchedContentCards is { Count: > 0 } && titleTokens.Length >= 2)
        {
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
            {
                var normalizedCardTitle = NormalizeForLexicalSignal(card.Title);
                if (string.IsNullOrWhiteSpace(normalizedCardTitle))
                    continue;

                if (normalizedCardTitle.Contains(normalizedRouteTitle, StringComparison.Ordinal)
                    || ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCardTitle, titleTokens, maxGapChars: 24)
                    || ContainsTitleLikeLexicalSequence(normalizedCardTitle, titleTokens))
                {
                    return true;
                }
            }
        }

        var normalizedText = NormalizeForLexicalSignal(match.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var exactPosition = normalizedText.IndexOf(normalizedRouteTitle, StringComparison.Ordinal);
        if (LooksLikeStandaloneLeadTitlePlacement(match.Text, normalizedText, normalizedRouteTitle, exactPosition))
            return true;
        if (HasLocalTitleTokenFooterTitleEvidence(normalizedText, exactPosition, normalizedRouteTitle.Length))
            return true;

        if (titleTokens.Length < 2)
            return false;

        var leadingText = normalizedText.Length <= 48 ? normalizedText : normalizedText[..48];
        var firstTitleTokenPosition = leadingText.IndexOf(titleTokens[0], StringComparison.Ordinal);
        if (firstTitleTokenPosition < 0 || firstTitleTokenPosition > 8)
        {
            var leadWindow = normalizedText.Length <= 140 ? normalizedText : normalizedText[..140];
            var leadWindowTitleTokenPosition = leadWindow.IndexOf(titleTokens[0], StringComparison.Ordinal);
            return leadWindowTitleTokenPosition >= 0
                && (HasLeadTitleCue(leadWindow, normalizedRouteTitle, leadWindowTitleTokenPosition)
                    || HasUppercaseLeadTitleTokenCoverage(match.Text, titleTokens))
                && ContainsTitleLikeLexicalSequence(leadWindow, titleTokens)
                && LooksLikeStructuredAnswerChunk(match);
        }

        return ContainsOrderedTitleTokenSubstringsInNormalizedText(leadingText, titleTokens, maxGapChars: 16)
            || ContainsTitleLikeLexicalSequence(leadingText, titleTokens);
    }


    internal static bool LooksLikeStandaloneLeadTitlePlacement(
        string? rawText,
        string normalizedText,
        string normalizedTitle,
        int exactPosition)
    {
        if (exactPosition < 0 || string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;
        if (exactPosition > 140)
            return false;

        var before = normalizedText[..exactPosition].TrimEnd();
        if (before.Length == 0)
            return !LeadTitlePrefixEndsWithBlockedToken(before);
        if (exactPosition <= 8)
        {
            if (LeadTitlePrefixEndsWithBlockedToken(before))
                return false;
            if (LooksLikeShortDurationTitlePrefix(before))
                return true;

            return HasRawStandaloneTitleBoundary(rawText, normalizedTitle);
        }

        if (!HasRawStandaloneTitleBoundary(rawText, normalizedTitle))
            return false;

        return !LeadTitlePrefixEndsWithBlockedToken(before);
    }


    private static bool LooksLikeShortDurationTitlePrefix(string normalizedPrefix)
        => Regex.IsMatch(
            normalizedPrefix,
            @"^\d{1,3}\s*(?:min|minute|minutes|h|heure|heures|hour|hours)$",
            RegexOptions.CultureInvariant);


    private static bool LeadTitlePrefixEndsWithBlockedToken(string before)
    {
        var beforeTokens = before.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (beforeTokens.Length == 0)
            return false;

        var lastToken = beforeTokens[^1];
        return lastToken.Any(char.IsDigit)
            || string.Equals(lastToken, "de", StringComparison.Ordinal)
            || string.Equals(lastToken, "du", StringComparison.Ordinal)
            || string.Equals(lastToken, "des", StringComparison.Ordinal)
            || string.Equals(lastToken, "d", StringComparison.Ordinal)
            || string.Equals(lastToken, "of", StringComparison.Ordinal)
            || string.Equals(lastToken, "with", StringComparison.Ordinal);
    }


    private static bool HasRawStandaloneTitleBoundary(string? rawText, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var tokens = normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Text.RegularExpressions.Regex.Escape)
            .ToArray();
        if (tokens.Length == 0)
            return false;

        var foldedRawText = FoldDiacritics(rawText).ToLowerInvariant();
        var pattern = @"(?<![\p{L}\p{Nd}])"
            + string.Join(@"[\s\p{P}\p{S}]+", tokens)
            + @"(?![\p{L}\p{Nd}])";
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     foldedRawText,
                     pattern,
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            if (match.Index > 180)
                break;
            if (match.Index <= 8)
            {
                var rawPrefix = foldedRawText[..match.Index].Trim();
                if (rawPrefix.Length == 0
                    || !rawPrefix.Any(char.IsLetterOrDigit)
                    || LooksLikeShortDurationTitlePrefix(NormalizeForLexicalSignal(rawPrefix)))
                {
                    return true;
                }
            }

            var beforeIndex = match.Index - 1;
            var crossedLineBreak = false;
            while (beforeIndex >= 0 && char.IsWhiteSpace(foldedRawText[beforeIndex]))
            {
                crossedLineBreak |= foldedRawText[beforeIndex] is '\n' or '\r';
                beforeIndex--;
            }
            if (beforeIndex < 0)
                return true;
            if (crossedLineBreak)
                return true;

            if (IsStandaloneTitleBoundaryCharacter(foldedRawText[beforeIndex]))
                return true;
        }

        return false;
    }


    internal static bool HasRawExactQuotedTitleEndBoundary(string? rawText, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var tokens = normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Text.RegularExpressions.Regex.Escape)
            .ToArray();
        if (tokens.Length == 0)
            return false;

        var foldedRawText = FoldDiacritics(rawText).ToLowerInvariant();
        var pattern = @"(?<![\p{L}\p{Nd}])"
            + string.Join(@"[\s\p{P}\p{S}]+", tokens)
            + @"(?![\p{L}\p{Nd}])";
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     foldedRawText,
                     pattern,
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            var suffixIndex = match.Index + match.Length;
            var crossedLineBreak = false;
            while (suffixIndex < foldedRawText.Length && char.IsWhiteSpace(foldedRawText[suffixIndex]))
            {
                crossedLineBreak |= foldedRawText[suffixIndex] is '\n' or '\r';
                suffixIndex++;
            }

            if (suffixIndex >= foldedRawText.Length || crossedLineBreak)
                return true;

            var suffixChar = foldedRawText[suffixIndex];
            if (char.IsDigit(suffixChar))
                return true;
            if (suffixChar is '.' or ':' or ';' or '!' or '?' or ')' or ']' or '}')
                return true;
        }

        return false;
    }


    private static bool IsStandaloneTitleBoundaryCharacter(char ch)
        => ch is '.' or ':' or ';' or '!' or '?' or '\n' or '\r';


    private static bool RouteHasTargetTitleEvidence(RagMatch match)
    {
        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
            return false;

        var directSignal = GetDirectChunkSignalText(match);
        if (string.IsNullOrWhiteSpace(directSignal))
            return false;

        var normalizedRouteTitle = NormalizeNavigationRouteEvidenceTitle(routeTitle);
        if (string.IsNullOrWhiteSpace(normalizedRouteTitle))
            return false;

        var normalizedDirect = NormalizeForLexicalSignal(directSignal);
        if (string.IsNullOrWhiteSpace(normalizedDirect))
            return false;

        if (normalizedRouteTitle.Length >= 4
            && normalizedDirect.Contains(normalizedRouteTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var titleTokens = normalizedRouteTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (titleTokens.Length == 1)
            return ContainsExactNormalizedToken(normalizedDirect, titleTokens[0]);

        if (titleTokens.Length >= 2
            && ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedDirect, titleTokens, maxGapChars: 12))
        {
            return true;
        }

        return titleTokens.Length >= 2
            && ContainsTitleLikeLexicalSequence(normalizedDirect, titleTokens);
    }


    internal static bool IsNavigationRouteMatch(RagMatch match)
        => string.Equals(ResolveRetriever(match), "navigation_route", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "navigation_route_v1", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "navigation_catalog_route_v1", StringComparison.Ordinal);


    private static bool IsNavigationCatalogRouteMatch(RagMatch match)
        => string.Equals(match.EmbeddingBasis, "navigation_catalog_route_v1", StringComparison.Ordinal)
           || (match.EmbedText?.StartsWith("Matched navigation_catalog_route:", StringComparison.Ordinal) ?? false);


    internal static bool IsDirectTitleTokenRouteMatch(RagMatch match)
        => string.Equals(ResolveRetriever(match), "direct_title_token_route", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "direct_title_token_route_v1", StringComparison.Ordinal);


    internal static bool IsTitleAnchorRouteMatch(RagMatch match)
        => string.Equals(ResolveRetriever(match), "title_anchor_route", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "title_anchor_route_v1", StringComparison.Ordinal)
           || (match.EmbedText?.StartsWith("Matched title_anchor_exact_page_route:", StringComparison.Ordinal) ?? false)
           || (match.EmbedText?.StartsWith("Matched title_anchor_route:", StringComparison.Ordinal) ?? false);


    internal static bool IsExactPageTitleAnchorRouteMatch(RagMatch match)
        => match.EmbedText?.StartsWith("Matched title_anchor_exact_page_route:", StringComparison.Ordinal) ?? false;


    internal static bool IsExplicitDocumentTitleRouteMatch(RagMatch match)
        => string.Equals(ResolveRetriever(match), "explicit_document_title_route", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "explicit_document_title_route_v1", StringComparison.Ordinal);


    internal static int ResolveDirectTitleTokenRouteMinimumOverlap(int queryTokenCount)
    {
        if (queryTokenCount <= 0)
            return 1;

        return queryTokenCount switch
        {
            <= 2 => queryTokenCount,
            <= 4 => 3,
            _ => Math.Max(3, (int)Math.Ceiling(queryTokenCount * 0.60))
        };
    }


    private static string NormalizeNavigationRouteEvidenceTitle(string title)
    {
        var tokens = NormalizeForLexicalSignal(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        while (tokens.Count > 1
               && (tokens[0].Length == 1
                   || tokens[0].All(char.IsDigit)
                   || IsFocusedLookupLeadingEdgeToken(tokens[0])))
        {
            tokens.RemoveAt(0);
        }

        return string.Join(' ', tokens);
    }


    internal static bool ContainsSpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
        => CountSpecificLexicalAnchors(lexicalTokens, candidateText) > 0;


    internal static bool RequiresPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens)
        => lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken);


    internal static bool ContainsPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return false;

        return ContainsPrimarySpecificLexicalAnchorInNormalizedText(
            lexicalTokens,
            NormalizeForLexicalSignal(candidateText));
    }


    internal static bool ContainsPrimarySpecificLexicalAnchorInNormalizedText(IReadOnlyList<string> lexicalTokens, string normalizedCandidate)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        var primaryTokens = lexicalTokens
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (primaryTokens.Length == 0)
            return true;

        foreach (var token in primaryTokens)
        {
            if (token.Any(char.IsDigit)
                && ContainsExactNormalizedToken(normalizedCandidate, token))
            {
                return true;
            }

            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 5 && ContainsExactNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
                if (foldedVariant.Length >= 6 && normalizedCandidate.Contains(foldedVariant, StringComparison.Ordinal))
                    return true;
                if (foldedVariant.Length >= 6 && ContainsNearNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
            }
        }

        return false;
    }


    internal static IReadOnlyList<string> ExtractComparativeSubjectAnchorTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var markerIndex = Array.FindIndex(tokens, IsComparativeCriterionMarker);
        if (markerIndex <= 0)
            return Array.Empty<string>();

        var start = Math.Max(0, markerIndex - 5);
        return tokens
            .Skip(start)
            .Take(markerIndex - start)
            .Where(IsComparativeSubjectAnchorToken)
            .TakeLast(2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    internal static bool ContainsComparativeSubjectAnchor(IReadOnlyList<string> subjectTokens, string? candidateText)
    {
        if (subjectTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(candidateText))
            return false;

        return ContainsComparativeSubjectAnchorInNormalizedText(subjectTokens, NormalizeForLexicalSignal(candidateText));
    }


    internal static bool ContainsComparativeSubjectAnchorInNormalizedText(IReadOnlyList<string> subjectTokens, string normalizedCandidate)
    {
        if (subjectTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var token in subjectTokens)
        {
            var variants = BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Distinct(StringComparer.Ordinal);
            if (variants.Any(variant => normalizedCandidate.Contains(FoldDiacritics(variant), StringComparison.Ordinal)))
                return true;
        }

        return false;
    }


    private static bool IsComparativeCriterionMarker(string token)
        => token is "plus" or "most" or "mas" or "mais" or "mehr";


    private static bool IsComparativeSubjectAnchorToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !LexicalStopwords.Contains(token)
           && !ComparativeSubjectStopwords.Contains(token);


    internal static bool IsPrimarySpecificLexicalAnchorToken(string token)
        => !SpecificAnchorStopwords.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token)
            && (token.Length >= 6 || token.Any(char.IsDigit) || IsReferenceLikeLookupTerm(token));


    internal static double ComputeExactTitleCandidateScore(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.0;
        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count == 0 && ContainsExactTitleActionMarker(query))
            return 0.0;
        if (quotedPhrases.Count > 0
            && string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal)
            && !HasQuotedTitlePlacementEvidence(quotedPhrases, match))
        {
            return 0.0;
        }

        var titleTokens = ExtractTitleScoringTokens(query);
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0.0;

        var candidateText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(16)
            .ToArray();
        return ComputeExactTitleCandidateScoreCore(
            match,
            NormalizeForLexicalSignal(candidateText),
            NormalizeForLexicalSignal(match.Text),
            titleTokens,
            phraseTerms,
            NormalizeForLexicalSignal(query));
    }


    internal static double ComputeQuotedTitleAnchorSignal(string query, RagMatch match)
    {
        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return 0.0;

        var titleAnchorText = BuildTitleAnchorSignalText(match);
        return string.IsNullOrWhiteSpace(titleAnchorText)
            ? 0.0
            : ComputeQuotedLookupCandidateScore(quotedPhrases, titleAnchorText);
    }


    private static string BuildTitleAnchorSignalText(RagMatch match)
    {
        var parts = new List<string?>(capacity: 6)
        {
            match.SectionTitle,
            match.HeadingPath
        };

        if (match.MatchedContentCards is { Count: > 0 })
        {
            parts.AddRange(match.MatchedContentCards
                .Take(MaxCalibrationCardCount)
                .Select(static card => card.Title));
        }

        if (IsResolvedTitleOrNavigationRoute(match) || IsDocumentProfileMatch(match) || HasProfileTitleHint(match))
            parts.Add(match.EmbedText);

        return string.Join("\n", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }


    internal static double ComputeExactTitleCandidateScoreCore(
        RagMatch match,
        string normalizedCandidate,
        string normalizedChunkText,
        IReadOnlyList<string> titleTokens,
        IReadOnlyList<string> phraseTerms,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0.0;
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var score = ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCandidate, titleTokens, maxGapChars: 80)
            ? 16.0 + Math.Min(4.0, titleTokens.Count)
            : 0.0;
        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 18.0;
        }

        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 14.0;
        }

        var orderedPhraseMatch = phraseTerms.Any(term => ContainsOrderedPhraseWindowInNormalizedText(normalizedCandidate, term, maxGapChars: 40));
        if (orderedPhraseMatch)
            score += 10.0;

        if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCandidate, titleTokens, maxGapChars: 60))
            score += 14.0;

        if (ContainsTitleLikeLexicalSequence(normalizedCandidate, titleTokens))
            score += 12.0;

        if (ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens))
            score += 10.0;

        if (score <= 0.0)
            return 0.0;

        if (normalizedCandidate.StartsWith(normalizedQuery, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith("matched profile title " + normalizedQuery, StringComparison.Ordinal))
        {
            score += 4.0;
        }

        if (string.Equals(match.ChunkType, "footer_titled_item_window_v1", StringComparison.Ordinal))
            score += 10.0;
        else if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            score += 8.0;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            score += 1.0;
        else if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            score += 1.0;

        return score;
    }


    internal static int ComputeDirectChunkTitleSignal(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(match.Text))
            return 0;

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count > 0)
            return HasQuotedTitlePlacementEvidence(quotedPhrases, match) ? 4 : 0;

        var titleTokens = ExtractTitleScoringTokens(query);
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0;

        return ComputeDirectChunkTitleSignalCore(
            NormalizeForLexicalSignal(match.Text),
            titleTokens,
            NormalizeForLexicalSignal(query));
    }


    internal static int ComputeDirectChunkTitleSignalCore(
        string normalizedChunkText,
        IReadOnlyList<string> titleTokens,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8 || string.IsNullOrWhiteSpace(normalizedChunkText))
            return 0;

        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 3;
        }

        if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedChunkText, titleTokens, maxGapChars: 60))
            return 2;

        return ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens) ? 1 : 0;
    }


    internal static int ComputeMatchedContentCardTitleSignal(
        RagMatch match,
        IReadOnlyList<string> titleTokens,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0;
        if (match.MatchedContentCards is not { Count: > 0 })
            return 0;

        var best = 0;
        foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
        {
            if (string.IsNullOrWhiteSpace(card.Title) || !LooksLikeConcreteMatchedContentCardTitle(card.Title))
                continue;

            var normalizedCard = NormalizeForLexicalSignal(card.Title);
            if (string.IsNullOrWhiteSpace(normalizedCard))
                continue;

            if (normalizedQuery.Length >= 8
                && normalizedQuery.Count(static ch => ch == ' ') >= 1
                && normalizedCard.Contains(normalizedQuery, StringComparison.Ordinal))
                best = Math.Max(best, 4);
            else if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCard, titleTokens, maxGapChars: 40))
                best = Math.Max(best, 3);
            else if (ContainsTitleLikeLexicalSequence(normalizedCard, titleTokens))
                best = Math.Max(best, 2);
            else if (ComputeLexicalCoverageInNormalizedText(titleTokens, normalizedCard) >= 0.80)
                best = Math.Max(best, 1);

            if (best >= 4)
                break;
        }

        return best;
    }


    internal static bool ContainsTitleLikeLexicalSequence(string normalizedCandidate, IReadOnlyList<string> titleTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || titleTokens.Count < 2)
            return false;

        var candidateTokens = normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 0)
            .Take(1500)
            .ToArray();
        if (candidateTokens.Length < titleTokens.Count)
            return false;

        var tokenVariants = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Where(IsTitleScoringTokenVariant)
                .DefaultIfEmpty(FoldDiacritics(token))
                .ToHashSet(StringComparer.Ordinal))
            .ToArray();

        for (var start = 0; start < candidateTokens.Length; start++)
        {
            if (!TokenMatchesTitleVariant(candidateTokens[start], tokenVariants[0]))
                continue;

            var current = start;
            var matchedAll = true;
            for (var expected = 1; expected < tokenVariants.Length; expected++)
            {
                var next = FindNextTitleToken(candidateTokens, current + 1, tokenVariants[expected]);
                if (next < 0)
                {
                    matchedAll = false;
                    break;
                }

                for (var i = current + 1; i < next; i++)
                {
                    if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                    {
                        matchedAll = false;
                        break;
                    }
                }

                if (!matchedAll)
                    break;

                current = next;
            }

            if (matchedAll)
                return true;
        }

        return false;
    }


    private static int FindNextTitleToken(IReadOnlyList<string> candidateTokens, int startIndex, IReadOnlySet<string> expectedVariants)
    {
        var maxExclusive = Math.Min(candidateTokens.Count, startIndex + 6);
        for (var i = startIndex; i < maxExclusive; i++)
        {
            if (TokenMatchesTitleVariant(candidateTokens[i], expectedVariants))
                return i;
            if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                break;
        }

        return -1;
    }


    private static bool TokenMatchesTitleVariant(string candidateToken, IReadOnlySet<string> expectedVariants)
    {
        if (expectedVariants.Contains(candidateToken))
            return true;

        foreach (var variant in expectedVariants)
        {
            if (variant.Length < 4
                || candidateToken.Length <= variant.Length
                || !candidateToken.StartsWith(variant, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = candidateToken[variant.Length..];
            if (TitleConnectorTokens.Contains(suffix)
                || suffix.All(char.IsDigit)
                || (suffix.Length <= 2 && suffix.All(char.IsLetter)))
            {
                return true;
            }
        }

        return false;
    }


    internal static bool ContainsOrderedTitleTokenSubstrings(string candidateText, IReadOnlyList<string> titleTokens, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || titleTokens.Count < 2)
            return false;

        return ContainsOrderedTitleTokenSubstringsInNormalizedText(
            FoldDiacritics(candidateText).ToLowerInvariant(),
            titleTokens,
            maxGapChars);
    }


    internal static bool ContainsOrderedTitleTokenSubstringsInNormalizedText(
        string normalizedCandidate,
        IReadOnlyList<string> titleTokens,
        int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || titleTokens.Count < 2)
            return false;

        var variantGroups = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Append(FoldDiacritics(token))
                .Where(static variant => variant.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        if (variantGroups.Any(static group => group.Length == 0))
            return false;

        return TryFindOrderedTitleTokenSubstrings(
            normalizedCandidate,
            variantGroups,
            tokenIndex: 0,
            searchFrom: 0,
            previousEnd: -1,
            maxGapChars);
    }


    private static bool TryFindOrderedTitleTokenSubstrings(
        string normalizedCandidate,
        IReadOnlyList<string[]> variantGroups,
        int tokenIndex,
        int searchFrom,
        int previousEnd,
        int maxGapChars)
    {
        if (tokenIndex >= variantGroups.Count)
            return true;

        var attempts = 0;
        foreach (var occurrence in EnumerateTitleTokenOccurrences(normalizedCandidate, variantGroups[tokenIndex], searchFrom))
        {
            if (previousEnd >= 0 && occurrence.Index - previousEnd > maxGapChars)
                break;

            if (TryFindOrderedTitleTokenSubstrings(
                normalizedCandidate,
                variantGroups,
                tokenIndex + 1,
                occurrence.Index + occurrence.Length,
                occurrence.Index + occurrence.Length,
                maxGapChars))
            {
                return true;
            }

            attempts++;
            if (attempts >= 48)
                break;
        }

        return false;
    }


    private static IEnumerable<(int Index, int Length)> EnumerateTitleTokenOccurrences(
        string normalizedCandidate,
        IReadOnlyList<string> variants,
        int searchFrom)
    {
        var seen = new HashSet<int>();
        while (searchFrom < normalizedCandidate.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;
            foreach (var variant in variants)
            {
                var index = normalizedCandidate.IndexOf(variant, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                if (bestIndex < 0 || index < bestIndex || (index == bestIndex && variant.Length > bestLength))
                {
                    bestIndex = index;
                    bestLength = variant.Length;
                }
            }

            if (bestIndex < 0)
                yield break;

            if (seen.Add(bestIndex))
                yield return (bestIndex, bestLength);

            searchFrom = bestIndex + 1;
        }
    }


    internal static bool ContainsExactTitleActionMarker(string query)
    {
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        return ContainsAny(
            normalized,
            " explique ",
            " expliquer ",
            " expliquez ",
            " explain ",
            " explains ",
            " summarize ",
            " summary ",
            " resume ",
            " resumer ",
            " resumee ",
            " decris ",
            " decrire ",
            " describe ",
            " detaille ",
            " detailler ",
            " pourquoi ",
            " why ",
            " comment ",
            " how ");
    }


    internal static bool ContainsSummarizationIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        return ContainsAny(
            normalized,
            " resume ",
            " resumer ",
            " resumes ",
            " summary ",
            " summarize ",
            " summarizes ",
            " summarise ",
            " summarises ",
            " synthese ",
            " synthesis ");
    }


    internal static bool ShouldApplySpecificCoverageTitlePriority(
        string query,
        IReadOnlyList<string> lexicalTokens,
        IReadOnlyList<string> comparativeSubjectTokens)
    {
        if (lexicalTokens.Count is < 2 or > 6)
            return false;
        if (comparativeSubjectTokens.Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (IsRecommendationSelectionQuery(normalized))
            return false;

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


    internal static bool ShouldPrioritizeFocusedSpecificContentCoverage(
        string query,
        IReadOnlyList<string> lexicalTokens)
    {
        if (lexicalTokens.Count is < 3 or > 8)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (IsRecommendationSelectionQuery(normalized)
            || ContainsAny(
                normalized,
                " lequel ",
                " laquelle ",
                " lesquels ",
                " lesquelles ",
                " which one ",
                " what one ",
                " compare ",
                " comparer ",
                " comparison ",
                " comparaison ",
                " difference ",
                " differences "))
        {
            return false;
        }

        return ExtractFocusedLookupPhrases(query).Count > 0
            || ContainsExactTitleActionMarker(query)
            || ShouldTreatFocusedLookupAsPreciseTitle(query);
    }


    internal static int ResolveRequiredFocusedSpecificContentCoverage(IReadOnlyList<string> lexicalTokens)
    {
        if (lexicalTokens.Count == 0)
            return 1;

        var meaningfulSpecificTokenCount = lexicalTokens
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => token.Length >= 5 || token.Any(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return meaningfulSpecificTokenCount == 0
            ? 1
            : Math.Min(2, meaningfulSpecificTokenCount);
    }


    internal static bool IsRecommendationSelectionQuery(string normalizedPaddedQuery)
        => ContainsAny(
                normalizedPaddedQuery,
                " quel ",
                " quelle ",
                " quelles ",
                " quels ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welche ")
            && ContainsAny(
                normalizedPaddedQuery,
                " choisir ",
                " choose ",
                " recommander ",
                " recommande ",
                " recommandes ",
                " recommendation ",
                " recommendations ",
                " recomendar ",
                " recomienda ",
                " recomiendas ",
                " sugerir ",
                " sugiere ",
                " elegir ",
                " escolha ",
                " escolher ",
                " recomenda ",
                " recomendas ",
                " suggerire ",
                " suggerisci ",
                " consiglia ",
                " consigliare ",
                " scegliere ",
                " scegli ",
                " empfiehl ",
                " empfiehlst ",
                " empfehlen ",
                " empfehlung ",
                " raten ",
                " adaptee ",
                " adaptees ",
                " adapte ",
                " adaptes ",
                " adecuado ",
                " adecuada ",
                " adequadas ",
                " adequado ",
                " adatto ",
                " adatta ",
                " geeignet ",
                " suitable ",
                " best ",
                " beste ",
                " meilleur ",
                " meilleure ",
                " meilleurs ",
                " meilleures ");


    internal static int CountSpecificLexicalAnchors(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        return CountSpecificLexicalAnchorsInNormalizedText(lexicalTokens, NormalizeForLexicalSignal(candidateText));
    }


    internal static int CountSpecificLexicalAnchorsInNormalizedText(IReadOnlyList<string> lexicalTokens, string normalized)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(normalized))
            return 0;

        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            if (SpecificAnchorStopwords.Contains(token))
                continue;
            if (token.Any(char.IsDigit)
                && ContainsExactNormalizedToken(normalized, token))
            {
                if (seen.Add(token))
                    matched++;
                continue;
            }

            if (token.Length < 5 && !token.Any(char.IsDigit))
                continue;

            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if ((foldedVariant.Length >= 5 && ContainsExactNormalizedToken(normalized, foldedVariant))
                    || (foldedVariant.Length >= 6
                    && (normalized.Contains(foldedVariant, StringComparison.Ordinal)
                        || ContainsNearNormalizedToken(normalized, foldedVariant)))
                   )
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }


    internal static bool ContainsNearNormalizedToken(string normalizedCandidate, string token)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(token) || token.Length < 6)
            return false;

        foreach (var candidate in normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate.Length < 6 || Math.Abs(candidate.Length - token.Length) > 1)
                continue;
            if (ComputeBoundedEditDistance(candidate, token, maxDistance: 1) <= 1)
                return true;
        }

        return false;
    }


    internal static int ComputeBoundedEditDistance(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }


    internal static bool ContainsExactNormalizedToken(string normalizedCandidate, string token)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(token))
            return false;

        return normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(token, StringComparer.Ordinal);
    }


    internal static bool LooksLikeStructuredAnswerUnit(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal)
            && !string.Equals(match.ChunkType, "footer_titled_item_window_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }


    internal static bool LooksLikeStructuredAnswerChunk(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal)
            && !string.Equals(match.ChunkType, "footer_titled_item_window_v1", StringComparison.Ordinal)
            && !string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }


    internal static int GetStructuredAnswerPriority(RagMatch match)
    {
        if (LooksLikeStructuredAnswerUnit(match))
            return 3;
        if (LooksLikeStructuredAnswerChunk(match))
            return 2;
        if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            return 1;
        return 0;
    }


    internal static double ComputeContentEvidencePriority(RagMatch match)
    {
        var priority = GetStructuredAnswerPriority(match) * 0.35;

        if (match.ContentDensityScore is double density)
            priority += Math.Clamp(density, 0.0, 1.0);

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.ContentRole, StringComparison.OrdinalIgnoreCase))
            priority += 0.25;
        else if (string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase))
            priority += 0.10;
        else if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            priority -= 0.25;

        var looksNavigational = LooksLikeNavigationalChunk(match);
        var shouldConserveCardBoost = looksNavigational
            || string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase)
            || match.NavigationScore is >= 0.55;
        if (match.MatchedContentCards is { Count: > 0 })
        {
            var hasStrongCardSupport = match.MatchedContentCards.Any(HasStrongMatchedContentCardSupport);
            priority += shouldConserveCardBoost
                ? hasStrongCardSupport ? 0.06 : 0.0
                : hasStrongCardSupport ? 0.12 : 0.04;
        }

        if (looksNavigational)
            priority -= 0.45;
        if (LooksLikeSourceListChunk(match) || LooksLikeGlossaryChunk(match))
            priority -= 0.25;

        return Math.Clamp(priority, -1.0, 3.0);
    }


    internal static bool HasStrongMatchedContentCardSupport(RagMatchedContentCard card)
    {
        if (card.Signals is null || card.Signals.Count == 0)
            return false;

        return card.Signals.Any(static signal =>
            string.Equals(signal, "structured_facts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "quantity_list", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "scale_basis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal, "scalable_quantities", StringComparison.OrdinalIgnoreCase));
    }


    internal static int ComputeWeakMatchedContentCardTitlePenalty(
        RagMatch match,
        bool fullTitleCoverage,
        double exactTitleScore,
        int directChunkTitleSignal,
        double quotedTitleAnchorSignal,
        bool allowIndependentTitleSignalWaiver)
    {
        if (allowIndependentTitleSignalWaiver
            && (fullTitleCoverage
            || exactTitleScore > 0.0
            || directChunkTitleSignal > 0
            || quotedTitleAnchorSignal > 0.0))
        {
            return 0;
        }

        if (match.MatchedContentCards is not { Count: > 0 })
            return 0;

        var inspected = 0;
        foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
        {
            if (string.IsNullOrWhiteSpace(card.Title))
                continue;

            inspected++;
            if (LooksLikeConcreteMatchedContentCardTitle(card.Title))
                return 0;
        }

        return inspected > 0 ? 1 : 0;
    }


    internal static bool LooksLikeConcreteMatchedContentCardTitle(string title)
    {
        if (LooksLikeProceduralFragmentContentCardTitle(title)
            || LooksLikeGenericContentSectionCardTitle(title)
            || LooksLikeTruncatedContentCardTitle(title)
            || LooksLikeQuantityOrOcrFragmentContentCardTitle(title))
        {
            return false;
        }

        var titleTokens = ExtractLexicalQueryTokens(title);
        return titleTokens.Count >= 2
               || (titleTokens.Count == 1 && IsReferenceLikeLookupTerm(titleTokens[0]));
    }


    internal static int ComputeUnrequestedQualifiedTitlePenalty(
        IReadOnlyList<string> subjectTokens,
        IReadOnlyList<string> queryTokens,
        RagMatch match)
    {
        if (subjectTokens.Count is 0 or > 3)
            return 0;

        var titleCandidates = EnumerateCandidateTitleTexts(match, includeSyntheticRouteTitle: false)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCalibrationCardCount + 3)
            .ToArray();
        if (titleCandidates.Length == 0)
            return 1;

        var bestPenalty = int.MaxValue;
        foreach (var title in titleCandidates)
        {
            var normalizedTitle = NormalizeForLexicalSignal(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle)
                || !ContainsAllTitleSubjectTokens(subjectTokens, normalizedTitle))
            {
                continue;
            }

            var titleTokens = ExtractLexicalQueryTokens(title)
                .Where(static token => !TitleConnectorTokens.Contains(token))
                .Where(static token => !PrimaryAnchorStopwords.Contains(token))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (titleTokens.Length == 0)
                continue;

            var extraTokenCount = titleTokens.Count(token =>
                !TokenMatchesAnyQueryToken(token, queryTokens)
                && !TokenMatchesAnyQueryToken(token, subjectTokens));
            if (extraTokenCount == 0)
                return 0;

            bestPenalty = Math.Min(bestPenalty, Math.Min(3, extraTokenCount));
        }

        return bestPenalty == int.MaxValue ? 1 : bestPenalty;
    }


    internal static IReadOnlyList<string> BuildUnrequestedQualifiedTitleSubjectTokens(
        string query,
        IReadOnlyList<string> fallbackTitleTokens)
    {
        foreach (var phrase in ExtractComparativeLookupPhrases(query))
        {
            var phraseTokens = ExtractTitleScoringTokens(phrase);
            if (phraseTokens.Count is >= 1 and <= 3)
                return phraseTokens;
        }

        return fallbackTitleTokens.Count is >= 1 and <= 3
            ? fallbackTitleTokens
            : Array.Empty<string>();
    }


    private static IEnumerable<string> EnumerateCandidateTitleTexts(
        RagMatch match,
        bool includeSyntheticRouteTitle = true)
    {
        if (includeSyntheticRouteTitle)
        {
            var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
            if (!string.IsNullOrWhiteSpace(routeTitle))
                yield return routeTitle;
        }

        if (match.MatchedContentCards is { Count: > 0 })
        {
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
            {
                if (!string.IsNullOrWhiteSpace(card.Title))
                    yield return card.Title;
            }
        }

        if (!string.IsNullOrWhiteSpace(match.SectionTitle))
            yield return match.SectionTitle;
        if (!string.IsNullOrWhiteSpace(match.HeadingPath))
            yield return match.HeadingPath;
    }


    private static bool ContainsAllTitleSubjectTokens(
        IReadOnlyList<string> subjectTokens,
        string normalizedTitle)
    {
        foreach (var token in subjectTokens)
        {
            if (!TokenMatchesNormalizedText(token, normalizedTitle))
                return false;
        }

        return true;
    }


    internal static bool TokenMatchesAnyQueryToken(string token, IReadOnlyList<string> queryTokens)
        => queryTokens.Any(queryToken =>
            string.Equals(queryToken, token, StringComparison.Ordinal)
            || BuildLexicalTokenVariants(queryToken)
                .DefaultIfEmpty(queryToken)
                .Any(variant => string.Equals(FoldDiacritics(variant), token, StringComparison.Ordinal))
            || BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Any(variant => string.Equals(FoldDiacritics(variant), queryToken, StringComparison.Ordinal)));


    private static bool TokenMatchesNormalizedText(string token, string normalizedText)
        => BuildLexicalTokenVariants(token)
            .DefaultIfEmpty(token)
            .Select(FoldDiacritics)
            .Distinct(StringComparer.Ordinal)
            .Any(variant => variant.Length >= 3 && ContainsExactNormalizedToken(normalizedText, variant));


    internal static bool LooksLikeStructuredAnswerText(string? value)
    {
        return StructuredContentLexicon.ContainsStructuredAnswerCue(value)
            || LooksLikeStructuredQuantityList(value)
            || LooksLikeDenseStructuredQuantityRun(value);
    }


    internal static bool LooksLikeStructuredQuantityList(string? value)
        => StructuredContentLexicon.LooksLikeStructuredQuantityList(value);


    internal static bool LooksLikeSourceListChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var urlCount = CountOccurrences(lower, "http://")
            + CountOccurrences(lower, "https://")
            + CountOccurrences(lower, "www.");
        if (urlCount >= 3)
            return true;

        var domainMarkers = CountOccurrences(lower, ".com")
            + CountOccurrences(lower, ".org")
            + CountOccurrences(lower, ".net")
            + CountOccurrences(lower, ".fr")
            + CountOccurrences(lower, ".ca")
            + CountOccurrences(lower, ".ch");

        return domainMarkers >= 5 && CountInlinePageNumberBoundaries(text) <= 2;
    }


    internal static bool LooksLikeDocumentOverviewChunk(RagMatch match)
    {
        var text = FoldDiacritics(match.Text ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasOverviewLanguage =
            text.Contains("ce livre", StringComparison.Ordinal)
            || text.Contains("ce guide", StringComparison.Ordinal)
            || text.Contains("ce document", StringComparison.Ordinal)
            || text.Contains("cet ouvrage", StringComparison.Ordinal)
            || text.Contains("this book", StringComparison.Ordinal)
            || text.Contains("this guide", StringComparison.Ordinal)
            || text.Contains("this document", StringComparison.Ordinal)
            || text.Contains("contains examples", StringComparison.Ordinal)
            || text.Contains("contient quelques exemples", StringComparison.Ordinal)
            || text.Contains("vous permettra", StringComparison.Ordinal)
            || text.Contains("permettra de", StringComparison.Ordinal)
            || text.Contains("introduction", StringComparison.Ordinal);

        if (!hasOverviewLanguage)
            return false;

        return !ContainsActionableDocumentProfileLanguage(text);
    }


    private static bool ContainsActionableDocumentProfileLanguage(string normalizedText)
        => ContainsAny(
            normalizedText,
            "procedure",
            "procedures",
            "materials",
            "material",
            "materiel",
            "materiaux",
            "materiales",
            "materiali",
            "materialien",
            "components",
            "component",
            "composants",
            "componentes",
            "componenti",
            "komponenten",
            "instructions",
            "instruction",
            "consignes",
            "instrucciones",
            "instrucoes",
            "istruzioni",
            "anweisungen",
            "steps",
            "step",
            "etapes",
            "pasos",
            "passos",
            "schritte",
            "tools",
            "outils",
            "herramientas",
            "ferramentas",
            "strumenti",
            "werkzeug",
            "equipment",
            "equipement",
            "equipamentos",
            "attrezzatura",
            "ausrustung");


    internal static bool LooksLikeNavigationalChunk(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return false;

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return true;

        var text = match.Text ?? string.Empty;
        var hasMeasuredSequentialContent = RetrievalContentClassifier.LooksLikeMeasuredSequentialContent(text);

        if (match.NavigationScore is >= 0.72
            && (match.ContentDensityScore is null or < 0.50)
            && !hasMeasuredSequentialContent)
            return true;

        if (RetrievalContentClassifier.IsNavigationChunkType(match.ChunkType))
            return true;

        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var foldedText = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";
        var hasStrongIndexMarker = HasStrongNavigationalMarker(folded, padded);
        var hasExplicitTocMarker = ContainsExplicitTableOfContentsMarker(folded, padded);
        var hasShortTocMarker = ContainsShortTableOfContentsMarker(padded)
            && (CountInlinePageNumberBoundaries(text) >= 3 || CountBulletMarkers(text) >= 8 || LooksLikeTitleListChunk(text));
        var hasStructuredProcedureBody = (foldedText.Contains("materials", StringComparison.Ordinal)
                || foldedText.Contains("materiaux", StringComparison.Ordinal)
                || foldedText.Contains("components", StringComparison.Ordinal)
                || foldedText.Contains("composants", StringComparison.Ordinal))
            && (foldedText.Contains("method", StringComparison.Ordinal)
                || foldedText.Contains("procedure", StringComparison.Ordinal)
                || foldedText.Contains("instructions", StringComparison.Ordinal)
                || foldedText.Contains("steps", StringComparison.Ordinal));
        if (hasStructuredProcedureBody && !hasStrongIndexMarker && !hasExplicitTocMarker && !hasShortTocMarker)
            return false;

        if (hasExplicitTocMarker
            || hasShortTocMarker
            || hasStrongIndexMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal)
            || padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (!hasStrongIndexMarker
                && padded.Contains(" index ", StringComparison.Ordinal)
                && CountBulletMarkers(text) < 8
                && CountInlinePageNumberBoundaries(text) < 5
                && !LooksLikeTitleListChunk(text))
                return false;

            return true;
        }

        if (CountInlinePageNumberBoundaries(text) >= 5 && !hasMeasuredSequentialContent)
            return true;

        return LooksLikeTitleListChunk(text);
    }


    internal static bool LooksLikeGlossaryChunk(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return false;

        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var definitionHeadings = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"(?:^|[\r\n\s])[\p{Lu}][\p{Lu}\p{Mn}\p{Pd}\s]{2,42}\s*:",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        if (definitionHeadings >= 4)
            return true;

        var glossaryLanguage = ContainsAny(
            $" {NormalizeQuery(folded)} ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " definition ",
            " definitions ");
        if (!glossaryLanguage)
            return false;

        var shortDefinitionSignals = CountOccurrences(folded, " signifie ")
            + CountOccurrences(folded, " designe ")
            + CountOccurrences(folded, " means ")
            + CountOccurrences(folded, " refers to ")
            + CountOccurrences(folded, " consiste a ");
        return shortDefinitionSignals >= 2 || definitionHeadings >= 2;
    }


    internal static bool ShouldAllowGlossaryResults(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {NormalizeQuery(FoldDiacritics(query).ToLowerInvariant())} ";
        return ContainsAny(
            normalized,
            " definition ",
            " definir ",
            " definis ",
            " que veut dire ",
            " signifie ",
            " meaning ",
            " define ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " terme ",
            " term ");
    }


    internal static bool LooksLikeStrongNavigationalChunk(RagMatch match)
    {
        if (match.NavigationScore is >= 0.85
            && (match.ContentDensityScore is null or < 0.45))
            return true;

        if (RetrievalContentClassifier.IsNavigationChunkType(match.ChunkType))
            return true;

        var text = match.Text ?? string.Empty;
        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";

        return HasStrongNavigationalMarker(folded, padded)
            || folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || folded.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || padded.Contains(" contents ", StringComparison.Ordinal)
            || padded.Contains(" sumario ", StringComparison.Ordinal)
            || padded.Contains(" sommario ", StringComparison.Ordinal)
            || padded.Contains(" toc ", StringComparison.Ordinal)
            || (padded.Contains(" indice ", StringComparison.Ordinal) && CountInlinePageNumberBoundaries(text) >= 3)
            || CountInlinePageNumberBoundaries(text) >= 8;
    }


    internal static bool ContainsExplicitTableOfContentsMarker(string foldedText, string paddedNormalizedText)
        => foldedText.Contains("sommaire", StringComparison.Ordinal)
            || foldedText.Contains("table des matieres", StringComparison.Ordinal)
            || foldedText.Contains("table of contents", StringComparison.Ordinal)
            || foldedText.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" contents ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice general ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenido ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenidos ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de materias ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice analitico ", StringComparison.Ordinal);


    internal static bool ContainsShortTableOfContentsMarker(string paddedNormalizedText)
        => paddedNormalizedText.Contains(" sumario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" sommario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" toc ", StringComparison.Ordinal);


    internal static bool HasStrongNavigationalMarker(string foldedText, string paddedNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(foldedText) || string.IsNullOrWhiteSpace(paddedNormalizedText))
            return false;

        if (foldedText.Contains("fiche-index", StringComparison.Ordinal)
            || foldedText.Contains("fiche index", StringComparison.Ordinal))
            return true;

        return System.Text.RegularExpressions.Regex.IsMatch(
            paddedNormalizedText,
            @"\b(?:index|liste|list|catalogue|catalog|inventaire|inventory)\s+(?:des?|de|du|d['’]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:index|liste|list|catalogue|catalog|inventory)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }


    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');


    internal static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var digitRun = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digitRun++;
                continue;
            }

            if (digitRun is > 0 and <= 4 && char.IsLetter(ch) && char.IsUpper(ch))
                count++;

            digitRun = 0;
        }

        return count;
    }


    internal static bool LooksLikeTitleListChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;

        var words = 0;
        var capitalizedStarts = 0;
        var sentenceMarkers = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is '.' or '!' or '?' or ';' or ':' or '\u2022')
                sentenceMarkers++;

            if (char.IsLetter(ch))
            {
                if (!inWord)
                {
                    words++;
                    if (char.IsUpper(ch))
                        capitalizedStarts++;
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return (words >= 24
                && capitalizedStarts >= Math.Max(12, words / 3)
                && sentenceMarkers <= 2)
            || (words >= 40
                && capitalizedStarts >= 20
                && sentenceMarkers <= 2)
            || (CountLowerToUpperTransitions(text) >= 8 && sentenceMarkers <= 3);
    }


    private static int CountLowerToUpperTransitions(string text)
    {
        var count = 0;
        var previousWasLower = false;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch) && previousWasLower)
                count++;

            previousWasLower = char.IsLower(ch);
        }

        return count;
    }


    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(needle))
            return 0;

        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0)
                break;

            count++;
            index = found + needle.Length;
        }

        return count;
    }


    internal static List<RagMatch> RerankDenseMatches(IReadOnlyList<RagMatch> matches)
        => matches
            .Select(match => new
            {
                Match = match,
                Score = ApplyChunkExtractionQualityScorePenalty(match.Score + ComputeDenseBoost(match), match)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .Select(item => item.Match with { Score = item.Score })
            .ToList();


    internal static List<RagMatch> ApplyRerankScores(
        IReadOnlyList<RagMatch> originalCandidates,
        IReadOnlyList<TeiClient.RerankItem> rerankedItems,
        int rerankedPrefixCount)
    {
        if (originalCandidates.Count == 0 || rerankedItems.Count == 0 || rerankedPrefixCount <= 0)
            return originalCandidates.ToList();

        var limitedPrefixCount = Math.Min(rerankedPrefixCount, originalCandidates.Count);
        var prefix = originalCandidates.Take(limitedPrefixCount).ToArray();
        var suffix = originalCandidates.Skip(limitedPrefixCount).ToArray();
        var byIndex = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Score).First().Score);

        if (byIndex.Count == 0)
            return originalCandidates.ToList();

        var orderedScores = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .Select(item => item.Score)
            .OrderBy(score => score)
            .ToArray();
        var minScore = orderedScores.Length == 0 ? 0.0 : orderedScores.First();
        var maxScore = orderedScores.Length == 0 ? 0.0 : orderedScores.Last();

        var rerankedPrefix = byIndex
            .Select(pair =>
            {
                var match = prefix[pair.Key];
                var normalizedRerank = NormalizeRerankScore(pair.Value, minScore, maxScore);
                var blended = Math.Min(1.02, (match.Score * 0.35) + (normalizedRerank * 0.65));
                return match with
                {
                    Score = blended,
                    RerankScore = pair.Value
                };
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.RerankScore)
            .ToList();

        var untouchedPrefix = Enumerable.Range(0, prefix.Length)
            .Where(index => !byIndex.ContainsKey(index))
            .Select(index => prefix[index])
            .ToList();

        var finalList = new List<RagMatch>(originalCandidates.Count);
        finalList.AddRange(rerankedPrefix);
        finalList.AddRange(untouchedPrefix);
        finalList.AddRange(suffix);
        return finalList;
    }


    private static double ComputeDenseBoost(RagMatch match)
    {
        double boost = 0.0;
        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            boost += 0.02;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            boost += 0.01;

        if (!string.IsNullOrWhiteSpace(match.HeadingPath))
            boost += 0.005;
        if (IsContextualEmbeddingBasis(match.EmbeddingBasis))
            boost += 0.005;

        return boost;
    }


    private static bool IsContextualEmbeddingBasis(string? embeddingBasis)
        => !string.IsNullOrWhiteSpace(embeddingBasis)
           && embeddingBasis.StartsWith("contextual_text_", StringComparison.Ordinal);


    internal static double NormalizeRerankScore(double rawScore, double minScore, double maxScore)
    {
        if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;
        if (maxScore <= minScore)
            return 1.0;

        var normalized = (rawScore - minScore) / (maxScore - minScore);
        return Math.Clamp(normalized, 0.0, 1.0);
    }


    internal static double NormalizeSparseScore(double rawScore)
    {
        if (rawScore <= 0.0 || double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;

        var normalized = 0.45 + Math.Min(0.42, Math.Log10(1 + (rawScore * 1000.0)) * 0.24);
        return Math.Min(0.92, normalized);
    }


    internal static double NormalizeDocumentProfileScore(double sparseRank, int matchCount)
    {
        if (matchCount <= 0)
            return 0.32;

        var lexicalScore = NormalizeSparseScore(sparseRank);
        var matchScore = Math.Min(0.28, matchCount * 0.045);
        var combined = Math.Max(lexicalScore, 0.48 + matchScore);
        return Math.Clamp(combined, 0.0, 0.86);
    }


    internal static double ComputeDocumentProfileSpecificityBoost(
        string query,
        string? candidateText,
        IReadOnlyList<string> peerCandidateTexts)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || peerCandidateTexts.Count == 0)
            return 0.0;

        var tokens = BuildDocumentProfileSpecificityTokens(query);
        if (tokens.Count == 0)
            return 0.0;

        var normalizedCandidate = NormalizeForLexicalSignal(candidateText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var normalizedPeers = peerCandidateTexts
            .Select(NormalizeForLexicalSignal)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (normalizedPeers.Length == 0)
            return 0.0;

        var rawBoost = 0.0;
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Count == 0)
                continue;

            if (!variants.Any(variant => normalizedCandidate.Contains(variant, StringComparison.Ordinal)))
                continue;

            var documentFrequency = normalizedPeers.Count(peer =>
                variants.Any(variant => peer.Contains(variant, StringComparison.Ordinal)));
            if (documentFrequency <= 0)
                continue;

            var inverseFrequency = Math.Log((normalizedPeers.Length + 1.0) / (documentFrequency + 0.5)) + 1.0;
            var tokenWeight =
                IsReferenceLikeLookupTerm(token) ? 1.45 :
                token.Length >= 10 ? 1.25 :
                token.Length >= 7 ? 1.10 :
                1.0;
            rawBoost += inverseFrequency * tokenWeight;
        }

        return Math.Clamp(rawBoost * 0.025, 0.0, 0.24);
    }


    internal static double ComputeDocumentProfileConstraintCoverageScore(
        IReadOnlyList<string> tokens,
        string normalizedCandidate)
    {
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var matchedWeight = 0.0;
        var totalWeight = 0.0;
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Count == 0)
                continue;

            var weight = GetDocumentProfileConstraintTokenWeight(token);
            totalWeight += weight;
            if (variants.Any(variant => ContainsExactNormalizedToken(normalizedCandidate, variant)))
            {
                matchedWeight += weight;
                if (IsAudienceConstraintToken(token))
                {
                    totalWeight += 2.0;
                    if (ContainsAudienceConstraintPhrase(normalizedCandidate, token))
                        matchedWeight += 2.0;
                }
            }
        }

        return totalWeight <= 0.0
            ? 0.0
            : Math.Clamp(matchedWeight / totalWeight, 0.0, 1.0);
    }


    internal static double ComputeDocumentProfileSelectionConstraintCoverage(
        IReadOnlyList<string> tokens,
        RagMatch match)
    {
        if (tokens.Count == 0 || !IsDocumentProfileMatch(match))
            return 0.0;

        var coverage = ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            NormalizeForLexicalSignal(GetTitleSignalText(match)));
        if (tokens.Any(IsAudienceConstraintToken)
            && !ContainsAnyDocumentProfileConstraintToken(tokens.Where(IsAudienceConstraintToken), NormalizeForLexicalSignal(GetTitleSignalText(match))))
        {
            return 0.0;
        }

        if (coverage >= 0.34)
            return coverage;

        return match.MatchedContentCards is { Count: > 0 } && coverage >= 0.20
            ? Math.Max(coverage, 0.34)
            : 0.0;
    }


    private static bool ContainsAnyDocumentProfileConstraintToken(
        IEnumerable<string> tokens,
        string normalizedCandidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Any(variant => ContainsExactNormalizedToken(normalizedCandidate, variant)))
                return true;
        }

        return false;
    }


    private static double GetDocumentProfileConstraintTokenWeight(string token)
        => token switch
        {
            "enfant" or "enfants" or "children" or "child" or "kids" or "apprenti" or "apprentis"
                or "apprentice" or "apprentices" or "debutant" or "debutants" or "beginner" or "beginners" => 2.0,
            "materiel" or "material" or "materials" or "risque" or "risques" or "risk" or "risks"
                or "failure" or "ratage" or "temps" or "time" or "duration" or "duree" => 1.5,
            _ => 1.0
        };


    internal static bool IsAudienceConstraintToken(string token)
        => token is "enfant" or "enfants" or "children" or "child" or "kids" or "apprenti" or "apprentis"
            or "apprentice" or "apprentices" or "debutant" or "debutants" or "beginner" or "beginners";


    private static bool ContainsAudienceConstraintPhrase(string normalizedCandidate, string token)
    {
        if (token is "enfant" or "enfants" or "children" or "child" or "kids")
        {
            return ContainsAny(
                normalizedCandidate,
                " pour enfants ",
                " pour les enfants ",
                " aux enfants ",
                " avec les enfants ",
                " destine aux enfants ",
                " destine pour enfants ",
                " adaptes aux enfants ",
                " adapte aux enfants ",
                " for children ",
                " for kids ",
                " with children ",
                " intended for children ",
                " designed for children ",
                " adapted for children ",
                " suitable for children ");
        }

        if (token is "apprenti" or "apprentis" or "apprentice" or "apprentices" or "debutant" or "debutants" or "beginner" or "beginners")
        {
            return ContainsAny(
                normalizedCandidate,
                " pour apprentis ",
                " pour les apprentis ",
                " pour debutants ",
                " pour les debutants ",
                " aux apprentis ",
                " aux debutants ",
                " destine aux apprentis ",
                " destine aux debutants ",
                " for apprentices ",
                " for beginners ",
                " intended for apprentices ",
                " intended for beginners ",
                " designed for apprentices ",
                " designed for beginners ",
                " suitable for apprentices ",
                " suitable for beginners ");
        }

        return false;
    }

}
