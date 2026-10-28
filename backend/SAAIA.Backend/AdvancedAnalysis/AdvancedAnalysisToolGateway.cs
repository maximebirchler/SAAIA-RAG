using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    AdvancedAnalysisFindDiagnostic? FindDiagnostic = null,
    RagRetrievalDiagnosticsDto? RetrievalDiagnostic = null,
    AdvancedAnalysisDocumentResolutionDiagnostic? DocumentResolution = null,
    IReadOnlyList<string>? DegradedRetrievers = null,
    int? TableOrdinal = null,
    int? TableRowOffset = null,
    int? TableRowLimit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TableObservationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TableObservationView = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TableObservationBandOrdinal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TableObservationLineOffset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TableObservationLineLimit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IncludeVisualSources = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? VisualSourceOffset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? VisualSourceLimit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeObservationView = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeObservationBlockId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeObservationRawSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeObservationOffset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeObservationLimit = null);

// A resolved path describes a search filter, never an evidence or read capability.
internal sealed record AdvancedAnalysisDocumentResolutionDiagnostic(
    string Status, int CandidateCount, string? ResolvedDocPath);

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
    bool NotExecutedAfterResourceLimit = false,
    RagRetrievalDiagnosticsDto? RetrievalDiagnostic = null,
    AdvancedAnalysisDocumentResolutionDiagnostic? DocumentResolution = null,
    IReadOnlyList<AdvancedAnalysisStructuredTableView>? StructuredTables = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdvancedAnalysisAlternativeTableReadings? AlternativeTableReadings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdvancedAnalysisVisualSources? VisualSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdvancedAnalysisAlternativeNativeReadings? AlternativeNativeReadings = null);

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
    IReadOnlyDictionary<string, string> PromptSourceKeys,
    IReadOnlyList<string>? ObservedCandidateEvidenceIds = null,
    IReadOnlyDictionary<string, AdvancedAnalysisEvidenceReadCheckpoint>? EvidenceReadWindows = null)
{
    public const string CurrentSchemaVersion =
        "saaia.advanced-analysis-research-checkpoint.v2";
}

internal sealed record AdvancedAnalysisEvidenceReadCheckpoint(
    int Offset, int Length,
    IReadOnlyList<AdvancedAnalysisEvidenceReadCheckpoint>? PreviousWindows = null);

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
    private readonly int _maximumStructuredTableCharacters;
    private readonly int _maximumNativeLineCharacters;
    private readonly int _maximumNativeTotalCharacters;
    private readonly bool _visualSourceCitationsEnabled;
    private readonly bool _nativeObservationReadEnabled;
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
        _maximumStructuredTableCharacters=Math.Clamp(options.MaximumEvidencePromptCharacters,1_000,200_000);
        _maximumNativeLineCharacters = Math.Clamp(options.MaximumEvidenceCharactersPerItem, 1_000, 200_000);
        _maximumNativeTotalCharacters = Math.Clamp(options.MaximumEvidenceCharactersTotal, _maximumNativeLineCharacters, 2_000_000);
        _visualSourceCitationsEnabled = options.VisualSourceCitationsEnabled;
        _nativeObservationReadEnabled = options.NativeObservationReadEnabled;
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
        long? chargedElapsedMilliseconds = null;
        var requestForTrace = request;
        using var probe = BackendDiagnostics.Start("advanced.research_tool",
            _services?.GetService<ILoggerFactory>()?.CreateLogger("SAAIA.Backend.AdvancedAnalysis"));
        probe.Set("job_id", _jobId);
        probe.Set("operation", request.Operation);
        long ChargeElapsedOnce()
        {
            if (chargedElapsedMilliseconds is { } previous)
                return previous;
            stopwatch?.Stop();
            var elapsed = stopwatch?.ElapsedMilliseconds ?? 0;
            _elapsedMilliseconds = checked(_elapsedMilliseconds + elapsed);
            chargedElapsedMilliseconds = elapsed;
            return elapsed;
        }
        try
        {
            callNumber = checked(_toolCallCount + 1);
            if (callNumber > _maximumToolCalls)
                throw new AdvancedAnalysisToolException("tool_call_limit_exceeded");
            if (_elapsedMilliseconds >= _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");
            _toolCallCount = callNumber;
            admitted = true;
            stopwatch = Stopwatch.StartNew();

            using var admission = await _bulkhead
                .AcquireAsync(cancellationToken)
                .ConfigureAwait(false);
            if (admission is null)
                throw new AdvancedAnalysisToolException("retrieval_capacity_busy");

            var context = new RetrievalExecutionContext(_tenantId,
                $"advanced:{_jobId:N}:{callNumber}", cancellationToken,
                _services?.GetService<TeiWorkloadGovernor>(),
                _services?.GetService<ILoggerFactory>()?.CreateLogger("SAAIA.Backend.Retrieval"));
            var effectiveDocPath = request.DocPath;
            var documentResolution = new AdvancedAnalysisDocumentResolutionDiagnostic(
                string.IsNullOrWhiteSpace(effectiveDocPath) ? "not_requested" : "explicit_path",
                0, NormalizePath(effectiveDocPath));
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
                documentResolution = DescribeDocumentResolution(
                    request.DocumentHint, candidates, effectiveDocPath);
            }
            requestForTrace = request with
            {
                DocPath = NormalizePath(effectiveDocPath),
                DocumentResolution = documentResolution
            };
            IReadOnlyList<AdvancedAnalysisEvidenceReference> references;
            IReadOnlyList<string> degraded;
            AdvancedAnalysisAlternativeTableReadings? alternativeReadings = null;
            AdvancedAnalysisVisualSources? visualSources = null;
            AdvancedAnalysisAlternativeNativeReadings? nativeReadings = null;
            AdvancedAnalysisResultEvidence? visualSourceIdentity = null;
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
                var observed = _evidence.First(item=>string.Equals(item.Reference.DocId,request.DocId,StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Reference.RevisionId,request.RevisionId,StringComparison.OrdinalIgnoreCase));
                if (request.NativeObservationView is not null)
                {
                    var native = await AdvancedAnalysisNativeObservationReader.ReadAsync(_dataSource, _tenantId,
                        requestForTrace, observed.Reference, _maximumStructuredTableCharacters, cancellationToken,
                        _maximumNativeLineCharacters, _maximumNativeTotalCharacters);
                    references = native.References;
                    nativeReadings = native.Readings;
                }
                else if (request.TableObservationView is not null)
                {
                    var secondary = await AdvancedAnalysisTableObservationReader.ReadAsync(_dataSource, _tenantId,
                        requestForTrace, observed.Reference, _maximumStructuredTableCharacters, cancellationToken);
                    references = secondary.References;
                    alternativeReadings = secondary.Readings;
                }
                else
                {
                    references = await ReadCanonicalPagesAsync(requestForTrace, cancellationToken).ConfigureAwait(false);
                    var inventory = await CanonicalTableObservationRepo.ListAsync(_dataSource, _tenantId,
                        Guid.Parse(request.DocId!), Guid.Parse(request.RevisionId!), observed.Reference.SourceHash,
                        request.PageStart, request.PageEnd, cancellationToken);
                    if (inventory is not null) alternativeReadings = new(inventory, null);
                }
                var source = observed.SourceOverview;
                if (request.IncludeVisualSources)
                    visualSourceIdentity = observed.Reference;
                var outside = source is not null && (request.PageStart > source.LastIndexedPhysicalPage || request.PageEnd < source.FirstIndexedPhysicalPage);
                requestForTrace = requestForTrace with {ReadDiagnostic=new AdvancedAnalysisReadDiagnostic(
                    request.NativeObservationView is not null ? "native_observation_" + (nativeReadings?.View?.Status
                        ?? nativeReadings?.Inventory?.Availability ?? "unavailable")
                        : request.TableObservationView is not null ? "table_observation_" + (alternativeReadings?.View?.Status
                        ?? (alternativeReadings?.Inventory is null ? "inventory_unavailable" : "inventory_returned"))
                        : outside ? "outside_indexed_page_range" : references.Count == 0 ? "no_canonical_chunks_in_window" : "canonical_chunks_returned",
                    source?.FirstIndexedPhysicalPage,source?.LastIndexedPhysicalPage,references.Count)};
                degraded = [];
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.RevisionId))
                {
                    ValidateObservedReadScope(requestForTrace);
                    await ValidateCanonicalSearchSourceAsync(requestForTrace, cancellationToken)
                        .ConfigureAwait(false);
                }
                var response = await RetrievalEngine.SearchAsync(
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
                        IncludeDiagnostics: true,
                        PageStart: request.PageStart,
                        PageEnd: request.PageEnd,
                        ResearchMode: "advanced_analysis",
                        IncludeResearchSurfaces: true,
                        SourceBackedCanonical: true))
                    .ConfigureAwait(false);
                references = BuildEvidenceReferences(response.Matches);
                degraded = response.DegradedRetrievers ?? [];
                requestForTrace = requestForTrace with
                {
                    RetrievalDiagnostic = response.Diagnostics,
                    DegradedRetrievers = degraded
                };
            }
            if (_elapsedMilliseconds + stopwatch.ElapsedMilliseconds > _maximumElapsedMilliseconds)
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
            if (request.Operation == "search_corpus" && !string.IsNullOrWhiteSpace(request.RevisionId)
                && revalidated.Evidence.Any(item =>
                    !string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AdvancedAnalysisToolException("canonical_search_source_identity_changed");
            }
            if (_elapsedMilliseconds + stopwatch.ElapsedMilliseconds > _maximumElapsedMilliseconds)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");

            var resolvedEvidence = request.Operation switch
            {
                "find_source_text" => revalidated.Evidence
                    .Select(item => PromoteLeadingCanonicalFindTitle(
                        request.Query,
                        item))
                    .ToArray(),
                "read_source" => revalidated.Evidence
                    .Select(item => item.SourceObservation is null && item.NativeObservation is null
                        ? PromoteLeadingCanonicalReadTitle(item) : item)
                    .ToArray(),
                _ => revalidated.Evidence
            };
            if (alternativeReadings?.View is { Lines: not null } secondaryView)
            {
                var lines = secondaryView.Lines.ToDictionary(line => line.AnchorId, StringComparer.Ordinal);
                resolvedEvidence = resolvedEvidence.Select(item => item.SourceObservation is { } secondary
                        && item.Reference.AnchorId is { } anchor && lines.TryGetValue(anchor, out var line)
                    ? item with { SourceObservation = secondary with
                    {
                        View = secondaryView.View, ViewOrdinal = line.ViewOrdinal, BandOrdinal = secondaryView.BandOrdinal,
                        ViewLineCount = secondaryView.ViewLineCount!.Value, LineOffset = secondaryView.LineOffset,
                        LineLimit = secondaryView.LineLimit, NextLineOffset = secondaryView.NextLineOffset
                    }} : item).ToArray();
            }
            if (visualSourceIdentity is not null)
            {
                // Use resolver-proved anchor and evidence IDs. The raw SQL
                // page references have not acquired those bindings yet.
                var visualReferences = resolvedEvidence.Concat(_evidence)
                    .Select(item => new AdvancedAnalysisEvidenceReference
                    {
                        EvidenceId = item.Reference.EvidenceId,
                        DocId = item.Reference.DocId,
                        RevisionId = item.Reference.RevisionId,
                        SourceHash = item.Reference.SourceHash,
                        PageStart = item.Reference.PageStart,
                        PageEnd = item.Reference.PageEnd,
                        AnchorId = item.Reference.AnchorId
                    }).ToArray();
                visualSources = await AdvancedAnalysisVisualSourceReader.ReadAsync(
                    _dataSource, _tenantId, requestForTrace, visualSourceIdentity,
                    visualReferences, _maximumStructuredTableCharacters, cancellationToken);
            }

            if (nativeReadings is not null)
            {
                nativeReadings = AdvancedAnalysisNativeInventoryContext.Preserve(
                    nativeReadings, _evidence, _tenantId, _maximumStructuredTableCharacters);
                resolvedEvidence = resolvedEvidence.Select(item => AttachNativeRead(item, nativeReadings)).ToArray();
                AdvancedAnalysisNativeInventoryContext.RecordDiagnostics(probe, nativeReadings, resolvedEvidence);
                for (var index = 0; index < _evidence.Count; index++)
                {
                    var updated = AttachNativeRead(_evidence[index], nativeReadings);
                    _evidence[index] = updated;
                    _evidenceByCanonicalKey[CanonicalKey(updated.Reference)] = updated;
                }
            }

            var observationEvidence = new List<AdvancedAnalysisResolvedEvidence>(
                resolvedEvidence.Count);
            if (visualSources is not null)
            {
                AdvancedAnalysisResolvedEvidence Attach(AdvancedAnalysisResolvedEvidence item)
                    => item.SourceOverview is null ? item : item with
                    {
                        SourceOverview = item.SourceOverview with { VisualSources = visualSources }
                    };

                resolvedEvidence = resolvedEvidence.Select(Attach).ToArray();
                for (var index = 0; index < _evidence.Count; index++)
                {
                    var item = _evidence[index];
                    if (!string.Equals(item.Reference.DocId, visualSources.DocId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.Reference.RevisionId, visualSources.RevisionId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.Reference.SourceHash, visualSources.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        continue;

                    item = Attach(item);
                    _evidence[index] = item;
                    _evidenceByCanonicalKey[CanonicalKey(item.Reference)] = item;
                }
            }
            var newEvidence = resolvedEvidence
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
            foreach (var item in resolvedEvidence)
            {
                var key = CanonicalKey(item.Reference);
                var canonical = _evidenceByCanonicalKey[key];
                if (string.IsNullOrWhiteSpace(canonical.ExactTitle)
                    && !string.IsNullOrWhiteSpace(item.ExactTitle))
                {
                    // A canonical find or page read can revisit a chunk that broad
                    // retrieval already stored. Preserve the newly proved leading
                    // title on the canonical in-memory item so the staged Judge
                    // receives the body as a named candidate on its next pass.
                    canonical = item;
                    _evidenceByCanonicalKey[key] = canonical;
                    for (var index = 0; index < _evidence.Count; index++)
                    {
                        if (!string.Equals(
                                CanonicalKey(_evidence[index].Reference),
                                key,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        _evidence[index] = canonical;
                        break;
                    }
                }
                if (request.Operation=="read_source" && canonical.TableFragment is not null)
                {
                    canonical=canonical with { TableReadWindow=request };
                    _evidenceByCanonicalKey[key]=canonical;
                    var storedIndex=_evidence.FindIndex(item=>CanonicalKey(item.Reference)==key);
                    if (storedIndex>=0) _evidence[storedIndex]=canonical;
                }
                if (item.SourceObservation is not null)
                {
                    canonical = canonical with { SourceObservation = item.SourceObservation };
                    _evidenceByCanonicalKey[key] = canonical;
                    var storedIndex = _evidence.FindIndex(existing => CanonicalKey(existing.Reference) == key);
                    if (storedIndex >= 0) _evidence[storedIndex] = canonical;
                }
                if (item.NativeObservation is not null)
                {
                    canonical = canonical with { NativeObservation = item.NativeObservation, SourceOverview = item.SourceOverview };
                    _evidenceByCanonicalKey[key] = canonical;
                    var storedIndex = _evidence.FindIndex(existing => CanonicalKey(existing.Reference) == key);
                    if (storedIndex >= 0) _evidence[storedIndex] = canonical;
                }
                observationEvidence.Add(canonical);
            }

            // Freeze the admitted work duration once for memory, the returned
            // observation and durable replay. Trace persistence is measured by
            // its own operation and is not recursively charged into that row.
            var structuredTables=request.Operation=="read_source"
                ? AdvancedAnalysisStructuredTableAssembler.Build(observationEvidence,request,
                    _maximumStructuredTableCharacters) : [];
            var workElapsedMilliseconds = ChargeElapsedOnce();
            probe.Set("document_resolution", requestForTrace.DocumentResolution?.Status);
            probe.Set("document_candidates", requestForTrace.DocumentResolution?.CandidateCount);
            probe.Set("evidence_count", observationEvidence.Count);
            probe.Set("structured_table_count", structuredTables.Count);
            probe.Set("structured_table_partial_count", structuredTables.Count(table=>!table.Complete));
            probe.Set("degraded_retriever_count", degraded.Count);
            probe.Set("tool_call_number", callNumber);
            await PersistEventAsync(
                "succeeded",
                requestForTrace,
                observationEvidence,
                degraded,
                workElapsedMilliseconds,
                errorCode: null,
                cancellationToken).ConfigureAwait(false);
            probe.Complete();
            return new AdvancedAnalysisSearchObservation(
                request.Query.Trim(),
                observationEvidence,
                degraded,
                workElapsedMilliseconds,
                callNumber,
                requestForTrace.ReadDiagnostic,
                requestForTrace.FindDiagnostic,
                RetrievalDiagnostic: requestForTrace.RetrievalDiagnostic,
                DocumentResolution: requestForTrace.DocumentResolution,
                StructuredTables: structuredTables,
                AlternativeTableReadings: alternativeReadings, VisualSources: visualSources, AlternativeNativeReadings: nativeReadings);
        }
        catch (Exception ex)
        {
            probe.Fail(ex, ex is AdvancedAnalysisToolException diagnosticError
                ? diagnosticError.ErrorCode : "retrieval_tool_failed");
            if (admitted)
            {
                var workElapsedMilliseconds = ChargeElapsedOnce();
                var errorCode = ex is AdvancedAnalysisToolException toolException
                    ? toolException.ErrorCode
                    : "retrieval_tool_failed";
                await PersistEventAsync(
                    "failed",
                    requestForTrace,
                    [],
                    [],
                    workElapsedMilliseconds,
                    errorCode,
                    cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (admitted)
                ChargeElapsedOnce();
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

    private async Task ValidateCanonicalSearchSourceAsync(
        AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
    {
        var observed = _evidence.First(item =>
            string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var identity = await connection.QuerySingleOrDefaultAsync<CanonicalReadSourceIdentity>(
            new CommandDefinition("""
                SELECT encode(dr.source_hash, 'hex') AS "SourceHash", d.doc_path AS "DocPath"
                FROM document_revisions dr
                JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
                WHERE dr.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
                  AND d.status='indexed' AND d.indexed_version>0
                  AND dr.indexed_version=d.indexed_version;
                """, new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!),
                    revision_id = Guid.Parse(request.RevisionId!) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (identity is null || string.IsNullOrWhiteSpace(identity.SourceHash)
            || !string.Equals(identity.SourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizePath(identity.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal))
            throw new AdvancedAnalysisToolException("canonical_search_source_identity_changed");
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
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var observed = _evidence.First(item =>
            string.Equals(item.Reference.DocId, request.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reference.RevisionId, request.RevisionId, StringComparison.OrdinalIgnoreCase));
        // An empty page window is still an observation about this exact source.
        // Revalidate the source before querying chunks so an absent result cannot
        // conceal a removed revision, changed hash or relocated document.
        var identity = await connection.QuerySingleOrDefaultAsync<CanonicalReadSourceIdentity>(
            new CommandDefinition("""
                SELECT encode(dr.source_hash, 'hex') AS "SourceHash", d.doc_path AS "DocPath"
                FROM document_revisions dr
                JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
                WHERE dr.tenant_id=@tenant AND d.doc_id=@doc_id AND dr.revision_id=@revision_id
                  AND d.status='indexed' AND d.indexed_version>0;
                """, new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!),
                    revision_id = Guid.Parse(request.RevisionId!) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (identity is null || string.IsNullOrWhiteSpace(identity.SourceHash)
            || !string.Equals(identity.SourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizePath(identity.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal))
            throw new AdvancedAnalysisToolException("canonical_read_source_identity_changed");
        var references = (await connection.QueryAsync<AdvancedAnalysisEvidenceReference>(new CommandDefinition(sql,
            new { tenant = _tenantId, doc_id = Guid.Parse(request.DocId!), revision_id = Guid.Parse(request.RevisionId!),
                page_start = request.PageStart, page_end = request.PageEnd, row_limit = request.TopK + 1 },
            transaction, cancellationToken: cancellationToken))).ToArray();
        if (references.Any(reference =>
                !string.Equals(NormalizePath(reference.DocPath), NormalizePath(request.DocPath), StringComparison.Ordinal)
                || !string.Equals(reference.SourceHash, observed.Reference.SourceHash, StringComparison.OrdinalIgnoreCase)))
            throw new AdvancedAnalysisToolException("canonical_read_source_identity_changed");
        if (references.Length > request.TopK)
            throw new AdvancedAnalysisToolException(
                "canonical_read_window_result_limit_exceeded",
                new("canonical_read_window_result_limit_exceeded", null, null, null,
                    request.TopK, references.Length, StopsResearch: false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return references;
    }

    private sealed record CanonicalReadSourceIdentity(string SourceHash, string DocPath);

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

    internal static AdvancedAnalysisDocumentResolutionDiagnostic DescribeDocumentResolution(
        string documentHint, IReadOnlyList<string> candidates, string? resolvedDocPath)
        => new(
            resolvedDocPath is not null ? "resolved"
                : CompactDocumentIdentity(documentHint).Length < 6 ? "hint_too_short"
                : candidates.Count > 1 ? "ambiguous" : "no_match",
            candidates.Count,
            NormalizePath(resolvedDocPath));

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

        using var probe = BackendDiagnostics.Start("advanced.tool_trace_persistence",
            _services?.GetService<ILoggerFactory>()?.CreateLogger("SAAIA.Backend.AdvancedAnalysis"));
        probe.Set("job_id", _jobId);
        probe.Set("attempt", _attemptCount);
        probe.Set("work_elapsed_ms", elapsedMilliseconds);
        probe.Set("evidence_count", evidence.Count);
        try
        {
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
            probe.Set("event_sequence", sequence.Value);
            probe.Complete();
        }
        catch (Exception exception)
        {
            probe.Fail(exception);
            throw;
        }
    }

    private void ValidateRequest(AdvancedAnalysisSearchRequest request)
    {
        if (request is not null)
            AdvancedAnalysisVisualSourceReader.ValidateRequest(request, _visualSourceCitationsEnabled);
        if (request is not null) AdvancedAnalysisTableObservationReader.ValidateRequest(request);
        if (request is not null && AdvancedAnalysisNativeObservationReader.HasRequest(request))
        {
            if (!_nativeObservationReadEnabled)
                throw new AdvancedAnalysisToolException("native_observation_read_disabled");
            AdvancedAnalysisNativeObservationReader.ValidateRequest(request);
        }
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
        if ((request.TableOrdinal is not null || request.TableRowOffset is not null || request.TableRowLimit is not null)
            && (request.Operation!="read_source" || request.TableOrdinal is null || request.TableOrdinal<0
                || request.TableRowOffset is <0 or >=AdvancedAnalysisTableFragment.MaximumRows
                || request.TableRowLimit is <1 or >AdvancedAnalysisStructuredTableAssembler.MaximumRowLimit))
            throw new AdvancedAnalysisToolException("canonical_table_read_window_invalid");
        if (request.Operation == "search_corpus" && !string.IsNullOrWhiteSpace(request.RevisionId)
            && (!Guid.TryParse(request.RevisionId, out _) || !Guid.TryParse(request.DocId, out _)
                || string.IsNullOrWhiteSpace(request.DocPath)))
            throw new AdvancedAnalysisToolException("canonical_search_scope_invalid");
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

    internal static string? ResolveLeadingCanonicalFindTitle(
        string query,
        string content)
    {
        var title = CollapseCanonicalWhitespace(query);
        if (title.Length is < 2 or > 240)
            return null;
        var normalizedContent = CollapseCanonicalWhitespace(content);
        if (!normalizedContent.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            return null;
        if (normalizedContent.Length > title.Length
            && char.IsLetterOrDigit(normalizedContent[title.Length]))
        {
            return null;
        }
        // The literal lookup is case-insensitive. Recover the source spelling
        // rather than treating the caller's casing as a documentary title.
        return normalizedContent[..title.Length];
    }

    private static AdvancedAnalysisResolvedEvidence PromoteLeadingCanonicalFindTitle(
        string query,
        AdvancedAnalysisResolvedEvidence evidence)
    {
        if (evidence.SourceStructure?.HasUnassignedPhysicalContext == true)
            return evidence;
        if (!string.IsNullOrWhiteSpace(evidence.ExactTitle))
            return evidence;
        var title = ResolveLeadingCanonicalFindTitle(query, evidence.Content);
        return title is null ? evidence : evidence with { ExactTitle = title };
    }

    internal static string? ResolveLeadingCanonicalReadTitle(string content)
    {
        var lines = (content ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return null;
        var title = CollapseCanonicalWhitespace(lines[0]);
        if (title.Length is < 2 or > 240
            || !title.Any(char.IsLetterOrDigit)
            || !lines.Skip(1).Any(line =>
                CollapseCanonicalWhitespace(line).Length > 0))
        {
            return null;
        }
        return title;
    }

    private static AdvancedAnalysisResolvedEvidence PromoteLeadingCanonicalReadTitle(
        AdvancedAnalysisResolvedEvidence evidence)
    {
        if (evidence.SourceObservation is not null || evidence.NativeObservation is not null) return evidence;
        if (evidence.SourceStructure?.HasUnassignedPhysicalContext == true)
            return evidence;
        if (!string.IsNullOrWhiteSpace(evidence.ExactTitle))
            return evidence;
        var signal = RetrievalContentClassifier.AnalyzeEvidenceContent(
            evidence.Content,
            evidence.ExactTitle);
        if (string.Equals(
                signal.ContentRole,
                RetrievalContentClassifier.NavigationRole,
                StringComparison.Ordinal))
        {
            return evidence;
        }
        var title = ResolveLeadingCanonicalReadTitle(evidence.Content);
        // This is an observed heading, not a semantic object identity. Candidate
        // observation keeps it awaiting review until the model assesses its body.
        return title is null ? evidence : evidence with { ExactTitle = title };
    }

    private static string CollapseCanonicalWhitespace(string? value)
        => string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static string CanonicalKey(AdvancedAnalysisResultEvidence evidence)
        => evidence.NativeObservation is { } native
            ? string.Join("|", "native", evidence.DocId, evidence.RevisionId, evidence.SourceHash, native.LineId).ToLowerInvariant()
            : string.Join(
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
