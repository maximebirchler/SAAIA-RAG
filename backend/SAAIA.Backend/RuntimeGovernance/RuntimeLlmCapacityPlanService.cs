using System.Text.Json;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeLlmCapacityPlanService(IHostEnvironment env, IOptions<LicenseOptions>? licenseOptions = null)
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
            CurrentLicenseSeats: GetCurrentLicenseSeats(),
            PlanMatchesLicense: PlanMatchesLicense(result.Plan),
            ReplanRequired: ReplanRequired(result),
            Plan: result.Plan,
            Queue: snapshot,
            Recommendations: BuildRecommendations(result));
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
            CurrentLicenseSeats: response.CurrentLicenseSeats,
            PlanMatchesLicense: response.PlanMatchesLicense,
            ReplanRequired: response.ReplanRequired,
            Plan: response.Plan,
            Queue: response.Queue,
            Recommendations: response.Recommendations);
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

    internal async Task<AdminRuntimeLlmCapacityPlanDto?> GetQueuePlanAsync(CancellationToken ct)
    {
        var result = await TryLoadPlanAsync(ct).ConfigureAwait(false);
        return result.Status == "ok" && PlanMatchesLicense(result.Plan) ? result.Plan : null;
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

    private int GetCurrentLicenseSeats()
    {
        var configured = licenseOptions?.Value.Seats ?? 0;
        if (configured > 0)
            return configured;

        var envSeats = Environment.GetEnvironmentVariable("SAAIA_LICENSE_SEATS");
        return int.TryParse(envSeats, out var parsed) ? Math.Max(1, parsed) : 1;
    }

    private bool PlanMatchesLicense(AdminRuntimeLlmCapacityPlanDto? plan)
        => plan is not null && Math.Max(1, plan.LicenseSeats) == GetCurrentLicenseSeats();

    private bool ReplanRequired(RuntimeLlmCapacityReadResult result)
        => result.Status is "missing" or "invalid" || !PlanMatchesLicense(result.Plan);

    private IReadOnlyList<string> BuildRecommendations(RuntimeLlmCapacityReadResult result)
    {
        var currentSeats = GetCurrentLicenseSeats();
        if (result.Status == "missing")
        {
            return new[]
            {
                $"Run infra/scripts/llm/install-llm.ps1 -AutoPlan -LicenseSeats {currentSeats} to generate the server LLM capacity plan."
            };
        }

        if (result.Status == "invalid")
        {
            return new[]
            {
                "Regenerate llm.capacity-plan.json; the current capacity plan cannot be parsed safely."
            };
        }

        if (!PlanMatchesLicense(result.Plan))
        {
            return new[]
            {
                $"License seats changed from {result.Plan?.LicenseSeats ?? 0} to {currentSeats}; rerun the LLM capacity planner before trusting server concurrency.",
                $"Use infra/scripts/llm/install-llm.ps1 -AutoPlan -LicenseSeats {currentSeats} and restart the LLM compose stack."
            };
        }

        return Array.Empty<string>();
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
