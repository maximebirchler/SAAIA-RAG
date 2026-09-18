using System.Diagnostics;
using System.Text.Json;
using Dapper;
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
    int? MaxPerPage = null,
    string? DocumentHint = null,
    string Operation = "search_corpus",
    string? RevisionId = null,
    AdvancedAnalysisReadDiagnostic? ReadDiagnostic = null,
    int Offset = 0,
    AdvancedAnalysisFindDiagnostic? FindDiagnostic = null);

internal sealed record AdvancedAnalysisFindDiagnostic(
    string Status, int ReturnedChunkCount, int? NextOffset);

internal sealed record AdvancedAnalysisReadDiagnostic(
    string Status,
    int? FirstIndexedPhysicalPage,
    int? LastIndexedPhysicalPage,
    int ReturnedChunkCount);

internal sealed record AdvancedAnalysisSearchObservation(
    string Query,
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
    IReadOnlyList<string> DegradedRetrievers,
    long ElapsedMilliseconds,
    int ToolCallNumber,
    AdvancedAnalysisReadDiagnostic? ReadDiagnostic = null,
    AdvancedAnalysisFindDiagnostic? FindDiagnostic = null,
    AdvancedAnalysisResearchResourceLimit? ResourceLimit = null,
    bool NotExecutedAfterResourceLimit = false);

internal sealed record AdvancedAnalysisResearchResourceLimit(string ReasonCode,
    int? MaximumEvidenceItems, int? ConsumedEvidenceItems, int? RequestedNewEvidenceItems,
    int? RequestedTopK = null, int? ObservedAtLeastChunkCount = null, bool StopsResearch = true);

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

internal sealed record AdvancedAnalysisToolBudget(
    int MaximumCalls, int ConsumedCalls,
    long MaximumElapsedMilliseconds, long ConsumedElapsedMilliseconds,
    int? MaximumEvidenceItems = null, int? ConsumedEvidenceItems = null)
{
    public int RemainingCalls => Math.Max(0, MaximumCalls - ConsumedCalls);
    public long RemainingElapsedMilliseconds => Math.Max(
        0, MaximumElapsedMilliseconds - ConsumedElapsedMilliseconds);
    public int? RemainingEvidenceItems => MaximumEvidenceItems is { } maximum
        && ConsumedEvidenceItems is { } consumed ? Math.Max(0, maximum - consumed) : null;
}

internal sealed record AdvancedAnalysisCandidateCheckpoint(
    string Key,
    string ExactTitle,
    string SourceKey,
    IReadOnlyList<string> TargetRoles,
    IReadOnlyList<string> SelectedRoles,
    string Status,
    string Note,
    IReadOnlyList<string> LocatorEvidenceIds,
    IReadOnlyList<string> BodyEvidenceIds);

internal sealed record AdvancedAnalysisResearchCheckpoint(
    string SchemaVersion,
    IReadOnlyList<AdvancedAnalysisCandidateCheckpoint> Candidates,
    IReadOnlyDictionary<string, string> PromptSourceKeys)
{
    public const string CurrentSchemaVersion =
        "saaia.advanced-analysis-research-checkpoint.v1";
}

internal interface IAdvancedAnalysisToolGateway
{
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence { get; }
    AdvancedAnalysisToolBudget? Budget => null;

    Task<IReadOnlyList<string>> ListCategoriesAsync(
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);

    Task<AdvancedAnalysisResearchCheckpoint?> LoadResearchCheckpointAsync(
        CancellationToken cancellationToken)
        => Task.FromResult<AdvancedAnalysisResearchCheckpoint?>(null);

    Task SaveResearchCheckpointAsync(
        AdvancedAnalysisResearchCheckpoint checkpoint,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

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

    IAdvancedAnalysisToolGateway Create(
        Guid jobId, Guid tenantId, string workerId, int attemptCount,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence,
        IReadOnlyList<AdvancedAnalysisToolEventSummary> previousToolEvents)
        => Create(jobId, tenantId, workerId, attemptCount, initialEvidence);
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
        => Create(jobId, tenantId, workerId, attemptCount, initialEvidence, []);

    public IAdvancedAnalysisToolGateway Create(
        Guid jobId, Guid tenantId, string workerId, int attemptCount,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence,
        IReadOnlyList<AdvancedAnalysisToolEventSummary> previousToolEvents)
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
            attemptCount,
            previousToolEvents);
}

internal sealed partial class AdvancedAnalysisToolGateway : IAdvancedAnalysisToolGateway
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
        int attemptCount = 0,
        IReadOnlyList<AdvancedAnalysisToolEventSummary>? previousToolEvents = null)
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
        var history = previousToolEvents ?? [];
        _toolCallCount = history.Count == 0 ? 0 : history.Max(e => e.EventSequence);
        _elapsedMilliseconds = history.Sum(e => e.ElapsedMilliseconds);
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

    public AdvancedAnalysisToolBudget Budget => new(
        _maximumToolCalls, _toolCallCount,
        _maximumElapsedMilliseconds, _elapsedMilliseconds, _maximumEvidenceItems, _evidence.Count);

    public async Task<AdvancedAnalysisResearchCheckpoint?> LoadResearchCheckpointAsync(
        CancellationToken cancellationToken)
    {
        if (_eventStore is null || string.IsNullOrWhiteSpace(_workerId))
            return null;
        try
        {
            return await _eventStore.LoadResearchCheckpointAsync(
                    _tenantId,
                    _jobId,
                    _workerId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or JsonException
                                          or NotSupportedException)
        {
            throw new AdvancedAnalysisToolException(
                "advanced_research_checkpoint_invalid");
        }
    }

    public async Task SaveResearchCheckpointAsync(
        AdvancedAnalysisResearchCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (_eventStore is null || string.IsNullOrWhiteSpace(_workerId))
            return;
        if (!await _eventStore.TrySaveResearchCheckpointAsync(
                _tenantId,
                _jobId,
                _workerId,
                checkpoint,
                cancellationToken).ConfigureAwait(false))
        {
            throw new AdvancedAnalysisToolException(
                "advanced_research_checkpoint_store_failed");
        }
    }

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT BTRIM(category)
            FROM documents
            WHERE tenant_id = @tenant
              AND status = 'indexed'
              AND indexed_version > 0
              AND NULLIF(BTRIM(category), '') IS NOT NULL
            ORDER BY BTRIM(category)
            LIMIT 256;
            """;
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var categories = await connection.QueryAsync<string>(
            new CommandDefinition(
                sql,
                new { tenant = _tenantId },
                cancellationToken: cancellationToken));
        return categories
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Select(static category => category.Trim())
            .Where(static category => category.Length <= MaximumScopeCharacters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AdvancedAnalysisSearchObservation> SearchAsync(
        AdvancedAnalysisSearchRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var callNumber = 0;
        var admitted = false;
        Stopwatch? stopwatch = null;
        var requestForTrace = request;
        try
        {
            callNumber = checked(_toolCallCount + 1);
            if (callNumber > _maximumToolCalls)
                throw new AdvancedAnalysisToolException("tool_call_limit_exceeded");
            if (_elapsedMilliseconds >= _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");
            _toolCallCount = callNumber;
            admitted = true;

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
            var effectiveDocPath = request.DocPath;
            if (string.IsNullOrWhiteSpace(effectiveDocPath)
                && !string.IsNullOrWhiteSpace(request.DocumentHint))
            {
                var candidates = await ResolveDocumentScopeCandidatesAsync(
                        _dataSource,
                        _tenantId,
                        request.DocumentHint,
                        NullIfBlank(request.Category),
                        cancellationToken)
                    .ConfigureAwait(false);
                effectiveDocPath = SelectStrongDocumentScope(
                    request.DocumentHint,
                    candidates);
            }
            requestForTrace = request with
            {
                DocPath = NormalizePath(effectiveDocPath)
            };
            stopwatch = Stopwatch.StartNew();
            IReadOnlyList<AdvancedAnalysisEvidenceReference> references;
            IReadOnlyList<string> degraded;
            if (request.Operation == "find_source_text")
            {
                ValidateObservedReadScope(requestForTrace);
                var found = await FindCanonicalTextAsync(requestForTrace, cancellationToken).ConfigureAwait(false);
                references = found.References;
                requestForTrace = requestForTrace with { FindDiagnostic = found.Diagnostic };
                degraded = [];
            }
            else if (request.Operation == "read_source")
            {
                ValidateObservedReadScope(requestForTrace);
                references = await ReadCanonicalPagesAsync(requestForTrace, cancellationToken)
                    .ConfigureAwait(false);
                var source = _evidence.First(item=>string.Equals(item.Reference.DocId,request.DocId,StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Reference.RevisionId,request.RevisionId,StringComparison.OrdinalIgnoreCase)).SourceOverview;
                var outside = source is not null && (request.PageStart > source.LastIndexedPhysicalPage || request.PageEnd < source.FirstIndexedPhysicalPage);
                requestForTrace = requestForTrace with {ReadDiagnostic=new AdvancedAnalysisReadDiagnostic(
                    outside ? "outside_indexed_page_range" : references.Count == 0 ? "no_canonical_chunks_in_window" : "canonical_chunks_returned",
                    source?.FirstIndexedPhysicalPage,source?.LastIndexedPhysicalPage,references.Count)};
                degraded = [];
            }
            else
            {
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
                        DocPath: NormalizePath(effectiveDocPath),
                        IncludeContextualSnippet: false,
                        CategoryPath: null,
                        CategoryRef: null,
                        IncludeDiagnostics: false,
                        PageStart: request.PageStart,
                        PageEnd: request.PageEnd,
                        ResearchMode: "advanced_analysis",
                        IncludeResearchSurfaces: true,
                        SourceBackedCanonical: true))
                    .ConfigureAwait(false);
                references = BuildEvidenceReferences(response.Matches);
                degraded = response.DegradedRetrievers ?? [];
            }
            stopwatch.Stop();
            _elapsedMilliseconds = checked(
                _elapsedMilliseconds + stopwatch.ElapsedMilliseconds);
            if (_elapsedMilliseconds > _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");

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
                    "accumulated_evidence_limit_exceeded",
                    new("accumulated_evidence_limit_exceeded", _maximumEvidenceItems,
                        _evidence.Count, newEvidence.Count));
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

            await PersistEventAsync(
                "succeeded",
                requestForTrace,
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
                callNumber,
                requestForTrace.ReadDiagnostic,
                requestForTrace.FindDiagnostic);
        }
        catch (Exception ex)
        {
            stopwatch?.Stop();
            if (admitted)
            {
                var errorCode = ex is AdvancedAnalysisToolException toolException
                    ? toolException.ErrorCode
                    : "retrieval_tool_failed";
                await PersistEventAsync(
                    "failed",
                    requestForTrace,
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

    private void ValidateObservedReadScope(AdvancedAnalysisSearchRequest request)
    {
        if (!_evidence.Any(item =>
                string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizePath(item.Reference.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal)))
            throw new AdvancedAnalysisToolException("canonical_read_source_not_observed");
    }

    private async Task<IReadOnlyList<AdvancedAnalysisEvidenceReference>> ReadCanonicalPagesAsync(
        AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.doc_id::text AS "DocId", dr.revision_id::text AS "RevisionId",
                   d.doc_name AS "FileName", d.doc_path AS "DocPath",
                   encode(dr.source_hash, 'hex') AS "SourceHash",
                   rc.page_start AS "PageStart", rc.page_end AS "PageEnd",
                   rc.retrieval_chunk_id::text AS "ChunkId"
            FROM retrieval_chunks rc
            JOIN document_revisions dr ON dr.tenant_id=rc.tenant_id AND dr.revision_id=rc.revision_id
            JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
            WHERE rc.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
              AND d.status='indexed' AND d.indexed_version>0
              AND rc.page_start<=@page_end AND rc.page_end>=@page_start
            ORDER BY rc.page_start, rc.page_end, rc.chunk_index, rc.retrieval_chunk_id
            LIMIT @row_limit;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var references = (await connection.QueryAsync<AdvancedAnalysisEvidenceReference>(new CommandDefinition(sql,
            new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!), revision_id = Guid.Parse(request.RevisionId!),
                page_start = request.PageStart, page_end = request.PageEnd, row_limit = request.TopK + 1 },
            cancellationToken: cancellationToken))).ToArray();
        var observed = _evidence.First(item =>
            string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase));
        if (references.Any(reference =>
                !string.Equals(NormalizePath(reference.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal)
                || !string.Equals(reference.SourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)))
            throw new AdvancedAnalysisToolException("canonical_read_source_identity_changed");
        if (references.Length > request.TopK)
            throw new AdvancedAnalysisToolException(
                "canonical_read_window_result_limit_exceeded",
                new("canonical_read_window_result_limit_exceeded", null, null, null,
                    request.TopK, references.Length, StopsResearch: false));
        return references;
    }

    internal static IReadOnlyList<AdvancedAnalysisEvidenceReference>
        BuildEvidenceReferences(IReadOnlyList<RagMatch> matches)
    {
        var references = new List<AdvancedAnalysisEvidenceReference>(
            matches.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            foreach (var card in match.MatchedContentCards?.Take(4)
                         ?? Enumerable.Empty<RagMatchedContentCard>())
            {
                if (string.Equals(
                        card.Kind,
                        "chunk_section_title",
                        StringComparison.Ordinal)
                    || !Guid.TryParse(card.ContentCardId, out _)
                    || !seen.Add("content-card:" + card.ContentCardId))
                {
                    continue;
                }
                references.Add(new AdvancedAnalysisEvidenceReference
                {
                    DocId = NullIfBlank(match.DocId),
                    FileName = NullIfBlank(match.DocName),
                    DocPath = NullIfBlank(match.DocPath),
                    ContentCardId = card.ContentCardId
                });
            }

            if (!Guid.TryParse(match.ChunkId, out _)
                || !seen.Add("chunk:" + match.ChunkId))
            {
                continue;
            }
            references.Add(new AdvancedAnalysisEvidenceReference
            {
                DocId = NullIfBlank(match.DocId),
                FileName = NullIfBlank(match.DocName),
                DocPath = NullIfBlank(match.DocPath),
                PageStart = match.PageStart.GetValueOrDefault(),
                PageEnd = match.PageEnd.GetValueOrDefault(),
                ChunkId = match.ChunkId
            });
        }
        return references;
    }

    internal static string? SelectStrongDocumentScope(
        string documentHint,
        IReadOnlyList<string> candidates)
    {
        var hint = CompactDocumentIdentity(documentHint);
        if (hint.Length < 6)
            return null;
        var strong = candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Where(path =>
            {
                var fileName = Path.GetFileName(path.Replace('\\', '/'));
                var candidate = CompactDocumentIdentity(fileName);
                return candidate.Contains(hint, StringComparison.Ordinal)
                       || hint.Contains(candidate, StringComparison.Ordinal);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return strong.Length == 1 ? strong[0] : null;
    }

    internal static async Task<IReadOnlyList<string>>
        ResolveDocumentScopeCandidatesAsync(
            NpgsqlDataSource dataSource,
            Guid tenantId,
            string documentHint,
            string? category,
            CancellationToken cancellationToken)
    {
        var compactHint = CompactDocumentIdentity(documentHint);
        if (compactHint.Length < 6)
            return [];
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            WITH candidates AS (
              SELECT
                doc_path,
                doc_name,
                updated_at,
                lower(regexp_replace(doc_name, '[^[:alnum:]]+', '', 'g'))
                  AS compact_name
              FROM documents
              WHERE tenant_id=@tenant
                AND status='indexed'
                AND indexed_version > 0
                AND (@category IS NULL OR lower(category)=@category)
            )
            SELECT doc_path
            FROM candidates
            WHERE compact_name LIKE ('%' || @compact_hint || '%')
               OR @compact_hint LIKE ('%' || compact_name || '%')
            ORDER BY
              CASE
                WHEN compact_name=@compact_hint THEN 0
                WHEN compact_name LIKE (@compact_hint || '%') THEN 1
                ELSE 2
              END,
              char_length(doc_name),
              updated_at DESC
            LIMIT 3;
            """;
        var paths = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                category = string.IsNullOrWhiteSpace(category)
                    ? null
                    : category.Trim().ToLowerInvariant(),
                compact_hint = compactHint
            },
            cancellationToken: cancellationToken));
        return paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CompactDocumentIdentity(string value)
        => System.Text.RegularExpressions.Regex.Replace(
                value ?? string.Empty,
                @"[^\p{L}\p{N}]",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .ToLowerInvariant();

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
        if (request.Operation is not "search_corpus" and not "read_source" and not "find_source_text")
            throw new AdvancedAnalysisToolException("research_operation_invalid");
        if (request.Operation == "find_source_text"
            && (!Guid.TryParse(request.DocId, out _) || !Guid.TryParse(request.RevisionId, out _)
                || string.IsNullOrWhiteSpace(request.DocPath)
                || !string.IsNullOrWhiteSpace(request.Category) || !string.IsNullOrWhiteSpace(request.DocumentHint)
                || request.PageStart is not null || request.PageEnd is not null
                || request.Query.Trim().Length is < 2 or > 512 || request.Offset is < 0 or > 10_000))
            throw new AdvancedAnalysisToolException("canonical_find_scope_or_literal_invalid");
        if (request.Operation != "find_source_text" && request.Offset != 0)
            throw new AdvancedAnalysisToolException("research_offset_invalid");
        if (request.Operation == "read_source"
            && (!Guid.TryParse(request.DocId, out _) || !Guid.TryParse(request.RevisionId, out _)
                || string.IsNullOrWhiteSpace(request.DocPath)
                || !string.IsNullOrWhiteSpace(request.Category) || !string.IsNullOrWhiteSpace(request.DocumentHint)
                || !request.PageStart.HasValue || !request.PageEnd.HasValue
                || (long)request.PageEnd.Value - request.PageStart.Value > 3))
            throw new AdvancedAnalysisToolException("canonical_read_scope_or_window_invalid");
        if (HasOversizedScope(request.Category)
            || HasOversizedScope(request.DocId)
            || HasOversizedScope(request.DocPath)
            || HasOversizedScope(request.DocumentHint))
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
    public AdvancedAnalysisResearchResourceLimit? ResourceLimit { get; }

    public AdvancedAnalysisToolException(string errorCode, AdvancedAnalysisResearchResourceLimit? resourceLimit = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        ResourceLimit = resourceLimit;
    }
}
