namespace SAAIA.Backend.Models;

public sealed record AdminRuntimeCatalogResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeCatalogRuntimeDto> Runtimes,
    IReadOnlyList<AdminRuntimeWarmupProfileDto> WarmupProfiles,
    IReadOnlyList<AdminRuntimeCapabilityCatalogDto> Capabilities
);

public sealed record AdminRuntimeCatalogRuntimeDto(
    string Key,
    string Label,
    string Kind,
    bool Enabled,
    string? BaseUrl = null,
    string? Model = null
);

public sealed record AdminRuntimeWarmupProfileDto(
    string Key,
    string Label,
    int PassCount,
    IReadOnlyList<string> Checks
);

public sealed record AdminRuntimeCapabilityCatalogDto(
    string Key,
    string DisplayName,
    string Family,
    string RuntimeKey,
    bool Implemented,
    bool DefaultDesiredEnabled,
    string StatusNote
);

public sealed record AdminRuntimeCapabilitiesResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items,
    IReadOnlyList<AdminRuntimeWarmupResultDto> WarmupResults
);

public sealed record AdminRuntimeCapabilityStateDto(
    string Key,
    string DisplayName,
    string Family,
    string RuntimeKey,
    bool Implemented,
    bool DesiredEnabled,
    bool Installed,
    bool Configured,
    bool Healthy,
    bool Qualified,
    bool Authorized,
    bool Selected,
    string ProfileKey,
    int PassCount,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? LastQualifiedAt = null,
    string? LastError = null,
    IReadOnlyDictionary<string, object?>? Details = null
);

public sealed record AdminRuntimeWarmupResultDto(
    Guid WarmupResultId,
    string CapabilityKey,
    string ProfileKey,
    int PassCount,
    bool Passed,
    DateTimeOffset MeasuredAt,
    IReadOnlyDictionary<string, object?>? Details = null
);

public sealed record AdminRuntimeRequalifyRequestDto(
    string? CapabilityKey = null,
    string? ProfileKey = null,
    bool? SelectWhenQualified = null
);

public sealed record AdminRuntimeRequalifyResponseDto(
    string CdcAlignment,
    string Environment,
    string ProfileKey,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items,
    IReadOnlyList<AdminRuntimeWarmupResultDto> WarmupResults
);

public sealed record AdminRuntimeCapabilitySelectionRequestDto(
    bool? DesiredEnabled = null,
    bool? Authorized = null,
    bool? Selected = null
);

public sealed record AdminRuntimeWarmupResultsResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeWarmupResultDto> Items
);
