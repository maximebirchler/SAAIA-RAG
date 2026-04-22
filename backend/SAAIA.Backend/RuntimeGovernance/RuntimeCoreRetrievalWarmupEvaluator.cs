using System.Diagnostics;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeCoreRetrievalWarmupEvaluator
{
    internal static async Task<CapabilityEvaluation> EvaluateAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
        var configured = installed
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
        var hardwareGate = RuntimeCapabilityGateService.EvaluateHardwareGate(profile.HardwareRequirements, options);
        var runtimeGates = EvaluateRuntimeSpecificGates(profile, options, rag);
        var profilePolicy = EvaluateProfilePolicy(profile, rag);

        var passDetails = new List<IReadOnlyDictionary<string, object?>>();
        var passesSucceeded = 0;
        string? lastError = null;
        var lastCheckedAt = DateTimeOffset.UtcNow;

        if (configured && hardwareGate.Passed && runtimeGates.Passed && profilePolicy.Passed)
        {
            for (var pass = 1; pass <= profile.PassCount; pass++)
            {
                var passResult = await RunWarmupPassAsync(definition.Key, profile, hardwareGate, runtimeGates, rag, httpFactory, pass, ct);
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
        else if (configured && hardwareGate.Passed && runtimeGates.Passed)
        {
            lastError = profilePolicy.Error;
            passDetails.Add(profilePolicy.Details);
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
            lastError = installed
                ? "retrieval stack is installed but not fully configured"
                : "retrieval stack is not configured";
        }

        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        var healthy = configured && passesSucceeded > 0;
        var qualified = configured && passesSucceeded == profile.PassCount;
        var authorizedPreference = existingState?.Authorized ?? options.AutoAuthorizeQualifiedCoreRetrieval;
        var authorized = qualified && desiredEnabled && authorizedPreference;
        var selectedPreference = selectWhenQualified ?? existingState?.Selected ?? options.AutoSelectQualifiedCoreRetrieval;
        var selected = qualified && authorized && desiredEnabled && selectedPreference;
        DateTimeOffset? lastQualifiedAt = qualified ? lastCheckedAt : null;
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : healthy ? "healthy" : configured ? "configured" : installed ? "installed" : "missing_dependencies",
            ["passesRequired"] = profile.PassCount,
            ["passesSucceeded"] = passesSucceeded,
            ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
            ["qdrantCollection"] = rag.QdrantCollection,
            ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
            ["embeddingsModel"] = rag.EmbeddingsModel,
            ["rerankEnabled"] = rag.EnableRerank,
            ["rerankBaseUrl"] = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
            ["hardGatesPassed"] = hardwareGate.Passed,
            ["hardware"] = hardwareGate.Details,
            ["runtimeGatesPassed"] = runtimeGates.Passed,
            ["runtimeGates"] = runtimeGates.Details,
            ["profilePolicyPassed"] = profilePolicy.Passed,
            ["profilePolicy"] = profilePolicy.Details,
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = RuntimeGovernanceService.BuildRuntimeEnvironmentSnapshot(),
            ["checks"] = passDetails
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
            LastQualifiedAt: lastQualifiedAt,
            LastError: lastError,
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastQualifiedAt?.AddHours(maxAgeHours)
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

    private static async Task<WarmupPassResult> RunWarmupPassAsync(
        string capabilityKey,
        AdminRuntimeWarmupProfileDto profile,
        HardwareGateResult hardwareGate,
        RuntimeSpecificGateResult runtimeGates,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        int passNumber,
        CancellationToken ct)
    {
        using var warmupActivity = RuntimeGovernanceTelemetry.StartWarmupCheckActivity(capabilityKey, profile.Key);
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var qdrantCheck = await CheckQdrantAsync(rag, httpFactory, ct);
        var teiCheck = await CheckEmbeddingsAsync(rag, httpFactory, ct);
        var rerankCheck = rag.EnableRerank
            ? await CheckRerankAsync(rag, httpFactory, ct)
            : BuildSkippedWarmupCheck("tei.rerank", new Dictionary<string, object?> { ["enabled"] = false });

        sw.Stop();
        var performanceBudget = EvaluatePerformanceBudget(profile, qdrantCheck, teiCheck, rerankCheck, sw.ElapsedMilliseconds, rag.EnableRerank);

        var passed = qdrantCheck.Passed && teiCheck.Passed && rerankCheck.Passed && performanceBudget.Passed;
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
                ["qdrantPassed"] = qdrantCheck.Passed,
                ["teiEmbeddingsPassed"] = teiCheck.Passed,
                ["teiRerankPassed"] = rerankCheck.Passed
            },
            ["measurementSemantics"] = BuildMeasurementSemanticsSummary(),
            ["measurements"] = BuildWarmupPassMeasurements(
                totalDurationMs: sw.ElapsedMilliseconds,
                qdrantDurationMs: qdrantCheck.DurationMs,
                embeddingsDurationMs: teiCheck.DurationMs,
                rerankDurationMs: rag.EnableRerank ? rerankCheck.DurationMs : null),
            ["qdrant"] = qdrantCheck.Details,
            ["teiEmbeddings"] = teiCheck.Details,
            ["teiRerank"] = rerankCheck.Details
        };

        var error = passed
            ? null
            : qdrantCheck.Error ?? teiCheck.Error ?? rerankCheck.Error ?? performanceBudget.Error ?? "warmup check failed";

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
            qdrantCheck.DurationMs,
            teiCheck.DurationMs,
            rag.EnableRerank ? rerankCheck.DurationMs : 0L);

        return new WarmupPassResult(passed, measuredAt, details, error);
    }

    internal static async Task<WarmupCheckResult> CheckQdrantAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var qdrant = httpFactory.CreateClient("qdrant");
            qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);
            using var response = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);
            sw.Stop();

            var details = new Dictionary<string, object?>
            {
                ["check"] = "qdrant.collection",
                ["status"] = response.IsSuccessStatusCode ? "ok" : "http_error",
                ["measuredAt"] = measuredAt,
                ["durationMs"] = sw.ElapsedMilliseconds,
                ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                ["statusCode"] = (int)response.StatusCode,
                ["collection"] = rag.QdrantCollection,
                ["baseUrl"] = rag.QdrantBaseUrl
            };

            if (!response.IsSuccessStatusCode)
            {
                var body = await TryReadBodyAsync(response, ct);
                details["body"] = body;
                return new WarmupCheckResult(false, "qdrant collection check failed", sw.ElapsedMilliseconds, details, "qdrant collection check failed");
            }

            return new WarmupCheckResult(true, "ok", sw.ElapsedMilliseconds, details, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "qdrant.collection",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.QdrantBaseUrl,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    internal static async Task<WarmupCheckResult> CheckEmbeddingsAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);
            var dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, ct);
            sw.Stop();

            return new WarmupCheckResult(
                true,
                "ok",
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.embeddings",
                    ["status"] = "ok",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.EmbeddingsBaseUrl,
                    ["model"] = rag.EmbeddingsModel,
                    ["dimension"] = dim
                },
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.embeddings",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.EmbeddingsBaseUrl,
                    ["model"] = rag.EmbeddingsModel,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    private static async Task<WarmupCheckResult> CheckRerankAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var rerankBaseUrl = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl!;
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rerankBaseUrl);

            var results = await TeiClient.RerankAsync(
                tei,
                rag.RerankModel,
                "retrieval warmup",
                ["warmup alpha", "warmup beta"],
                ct);
            sw.Stop();

            return new WarmupCheckResult(
                results.Count > 0,
                results.Count > 0 ? "ok" : "rerank returned no scores",
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.rerank",
                    ["status"] = results.Count > 0 ? "ok" : "empty_results",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rerankBaseUrl,
                    ["model"] = rag.RerankModel,
                    ["results"] = results.Count
                },
                results.Count > 0 ? null : "rerank returned no scores");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.rerank",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.RerankBaseUrl ?? rag.EmbeddingsBaseUrl,
                    ["model"] = rag.RerankModel,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    private static WarmupCheckResult BuildSkippedWarmupCheck(string checkName, IReadOnlyDictionary<string, object?> extraDetails)
    {
        var details = new Dictionary<string, object?>(extraDetails, StringComparer.Ordinal)
        {
            ["check"] = checkName,
            ["status"] = "skipped",
            ["measuredAt"] = DateTimeOffset.UtcNow,
            ["durationMs"] = 0L,
            ["measurements"] = BuildSkippedMeasurements()
        };

        return new WarmupCheckResult(true, "skipped", 0L, details, null);
    }

    private static PerformanceBudgetResult EvaluatePerformanceBudget(
        AdminRuntimeWarmupProfileDto profile,
        WarmupCheckResult qdrantCheck,
        WarmupCheckResult embeddingsCheck,
        WarmupCheckResult rerankCheck,
        long totalDurationMs,
        bool rerankEnabled)
    {
        if (profile.CheckPolicy is { EnforcePerformanceBudgets: false })
            return new PerformanceBudgetResult(true, new Dictionary<string, object?>(), Array.Empty<string>(), null);

        var budgets = profile.PerformanceBudgets;
        if (budgets is null)
            return new PerformanceBudgetResult(true, new Dictionary<string, object?>(), Array.Empty<string>(), null);

        var violations = new List<string>();

        if (budgets.MaxPassDurationMs is long maxPass && totalDurationMs > maxPass)
            violations.Add($"pass_duration_ms>{maxPass}");
        if (budgets.MaxQdrantCheckMs is long maxQdrant && qdrantCheck.DurationMs > maxQdrant)
            violations.Add($"qdrant_duration_ms>{maxQdrant}");
        if (budgets.MaxEmbeddingsCheckMs is long maxEmbeddings && embeddingsCheck.DurationMs > maxEmbeddings)
            violations.Add($"tei_embeddings_duration_ms>{maxEmbeddings}");
        if (rerankEnabled && budgets.MaxRerankCheckMs is long maxRerank && rerankCheck.DurationMs > maxRerank)
            violations.Add($"tei_rerank_duration_ms>{maxRerank}");

        var budgetSnapshot = new Dictionary<string, object?>
        {
            ["maxPassDurationMs"] = budgets.MaxPassDurationMs,
            ["maxQdrantCheckMs"] = budgets.MaxQdrantCheckMs,
            ["maxEmbeddingsCheckMs"] = budgets.MaxEmbeddingsCheckMs,
            ["maxRerankCheckMs"] = budgets.MaxRerankCheckMs
        };

        return violations.Count == 0
            ? new PerformanceBudgetResult(true, budgetSnapshot, Array.Empty<string>(), null)
            : new PerformanceBudgetResult(false, budgetSnapshot, violations.ToArray(), $"performance budget failed: {string.Join(", ", violations)}");
    }

    private static ProfilePolicyResult EvaluateProfilePolicy(AdminRuntimeWarmupProfileDto profile, RagOptions rag)
    {
        var policy = profile.CheckPolicy ?? new AdminRuntimeWarmupCheckPolicyDto(false, true, true);
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);
        var violations = new List<string>();

        if (policy.RequireRerankEnabled && !rag.EnableRerank)
            violations.Add("rerank_required_but_disabled");
        if (requirements.RequireQdrant && string.IsNullOrWhiteSpace(rag.QdrantBaseUrl))
            violations.Add("qdrant_required_but_missing");
        if (requirements.RequireEmbeddings && string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl))
            violations.Add("embeddings_required_but_missing");
        if (requirements.RequireRerank && string.IsNullOrWhiteSpace(rag.RerankBaseUrl) && string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl))
            violations.Add("rerank_runtime_required_but_missing");
        if (requirements.RequireCollection && string.IsNullOrWhiteSpace(rag.QdrantCollection))
            violations.Add("qdrant_collection_required_but_missing");
        if (requirements.RequireEmbeddingsModel && string.IsNullOrWhiteSpace(rag.EmbeddingsModel))
            violations.Add("embeddings_model_required_but_missing");
        if (requirements.RequireRerankModel && string.IsNullOrWhiteSpace(rag.RerankModel))
            violations.Add("rerank_model_required_but_missing");

        var details = new Dictionary<string, object?>
        {
            ["requireRerankEnabled"] = policy.RequireRerankEnabled,
            ["allowSkippedChecks"] = policy.AllowSkippedChecks,
            ["enforcePerformanceBudgets"] = policy.EnforcePerformanceBudgets,
            ["runtimeRequirements"] = new Dictionary<string, object?>
            {
                ["requireQdrant"] = requirements.RequireQdrant,
                ["requireEmbeddings"] = requirements.RequireEmbeddings,
                ["requireRerank"] = requirements.RequireRerank,
                ["requireCollection"] = requirements.RequireCollection,
                ["requireEmbeddingsModel"] = requirements.RequireEmbeddingsModel,
                ["requireRerankModel"] = requirements.RequireRerankModel
            },
            ["rerankEnabled"] = rag.EnableRerank,
            ["violations"] = violations.ToArray()
        };

        return violations.Count == 0
            ? new ProfilePolicyResult(true, details, null)
            : new ProfilePolicyResult(false, details, $"profile policy failed: {string.Join(", ", violations)}");
    }

    private static RuntimeSpecificGateResult EvaluateRuntimeSpecificGates(
        AdminRuntimeWarmupProfileDto profile,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);

        var activeRerank = rag.EnableRerank || requirements.RequireRerank || requirements.RequireRerankModel;

        var qdrantRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.QdrantRuntimeMinCpuCores,
            options.QdrantRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);
        var embeddingsRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.EmbeddingsRuntimeMinCpuCores,
            options.EmbeddingsRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);
        var rerankRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.RerankRuntimeMinCpuCores,
            options.RerankRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);

        var qdrantGate = RuntimeCapabilityGateService.EvaluateHardwareGate(qdrantRequirements, options);
        var embeddingsGate = RuntimeCapabilityGateService.EvaluateHardwareGate(embeddingsRequirements, options);
        var rerankGate = activeRerank
            ? RuntimeCapabilityGateService.EvaluateHardwareGate(rerankRequirements, options)
            : new HardwareGateResult(
                true,
                new Dictionary<string, object?>
                {
                    ["status"] = "skipped",
                    ["active"] = false,
                    ["requirements"] = new Dictionary<string, object?>
                    {
                        ["minCpuCores"] = rerankRequirements.MinCpuCores,
                        ["minAvailableMemoryMb"] = rerankRequirements.MinAvailableMemoryMb,
                        ["require64BitProcess"] = rerankRequirements.Require64BitProcess
                    }
                },
                null);

        var failures = new List<string>();
        if (!qdrantGate.Passed)
            failures.Add("qdrant_runtime_gate_failed");
        if (!embeddingsGate.Passed)
            failures.Add("embeddings_runtime_gate_failed");
        if (!rerankGate.Passed)
            failures.Add("rerank_runtime_gate_failed");

        var details = new Dictionary<string, object?>
        {
            ["qdrant"] = qdrantGate.Details,
            ["teiEmbeddings"] = embeddingsGate.Details,
            ["teiRerank"] = rerankGate.Details,
            ["failures"] = failures.ToArray()
        };

        return failures.Count == 0
            ? new RuntimeSpecificGateResult(true, details, null)
            : new RuntimeSpecificGateResult(false, details, $"runtime gate failed: {string.Join(", ", failures)}");
    }

    private static AdminRuntimeHardwareRequirementsDto MergeHardwareRequirements(
        AdminRuntimeHardwareRequirementsDto? profileRequirements,
        int runtimeMinCpuCores,
        long runtimeMinAvailableMemoryMb,
        bool runtimeRequire64Bit)
        => new(
            MinCpuCores: Math.Max(profileRequirements?.MinCpuCores ?? 1, runtimeMinCpuCores),
            MinAvailableMemoryMb: Math.Max(profileRequirements?.MinAvailableMemoryMb ?? 1, runtimeMinAvailableMemoryMb),
            Require64BitProcess: (profileRequirements?.Require64BitProcess ?? false) || runtimeRequire64Bit);

    private static IReadOnlyDictionary<string, object?> BuildMeasurementSemanticsSummary()
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = "measured",
            ["qdrantLoadTimeMs"] = "measured",
            ["embeddingsLoadTimeMs"] = "measured",
            ["rerankLoadTimeMs"] = "measured_if_rerank_enabled_otherwise_null",
            ["ttftMs"] = "not_applicable_for_retrieval_runtime",
            ["tokensPerSecond"] = "not_applicable_for_retrieval_runtime"
        };

    private static IReadOnlyDictionary<string, object?> BuildWarmupPassMeasurements(
        long totalDurationMs,
        long qdrantDurationMs,
        long embeddingsDurationMs,
        long? rerankDurationMs)
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = totalDurationMs,
            ["qdrantLoadTimeMs"] = qdrantDurationMs,
            ["embeddingsLoadTimeMs"] = embeddingsDurationMs,
            ["rerankLoadTimeMs"] = rerankDurationMs,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = BuildMeasurementSemanticsSummary()
        };

    private static IReadOnlyDictionary<string, object?> BuildNonGenerativeMeasurements(long durationMs)
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = durationMs,
            ["qdrantLoadTimeMs"] = null,
            ["embeddingsLoadTimeMs"] = null,
            ["rerankLoadTimeMs"] = null,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = BuildMeasurementSemanticsSummary()
        };

    private static IReadOnlyDictionary<string, object?> BuildSkippedMeasurements()
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = 0L,
            ["qdrantLoadTimeMs"] = null,
            ["embeddingsLoadTimeMs"] = null,
            ["rerankLoadTimeMs"] = null,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = new Dictionary<string, object?>
            {
                ["loadTimeMs"] = "skipped",
                ["qdrantLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["embeddingsLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["rerankLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["ttftMs"] = "not_applicable_for_retrieval_runtime",
                ["tokensPerSecond"] = "not_applicable_for_retrieval_runtime"
            }
        };

    private static async Task<string> TryReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }
}
