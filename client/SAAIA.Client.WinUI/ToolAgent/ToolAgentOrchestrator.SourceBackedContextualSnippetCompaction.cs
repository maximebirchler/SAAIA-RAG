using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string CompactContextualSnippetForPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var raw = value.Trim();
        var lines = Regex.Split(raw, @"\r?\n")
            .Select(CollapseWhitespace)
            .Where(static line => line.Length > 0)
            .ToList();

        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Matched profile title:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Document:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Section:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HeadingPath:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("ChunkType:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(line);
            }
        }

        var excerpt = ExtractContextualSnippetBlock(raw, "Excerpt:")
            ?? ExtractContextualSnippetBlock(raw, "Context:");
        if (!string.IsNullOrWhiteSpace(excerpt))
            kept.Add("Evidence: " + TruncateForPrompt(excerpt, RagWriterContextualEvidenceChars));

        var hasMatchedProfileTitle = raw.Contains("Matched profile title:", StringComparison.OrdinalIgnoreCase);
        var previous = ExtractContextualSnippetBlock(raw, "PreviousContext:");
        if (hasMatchedProfileTitle
            && !string.IsNullOrWhiteSpace(previous)
            && ShouldIncludePreviousContextualEvidence(excerpt, previous))
        {
            kept.Add("PreviousEvidence: " + TruncateForPrompt(previous, RagWriterContextualEvidenceChars));
        }

        var next = ExtractContextualSnippetBlock(raw, "NextContext:");
        if (!string.IsNullOrWhiteSpace(next) && ShouldIncludeNextContextualEvidence(excerpt, next))
        {
            kept.Add("NextEvidence: " + TruncateForPrompt(next, RagWriterContextualEvidenceChars));
        }

        if (kept.Count == 0)
            return TruncateForPrompt(raw, RagWriterContextualRawChars);

        return TruncateForPrompt(string.Join(" | ", kept.Distinct(StringComparer.OrdinalIgnoreCase)), RagWriterContextualTotalChars);
    }

    private static bool ShouldIncludePreviousContextualEvidence(string? currentEvidence, string? previousEvidence)
    {
        if (string.IsNullOrWhiteSpace(previousEvidence))
            return false;

        var current = CollapseWhitespace(currentEvidence ?? string.Empty);
        var previous = CollapseWhitespace(previousEvidence);
        if (previous.Length < 40)
            return false;

        if (ContainsStructuredItemHeading(current))
            return false;

        var previousQuantitySignals = CountQuantityLikeSignals(previous);
        return previousQuantitySignals >= 3;
    }

    private static bool ShouldIncludeNextContextualEvidence(string? currentEvidence, string? nextEvidence)
    {
        if (string.IsNullOrWhiteSpace(nextEvidence))
            return false;

        var current = CollapseWhitespace(currentEvidence ?? string.Empty);
        var next = CollapseWhitespace(nextEvidence);
        if (next.Length < 40)
            return false;

        if (ContainsStructuredItemHeading(next) && !ContainsStructuredItemHeading(current))
            return true;

        if (CountQuantityLikeSignals(next) >= 2
            && Regex.IsMatch(next, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (CountQuantityLikeSignals(next) >= 3 && LooksLikeDenseDelimitedEvidence(next))
            return true;

        if (Regex.IsMatch(next, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(current, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsStructuredItemHeading(string value)
        => Regex.IsMatch(
            NormalizeLexicalLookup(value),
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|preparacion|operation|workflow|execution|technique|procedure|method|methode)\b",
            RegexOptions.CultureInvariant);

    private static bool LooksLikeDelimitedTargetFact(string value)
        => Regex.Matches(value, @"[,;]").Count >= 1
           && Regex.Matches(value, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant).Count >= 3;

    private static bool LooksLikeDenseDelimitedEvidence(string value)
        => Regex.Matches(value, @"[,;]").Count >= 2
           || Regex.Matches(
                   value,
                   @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|h|min|minutes?|seconds?|secondes?|%|\p{L}{4,})\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count >= 4;

    private static int CountQuantityLikeSignals(string value)
    {
        var normalized = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;
        normalized = Regex.Replace(normalized, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);

        return Regex.Matches(
                normalized,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|h|min|minutes?|seconds?|secondes?|Â°?\s*c|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|\p{L}{4,})\b",
                RegexOptions.CultureInvariant)
            .Count;
    }

    private static string? ExtractContextualSnippetBlock(string raw, string marker)
    {
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var start = idx + marker.Length;
        var rest = raw[start..];
        var next = Regex.Match(rest, @"\r?\n\r?\n(?:PreviousContext|NextContext|Excerpt|Context):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (next.Success && next.Index > 0)
            rest = rest[..next.Index];

        return CollapseWhitespace(rest);
    }
}
