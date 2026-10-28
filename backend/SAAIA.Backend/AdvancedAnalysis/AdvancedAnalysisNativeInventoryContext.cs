using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

/// <summary>Current, source-bound locator windows; never a semantic ownership map.</summary>
internal static class AdvancedAnalysisNativeInventoryContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static IEnumerable<AdvancedAnalysisObservedNativeInventory> Windows(
        AdvancedAnalysisAlternativeNativeReadings value)
    {
        foreach (var retained in value.RetainedInventories ?? [])
            yield return retained;
        if (value.Inventory is { } current)
            yield return new(value.PhysicalPage, current);
    }

    internal static AdvancedAnalysisAlternativeNativeReadings Preserve(
        AdvancedAnalysisAlternativeNativeReadings current,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        Guid tenantId,
        int maximumCharacters)
    {
        if (tenantId == Guid.Empty || current.TenantId != tenantId
            || current.ManifestSha256 is not { Length: 64 } manifest || !manifest.All(char.IsAsciiHexDigit))
            throw new AdvancedAnalysisToolException("native_inventory_context_identity_invalid");
        var windows = new List<AdvancedAnalysisObservedNativeInventory>();
        var omitted = current.InventoryWindowsOmitted;
        foreach (var item in evidence)
        {
            if (!Scope(current, item.Reference.DocId, item.Reference.RevisionId, item.Reference.SourceHash)
                || item.SourceOverview?.NativeObservations is not { } previous
                || !Scope(current, previous.DocId, previous.RevisionId, previous.SourceSha256)
                || previous.TenantId != tenantId
                || !string.Equals(previous.ManifestSha256, manifest, StringComparison.OrdinalIgnoreCase))
                continue;
            omitted = Math.Max(omitted, previous.InventoryWindowsOmitted);
            foreach (var window in Windows(previous))
                Add(window);
        }
        foreach (var window in Windows(current))
            Add(window);
        if (current.Inventory is { } own)
            windows.RemoveAll(window => Key(window) == Key(new(current.PhysicalPage, own)));

        var result = Build();
        while (MetadataCharacters(result) > maximumCharacters && windows.Count > 0)
        {
            windows.RemoveAt(0);
            omitted++;
            result = Build();
        }
        if (MetadataCharacters(result) > maximumCharacters)
            throw new AdvancedAnalysisToolException("native_inventory_context_budget_does_not_fit");
        return result;

        AdvancedAnalysisAlternativeNativeReadings Build() => current with
        {
            RetainedInventories = windows.Count == 0 ? null : windows.ToArray(),
            InventoryWindowsOmitted = omitted
        };
        void Add(AdvancedAnalysisObservedNativeInventory window)
        {
            if (window.PhysicalPage <= 0 || window.Inventory.Offset < 0
                || window.Inventory.Observations?.Any(descriptor => descriptor.PhysicalPage != window.PhysicalPage
                    || !Scope(current, descriptor.DocId, descriptor.RevisionId, descriptor.SourceSha256)
                    || !string.Equals(descriptor.ManifestSha256, manifest, StringComparison.OrdinalIgnoreCase)) == true)
                throw new AdvancedAnalysisToolException("native_inventory_context_identity_invalid");
            windows.RemoveAll(existing => Key(existing) == Key(window));
            windows.Add(window);
        }
    }

    private static (int Page, int Offset, int Count) Key(AdvancedAnalysisObservedNativeInventory window)
        => (window.PhysicalPage, window.Inventory.Offset, window.Inventory.Observations?.Count ?? -1);

    private static bool Scope(AdvancedAnalysisAlternativeNativeReadings expected, string? document,
        string? revision, string? source)
        => string.Equals(expected.DocId, document, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.RevisionId, revision, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.SourceSha256, source, StringComparison.OrdinalIgnoreCase);

    private static int MetadataCharacters(AdvancedAnalysisAlternativeNativeReadings value)
        => JsonSerializer.Serialize(value with
        {
            View = value.View is { } raw ? raw with { Lines = [] } : null
        }, Web).Length;

    internal static void RecordDiagnostics(BackendDiagnostics.Operation probe,
        AdvancedAnalysisAlternativeNativeReadings value, IReadOnlyList<AdvancedAnalysisResolvedEvidence> resolved)
    {
        probe.Set("native_inventory_windows_current", value.Inventory is null ? 0 : 1);
        probe.Set("native_inventory_windows_retained", value.RetainedInventories?.Count ?? 0);
        probe.Set("native_inventory_windows_omitted", value.InventoryWindowsOmitted);
        probe.Set("native_block_polygon_present", resolved.Any(item => item.NativeObservation?.ObservedBlockPolygon is not null));
    }
}
