using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private sealed record QuantityScaleLine(string Original, string Scaled, bool IsNumeric);

    private sealed record QuantityScalingCandidate(RagHitSummary Hit, int SourceCount, IReadOnlyList<QuantityScaleLine> Lines, double Score);

    private static IReadOnlyList<string> ExtractSourceBackedExcludedTerms(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:sans|pas\s+d['\u2019]?|pas\s+de|without|no|sin|sem|ohne|senza)\s+(?<term>[\p{L}\p{N}'\u2019 \-]{2,60})",
                     RegexOptions.CultureInvariant))
        {
            foreach (var rawPart in Regex.Split(
                match.Groups["term"].Value,
                @"\s*(?:,|/|\bet\b|\bou\b|\bni\b|\band\b|\bor\b|\bnor\b|\by\b|\bo\b|\be\b|\boder\b|\bund\b)\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var term = Regex.Replace(
                        rawPart,
                        @"(?i)\b(?:avec|with|dans|from|pour|for|sur|de|du|des|document|documents|source|sources|element|elements|item|items|option|options)\b.*$",
                        string.Empty,
                        RegexOptions.CultureInvariant)
                    .Trim(' ', '.', ',', ':', ';', '?', '!', '"', '\'');

                var normalized = NormalizeLexicalLookup(term);
                if (LooksLikeIdiomaticNoDetailExclusion(normalized, NormalizeLexicalLookup(match.Value)))
                    continue;
                if (normalized.Length >= 2)
                    terms.Add(normalized);
            }
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:en\s+evitant|en\s+Ã©vitant|evitant|Ã©vitant|eviter|Ã©viter|avoid(?:ing)?|except|sauf|salvo|sem|ohne|senza)\s+(?<terms>[^.?!;]{2,140})",
                     RegexOptions.CultureInvariant))
        {
            foreach (var rawPart in Regex.Split(match.Groups["terms"].Value, @"\s*(?:,|/|\bet\b|\bou\b|\bni\b|\band\b|\bor\b|\bnor\b|\by\b|\bo\b|\be\b|\boder\b|\bund\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var part = Regex.Replace(
                        rawPart,
                        @"(?i)\b(?:dans|from|pour|for|sur|avec|with|document|documents|source|sources|element|elements|item|items|option|options)\b.*$",
                        string.Empty,
                        RegexOptions.CultureInvariant)
                    .Trim(' ', '.', ',', ':', ';', '?', '!', '"', '\'');
                var normalized = NormalizeLexicalLookup(part);
                if (LooksLikeIdiomaticNoDetailExclusion(normalized, NormalizeLexicalLookup(match.Value)))
                    continue;
                if (normalized.Length >= 2)
                    terms.Add(normalized);
            }
        }

        return terms.Distinct(StringComparer.Ordinal).Take(5).ToArray();
    }

    private static bool LooksLikeIdiomaticNoDetailExclusion(string normalizedTerm, string normalizedMatch)
    {
        if (string.IsNullOrWhiteSpace(normalizedTerm))
            return true;

        if (Regex.IsMatch(
                normalizedMatch,
                @"\b(?:sans|without|sin|sem|ohne|senza)\s+(?:rentrer|entrer|going|go|entrar|entrando)\s+(?:dans|into|en|em|in)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalizedTerm,
            @"^(?:rentrer|entrer|going|go|entrar|entrando)(?:\s+(?:dans|into|en|em|in))?$",
            RegexOptions.CultureInvariant);
    }

    private static bool RagHitContainsAnyExcludedTerm(RagHitSummary hit, IReadOnlyList<string> excludedTerms)
    {
        if (excludedTerms.Count == 0)
            return false;

        var haystack = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        return excludedTerms.Any(term =>
        {
            return Regex.IsMatch(
                haystack,
                $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:s|es)?([^\p{{L}}\p{{N}}]|$)",
                RegexOptions.CultureInvariant);
        });
    }

    private static string BuildNoSourceBackedCompliantOptionAnswer(string language, IReadOnlyList<string> excludedTerms, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var excluded = string.Join(", ", excludedTerms);
        var labels = language switch
        {
            "en" => (
                Header: $"I did not find a source-backed option that respects the exclusion: {excluded}.",
                Detail: "The retrieved source excerpts still contain the excluded element, so I will not invent a compliant variant.",
                Nearby: "Retrieved conflicting sources:"
            ),
            "es" => (
                Header: $"No he encontrado una opcion con fuente que respete la exclusion: {excluded}.",
                Detail: "Los extractos recuperados siguen conteniendo el elemento excluido, asi que no invento una variante conforme.",
                Nearby: "Fuentes recuperadas en conflicto:"
            ),
            "pt" => (
                Header: $"Nao encontrei uma opcao com fonte que respeite a exclusao: {excluded}.",
                Detail: "Os excertos recuperados ainda contem o elemento excluido, por isso nao invento uma variante conforme.",
                Nearby: "Fontes recuperadas em conflito:"
            ),
            "de" => (
                Header: $"Ich habe keine quellenbasierte Option gefunden, die den Ausschluss erfuellt: {excluded}.",
                Detail: "Die gefundenen Auszuege enthalten das ausgeschlossene Element weiterhin, daher erfinde ich keine passende Variante.",
                Nearby: "Gefundene widersprechende Quellen:"
            ),
            "it" => (
                Header: $"Non ho trovato un'opzione supportata dalle fonti che rispetti l'esclusione: {excluded}.",
                Detail: "Gli estratti recuperati contengono ancora l'elemento escluso, quindi non invento una variante conforme.",
                Nearby: "Fonti recuperate in conflitto:"
            ),
            _ => (
                Header: $"Je n'ai pas trouvÃ© d'option sourcÃ©e qui respecte l'exclusion : {excluded}.",
                Detail: "Les extraits retrouvÃ©s contiennent encore l'Ã©lÃ©ment exclu, donc je n'invente pas une variante conforme.",
                Nearby: "Sources retrouvÃ©es en conflit :"
            )
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Detail);
        sb.AppendLine(labels.Nearby);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 240);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSourceBackedExtractionQualityCaveat(StringBuilder sb, IReadOnlyList<RagHitSummary> hits, string language)
    {
        if (!hits.Any(ShouldFlagExtractionQualityCaveat))
            return;

        var note = NormalizeLanguageCode(language) switch
        {
            "en" => "Note: at least one cited page has a low extraction/OCR confidence flag, so verify the source page if the detail is critical.",
            "es" => "Nota: al menos una pÃ¡gina citada tiene una marca de baja confianza de extracciÃ³n/OCR; verifica la pÃ¡gina fuente si el detalle es crÃ­tico.",
            "pt" => "Nota: pelo menos uma pÃ¡gina citada tem baixa confianÃ§a de extraÃ§Ã£o/OCR; verifica a pÃ¡gina fonte se o detalhe for crÃ­tico.",
            "de" => "Hinweis: Mindestens eine zitierte Seite hat eine niedrige Extraktions- oder OCR-Vertrauensbewertung. PrÃ¼fe die Quellseite, wenn das Detail kritisch ist.",
            "it" => "Nota: almeno una pagina citata ha un indicatore di bassa affidabilitÃ  di estrazione/OCR; verifica la pagina sorgente se il dettaglio Ã¨ critico.",
            _ => "Note : au moins une page citÃ©e a un signal de confiance faible d'extraction/OCR ; vÃ©rifie la page source si le dÃ©tail est critique."
        };

        sb.AppendLine();
        sb.Append(note);
    }

    private static bool ShouldFlagExtractionQualityCaveat(RagHitSummary hit)
    {
        if (hit.ManualReviewRecommended)
            return true;

        if (hit.ExtractionConfidence is <= 0.5)
            return true;

        var status = (hit.QualityStatus ?? string.Empty).Trim().ToLowerInvariant();
        return status.Contains("manual_review", StringComparison.Ordinal)
            || status.Contains("low_text", StringComparison.Ordinal)
            || status.Contains("ocr_failed", StringComparison.Ordinal)
            || status.Contains("low_confidence", StringComparison.Ordinal);
    }

    private static bool LooksLikeSourceBackedQuantityScalingRequest(string? query)
    {
        var normalized = NormalizeLooseLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksExplicitScaling = Regex.IsMatch(
            normalized,
                @"\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|multiplie|multiplier|calcule|calculer|mets|mettre|quantites?|quantit[eÃ©]s?|amounts?|values?|valeurs?|scale|resize|adjust|adapt|convert|multiply|counts?|units?|items?)\b",
            RegexOptions.CultureInvariant);
        if (LooksLikeDocumentaryPlanningRequest(query) && !asksExplicitScaling)
            return false;

        return asksExplicitScaling && TryExtractTargetScaleCount(query, out _);
    }

    private static string BuildSourceBackedQuantityScalingAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        if (!TryExtractTargetScaleCount(query, out var targetCount))
            return string.Empty;

        var candidates = SelectQuantityScalingCandidates(hits, query, targetCount);

        var selected = candidates.FirstOrDefault();
        if (selected is null)
            return string.Empty;

        var factor = targetCount / (double)selected.SourceCount;
        var labels = BuildQuantityScalingLabels(language);
        var docLabel = string.IsNullOrWhiteSpace(selected.Hit.DocName) ? selected.Hit.DocPath : selected.Hit.DocName;
        var factorText = FormatScaleNumber(factor);

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.Append(labels.Base);
        sb.Append(' ');
        sb.Append(docLabel);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(selected.Hit.PageStart);
        sb.Append(" - ");
        sb.Append(selected.SourceCount);
        sb.Append(" -> ");
        sb.Append(targetCount);
        sb.Append(" (");
        sb.Append(labels.Factor);
        sb.Append(" x");
        sb.Append(factorText);
        sb.AppendLine(").");

        sb.AppendLine(labels.ItemizedList);
        foreach (var line in selected.Lines.Take(18))
        {
            sb.Append("- ");
            sb.Append(line.Scaled);
            sb.Append(" (");
            sb.Append(labels.Source);
            sb.Append(": ");
            sb.Append(line.Original);
            sb.AppendLine(")");
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static List<QuantityScalingCandidate> SelectQuantityScalingCandidates(IEnumerable<RagHitSummary> hits, string query, int targetCount)
    {
        var subject = TryExtractQuantityScalingSubject(query);
        return hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(hit =>
            {
                var text = GetBestRagEvidenceText(hit);
                int sourceCount;
                string? sourceCountLabel;
                List<QuantityScaleLine> lines;
                if (TryBuildQuantityScaleLinesFromCardEvidence(
                        hit,
                        query,
                        targetCount,
                        out sourceCount,
                        out sourceCountLabel,
                        out var evidenceLines))
                {
                    lines = evidenceLines;
                }
                else
                {
                    sourceCount = TryExtractSourceScaleBaseCount(text, out var baseCount, out sourceCountLabel) ? baseCount : 0;
                    lines = sourceCount > 0
                        ? ExtractQuantityScaleLines(text, targetCount / (double)sourceCount)
                        : new List<QuantityScaleLine>();
                }
                var isScalable = sourceCount > 0
                    && HasExplicitScalableQuantitySourceSignal(hit, query, text, sourceCountLabel, lines);
                var score = isScalable
                    ? ComputeRagHitLexicalRelevance(string.IsNullOrWhiteSpace(subject) ? query : subject!, GetRagHitLookupText(hit))
                      + 16
                      + Math.Min(lines.Count(static line => line.IsNumeric), 8)
                    : double.NegativeInfinity;
                return new QuantityScalingCandidate(hit, sourceCount, lines, score);
            })
            .Where(item => item.SourceCount > 0
                           && item.Lines.Count(static line => line.IsNumeric) >= 2
                           && !double.IsNegativeInfinity(item.Score))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Hit.Score)
            .ToList();
    }

    private static bool HasExplicitScalableQuantitySourceSignal(
        RagHitSummary hit,
        string query,
        string evidenceText,
        string? sourceCountLabel,
        IReadOnlyList<QuantityScaleLine> lines)
    {
        if (HasContentCardNonScalableEvidence(hit) && !HasContentCardScalableQuantityEvidence(hit))
            return false;

        if (LooksLikeSafetyOrComplianceQuantityContext(hit, query, evidenceText))
            return false;

        var numericLineCount = lines.Count(static line => line.IsNumeric);
        if (numericLineCount < 2)
            return false;

        TryExtractTargetScaleCount(query, out _, out var targetCountLabel);
        var labelsCompatible = ScaleCountLabelsLookCompatible(sourceCountLabel, targetCountLabel);
        var sourceLabelIsListHeading = IsQuantityScaleListHeadingLabel(sourceCountLabel);
        var sourceLabelLooksScalable = sourceLabelIsListHeading || IsLikelyScalableScaleCountLabel(sourceCountLabel);
        var cardHasScalableQuantityBasis = TryExtractScalableQuantityCardBasis(
            hit,
            out _,
            out var cardScaleCountLabel);
        var cardLabelsCompatible = ScaleCountLabelsLookCompatible(cardScaleCountLabel, targetCountLabel);

        var normalizedEvidence = NormalizeLooseLookup(evidenceText);
        var cardSignals = BuildQuantityScalingCardSignalText(hit);
        var sourceContext = NormalizeLooseLookup(
            $"{hit.SectionTitle} {hit.HeadingPath} {hit.CategoryPath} {normalizedEvidence} {cardSignals}");

        var hasExplicitBase = HasExplicitQuantityScaleBasePhrase(normalizedEvidence) || cardHasScalableQuantityBasis;
        var hasQuantityListSignal = HasQuantityListSignal(sourceContext)
                                    || HasQuantityListCardSignal(hit)
                                    || numericLineCount >= 3;
        var hasScalableSignal = HasScalableQuantitySignal(sourceContext) || cardHasScalableQuantityBasis;

        return hasExplicitBase
               && hasQuantityListSignal
               && (hasScalableSignal || labelsCompatible || cardLabelsCompatible || sourceLabelLooksScalable);
    }

    private static bool TryExtractScalableQuantityCardBasis(RagHitSummary hit, out int count, out string? label)
    {
        count = 0;
        label = null;
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        foreach (var card in hit.MatchedContentCards)
        {
            if (card.Evidence?.ScaleBasis is { Count: > 0 } basis
                && card.Evidence.NonScalableReasons.Count == 0
                && card.Evidence.QuantityFacts.Count >= 2)
            {
                count = basis.Count;
                label = basis.Label;
                return true;
            }

            var hasScaleBasis = false;
            var hasQuantityList = false;
            var hasScalableQuantities = false;
            var cardCount = 0;
            string? cardLabel = null;

            foreach (var signal in card.Signals ?? Array.Empty<string>())
            {
                var raw = CollapseWhitespace(signal).Trim();
                if (raw.Length == 0)
                    continue;

                var lower = raw.ToLowerInvariant();
                var normalized = NormalizeScaleSignalToken(raw);
                if (string.Equals(normalized, "scale_basis", StringComparison.Ordinal))
                {
                    hasScaleBasis = true;
                    continue;
                }

                if (string.Equals(normalized, "quantity_list", StringComparison.Ordinal))
                {
                    hasQuantityList = true;
                    continue;
                }

                if (string.Equals(normalized, "scalable_quantities", StringComparison.Ordinal))
                {
                    hasScalableQuantities = true;
                    continue;
                }

                if (lower.StartsWith("scale_basis_count:", StringComparison.Ordinal)
                    && int.TryParse(lower["scale_basis_count:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    && parsed is > 0 and <= 200)
                {
                    cardCount = parsed;
                    hasScaleBasis = true;
                    continue;
                }

                if (lower.StartsWith("scale_basis_label:", StringComparison.Ordinal))
                {
                    cardLabel = NormalizeScaleSignalToken(lower["scale_basis_label:".Length..]);
                }
            }

            if (cardCount > 0 && (hasScalableQuantities || (hasScaleBasis && hasQuantityList)))
            {
                count = cardCount;
                label = cardLabel;
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildQuantityScaleLinesFromCardEvidence(
        RagHitSummary hit,
        string query,
        int targetCount,
        out int sourceCount,
        out string? sourceCountLabel,
        out List<QuantityScaleLine> lines)
    {
        sourceCount = 0;
        sourceCountLabel = null;
        lines = new List<QuantityScaleLine>();

        if (hit.MatchedContentCards is not { Count: > 0 }
            || LooksLikeSafetyOrComplianceQuantityContext(hit, query, GetBestRagEvidenceText(hit)))
        {
            return false;
        }

        foreach (var card in hit.MatchedContentCards)
        {
            var evidence = card.Evidence;
            if (evidence?.ScaleBasis is not { Count: > 0 } basis
                || evidence.NonScalableReasons.Count > 0
                || evidence.QuantityFacts.Count < 2)
            {
                continue;
            }

            var factor = targetCount / (double)basis.Count;
            var scaledLines = evidence.QuantityFacts
                .Where(static fact => fact.Value > 0
                                      && !string.IsNullOrWhiteSpace(fact.Unit)
                                      && !string.IsNullOrWhiteSpace(fact.Label))
                .Take(18)
                .Select(fact =>
                {
                    var source = string.IsNullOrWhiteSpace(fact.SourceText)
                        ? $"{FormatScaleNumber(fact.Value)} {fact.Unit} {fact.Label}"
                        : CollapseWhitespace(fact.SourceText);
                    var scaled = $"{FormatScaleNumber(fact.Value * factor)} {fact.Unit} {fact.Label}";
                    return new QuantityScaleLine(source, scaled, IsNumeric: true);
                })
                .ToList();

            if (scaledLines.Count(static line => line.IsNumeric) < 2)
                continue;

            sourceCount = basis.Count;
            sourceCountLabel = basis.Label;
            lines = scaledLines;
            return true;
        }

        return false;
    }

    private static bool HasQuantityListCardSignal(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        return hit.MatchedContentCards.Any(static card =>
            (card.Signals ?? Array.Empty<string>()).Any(static signal =>
            {
                var normalized = NormalizeScaleSignalToken(signal);
                return string.Equals(normalized, "quantity_list", StringComparison.Ordinal)
                       || string.Equals(normalized, "scalable_quantities", StringComparison.Ordinal);
            }));
    }

    private static string NormalizeScaleSignalToken(string? value)
    {
        var normalized = Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                "_",
                RegexOptions.CultureInvariant)
            .Trim('_');
        normalized = Regex.Replace(normalized, @"_+", "_", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static bool LooksLikeSafetyOrComplianceQuantityContext(RagHitSummary hit, string query, string evidenceText)
    {
        var haystack = NormalizeLooseLookup(
            $"{query} {hit.DocName} {hit.DocPath} {hit.CategoryPath} {hit.SectionTitle} {hit.HeadingPath} {evidenceText} {BuildQuantityScalingCardSignalText(hit)}");

        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        return Regex.IsMatch(
                haystack,
                @"\b(?:safety|security|securite|hazard|danger|risk|risque|warning|caution|emergency|incident|injury|explosion|fire|flammable|toxic|toxicity|exposure|contamination|ppe|epi|lockout|loto|compliance|conformite|conformity|regulatory|reglementaire|reglementation|regulation|directive|legal|law|statutory|norme|standard|iso|iec|clause|article|shall|must|mandatory|required|obligatoire|exigence|interdit|prohibited|forbidden|limit|limite|threshold|seuil)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                haystack,
                @"\b(?:do\s+not|must\s+not|shall\s+not|ne\s+pas|ne\s+jamais|il\s+faut|il\s+ne\s+faut\s+pas)\b",
                RegexOptions.CultureInvariant);
    }

    private static string BuildQuantityScalingCardSignalText(RagHitSummary hit)
        => hit.MatchedContentCards is null
            ? string.Empty
            : string.Join(
                ' ',
                hit.MatchedContentCards.Select(static card =>
                    $"{card.Title} {card.Kind} {string.Join(' ', card.Signals ?? Array.Empty<string>())} {string.Join(' ', card.Evidence?.NonScalableReasons ?? Array.Empty<string>())}"));

    private static bool HasExplicitQuantityScaleBasePhrase(string normalizedEvidence)
        => Regex.IsMatch(
               normalizedEvidence,
               @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s*[\p{L}'\u2019.\-]{0,30}\b",
               RegexOptions.CultureInvariant)
           || Regex.IsMatch(
               normalizedEvidence,
               @"\b(?:base|basis|batch|lot|serie|set|per\s+unit|par\s+unite|par\s+element)\b",
               RegexOptions.CultureInvariant)
           || Regex.IsMatch(
               normalizedEvidence,
                @"\b\d{1,3}\s*(?:items?|elements?|quantities?|quantites?|amounts?|values?|valeurs?|requirements?|materials?|materiel|components?|composants?|supplies|entries?)\s*\d",
               RegexOptions.CultureInvariant);

    private static bool HasQuantityListSignal(string normalizedContext)
        => Regex.IsMatch(
            normalizedContext,
            @"\b(?:quantites?|quantit[eÃ©]s?|quantities?|amounts?|values?|valeurs?|items?|elements?|components?|composants?|materials?|materiel|supplies|entries?|requirements?)\b",
            RegexOptions.CultureInvariant);

    private static bool HasScalableQuantitySignal(string normalizedContext)
        => Regex.IsMatch(
            normalizedContext,
            @"\b(?:scale|scaled|scalable|scaling|adapter|adapte|adapted|adjust|adjusted|proportion(?:al|nel|nelle)?|ratio|factor|facteur|multiply|multiplier|base|basis|batch|lot|serie|personnes?|persons?|people|units?|items?)\b",
            RegexOptions.CultureInvariant);

    private static bool ScaleCountLabelsLookCompatible(string? sourceLabel, string? targetLabel)
    {
        var source = NormalizeScaleCountLabel(sourceLabel);
        var target = NormalizeScaleCountLabel(targetLabel);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;

        if (source == target)
            return true;

        if (IsQuantityScaleListHeadingLabel(source))
            return true;

        return (source, target) switch
        {
            ("personne", "people") or ("people", "personne") => true,
            ("person", "personne") or ("personne", "person") => true,
            ("unit", "unite") or ("unite", "unit") => true,
            ("item", "element") or ("element", "item") => true,
            _ => false
        };
    }

    private static bool IsLikelyScalableScaleCountLabel(string? label)
    {
        var normalized = NormalizeScaleCountLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:personne|person|people|unit|unite|item|element|piece|part|batch|lot|set|serie)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool IsQuantityScaleListHeadingLabel(string? label)
    {
        var normalized = NormalizeScaleCountLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:entry|item|element|quantity|quantite|amount|value|valeur|requirement|material|materiel|component|composant|supply)\b",
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeScaleCountLabel(string? label)
    {
        var normalized = NormalizeLooseLookup(label)
            .Trim(' ', '.', ',', ';', ':', '-', '/', '\\');
        if (normalized.EndsWith("ies", StringComparison.Ordinal) && normalized.Length > 4)
            normalized = normalized[..^3] + "y";
        else if (normalized.EndsWith("es", StringComparison.Ordinal) && normalized.Length > 4)
            normalized = normalized[..^2];
        else if (normalized.EndsWith('s') && normalized.Length > 3)
            normalized = normalized[..^1];

        return normalized;
    }

    private static (string Header, string Base, string Factor, string ItemizedList, string Source, string Caution) BuildQuantityScalingLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "Here is the deterministic quantity adaptation from the cited source.",
                "Source base:",
                "factor",
                "Adjusted quantities:",
                "source",
                "Quantities absent from the source stay unspecified; I do not invent missing steps."),
            "es" => (
                "Aqui tienes la adaptacion determinista de cantidades a partir de la fuente citada.",
                "Base fuente:",
                "factor",
                "Cantidades ajustadas:",
                "fuente",
                "Las cantidades ausentes de la fuente quedan sin especificar; no invento pasos que faltan."),
            "pt" => (
                "Aqui esta a adaptacao deterministica das quantidades a partir da fonte citada.",
                "Base da fonte:",
                "fator",
                "Quantidades ajustadas:",
                "fonte",
                "Quantidades ausentes da fonte ficam sem especificacao; nao invento passos em falta."),
            "de" => (
                "Hier ist die deterministische Mengenanpassung aus der zitierten Quelle.",
                "Quellbasis:",
                "Faktor",
                "Angepasste Mengen:",
                "Quelle",
                "Mengen, die in der Quelle fehlen, bleiben unspezifiziert; fehlende Schritte erfinde ich nicht."),
            "it" => (
                "Ecco l'adattamento deterministico delle quantita dalla fonte citata.",
                "Base fonte:",
                "fattore",
                "Quantita adattate:",
                "fonte",
                "Le quantita assenti dalla fonte restano non specificate; non invento passaggi mancanti."),
            _ => (
                "Voici l'adaptation calculÃ©e Ã  partir de la source citÃ©e.",
                "Base source :",
                "facteur",
                "QuantitÃ©s adaptÃ©es :",
                "source",
                "Les quantitÃ©s absentes de la source restent non spÃ©cifiÃ©es ; je n'invente pas les Ã©tapes manquantes.")
        };
    }

    private static bool TryExtractTargetScaleCount(string? query, out int count)
        => TryExtractTargetScaleCount(query, out count, out _);

    private static bool TryExtractTargetScaleCount(string? query, out int count, out string? label)
        => TryExtractScaleCount(query, out count, out label);

    private static bool TryExtractSourceScaleBaseCount(string? text, out int count)
        => TryExtractSourceScaleBaseCount(text, out count, out _);

    private static bool TryExtractSourceScaleBaseCount(string? text, out int count, out string? label)
        => TryExtractScaleCount(text, out count, out label);

    private static bool TryExtractScaleCount(string? value, out int count, out string? label)
    {
        count = 0;
        label = null;
        var normalized = NormalizeLooseLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var patterns = new[]
        {
            @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30}))?\b",
            @"\b(?<n>\d{1,3})\s*(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30})(?=\d|\b)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.CultureInvariant);
            if (match.Success
                && int.TryParse(match.Groups["n"].Value, out var n)
                && n is > 0 and <= 200
                && IsPlausibleScaleCountLabel(match.Groups["label"].Value))
            {
                count = n;
                label = NullIfWhiteSpace(match.Groups["label"].Value);
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausibleScaleCountLabel(string? label)
    {
        var normalized = NormalizeLooseLookup(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (!Regex.IsMatch(normalized, @"^[\p{L}][\p{L}'\u2019.\-]{1,30}$", RegexOptions.CultureInvariant))
            return false;

        if (LooksLikeProcedureLeadLabel(normalized))
            return false;

        return !Regex.IsMatch(
            normalized,
            @"^(?:g|kg|mg|ml|cl|l|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|%|s|sec|secs|secondes?|seconds?|min|mins|minutes?|h|hr|hrs|heures?|hours?|jour|jours|day|days|mois|month|months|annee|annees|year|years|c|celsius|fahrenheit)$",
            RegexOptions.CultureInvariant);
    }

    private const string OperationalActionLeadPattern =
        @"ouvrir|ouvrez|fermer|fermez|ajouter|ajoutez|appliquer|appliquez|configurer|configurez|connecter|connectez|installer|installez|lancer|lancez|demarrer|demarrez|arreter|arretez|retirer|retirez|supprimer|supprimez|remplacer|remplacez|valider|validez|verifier|verifiez|controler|controlez|mettre\s+a\s+jour|mettez\s+a\s+jour|actualiser|actualisez|executer|executez|activer|activez|desactiver|desactivez|selectionner|selectionnez|saisir|saisissez|regler|reglez|ajuster|ajustez|preparer|preparez|former|formez|laisser|laissez|placer|placez|poser|posez|deposer|deposez|servir|servez|start|stop|open|close|add|apply|configure|connect|install|launch|run|execute|remove|delete|replace|validate|verify|check|update|enable|disable|select|enter|set|adjust|measure|restart|prepare|fill|place|serve";

    private static bool LooksLikeProcedureLeadLabel(string normalizedLabel)
        => Regex.IsMatch(
            NormalizeLooseLookup(normalizedLabel),
            @"^(?:pour|pendant|dans|puis|quand|lorsque|avant|apres|jusqu|jusque|then|when|after|before|until|while|durante|cuando|antes|despues|depois|quando|wenn|nach|bevor|" + OperationalActionLeadPattern + @")$",
            RegexOptions.CultureInvariant);

    private static string? TryExtractQuantityScalingSubject(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        var patterns = new[]
        {
            @"(?i)\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|calcule|calculer|mets|mettre|scale|resize|adjust|adapt|convert)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>.+?)\s+(?:pour|for|para|per|fur|fuer|zu|a)\s+\d{1,3}\b",
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var title = CleanupRequestedItemTitle(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        return TryExtractRequestedItemTitle(query);
    }

    private static List<QuantityScaleLine> ExtractQuantityScaleLines(string text, double factor)
    {
        var normalized = text
            .Replace('\u2022', '|')
            .Replace('\u00b7', '|');
        normalized = Regex.Replace(
            normalized,
            @"(?<!^)(?<![\d,.])(?=\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12})\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<!^)(?<![\d,.])(?=\d+\s+[\p{L}'\u2019\-]{3,}(?:\s+[\p{L}'\u2019\-]{3,}){0,3}\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var parts = Regex.Split(normalized, @"[|;\r\n]+", RegexOptions.CultureInvariant)
            .Select(CollapseWhitespace)
            .Where(part => part.Length > 0)
            .ToList();

        var lines = new List<QuantityScaleLine>();
        foreach (var part in parts.Skip(1))
        {
            if (LooksLikeQuantityListBoundary(part))
            {
                break;
            }

            if (TryScaleQuantitySegment(part, factor, out var scaled))
            {
                lines.Add(new QuantityScaleLine(part, scaled, IsNumeric: true));
                continue;
            }

            if (TryKeepQualitativeQuantitySegment(part, out var qualitative))
            {
                lines.Add(new QuantityScaleLine(part, qualitative, IsNumeric: false));
            }
        }

        return lines;
    }

    private static bool LooksLikeQuantityListBoundary(string text)
    {
        var normalized = NormalizeLooseLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
                normalized,
                @"\b(?:preparation|workflow|workflows?|procedure|procedures?|process|execution|operation|operations|instructions?|method|methods?|methode|methodes|mode\s+operatoire|etape|etapes|steps?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d{1,3}\s*[\).\-]\s+\p{L}", RegexOptions.CultureInvariant);
    }

    private static bool TryKeepQualitativeQuantitySegment(string segment, out string scaled)
    {
        scaled = string.Empty;
        var cleaned = CollapseWhitespace(segment ?? string.Empty).Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var normalized = NormalizeLexicalLookup(cleaned);
        if (!Regex.IsMatch(normalized, @"^[\p{L}'\-\s]{3,70}$", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"\b(?:preparation|operation|workflow|execution|procedure|method|methode|etapes?|steps?|instructions?|requirements?|values?|materiel|materials?|equipment|tools?|outils?)\b", RegexOptions.CultureInvariant))
            return false;

        scaled = $"{cleaned} (quantitÃ© non prÃ©cisÃ©e dans la source)";
        return true;
    }

    private static bool TryScaleQuantitySegment(string segment, double factor, out string scaled)
    {
        scaled = string.Empty;
        var match = Regex.Match(
            segment,
            @"^\s*(?<num>\d+(?:[,.]\d+)?|[1-9]\d*\s*/\s*[1-9]\d*|\u00bd|\u00bc|\u00be)\s*(?<rest>.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        if (!TryParseFlexibleNumber(match.Groups["num"].Value, out var number))
            return false;

        var rest = CollapseWhitespace(match.Groups["rest"].Value);
        if (string.IsNullOrWhiteSpace(rest))
            return false;
        if (LooksLikeNonScalableQuantitySegment(rest))
            return false;

        var scaledNumber = number * factor;
        scaled = FormatScaleNumber(scaledNumber) + " " + rest;
        return true;
    }

    private static bool LooksLikeNonScalableQuantitySegment(string rest)
    {
        var normalized = NormalizeLooseLookup(rest);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:min|mins|minutes?|h|hr|hrs|heures?|hours?|s|sec|secs|secondes?|seconds?|jour|jours|days?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|%|mm|cm|m|km|nm|eur|euro|euros|chf|usd|gbp)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:celsius|fahrenheit|degrees?|degres?)\b|^Â°\s*c\b|^c\s*$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:items?|elements?|quantities?|quantites?|amounts?|values?|valeurs?|requirements?|materials?|materiel|components?|composants?|supplies|entries?)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:units?|unites?|personnes?|persons?|people)\s*:?$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:temperature|temp|pressure|pression|voltage|tension|current|courant|speed|vitesse|frequency|frequence|torque|couple|force|setting|reglage|parametre|parameter|limit|limite|threshold|seuil|tolerance|clearance|jeu|distance|dimension|diameter|diametre|angle|slope|pente|concentration|dosage|ph)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool TryParseFlexibleNumber(string value, out double number)
    {
        number = 0;
        var s = CollapseWhitespace(value)
            .Replace(',', '.');

        if (s == "\u00bd")
        {
            number = 0.5;
            return true;
        }
        if (s == "\u00bc")
        {
            number = 0.25;
            return true;
        }
        if (s == "\u00be")
        {
            number = 0.75;
            return true;
        }

        var fraction = Regex.Match(s, @"^(?<a>\d+)\s*/\s*(?<b>\d+)$", RegexOptions.CultureInvariant);
        if (fraction.Success
            && double.TryParse(fraction.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(fraction.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && b != 0)
        {
            number = a / b;
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string FormatScaleNumber(double value)
    {
        var rounded = Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value)
            : Math.Round(value, 2);

        return rounded.ToString(rounded % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)
            .Replace('.', ',');
    }
}
