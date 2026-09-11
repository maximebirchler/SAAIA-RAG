using System.Collections.Generic;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int RouterCanonicalHintsLimit = 6;
    private const int SerializedTailContentMaxChars = 420;
    private const int RagWriterMaxHits = 4;
    private const int RagWriterComparativeMaxHits = 8;
    private const int RagWriterMaxExcerptChars = 320;
    private const int RagWriterMaxFullTextChars = 420;
    private const int RagWriterContextualEvidenceChars = 420;
    private const int RagWriterContextualRawChars = 420;
    private const int RagWriterContextualTotalChars = 900;
    private const int RagWriterBroadMaxHits = 8;
    private const int RagWriterPlanningMaxHits = 10;
    private const int RagWriterMergedBroadMaxHits = 10;
    private const int RagWriterMergedPlanningMaxHits = 10;
    private const int RagWriterBroadExcerptChars = 280;
    private const int RagWriterBroadFullTextChars = 520;
    private const int RagWriterBroadContextualChars = 520;
    private const int RagWriterMaxContentCards = 4;
    private const int RagWriterMaxCardQuantityFacts = 4;
    private const int RagWriterMaxCardFacts = 6;
    private const int RagWriterMaxCardEvidenceTextChars = 120;
    private const int SourceBackedEvidenceMaxChars = 620;
    private const int WriterPromptCharsPerTokenEstimate = 4;
    private const int WriterPromptMinimumContextTokens = 2048;
    private const int WriterPromptMinimumToolResultsChars = 2400;
    private const int WriterPromptMaximumToolResultsChars = 26000;
    private const string SourceReferenceExtensionRegex = @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv|json|ya?ml|html?|png|jpe?g|tiff?|bmp)\b";
    private readonly ApiClient _api;
    private readonly ILlmClient _llm;
    private readonly ToolMemory _mem;
    private readonly AppSettings? _settings;
    private long _lastRouterMs;
    private long _lastToolsMs;
    private long _lastWriterMs;
    private long _lastTotalMs;
    private long _lastCriticMs;
    private string? _lastCriticStatus;
    private string? _lastCriticWarning;
    private bool _lastCriticRevisedAnswer;
    private bool _lastCriticEligible;
    private string? _lastCriticSkipReason;
    private bool _lastUsedGeneralChatPrompt;
    private bool _lastUsedInventoryRendered;
    private bool _lastUsedSummaryFlow;
    private string _lastResponseFormat = "auto";
    private string _lastEffectiveMode = "auto";
    private List<string> _lastWriterToolNames = new();
    private List<(string tool, long durationMs, bool ok)> _lastToolDurations = new();
    private string _lastAnswerSource = "unknown";

    // limite securite perf (spec : max 5 RAG/calls par requete)
    private const int MaxToolCalls = 8;
    private const int MaxRagToolCalls = 5;
    private const int MaxBroadExplorationRagToolCalls = 16;
    private const int MaxInitialSourceBackedPlanningProbeQueries = 6;
    private const int InitialSourceBackedPlanningProbeTopK = 12;
    private const int InitialSourceBackedPlanningProbeMaxPerDoc = 8;
    private const int InitialSourceBackedPlanningProbeMaxPerPage = 4;
    private const int RouterLlmTimeoutMs = 8000;
    private const int StructuredRouterLlmTimeoutMs = 30000;
    private const int SourceBackedRouterLlmTimeoutMs = 180000;
    private const int SourceBackedEvidenceExplorationTimeoutMs = 180000;
    private const int MaxSourceBackedEvidenceExplorationTimeoutMs = 540000;
    private const int SourceBackedLlmEvidencePlannerTimeoutMs = 240000;
    private const int SourceBackedPlanningWriterTimeoutMs = 240000;
    private const int ExtendedSourceBackedPlanningWriterTimeoutMs = MaxSourceBackedEvidenceExplorationTimeoutMs;
    private const int MaxSourceBackedLlmEvidencePlannerSourceLeadLines = 6;
    private const int MaxSourceBackedLlmEvidencePlannerStructureHintLines = 10;
    private const int MaxSourceBackedLlmEvidencePlannerCategoryHints = 4;
    private const int MaxSourceBackedLlmEvidencePlannerDeterministicSeeds = 8;
    private const int MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries = 12;
    private const int MaxSourceBackedLlmEvidencePlannerCoverageTraceLines = 14;
    private const int MaxSourceBackedLlmEvidencePlannerWorkingNoteLines = 8;

    private const int MaxSourceBackedNavigationOrientationQueries = 5;
    private const int MaxSourceBackedSummaryOrientationQueries = 5;


    internal ToolAgentOrchestrator(ApiClient api, ILlmClient llm, ToolMemory mem, AppSettings? settings = null)
    {
        _api = api;
        _llm = llm;
        _mem = mem;
        _settings = settings;
    }

    private LlmProviderDescriptor? ActiveProviderDescriptor
        => (_llm as ILlmProvider)?.Descriptor;

    private bool EnforcesLocalCapabilityBoundary
        => ShouldEnforceLocalCapabilityBoundary(ActiveProviderDescriptor?.Mode);

    private static bool ShouldEnforceLocalCapabilityBoundary(LlmProviderMode? providerMode)
        => providerMode is null or LlmProviderMode.Local;

}
