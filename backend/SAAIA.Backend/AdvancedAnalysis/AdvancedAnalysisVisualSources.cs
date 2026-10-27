using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisVisualObservedRegion(
    string EvidenceId,
    string AnchorId,
    string Precision,
    IReadOnlyList<string> BlockIds,
    CanonicalPolygon Polygon);

internal sealed record AdvancedAnalysisVisualSourceReference(
    string DocId,
    string RevisionId,
    string SourceSha256,
    int PhysicalPage,
    string AssetId,
    string ContentSha256,
    string MediaType,
    int PixelWidth,
    int PixelHeight,
    long SizeBytes,
    string AuthenticatedApiPath,
    string ReferenceKind,
    string? FigureId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdvancedAnalysisVisualObservedRegion? ObservedRegion);

internal sealed record AdvancedAnalysisVisualSources(
    string Status,
    string DocId,
    string RevisionId,
    string SourceSha256,
    int PageStart,
    int PageEnd,
    long? AvailableAssetCount,
    int? ReturnedAssetCount,
    int AssetOffset,
    int AssetLimit,
    int? NextAssetOffset,
    long? OmittedAssetCount,
    bool? AuthenticatedPixelReadAvailable,
    bool? HumanViewerAvailable,
    bool ImageToolAvailable,
    bool ModelPixelsProvided,
    string InterpretationStatus,
    string SemanticAccuracy,
    IReadOnlyList<AdvancedAnalysisVisualSourceReference>? Assets,
    int SerializedWrapperCharacters);

internal static class AdvancedAnalysisVisualSourceProjector
{
    // Match the provider's actual JSON encoder, including observed anchor IDs.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static AdvancedAnalysisVisualSources Project(
        Guid document,
        Guid revision,
        string sourceSha,
        int first,
        int last,
        bool scopeExists,
        long? total,
        IReadOnlyList<CanonicalVisualAssetDescriptor>? assets,
        int offset,
        int limit,
        int maximumCharacters,
        IReadOnlyDictionary<int, AdvancedAnalysisVisualObservedRegion>? regions = null)
    {
        Require(document != Guid.Empty && revision != Guid.Empty && IsHash(sourceSha)
            && first > 0 && last >= first && last - first < 4
            && offset is >= 0 and <= CanonicalVisualAssetRepo.MaximumDocumentAssets
            && limit is >= 1 and <= 64 && maximumCharacters >= 256);
        var normalizedHash = sourceSha.ToLowerInvariant();

        AdvancedAnalysisVisualSources Build(
            string status,
            IReadOnlyList<AdvancedAnalysisVisualSourceReference>? rows,
            int? next)
        {
            var result = new AdvancedAnalysisVisualSources(
                status, document.ToString("D"), revision.ToString("D"), normalizedHash,
                first, last, total, rows?.Count, offset, limit, next,
                rows is null ? null : total - rows.Count,
                scopeExists ? total > 0 : null, null, false, false,
                "not_performed", "not_verified", rows, 0);

            // Include the decimal length field itself in the measured wrapper.
            for (var pass = 0; pass < 8; pass++)
            {
                var actual = JsonSerializer.Serialize(new { visualSources = result }, WireJson).Length;
                if (result.SerializedWrapperCharacters == actual)
                    return result;

                result = result with { SerializedWrapperCharacters = actual };
            }

            throw new AdvancedAnalysisToolException("visual_source_json_measurement_failed");
        }

        if (!scopeExists)
        {
            Require(total is null && assets is null);
            var unknown = Build("source_scope_unavailable", null, null);
            RequireBudget(unknown, maximumCharacters);
            return unknown;
        }

        var knownTotal = total.GetValueOrDefault(-1);
        Require(knownTotal is >= 0 and <= CanonicalVisualAssetRepo.MaximumDocumentAssets
            && assets is not null
            && assets.Count == Math.Min(limit, Math.Max(0, knownTotal - offset)));

        var projected = new List<AdvancedAnalysisVisualSourceReference>();
        foreach (var asset in assets!)
        {
            Require(asset.PageNumber >= first && asset.PageNumber <= last
                && string.Equals(asset.SourceSha256, sourceSha, StringComparison.OrdinalIgnoreCase)
                && asset.AssetId.Length is > 0 and <= 128
                && asset.AssetId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                && IsHash(asset.ContentSha256) && asset.MediaType == "image/png"
                && asset.PixelWidth > 0 && asset.PixelHeight > 0 && asset.SizeBytes > 0);

            var region = asset.FigureId is null
                && regions?.TryGetValue(asset.PageNumber, out var observed) == true ? observed : null;
            var referenceKind = asset.FigureId is not null ? "figure_asset"
                : region is not null ? "page_with_observed_region" : "page";
            var descriptor = new AdvancedAnalysisVisualSourceReference(
                document.ToString("D"), revision.ToString("D"), normalizedHash,
                asset.PageNumber, asset.AssetId, asset.ContentSha256.ToLowerInvariant(),
                asset.MediaType, asset.PixelWidth, asset.PixelHeight, asset.SizeBytes,
                $"/documents/{document:D}/revisions/{revision:D}/assets/{asset.AssetId}",
                referenceKind, asset.FigureId, region);
            projected.Add(descriptor);

            var candidate = Build("stored_asset_metadata", projected.ToArray(),
                offset + projected.Count < knownTotal ? offset + projected.Count : null);
            if (candidate.SerializedWrapperCharacters <= maximumCharacters)
                continue;

            // A descriptor is indivisible. The same offset can be retried with
            // a larger budget; no partial asset identity is returned.
            projected.RemoveAt(projected.Count - 1);
            break;
        }

        var next = offset + projected.Count < knownTotal ? (int?)(offset + projected.Count) : null;
        var status = assets!.Count > 0 && projected.Count == 0 ? "asset_does_not_fit_json_budget"
            : projected.Count < assets.Count ? "partial_json_budget"
            : knownTotal == 0 ? "no_stored_pixels"
            : offset >= knownTotal ? "end_of_inventory" : "stored_asset_metadata";
        var final = Build(status, projected.ToArray(), next);
        RequireBudget(final, maximumCharacters);
        return final;
    }

    private static bool IsHash(string value) => Regex.IsMatch(value, "^[a-fA-F0-9]{64}$");

    private static void Require(bool valid)
    {
        if (!valid)
            throw new AdvancedAnalysisToolException("visual_source_scope_or_metadata_invalid");
    }

    private static void RequireBudget(AdvancedAnalysisVisualSources result, int maximumCharacters)
    {
        if (result.SerializedWrapperCharacters > maximumCharacters)
            throw new AdvancedAnalysisToolException("visual_source_json_budget_too_small");
    }
}
