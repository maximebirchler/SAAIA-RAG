using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class WarmupGateTests
{
    [Fact]
    public async Task WarmupGate_pass_with_three_nominal_runs_updates_last_known_good()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(LoadMs: 21000, TtftMs: 11000, TokPerSec: 6.2),
                        new WarmupMeasurement(LoadMs: 20800, TtftMs: 11200, TokPerSec: 6.1),
                        new WarmupMeasurement(LoadMs: 21300, TtftMs: 10900, TokPerSec: 6.3)
                    },
                    DriverVersion: "573.71",
                    HardwareFingerprint: "test-fp",
                    Trigger: "admin"),
                root);

            Assert.Equal(WarmupGateStatus.Pass, result.Status);
            Assert.Equal(profile.ProfileId, result.SelectedProfile?.ProfileId);
            Assert.False(result.RollbackApplied);
            Assert.Equal(3, result.PassCount);

            var lastGood = await RollbackManager.ReadLastKnownGoodAsync(root);
            Assert.Equal(profile.ProfileId, lastGood?.ProfileId);

            var warmupResults = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
                GovernanceArtifactStore.WarmupResultsFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, warmupResults.Status);
            Assert.Equal(WarmupGateStatus.Pass, warmupResults.Value!.Items[0].Status);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WarmupGate_fail_block_when_thresholds_fail_and_no_last_known_good_exists()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(LoadMs: 40000, TtftMs: 22000, TokPerSec: 2.0),
                        new WarmupMeasurement(LoadMs: 41000, TtftMs: 23000, TokPerSec: 2.1),
                        new WarmupMeasurement(LoadMs: 42000, TtftMs: 24000, TokPerSec: 2.2)
                    }),
                root);

            Assert.Equal(WarmupGateStatus.FailBlock, result.Status);
            Assert.Null(result.SelectedProfile);
            Assert.Contains("no_last_known_good", result.Reasons);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WarmupGate_fail_fallback_rolls_back_to_last_known_good()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            var fallback = WarmupProfileStore.CreateReferenceCudaFallbackProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);
            await RollbackManager.SaveLastKnownGoodAsync(fallback, root);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(LoadMs: 40000, TtftMs: 22000, TokPerSec: 2.0),
                        new WarmupMeasurement(LoadMs: 41000, TtftMs: 23000, TokPerSec: 2.1),
                        new WarmupMeasurement(LoadMs: 42000, TtftMs: 24000, TokPerSec: 2.2)
                    }),
                root);

            Assert.Equal(WarmupGateStatus.FailFallback, result.Status);
            Assert.True(result.RollbackApplied);
            Assert.Equal(fallback.ProfileId, result.SelectedProfile?.ProfileId);

            var rollbackLog = await GovernanceArtifactStore.ReadAsync<RollbackLogArtifact>(
                GovernanceArtifactStore.RollbackLogFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, rollbackLog.Status);
            Assert.Equal(profile.ProfileId, rollbackLog.Value!.Items[0].FromProfileId);
            Assert.Equal(fallback.ProfileId, rollbackLog.Value.Items[0].ToProfileId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WarmupGate_blacklisted_profile_is_refused_before_warmup_runs()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.BlacklistFile,
                BlacklistPolicy.Create(new BlacklistRule(
                    RuleId: "p520-flash-attn-bad-driver",
                    Active: true,
                    Runtime: profile.Runtime,
                    ModelId: profile.ModelId,
                    ProfileId: profile.ProfileId,
                    DriverContains: "573.71",
                    Reason: "Known unstable driver/runtime pair.")),
                root);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    Runs: Array.Empty<WarmupMeasurement>(),
                    DriverVersion: "573.71"),
                root);

            Assert.Equal(WarmupGateStatus.FailBlock, result.Status);
            Assert.Null(result.SelectedProfile);
            Assert.Contains(result.Reasons, item => item.Contains("blacklisted:p520-flash-attn-bad-driver"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WarmupGate_pass_degraded_when_runs_are_within_degraded_budget_only()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(LoadMs: 25000, TtftMs: 14000, TokPerSec: 4.2),
                        new WarmupMeasurement(LoadMs: 25200, TtftMs: 14200, TokPerSec: 4.1),
                        new WarmupMeasurement(LoadMs: 25400, TtftMs: 14300, TokPerSec: 4.3)
                    }),
                root);

            Assert.Equal(WarmupGateStatus.PassDegraded, result.Status);
            Assert.Equal(profile.ProfileId, result.SelectedProfile?.ProfileId);
            Assert.Contains("pass_degraded_thresholds", result.Reasons);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RunQualificationAsync_collects_configured_runs_then_evaluates_gate()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root);
            var harness = new StubWarmupHarness(new[]
            {
                new WarmupMeasurement(LoadMs: 21000, TtftMs: 11000, TokPerSec: 6.2),
                new WarmupMeasurement(LoadMs: 20800, TtftMs: 11200, TokPerSec: 6.1),
                new WarmupMeasurement(LoadMs: 21300, TtftMs: 10900, TokPerSec: 6.3)
            });

            var result = await WarmupGate.RunQualificationAsync(
                profile,
                "http://127.0.0.1:1234/v1",
                "local",
                harness,
                root,
                observedLoadMs: 28000,
                driverVersion: "573.71",
                hardwareFingerprint: "test-fp",
                trigger: "unit-test");

            Assert.Equal(WarmupGateStatus.Pass, result.Status);
            Assert.Equal(3, harness.CallCount);

            var warmupResults = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
                GovernanceArtifactStore.WarmupResultsFile,
                root);
            Assert.Equal("unit-test", warmupResults.Value!.Items[0].Trigger);
            Assert.Equal(28000, warmupResults.Value.Items[0].LastLoadMs);
            Assert.NotNull(warmupResults.Value.Items[0].RuntimeMetrics);
            Assert.Equal(28000, warmupResults.Value.Items[0].RuntimeMetrics!["runtime.observed_start_load_ms"]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WarmupGate_hard_gate_blocks_when_dxgi_budget_is_insufficient()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await EnsureDefaultArtifactsWithHardwareAsync(root, dxgiBudgetMiB: 1024);

            var result = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(LoadMs: 21000, TtftMs: 11000, TokPerSec: 6.2),
                        new WarmupMeasurement(LoadMs: 20800, TtftMs: 11200, TokPerSec: 6.1),
                        new WarmupMeasurement(LoadMs: 21300, TtftMs: 10900, TokPerSec: 6.3)
                    }),
                root);

            Assert.Equal(WarmupGateStatus.FailBlock, result.Status);
            Assert.Contains(result.Reasons, item => item.StartsWith("hard_gate_dxgi_budget_insufficient", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-warmup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task EnsureDefaultArtifactsWithHardwareAsync(string root, int dxgiBudgetMiB = 4096)
    {
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);
        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.HardwareProbeFile,
            CreateHardwareProbe(dxgiBudgetMiB),
            root);
    }

    private static HardwareProbeArtifact CreateHardwareProbe(int dxgiBudgetMiB)
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4096L * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");
        var dxgi = new DxgiVideoMemorySnapshot(
            (ulong)dxgiBudgetMiB * 1024 * 1024,
            128UL * 1024 * 1024,
            (ulong)Math.Max(0, dxgiBudgetMiB - 512) * 1024 * 1024,
            0,
            "test");
        var memory = new SystemMemorySnapshot(
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            "test");

        return HardwareProbeService.CreateArtifact(
            gpu: gpu,
            gpuDriverVersion: "573.71",
            dxgi: dxgi,
            memory: memory,
            machineName: "test-machine",
            processorCount: 8,
            is64BitOperatingSystem: true,
            capturedAt: DateTimeOffset.UtcNow);
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class StubWarmupHarness : ILocalLlmWarmupHarness
    {
        private readonly Queue<WarmupMeasurement> _measurements;

        public StubWarmupHarness(IEnumerable<WarmupMeasurement> measurements)
            => _measurements = new Queue<WarmupMeasurement>(measurements);

        public int CallCount { get; private set; }

        public Task<WarmupMeasurement> RunOnceAsync(
            string llmBaseUrl,
            string model,
            LocalLlmWarmupHarnessOptions? options = null,
            CancellationToken ct = default)
        {
            CallCount++;
            Assert.Equal("http://127.0.0.1:1234/v1", llmBaseUrl);
            Assert.Equal("local", model);
            return Task.FromResult(_measurements.Dequeue());
        }
    }
}
