using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
    public async Task<JsonElement> AdminCatalogHealthAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/admin/catalog/health"), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var legacy = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/admin/catalog/snapshot/status"), ct).ConfigureAwait(false);
            legacy.EnsureSuccessStatusCode();
            var legacyJson = await legacy.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            return legacyDoc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> AdminCatalogRescanNowAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Post, "/admin/catalog/rescan", "{}"), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var legacy = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Post, "/admin/catalog/snapshot/refresh", "{}"), ct).ConfigureAwait(false);
            legacy.EnsureSuccessStatusCode();
            var legacyJson = await legacy.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            return legacyDoc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> AdminIngestionReindexAsync(string docPath, CancellationToken ct)
        => AdminIngestionReindexAsync(docPath, null, ct);

    public async Task<JsonElement> AdminIngestionReindexAsync(string? docPath, string? docId, CancellationToken ct)
    {
        Guid? parsedDocId = null;
        if (!string.IsNullOrWhiteSpace(docId) && Guid.TryParse(docId, out var guid))
            parsedDocId = guid;

        var adminBody = JsonSerializer.Serialize(new
        {
            docPath,
            docId = parsedDocId,
            level = (string?)null,
            jobId = (Guid?)null,
            docLanguage = (string?)null,
            sourceHash = (string?)null,
            summaryText = (string?)null,
            meta = (object?)null,
            force = (bool?)null
        }, JsonOpts);

        using var adminResp = await SendWithRateLimitRetryAsync(
            () => NewAdminRequest(HttpMethod.Post, "/admin/ingestion/reindex", adminBody),
            ct).ConfigureAwait(false);

        if (adminResp.StatusCode != HttpStatusCode.NotFound)
        {
            if (adminResp.StatusCode == HttpStatusCode.Conflict)
            {
                var conflictJson = await adminResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var conflictDoc = JsonDocument.Parse(conflictJson);
                return conflictDoc.RootElement.Clone();
            }

            adminResp.EnsureSuccessStatusCode();
            var adminJson = await adminResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var adminDoc = JsonDocument.Parse(adminJson);
            return adminDoc.RootElement.Clone();
        }

        var legacyBody = JsonSerializer.Serialize(new
        {
            docPath = docPath ?? string.Empty,
            category = (string?)null,
            action = "upsert"
        }, JsonOpts);

        using var legacyResp = await SendWithRateLimitRetryAsync(
            () => NewAdminRequest(HttpMethod.Post, "/ingest/enqueue", legacyBody),
            ct).ConfigureAwait(false);
        legacyResp.EnsureSuccessStatusCode();
        var legacyJson = await legacyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var legacyDoc = JsonDocument.Parse(legacyJson);
        return legacyDoc.RootElement.Clone();
    }

    public Task<JsonElement> AdminJobsListAsync(
        string? type,
        int limit,
        int offset,
        string? dateField,
        string? dateFrom,
        string? dateTo,
        string? sortDirection,
        CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(type))
            qs.Add($"type={Uri.EscapeDataString(type.Trim())}");
        if (!string.IsNullOrWhiteSpace(dateField))
            qs.Add($"dateField={Uri.EscapeDataString(dateField.Trim())}");
        if (!string.IsNullOrWhiteSpace(dateFrom))
            qs.Add($"dateFrom={Uri.EscapeDataString(dateFrom.Trim())}");
        if (!string.IsNullOrWhiteSpace(dateTo))
            qs.Add($"dateTo={Uri.EscapeDataString(dateTo.Trim())}");
        if (!string.IsNullOrWhiteSpace(sortDirection))
            qs.Add($"sortDir={Uri.EscapeDataString(sortDirection.Trim())}");

        return SendJsonAsync(HttpMethod.Get, "/admin/jobs?" + string.Join("&", qs), null, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsCancelAsync(string jobId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jobId }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/cancel", body, admin: true, ct);
    }

    public Task<JsonElement> AdminAuditListAsync(
        string? action,
        string? target,
        string? since,
        string? until,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 1000);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(action))
            qs.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (!string.IsNullOrWhiteSpace(target))
            qs.Add($"target={Uri.EscapeDataString(target.Trim())}");
        if (!string.IsNullOrWhiteSpace(since))
            qs.Add($"since={Uri.EscapeDataString(since.Trim())}");
        if (!string.IsNullOrWhiteSpace(until))
            qs.Add($"until={Uri.EscapeDataString(until.Trim())}");

        return SendJsonAsync(HttpMethod.Get, "/admin/audit?" + string.Join("&", qs), null, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsPauseAsync(string jobId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jobId }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/pause", body, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsResumeAsync(string jobId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jobId }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/resume", body, admin: true, ct);
    }

    public Task<JsonElement> AdminJobGetAsync(string jobId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId is required.", nameof(jobId));

        return SendJsonAsync(HttpMethod.Get, "/admin/jobs/" + Uri.EscapeDataString(jobId.Trim()), null, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsPurgeAsync(string? scope, string? type, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
            type = string.IsNullOrWhiteSpace(type) ? null : type.Trim()
        }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/purge", body, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsDeleteHistoryAsync(IReadOnlyCollection<string> jobIds, CancellationToken ct)
    {
        var ids = (jobIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var body = JsonSerializer.Serialize(new { jobIds = ids }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/delete_history", body, admin: true, ct);
    }

    public Task<JsonElement> AdminQdrantHealthAsync(CancellationToken ct)
        => SendJsonAsync(HttpMethod.Get, "/admin/qdrant/health", null, admin: true, ct);

    public Task<JsonElement> AdminRuntimeOperationalSummaryAsync(CancellationToken ct)
        => SendJsonAsync(HttpMethod.Get, "/admin/runtime/operational-summary", null, admin: true, ct);

    public Task<JsonElement> AdminRuntimeCapabilityBQualityReviewSummaryAsync(CancellationToken ct)
        => SendJsonAsync(
            HttpMethod.Get,
            "/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review-summary",
            null,
            admin: true,
            ct);

    public Task<JsonElement> AdminRuntimeCapabilityBQualityReviewAsync(int limit, CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        return SendJsonAsync(
            HttpMethod.Get,
            $"/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review?limit={safeLimit}",
            null,
            admin: true,
            ct);
    }

    public Task<JsonElement> AdminRuntimeCapabilityBEnqueueAsync(
        IReadOnlyList<Guid>? docIds,
        IReadOnlyList<string>? docPaths,
        bool dryRun,
        bool force,
        int? maxCandidates,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            docIds,
            docPaths,
            category = (string?)null,
            maxCandidates,
            dryRun,
            force
        }, JsonOpts);

        return SendJsonAsync(
            HttpMethod.Post,
            "/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue",
            body,
            admin: true,
            ct);
    }

    public Task<JsonElement> AdminRuntimeCapabilityAEnqueueAsync(
        IReadOnlyList<string>? reasonFilters,
        bool dryRun,
        bool allowUnsafeCandidates,
        int? maxCandidates,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            reasonFilters,
            dryRun,
            allowUnsafeCandidates,
            maxCandidates
        }, JsonOpts);

        return SendJsonAsync(
            HttpMethod.Post,
            "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue",
            body,
            admin: true,
            ct);
    }
}
