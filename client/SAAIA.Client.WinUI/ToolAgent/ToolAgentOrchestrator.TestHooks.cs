#if DEBUG
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string TestHookDocPath = "TestFixtures/reference-document.pdf";
    private const string TestHookDocName = "reference-document.pdf";

    internal static string NormalizeRouterIntentForTests(string? intent)
        => NormalizeRouterIntent(intent);

    internal static string InferIntentFromToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => InferIntentFromToolCalls(toolCalls) ?? string.Empty;

    internal static string DetectMessageLanguageForTests(string? message)
        => DetectMessageLanguage(message);

    internal static string ResolveTurnLanguageForTests(string userMessage, string? routerLanguage, string interactionLanguage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        return sut.ResolveTurnLanguage(userMessage, routerLanguage, interactionLanguage);
    }

    internal static RouterPlan.ToolCall[] SanitizeToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => SanitizeToolCalls(toolCalls).ToArray();

    internal static RouterPlan ApplyDocumentaryRagDefaultsForTests(RouterPlan plan, string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        sut.ApplyDocumentaryRagDefaults(plan, userMessage);
        return plan;
    }

    internal static RouterPlan ApplySourceBackedClarificationOverrideForTests(RouterPlan plan, string userMessage)
    {
        ApplySourceBackedClarificationOverride(plan, userMessage);
        return plan;
    }

    internal static JsonElement NormalizeToolArgsForTests(string toolName, string jsonArgs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        return NormalizeToolArgs(toolName, doc.RootElement);
    }

    internal static string ResolveAdminSummarySubmitDocLanguageForTests(string jsonArgs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        return ResolveAdminSummarySubmitDocLanguage(doc.RootElement);
    }

    internal static JsonElement NormalizeRagHitsForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return NormalizeRagHits(doc.RootElement);
    }

    internal static string ClassifyRagHitRoleForTests(string json, string? query = null)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ClassifyRagHitEvidenceProfile(BuildRagHitSummary(doc.RootElement), query).Role;
    }

    internal static bool LooksLikeExactItemReferenceOnlyHitForTests(string requestedTitle, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return LooksLikeExactItemReferenceOnlyHit(requestedTitle, BuildRagHitSummary(doc.RootElement));
    }

    internal static string SerializeWriterRagResultsForTests(string toolName, string json, string userMessage)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict"
        };

        return SerializeToolResults(BuildWriterToolResults(plan, toolResults, userMessage));
    }

    internal static string SerializeWriterRagResultsForTests(
        IReadOnlyList<(string ToolName, string Json)> results,
        string userMessage)
    {
        var toolResults = new ToolResults();
        foreach (var (toolName, json) in results)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = toolName,
                Result = doc.RootElement.Clone()
            });
        }

        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict"
        };

        return SerializeToolResults(BuildWriterToolResults(plan, toolResults, userMessage));
    }

    internal static string BuildProbeRagToolResultsJsonForTests(IReadOnlyList<RagItem> hits)
    {
        var toolResults = BuildProbeRagToolResults(hits);
        return JsonSerializer.Serialize(toolResults.Items.Single().Result);
    }

    internal static bool ShouldUseSourceBackedExtractiveAnswerForTests(string query, ToolResults toolResults)
        => ShouldUseSourceBackedExtractiveAnswer(query, toolResults);

    internal static bool LooksLikeShortTechnicalEvidenceTopicForTests(string? query)
        => LooksLikeShortTechnicalEvidenceTopic(query);

    internal static bool LooksLikeVagueVerificationScopeQuestionForTests(string? query)
        => LooksLikeVagueVerificationScopeQuestion(query);

    internal static string BuildSourceBackedExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedExtractiveAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedExtractiveSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromExtractiveHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static string BuildSourceBackedExtractiveSourcesPayloadForTests(ToolResults toolResults, string query)
        => JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromExtractiveHits(toolResults, query)));

    internal static string BuildDocumentVersionTraceabilityAnswerForTests(string json, string query, string language = "fr")
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.multi_search",
            Result = doc.RootElement.Clone()
        });

        return TryBuildDocumentVersionTraceabilityAnswer(toolResults, query, language) ?? string.Empty;
    }

    internal static string BuildDocumentVersionTraceabilitySourcesPayloadForTests(string json, string query)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.multi_search",
            Result = doc.RootElement.Clone()
        });

        return JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, query)));
    }

    internal static string BuildDocumentVersionTraceabilitySearchQueryForTests(string query)
        => BuildDocumentVersionTraceabilitySearchQuery(query);

    internal static string[] BuildDocumentVersionTraceabilitySearchQueriesForTests(string query)
        => BuildDocumentVersionTraceabilitySearchQueries(query);

    internal static string BuildDocumentVersionTraceabilityExactSearchQueryForTests(string exactTitle, string query)
        => BuildDocumentVersionTraceabilityExactSearchQuery(exactTitle, query);

    internal static string[][] ExtractComparativeEntityAnchorTermsForTests(string query)
        => ExtractComparativeEntityAnchorTerms(query);

    internal static string BuildRagSearchSourcesPayloadForTests(string json, string toolName = "rag.search")
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        return JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromRagHits(toolResults)));
    }

    internal static string BuildSummarySearchSourcesPayloadForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "summary.search",
            Result = doc.RootElement.Clone()
        });

        return JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromSummarySearch(toolResults)));
    }

    internal static string BuildSourceResolveSourcesPayloadForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "sources.resolve",
            Result = doc.RootElement.Clone()
        });

        var source = TryBuildSourceFromResolveResult(toolResults);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildEnrichedSourceResolveSourcesPayloadForTests(
        ToolMemory mem,
        string rawRef,
        string backendRef,
        string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.Object)
            return JsonSerializer.Serialize(BuildSourcesPayload([]));

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var enriched = sut.EnrichSourceResolveResult(rawRef, backendRef, doc.RootElement.Clone(), sourceEl.Clone());
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "sources.resolve",
            Result = enriched
        });

        var source = TryBuildSourceFromResolveResult(toolResults);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildLocalSourceResolveFallbackPayloadForTests(ToolMemory mem, string sourceRef)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var source = sut.ResolveSourceRef(sourceRef);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildSummarySourcesPayloadForTests(string toolName, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        var (_, sourcesPayload, _, _, _) = sut.TryBuildSummaryAnswer(toolResults);
        return JsonSerializer.Serialize(sourcesPayload);
    }

    internal static string BuildSummaryRetrievalQueryForTests(string docName, string strategy, string language, string level)
        => BuildSummaryRetrievalQuery(
            new ResolvedDocRef("doc-1", TestHookDocPath, docName, null, null, null),
            strategy,
            language,
            level);

    internal static string BuildSummaryRetrievalQueryWithSourceCardsForTests(
        string docName,
        string strategy,
        string language,
        string level,
        string? categoryPath,
        params string[] cardTitles)
        => BuildSummaryRetrievalQuery(
            new ResolvedDocRef("doc-1", TestHookDocPath, docName, null, categoryPath, null),
            strategy,
            language,
            level,
            new ToolMemory.SourceRef
            {
                DocPath = TestHookDocPath,
                Label = docName,
                CategoryPath = categoryPath,
                MatchedContentCards = cardTitles
                    .Where(static title => !string.IsNullOrWhiteSpace(title))
                    .Select(static title => new ToolMemory.SourceContentCardRef
                    {
                        Title = title.Trim()
                })
                    .ToList()
            });

    internal static string BuildSummaryRetrievalQueryWithSourceProfileSignalsForTests(
        string docName,
        string strategy,
        string language,
        string level,
        string? categoryPath,
        params string[] profileTerms)
        => BuildSummaryRetrievalQuery(
            new ResolvedDocRef("doc-1", TestHookDocPath, docName, null, categoryPath, null),
            strategy,
            language,
            level,
            new ToolMemory.SourceRef
            {
                DocPath = TestHookDocPath,
                Label = docName,
                CategoryPath = categoryPath,
                ProfileSignals = new ToolMemory.SourceProfileSignalsRef
                {
                    ProfileVersion = "llm_backoffice_v1",
                    Language = language,
                    Keywords = profileTerms.Take(2).ToList(),
                    Topics = profileTerms.Skip(2).Take(2).ToList(),
                    HypotheticalQuestions = profileTerms.Skip(4).Take(1).ToList(),
                    Limits = profileTerms.Skip(5).Take(1).ToList()
                }
            });

    internal static string BuildLiveSummaryFallbackSourcePayloadForTests(
        string docId,
        string docPath,
        string docName,
        string? categoryPath,
        int? pages,
        string? categoryRef,
        string? sourceHash,
        string? docLanguage,
        string? profileLanguage,
        string? category = null)
    {
        var source = BuildLiveSummaryFallbackSourceMetadata(new ResolvedDocRef(
            docId,
            docPath,
            docName,
            Category: category,
            categoryPath,
            pages,
            categoryRef,
            sourceHash,
            docLanguage,
            profileLanguage));

        return JsonSerializer.Serialize(BuildSourcesPayload([source]));
    }

    internal static string BuildDuplicatePageSourcesPayloadForTests()
    {
        var sources = new List<ToolMemory.SourceRef>
        {
            new()
            {
                DocId = "doc-v1",
                DocPath = "Knowledge/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 10,
                Label = "guide.pdf"
            },
            new()
            {
                DocPath = "Knowledge/archive/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 12,
                Label = "guide.pdf",
                SourceHash = "archive456",
                DocLanguage = "en"
            },
            new()
            {
                DocId = "doc-v2",
                DocPath = "Knowledge/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 12,
                Label = "guide.pdf",
                SourceHash = "abc123",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 4,
                PageEnd = 4,
                Label = "manual.pdf"
            }
        };

        return JsonSerializer.Serialize(BuildSourcesPayload(sources));
    }

    internal static string BuildAliasedDuplicatePageSourcesPayloadForTests()
    {
        var sources = new List<ToolMemory.SourceRef>
        {
            new()
            {
                DocPath = "Knowledge/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 10,
                Label = "guide.pdf",
                SourceHash = "same-source",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "C:/saaia-repo/documents/Knowledge/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 11,
                Label = "guide.pdf",
                SourceHash = "same-source",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "Knowledge/archive/guide.pdf",
                DocName = "guide.pdf",
                PageStart = 10,
                PageEnd = 10,
                Label = "guide.pdf",
                SourceHash = "archive-source",
                DocLanguage = "en"
            }
        };

        return JsonSerializer.Serialize(BuildSourcesPayload(sources));
    }

    internal static string BuildFilenameOnlyDuplicatePageSourcesPayloadForTests()
    {
        return JsonSerializer.Serialize(BuildSourcesPayload(BuildFilenameOnlyDuplicatePageSourcesForTests()));
    }

    internal static string BuildFilenameOnlyDuplicatePageSourcesMemoryPayloadForTests()
        => JsonSerializer.Serialize(new
        {
            sources = NormalizeVisibleSourceRefsForMemory(BuildFilenameOnlyDuplicatePageSourcesForTests())
                .Select(static source => new
                {
                    source.DocPath,
                    source.PageStart,
                    source.PageEnd,
                    source.SourceHash
                })
        });

    internal static string BuildQualifiedHashDuplicatePageSourcesPayloadForTests()
    {
        var sources = new List<ToolMemory.SourceRef>
        {
            new()
            {
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "same-source",
                CategoryPath = "Knowledge",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "C:/saaia-repo/documents/Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "same-source",
                CategoryPath = "Knowledge",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "Knowledge/archive/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "archive-source",
                CategoryPath = "Knowledge/archive",
                DocLanguage = "en"
            }
        };

        return JsonSerializer.Serialize(BuildSourcesPayload(sources));
    }

    private static List<ToolMemory.SourceRef> BuildFilenameOnlyDuplicatePageSourcesForTests()
        => new()
        {
            new()
            {
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "same-source",
                CategoryPath = "Knowledge",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "same-source",
                CategoryPath = "Knowledge",
                DocLanguage = "fr"
            },
            new()
            {
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 42,
                PageEnd = 42,
                Label = "manual.pdf",
                SourceHash = "same-source",
                CategoryPath = "Knowledge",
                DocLanguage = "fr"
            }
        };

    internal async Task<(bool Queued, string? JobId, string? Error)> TryQueueAdminSummaryGenerationForTests(
        string docRef,
        CancellationToken ct)
    {
        var outcome = await TryQueueAdminSummaryGenerationAsync(docRef, ct).ConfigureAwait(false);
        return (outcome.Queued, outcome.JobId, outcome.Error);
    }

    internal Task<JsonElement> ExecuteSourcesResolveForTests(string sourceRef, CancellationToken ct)
        => ExecSourcesResolveV2Async(CreateJsonArgs(new { @ref = sourceRef }), ct);

    internal static string BuildSourceBackedExtractiveHeaderForTests(string language, bool noExplicitPairing)
        => BuildSourceBackedExtractiveHeader(language, noExplicitPairing);

    internal static string BuildSourceBackedPlanningAnswerForTests(ToolResults toolResults, string language, string? query = null)
        => BuildSourceBackedPlanningAnswer(toolResults, language, query: query);

    internal static string ExtractPlanItemTitleV2ForTests(string text)
        => ExtractPlanItemTitleV2(text);

    internal static bool ExactItemTextMatchesRequestOrStructureForTests(string requestedTitle, string text)
        => ExactItemTextMatchesRequestOrStructure(requestedTitle, text);

    internal static string TrimAfterLikelyExactItemBoundaryForTests(string text)
        => TrimAfterLikelyExactItemBoundary(text);

    internal static string BuildSourceBackedPlanningOrExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, query, language);

    internal static string[] BuildPlanningExplorationRetrievalQueriesForTests(string query)
        => BuildPlanningExplorationRetrievalQueries(query);

    internal static bool ShouldExpandSourceBackedPlanningRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandSourceBackedPlanningRetrieval(toolResults, query, language);

    internal static bool IsBetterSourceBackedPlanningCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

    internal static bool ShouldExpandSourceBackedEvidenceRetrievalForTests(
        ToolResults toolResults,
        string query,
        string language)
        => ShouldExpandSourceBackedEvidenceRetrieval(toolResults, query, language);

    internal static string AnalyzeSourceBackedEvidenceSufficiencyReasonForTests(
        ToolResults toolResults,
        string query,
        string language)
        => AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language).Reason;

    internal static string[] BuildSourceBackedEvidenceExplorationPassLabelsForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedEvidenceExplorationPasses(toolResults, query, language)
            .Select(static pass => pass.Label)
            .ToArray();

    internal static string[] BuildSourceBackedEvidenceExplorationPassQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedEvidenceExplorationPasses(toolResults, query, language)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static string[] ParseSourceBackedLlmEvidenceExplorationPassLabelsForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.Label)
            .ToArray();

    internal static string[] ParseSourceBackedLlmEvidenceExplorationQueriesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static bool IsBetterSourceBackedEvidenceCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedEvidenceCoverage(current, candidate, query, language);

    internal static string[] BuildDocumentaryProbeRetrievalQueriesForTests(string query)
        => BuildDocumentaryProbeRetrievalQueries(query);

    internal static bool ShouldExpandDocumentaryProbeRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandDocumentaryProbeRetrieval(toolResults, query, language);

    internal static bool ShouldUseWriterForDocumentaryProbeAnswerForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForDocumentaryProbeAnswer(toolResults, query);

    internal static bool IsBetterDocumentaryProbeCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterDocumentaryProbeCoverage(current, candidate, query, language);

    internal static string[] BuildSourceBackedEvidenceExpansionRetrievalQueriesForTests(string query)
        => BuildSourceBackedEvidenceExpansionRetrievalQueries(query);

    internal static string RemoveTrailingModelEmittedSourceListForTests(string answer)
        => RemoveTrailingModelEmittedSourceList(answer);

    internal static bool ShouldUseWriterForBroadSourceBackedPlanningForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedPlanning(toolResults, query);

    internal static bool ShouldAllowWriterForPartialSourceBackedPlanningForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language);

    internal static bool ShouldUseWriterForBroadSourceBackedSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query);

    internal static bool ShouldPreferWriterForPolishedSourceBackedAnswerForTests(ToolResults toolResults, string query)
        => ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query);

    internal static bool ShouldUseAdvisoryEvidenceGuardForBroadSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(toolResults, query);

    internal static bool ShouldOfferBroadenedSourceSearchForTests(string query)
        => ShouldOfferBroadenedSourceSearch(query);

    internal static string BuildAnswerShapeGuidanceForWriterForTests(string query, string language)
        => BuildAnswerShapeGuidanceForWriter(query, language);

    internal static string BuildSourceBackedCoverageHintsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCoverageHintsForWriter(toolResults, query, language);

    internal static string BuildSourceBackedWritingBriefForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedWritingBriefForWriter(toolResults, query, language);

    internal static string BuildSourceBackedCandidateLeadsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCandidateLeadsForWriter(toolResults, query, language);

    internal static bool LooksLikeRawExcerptDumpPlanningAnswerForTests(string answer, string query)
        => LooksLikeRawExcerptDumpPlanningAnswer(answer, query);

    internal static bool LooksLikeWriterControlLeakForTests(string answer)
        => LooksLikeWriterControlLeak(answer);

    internal static bool LooksLikePoorPlanningFallbackAnswerForTests(string answer, string query)
        => LooksLikePoorPlanningFallbackAnswer(answer, query);

    internal static string BuildSourceBackedOptionAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedOptionAnswer(toolResults, language, query: query);

    internal static string TryBuildMissingBroadCompositionAnchorAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildMissingBroadCompositionAnchorAnswer(toolResults, query, language);

    internal static string TryBuildMissingRequiredEvidenceAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);

    internal static string TryBuildBackendGuidanceClarificationAnswerForTests(ToolResults toolResults, string language, string query = "")
        => TryBuildBackendGuidanceClarificationAnswer(toolResults, query, language);

    internal static string BuildRagEvidenceFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildRagEvidenceFallbackAnswer(toolResults, query, language);

    internal static string BuildReadablePartialPlanningEvidenceAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildReadablePartialPlanningEvidenceAnswer(
            EnumerateRagHitSummaries(toolResults).ToList(),
            query,
            language);

    internal static string TryBuildSourcePolicyGuardAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildSourcePolicyGuardAnswer(toolResults, query, language);

    internal static bool LooksLikeDocumentInstructionPolicyRequestForTests(string query)
        => LooksLikeDocumentInstructionPolicyRequest(query);

    internal static bool LooksLikeDocumentVersionTraceabilityRequestForTests(string query)
        => LooksLikeDocumentVersionTraceabilityRequest(query);

    internal static string BuildMissingExplicitDocumentAnswerForTests(string language, string requestedDocument)
        => BuildMissingExplicitDocumentAnswer(language, requestedDocument, Array.Empty<RagHitSummary>());

    internal static string BuildSourcePolicyRetrievalQueryForTests(string query)
        => BuildSourcePolicyRetrievalQuery(query);

    internal static string[] DeriveSourceBackedOptionSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromOptionHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static bool LooksLikeSourceBackedOptionRequestForTests(string query)
        => LooksLikeSourceBackedOptionRequest(query);

    internal static bool LooksLikeSourceBackedCountdownPlanningRequestForTests(string query)
        => LooksLikeSourceBackedCountdownPlanningRequest(query);

    internal static string BuildSourceBackedCountdownPlanningAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCountdownPlanningAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedCountdownSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromCountdownPlanningHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static bool LooksLikeLowStructureShortProcedureTextForTests(string text)
        => LooksLikeLowStructureShortProcedureHit(new RagHitSummary(
            TestHookDocPath,
            TestHookDocName,
            1,
            1,
            text));

    internal static bool LooksLikeLowSignalContentCandidateForTests(string excerpt, int pageStart = 1, string? fullText = null)
        => LooksLikeLowSignalContentCandidateHit(new RagHitSummary(
            TestHookDocPath,
            TestHookDocName,
            pageStart,
            pageStart,
            excerpt,
            FullText: fullText));

    internal static string SerializeTailForTests(IReadOnlyList<(string role, string content)> history, int maxTurns)
        => SerializeTail(history, maxTurns);

    internal static (string EffectiveUserMessage, bool Consumed) PreparePendingClarificationForTests(
        ToolMemory mem,
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var prepared = sut.PrepareUserMessageForPendingClarification(chatHistory, userMessage);
        return (prepared.EffectiveUserMessage, prepared.Consumed);
    }

    internal static string NormalizeRagQueryForTests(string query)
        => NormalizeRagQueryForRetrieval(query);

    internal static string ResolveRagSearchExecutionQueryForTests(string query)
        => ResolveRagSearchExecutionQuery(query);

    internal static bool LooksLikeStandaloneDocumentaryTopicForTests(string query)
        => LooksLikeStandaloneDocumentaryTopic(query);

    internal static bool LooksLikeSourceBackedActionRequestForTests(string query)
        => LooksLikeSourceBackedActionRequest(query);

    internal static bool LooksLikeStructuredItemCardRequestForTests(string query)
        => LooksLikeStructuredItemCardRequest(query);

    internal static bool LooksLikeSourceAbsentAssertionPolicyRequestForTests(string query)
        => LooksLikeSourceAbsentAssertionPolicyRequest(query);

    internal static bool ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequestForTests(string query)
        => ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequest(query);

    internal static bool LooksLikeBinaryAnswerWithSourceUncertaintyRequestForTests(string query)
        => LooksLikeBinaryAnswerWithSourceUncertaintyRequest(query);

    internal static bool LooksLikeSourceBackedPlanningRequestForTests(string query)
        => LooksLikeSourceBackedPlanningRequest(query);

    internal static bool LooksLikeDocumentContentSelectionExplanationRequestForTests(string query)
        => LooksLikeDocumentContentSelectionExplanationRequest(query);

    internal static bool LooksLikeUnresolvedSourceBackedDeicticFollowupForTests(string query)
        => LooksLikeUnresolvedSourceBackedDeicticFollowup(query);

    internal static string? TryExtractRequestedItemTitleForTests(string query)
        => TryExtractRequestedItemTitle(query);

    internal static string? TryExtractPdfFileNameRequestedTitleForTests(string query)
        => TryExtractPdfFileNameRequestedTitle(query);

    internal static IReadOnlyList<string> ExtractExplicitDocumentFileReferenceQueriesForTests(string query)
        => ExtractExplicitDocumentFileReferenceQueries(query);

    internal static bool LooksLikeNoRagDataAnswerForTests(string answer)
        => LooksLikeNoRagDataAnswer(answer);

    internal static bool ShouldFallbackFromNoRagDataAnswerForTests(string answer)
        => ShouldFallbackFromNoRagDataAnswer(answer);

    internal static string TryBuildNoRagEvidenceAnswerForTests(ToolResults toolResults, string language, string query)
        => TryBuildNoRagEvidenceAnswerForEmptySearch(toolResults, language, query);

    internal static bool ShouldReplaceOverPromotedSourceBackedOptionAnswerForTests(
        string answer,
        ToolResults toolResults,
        string query)
        => ShouldReplaceOverPromotedSourceBackedOptionAnswer(answer, toolResults, query);

    internal static bool LooksLikeMissingExactItemWithoutSourceLeadsForTests(string answer)
        => LooksLikeMissingExactItemWithoutSourceLeads(answer);

    internal static bool LooksLikeDegenerateLlmOutputForTests(string answer)
        => LooksLikeDegenerateLlmOutput(answer);

    internal static string[] BuildPlanningRetrievalQueriesForTests(string query)
        => BuildPlanningRetrievalQueries(query);

    internal static string[] BuildSourceBackedActionRetrievalQueriesForTests(string query)
        => BuildSourceBackedActionRetrievalQueries(query);

    internal static int NormalizeSourceBackedActionTopKForTests(int? requestedTopK, string query)
        => NormalizeSourceBackedActionTopK(requestedTopK, query);

    internal static int ResolveSourceBackedActionRetrievalQueryLimitForTests(string query)
        => ResolveSourceBackedActionRetrievalQueryLimit(query);

    internal static int ResolveRagMultiSearchQueryBudgetForTests(int topK, int availableQueries)
        => ResolveRagMultiSearchQueryBudget(topK, availableQueries);

    internal static string[] BuildPreciseRetrievalQueriesForTests(string exactTitle, string retrievalQuery, string? originalQuery = null)
        => BuildPreciseRetrievalQueries(exactTitle, retrievalQuery, originalQuery);

    internal static bool ShouldTryPreciseMultiSearchForExactItemForTests(
        JsonElement ragResult,
        string query,
        string exactItemTitle,
        string? requestedExplicitDocument = null,
        bool isCompactTechnicalExactItem = false,
        bool isDocumentVersionTraceabilityRequest = false)
        => ShouldTryPreciseMultiSearchForExactItem(
            ragResult,
            query,
            exactItemTitle,
            requestedExplicitDocument,
            isCompactTechnicalExactItem,
            isDocumentVersionTraceabilityRequest);

    internal static bool LooksLikeComparativeDocumentaryRequestForTests(string query)
        => LooksLikeComparativeDocumentaryRequest(query);

    internal static string[] BuildComparativeRetrievalQueriesForTests(string query)
        => BuildComparativeRetrievalQueries(query);

    internal static int CountExplicitDocumentFileReferencesForTests(string query)
        => CountExplicitDocumentFileReferences(query);

    internal static bool ShouldRunDocumentaryProbeForTests(string query, RouterPlan plan)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        return sut.ShouldRunDocumentaryProbe(query, plan);
    }

    internal static (bool Matched, string Topic) TryExtractDocumentContentSearchTopicForTests(string query)
    {
        var matched = TryExtractDocumentContentSearchTopic(query, out var topic);
        return (matched, topic);
    }

    internal static string BuildDocumentContentSearchAnswerForTests(ToolResults toolResults, string topic, string language)
        => BuildDocumentContentSearchAnswer(toolResults, topic, language);

    internal static bool ShouldExpandDocumentContentSearchForTests(string query, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ShouldExpandDocumentContentSearch(query, doc.RootElement);
    }

    internal static bool ShouldUseWriterForDocumentContentSearchAnswerForTests(string query, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ShouldUseWriterForDocumentContentSearchAnswer(query, doc.RootElement);
    }

    internal static bool IsBetterDocumentContentSearchCoverageForTests(string currentJson, string candidateJson)
    {
        using var current = JsonDocument.Parse(string.IsNullOrWhiteSpace(currentJson) ? "{}" : currentJson);
        using var candidate = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidateJson) ? "{}" : candidateJson);
        return IsBetterDocumentContentSearchCoverage(current.RootElement, candidate.RootElement);
    }
}
#endif
