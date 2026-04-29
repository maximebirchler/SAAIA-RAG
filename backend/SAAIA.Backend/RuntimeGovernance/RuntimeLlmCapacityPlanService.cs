using System.Text.Json;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeLlmCapacityPlanService(IHostEnvironment env)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal async Task<AdminRuntimeLlmCapacityResponseDto> GetCapacityAsync(
        RuntimeLlmQueueManager queue,
        CancellationToken ct)
    {
        var result = await TryLoadPlanAsync(ct).ConfigureAwait(false);
        var snapshot = queue.GetSnapshot(result.Plan);

        return new AdminRuntimeLlmCapacityResponseDto(
            CdcAlignment: "v3.1",
            Environment: env.EnvironmentName,
            GeneratedAt: DateTimeOffset.UtcNow,
            Status: result.Status,
            Path: result.Path,
            Error: result.Error,
            Plan: result.Plan,
            Queue: snapshot);
    }

    internal async Task<AdminRuntimeLlmCapacityArtifactDto> GetCapacityArtifactAsync(
        RuntimeLlmQueueManager queue,
        CancellationToken ct)
    {
        var response = await GetCapacityAsync(queue, ct).ConfigureAwait(false);
        return new AdminRuntimeLlmCapacityArtifactDto(
            Artifact: "llm_capacity_plan.json",
            CdcAlignment: response.CdcAlignment,
            Environment: response.Environment,
            GeneratedAt: response.GeneratedAt,
            Status: response.Status,
            Path: response.Path,
            Error: response.Error,
            Plan: response.Plan,
            Queue: response.Queue);
    }

    internal async Task<RuntimeLlmCapacityReadResult> TryLoadPlanAsync(CancellationToken ct)
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                await using var stream = File.OpenRead(path);
                var plan = await JsonSerializer.DeserializeAsync<AdminRuntimeLlmCapacityPlanDto>(
                    stream,
                    JsonOptions,
                    ct).ConfigureAwait(false);

                if (plan is null)
                    return new RuntimeLlmCapacityReadResult("invalid", path, "capacity plan JSON is empty", null);

                return new RuntimeLlmCapacityReadResult("ok", path, null, NormalizePlan(plan));
            }
            catch (JsonException ex)
            {
                return new RuntimeLlmCapacityReadResult("invalid", path, ex.Message, null);
            }
            catch (IOException ex)
            {
                return new RuntimeLlmCapacityReadResult("invalid", path, ex.Message, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new RuntimeLlmCapacityReadResult("invalid", path, ex.Message, null);
            }
        }

        return new RuntimeLlmCapacityReadResult("missing", null, "llm.capacity-plan.json was not found", null);
    }

    internal IReadOnlyList<string> GetCandidatePaths()
    {
        var candidates = new List<string>();
        AddIfNotBlank(candidates, Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH"));

        AddIfNotBlank(candidates, Path.Combine(env.ContentRootPath, "llm.capacity-plan.json"));
        AddIfNotBlank(candidates, Path.Combine(env.ContentRootPath, "deploy", "llm.capacity-plan.json"));

        var installRoot = Environment.GetEnvironmentVariable("SAAIA_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(installRoot))
            AddIfNotBlank(candidates, Path.Combine(installRoot, "deploy", "llm.capacity-plan.json"));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AdminRuntimeLlmCapacityPlanDto NormalizePlan(AdminRuntimeLlmCapacityPlanDto plan)
    {
        var instances = Math.Max(1, plan.Instances);
        var slotsPerInstance = Math.Max(1, plan.SlotsPerInstance);
        var totalSlots = Math.Max(1, plan.TotalSlots > 0 ? plan.TotalSlots : instances * slotsPerInstance);
        var queueLimit = Math.Max(0, plan.QueueLimit);
        var perUserActiveLimit = Math.Max(1, plan.PerUserActiveLimit);
        var perUserQueuedLimit = Math.Max(0, plan.PerUserQueuedLimit);

        return plan with
        {
            Instances = instances,
            SlotsPerInstance = slotsPerInstance,
            TotalSlots = totalSlots,
            QueueLimit = queueLimit,
            PerUserActiveLimit = perUserActiveLimit,
            PerUserQueuedLimit = perUserQueuedLimit
        };
    }

    private static void AddIfNotBlank(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(Path.GetFullPath(value));
    }
}

internal sealed record RuntimeLlmCapacityReadResult(
    string Status,
    string? Path,
    string? Error,
    AdminRuntimeLlmCapacityPlanDto? Plan);
