using SAAIA.Contracts.DocumentIntelligence;

internal readonly record struct CanonicalVisualAssetBudget(int MaximumAssets, long MaximumBytes)
{
    internal static CanonicalVisualAssetBudget Default => new(
        CanonicalVisualAssetPolicy.MaximumDocumentAssets, CanonicalVisualAssetPolicy.MaximumDocumentBytes);

    internal void Validate()
    {
        if (MaximumAssets is < 0 or > CanonicalVisualAssetPolicy.MaximumDocumentAssets
            || MaximumBytes is < 0 or > CanonicalVisualAssetPolicy.MaximumDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(CanonicalVisualAssetBudget));
    }
}

internal sealed record CanonicalVisualAssetMeasurements(
    long AssetCount, long LogicalBytes, long PageAssets, long FigureAssets,
    int UniqueImageReferences, long UniqueImageBytes);

internal sealed class CanonicalVisualAssetBudgetException(
    string dimension, string phase, CanonicalVisualAssetMeasurements measurements,
    CanonicalVisualAssetBudget budget) : IOException("canonical_visual_asset_document_" + dimension + "_budget_exceeded")
{
    internal string Dimension { get; } = dimension;
    internal string Phase { get; } = phase;
    internal CanonicalVisualAssetMeasurements Measurements { get; } = measurements;
    internal CanonicalVisualAssetBudget Budget { get; } = budget;
}

/// <summary>One logical publication policy for projection and immutable SQL storage.</summary>
internal static class CanonicalVisualAssetPolicy
{
    internal const int MaximumDocumentAssets = 2048;
    // Logical publication budgets are independent of response transport size.
    // A transport profile above the 512 MiB default does not raise this byte cap.
    // Each provenance still counts separately, even when pixel arrays are shared.
    internal const long MaximumDocumentBytes = 384L * 1024 * 1024;

    internal static CanonicalVisualAssetMeasurements MeasureSource(DoclingDocument source)
    {
        var images = new Dictionary<DoclingImageReference, long>(ReferenceEqualityComparer.Instance);
        long pageAssets = 0, figureAssets = 0, logicalBytes = 0, uniqueBytes = 0;
        foreach (var page in source.Pages.Values)
            if (page.Image is { } image) Count(image, 1, pageImage: true);
        foreach (var picture in source.Pictures)
            if (picture.Image is { } image) Count(image, picture.Provenance.Count, pageImage: false);
        return new(pageAssets + figureAssets, logicalBytes, pageAssets, figureAssets, images.Count, uniqueBytes);

        void Count(DoclingImageReference image, int copies, bool pageImage)
        {
            if (copies == 0) return;
            if (!images.TryGetValue(image, out var bytes))
            {
                bytes = CanonicalPngAsset.MeasureEmbeddedByteLength(image);
                images.Add(image, bytes);
                uniqueBytes = checked(uniqueBytes + bytes);
            }
            logicalBytes = checked(logicalBytes + bytes * copies);
            if (pageImage) pageAssets += copies; else figureAssets += copies;
        }
    }

    internal static CanonicalVisualAssetMeasurements MeasurePublication(IReadOnlyList<CanonicalAsset> assets)
    {
        var images = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long logicalBytes = 0, uniqueBytes = 0, pages = 0;
        foreach (var asset in assets)
        {
            if (asset.SizeBytes is < 0 or > CanonicalPngAsset.MaximumBytes
                || asset.PublicationBytes is { } payload && payload.LongLength != asset.SizeBytes)
                throw new InvalidDataException("canonical_visual_asset_identity_invalid");
            logicalBytes = checked(logicalBytes + asset.SizeBytes);
            if (asset.FigureId is null) pages++;
            if (asset.PublicationBytes is { } bytes && images.Add(bytes)) uniqueBytes += bytes.LongLength;
        }
        return new(assets.Count, logicalBytes, pages, assets.Count - pages, images.Count, uniqueBytes);
    }

    internal static void RequireWithinBudget(CanonicalVisualAssetMeasurements measurements,
        CanonicalVisualAssetBudget budget, string phase)
    {
        budget.Validate();
        if (measurements.AssetCount > budget.MaximumAssets)
            throw new CanonicalVisualAssetBudgetException("count", phase, measurements, budget);
        if (measurements.LogicalBytes > budget.MaximumBytes)
            throw new CanonicalVisualAssetBudgetException("bytes", phase, measurements, budget);
    }
}
