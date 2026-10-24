using System.Diagnostics;
using Npgsql;

namespace SAAIA.Backend.Retrieval;

/// <summary>Immutable per-query decisions, grouped by responsibility. No mutable runtime flags live in this plan.</summary>
internal sealed record InteractiveRetrievalPlan(RagSearchRequestDto Request, InteractiveScope Scope,
    InteractiveQuery Query, InteractiveSelectionPolicy Selection, InteractiveRoutes Routes,
    InteractiveBackfillTerms Backfills, int SparseCommandTimeoutSeconds, int SqlHeavyCandidateLimit)
{
    internal const string CatalogOverviewFallbackQuery = "Which documents are available in this category?";
}

internal sealed record InteractiveScope(
    string? RequestedCategory,
    string? CategoryPath,
    string? Category,
    string? CategoryQueryHint,
    bool HasDocScope,
    bool HasExplicitFileDocumentHint,
    int? RequestedPageStart,
    int? RequestedPageEnd,
    bool HasRequestedPageWindow,
    bool HasCategoryFilter);

internal sealed record InteractiveQuery(
    string RetrievalQuery,
    string LexicalRetrievalSurface,
    bool UsesDelimitedLexicalSurface,
    string QueryNorm,
    string RetrievalQueryNorm,
    string SelectionQuery,
    string RankingQuery);

internal sealed record InteractiveSelectionPolicy(
    int TopK,
    int RequestedTopK,
    double MinScore,
    double SelectionMinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    bool PreferItemInventory,
    bool PreferDocumentDiversity);

internal sealed record InteractiveRoutes(
    bool UseBareVsComparativeOperandRoute,
    bool UseChoiceBetweenLexicalRoute,
    bool IncludeResearchSurfaces,
    string Mode,
    bool SkipPreciseTitleLookupForDocumentOverview,
    bool SkipChunkRetrieversForDocumentOverview,
    bool HasQuotedLookupPhrases,
    bool SkipUnquotedPreciseTitleScans,
    bool UseScopedProfileFallback,
    bool UseScopedCatalogDocumentOverviewFallback,
    bool AllowSparseAssistForScopedProfileFallback,
    bool AllowSparseAssistForBroadDiversity,
    bool AllowNavigationCatalogRouteForBroadExploration,
    bool SkipUnanchoredTitleAnchorRouteForBroadDiversity,
    bool SkipSparseRetrieverForBroadDiversity,
    bool SkipSparseProfileCardAssist,
    bool RequireDocumentOverviewProfileMatch,
    bool SkipLocalTitleTokenRoute,
    bool SkipQuotedTitleFullScan);

internal sealed record InteractiveBackfillTerms(IReadOnlyList<string> DateDocumentTitles,
    IReadOnlyList<string> CompactDocumentCodes, IReadOnlyList<string> DocumentStatusOperands);

internal sealed record InteractiveRetrievalResources(RetrievalExecutionContext Context, NpgsqlDataSource DataSource,
    RagOptions Options, IHttpClientFactory HttpClients);

/// <summary>One owner for timers; channel callbacks update this request only.</summary>
internal sealed class InteractiveRetrievalTimings
{
    internal Stopwatch Total { get; } = new();
    internal Stopwatch OrderingWatch { get; set; } = new();
    internal long ExactMs { get; set; }
    internal long QuotedTitleMs { get; set; }
    internal long LocalTitleTokenMs { get; set; }
    internal long TitleAnchorRoutePhaseMs { get; set; }
    internal long TeiMs { get; set; }
    internal long RerankMs { get; set; }
    internal long SparseMs { get; set; }
    internal long QdrantMs { get; set; }
    internal long SparsePhaseMs { get; set; }
    internal long DensePhaseMs { get; set; }
    internal long LinkedPhaseMs { get; set; }
    internal long ProfilePhaseMs { get; set; }
    internal long FusionMs { get; set; }
    internal long RerankPhaseMs { get; set; }
    internal long SelectionMs { get; set; }
    internal int QdrantStatus { get; set; }
}

internal sealed record InteractiveRetrievalSession(InteractiveRetrievalResources Resources,
    InteractiveRetrievalPlan Plan, InteractiveRetrievalTimings Timings, RetrievalPhaseDiagnostics Phases,
    RetrievalDegradationTracker Degradation, RagRetrievalDiagnosticsBuilder Diagnostics);

internal enum InteractiveInitialRoute { Exact, TitleAnchor, Broad }

internal sealed record InteractiveLookupSeeds(List<RagMatch> ExactMatches,
    List<RagMatch> QuotedTitleAnchorMatches, List<RagMatch> TitleLookupMatches,
    string ExplicitDocumentScopedBackfillQuery, bool UseExplicitDocumentScopedBackfill,
    List<RagMatch>? EarlyTitleAnchorRouteMatches, long EarlyTitleAnchorRouteMs, InteractiveInitialRoute Route);

/// <summary>Mutable candidate buffers have one owner per request; the query plan remains immutable.</summary>
internal sealed class InteractiveCandidateSelection(int capacity)
{
    internal List<RagMatch> Matches { get; } = new(capacity);
    internal HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<RagMatch>? BroadDiversityCandidates { get; set; }
    internal List<RagMatch> ReusablePreciseTitleCandidates { get; set; } = [];
    internal List<RagMatch>? DocumentStatusOperandCandidates { get; set; }
}

internal sealed record InteractiveRetrievalFlow(InteractiveRetrievalSession Session,
    InteractiveLookupSeeds Seeds, InteractiveCandidateSelection Selection);

internal sealed record InteractivePrimaryChannels(List<RagMatch> EffectiveSparseMatches,
    List<RagMatch> EffectiveDenseMatches, List<RagMatch> ProfileMatches, List<RagMatch> CombinedTitleAnchorRouteMatches,
    bool DeferDocumentProfileSearch, int DocumentProfileCandidateCount, bool AllowComparativeDocumentProfileAssist,
    bool PrioritizeDocumentProfilesForSelection);
