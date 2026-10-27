using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static void RequireObservedNativeRawLocator(JsonElement arguments, JsonElement prompt)
    {
        static string? Exact(JsonElement item, string name)
            => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int? Number(JsonElement item, string name)
            => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        static IEnumerable<JsonElement> Items(JsonElement item, string name)
            => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var values)
                && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray() : [];
        static JsonElement Object(JsonElement item, string name)
            => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Object ? value : default;

        if (Exact(arguments, "nativeObservationView") != "raw")
            return;
        var sourceKey = Exact(arguments, "sourceKey");
        var page = Number(arguments, "pageStart");
        var block = Exact(arguments, "nativeObservationBlockId");
        var hash = Exact(arguments, "nativeObservationRawSha256");
        bool Matches(JsonElement descriptor)
            => page is > 0 && Number(arguments, "pageEnd") == page
                && Number(descriptor, "physicalPage") == page
                && !string.IsNullOrEmpty(block) && Exact(descriptor, "blockId") == block
                && !string.IsNullOrEmpty(hash)
                && string.Equals(Exact(descriptor, "rawTextSha256"), hash, StringComparison.OrdinalIgnoreCase);
        bool Scoped(JsonElement item) => !string.IsNullOrEmpty(sourceKey) && Exact(item, "sourceKey") == sourceKey;

        // Consult only capabilities in the current user payload. A syntactically
        // valid hash from another block, page, source or historical turn grants
        // no access. The SQL reader still independently verifies the actual data.
        var alternative = Object(prompt, "alternativeNativeReadings");
        foreach (var inventory in Items(alternative, "inventories").Where(Scoped))
            if (Number(inventory, "physicalPage") == page
                && Items(Object(inventory, "inventory"), "observations").Any(Matches))
                return;
        if (Items(alternative, "views").Any(item => Scoped(item) && Matches(item))
            || Items(alternative, "emptyWindows").Any(item => Scoped(item) && Matches(Object(item, "window"))))
            return;
        foreach (var item in Items(prompt, "evidence").Where(Scoped))
            if (Matches(Object(Object(item, "nativeObservation"), "reference")))
                return;

        // The existing native argument recovery reports every linked call in
        // the batch as rejected before executing any operation or changing state.
        throw new AdvancedAnalysisProviderException("advanced_native_raw_locator_not_observed");
    }
}
