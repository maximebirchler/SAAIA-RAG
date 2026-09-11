using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisSearchRequest(
    string Query,
    string? Category = null,
    int TopK = 12,
    string? DocId = null,
    string? DocPath = null,
    int? PageStart = null,
    int? PageEnd = null,
    int? MaxPerDocument = null,
    int? MaxPerPage = null);

internal sealed record AdvancedAnalysisSearchObservation(
    string Query,
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
    IReadOnlyList<string> DegradedRetrievers,
    long ElapsedMilliseconds,
    int ToolCallNumber);

internal sealed record AdvancedAnalysisToolEventSummary(
    int EventSequence,
    int AttemptCount,
    string ToolName,
    string Status,
    AdvancedAnalysisSearchRequest Request,
    IReadOnlyList<AdvancedAnalysisResultEvidence> Evidence,
    IReadOnlyList<string> DegradedRetrievers,
    long ElapsedMilliseconds,
    string? ErrorCode);

internal interface IAdvancedAnalysisToolGateway
{
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence { get; }

    Task<AdvancedAnalysisSearchObservation> SearchAsync(
        AdvancedAnalysisSearchRequest request,
        CancellationToken cancellationToken);
}

internal interface IAdvancedAnalysisToolGatewayFactory
{
    IAdvancedAnalysisToolGateway Create(
        Guid jobId,
        Guid tenantId,
        string workerId,
        int attemptCount,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence);
}

internal sealed class AdvancedAnalysisToolGatewayFactory :
    IAdvancedAnalysisToolGatewayFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RagOptions _ragOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly RagSearchBulkhead _bulkhead;
    private readonly AdvancedAnalysisEvidenceResolver _resolver;
    private readonly AdvancedAnalysisJobStore _store;
    private readonly AdvancedAnalysisOptions _options;

    public AdvancedAnalysisToolGatewayFactory(
        NpgsqlDataSource dataSource,
        IOptions<RagOptions> ragOptions,
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        RagSearchBulkhead bulkhead,
        AdvancedAnalysisEvidenceResolver resolver,
        AdvancedAnalysisJobStore store,
        IOptions<AdvancedAnalysisOptions> options)
    {
        _dataSource = dataSource;
        _ragOptions = ragOptions.Value;
        _httpClientFactory = httpClientFactory;
        _services = services;
        _bulkhead = bulkhead;
        _resolver = resolver;
        _store = store;
        _options = options.Value;
    }

    public IAdvancedAnalysisToolGateway Create(
        Guid jobId,
        Guid tenantId,
        string workerId,
        int attemptCount,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence)
        => new AdvancedAnalysisToolGateway(
            jobId,
            tenantId,
            initialEvidence,
            _dataSource,
            _ragOptions,
            _httpClientFactory,
            _services,
            _bulkhead,
            _resolver,
            _options,
            _store,
            workerId,
            attemptCount);
}

internal sealed class AdvancedAnalysisToolGateway : IAdvancedAnalysisToolGateway
{
    private const int MaximumQueryCharacters = 8_000;
    private const int MaximumScopeCharacters = 2_000;
    private readonly Guid _jobId;
    private readonly Guid _tenantId;
    private readonly NpgsqlDataSource _dataSource;
    private readonly RagOptions _ragOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly RagSearchBulkhead _bulkhead;
    private readonly AdvancedAnalysisEvidenceResolver _resolver;
    private readonly AdvancedAnalysisJobStore? _eventStore;
    private readonly string? _workerId;
    private readonly int _attemptCount;
    private readonly int _maximumToolCalls;
    private readonly int _maximumSearchTopK;
    private readonly int _maximumEvidenceItems;
    private readonly long _maximumElapsedMilliseconds;
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly List<AdvancedAnalysisResolvedEvidence> _evidence;
    private readonly Dictionary<string, AdvancedAnalysisResolvedEvidence>
        _evidenceByCanonicalKey;
    private int _toolCallCount;
    private long _elapsedMilliseconds;

    public AdvancedAnalysisToolGateway(
        Guid jobId,
        Guid tenantId,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence,
        NpgsqlDataSource dataSource,
        RagOptions ragOptions,
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        RagSearchBulkhead bulkhead,
        AdvancedAnalysisEvidenceResolver resolver,
        AdvancedAnalysisOptions options,
        AdvancedAnalysisJobStore? eventStore = null,
        string? workerId = null,
        int attemptCount = 0)
    {
        _jobId = jobId;
        _tenantId = tenantId;
        _dataSource = dataSource;
        _ragOptions = ragOptions;
        _httpClientFactory = httpClientFactory;
        _services = services;
        _bulkhead = bulkhead;
        _resolver = resolver;
        _eventStore = eventStore;
        _workerId = workerId;
        _attemptCount = attemptCount;
        _maximumToolCalls = Math.Clamp(options.MaximumToolCalls, 1, 256);
        _maximumSearchTopK = Math.Clamp(options.MaximumSearchTopK, 1, 500);
        _maximumEvidenceItems = Math.Clamp(
            options.MaximumAccumulatedEvidenceItems,
            1,
            2_000);
        _maximumElapsedMilliseconds = Math.Clamp(
            options.MaximumToolElapsedMilliseconds,
            1_000,
            3_600_000);
        _evidence = new List<AdvancedAnalysisResolvedEvidence>(initialEvidence);
        _evidenceByCanonicalKey = initialEvidence
            .GroupBy(static item => CanonicalKey(item.Reference), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        if (_evidenceByCanonicalKey.Count != initialEvidence.Count
            || initialEvidence.Count > _maximumEvidenceItems)
        {
            throw new AdvancedAnalysisToolException(
                "initial_evidence_limit_or_duplicate");
        }
    }

    public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence =>
        _evidence.ToArray();

    public async Task<AdvancedAnalysisSearchObservation> SearchAsync(
        AdvancedAnalysisSearchRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var callNumber = 0;
        Stopwatch? stopwatch = null;
        try
        {
            callNumber = checked(_toolCallCount + 1);
            if (callNumber > _maximumToolCalls)
                throw new AdvancedAnalysisToolException("tool_call_limit_exceeded");
            if (_elapsedMilliseconds >= _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");
            _toolCallCount = callNumber;

            using var admission = await _bulkhead
                .AcquireAsync(cancellationToken)
                .ConfigureAwait(false);
            if (admission is null)
                throw new AdvancedAnalysisToolException("retrieval_capacity_busy");

            var context = new DefaultHttpContext
            {
                RequestServices = _services
            };
            context.RequestAborted = cancellationToken;
            context.TraceIdentifier = $"advanced:{_jobId:N}:{callNumber}";
            context.Items[ApiKeyAuth.TenantIdItemKey] = _tenantId;
            context.Items[RequestIdMiddleware.RequestIdItemKey] =
                context.TraceIdentifier;
            stopwatch = Stopwatch.StartNew();
            var response = await RagEndpoints.SearchCoreAsync(
                context,
                _dataSource,
                _ragOptions,
                _httpClientFactory,
                new RagSearchRequestDto(
                    Query: request.Query.Trim(),
                    Category: NullIfBlank(request.Category),
                    TopK: Math.Clamp(request.TopK, 1, _maximumSearchTopK),
                    MinScore: 0.0,
                    Candidates: null,
                    MaxPerDoc: NormalizePositive(request.MaxPerDocument),
                    MaxPerPage: NormalizePositive(request.MaxPerPage),
                    Mode: "broad",
                    Diversity: null,
                    DocId: NullIfBlank(request.DocId),
                    DocPath: NormalizePath(request.DocPath),
                    IncludeContextualSnippet: false,
                    CategoryPath: null,
                    CategoryRef: null,
                    IncludeDiagnostics: false,
                    PageStart: request.PageStart,
                    PageEnd: request.PageEnd,
                    ResearchMode: "advanced_analysis",
                    IncludeResearchSurfaces: false,
                    SourceBackedCanonical: true))
                .ConfigureAwait(false);
            stopwatch.Stop();
            _elapsedMilliseconds = checked(
                _elapsedMilliseconds + stopwatch.ElapsedMilliseconds);
            if (_elapsedMilliseconds > _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");

            var references = response.Matches
                .Where(static match => Guid.TryParse(match.ChunkId, out _))
                .Select(static match => new AdvancedAnalysisEvidenceReference
                {
                    DocId = NullIfBlank(match.DocId),
                    FileName = NullIfBlank(match.DocName),
                    DocPath = NullIfBlank(match.DocPath),
                    PageStart = match.PageStart.GetValueOrDefault(),
                    PageEnd = match.PageEnd.GetValueOrDefault(),
                    ChunkId = match.ChunkId
                })
                .ToList();
            var revalidated = await _resolver.ResolveAsync(
                _tenantId,
                references,
                cancellationToken).ConfigureAwait(false);
            if (!revalidated.IsValid)
            {
                throw new AdvancedAnalysisToolException(
                    revalidated.ErrorCode ?? "retrieval_evidence_revalidation_failed");
            }

            var observationEvidence = new List<AdvancedAnalysisResolvedEvidence>(
                revalidated.Evidence.Count);
            var newEvidence = revalidated.Evidence
                .Where(item => !_evidenceByCanonicalKey.ContainsKey(
                    CanonicalKey(item.Reference)))
                .GroupBy(
                    static item => CanonicalKey(item.Reference),
                    StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToList();
            if (_evidence.Count + newEvidence.Count > _maximumEvidenceItems)
            {
                throw new AdvancedAnalysisToolException(
                    "accumulated_evidence_limit_exceeded");
            }
            foreach (var item in newEvidence)
            {
                var key = CanonicalKey(item.Reference);
                _evidenceByCanonicalKey.Add(key, item);
                _evidence.Add(item);
            }
            foreach (var item in revalidated.Evidence)
            {
                var key = CanonicalKey(item.Reference);
                var canonical = _evidenceByCanonicalKey[key];
                observationEvidence.Add(canonical);
            }

            var degraded = response.DegradedRetrievers ?? [];
            await PersistEventAsync(
                "succeeded",
                request,
                observationEvidence,
                degraded,
                stopwatch.ElapsedMilliseconds,
                errorCode: null,
                cancellationToken).ConfigureAwait(false);
            return new AdvancedAnalysisSearchObservation(
                request.Query.Trim(),
                observationEvidence,
                degraded,
                stopwatch.ElapsedMilliseconds,
                callNumber);
        }
        catch (Exception ex)
        {
            stopwatch?.Stop();
            if (callNumber > 0)
            {
                var errorCode = ex is AdvancedAnalysisToolException toolException
                    ? toolException.ErrorCode
                    : "retrieval_tool_failed";
                await PersistEventAsync(
                    "failed",
                    request,
                    [],
                    [],
                    stopwatch?.ElapsedMilliseconds ?? 0,
                    errorCode,
                    cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _serialGate.Release();
        }
    }

    private async Task PersistEventAsync(
        string status,
        AdvancedAnalysisSearchRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<string> degradedRetrievers,
        long elapsedMilliseconds,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        if (_eventStore is null)
            return;
        if (string.IsNullOrWhiteSpace(_workerId) || _attemptCount < 1)
            throw new AdvancedAnalysisToolException("tool_trace_context_invalid");

        var sequence = await _eventStore.TryAppendToolEventAsync(
            _tenantId,
            _jobId,
            _workerId,
            _attemptCount,
            _maximumToolCalls,
            status,
            request,
            evidence.Select(static item => item.Reference).ToList(),
            degradedRetrievers,
            elapsedMilliseconds,
            errorCode,
            cancellationToken).ConfigureAwait(false);
        if (!sequence.HasValue)
            throw new AdvancedAnalysisToolException("tool_trace_persistence_failed");
    }

    private void ValidateRequest(AdvancedAnalysisSearchRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Query)
            || request.Query.Length > MaximumQueryCharacters)
        {
            throw new AdvancedAnalysisToolException("search_query_invalid");
        }
        if (request.TopK is <= 0 || request.TopK > _maximumSearchTopK)
            throw new AdvancedAnalysisToolException("search_top_k_invalid");
        if (HasOversizedScope(request.Category)
            || HasOversizedScope(request.DocId)
            || HasOversizedScope(request.DocPath))
        {
            throw new AdvancedAnalysisToolException("search_scope_invalid");
        }
        if (request.PageStart is <= 0
            || request.PageEnd is <= 0
            || (request.PageStart.HasValue
                && request.PageEnd.HasValue
                && request.PageEnd < request.PageStart))
        {
            throw new AdvancedAnalysisToolException("search_page_window_invalid");
        }
    }

    private static bool HasOversizedScope(string? value)
        => value?.Length > MaximumScopeCharacters;

    private static int? NormalizePositive(int? value)
        => value is > 0 ? value : null;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePath(string? value)
        => NullIfBlank(value)?.Replace('\\', '/').TrimStart('/');

    private static string CanonicalKey(AdvancedAnalysisResultEvidence evidence)
        => string.Join(
            "|",
            evidence.DocId,
            evidence.RevisionId,
            evidence.ChunkId ?? string.Empty,
            evidence.AnchorId ?? string.Empty,
            evidence.ContentCardId ?? string.Empty,
            evidence.PageStart,
            evidence.PageEnd,
            evidence.SourceHash).ToLowerInvariant();
}

internal sealed class AdvancedAnalysisToolException : Exception
{
    public string ErrorCode { get; }

    public AdvancedAnalysisToolException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }
}
