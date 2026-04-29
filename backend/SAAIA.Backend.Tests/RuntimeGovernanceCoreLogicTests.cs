using SAAIA.Backend;
using SAAIA.Backend.Models;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RuntimeGovernanceCoreLogicTests
{
    [Fact]
    public async Task RuntimeLlmCapacityPlanService_reads_capacity_plan_from_configured_path()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var planPath = Path.Combine(tempRoot, "llm.capacity-plan.json");
            await File.WriteAllTextAsync(planPath, """
            {
              "version": "1.0",
              "generatedAt": "2026-04-29T12:00:00Z",
              "licenseSeats": 25,
              "profile": "small-server",
              "modelRepo": "bartowski/Qwen2.5-3B-Instruct-GGUF",
              "modelFile": "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
              "modelLabel": "Qwen2.5-3B-Instruct-Q4_K_M",
              "placement": "server",
              "instances": 1,
              "slotsPerInstance": 3,
              "totalSlots": 3,
              "queueLimit": 40,
              "perUserActiveLimit": 1,
              "perUserQueuedLimit": 2,
              "notes": "test plan",
              "hardware": {
                "cpuCount": 8,
                "totalRamMiB": 32768,
                "gpuName": "RTX test",
                "gpuVramMiB": 8192
              },
              "llamaArgs": {
                "ctxSize": 8192,
                "batchSize": 512,
                "uBatchSize": 128,
                "gpuLayers": 99
              }
            }
            """);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", planPath);

            var service = new RuntimeLlmCapacityPlanService(new StubHostEnvironment { ContentRootPath = tempRoot });
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("ok", response.Status);
            Assert.Equal(planPath, response.Path);
            Assert.NotNull(response.Plan);
            Assert.Equal(25, response.Plan!.LicenseSeats);
            Assert.Equal("Qwen2.5-3B-Instruct-Q4_K_M.gguf", response.Plan.ModelFile);
            Assert.Equal(3, response.Queue.TotalSlots);
            Assert.Equal(40, response.Queue.QueueLimit);
            Assert.Equal(3, response.Queue.AvailableSlots);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeLlmCapacityPlanService_reports_missing_plan_without_throwing()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-missing-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", null);

            var service = new RuntimeLlmCapacityPlanService(new StubHostEnvironment { ContentRootPath = tempRoot });
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("missing", response.Status);
            Assert.Null(response.Plan);
            Assert.Equal(1, response.Queue.TotalSlots);
            Assert.Equal(10, response.Queue.QueueLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void RuntimeLlmQueueManager_enforces_slot_and_per_user_limits()
    {
        var manager = new RuntimeLlmQueueManager();
        var plan = new AdminRuntimeLlmCapacityPlanDto(
            Version: "1.0",
            GeneratedAt: null,
            LicenseSeats: 100,
            Profile: "test",
            ModelRepo: null,
            ModelFile: null,
            ModelLabel: null,
            Placement: "server",
            Instances: 1,
            SlotsPerInstance: 2,
            TotalSlots: 2,
            QueueLimit: 1,
            PerUserActiveLimit: 1,
            PerUserQueuedLimit: 1,
            Notes: null,
            Hardware: null,
            LlamaArgs: null);

        using var userA = manager.TryAcquire("user-a", plan);
        Assert.NotNull(userA);
        Assert.Null(manager.TryAcquire("user-a", plan));

        using var userB = manager.TryAcquire("user-b", plan);
        Assert.NotNull(userB);
        Assert.Null(manager.TryAcquire("user-c", plan));

        Assert.True(manager.TryRegisterQueued("user-c", plan));
        Assert.False(manager.TryRegisterQueued("user-d", plan));
        manager.ReleaseQueued("user-c");

        var snapshot = manager.GetSnapshot(plan);
        Assert.Equal(2, snapshot.Active);
        Assert.Equal(0, snapshot.AvailableSlots);
        Assert.Equal(0, snapshot.Queued);
    }

    [Fact]
    public void EvaluateSelectionUpdate_rejects_authorization_when_capability_is_stale()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: false,
            Selected: false,
            ProfileKey: "default-local",
            PassCount: 3,
            Stale: true,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: true,
                Authorized: true,
                Selected: false));

        Assert.False(decision.Accepted);
        Assert.Equal("capability must be requalified because its qualification is stale", decision.Error);
        Assert.Equal(current, decision.State);
    }

    [Fact]
    public void EvaluateSelectionUpdate_disabling_capability_clears_authorization_and_selection()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: true,
            Selected: true,
            ProfileKey: "default-local",
            PassCount: 3,
            PersistedAuthorized: true,
            PersistedSelected: true,
            EffectiveAuthorized: true,
            EffectiveSelected: true);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: false,
                Authorized: null,
                Selected: null));

        Assert.True(decision.Accepted);
        Assert.False(decision.State.DesiredEnabled);
        Assert.False(decision.State.Authorized);
        Assert.False(decision.State.Selected);
        Assert.False(decision.State.EffectiveAuthorized);
        Assert.False(decision.State.EffectiveSelected);
    }

    [Fact]
    public void BuildDefaultState_for_capability_b_reflects_disabled_backoffice_runtime()
    {
        var definition = Assert.Single(
            RuntimeCapabilityRegistry.Definitions,
            item => item.Key == "capability_b.backoffice_generation");
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");

        try
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", null);

            var state = RuntimeCapabilityStateResolver.BuildDefaultState(
                definition,
                new RuntimeGovernanceOptions(),
                new RagOptions());

            Assert.True(state.Implemented);
            Assert.True(state.Installed);
            Assert.False(state.Configured);
            Assert.False(state.Qualified);
            Assert.False(state.Selected);
            Assert.NotNull(state.Details);
            Assert.Equal("backoffice_disabled", state.Details!["status"]);
            Assert.Equal(false, state.Details["backofficeEnabled"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
