using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed record CapabilityEvaluation(
    AdminRuntimeCapabilityStateDto State,
    AdminRuntimeWarmupResultDto? WarmupResult);

internal sealed record CapabilitySelectionUpdateResult(
    AdminRuntimeCapabilityStateDto? State,
    string? Error);

internal sealed record CapabilityGateResult(
    AdminRuntimeCapabilityStateDto? State,
    string? Error);

internal sealed record WarmupPassResult(
    bool Passed,
    DateTimeOffset MeasuredAt,
    IReadOnlyDictionary<string, object?> Details,
    string? Error);

internal sealed record WarmupCheckResult(
    bool Passed,
    string Status,
    long DurationMs,
    IReadOnlyDictionary<string, object?> Details,
    string? Error);

internal sealed record PerformanceBudgetResult(
    bool Passed,
    IReadOnlyDictionary<string, object?> Budgets,
    IReadOnlyList<string> Violations,
    string? Error);

internal sealed record ProfilePolicyResult(
    bool Passed,
    IReadOnlyDictionary<string, object?> Details,
    string? Error);

internal sealed record HardwareGateResult(
    bool Passed,
    IReadOnlyDictionary<string, object?> Details,
    string? Error);

internal sealed record RuntimeSpecificGateResult(
    bool Passed,
    IReadOnlyDictionary<string, object?> Details,
    string? Error);
