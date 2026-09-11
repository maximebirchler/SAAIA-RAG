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

    private static void AppendFactList(StringBuilder sb, string title, IReadOnlyList<string> items, string language)
    {
        sb.Append("- ");
        sb.Append(title);
        sb.Append(" : ");
        sb.AppendLine(items.Count == 0 ? SourceBackedNotVisibleLabel(language) : string.Join("; ", items));
    }

    private static string[] ExtractContentCardEvidenceFacts(IEnumerable<RagHitSummary> hits)
        => ExtractContentCardQuantityEvidenceFacts(hits)
            .Concat(ExtractContentCardGenericEvidenceFacts(hits))
            .Concat(ExtractContentCardNonScalableReasons(hits))
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ExtractContentCardQuantityEvidenceFacts(IEnumerable<RagHitSummary> hits)
    {
        var facts = new List<string>();
        foreach (var card in hits
                     .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                     .Where(static card => card.Evidence is not null))
        {
            var evidence = card.Evidence!;
            if (evidence.ScaleBasis is { Count: > 0 } basis)
            {
                var label = CollapseWhitespace(basis.Label ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(label) && basis.Count is >= 1 and <= 50)
                {
                    facts.Add(CleanContentCardDisplayFactValue(CollapseWhitespace(string.Join(' ', new[]
                    {
                        basis.Count.ToString(CultureInfo.InvariantCulture),
                        label
                    }))));
                }
            }

            foreach (var fact in evidence.QuantityFacts.Take(8))
            {
                facts.Add(BuildContentCardDisplayFact(
                    fact.SourceText,
                    fact.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    fact.Unit,
                    fact.Label));
            }
        }

        return facts
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Where(IsUsefulContentCardDisplayFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ExtractContentCardGenericEvidenceFacts(IEnumerable<RagHitSummary> hits)
    {
        var facts = new List<string>();
        foreach (var evidence in hits
                     .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                     .Select(static card => card.Evidence)
                     .Where(static evidence => evidence is not null))
        {
            var hasStructuredQuantities = evidence!.ScaleBasis is not null || evidence.QuantityFacts.Count > 0;
            foreach (var fact in (evidence!.Facts ?? []).Take(12))
            {
                if (hasStructuredQuantities && IsContentCardQuantityLikeGenericFact(fact))
                    continue;

                facts.Add(BuildContentCardDisplayFact(
                    fact.SourceText,
                    fact.Label,
                    fact.Value,
                    fact.Unit));
            }
        }

        return facts
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Where(IsUsefulContentCardDisplayFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsContentCardQuantityLikeGenericFact(RagHitEvidenceFactSummary fact)
    {
        var kind = NormalizeLexicalLookup(fact.Kind);
        if (kind is "quantity" or "quantities" or "duration" or "durations")
            return true;

        if (double.TryParse(fact.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return true;

        var unit = NormalizeLexicalLookup(fact.Unit);
        if (!string.IsNullOrWhiteSpace(unit)
            && double.TryParse(fact.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        return false;
    }

    private static string BuildContentCardDisplayFact(string? sourceText, params string?[] structuredParts)
    {
        var structured = CollapseWhitespace(string.Join(' ', structuredParts.Where(static value => !string.IsNullOrWhiteSpace(value))));
        var source = CollapseWhitespace(sourceText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source))
            return CleanContentCardDisplayFactValue(structured);

        if (!string.IsNullOrWhiteSpace(structured)
            && (source.Length > 90 || LooksLikeNoisyContentCardSourceText(source)))
        {
            return CleanContentCardDisplayFactValue(structured);
        }

        var value = !string.IsNullOrWhiteSpace(structured) ? structured : source;
        return CleanContentCardDisplayFactValue(value);
    }

    private static string CleanContentCardDisplayFactValue(string value)
    {
        value = Regex.Replace(value, @"(?i)\bscale_basis\b", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(\d+(?:[\.,]\d+)?)\s*(g|kg|mg|ml|cl|l|min|s|h|cm|mm)(?=\p{L}{3,})", "$1 $2 ", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(\p{L}{4,})(cette|celui|celle|this|that)\b", "$1", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b\d{5,}\b", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\s+\+\s+.*$", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(?:preparation|pr\u00e9paration|operation|workflow|execution|procedure)\b.*$", string.Empty, RegexOptions.CultureInvariant);
        value = CollapseWhitespace(value.Trim(' ', ';', ',', ':', '-'));
        return FormatReadableEvidenceExcerpt(value, maxLength: 90);
    }

    private static bool IsUsefulContentCardDisplayFact(string fact)
    {
        fact = CollapseWhitespace(fact);
        if (fact.Length < 3)
            return false;

        var normalized = NormalizeLexicalLookup(fact);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"^\d+(?:[\.,]\d+)?$", RegexOptions.CultureInvariant))
            return false;

        return !normalized.Contains("scale_basis", StringComparison.Ordinal);
    }

    private static bool LooksLikeNoisyContentCardSourceText(string source)
    {
        var normalized = NormalizeLexicalLookup(source);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.Contains("scale_basis", StringComparison.Ordinal)
            || Regex.Matches(source, @"[;,+]").Count >= 4
            || Regex.Matches(source, @"\p{L}{2,}").Count > 18;
    }

    private static string[] ExtractContentCardNonScalableReasons(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Select(static card => card.Evidence)
            .Where(static evidence => evidence is not null)
            .SelectMany(static evidence => evidence!.NonScalableReasons)
            .Select(CollapseWhitespace)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool HasContentCardEvidenceFacts(RagHitSummary hit)
        => ExtractContentCardEvidenceFacts(new[] { hit }).Length > 0;

    private static bool HasContentCardEvidence(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(static card => card.Evidence is not null);

    private static bool HasContentCardQuantityEvidence(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(static card => card.Evidence?.ScaleBasis is not null || card.Evidence?.QuantityFacts.Count > 0);

    private static bool HasContentCardNonScalableEvidence(RagHitSummary hit)
        => hit.MatchedContentCards?
            .Any(static card => card.Evidence?.NonScalableReasons.Count > 0) == true;

    private static bool HasContentCardScalableQuantityEvidence(RagHitSummary hit)
        => hit.MatchedContentCards?
            .Any(static card => card.Evidence?.ScaleBasis is { Count: > 0 }
                                && card.Evidence.NonScalableReasons.Count == 0
                                && card.Evidence.QuantityFacts.Count >= 2) == true;

    private static bool ContainsProcedureEvidenceCue(string text)
    {
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:" + ProcedureSectionHeadingPattern + @")\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(text, @"(?:^|\s)(?:[1-9][\.)]\s+|[\u2022\u00b7]\s+\p{L})", RegexOptions.CultureInvariant);
    }

}
