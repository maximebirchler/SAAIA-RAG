using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Repetition is an observation, never proof that source text is disposable.</summary>
internal static class CanonicalRepeatedPageFurnitureClassifier
{
    internal const string InferredQualityFlag = "canonical_repeated_margin_observed";
    internal const string ClassifierVersion = "canonical_margin_observation_v2";

    public static void Apply(IReadOnlyCollection<CanonicalPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var candidates = pages.SelectMany(page => page.Blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text.Canonical)
                && block.Polygon?.Points.Count > 0
                && (block.IsRepeatedFurniture
                    || string.Equals(block.BlockType, "text", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(block.BlockType, "page_header", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(block.BlockType, "page_footer", StringComparison.OrdinalIgnoreCase)))
            .Select(block => new { Page = page, Block = block,
                Center = block.Polygon!.Points.Average(point => point.Y) }))
            .Where(item => double.IsFinite(item.Center) && (item.Center <= 0.15 || item.Center >= 0.85))
            .GroupBy(item => (Band: item.Center <= 0.15 ? "top" : "bottom", Text: item.Block.Text.Canonical));
        foreach (var group in candidates)
        {
            var repeatedPages = group.Select(item => item.Page.PageNumber).Distinct().Count();
            if (repeatedPages < 2) continue;
            foreach (var item in group)
            {
                if (!item.Block.QualityFlags.Contains(InferredQualityFlag, StringComparer.Ordinal))
                    item.Block.QualityFlags.Add(InferredQualityFlag);
                item.Block.Provenance.Attributes["repeatedFurnitureClassifier"] = ClassifierVersion;
                item.Block.Provenance.Attributes["repeatedFurnitureDistinctPages"] = repeatedPages.ToString(System.Globalization.CultureInfo.InvariantCulture);
                item.Block.Provenance.Attributes["repeatedFurnitureMarginBand"] = group.Key.Band;
                item.Block.Provenance.Attributes["repetitionDecision"] = "source_text_retained_role_not_inferred";
            }
        }
    }
}
