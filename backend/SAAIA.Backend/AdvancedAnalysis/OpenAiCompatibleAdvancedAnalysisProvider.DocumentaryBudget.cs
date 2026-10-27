using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private int MeasurePromptEvidenceCharacters(PromptEvidenceItem evidence)
    {
        var budgetEvidence = evidence;
        if (_options.NativeObservationReadEnabled
            && (evidence.NativeObservation is not null || evidence.SourceOverview?.NativeObservations is not null)
            && evidence.SourceOverview is { } overview)
        {
            // These inventories remain in the typed projection. The final
            // serializer moves them into the shared current-user documentary
            // sections, so they must not consume the per-excerpt budget again.
            // The reserved shared budget and exact final-wire gate still apply.
            budgetEvidence = evidence with
            {
                SourceOverview = overview with { TableRegionObservations = null, NativeObservations = null }
            };
        }
        return JsonSerializer.Serialize(budgetEvidence, JsonOptions).Length;
    }

    private JsonNode? TableReadingsForNativeToolResult(object alternative,
        AdvancedAnalysisSearchRequest? request, IReadOnlyList<PromptEvidenceItem> visible)
    {
        var node = JsonSerializer.SerializeToNode(alternative, JsonOptions);
        if (!_options.NativeObservationReadEnabled || request?.NativeObservationView is null
            || node is not JsonObject result || result["inventories"] is not JsonArray inventories)
            return node;

        var current = JsonSerializer.SerializeToNode(BuildAlternativeTableReadingsForPrompt(visible), JsonOptions);
        var currentInventories = current?["inventories"] as JsonArray;
        for (var index = 0; index < inventories.Count; index++)
        {
            if (inventories[index] is not JsonObject inventory
                || inventory["sourceKey"]?.GetValue<string>() is not { } sourceKey)
                continue;
            var sources = visible.Where(item => item.SourceKey == sourceKey
                && item.SourceOverview?.TableRegionObservations is not null).ToArray();
            var currentInventory = currentInventories?.OfType<JsonObject>().SingleOrDefault(item =>
                item["sourceKey"]?.GetValue<string>() == sourceKey);
            if (sources.Length == 0 || !JsonNode.DeepEquals(inventory, currentInventory)
                || sources.Any(item => item.SourceReference is not { } reference
                    || !string.Equals(reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(reference.SourceHash, item.SourceOverview!.TableRegionObservations!.SourceSha256,
                        StringComparison.OrdinalIgnoreCase))
                || sources.Select(item => JsonSerializer.Serialize(item.SourceOverview!.TableRegionObservations, JsonOptions))
                    .Distinct(StringComparer.Ordinal).Skip(1).Any())
                continue;

            // The same visible source builds the full inventory in this
            // request's current user.alternativeTableReadings. Refer to it
            // explicitly instead of duplicating its pages and descriptors in
            // the escaped tool string. Native page/block/window data is never
            // deduplicated by this document-scoped table-inventory reference.
            inventories[index] = JsonSerializer.SerializeToNode(new
            {
                sourceKey,
                inventoryReference = "current_user.alternativeTableReadings.inventories[sourceKey]"
            }, JsonOptions);
        }
        // This contract and capability remain in the same current-user section.
        // A tool carrying any unreplaced inventory retains its original wrapper.
        if (inventories.Count > 0 && inventories.OfType<JsonObject>().All(item => item.ContainsKey("inventoryReference")))
        {
            result.Remove("instruction");
            result.Remove("imageInspectionAvailable");
        }
        return result;
    }

    private JsonNode? NativeReadingsForToolResult(object alternative, IReadOnlyList<PromptEvidenceItem> visible)
    {
        var node = JsonSerializer.SerializeToNode(alternative, JsonOptions);
        if (!_options.NativeObservationReadEnabled || node is not JsonObject result
            || result["views"] is not JsonArray views)
            return node;

        var current = JsonSerializer.SerializeToNode(BuildAlternativeNativeReadingsForPrompt(visible), JsonOptions);
        var currentViews = current?["views"] as JsonArray;
        foreach (var view in views.OfType<JsonObject>())
        {
            // Compare the complete window, not only its document or source key.
            // This includes page, block, raw SHA, offsets, admitted line hashes,
            // quality and refusal state. A divergent window stays self-contained.
            if (currentViews?.Any(candidate => JsonNode.DeepEquals(candidate, view)) != true)
                continue;
            foreach (var property in view.Select(property => property.Key).ToArray())
                if (property is not ("sourceKey" or "physicalPage" or "blockId" or "rawTextSha256"
                    or "offset" or "limit" or "lines"))
                    view.Remove(property);
            if (view["lines"] is JsonArray lines)
                foreach (var line in lines.OfType<JsonObject>())
                    foreach (var property in line.Select(property => property.Key).ToArray())
                        if (property != "evidenceId")
                            line.Remove(property);
            view["metadataReference"] = "current_user.alternativeNativeReadings.views[exact_window_identity]";
        }
        // Quality, role, cursors and line hashes are never discarded: the exact
        // matching view above remains authoritative in the current user payload.
        if (views.Count > 0 && views.OfType<JsonObject>().All(view => view.ContainsKey("metadataReference")))
        {
            result.Remove("instruction");
            result.Remove("semanticAccuracy");
            result.Remove("sourceFidelityApproved");
        }
        return result;
    }
}
