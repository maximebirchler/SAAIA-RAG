using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed record RuntimeCapabilityDefinition(
    string Key,
    string DisplayName,
    string Family,
    string RuntimeKey,
    bool Implemented,
    bool DefaultDesiredEnabled,
    string StatusNote);

internal static class RuntimeCapabilityRegistry
{
    private static readonly RuntimeCapabilityDefinition[] DefinitionsArray =
    [
        new("core.retrieval", "Core retrieval", "core", "retrieval-stack", true, true, "Dense/sparse/exact retrieval stack qualified against Qdrant and TEI."),
        new("capability_a.corpus_enrichment", "Capability A - Corpus Enrichment", "A", "server-capability-a", true, false, "Capability A plans corpus enrichment candidates and enqueues controlled reindex jobs through the ingestion pipeline."),
        new("capability_b.backoffice_generation", "Capability B - Backoffice Generation", "B", "server-capability-b", true, false, "Capability B plans and enqueues governed backoffice summary generation jobs through the admin summary pipeline."),
        new("capability_c.retrieval_intelligence", "Capability C - Retrieval Intelligence", "C", "server-capability-c", false, false, "Capability C remains intentionally unimplemented in v3.0 backend.")
    ];

    internal static IReadOnlyList<RuntimeCapabilityDefinition> Definitions => DefinitionsArray;

    internal static IReadOnlyList<AdminRuntimeCapabilityCatalogDto> GetCapabilityCatalog()
        => DefinitionsArray.Select(static def => new AdminRuntimeCapabilityCatalogDto(
            def.Key,
            def.DisplayName,
            def.Family,
            def.RuntimeKey,
            def.Implemented,
            def.DefaultDesiredEnabled,
            def.StatusNote)).ToArray();

    internal static IReadOnlyList<RuntimeCapabilityDefinition> ResolveSelection(string? capabilityKey)
    {
        if (string.IsNullOrWhiteSpace(capabilityKey))
            return DefinitionsArray;

        var match = DefinitionsArray.FirstOrDefault(def => string.Equals(def.Key, capabilityKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? DefinitionsArray : [match];
    }

    internal static RuntimeCapabilityDefinition? FindDefinition(string? capabilityKey)
        => string.IsNullOrWhiteSpace(capabilityKey)
            ? null
            : DefinitionsArray.FirstOrDefault(def => string.Equals(def.Key, capabilityKey.Trim(), StringComparison.OrdinalIgnoreCase));
}
