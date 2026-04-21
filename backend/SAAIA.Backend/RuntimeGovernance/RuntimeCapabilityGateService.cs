using System.Runtime.InteropServices;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityGateService
{
    internal static HardwareGateResult EvaluateHardwareGate(RuntimeGovernanceOptions options)
        => EvaluateHardwareGate(null, options);

    internal static HardwareGateResult EvaluateHardwareGate(
        AdminRuntimeHardwareRequirementsDto? requirements,
        RuntimeGovernanceOptions options)
    {
        var processors = Environment.ProcessorCount;
        var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var is64Bit = Environment.Is64BitProcess;
        var architecture = RuntimeInformation.ProcessArchitecture.ToString();
        var minCpuCores = requirements?.MinCpuCores ?? options.MinCpuCores;
        var minAvailableMemoryMb = requirements?.MinAvailableMemoryMb ?? options.MinAvailableMemoryMb;
        var require64BitProcess = requirements?.Require64BitProcess ?? options.Require64BitProcess;

        var reasons = new List<string>();
        if (processors < Math.Max(1, minCpuCores))
            reasons.Add($"cpu_cores<{minCpuCores}");
        if (availableMemoryMb < Math.Max(1, minAvailableMemoryMb))
            reasons.Add($"available_memory_mb<{minAvailableMemoryMb}");
        if (require64BitProcess && !is64Bit)
            reasons.Add("process_not_64bit");

        var details = new Dictionary<string, object?>
        {
            ["cpuCores"] = processors,
            ["availableMemoryMb"] = availableMemoryMb,
            ["is64BitProcess"] = is64Bit,
            ["architecture"] = architecture,
            ["minCpuCores"] = minCpuCores,
            ["minAvailableMemoryMb"] = minAvailableMemoryMb,
            ["require64BitProcess"] = require64BitProcess,
            ["source"] = requirements is null ? "global_defaults" : "profile",
            ["reasons"] = reasons.ToArray()
        };

        return reasons.Count == 0
            ? new HardwareGateResult(true, details, null)
            : new HardwareGateResult(false, details, $"hardware gate failed: {string.Join(", ", reasons)}");
    }

    internal static async Task<CapabilityGateResult> EnsureCapabilityReadyAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        var definition = RuntimeCapabilityRegistry.FindDefinition(capabilityKey);
        if (definition is null)
            return new CapabilityGateResult(null, "unknown capability");

        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var current = RuntimeCapabilityStateResolver.ResolveState(
            definition,
            existingStates.TryGetValue(definition.Key, out var row) ? row : null,
            options,
            rag);

        if (!current.Implemented)
            return new CapabilityGateResult(current, "capability_not_implemented");
        if (!current.DesiredEnabled)
            return new CapabilityGateResult(current, "capability_not_desired_enabled");
        if (!current.Qualified)
            return new CapabilityGateResult(current, "capability_not_qualified");
        if (!current.Authorized || !current.EffectiveAuthorized)
            return new CapabilityGateResult(current, "capability_not_authorized");
        if (!current.Selected || !current.EffectiveSelected)
            return new CapabilityGateResult(current, "capability_not_selected");

        return new CapabilityGateResult(current, null);
    }
}
