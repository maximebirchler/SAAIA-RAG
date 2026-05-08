using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Security;

namespace SAAIA.Backend.Endpoints;

public static class ReadyEndpoints
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private const string ReadyCacheKey = "ready:v6";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed record ReadinessSnapshot(bool Ok, object Payload);

    public static void Map(WebApplication app)
    {
        app.MapGet("/ready", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        IHostEnvironment env,
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> ragOpt,
        IOptions<ChatOptions> chatOpt,
        IOptions<IngestionOptions> ingestionOpt,
        SignedConfigStatus? cfgStatus,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var requestId = ctx.GetRequestId();

        // 1) Cache rapide
        if (cache.TryGetValue(ReadyCacheKey, out ReadinessSnapshot? cached) && cached is not null)
        {
            return cached.Ok
                ? Results.Ok(cached.Payload)
                : Results.Json(cached.Payload, statusCode: 503);
        }

        // 2) Gate anti-parallélisme
        await _gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(ReadyCacheKey, out cached) && cached is not null)
            {
                return cached.Ok
                    ? Results.Ok(cached.Payload)
                    : Results.Json(cached.Payload, statusCode: 503);
            }

            var details = new Dictionary<string, object?>();
            var ok = true;

            // DB check
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                details["db"] = true;
            }
            catch (Exception ex)
            {
                ok = false;
                details["db"] = false;
                details["db_error"] = ex.Message;
            }

            // TEI check (embedding ping, timeout court) => donne aussi la dimension (utile pour bootstrap Qdrant)
            var teiDim = 0;
            try
            {
                var rag = ragOpt.Value;
                var tei = httpFactory.CreateClient("tei");
                tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

                using var teiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                teiCts.CancelAfter(TimeSpan.FromSeconds(10));

                var vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { "ping" }, teiCts.Token);
                teiDim = (vectors is { Length: > 0 }) ? vectors[0].Length : 0;

                details["tei"] = teiDim > 0;
                details["tei_dim"] = teiDim;
                if (teiDim <= 0) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["tei"] = false;
                details["tei_error"] = ex.Message;
            }

            // Qdrant check (+ auto-create collection if missing and TEI is OK)
            try
            {
                var rag = ragOpt.Value;

                // P2.1c: policy (signed) + secret resolution (ENV/FILE) via Rag:QdrantApiKeyRef
                var effectiveKey = SecretRefResolver.Resolve(rag.QdrantApiKey, rag.QdrantApiKeyRef, env.ContentRootPath, out var keySource);
                details["qdrant_auth_policy_required"] = string.Equals(env.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase) && rag.RequireQdrantAuthInProd;
                details["qdrant_auth_configured"] = !string.IsNullOrWhiteSpace(effectiveKey);
                details["qdrant_auth_mode"] = rag.QdrantAuthMode;
                details["qdrant_auth_source"] = keySource;

                var qdrant = httpFactory.CreateClient("qdrant");
                qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

                // Fresh install: bootstrap collection so /ready can turn green without requiring a first ingestion/query.
                if (teiDim > 0)
                {
                    await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, teiDim, ct);
                    details["qdrant_collection_bootstrap"] = true;
                }
                else
                {
                    details["qdrant_collection_bootstrap"] = false;
                }

                using var resp = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);
                details["qdrant"] = resp.IsSuccessStatusCode;
                details["qdrant_status"] = (int)resp.StatusCode;

                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                {
                    details["qdrant_auth_required"] = true;
                    if (string.IsNullOrWhiteSpace(effectiveKey))
                        details["qdrant_auth_hint"] = "Qdrant returned 401/403 but backend has no effective Qdrant key (Rag:QdrantApiKey/Ref).";
                }

                if (!resp.IsSuccessStatusCode) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["qdrant"] = false;
                details["qdrant_error"] = ex.Message;
            }

            // Backend LLM is an optional server capability for backoffice jobs/corpus enrichment.
            // It is deliberately not part of user chat readiness: the nominal chat writer runs
            // on the client-side LLM per CDC v3.1.
            var llmReady = await ProbeLlmReadinessAsync(httpFactory, chatOpt.Value, details, ct);
            ApplyBackofficeLlmReadinessPolicy(chatOpt.Value, llmReady, details);
            ProbeIngestionReadiness(ingestionOpt.Value, details);
            if (!ProbeOcrReadiness(ingestionOpt.Value, details))
                ok = false;

            // Signed deployment config status
            if (cfgStatus is not null)
            {
                details["config_signature_mode"] = cfgStatus.Mode;
                details["config_signature_present"] = cfgStatus.SignaturePresent;
                details["config_signature_verified"] = cfgStatus.Verified;
            }

            var payload = new { ok, ts = DateTimeOffset.UtcNow, requestId, details };
            var snap = new ReadinessSnapshot(ok, payload);

            cache.Set(ReadyCacheKey, snap, CacheTtl);

            return ok
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: 503);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool ApplyBackofficeLlmReadinessPolicy(
        ChatOptions chat,
        bool llmReady,
        Dictionary<string, object?> details)
    {
        var backofficeEnabled = string.Equals(
            Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        details["llm_backoffice_enabled"] = backofficeEnabled;
        details["llm_backoffice_required"] = false;

        if (!backofficeEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(chat.LlmBaseUrl))
        {
            details["llm"] = "server-backoffice-missing-url";
            details["llm_error"] = "BACKOFFICE_LLM_ENABLED=true but Chat:LlmBaseUrl is empty.";
            details["llm_backoffice_degraded"] = true;
            return true;
        }

        details["llm_backoffice_degraded"] = !llmReady;
        return true;
    }

    private static async Task<bool> ProbeLlmReadinessAsync(
        IHttpClientFactory httpFactory,
        ChatOptions chat,
        Dictionary<string, object?> details,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chat.LlmBaseUrl))
        {
            details["llm"] = "client-only";
            details["llm_server_configured"] = false;
            return true;
        }

        details["llm"] = "server-configured";
        details["llm_server_configured"] = true;
        details["llm_model"] = chat.LlmModel;

        try
        {
            var llm = httpFactory.CreateClient("llm");
            using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            llmCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await llm.GetAsync("v1/models", llmCts.Token);
            details["llm_status"] = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                details["llm"] = "server-unavailable";
                return false;
            }

            details["llm"] = "server-ok";
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            details["llm"] = "server-timeout";
            details["llm_error"] = "timeout";
            return false;
        }
        catch (Exception ex)
        {
            details["llm"] = "server-unavailable";
            details["llm_error"] = ex.Message;
            return false;
        }
    }

    internal static bool ProbeOcrReadiness(
        IngestionOptions ingestion,
        Dictionary<string, object?> details)
    {
        details["ocr_enabled"] = ingestion.OcrEnabled;
        if (!ingestion.OcrEnabled)
        {
            details["ocr_status"] = "disabled";
            details["ocr_required"] = false;
            details["ocr_configured"] = false;
            details["ocr_ready"] = true;
            return true;
        }

        var commandConfigured = !string.IsNullOrWhiteSpace(ingestion.OcrCommand);
        var commandAvailable = commandConfigured && ResolveCommandAvailable(ingestion.OcrCommand);
        var imageRendererCommand = PdfOcrTextExtractor.ResolveImageRendererCommand(ingestion);
        var imageTextCommand = PdfOcrTextExtractor.ResolveImageTextCommand(ingestion);
        var imageRendererAvailable = !ingestion.OcrImagePageEnabled || ResolveCommandAvailable(imageRendererCommand);
        var imageTextCommandAvailable = ResolveCommandAvailable(imageTextCommand);
        var imageTextAvailable = !ingestion.OcrImagePageEnabled || imageTextCommandAvailable;
        var languageReadiness = ProbeOcrLanguageReadiness(ingestion, imageTextCommandAvailable);
        var languageMode = ResolveOcrLanguageMode(ingestion.OcrLanguages);
        var ready = commandConfigured
            && commandAvailable
            && imageRendererAvailable
            && imageTextAvailable
            && languageReadiness.Ready;

        details["ocr_status"] = ready ? "ready" : "not_ready";
        details["ocr_required"] = true;
        details["ocr_configured"] = commandConfigured;
        details["ocr_engine"] = string.IsNullOrWhiteSpace(ingestion.OcrCommand)
            ? null
            : Path.GetFileName(ingestion.OcrCommand.Trim());
        details["ocr_languages"] = string.IsNullOrWhiteSpace(ingestion.OcrLanguages)
            ? null
            : ingestion.OcrLanguages.Trim();
        details["ocr_language_mode"] = languageMode;
        details["ocr_auto_max_languages"] = PdfOcrTextExtractor.ResolveAutoOcrLanguageLimit(ingestion);
        details["ocr_auto_fallback_languages"] = string.IsNullOrWhiteSpace(ingestion.OcrAutoFallbackLanguages)
            ? null
            : ingestion.OcrAutoFallbackLanguages.Trim();
        details["ocr_auto_detect_languages"] = ingestion.OcrAutoDetectLanguages;
        details["ocr_image_page_enabled"] = ingestion.OcrImagePageEnabled;
        details["ocr_image_page_max_pages"] = ingestion.OcrImagePageMaxPages;
        details["ocr_image_page_render_dpi"] = ingestion.OcrImagePageRenderDpi;
        details["ocr_image_page_segmentation_mode"] = ingestion.OcrImagePageSegmentationMode;
        details["ocr_image_page_max_total_seconds"] = ingestion.OcrImagePageMaxTotalSeconds <= 0
            ? "disabled"
            : Math.Clamp(ingestion.OcrImagePageMaxTotalSeconds, 30, 86400).ToString(CultureInfo.InvariantCulture);
        details["ocr_image_renderer_command"] = imageRendererCommand;
        details["ocr_image_text_command"] = imageTextCommand;
        details["ocr_command_available"] = commandAvailable;
        details["ocr_image_renderer_available"] = ingestion.OcrImagePageEnabled ? imageRendererAvailable : null;
        details["ocr_image_text_available"] = ingestion.OcrImagePageEnabled ? imageTextAvailable : null;
        details["ocr_language_configured_codes"] = languageReadiness.ConfiguredLanguages;
        details["ocr_language_installed_count"] = languageReadiness.InstalledLanguageCount;
        details["ocr_languages_verified"] = languageReadiness.Verified;
        details["ocr_missing_languages"] = languageReadiness.MissingLanguages;
        details["ocr_ready"] = ready;
        if (ready && languageReadiness.ConfiguredLanguages.Length > 0 && !languageReadiness.Verified)
            details["ocr_warning"] = "ocr_languages_not_verified";

        if (!ready)
        {
            var missing = new List<string>();
            if (!commandConfigured)
                missing.Add("ocr_command");
            else if (!commandAvailable)
                missing.Add("ocr_command_unavailable");
            if (ingestion.OcrImagePageEnabled && !imageRendererAvailable)
                missing.Add("ocr_image_renderer_unavailable");
            if (ingestion.OcrImagePageEnabled && !imageTextAvailable)
                missing.Add("ocr_image_text_unavailable");
            if (!languageReadiness.Ready)
                missing.Add("ocr_languages_missing");
            details["ocr_error"] = string.Join(",", missing);
        }

        return ready;
    }

    internal static OcrLanguageReadiness ProbeOcrLanguageReadiness(
        IngestionOptions ingestion,
        bool canProbeLanguages)
    {
        var configured = PdfOcrTextExtractor.ResolveExplicitConfiguredOcrLanguagesForReadiness(ingestion).ToArray();
        if (configured.Length == 0)
            return new OcrLanguageReadiness(true, true, [], 0, []);

        if (!canProbeLanguages)
            return new OcrLanguageReadiness(true, false, configured, 0, []);

        var installed = PdfOcrTextExtractor.LoadInstalledTesseractLanguagesForReadiness(ingestion).ToArray();
        return ResolveOcrLanguageReadiness(configured, installed);
    }

    internal static OcrLanguageReadiness ResolveOcrLanguageReadiness(
        IReadOnlyList<string> configuredLanguages,
        IReadOnlyList<string> installedLanguages)
    {
        var configured = configuredLanguages
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installed = installedLanguages
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configured.Length == 0)
            return new OcrLanguageReadiness(true, true, [], installed.Count, []);

        if (installed.Count == 0)
            return new OcrLanguageReadiness(true, false, configured, 0, []);

        var missing = configured
            .Where(language => !installed.Contains(language))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OcrLanguageReadiness(
            Ready: missing.Length == 0,
            Verified: true,
            ConfiguredLanguages: configured,
            InstalledLanguageCount: installed.Count,
            MissingLanguages: missing);
    }

    private static void ProbeIngestionReadiness(
        IngestionOptions ingestion,
        Dictionary<string, object?> details)
    {
        details["ingestion_embeddings_batch_size"] = IngestionOptions.ResolveEmbeddingsBatchSize(ingestion.EmbeddingsBatchSize);
        details["ingestion_embeddings_batch_size_configured"] = ingestion.EmbeddingsBatchSize;
        details["ingestion_embeddings_batch_size_min"] = IngestionOptions.MinEmbeddingsBatchSize;
        details["ingestion_embeddings_batch_size_max"] = IngestionOptions.MaxEmbeddingsBatchSize;
        details["ingestion_embeddings_batch_size_dynamic"] = false;
        details["ingestion_worker_concurrency"] = Math.Max(1, ingestion.WorkerConcurrency);
        details["ingestion_tei_max_concurrency"] = Math.Max(1, ingestion.TeiMaxConcurrency);
        details["ingestion_qdrant_max_concurrency"] = Math.Max(1, ingestion.QdrantMaxConcurrency);
        details["ingestion_ocr_max_concurrency"] = Math.Max(1, ingestion.OcrMaxConcurrency);
        details["ingestion_ocr_image_page_max_pages"] = ingestion.OcrImagePageMaxPages <= 0
            ? "all"
            : Math.Clamp(ingestion.OcrImagePageMaxPages, 1, 500).ToString(CultureInfo.InvariantCulture);
        details["ingestion_ocr_image_page_max_total_seconds"] = ingestion.OcrImagePageMaxTotalSeconds <= 0
            ? "disabled"
            : Math.Clamp(ingestion.OcrImagePageMaxTotalSeconds, 30, 86400).ToString(CultureInfo.InvariantCulture);
        details["ingestion_ocr_image_page_reliability"] = ingestion.OcrImagePageMaxPages <= 0
            && ingestion.OcrImagePageMaxTotalSeconds <= 0
            ? "complete"
            : ingestion.OcrImagePageMaxPages <= 0
                ? "time_budgeted"
                : "budgeted";
        details["ingestion_tei_timeout_seconds"] = Math.Max(1, ingestion.TeiTimeoutSeconds);
        details["ingestion_qdrant_timeout_seconds"] = Math.Max(1, ingestion.QdrantTimeoutSeconds);
    }

    private static string ResolveOcrLanguageMode(string? languages)
    {
        if (string.IsNullOrWhiteSpace(languages))
            return "default";

        var tokens = languages.Split(
            ['+', ',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(static token =>
            token.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || token.Equals("all", StringComparison.OrdinalIgnoreCase))
            ? "auto"
            : "explicit";
    }

    private static bool ResolveCommandAvailable(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(trimmed);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, trimmed + ext);
                if (File.Exists(candidate))
                    return true;
            }
        }

        return false;
    }
}

internal sealed record OcrLanguageReadiness(
    bool Ready,
    bool Verified,
    string[] ConfiguredLanguages,
    int InstalledLanguageCount,
    string[] MissingLanguages);
