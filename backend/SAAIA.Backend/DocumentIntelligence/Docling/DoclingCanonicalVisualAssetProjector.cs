using System.Globalization;
using System.Security.Cryptography;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingCanonicalVisualAssetProjector
{
    internal static void LinkPageAssets(CanonicalDocument document)
    {
        foreach (var page in document.Pages)
        foreach (var figure in page.Figures)
        {
            if (figure.AssetIds.Count > 0) continue;
            var pageAsset = document.Assets.FirstOrDefault(asset => asset.PageNumber == page.PageNumber && asset.FigureId is null);
            figure.Provenance.Attributes["visualAssetStatus"] = pageAsset is null ? "not_exported" : "page_available";
            if (pageAsset is not null) figure.AssetIds.Add(pageAsset.AssetId);
        }
    }

    internal static void Apply(CanonicalDocument document, DoclingDocument source)
    {
        long totalBytes = 0;
        var exportedPages = source.Pages.ToDictionary(pair => pair.Value.PageNumber > 0
            ? pair.Value.PageNumber : int.Parse(pair.Key, CultureInfo.InvariantCulture), pair => pair.Value);
        foreach (var page in document.Pages)
        {
            var exported = exportedPages[page.PageNumber];
            if (exported.Image is not null) Add(exported.Image, page.PageNumber, null);
            foreach (var figure in page.Figures)
                figure.Provenance.Attributes["visualAssetStatus"] = "not_exported";
        }
        foreach (var item in source.Pictures)
        for (var index = 0; index < item.Provenance.Count; index++)
        {
            var page = document.Pages.Single(page => page.PageNumber == item.Provenance[index].PageNumber);
            var figureId = CanonicalStableId.Create("figure", document.Source.Sha256, item.SelfRef,
                page.PageNumber.ToString(CultureInfo.InvariantCulture), index.ToString(CultureInfo.InvariantCulture));
            var figure = page.Figures.Single(figure => figure.FigureId == figureId);
            if (item.Image is not null)
            {
                var asset = Add(item.Image, page.PageNumber, figureId);
                figure.AssetIds.Add(asset.AssetId);
                figure.Provenance.Attributes["visualAssetStatus"] = "available";
            }
            else if (document.Assets.FirstOrDefault(asset => asset.PageNumber == page.PageNumber && asset.FigureId is null) is { } pageAsset)
            {
                // The whole page contains the figure. This is not a claim that
                // a crop or its semantic relationships were extracted.
                figure.AssetIds.Add(pageAsset.AssetId);
                figure.Provenance.Attributes["visualAssetStatus"] = "page_available";
            }
        }

        CanonicalAsset Add(DoclingImageReference image, int page, string? figure)
        {
            var png = CanonicalPngAsset.ReadEmbedded(image);
            totalBytes += png.Bytes.Length;
            if (document.Assets.Count >= 512 || totalBytes > 128L * 1024 * 1024)
                throw new InvalidDataException("canonical_visual_asset_document_budget_exceeded");
            var hash = Convert.ToHexString(SHA256.HashData(png.Bytes)).ToLowerInvariant();
            var assetId = CanonicalStableId.Create("asset", document.RevisionId.ToString("D"), document.Source.Sha256,
                page.ToString(CultureInfo.InvariantCulture), figure ?? "physical_page", hash);
            var asset = new CanonicalAsset
            {
                AssetId = assetId, MediaType = "image/png", Sha256 = hash,
                StorageKey = $"sqlasset://revision/{document.RevisionId:D}/{assetId}", SizeBytes = png.Bytes.Length,
                PageNumber = page, FigureId = figure, PixelWidth = png.Width, PixelHeight = png.Height,
                PublicationBytes = png.Bytes
            };
            document.Assets.Add(asset);
            return asset;
        }
    }
}
