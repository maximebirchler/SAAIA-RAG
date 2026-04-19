using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceService
{
    private const string CdcAlignment = "v3.0";

    internal static AdminRuntimeCatalogResponseDto BuildCatalog(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env)
        => new(
            CdcAlignment,
            env.EnvironmentName,
            BuildRuntimes(rag),
            BuildWarmupProfiles(options),
            BuildCapabilityCatalog());

    internal static async Task<AdminRuntimeCapabilitiesResponseDto> GetCapabilitiesAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var persisted = await LoadCapabilityStateRowsAsync(conn, ct);

        var items = RuntimeCapabilities
            .Select(def => persisted.TryGetValue(def.Key, out var row)
                ? MapStateRow(row)
                : BuildDefaultState(def, options, rag))
            .ToArray();

        var warmupResults = await LoadWarmupResultsAsync(conn, capabilityKey: null, limit: 25, ct);

        return new AdminRuntimeCapabilitiesResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            items,
            warmupResults);
    }

    internal static async Task<AdminRuntimeWarmupResultsResponseDto> GetWarmupResultsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var items = await LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
        return new AdminRuntimeWarmupResultsResponseDto(CdcAlignment, env.EnvironmentName, items);
    }

    internal static async Task<AdminRuntimeRequalifyResponseDto> RequalifyAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeRequalifyRequestDto? req,
        CancellationToken ct)
    {
        var profile = ResolveProfile(req?.ProfileKey, options);
        var definitions = ResolveCapabilitySelection(req?.CapabilityKey);

        var items = new List<AdminRuntimeCapabilityStateDto>(definitions.Count);
        var warmupResults = new List<AdminRuntimeWarmupResultDto>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);

        foreach (var definition in definitions)
        {
            var evaluation = await EvaluateCapabilityAsync(
                definition,
                profile,
                existingStates.TryGetValue(definition.Key, out var existingState) ? MapStateRow(existingState) : null,
                req?.SelectWhenQualified,
                options,
                rag,
                httpFactory,
                ct);

            await UpsertCapabilityStateAsync(conn, evaluation.State, ct);
            items.Add(evaluation.State);

            if (evaluation.WarmupResult is not null)
            {
                await InsertWarmupResultAsync(conn, evaluation.WarmupResult, ct);
                warmupResults.Add(evaluation.WarmupResult);
            }
        }

        return new AdminRuntimeRequalifyResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            profile.Key,
            items,
            warmupResults);
    }

    internal static async Task<CapabilitySelectionUpdateResult> UpdateSelectionAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string capabilityKey,
        AdminRuntimeCapabilitySelectionRequestDto? req,
        CancellationToken ct)
    {
        var definition = RuntimeCapabilities.FirstOrDefault(def => string.Equals(def.Key, capabilityKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return new CapabilitySelectionUpdateResult(null, "unknown capability");

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);
        var current = existingStates.TryGetValue(definition.Key, out var row)
            ? MapStateRow(row)
            : BuildDefaultState(definition, options, rag);

        var desiredEnabled = req?.DesiredEnabled ?? current.DesiredEnabled;
        var authorized = req?.Authorized ?? current.Authorized;
        var selected = req?.Selected ?? current.Selected;

        if (authorized && !current.Qualified)
            return new CapabilitySelectionUpdateResult(current, "capability must be qualified before it can be authorized");

        if (selected && !authorized)
            return new CapabilitySelectionUpdateResult(current, "capability must be authorized before it can be selected");

        if (selected && !current.Qualified)
            return new CapabilitySelectionUpdateResult(current, "capability must be qualified before it can be selected");

        if (!desiredEnabled)
            selected = false;

        var details = new Dictionary<string, object?>(current.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["selectionUpdatedAt"] = DateTimeOffset.UtcNow
        };

        var updated = current with
        {
            DesiredEnabled = desiredEnabled,
            Authorized = authorized && current.Qualified,
            Selected = selected && current.Qualified && authorized && desiredEnabled,
            Details = details
        };

        await UpsertCapabilityStateAsync(conn, updated, ct);
        return new CapabilitySelectionUpdateResult(updated, null);
    }

    private static async Task<CapabilityEvaluation> EvaluateCapabilityAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        if (!definition.Implemented)
        {
            var stubDetails = new Dictionary<string, object?>
            {
                ["status"] = "not_implemented",
                ["note"] = definition.StatusNote
            };

            return new CapabilityEvaluation(
                new AdminRuntimeCapabilityStateDto(
                    Key: definition.Key,
                    DisplayName: definition.DisplayName,
                    Family: definition.Family,
                    RuntimeKey: definition.RuntimeKey,
                    Implemented: false,
                    DesiredEnabled: existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled,
                    Installed: false,
                    Configured: false,
                    Healthy: false,
                    Qualified: false,
                    Authorized: false,
                    Selected: false,
                    ProfileKey: profile.Key,
                    PassCount: 0,
                    LastCheckedAt: DateTimeOffset.UtcNow,
                    LastQualifiedAt: null,
                    LastError: null,
                    Details: stubDetails),
                null);
        }

        var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
        var configured = installed
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);

        var passDetails = new List<IReadOnlyDictionary<string, object?>>();
        var passesSucceeded = 0;
        string? lastError = null;
        var lastCheckedAt = DateTimeOffset.UtcNow;

        if (configured)
        {
            for (var pass = 1; pass <= profile.PassCount; pass++)
            {
                var passResult = await RunCoreRetrievalWarmupPassAsync(rag, httpFactory, pass, ct);
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
            Details: details);

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

    private static async Task<WarmupPassResult> RunCoreRetrievalWarmupPassAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        int passNumber,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var qdrantCheck = await CheckQdrantAsync(rag, httpFactory, ct);
        var teiCheck = await CheckEmbeddingsAsync(rag, httpFactory, ct);
        var rerankCheck = rag.EnableRerank
            ? await CheckRerankAsync(rag, httpFactory, ct)
            : new WarmupCheckResult(true, "skipped", new Dictionary<string, object?> { ["enabled"] = false }, null);

        sw.Stop();

        var passed = qdrantCheck.Passed && teiCheck.Passed && rerankCheck.Passed;
        var details = new Dictionary<string, object?>
        {
            ["pass"] = passNumber,
            ["measuredAt"] = measuredAt,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["qdrant"] = qdrantCheck.Details,
            ["teiEmbeddings"] = teiCheck.Details,
            ["teiRerank"] = rerankCheck.Details
        };

        var error = passed
            ? null
            : qdrantCheck.Error ?? teiCheck.Error ?? rerankCheck.Error ?? "warmup check failed";

        return new WarmupPassResult(passed, measuredAt, details, error);
    }

    private static async Task<WarmupCheckResult> CheckQdrantAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        try
        {
            var qdrant = httpFactory.CreateClient("qdrant");
            qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);
            using var response = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);

            var details = new Dictionary<string, object?>
            {
                ["statusCode"] = (int)response.StatusCode,
                ["collection"] = rag.QdrantCollection,
                ["baseUrl"] = rag.QdrantBaseUrl
            };

            if (!response.IsSuccessStatusCode)
            {
                var body = await TryReadBodyAsync(response, ct);
                details["body"] = body;
                return new WarmupCheckResult(false, "qdrant collection check failed", details, "qdrant collection check failed");
            }

            return new WarmupCheckResult(true, "ok", details, null);
        }
        catch (Exception ex)
        {
            return new WarmupCheckResult(
                false,
                ex.Message,
                new Dictionary<string, object?> { ["baseUrl"] = rag.QdrantBaseUrl, ["exception"] = ex.GetType().Name },
                ex.Message);
        }
    }

    private static async Task<WarmupCheckResult> CheckEmbeddingsAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        try
        {
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);
            var dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, ct);

            return new WarmupCheckResult(
                true,
                "ok",
                new Dictionary<string, object?>
                {
                    ["baseUrl"] = rag.EmbeddingsBaseUrl,
                    ["model"] = rag.EmbeddingsModel,
                    ["dimension"] = dim
                },
                null);
        }
        catch (Exception ex)
        {
            return new WarmupCheckResult(
                false,
                ex.Message,
                new Dictionary<string, object?> { ["baseUrl"] = rag.EmbeddingsBaseUrl, ["model"] = rag.EmbeddingsModel, ["exception"] = ex.GetType().Name },
                ex.Message);
        }
    }

    private static async Task<WarmupCheckResult> CheckRerankAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
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

            return new WarmupCheckResult(
                results.Count > 0,
                results.Count > 0 ? "ok" : "rerank returned no scores",
                new Dictionary<string, object?>
                {
                    ["baseUrl"] = rerankBaseUrl,
                    ["model"] = rag.RerankModel,
                    ["results"] = results.Count
                },
                results.Count > 0 ? null : "rerank returned no scores");
        }
        catch (Exception ex)
        {
            return new WarmupCheckResult(
                false,
                ex.Message,
                new Dictionary<string, object?> { ["baseUrl"] = rag.RerankBaseUrl ?? rag.EmbeddingsBaseUrl, ["model"] = rag.RerankModel, ["exception"] = ex.GetType().Name },
                ex.Message);
        }
    }

    private static async Task UpsertCapabilityStateAsync(NpgsqlConnection conn, AdminRuntimeCapabilityStateDto state, CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_capability_state(
  capability_key,
  capability_family,
  display_name,
  runtime_key,
  profile_key,
  implemented,
  desired_enabled,
  installed,
  configured,
  healthy,
  qualified,
  authorized,
  selected,
  pass_count,
  last_checked_at,
  last_qualified_at,
  last_error,
  details,
  updated_at)
VALUES(
  @capability_key,
  @capability_family,
  @display_name,
  @runtime_key,
  @profile_key,
  @implemented,
  @desired_enabled,
  @installed,
  @configured,
  @healthy,
  @qualified,
  @authorized,
  @selected,
  @pass_count,
  @last_checked_at,
  @last_qualified_at,
  @last_error,
  CAST(@details AS jsonb),
  now())
ON CONFLICT (capability_key) DO UPDATE SET
  capability_family = EXCLUDED.capability_family,
  display_name = EXCLUDED.display_name,
  runtime_key = EXCLUDED.runtime_key,
  profile_key = EXCLUDED.profile_key,
  implemented = EXCLUDED.implemented,
  desired_enabled = EXCLUDED.desired_enabled,
  installed = EXCLUDED.installed,
  configured = EXCLUDED.configured,
  healthy = EXCLUDED.healthy,
  qualified = EXCLUDED.qualified,
  authorized = EXCLUDED.authorized,
  selected = EXCLUDED.selected,
  pass_count = EXCLUDED.pass_count,
  last_checked_at = EXCLUDED.last_checked_at,
  last_qualified_at = EXCLUDED.last_qualified_at,
  last_error = EXCLUDED.last_error,
  details = EXCLUDED.details,
  updated_at = now();
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            capability_key = state.Key,
            capability_family = state.Family,
            display_name = state.DisplayName,
            runtime_key = state.RuntimeKey,
            profile_key = state.ProfileKey,
            implemented = state.Implemented,
            desired_enabled = state.DesiredEnabled,
            installed = state.Installed,
            configured = state.Configured,
            healthy = state.Healthy,
            qualified = state.Qualified,
            authorized = state.Authorized,
            selected = state.Selected,
            pass_count = state.PassCount,
            last_checked_at = state.LastCheckedAt?.UtcDateTime,
            last_qualified_at = state.LastQualifiedAt?.UtcDateTime,
            last_error = state.LastError,
            details = JsonSerializer.Serialize(state.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

    private static async Task InsertWarmupResultAsync(NpgsqlConnection conn, AdminRuntimeWarmupResultDto result, CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_warmup_results(
  warmup_result_id,
  capability_key,
  profile_key,
  pass_count,
  passed,
  measured_at,
  details)
VALUES(
  @warmup_result_id,
  @capability_key,
  @profile_key,
  @pass_count,
  @passed,
  @measured_at,
  CAST(@details AS jsonb));
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            warmup_result_id = result.WarmupResultId,
            capability_key = result.CapabilityKey,
            profile_key = result.ProfileKey,
            pass_count = result.PassCount,
            passed = result.Passed,
            measured_at = result.MeasuredAt.UtcDateTime,
            details = JsonSerializer.Serialize(result.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

    private static AdminRuntimeCapabilityStateDto BuildDefaultState(
        RuntimeCapabilityDefinition definition,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        if (definition.Key == "core.retrieval")
        {
            var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
            var configured = installed
                && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);

            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: true,
                Installed: installed,
                Configured: configured,
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: options.DefaultProfileKey,
                PassCount: options.WarmupPassCount,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = configured ? "configured_not_checked" : installed ? "installed_not_checked" : "missing_dependencies",
                    ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
                    ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl
                });
        }

        return new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: false,
            DesiredEnabled: definition.DefaultDesiredEnabled,
            Installed: false,
            Configured: false,
            Healthy: false,
            Qualified: false,
            Authorized: false,
            Selected: false,
            ProfileKey: options.DefaultProfileKey,
            PassCount: 0,
            LastCheckedAt: null,
            LastQualifiedAt: null,
            LastError: null,
            Details: new Dictionary<string, object?>
            {
                ["status"] = "not_implemented",
                ["note"] = definition.StatusNote
            });
    }

    private static AdminRuntimeCapabilityStateDto MapStateRow(CapabilityStateRow row)
        => new(
            Key: row.CapabilityKey,
            DisplayName: row.DisplayName,
            Family: row.CapabilityFamily,
            RuntimeKey: row.RuntimeKey,
            Implemented: row.Implemented,
            DesiredEnabled: row.DesiredEnabled,
            Installed: row.Installed,
            Configured: row.Configured,
            Healthy: row.Healthy,
            Qualified: row.Qualified,
            Authorized: row.Authorized,
            Selected: row.Selected,
            ProfileKey: row.ProfileKey,
            PassCount: row.PassCount,
            LastCheckedAt: row.LastCheckedAt,
            LastQualifiedAt: row.LastQualifiedAt,
            LastError: row.LastError,
            Details: ParseDetails(row.DetailsJson));

    private static AdminRuntimeWarmupResultDto MapWarmupResultRow(WarmupResultRow row)
        => new(
            row.WarmupResultId,
            row.CapabilityKey,
            row.ProfileKey,
            row.PassCount,
            row.Passed,
            row.MeasuredAt,
            ParseDetails(row.DetailsJson));

    internal static IReadOnlyList<AdminRuntimeCapabilityCatalogDto> GetCapabilityCatalog()
        => BuildCapabilityCatalog();

    private static IReadOnlyList<AdminRuntimeCapabilityCatalogDto> BuildCapabilityCatalog()
        => RuntimeCapabilities.Select(static def => new AdminRuntimeCapabilityCatalogDto(
            def.Key,
            def.DisplayName,
            def.Family,
            def.RuntimeKey,
            def.Implemented,
            def.DefaultDesiredEnabled,
            def.StatusNote)).ToArray();

    private static IReadOnlyList<AdminRuntimeCatalogRuntimeDto> BuildRuntimes(RagOptions rag)
        =>
        [
            new(
                Key: "qdrant",
                Label: "Qdrant vector store",
                Kind: "vector_store",
                Enabled: !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl),
                BaseUrl: rag.QdrantBaseUrl,
                Model: rag.QdrantCollection),
            new(
                Key: "tei-embeddings",
                Label: "TEI embeddings",
                Kind: "embedding_runtime",
                Enabled: !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl),
                BaseUrl: rag.EmbeddingsBaseUrl,
                Model: rag.EmbeddingsModel),
            new(
                Key: "tei-rerank",
                Label: "TEI rerank",
                Kind: "rerank_runtime",
                Enabled: rag.EnableRerank,
                BaseUrl: string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
                Model: rag.RerankModel)
        ];

    private static IReadOnlyList<AdminRuntimeWarmupProfileDto> BuildWarmupProfiles(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: options.DefaultProfileKey,
                Label: "Default local qualification",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_if_enabled"
                ])
        ];

    private static AdminRuntimeWarmupProfileDto ResolveProfile(string? requestedProfileKey, RuntimeGovernanceOptions options)
    {
        var profiles = BuildWarmupProfiles(options);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Key, requestedProfileKey, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    private static IReadOnlyList<RuntimeCapabilityDefinition> ResolveCapabilitySelection(string? capabilityKey)
    {
        if (string.IsNullOrWhiteSpace(capabilityKey))
            return RuntimeCapabilities;

        var match = RuntimeCapabilities.FirstOrDefault(def => string.Equals(def.Key, capabilityKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? RuntimeCapabilities : [match];
    }

    private static IReadOnlyDictionary<string, object?>? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ConvertObject(doc.RootElement);
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = ConvertValue(property.Value);

        return result;
    }

    private static object? ConvertValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToArray(),
            JsonValueKind.String => element.TryGetDateTimeOffset(out var dto) ? dto : element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
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

    private static readonly RuntimeCapabilityDefinition[] RuntimeCapabilities =
    [
        new("core.retrieval", "Core retrieval", "core", "retrieval-stack", true, true, "Dense/sparse/exact retrieval stack qualified against Qdrant and TEI."),
        new("capability_a.corpus_enrichment", "Capability A - Corpus Enrichment", "A", "server-capability-a", false, false, "Capability A remains intentionally unimplemented in v3.0 backend."),
        new("capability_b.backoffice_generation", "Capability B - Backoffice Generation", "B", "server-capability-b", false, false, "Capability B remains intentionally unimplemented in v3.0 backend."),
        new("capability_c.retrieval_intelligence", "Capability C - Retrieval Intelligence", "C", "server-capability-c", false, false, "Capability C remains intentionally unimplemented in v3.0 backend.")
    ];

    private sealed record RuntimeCapabilityDefinition(
        string Key,
        string DisplayName,
        string Family,
        string RuntimeKey,
        bool Implemented,
        bool DefaultDesiredEnabled,
        string StatusNote);

    private sealed record CapabilityEvaluation(
        AdminRuntimeCapabilityStateDto State,
        AdminRuntimeWarmupResultDto? WarmupResult);

    internal sealed record CapabilitySelectionUpdateResult(
        AdminRuntimeCapabilityStateDto? State,
        string? Error);

    private sealed record WarmupPassResult(
        bool Passed,
        DateTimeOffset MeasuredAt,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record WarmupCheckResult(
        bool Passed,
        string Status,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record CapabilityStateRow(
        string CapabilityKey,
        string CapabilityFamily,
        string DisplayName,
        string RuntimeKey,
        string ProfileKey,
        bool Implemented,
        bool DesiredEnabled,
        bool Installed,
        bool Configured,
        bool Healthy,
        bool Qualified,
        bool Authorized,
        bool Selected,
        int PassCount,
        DateTimeOffset? LastCheckedAt,
        DateTimeOffset? LastQualifiedAt,
        string? LastError,
        string? DetailsJson);

    private sealed record WarmupResultRow(
        Guid WarmupResultId,
        string CapabilityKey,
        string ProfileKey,
        int PassCount,
        bool Passed,
        DateTimeOffset MeasuredAt,
        string? DetailsJson);

    private static async Task<Dictionary<string, CapabilityStateRow>> LoadCapabilityStateRowsAsync(NpgsqlConnection conn, CancellationToken ct)
        => (await conn.QueryAsync<CapabilityStateRow>(new CommandDefinition("""
SELECT
  capability_key AS "CapabilityKey",
  capability_family AS "CapabilityFamily",
  display_name AS "DisplayName",
  runtime_key AS "RuntimeKey",
  profile_key AS "ProfileKey",
  implemented AS "Implemented",
  desired_enabled AS "DesiredEnabled",
  installed AS "Installed",
  configured AS "Configured",
  healthy AS "Healthy",
  qualified AS "Qualified",
  authorized AS "Authorized",
  selected AS "Selected",
  pass_count AS "PassCount",
  last_checked_at AS "LastCheckedAt",
  last_qualified_at AS "LastQualifiedAt",
  last_error AS "LastError",
  details AS "DetailsJson"
FROM runtime_capability_state
ORDER BY capability_key;
""", cancellationToken: ct))).ToDictionary(static row => row.CapabilityKey, StringComparer.Ordinal);

    private static async Task<AdminRuntimeWarmupResultDto[]> LoadWarmupResultsAsync(
        NpgsqlConnection conn,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<WarmupResultRow>(new CommandDefinition("""
SELECT
  warmup_result_id AS "WarmupResultId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  pass_count AS "PassCount",
  passed AS "Passed",
  measured_at AS "MeasuredAt",
  details AS "DetailsJson"
FROM runtime_warmup_results
WHERE (@capability_key IS NULL OR capability_key = @capability_key)
ORDER BY measured_at DESC
LIMIT @limit;
""", new { capability_key = string.IsNullOrWhiteSpace(capabilityKey) ? null : capabilityKey.Trim(), limit }, cancellationToken: ct)))
            .Select(MapWarmupResultRow)
            .ToArray();
}
