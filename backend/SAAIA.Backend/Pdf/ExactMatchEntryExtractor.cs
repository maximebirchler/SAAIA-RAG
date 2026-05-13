using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class ExactMatchEntryExtractor
{
    public static IReadOnlyList<ExtractedExactMatchEntry> Extract(IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (units.Count == 0)
            return Array.Empty<ExtractedExactMatchEntry>();

        var entries = new List<ExtractedExactMatchEntry>();
        var entryIndex = 0;

        foreach (var unit in units.OrderBy(u => u.Ordinal))
        {
            var normalizedUnit = NormalizeWhitespace(unit.Text);
            if (string.IsNullOrWhiteSpace(normalizedUnit))
                continue;
            if (!IsExactMatchContentUnit(normalizedUnit))
                continue;

            var references = ExtractTargetedReferences(normalizedUnit).ToList();
            var referenceVariants = references
                .SelectMany(ExpandReferenceVariants)
                .Select(NormalizeWhitespace)
                .Where(static v => !string.IsNullOrWhiteSpace(v));

            var restrictToTargetedReferences = ExtractionQualityPolicy.ShouldRestrictUnitToTargetedReferences(unit);
            var candidates = (restrictToTargetedReferences ? Enumerable.Empty<string>() : SplitCandidates(normalizedUnit))
                .Concat(references)
                .Concat(referenceVariants)
                .Select(NormalizeWhitespace)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                candidates.Add(normalizedUnit);

            foreach (var candidate in candidates)
            {
                var tokenCount = CountTokens(candidate);
                if (tokenCount <= 0)
                    continue;

                var normalizedText = NormalizeForLookup(candidate);
                if (string.IsNullOrWhiteSpace(normalizedText))
                    continue;

                var kind = InferEntryKind(candidate);

                entries.Add(new ExtractedExactMatchEntry(
                    EntryIndex: entryIndex++,
                    SectionOrdinal: unit.SectionOrdinal,
                    UnitOrdinal: unit.Ordinal,
                    PageStart: unit.PageStart,
                    PageEnd: unit.PageEnd,
                    Text: candidate,
                    NormalizedText: normalizedText,
                    CharCount: candidate.Length,
                    TokenCount: tokenCount,
                    Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)),
                    Kind: kind,
                    OffsetStart: ResolveOffsetStart(unit, candidate),
                    OffsetEnd: ResolveOffsetEnd(unit, candidate)));
            }
        }

        return entries;
    }

    private static bool IsExactMatchContentUnit(string text)
    {
        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        if (string.Equals(signal.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.Ordinal))
            return false;

        return !string.Equals(signal.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.Ordinal)
               || !IsCatalogLikeNavigationReason(signal.NavigationReason);
    }

    private static bool IsCatalogLikeNavigationReason(string? reason)
        => reason is "inline_page_number_list"
            or "numeric_title_catalog"
            or "title_list_with_page_refs"
            or "compact_title_catalog_with_page_refs"
            or "dense_title_catalog"
            or "title_list_shape";

    internal static IReadOnlyList<string> ExtractLookupTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var terms = new List<string>();
        var normalizedWhole = NormalizeForLookup(text);
        if (!string.IsNullOrWhiteSpace(normalizedWhole))
            terms.Add(normalizedWhole);

        foreach (var reference in ExtractTargetedReferences(text))
        {
            var normalizedReference = NormalizeForLookup(reference);
            if (!string.IsNullOrWhiteSpace(normalizedReference))
                terms.Add(normalizedReference);

            // Generate sub-variants for composite references:
            // "STD 12345" -> also produce "12345"
            // "ORG TYPE 12345" -> also produce "type 12345", "12345"
            foreach (var variant in ExpandReferenceVariants(reference))
            {
                var normalizedVariant = NormalizeForLookup(variant);
                if (!string.IsNullOrWhiteSpace(normalizedVariant))
                    terms.Add(normalizedVariant);
            }
        }

        foreach (Match match in ReferenceKeyRegex().Matches(text))
        {
            var normalizedKey = NormalizeForLookup(match.Value);
            if (!string.IsNullOrWhiteSpace(normalizedKey))
                terms.Add(normalizedKey);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(term => term.Length)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractReferenceKeys(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in ExtractTargetedReferences(text))
        {
            foreach (Match match in ReferenceKeyRegex().Matches(reference))
            {
                var key = NormalizeWhitespace(match.Value).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(key))
                    keys.Add(key);
            }
        }

        return keys
            .OrderByDescending(static key => key.Length)
            .ToArray();
    }

    private static IEnumerable<string> SplitCandidates(string text)
    {
        var sentences = SentenceSplitRegex()
            .Split(text)
            .Select(NormalizeWhitespace)
            .Where(static sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        if (sentences.Count <= 1)
            return [text];

        var goodSentences = sentences
            .Where(static sentence => sentence.Length is >= 40 and <= 400)
            .Where(static sentence => CountTokens(sentence) >= 5)
            .ToList();

        return goodSentences.Count > 0 ? goodSentences : [text];
    }

    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    internal static string NormalizeForLookup(string text)
    {
        var compact = NormalizeWhitespace(text).ToLowerInvariant();
        compact = ExactPunctuationRegex().Replace(compact, " ");
        return Regex.Replace(compact, @"\s+", " ").Trim();
    }

    internal static IEnumerable<string> ExtractTargetedReferences(string text)
    {
        foreach (Match match in StandardReferenceRegex().Matches(text))
        {
            var value = NormalizeWhitespace(match.Value);
            if (!string.IsNullOrWhiteSpace(value)
                && !HasGenericReferenceLeadToken(value)
                && IsPlausibleTargetedReference(value))
            {
                yield return value;
            }
        }

        foreach (Match match in SpacedCodeReferenceRegex().Matches(text))
        {
            var value = NormalizeWhitespace(match.Value);
            if (!string.IsNullOrWhiteSpace(value)
                && !HasGenericReferenceLeadToken(value)
                && IsPlausibleTargetedReference(value))
            {
                yield return value;
            }
        }

        foreach (Match match in CodeReferenceRegex().Matches(text))
        {
            var value = NormalizeWhitespace(match.Value);
            if (!string.IsNullOrWhiteSpace(value)
                && !HasGenericReferenceLeadToken(value)
                && IsPlausibleTargetedReference(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// For a composite reference like "ORG TYPE 12345" or "STD 12345", produce sub-variants:
    /// - Strip known prefixes progressively (EN/ISO/IEC/CEN/TR/etc.)
    /// - Extract bare numeric core (e.g., "12345")
    /// This allows cross-matching between shorter and longer forms of the same reference.
    /// </summary>
    internal static IEnumerable<string> ExpandReferenceVariants(string reference)
    {
        var normalized = NormalizeWhitespace(reference);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var compactAlphaNum = RemoveReferenceSeparators(normalized);
        if (!string.Equals(compactAlphaNum, normalized, StringComparison.OrdinalIgnoreCase)
            && compactAlphaNum.Any(char.IsLetter)
            && compactAlphaNum.Any(char.IsDigit))
        {
            yield return compactAlphaNum;
        }

        var compactStandardMatch = CompactStandardReferenceRegex().Match(compactAlphaNum);
        if (compactStandardMatch.Success)
        {
            var prefix = compactStandardMatch.Groups["prefix"].Value.ToUpperInvariant();
            var suffix = compactStandardMatch.Groups["suffix"].Value;
            var spaced = $"{prefix} {suffix}";
            if (!string.Equals(spaced, normalized, StringComparison.OrdinalIgnoreCase))
                yield return spaced;

            var numericCore = LeadingDigitsRegex().Match(suffix);
            if (numericCore.Success)
                yield return numericCore.Value;
        }

        var parts = Regex.Split(normalized, @"[\s._/\-]+")
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length <= 1)
            yield break;

        // Progressively strip leading prefix tokens
        // "ORG TYPE 12345" -> "TYPE 12345" -> "12345"
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "EN", "ISO", "IEC", "ASTM", "DIN", "NFPA", "API", "ANSI", "CEN", "TR", "TS", "PD", "BS" };

        for (var i = 1; i < parts.Length; i++)
        {
            if (!prefixes.Contains(parts[i - 1]))
                break;
            var sub = string.Join(" ", parts[i..]);
            if (!string.IsNullOrWhiteSpace(sub))
                yield return sub;
        }

        // Also extract bare numeric core if present
        foreach (var part in parts)
        {
            if (part.Length >= 4 && part.All(char.IsDigit))
            {
                yield return part;
                break; // Only first numeric core
            }
        }
    }

    private static string InferEntryKind(string candidate)
    {
        if (IsStandaloneRegexMatch(candidate, StandardReferenceRegex())
            && IsPlausibleTargetedReference(candidate))
        {
            return "standard_ref";
        }

        if (IsStandaloneRegexMatch(candidate, CodeReferenceRegex())
            && IsPlausibleTargetedReference(candidate))
        {
            return "code_ref";
        }

        return "verbatim_excerpt";
    }

    private static bool IsStandaloneRegexMatch(string candidate, Regex regex)
    {
        var normalized = NormalizeWhitespace(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var match = regex.Match(normalized);
        return match.Success
            && string.Equals(NormalizeWhitespace(match.Value), normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int? ResolveOffsetStart(ExtractedDocumentUnit unit, string candidate)
    {
        if (unit.OffsetStart is null || string.IsNullOrWhiteSpace(candidate))
            return null;

        var index = unit.Text.IndexOf(candidate, StringComparison.Ordinal);
        return index >= 0
            ? unit.OffsetStart.Value + index
            : null;
    }

    private static int? ResolveOffsetEnd(ExtractedDocumentUnit unit, string candidate)
    {
        var start = ResolveOffsetStart(unit, candidate);
        return start is null
            ? null
            : start.Value + candidate.Length;
    }

    [GeneratedRegex(@"(?<=[\.\!\?\;\:])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPunctuationRegex();

    [GeneratedRegex(@"\b(?:EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR)[\s._/\-]*(?:[A-Z]{1,4}[\s._/\-]*)?\d[\w\-\/\.:]*\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardReferenceRegex();

    [GeneratedRegex(@"\b(?!(?:EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR|TS|PD|BS)\b)(?=[A-Z][A-Z0-9\s._/\-]{4,40}\b)(?=[A-Z0-9\s._/\-]*[A-Z])(?=[A-Z0-9\s._/\-]*\d)[A-Z]{3,10}(?:[\s._/\-]+\d[\w\-\/\.:]*)+\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SpacedCodeReferenceRegex();

    [GeneratedRegex(@"\b(?=[A-Z0-9._/\-]{4,40}\b)(?=[A-Z0-9._/\-]*[A-Z])(?=[A-Z0-9._/\-]*\d)[A-Z0-9][A-Z0-9._/\-]{2,39}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CodeReferenceRegex();

    [GeneratedRegex(@"^(?<prefix>EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR|TS|PD|BS)(?<suffix>\d[\w\-\/\.:]*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CompactStandardReferenceRegex();

    [GeneratedRegex(@"\b\d{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceKeyRegex();

    [GeneratedRegex(@"^\d{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDigitsRegex();

    [GeneratedRegex(@"^(?:en|iso|iec|astm|din|nfpa|api|ansi|cen|tr|ts|pd|bs)[\s._/\-]*(?<digits>\d{1,3})(?:\b|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortLowercaseStandardWordCollisionRegex();

    private static string RemoveReferenceSeparators(string text)
        => Regex.Replace(text, @"[\s._/\-]+", string.Empty);

    private static bool HasGenericReferenceLeadToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var firstToken = Regex.Split(NormalizeWhitespace(value), @"[\s._/\-]+")
            .FirstOrDefault(static token => !string.IsNullOrWhiteSpace(token));
        if (string.IsNullOrWhiteSpace(firstToken))
            return false;

        return GenericReferenceLeadTokens.Contains(firstToken.ToLowerInvariant());
    }

    private static bool IsPlausibleTargetedReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var compact = NormalizeWhitespace(value);
        var shortLowercaseStandard = ShortLowercaseStandardWordCollisionRegex().Match(compact);
        if (!shortLowercaseStandard.Success)
            return true;

        var digits = shortLowercaseStandard.Groups["digits"].Value;
        return digits.Length >= 4;
    }

    private static readonly HashSet<string> GenericReferenceLeadTokens = new(StringComparer.Ordinal)
    {
        "pdf",
        "doc",
        "document",
        "manuel",
        "manual",
        "notice",
        "terminal",
        "guide"
    };
}

internal sealed record ExtractedExactMatchEntry(
    int EntryIndex,
    int? SectionOrdinal,
    int UnitOrdinal,
    int PageStart,
    int PageEnd,
    string Text,
    string NormalizedText,
    int CharCount,
    int TokenCount,
    byte[] Checksum,
    string Kind,
    int? OffsetStart = null,
    int? OffsetEnd = null);
