using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    internal static JsonObject AttachCanonicalNavigationMetadata(JsonObject item, string? metadataJson)
    {
        if (metadataJson is null) return item;
        using var metadata = JsonDocument.Parse(metadataJson);
        if (!metadata.RootElement.TryGetProperty("printedPageLabel", out var label) || label.ValueKind != JsonValueKind.String)
            return item;
        // The SQL query binds the row to the current tenant/document/revision/source. Only the
        // new persisted proof fields are added; old responses gain no nullable placeholders.
        foreach (var key in new[] { "printedPageLabel", "printedPageLabelKind", "targetPageSpace", "sourceTableId",
            "sourceTableOrdinal", "sourceRowIndex", "sourceCellIds", "sourceChunkIndices", "targetCanonicalBlockId" })
            if (metadata.RootElement.TryGetProperty(key, out var value))
                item[key] = JsonNode.Parse(value.GetRawText());
        return item;
    }
}
