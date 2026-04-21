using System.Diagnostics;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAdminEvaluators
{
    internal static Task<CapabilityEvaluation> EvaluateCapabilityAAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string capabilityKey,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var installed = true;
        var configured = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
        var healthy = configured;
        var qualified = configured;
        var lastCheckedAt = DateTimeOffset.UtcNow;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var checks = new Dictionary<string, object?>
        {
            ["documentsCatalogAccessible"] = true,
            ["retrievalStackConfigured"] = configured,
            ["qdrantConfigured"] = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl) && !string.IsNullOrWhiteSpace(rag.QdrantCollection),
            ["embeddingsConfigured"] = !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl) && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel)
        };

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : "configured",
            ["mode"] = "corpus_enrichment_admin",
            ["passesRequired"] = 1,
            ["passesSucceeded"] = qualified ? 1 : 0,
            ["checks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["pass"] = 1,
                    ["status"] = qualified ? "passed" : "failed",
                    ["check"] = capabilityKey,
                    ["measuredAt"] = lastCheckedAt,
                    ["details"] = checks
                }
            },
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = RuntimeGovernanceService.BuildRuntimeEnvironmentSnapshot(),
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue",
                ["executionMode"] = "reindex_existing_ingestion_pipeline"
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: 1,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : "capability A requires the retrieval stack to be configured before it can enqueue enrichment jobs",
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: 1,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return Task.FromResult(new CapabilityEvaluation(state, warmupResult));
    }

    internal static async Task<CapabilityEvaluation> EvaluateCapabilityBAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        string capabilityKey,
        CancellationToken ct)
    {
        var backofficeEnabled = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled();
        var hardwareGate = RuntimeCapabilityGateService.EvaluateHardwareGate(profile.HardwareRequirements, options);
        var runtimeGates = EvaluateCapabilityBRuntimeGates(backofficeEnabled, options, httpFactory);
        var installed = true;
        var configured = backofficeEnabled;
        var passDetails = new List<IReadOnlyDictionary<string, object?>>();
        var passesSucceeded = 0;
        string? lastError = null;
        var lastCheckedAt = DateTimeOffset.UtcNow;

        if (configured && hardwareGate.Passed && runtimeGates.Passed)
        {
            for (var pass = 1; pass <= profile.PassCount; pass++)
            {
                var passResult = await RunCapabilityBWarmupPassAsync(
                    capabilityKey,
                    profile,
                    hardwareGate,
                    runtimeGates,
                    httpFactory,
                    pass,
                    ct);
                passDetails.Add(passResult.Details);
                lastCheckedAt = passResult.MeasuredAt;
                if (passResult.Passed)
                {
                    passesSucceeded++;
                }
                else
                {
                    lastError = passResult.Error;
                }
            }
        }
        else if (configured && hardwareGate.Passed)
        {
            lastError = runtimeGates.Error;
            passDetails.Add(runtimeGates.Details);
        }
        else if (configured)
        {
            lastError = hardwareGate.Error;
            passDetails.Add(hardwareGate.Details);
        }
        else
        {
            lastError = "capability B requires BACKOFFICE_LLM_ENABLED=true before it can enqueue server backoffice summary jobs";
        }

        var healthy = configured && passesSucceeded > 0;
        var qualified = configured && passesSucceeded == profile.PassCount;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : healthy ? "healthy" : configured ? "configured" : "installed",
            ["mode"] = "summary_generation_admin",
            ["passesRequired"] = profile.PassCount,
            ["passesSucceeded"] = passesSucceeded,
            ["backofficeEnabled"] = backofficeEnabled,
            ["hardGatesPassed"] = hardwareGate.Passed,
            ["hardware"] = hardwareGate.Details,
            ["runtimeGatesPassed"] = runtimeGates.Passed,
            ["runtimeGates"] = runtimeGates.Details,
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = RuntimeGovernanceService.BuildRuntimeEnvironmentSnapshot(),
            ["checks"] = passDetails,
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue",
                ["executionMode"] = "server_backoffice_summary_jobs",
                ["requiresBackofficeLlmEnabled"] = true
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: profile.PassCount,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : lastError,
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: profile.PassCount,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return new CapabilityEvaluation(state, warmupResult);
    }

    private static async Task<WarmupPassResult> RunCapabilityBWarmupPassAsync(
        string capabilityKey,
        AdminRuntimeWarmupProfileDto profile,
        HardwareGateResult hardwareGate,
        RuntimeSpecificGateResult runtimeGates,
        IHttpClientFactory httpFactory,
        int passNumber,
        CancellationToken ct)
    {
        using var warmupActivity = RuntimeGovernanceTelemetry.StartWarmupCheckActivity(capabilityKey, profile.Key);
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var llmProbe = await CapabilityBLiveRuntimeProbe.ProbeAsync(httpFactory, ct);
        sw.Stop();

        var performanceBudget = EvaluateCapabilityBPerformanceBudget(profile, llmProbe, sw.ElapsedMilliseconds);
        var passed = llmProbe.Available && performanceBudget.Passed;
        var details = new Dictionary<string, object?>
        {
            ["pass"] = passNumber,
            ["measuredAt"] = measuredAt,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["status"] = passed ? "passed" : "failed",
            ["hardGatesPassed"] = hardwareGate.Passed,
            ["hardware"] = hardwareGate.Details,
            ["runtimeGatesPassed"] = runtimeGates.Passed,
            ["runtimeGates"] = runtimeGates.Details,
            ["performanceBudgetPassed"] = performanceBudget.Passed,
            ["performanceBudgets"] = performanceBudget.Budgets,
            ["performanceBudgetViolations"] = performanceBudget.Violations,
            ["checksSummary"] = new Dictionary<string, object?>
            {
                ["llmPassed"] = llmProbe.Available
            },
            ["measurementSemantics"] = BuildCapabilityBMeasurementSemanticsSummary(),
            ["measurements"] = new Dictionary<string, object?>
            {
                ["loadTimeMs"] = sw.ElapsedMilliseconds,
                ["llmLoadTimeMs"] = llmProbe.DurationMs,
                ["ttftMs"] = null,
                ["tokensPerSecond"] = null,
                ["applicability"] = BuildCapabilityBMeasurementSemanticsSummary()
            },
            ["llm"] = BuildCapabilityBLlmDetails(llmProbe)
        };

        var error = passed
            ? null
            : llmProbe.Error ?? performanceBudget.Error ?? "capability B warmup check failed";

        RuntimeGovernanceTelemetry.CompleteWarmupCheck(
            warmupActivity,
            capabilityKey,
            profile.Key,
            passNumber,
            passed,
            hardwareGate.Passed,
            runtimeGates.Passed,
            performanceBudget.Passed,
            sw.ElapsedMilliseconds,
            qdrantDurationMs: 0L,
            embeddingsDurationMs: 0L,
            rerankDurationMs: 0L);

        return new WarmupPassResult(passed, measuredAt, details, error);
    }

    private static RuntimeSpecificGateResult EvaluateCapabilityBRuntimeGates(
        bool backofficeEnabled,
        RuntimeGovernanceOptions options,
        IHttpClientFactory httpFactory)
    {
        Uri? llmBaseAddress = null;
        try
        {
            llmBaseAddress = httpFactory.CreateClient("llm").BaseAddress;
        }
        catch
        {
        }

        var failures = new List<string>();
        if (!backofficeEnabled)
            failures.Add("backoffice_llm_disabled");
        if (!options.CapabilityBWorkerEnabled)
            failures.Add("capability_b_worker_disabled");
        if (llmBaseAddress is null)
            failures.Add("llm_runtime_not_configured");

        var details = new Dictionary<string, object?>
        {
            ["backofficeEnabled"] = backofficeEnabled,
            ["workerEnabled"] = options.CapabilityBWorkerEnabled,
            ["llmConfigured"] = llmBaseAddress is not null,
            ["llmBaseUrl"] = llmBaseAddress?.ToString(),
            ["failures"] = failures.ToArray()
        };

        return failures.Count == 0
            ? new RuntimeSpecificGateResult(true, details, null)
            : new RuntimeSpecificGateResult(false, details, $"runtime gate failed: {string.Join(", ", failures)}");
    }

    private static PerformanceBudgetResult EvaluateCapabilityBPerformanceBudget(
        AdminRuntimeWarmupProfileDto profile,
        CapabilityBLiveRuntimeProbeResult llmProbe,
        long totalDurationMs)
    {
        if (profile.CheckPolicy is { EnforcePerformanceBudgets: false })
            return new PerformanceBudgetResult(true, new Dictionary<string, object?>(), Array.Empty<string>(), null);

        var budgets = profile.PerformanceBudgets;
        if (budgets is null)
            return new PerformanceBudgetResult(true, new Dictionary<string, object?>(), Array.Empty<string>(), null);

        var violations = new List<string>();
        if (budgets.MaxPassDurationMs is long maxPass && totalDurationMs > maxPass)
            violations.Add($"pass_duration_ms>{maxPass}");

        var budgetSnapshot = new Dictionary<string, object?>
        {
            ["maxPassDurationMs"] = budgets.MaxPassDurationMs,
            ["llmDurationMs"] = llmProbe.DurationMs
        };

        return violations.Count == 0
            ? new PerformanceBudgetResult(true, budgetSnapshot, Array.Empty<string>(), null)
            : new PerformanceBudgetResult(false, budgetSnapshot, violations.ToArray(), $"performance budget failed: {string.Join(", ", violations)}");
    }

    private static IReadOnlyDictionary<string, object?> BuildCapabilityBMeasurementSemanticsSummary()
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = "measured",
            ["llmLoadTimeMs"] = "measured",
            ["ttftMs"] = "not_measured_for_admin_backoffice_warmup",
            ["tokensPerSecond"] = "not_measured_for_admin_backoffice_warmup"
        };

    private static IReadOnlyDictionary<string, object?> BuildCapabilityBLlmDetails(CapabilityBLiveRuntimeProbeResult probe)
        => new Dictionary<string, object?>
        {
            ["check"] = "llm.chat_completion",
            ["status"] = probe.Status,
            ["measuredAt"] = probe.MeasuredAt,
            ["durationMs"] = probe.DurationMs,
            ["baseUrl"] = probe.BaseUrl,
            ["statusCode"] = probe.StatusCode,
            ["hasContent"] = probe.HasContent,
            ["preview"] = probe.Preview,
            ["body"] = probe.Body
        };
}
