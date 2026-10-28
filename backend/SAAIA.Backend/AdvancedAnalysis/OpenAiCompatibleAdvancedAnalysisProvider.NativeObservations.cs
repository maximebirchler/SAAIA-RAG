using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string NativeObservationContract = """
        Native audit readings are retained source observations, not primary
        indexed text, newly extracted text or verified semantic truth. Discover
        their inventory with read_source nativeObservationView=inventory on one
        physical page and an exact current sourceKey. Read raw with its observed
        blockId and rawTextSha256. Offsets are raw line ordinals, not UTF-16
        character offsets. Each line carries Unicode scalar bounds including
        its original CR/LF terminator. Lines are indivisible and never sliced.
        alternativeNativeReadings references only exact whole text in current
        user.evidence, or user.observations in research review. Cite its
        evidenceId; never reconstruct an omitted line
        from history. Per-line nextLineOffset and shared-view nextOffset both
        resume after the prefix actually admitted to this prompt. A line read
        by the backend but omitted here is not model evidence and must be read
        again at that continuation cursor. No captured native block means acquisition is unavailable,
        not known empty. Observed empty raw text is distinct. These readings
        establish no semantic novelty, source order, image pixels or new OCR.
        Retained inventories are previously observed locator windows explicitly
        rendered in this current payload, not permission inferred from history.
        observedPolygon and observedBlockPolygon locate the observed block in
        normalized page coordinates (0 to 1, origin top-left); a block polygon
        is not a separate polygon for each line or an asserted semantic parent.
        Use the actual text and these bounded physical observations to inspect
        layout; never manufacture a title-to-body relationship. Inventory
        window omissions are explicit and do not establish source absence.
        """;

    private static bool HasNativeObservationRead(JsonElement root)
        => root.TryGetProperty("nativeObservationCapabilities", out var capabilities)
            && capabilities.TryGetProperty("nativeObservationReadAvailable", out var available)
            && available.ValueKind == JsonValueKind.True;

    private static AdvancedAnalysisSourceOverview? NativeOverviewMetadata(AdvancedAnalysisSourceOverview? overview)
        => overview?.NativeObservations is not { } native ? overview : overview with
        {
            NativeObservations = native with { View = native.View is { } view ? view with { Lines = [] } : null }
        };

    private string AddNativeObservationsToPrompt(string prompt)
    {
        if (!_options.NativeObservationReadEnabled)
            return prompt;
        if (JsonNode.Parse(prompt) is not JsonObject node)
            return prompt;
        node["nativeObservationCapabilities"] = JsonSerializer.SerializeToNode(new
        {
            nativeObservationReadAvailable = true, indexed = false, primaryTextReplaced = false,
            imageToolAvailable = false, modelPixelsProvided = false, semanticAccuracy = "not_verified"
        }, JsonOptions);
        node["nativeObservationReadContract"] = NativeObservationContract;
        var collection = node["evidence"] is JsonArray ? "evidence" : "observations";
        var evidence = node[collection] as JsonArray;
        if (evidence?.Deserialize<PromptEvidenceItem[]>(JsonOptions) is { } typed)
        {
            if (BuildAlternativeNativeReadingsForPrompt(typed) is { } alternative)
            {
                var projected = NativeViewQualityReferences(alternative, typed, collection);
                node["alternativeNativeReadings"] = projected;
                SetNativeModelContinuationCursors(evidence, projected);
            }
            foreach (var item in evidence.OfType<JsonObject>())
            {
                if (item["sourceOverview"] is JsonObject overview)
                    overview.Remove("nativeObservations");
                if (item["nativeObservation"] is JsonObject native)
                    native.Remove("observedBlockPolygon");
            }
        }
        var declarations = (node["researchTools"] as JsonObject)?["tools"] as JsonArray ?? node["tools"] as JsonArray;
        declarations?.Add(JsonSerializer.SerializeToNode(new
        {
            name = "read_source_native_audit", operation = "read_source",
            sourceKey = "exact current sourceKey", pageStart = "one physical page", pageEnd = "same page",
            nativeObservationView = "inventory|raw", nativeObservationBlockId = "observed blockId for raw only",
            nativeObservationRawSha256 = "observed rawTextSha256 for raw only",
            nativeObservationOffset = "raw line ordinal, or inventory ordinal; start 0 then nextOffset",
            nativeObservationLimit = "integer 1 to 64; default 16",
            instruction = "Do not mix table row or table observation parameters. Whole lines only; a refusal retains its exact offset."
        }, JsonOptions));
        return node.ToJsonString(JsonOptions);
    }

    private static void SetNativeModelContinuationCursors(JsonArray evidence, JsonNode? alternative)
    {
        if (alternative?["views"] is not JsonArray views)
            return;
        foreach (var view in views.OfType<JsonObject>())
            foreach (var item in evidence.OfType<JsonObject>())
            {
                if (item["nativeObservation"] is not JsonObject native
                    || native["reference"] is not JsonObject reference
                    || !JsonNode.DeepEquals(item["sourceKey"], view["sourceKey"])
                    || !JsonNode.DeepEquals(reference["physicalPage"], view["physicalPage"])
                    || !JsonNode.DeepEquals(reference["blockId"], view["blockId"])
                    || !JsonNode.DeepEquals(reference["rawTextSha256"], view["rawTextSha256"])
                    || !JsonNode.DeepEquals(native["lineOffset"], view["offset"])
                    || !JsonNode.DeepEquals(native["lineLimit"], view["limit"]))
                    continue;

                // This is prompt-only operational metadata. The canonical line
                // identity, quality and reader DTO stay unchanged. A backend
                // cursor beyond an omitted line would make the model skip it.
                native["nextLineOffset"] = view["nextOffset"]?.DeepClone();
            }
    }

    private static object? BuildAlternativeNativeReadingsForPrompt(IReadOnlyList<PromptEvidenceItem> evidence)
    {
        var inventories = evidence.Where(item => item.SourceOverview?.NativeObservations is not null)
            .SelectMany(item => AdvancedAnalysisNativeInventoryContext.Windows(item.SourceOverview!.NativeObservations!)
                .Select(window => new { item.SourceKey, window.PhysicalPage, window.Inventory }))
            .GroupBy(item => new { item.SourceKey, item.PhysicalPage, item.Inventory.Offset,
                Count = item.Inventory.Observations?.Count ?? -1 })
            .Select(group => new { group.Key.SourceKey, group.Key.PhysicalPage, inventory = group.First().Inventory }).ToArray();
        var inventoryOmissions = evidence.Where(item => item.SourceOverview?.NativeObservations?.InventoryWindowsOmitted > 0)
            .GroupBy(item => item.SourceKey)
            .Select(group => new { sourceKey = group.Key,
                omittedWindowCount = group.Max(item => item.SourceOverview!.NativeObservations!.InventoryWindowsOmitted),
                reason = "retained_inventory_metadata_budget" }).ToArray();
        var views = evidence.Where(item => item.NativeObservation is not null)
            .GroupBy(item => new { item.SourceKey, item.NativeObservation!.Reference.BlockId,
                item.NativeObservation.Reference.RawTextSha256, item.NativeObservation.Reference.PhysicalPage,
                item.NativeObservation.LineOffset, item.NativeObservation.LineLimit })
            .Select(group =>
            {
                var source = group.First().NativeObservation!;
                var next = source.LineOffset;
                var lines = new List<object>();
                foreach (var item in group.OrderBy(item => item.NativeObservation!.Reference.LineOrdinal))
                {
                    var line = item.NativeObservation!.Reference;
                    if (line.LineOrdinal != next || item.ContentTruncated || item.Content.Length != item.OriginalContentLength)
                        break;
                    lines.Add(new { item.EvidenceId, line.LineId, line.LineOrdinal, line.LineTextSha256,
                        line.UnicodeScalarStart, line.UnicodeScalarEndExclusive });
                    next++;
                }
                var rendered = JsonSerializer.SerializeToNode(new
                {
                    group.Key.SourceKey, group.Key.PhysicalPage, group.Key.BlockId, group.Key.RawTextSha256,
                    offsetUnit = "raw_line_ordinal", offset = source.LineOffset, limit = source.LineLimit,
                    rawLineCount = source.ViewLineCount, returnedLineCount = lines.Count,
                    nextOffset = next < source.ViewLineCount ? (int?)next : null,
                    status = next < Math.Min(source.ViewLineCount, source.LineOffset + source.LineLimit)
                        ? lines.Count == 0 ? "line_does_not_fit_model_budget" : "partial_model_budget" : "observed",
                    lines, source.QualitySignals, source.Role, source.SemanticAccuracy, source.SourceFidelityApproved
                }, JsonOptions)!.AsObject();
                if (group.Select(item => item.NativeObservation!.ObservedBlockPolygon).FirstOrDefault(polygon => polygon is not null) is { } polygon)
                {
                    rendered["observedBlockPolygon"] = JsonSerializer.SerializeToNode(polygon, JsonOptions);
                    rendered["coordinateSpace"] = CanonicalSchema.NormalizedTopLeftCoordinateSpace;
                    rendered["polygonScope"] = "whole_observed_block_not_individual_line_or_semantic_association";
                }
                return (object)rendered;
            }).ToArray();
        // Keep empty/refused windows discoverable even when no line reference
        // was admitted. Metadata never repeats raw text from the current view.
        var empty = evidence.Where(item => item.SourceOverview?.NativeObservations?.View is { } view
                && !evidence.Any(candidate => candidate.NativeObservation?.Reference.BlockId == view.BlockId
                    && candidate.SourceKey == item.SourceKey))
            .GroupBy(item => new { item.SourceKey, item.SourceOverview!.NativeObservations!.PhysicalPage,
                item.SourceOverview.NativeObservations.View!.BlockId })
            .Select(group => new { group.Key.SourceKey, window = NativeWindowMetadata(group.First().SourceOverview!.NativeObservations!.View!) }).ToArray();
        if (inventories.Length == 0 && views.Length == 0 && empty.Length == 0 && inventoryOmissions.Length == 0)
            return null;
        var result = JsonSerializer.SerializeToNode(new
        {
            inventories, views, emptyWindows = empty, semanticAccuracy = "not_verified",
            sourceFidelityApproved = false, instruction = "Exact literal text occurs once in the current documentary collection (evidence or observations); references here supply no additional text."
        }, JsonOptions)!.AsObject();
        if (inventoryOmissions.Length > 0)
            result["inventoryOmissions"] = JsonSerializer.SerializeToNode(inventoryOmissions, JsonOptions);
        return result;
    }

    private static object NativeWindowMetadata(AdvancedAnalysisNativeObservationWindow view)
        => new { view.BlockId, view.RawTextSha256, view.PhysicalPage, view.OffsetUnit, view.Offset,
            view.Limit, view.RawLineCount, view.NextOffset, view.Status };
}
