using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal static class CanonicalRepeatedPageFurnitureClassifier
{
    internal const string InferredQualityFlag =
        "canonical_repeated_page_furniture_inferred";
    internal const string TrimmedQualityFlag =
        "canonical_repeated_page_furniture_trimmed";
    internal const string ClassifierVersion =
        "canonical_margin_frequency_v1";

    private const double MarginSize = 0.15;
    private const double MaximumVerticalSpread = 0.12;
    private const int MaximumCandidateCharacters = 240;
    private const int MaximumCandidateWords = 24;

    private sealed record Candidate(
        CanonicalPage Page,
        CanonicalBlock Block,
        string MarginBand,
        string ExactSignature,
        string FlexibleSignature,
        double VerticalCenter);

    private sealed record FurniturePattern(
        string MarginBand,
        string Signature,
        int DistinctPages);

    private sealed record TextToken(
        int Start,
        int EndExclusive,
        string Value);

    public static void Apply(IReadOnlyCollection<CanonicalPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            return;

        var candidates = pages
            .SelectMany(page => page.Blocks.Select(block =>
                BuildCandidate(page, block)))
            .Where(static candidate => candidate is not null)
            .Cast<Candidate>()
            .ToArray();
        if (candidates.Length == 0)
            return;

        var corroboratedPageThreshold = Math.Max(
            3,
            (int)Math.Ceiling(pages.Count * 0.20));
        var independentPageThreshold = Math.Max(
            3,
            (int)Math.Ceiling(pages.Count * 0.50));
        var patterns = new HashSet<FurniturePattern>();

        foreach (var flexibleGroup in candidates.GroupBy(
                     static candidate =>
                         (candidate.MarginBand, candidate.FlexibleSignature)))
        {
            var distinctPages = CountDistinctPages(flexibleGroup);
            var hasParserFurnitureEvidence = flexibleGroup.Any(
                static candidate => candidate.Block.IsRepeatedFurniture);
            if (hasParserFurnitureEvidence
                && distinctPages >= corroboratedPageThreshold
                && HasStableVerticalPosition(flexibleGroup))
            {
                MarkInferredFurniture(
                    flexibleGroup,
                    distinctPages,
                    pages.Count);
                patterns.Add(new(
                    flexibleGroup.Key.MarginBand,
                    flexibleGroup.Key.FlexibleSignature,
                    distinctPages));
                continue;
            }

            foreach (var exactGroup in flexibleGroup.GroupBy(
                         static candidate => candidate.ExactSignature))
            {
                distinctPages = CountDistinctPages(exactGroup);
                if (distinctPages < independentPageThreshold
                    || !HasStableVerticalPosition(exactGroup))
                {
                    continue;
                }

                MarkInferredFurniture(
                    exactGroup,
                    distinctPages,
                    pages.Count);
                patterns.Add(new(
                    flexibleGroup.Key.MarginBand,
                    exactGroup.Key,
                    distinctPages));
            }
        }

        TrimEmbeddedFurniture(pages, patterns);
    }

    private static Candidate? BuildCandidate(
        CanonicalPage page,
        CanonicalBlock block)
    {
        if (!IsEligibleBlock(block)
            || string.IsNullOrWhiteSpace(block.Text.Canonical)
            || block.Text.Canonical.Length > MaximumCandidateCharacters
            || CountWords(block.Text.Canonical) > MaximumCandidateWords
            || !TryResolveMarginBand(
                block.Polygon,
                out var marginBand,
                out var verticalCenter))
        {
            return null;
        }

        var exactSignature = NormalizeSignature(block.Text.Canonical);
        if (string.IsNullOrWhiteSpace(exactSignature))
            return null;
        var flexibleSignature = RemoveBoundaryPageNumber(exactSignature);
        if (string.IsNullOrWhiteSpace(flexibleSignature))
            flexibleSignature = "<page-number>";

        return new(
            page,
            block,
            marginBand,
            exactSignature,
            flexibleSignature,
            verticalCenter);
    }

    private static bool IsEligibleBlock(CanonicalBlock block)
        => block.IsRepeatedFurniture
           || string.Equals(
               block.BlockType,
               "text",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               block.BlockType,
               "page_header",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               block.BlockType,
               "page_footer",
               StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveMarginBand(
        CanonicalPolygon? polygon,
        out string marginBand,
        out double verticalCenter)
    {
        marginBand = "";
        verticalCenter = 0;
        if (polygon is null || polygon.Points.Count == 0)
            return false;

        verticalCenter = polygon.Points.Average(static point => point.Y);
        if (!double.IsFinite(verticalCenter))
            return false;
        if (verticalCenter <= MarginSize)
        {
            marginBand = "top";
            return true;
        }
        if (verticalCenter >= 1.0 - MarginSize)
        {
            marginBand = "bottom";
            return true;
        }

        return false;
    }

    private static bool HasStableVerticalPosition(
        IEnumerable<Candidate> candidates)
    {
        var positions = candidates
            .Select(static candidate => candidate.VerticalCenter)
            .ToArray();
        return positions.Length > 0
               && positions.Max() - positions.Min()
               <= MaximumVerticalSpread;
    }

    private static int CountDistinctPages(
        IEnumerable<Candidate> candidates)
        => candidates
            .Select(static candidate => candidate.Page.PageNumber)
            .Distinct()
            .Count();

    private static void MarkInferredFurniture(
        IEnumerable<Candidate> candidates,
        int distinctPages,
        int documentPages)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Block.IsRepeatedFurniture)
                continue;

            candidate.Block.IsRepeatedFurniture = true;
            AddDistinct(
                candidate.Block.QualityFlags,
                InferredQualityFlag);
            AddDistinct(
                candidate.Page.QualityFlags,
                InferredQualityFlag);
            candidate.Block.Provenance.Attributes[
                "repeatedFurnitureClassifier"] = ClassifierVersion;
            candidate.Block.Provenance.Attributes[
                "repeatedFurnitureDistinctPages"] =
                distinctPages.ToString(CultureInfo.InvariantCulture);
            candidate.Block.Provenance.Attributes[
                "repeatedFurnitureDocumentPages"] =
                documentPages.ToString(CultureInfo.InvariantCulture);
            candidate.Block.Provenance.Attributes[
                "repeatedFurnitureMarginBand"] = candidate.MarginBand;
            candidate.Block.Provenance.Attributes[
                "repeatedFurnitureSignatureSha256"] =
                SignatureSha256(candidate.FlexibleSignature);
        }
    }

    private static void TrimEmbeddedFurniture(
        IReadOnlyCollection<CanonicalPage> pages,
        IReadOnlyCollection<FurniturePattern> patterns)
    {
        foreach (var page in pages)
        {
            foreach (var block in page.Blocks)
            {
                if (block.IsRepeatedFurniture
                    || IsSemanticStructuralBlock(block)
                    || string.IsNullOrWhiteSpace(block.Text.Retrieval)
                    || !TryResolveMarginBand(
                        block.Polygon,
                        out var marginBand,
                        out _))
                {
                    continue;
                }

                foreach (var pattern in patterns
                             .Where(candidate =>
                                 string.Equals(
                                     candidate.MarginBand,
                                     marginBand,
                                     StringComparison.Ordinal))
                             .OrderByDescending(static candidate =>
                                 candidate.Signature.Length))
                {
                    if (string.Equals(
                            pattern.Signature,
                            "<page-number>",
                            StringComparison.Ordinal)
                        || !TryTrimBoundarySignature(
                            block.Text.Retrieval,
                            pattern.Signature,
                            marginBand,
                            out var trimmed)
                        || CountWords(trimmed) < 3)
                    {
                        continue;
                    }

                    block.Text.Retrieval = trimmed;
                    AddDistinct(
                        block.QualityFlags,
                        TrimmedQualityFlag);
                    AddDistinct(
                        page.QualityFlags,
                        TrimmedQualityFlag);
                    block.Provenance.Attributes[
                        "repeatedFurnitureTrimClassifier"] =
                        ClassifierVersion;
                    block.Provenance.Attributes[
                        "repeatedFurnitureTrimDistinctPages"] =
                        pattern.DistinctPages.ToString(
                            CultureInfo.InvariantCulture);
                    block.Provenance.Attributes[
                        "repeatedFurnitureTrimMarginBand"] =
                        marginBand;
                    block.Provenance.Attributes[
                        "repeatedFurnitureTrimSignatureSha256"] =
                        SignatureSha256(pattern.Signature);
                    break;
                }
            }
        }
    }

    private static bool IsSemanticStructuralBlock(CanonicalBlock block)
        => string.Equals(
               block.BlockType,
               "section_header",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               block.BlockType,
               "title",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               block.BlockType,
               "caption",
               StringComparison.OrdinalIgnoreCase);

    private static bool TryTrimBoundarySignature(
        string value,
        string signature,
        string marginBand,
        out string trimmed)
    {
        trimmed = value;
        var textTokens = Tokenize(value);
        var signatureTokens = signature.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (textTokens.Count <= signatureTokens.Length
            || signatureTokens.Length == 0)
        {
            return false;
        }

        if (string.Equals(
                marginBand,
                "bottom",
                StringComparison.Ordinal))
        {
            var offset = textTokens.Count - signatureTokens.Length;
            if (!TokensMatch(
                    textTokens,
                    offset,
                    signatureTokens))
            {
                return false;
            }

            trimmed = value[..textTokens[offset].Start].TrimEnd();
            return !string.IsNullOrWhiteSpace(trimmed);
        }

        if (!TokensMatch(textTokens, 0, signatureTokens))
            return false;
        trimmed = value[textTokens[signatureTokens.Length - 1].EndExclusive..]
            .TrimStart();
        return !string.IsNullOrWhiteSpace(trimmed);
    }

    private static bool TokensMatch(
        IReadOnlyList<TextToken> textTokens,
        int textOffset,
        IReadOnlyList<string> signatureTokens)
    {
        for (var index = 0; index < signatureTokens.Count; index++)
        {
            if (!string.Equals(
                    textTokens[textOffset + index].Value,
                    signatureTokens[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<TextToken> Tokenize(string value)
    {
        var tokens = new List<TextToken>();
        for (var index = 0; index < value.Length;)
        {
            if (!char.IsLetterOrDigit(value[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length
                   && char.IsLetterOrDigit(value[index]))
            {
                index++;
            }

            tokens.Add(new(
                start,
                index,
                value[start..index]
                    .Normalize(NormalizationForm.FormKC)
                    .ToLowerInvariant()));
        }

        return tokens;
    }

    private static string SignatureSha256(string signature)
        => Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(signature)))
            .ToLowerInvariant();

    private static string NormalizeSignature(string value)
    {
        var normalized = value
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
        var output = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && output.Length > 0)
                    output.Append(' ');
                output.Append(character);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return output.ToString().Trim();
    }

    private static string RemoveBoundaryPageNumber(string signature)
    {
        var tokens = signature.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        var end = tokens.Length;
        if (start < end && IsPageNumberToken(tokens[start]))
            start++;
        if (start < end && IsPageNumberToken(tokens[end - 1]))
            end--;
        return string.Join(' ', tokens[start..end]);
    }

    private static bool IsPageNumberToken(string token)
        => token.Length <= 6
           && token.All(char.IsDigit);

    private static int CountWords(string value)
        => value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;

    private static void AddDistinct(
        ICollection<string> values,
        string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
            values.Add(value);
    }
}
