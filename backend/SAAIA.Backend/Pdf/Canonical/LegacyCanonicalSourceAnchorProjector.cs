using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal static class LegacyCanonicalSourceAnchorProjector
{
    public static SourceAnchor ProjectPageRange(
        CanonicalDocument document,
        string projectionId,
        string projectionType,
        int pageStart,
        int pageEnd)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        if (pageStart <= 0 || pageEnd < pageStart)
            throw new ArgumentOutOfRangeException(nameof(pageStart), "Page range is invalid.");

        var regions = document.Pages
            .Where(page => page.PageNumber >= pageStart && page.PageNumber <= pageEnd)
            .OrderBy(static page => page.PageNumber)
            .Select(page =>
            {
                var blocks = page.Blocks
                    .OrderBy(static block => block.ReadingOrder)
                    .ThenBy(static block => block.Ordinal)
                    .ToArray();
                var rawText = string.Join(
                    "\n",
                    blocks
                        .Select(static block => block.Text.Raw)
                        .Where(static text => !string.IsNullOrWhiteSpace(text)));

                return new SourceAnchorRegion
                {
                    PageNumber = page.PageNumber,
                    BlockIds = blocks.Select(static block => block.BlockId).ToList(),
                    RawTextSha256 = Sha256(rawText)
                };
            })
            .ToList();

        var anchor = new SourceAnchor
        {
            AnchorId = CanonicalStableId.Create(
                "anchor",
                document.Source.Sha256,
                document.RevisionId.ToString("D"),
                projectionType,
                projectionId),
            DocumentId = document.DocumentId,
            RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256,
            ManifestSha256 = document.ManifestSha256,
            ProjectionId = projectionId,
            ProjectionType = projectionType,
            Precision = "page_block",
            Regions = regions
        };

        CanonicalContractValidator.ValidateOrThrow(anchor, document);
        return anchor;
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
