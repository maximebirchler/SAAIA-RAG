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

    private static bool LooksLikeParameterLookupRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:vitesse|vitesses|speed|speeds|temperature|temperatures|reglage|reglages|setting|settings|parametre|parametres|parameter|parameters|programme|program)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStructuredItemCardRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (Regex.IsMatch(
                normalized,
                @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|components?|composants?|preparation|operation|workflow|execution|technique|materiel|material|materials|procedure|process|method|methode)\b",
                RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            normalized,
            @"\b(?:fiche|card|item|items|element|elements|etape|etapes|steps|temps|time|source|sources|procedure|process|values?|valeurs?)\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildSourceBackedExactItemParameterAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(SourceBackedLabel(language, "Parametres visibles dans les extraits :", "Visible parameters in the excerpts:", "Parametros visibles en los extractos:", "Parametros visiveis nos excertos:", "Sichtbare Parameter in den Auszuegen:", "Parametri visibili negli estratti:"));

        foreach (var hit in hits.Take(3))
        {
            var facts = ExtractParameterFacts(GetBestRagEvidenceText(hit)).Take(8).ToArray();
            if (facts.Length == 0)
                continue;

            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(string.Join("; ", facts));
        }

        if (!Regex.IsMatch(sb.ToString(), @"\s(?:p\.|S\.)\d+", RegexOptions.CultureInvariant))
        {
            foreach (var hit in hits.Take(2))
            {
                var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
                sb.Append("- ");
                sb.Append(docLabel);
                sb.Append(' ');
                sb.Append(SourceBackedPagePrefix(language));
                sb.Append(hit.PageStart);
                sb.Append(" : ");
                sb.AppendLine(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260));
            }
        }

        AppendControlExcerpt(sb, language, hits);
        return sb.ToString().TrimEnd();
    }

    private static string BuildSourceBackedExactItemCardAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string header)
    {
        var cardHits = hits
            .Where(hit => ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit)
                || RagHitContainsRequestedTitle(hit, requestedTitle)
                || RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit))
            .OrderByDescending(hit => ExactItemEvidenceStartsWithRequestedTitle(requestedTitle, hit))
            .ThenByDescending(hit => HasStrongExactItemTitleAnchor(requestedTitle, hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit))
            .ThenByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
            .ThenByDescending(hit => hit.Score)
            .ToList();
        var structuredCompanions = hits
            .Where(hit => hit.PageEnd > hit.PageStart || !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => ComputeExactItemCardCompletenessCueScore(hit) >= 8 || hit.PageEnd > hit.PageStart)
            .Where(hit => !cardHits.Any(existing => SameRagHitRange(existing, hit)))
            .ToList();
        if (structuredCompanions.Count > 0)
        {
            cardHits = cardHits
                .Concat(structuredCompanions)
                .OrderByDescending(hit => ExactItemEvidenceStartsWithRequestedTitle(requestedTitle, hit))
                .ThenByDescending(hit => HasStrongExactItemTitleAnchor(requestedTitle, hit)
                    || IsUsableExactItemNavigationOverride(requestedTitle, hit))
                .ThenByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }

        if (cardHits.Count == 0)
        {
            cardHits = hits
                .OrderByDescending(hit => ExactItemEvidenceStartsWithRequestedTitle(requestedTitle, hit))
                .ThenByDescending(hit => HasStrongExactItemTitleAnchor(requestedTitle, hit)
                    || IsUsableExactItemNavigationOverride(requestedTitle, hit))
                .ThenByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }

        var primary = cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && !LooksLikeNavigationOnlyHit(hit)
                && RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit)
                && ComputeExactItemCardCompletenessCueScore(hit) >= 4)
            ?? cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && IsUsableExactItemNavigationOverride(requestedTitle, hit)
                && ComputeExactItemCardCompletenessCueScore(hit) >= 4)
            ?? cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && !LooksLikeNavigationOnlyHit(hit)
                && RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit))
            ?? cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && IsUsableExactItemNavigationOverride(requestedTitle, hit))
            ?? cardHits.First();
        var primaryDoc = string.IsNullOrWhiteSpace(primary.DocName) ? primary.DocPath : primary.DocName;
        var evidenceHits = SelectExactItemCardEvidenceHits(cardHits, primary);
        var evidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var itemizedEvidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit))));
        var procedureEvidence = CollapseWhitespace(string.Join(' ', evidenceHits
            .OrderByDescending(hit => ComputeExactItemProcedureEvidenceScore(requestedTitle, hit))
            .Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var visibleDurationsAndQuantities = ExtractVisibleDurationsAndQuantities(procedureEvidence).Take(8).ToArray();
        var itemizedFacts = ExtractItemizedQuantityFacts(itemizedEvidence).Take(10).ToArray();
        var steps = ExtractProcedureSteps(procedureEvidence).Take(10).ToArray();
        var cardEvidenceFacts = ExtractContentCardEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardQuantityEvidenceFacts = ExtractContentCardQuantityEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardGenericEvidenceFacts = ExtractContentCardGenericEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardNonScalableReasons = ExtractContentCardNonScalableReasons(evidenceHits).Take(8).ToArray();
        var hasCardQuantityEvidence = HasContentCardQuantityEvidence(evidenceHits);
        var hasAnyCardEvidence = HasContentCardEvidence(evidenceHits);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(SourceBackedLabel(language, "Fiche documentÃƒÂ©e :", "Documented card:", "Ficha documentada:", "Ficha documentada:", "Belegte Karte:", "Scheda documentata:"));
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Source principale", "Main source", "Fuente principal", "Fonte principal", "Hauptquelle", "Fonte principale"));
        sb.Append(" : ");
        sb.Append(primaryDoc);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.AppendLine(primary.PageStart.ToString(CultureInfo.InvariantCulture));

        if (!ShouldIncludeRawSourceExcerptForAnswerLanguage(language, primary))
        {
            sb.Append("- ");
            sb.Append(SourceBackedLabel(
                language,
                "Contenu visible",
                "Visible content",
                "Contenido visible",
                "Conteudo visivel",
                "Sichtbarer Inhalt",
                "Contenuto visibile"));
            sb.Append(" : ");
            sb.AppendLine(SourceBackedLabel(
                language,
                "les ÃƒÂ©lÃƒÂ©ments, valeurs, temps et ÃƒÂ©tapes doivent ÃƒÂªtre lus sur la page citÃƒÂ©e dans la langue du document.",
                "items, values, timing and steps should be read on the cited page in the document language.",
                "los elementos, valores, tiempos y pasos deben leerse en la pagina citada, en el idioma del documento.",
                "os elementos, valores, tempos e etapas devem ser lidos na pagina citada, na lingua do documento.",
                "Elemente, Werte, Zeiten und Schritte sind auf der zitierten Seite in der Dokumentsprache zu lesen.",
                "elementi, valori, tempi e passaggi vanno letti nella pagina citata, nella lingua del documento."));
            return sb.ToString().TrimEnd();
        }

        if (hasAnyCardEvidence)
        {
            if (hasCardQuantityEvidence)
            {
                AppendFactList(sb, SourceBackedLabel(language, "DurÃƒÂ©es / quantitÃƒÂ©s visibles", "Visible durations / quantities", "Duraciones / cantidades visibles", "Duracoes / quantidades visiveis", "Sichtbare Dauern / Mengen", "Durate / quantita visibili"), cardQuantityEvidenceFacts, language);
            }

            if (cardGenericEvidenceFacts.Length > 0 || !hasCardQuantityEvidence)
            {
                var genericFacts = cardGenericEvidenceFacts.Length > 0 ? cardGenericEvidenceFacts : cardEvidenceFacts;
                AppendFactList(sb, SourceBackedLabel(language, "Informations visibles", "Visible information", "Informacion visible", "Informacao visivel", "Sichtbare Informationen", "Informazioni visibili"), genericFacts, language);
            }

            if (cardNonScalableReasons.Length > 0)
            {
                AppendFactList(sb, SourceBackedLabel(language, "Contraintes non adaptables", "Non-scalable constraints", "Restricciones no adaptables", "Restricoes nao adaptaveis", "Nicht skalierbare Vorgaben", "Vincoli non scalabili"), cardNonScalableReasons, language);
            }
        }
        else if (primary.MatchedContentCards is { Count: > 0 }
                 || evidenceHits.Any(static hit => hit.MatchedContentCards is { Count: > 0 }))
        {
            var visibleInfo = visibleDurationsAndQuantities
                .Concat(itemizedFacts)
                .Concat(steps)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            AppendFactList(sb, SourceBackedLabel(language, "Informations visibles", "Visible information", "Informacion visible", "Informacao visivel", "Sichtbare Informationen", "Informazioni visibili"), visibleInfo, language);
        }
        else
        {
            AppendFactList(sb, SourceBackedLabel(language, "DurÃƒÂ©es / quantitÃƒÂ©s visibles", "Visible durations / quantities", "Duraciones / cantidades visibles", "Duracoes / quantidades visiveis", "Sichtbare Dauern / Mengen", "Durate / quantita visibili"), visibleDurationsAndQuantities, language);
            AppendFactList(sb, SourceBackedLabel(language, "Ãƒâ€°lÃƒÂ©ments / quantitÃƒÂ©s visibles", "Visible items / quantities", "Elementos / cantidades visibles", "Elementos / quantidades visiveis", "Sichtbare Elemente / Mengen", "Elementi / quantita visibili"), itemizedFacts, language);
            AppendFactList(sb, SourceBackedLabel(language, "Ãƒâ€°tapes visibles", "Visible steps", "Pasos visibles", "Passos visiveis", "Sichtbare Schritte", "Passaggi visibili"), steps, language);
        }
        AppendExactItemControlExcerpt(sb, language, requestedTitle, cardHits);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<RagHitSummary> SelectExactItemCardEvidenceHits(IReadOnlyList<RagHitSummary> cardHits, RagHitSummary primary)
    {
        var primaryDocPath = (primary.DocPath ?? string.Empty).Replace('\\', '/');
        var evidenceHits = cardHits
            .Where(hit => string.Equals((hit.DocPath ?? string.Empty).Replace('\\', '/'), primaryDocPath, StringComparison.OrdinalIgnoreCase)
                && (hit.PageStart == primary.PageStart || IsLinkedExactItemEvidenceHit(primary, hit)))
            .OrderByDescending(hit => SameRagHitRange(hit, primary))
            .ThenBy(hit => Math.Abs(hit.PageStart - primary.PageStart))
            .ThenByDescending(ComputeExactItemCardCompletenessCueScore)
            .Take(4)
            .ToArray();

        return evidenceHits.Length > 0
            ? evidenceHits
            : cardHits.Take(1).ToArray();
    }

    private static bool IsLinkedExactItemEvidenceHit(RagHitSummary primary, RagHitSummary candidate)
    {
        if (SameRagHitRange(primary, candidate))
            return true;

        if (!string.Equals(primary.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (LooksLikeNavigationOnlyHit(candidate) || LooksLikeLowSignalContentCandidateHit(candidate))
            return false;

        if (IsChunkLinkedToPrimary(primary, candidate))
            return true;

        var primarySection = NormalizeLexicalLookup(
            string.IsNullOrWhiteSpace(primary.SectionTitle) ? primary.HeadingPath : primary.SectionTitle);
        var candidateSection = NormalizeLexicalLookup(
            string.IsNullOrWhiteSpace(candidate.SectionTitle) ? candidate.HeadingPath : candidate.SectionTitle);
        if (!string.IsNullOrWhiteSpace(primarySection)
            && primarySection.Length >= 4
            && string.Equals(primarySection, candidateSection, StringComparison.Ordinal)
            && candidate.PageStart <= primary.PageEnd + 2
            && candidate.PageEnd >= Math.Max(1, primary.PageStart - 2))
        {
            return true;
        }

        return false;
    }

    private static bool IsChunkLinkedToPrimary(RagHitSummary primary, RagHitSummary candidate)
    {
        static bool SameChunk(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left)
               && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        return SameChunk(primary.NextChunkId, candidate.ChunkId)
               || SameChunk(primary.PrevChunkId, candidate.ChunkId)
               || SameChunk(primary.SameSectionChunkId, candidate.ChunkId)
               || SameChunk(candidate.NextChunkId, primary.ChunkId)
               || SameChunk(candidate.PrevChunkId, primary.ChunkId)
               || SameChunk(candidate.SameSectionChunkId, primary.ChunkId);
    }

    private static int ComputeExactItemCardEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var evidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var itemizedEvidence = GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit);
        var displayTitleScore = ComputeBestSourceBackedDisplayTitleScore(requestedTitle, hit);
        var score = 0;
        if (ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit))
            score += 20;
        else if (RagHitContainsRequestedTitle(hit, requestedTitle))
            score += ComputeExactItemCardCompletenessCueScore(hit) >= 4 ? 10 : -12;
        if (displayTitleScore >= 40)
            score += 60 + displayTitleScore;
        else if (ExtractRequestedTitleSignalTerms(NormalizeLexicalLookup(requestedTitle)).Length >= 3 && displayTitleScore < 20)
            score -= 40;
        score += ComputeExactItemHeadTermEvidenceScore(requestedTitle, hit);
        score += ComputeExactItemAnchorStrengthScore(requestedTitle, hit);
        score += ComputeRequestedDocumentTypeAnchorScore(requestedTitle, hit);
        score += ComputeExactVisibleTitleMatchScore(requestedTitle, hit);
        score += ExtractItemizedQuantityFacts(itemizedEvidence).Length * 4;
        score += ExtractProcedureSteps(evidence).Length * 3;
        score += ExtractVisibleDurationsAndQuantities(evidence).Length;
        score += ExtractContentCardEvidenceFacts(new[] { hit }).Length * 4;
        if (LooksLikeNavigationOnlyHit(hit))
            score -= 120;
        if (LooksLikePageReferenceOnlyHit(hit))
            score -= 220;
        return score;
    }

    private static int ComputeExactItemHeadTermEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var titleTerms = ExtractRequestedTitleSignalTerms(NormalizeLexicalLookup(requestedTitle));
        if (titleTerms.Length < 2)
            return 0;

        var headTerms = titleTerms.Take(Math.Min(2, titleTerms.Length)).ToArray();
        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var titleText = NormalizeLexicalLookup(string.Join(' ', ExtractSourceBackedTitleCandidates(hit)));

        var matchedContentHeadTerms = headTerms.Count(term => content.Contains(term, StringComparison.Ordinal));
        var matchedTitleHeadTerms = headTerms.Count(term => titleText.Contains(term, StringComparison.Ordinal));

        var score = matchedContentHeadTerms * 45 + matchedTitleHeadTerms * 25;
        if (matchedContentHeadTerms == headTerms.Length)
            score += 70;
        if (matchedTitleHeadTerms == headTerms.Length)
            score += 50;
        if (titleTerms.Length >= 3 && matchedContentHeadTerms == 0 && matchedTitleHeadTerms == 0)
            score -= 160;

        return score;
    }

    private static int ComputeBestSourceBackedDisplayTitleScore(string requestedTitle, RagHitSummary hit)
        => ExtractSourceBackedTitleCandidates(hit)
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Select(title => ComputeSourceBackedDisplayTitleScore(requestedTitle, title))
            .DefaultIfEmpty(0)
            .Max();

    private static int ComputeExactItemAnchorStrengthScore(string requestedTitle, RagHitSummary hit)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return 0;

        static bool AllTermsIn(string text, IReadOnlyList<string> terms)
        {
            var normalized = NormalizeLexicalLookup(text);
            return !string.IsNullOrWhiteSpace(normalized)
                && terms.All(term => normalized.Contains(term, StringComparison.Ordinal));
        }

        var score = 0;
        var cardTitles = string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>());
        if (AllTermsIn(cardTitles, titleTerms))
            score += 90;

        var contextualTitleText = string.Join(' ', ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty));
        if (AllTermsIn(contextualTitleText, titleTerms))
            score += 80;

        if (AllTermsIn($"{hit.SectionTitle} {hit.HeadingPath}", titleTerms))
            score += 60;

        var excerptLead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        if (excerptLead.Length > 260)
            excerptLead = excerptLead[..260];
        if (AllTermsIn(excerptLead, titleTerms))
            score += 45;

        if (AllTermsIn(hit.Excerpt ?? string.Empty, titleTerms))
            score += 25;

        if (score == 0 && AllTermsIn(GetBestExactItemEvidenceText(requestedTitle, hit), titleTerms))
            score += 8;

        return score;
    }

    private static bool LooksLikePageReferenceOnlyHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasStructuredEvidence = Regex.IsMatch(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|operation|workflow|execution|procedure|etapes?|steps?|\d+\s*(?:g|kg|ml|cl|l))\b",
            RegexOptions.CultureInvariant);
        if (hasStructuredEvidence)
            return false;

        return Regex.IsMatch(
            text,
            @"\b[\p{L}'\-]{4,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{2,3}\s+[\p{L}'\-]{4,}",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeExactItemReferenceOnlyHit(string requestedTitle, RagHitSummary hit)
    {
        if (LooksLikeExactItemIndexAssetOnlyHit(requestedTitle, hit))
            return true;

        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var hasStructuredDetails = Regex.IsMatch(
            evidence,
            @"\b(?:" + ExactItemStructureHeadingPattern + @")\b|\b\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?|s|sec(?:onde)?s?|(?:\u00b0|deg|degres?)\s*c)\b|(?:^|\s)[1-9][\.)]\s+",
            RegexOptions.CultureInvariant);
        if (hasStructuredDetails)
            return false;

        if (ExactItemEvidenceStartsWithRequestedTitle(requestedTitle, hit)
            && Regex.IsMatch(
                evidence,
                @"\b\d+\s*(?:min(?:ute)?s?|h|heures?|hours?|s|sec(?:onde)?s?|(?:\u00b0|deg|degres?)\s*c)\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            return false;
        }

        if (LooksLikeNavigationOnlyHit(hit) || LooksLikePageReferenceOnlyHit(hit))
            return true;

        var titleIndex = FindApproximateRequestedTitleIndex(requestedTitle, evidence);
        if (titleIndex < 0)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                titleIndex = FindApproximateRequestedTitleIndex(variant, evidence);
                if (titleIndex >= 0)
                    break;
            }
        }

        if (!ExactItemEvidenceStartsWithRequestedTitle(requestedTitle, hit)
            && LooksLikeIndexAssetTail(evidence))
        {
            return true;
        }

        if (titleIndex < 0)
            return false;

        var titlePageReferenceCount = Regex.Matches(
            evidence,
            @"\b[\p{L}'\-]{3,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{1,3}\b",
            RegexOptions.CultureInvariant).Count;
        return titlePageReferenceCount >= 2;
    }

    private static bool LooksLikeIndexAssetTail(string evidence)
    {
        var normalized = NormalizeLexicalLookup(evidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var indexMatch = Regex.Match(
            normalized,
            @"\b(?:index|sommaire|table\s+of\s+contents|indice|inhalt)\b",
            RegexOptions.CultureInvariant);
        if (!indexMatch.Success)
            return false;

        var indexAtTail = indexMatch.Index >= normalized.Length * 0.35 || normalized.Length - indexMatch.Index <= 520;
        if (!indexAtTail)
            return false;

        var tailStart = Math.Min(indexMatch.Index, Math.Max(0, evidence.Length - 1));
        var tail = evidence[tailStart..];
        var assetRefCount = Regex.Matches(
            tail,
            @"(?i)\b(?:[a-z]{2,}\d{4,}[a-z0-9_-]*|[a-z0-9]{4,}[_-][a-z0-9][a-z0-9_-]{5,}|[a-z]{2,4}\d*[_-][a-z0-9_-]{6,})\b",
            RegexOptions.CultureInvariant).Count;
        var multiSeparatorTokenCount = Regex.Matches(
            tail,
            @"\b[\p{L}\p{N}]+(?:[_-][\p{L}\p{N}]+){2,}\b",
            RegexOptions.CultureInvariant).Count;

        return assetRefCount + multiSeparatorTokenCount >= 2;
    }

    private static bool LooksLikeExactItemIndexAssetOnlyHit(string requestedTitle, RagHitSummary hit)
    {
        var candidates = new[]
            {
                hit.Excerpt,
                hit.FullText,
                string.IsNullOrWhiteSpace(hit.ContextualSnippet) ? null : StripContextualMetadataForEvidence(hit.ContextualSnippet!),
                GetBestExactItemEvidenceText(requestedTitle, hit)
            }
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => CollapseWhitespace(text!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        return candidates.Any(candidate =>
        {
            if (Regex.IsMatch(
                    candidate,
                    @"(?i)\[\s*index\s*:?\s*\].{0,700}\b[a-z]{2,}\d{4,}[a-z0-9_-]*",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }

            if (LooksLikeExactItemIndexAssetOnlyText(requestedTitle, candidate))
                return true;

            var normalizedCandidate = NormalizeLexicalLookup(candidate);
            return LooksLikeIndexAssetTail(candidate)
                   && !TextStartsWithRequestedTitlePhrase(normalizedCandidate, normalizedTitle);
        });
    }

    private static bool LooksLikeExactItemIndexAssetOnlyText(string requestedTitle, string evidence)
    {
        if (evidence.Length < 80)
            return false;

        var titleIndex = FindApproximateRequestedTitleIndex(requestedTitle, evidence);
        if (titleIndex < 0)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                titleIndex = FindApproximateRequestedTitleIndex(variant, evidence);
                if (titleIndex >= 0)
                    break;
            }
        }
        if (titleIndex < 0)
            return false;

        var titleAtTail = titleIndex >= evidence.Length * 0.45 || evidence.Length - titleIndex <= 420;
        if (!titleAtTail)
            return false;

        var afterTitle = evidence[Math.Min(titleIndex, evidence.Length)..];
        var normalizedAfterTitle = NormalizeLexicalLookup(afterTitle);
        var hasUsableDetailsAfterTitle = Regex.IsMatch(
            normalizedAfterTitle,
            @"\b(?:" + ExactItemStructureHeadingPattern + @")\b|\b\d+\s*(?:g|kg|ml|cl|l)\b|(?:^|\s)[1-9][\.)]\s+",
            RegexOptions.CultureInvariant);
        if (hasUsableDetailsAfterTitle)
            return false;

        var hasIndexCue = Regex.IsMatch(
            normalizedAfterTitle,
            @"\b(?:index|sommaire|table\s+of\s+contents|indice|inhalt)\b",
            RegexOptions.CultureInvariant);
        var assetRefCount = Regex.Matches(
            afterTitle,
            @"(?i)\b(?:[a-z]{2,}\d{4,}[a-z0-9_-]*|[a-z0-9]{4,}[_-][a-z0-9][a-z0-9_-]{5,}|[a-z]{2,4}\d*[_-][a-z0-9_-]{6,})\b",
            RegexOptions.CultureInvariant).Count;
        var multiSeparatorTokenCount = Regex.Matches(
            afterTitle,
            @"\b[\p{L}\p{N}]+(?:[_-][\p{L}\p{N}]+){2,}\b",
            RegexOptions.CultureInvariant).Count;

        return (hasIndexCue && assetRefCount + multiSeparatorTokenCount >= 2) || assetRefCount >= 3;
    }

    private static int FindApproximateRequestedTitleIndex(string requestedTitle, string text)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedTitle))
            return -1;

        var directIndex = normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (directIndex >= 0)
            return Math.Min(directIndex, Math.Max(0, text.Length - 1));

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return -1;

        var loosePattern = string.Join(@"[\s_\-./]+", titleTerms.Select(Regex.Escape));
        var looseMatch = Regex.Match(normalizedText, loosePattern, RegexOptions.CultureInvariant);
        if (looseMatch.Success)
            return Math.Min(looseMatch.Index, Math.Max(0, text.Length - 1));

        var cursor = 0;
        var firstIndex = -1;
        foreach (var term in titleTerms)
        {
            var termIndex = normalizedText.IndexOf(term, cursor, StringComparison.Ordinal);
            if (termIndex < 0)
                return -1;
            if (firstIndex < 0)
                firstIndex = termIndex;
            cursor = termIndex + term.Length;
        }

        return Math.Min(firstIndex, Math.Max(0, text.Length - 1));
    }

    private static int ComputeExactVisibleTitleMatchScore(string requestedTitle, RagHitSummary hit)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return 0;

        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return 0;

        var escapedTitle = Regex.Escape(normalizedTitle);
        if (Regex.IsMatch(
                evidence,
                $@"(?<![\p{{L}}\p{{N}}]){escapedTitle}(?:\s*(?:$|[\.:;\u2022\u00b7])|(?=\s*(?:pour|items?|elements?|quantities?|preparation|operation|workflow|execution|procedure|temps|time|repos|rest)\b)|(?=(?:pour|items?|elements?|quantities?|preparation|operation|workflow|execution|procedure|temps|time|repos|rest)\b))",
                RegexOptions.CultureInvariant))
        {
            return 40;
        }

        if (evidence.Equals(normalizedTitle, StringComparison.Ordinal)
            || evidence.StartsWith(normalizedTitle + " ", StringComparison.Ordinal))
        {
            return 40;
        }

        return evidence.Contains(normalizedTitle, StringComparison.Ordinal) ? 4 : 0;
    }

    private static int ComputeExactItemProcedureEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var evidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var score = ExtractProcedureSteps(evidence).Length * 6
            + ExtractVisibleDurationsAndQuantities(evidence).Length * 2;
        if (Regex.IsMatch(evidence, @"(?i)(?:^|\s)[1-9][\.)]\s+", RegexOptions.CultureInvariant))
            score += 18;
        score += ComputeExactVisibleTitleMatchScore(requestedTitle, hit);
        return score;
    }

    private static string GetFocusedExactItemEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var text = GetBestExactItemEvidenceText(requestedTitle, hit);
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var idx = string.IsNullOrWhiteSpace(normalizedTitle)
            ? -1
            : normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (idx < 0)
            return text;

        var start = Math.Max(0, Math.Min(idx, text.Length) - 760);
        var length = Math.Min(1800, text.Length - start);
        return TrimAfterLikelyExactItemBoundary(text.Substring(start, length));
    }

    private static string GetFocusedExactItemStructuredEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var text = GetBestExactItemEvidenceText(requestedTitle, hit);
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var idx = string.IsNullOrWhiteSpace(normalizedTitle)
            ? -1
            : normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = text[..Math.Min(idx, text.Length)];
            var prefixLooksLikePreviousStructuredItem =
                CountQuantityLikeSignals(prefix) >= 2
                && ContainsStructuredItemHeading(prefix);
            var hasPreTitleStructuredEvidence =
                !prefixLooksLikePreviousStructuredItem
                && (Regex.IsMatch(NormalizeLexicalLookup(prefix), @"\b(?:procedure|procedures?|instructions?|method|methode|etape|etapes|steps?|preparation|operation|workflow|execution|technique)\b", RegexOptions.CultureInvariant)
                    || Regex.IsMatch(prefix, @"[\u2022\u00b7]\s*\p{L}{3,}", RegexOptions.CultureInvariant));
            var start = hasPreTitleStructuredEvidence
                ? Math.Max(0, Math.Min(idx, text.Length) - 760)
                : Math.Min(idx, text.Length);
            var length = Math.Min(1800, text.Length - start);
            return TrimAfterLikelyExactItemBoundary(text.Substring(start, length));
        }

        return TrimAfterLikelyExactItemBoundary(text);
    }

    private static string GetBestExactItemEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);
        var contextual = CollapseWhitespace(hit.ContextualSnippet ?? string.Empty);
        var structuredEvidence = BuildRagHitContentCardEvidenceText(hit);
        if (contextual.Length > excerpt.Length + 40 && ExactItemTextMatchesRequestOrStructure(requestedTitle, contextual))
            return StripContextualMetadataForEvidence(contextual);
        if (fullText.Length > excerpt.Length + 120 && ExactItemTextMatchesRequestOrStructure(requestedTitle, fullText))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);

        if (excerpt.Length >= 40)
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        if (!string.IsNullOrWhiteSpace(fullText))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
        return GetBestRagEvidenceText(hit);
    }

    private static string StripContextualMetadataForEvidence(string contextual)
    {
        var value = CollapseWhitespace(contextual);
        value = Regex.Replace(
            value,
            @"(?i)\bMatched\s+(?:profile|quoted)\s+title\s*:\s*.+?(?=\s*\|\s*(?:Document|Section|HeadingPath|ChunkType|Pages|PreviousEvidence|NextEvidence|Evidence)\s*:|$)",
            string.Empty,
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\bMatched\s+(?:direct_title_token_route|local_title_token_route|title_anchor_route)\s*:\s*.+?(?=\s*(?:\|\s*)?(?:Document|Section|HeadingPath|ChunkType|Pages|Context|Excerpt|PreviousContext|NextContext|PreviousEvidence|NextEvidence|Evidence)\s*:|$)",
            string.Empty,
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:Matched\s+(?:profile|quoted)\s+title|Matched\s+(?:direct_title_token_route|local_title_token_route|title_anchor_route)|Document|Section|HeadingPath|ChunkType|Pages)\s*:\s*.+?(?=\s*\b(?:Matched\s+(?:profile|quoted)\s+title|Matched\s+(?:direct_title_token_route|local_title_token_route|title_anchor_route)|Document|Section|HeadingPath|ChunkType|Pages|Context|PreviousContext|NextContext|Excerpt|PreviousEvidence|NextEvidence|Evidence)\s*:|$)",
            " ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\s*\|\s*(?:Document|Section|HeadingPath|ChunkType|Pages)\s*:[^|]+",
            " ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\s*\|\s*(?:PreviousEvidence|NextEvidence|Evidence)\s*:\s*",
            ". ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:Context|PreviousContext|NextContext|Excerpt|PreviousEvidence|NextEvidence|Evidence)\s*:\s*",
            ". ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(value).Trim(' ', '.', ',', ';', ':');
    }

    private static bool LooksLikeConcreteContextualEvidence(string? text)
    {
        var value = CollapseWhitespace(text ?? string.Empty);
        if (value.Length < 80)
            return false;
        if (LooksLikeNoisyCandidateSupportCue(value) || LooksLikePureRouteNavigationText(value))
            return false;

        var normalized = NormalizeLexicalLookup(value);
        if (Regex.IsMatch(
                normalized,
                @"\b(?:matched\s+profile|matched\s+title|profile\s+signals|document\s+profile|navigationonly|orientationonly|table\s+of\s+contents|sommaire|contents|index)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.Matches(normalized, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Count >= 12;
    }

    private const string ItemizedSectionHeadingPattern =
        @"items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|materials?|materiel|mat[eÃƒÂ©]riel|components?|composants?";

    private const string ProcedureSectionHeadingPattern =
        @"preparation|pr(?:e|\u00e9)paration|preparacion|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|procedure|procedures?|process|processus|operation|operations?|workflow|workflows?|execution|instruction|instructions|method|methods?|methode|methodes|m(?:e|\u00e9)thode|m(?:e|\u00e9)thodes|mode\s+operatoire|technique|etape|etapes|(?:e|\u00e9)tapes?|steps?";

    private const string ExactItemStructureHeadingPattern =
        ItemizedSectionHeadingPattern + "|" + ProcedureSectionHeadingPattern;

    private const string NonItemizedSectionHeadingPattern =
        ItemizedSectionHeadingPattern + "|" + ProcedureSectionHeadingPattern;

    private static bool ExactItemTextMatchesRequestOrStructure(string requestedTitle, string text)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedText.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length > 0 && titleTerms.All(term => normalizedText.Contains(term, StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static string TrimAfterLikelyExactItemBoundary(string text)
    {
        var value = text ?? string.Empty;
        if (value.Length < 260)
            return value;

        var boundaries = new[]
        {
            @"\b\d{1,4}\s*[\u2022\u00b7]\s*(?:[A-Z\u00c0-\u017f]|\p{Lu})",
            @"\b\d{1,4}\s*\|\s*[\p{L} ]{3,50}"
        };

        var cutAt = value.Length;
        foreach (var pattern in boundaries)
        {
            var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
            if (match.Success && match.Index >= 220)
                cutAt = Math.Min(cutAt, match.Index);
        }

        return cutAt < value.Length
            ? value[..cutAt].TrimEnd(' ', '.', ',', ';', ':')
            : value;
    }

    private static bool ExactItemEvidenceTextMatchesRequestedTitle(string requestedTitle, RagHitSummary hit)
    {
        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) && evidence.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        return titleTerms.Length > 0 && titleTerms.All(term => evidence.Contains(term, StringComparison.Ordinal));
    }

}
