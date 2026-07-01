#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services;
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

    internal static bool TryRepairJsonObjectForParsingForTests(string json, out string repaired)
        => TryRepairJsonObjectForParsing(json, out repaired);

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

    internal static string SerializeBudgetedWriterRagResultsForTests(string toolName, string json, string userMessage, int contextTokens, int maxOutputTokens = 900)
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

        var budget = CreateWriterPromptBudget(contextTokens, maxOutputTokens);
        var compacted = BuildWriterToolResults(plan, toolResults, userMessage);
        return SerializeToolResults(ApplyWriterToolResultsBudget(compacted, userMessage, budget.ToolResultsChars));
    }

    internal static int ResolveWriterToolResultsBudgetCharsForTests(int contextTokens, int maxOutputTokens = 900)
        => CreateWriterPromptBudget(contextTokens, maxOutputTokens).ToolResultsChars;

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

    internal static bool ShouldSkipExactItemPreRouterShortcutForTests(string query)
        => ShouldSkipExactItemPreRouterShortcut(query);

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

    internal static string[] BuildSourceBackedPlanningDraftSourceKeysForTests(ToolResults toolResults, string language, string? query = null)
    {
        var draft = BuildSourceBackedPlanningDraft(toolResults, language, query: query);
        return draft.Sources
            .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
            .ToArray();
    }

    internal static string ExtractPlanItemTitleV2ForTests(string text)
        => ExtractPlanItemTitleV2(text);

    internal static bool ExactItemTextMatchesRequestOrStructureForTests(string requestedTitle, string text)
        => ExactItemTextMatchesRequestOrStructure(requestedTitle, text);

    internal static string TrimAfterLikelyExactItemBoundaryForTests(string text)
        => TrimAfterLikelyExactItemBoundary(text);

    internal static string BuildSourceBackedPlanningOrExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, query, language);

    internal static string FormatSourceBackedPlanningDisplayTitleForTests(string title)
        => HumanizeSourceBackedDisplayTitle(CleanSourceBackedOptionTitle(title));

    internal static bool LooksLikeNoisyStructuredPlanningCandidateTitleForTests(string title)
        => LooksLikeNoisyStructuredPlanningCandidateTitle(title);

    internal static bool ShouldTrustSourceBackedExplorationPassCategoryScopeForTests(
        string? passOrigin,
        bool passHasDocumentScope,
        string? resolvedPassCategoryScope,
        string? passCategoryScope,
        bool categoryScopeTrustedByCurrentEvidence,
        bool passCategoryScopeReusedFromInference)
        => ShouldTrustSourceBackedExplorationPassCategoryScope(
            passOrigin,
            passHasDocumentScope,
            resolvedPassCategoryScope,
            passCategoryScope,
            categoryScopeTrustedByCurrentEvidence,
            passCategoryScopeReusedFromInference);

    internal static string[] BuildPlanningExplorationRetrievalQueriesForTests(string query)
        => BuildPlanningExplorationRetrievalQueries(query);

    internal static string[] BuildSourceBackedCandidateDiscoveryRetrievalQueriesForTests(string query)
        => BuildSourceBackedCandidateDiscoveryRetrievalQueries(query);

    internal static string[] BuildInitialSourceBackedPlanningProbeQueriesForTests(string query)
        => BuildInitialSourceBackedPlanningProbeQueries(query);

    internal static string NormalizeInitialSourceBackedPlanningProbeFamilyKeyForTests(string query)
        => NormalizeInitialSourceBackedPlanningProbeFamilyKey(query);

    internal static (string? CategoryScope, bool ReusedFromCurrentTurnInference) ResolveSourceBackedExplorationPassCategoryScopeForTests(
        string? resolvedPassCategoryScope,
        string? currentCategoryScope,
        string? currentTurnInferredCategoryScope,
        string? passOrigin,
        bool passHasDocumentScope)
        => ResolveSourceBackedExplorationPassCategoryScope(
            resolvedPassCategoryScope,
            currentCategoryScope,
            currentTurnInferredCategoryScope,
            passOrigin,
            passHasDocumentScope);

    internal static bool ShouldExpandSourceBackedPlanningRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandSourceBackedPlanningRetrieval(toolResults, query, language);

    internal static bool ShouldRespectLlmRouterGeneralWithoutToolsForTests(RouterPlan plan)
        => ShouldRespectLlmRouterGeneralWithoutTools(plan);

    internal static string[] SourceBackedPlanningCandidateTitlesForTests(ToolResults toolResults, string query, string language, int maxItems = 32)
        => SelectSourceBackedPlanningCandidates(toolResults, query, maxItems, language)
            .Select(static candidate => candidate.Title)
            .ToArray();

    internal static string[] StrictSourceBackedOptionTitlesForTests(ToolResults toolResults, string query)
        => EnumerateRagHitSummaries(toolResults)
            .SelectMany(hit => ExtractStrictSourceBackedOptionTitles(hit, query))
            .ToArray();

    internal static string[] BuildSourceBackedPlanningTraceLinesForTests(ToolResults toolResults, string query, string language = "fr")
        => BuildSourceBackedPlanningTraceLines(toolResults, query, language);

    internal static string BuildRagTraceLineForTests(string eventName, params (string Key, object? Value)[] fields)
        => BuildRagTraceLine(eventName, "test-trace", 7, 123, fields);

    internal static bool IsBetterSourceBackedPlanningCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

    internal static bool LooksLikeUnsupportedSourceBackedPlanningAnswerForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => LooksLikeUnsupportedSourceBackedPlanningAnswer(answer, toolResults, query, language);

    internal static (int ItemCount, int SupportedItemCount, int UnsupportedItemCount, int CandidateCount, int SourceCount)
        AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            string? answer,
            ToolResults toolResults,
            string? query,
            string language)
    {
        var analysis = AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language);
        return (
            analysis.ItemCount,
            analysis.SupportedItemCount,
            analysis.UnsupportedItemCount,
            analysis.CandidateCount,
            analysis.Sources.Count);
    }

    internal static string[] ExtractConcretePlanningAnswerItemsForTests(string answer)
        => ExtractConcretePlanningAnswerItems(answer).ToArray();

    internal static bool ShouldRejectUnsupportedPlanningAnswerForFinalForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => ShouldRejectUnsupportedPlanningAnswerForFinal(
            AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language),
            query);

    internal static (
        bool Applied,
        string Answer,
        int SourceCount,
        string[] SourceKeys,
        string Resolution,
        int ItemCount,
        int SupportedItemCount,
        int UnsupportedItemCount,
        int CandidateCount) FinalizeSourceBackedPlanningResponseForTests(
            string? answer,
            ToolResults toolResults,
            string? query,
            string language)
    {
        var applied = TryFinalizeSourceBackedPlanningResponse(
            answer,
            toolResults,
            query,
            language,
            out var finalAnswer,
            out var sources,
            out var analysis,
            out var resolution);

        return (
            applied,
            finalAnswer,
            sources.Count,
            sources
                .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
                .ToArray(),
            resolution,
            analysis.ItemCount,
            analysis.SupportedItemCount,
            analysis.UnsupportedItemCount,
            analysis.CandidateCount);
    }

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

    internal static string[] BuildSourceBackedRouteAnchorFollowupRetrievalQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedRouteAnchorFollowupRetrievalQueries(toolResults, query, language);

    internal static (string Label, string? DocId, string? DocPath, string? CategoryScope, int? PageStart, int? PageEnd, string[] Queries)[] BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(toolResults, query, language)
            .Select(static pass => (pass.Label, pass.DocId, pass.DocPath, pass.CategoryScope, pass.PageStart, pass.PageEnd, pass.Queries))
            .ToArray();

    internal static int ResolveSourceBackedEvidenceExplorationTopKForTests(string? query, string passLabel)
        => ResolveSourceBackedEvidenceExplorationTopK(query, passLabel);

    internal static int ResolveSourceBackedDocumentScopedExplorationMaxPerPageForTests(string? query, string passLabel)
        => ResolveSourceBackedDocumentScopedExplorationMaxPerPage(query, passLabel);

    internal static string BuildSourceBackedAnchorFollowupSignatureForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedAnchorFollowupSignature(
            BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(toolResults, query, language),
            BuildSourceBackedRouteAnchorFollowupExplorationPass(toolResults, query, language));

    internal static (string? DocId, string? DocPath, string? CategoryPath, string DisplayName, int Score)[] SelectSourceBackedDocumentNavigationSeedsForTests(
        ToolResults toolResults,
        int maxDocuments = 3)
        => SelectSourceBackedDocumentNavigationSeeds(toolResults, maxDocuments)
            .Select(static seed => (seed.DocId, seed.DocPath, seed.CategoryPath, seed.DisplayName, seed.Score))
            .ToArray();

    internal static bool HasSourceBackedRouteAnchorFollowupQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => HasSourceBackedRouteAnchorFollowupQueries(toolResults, query, language);

    internal static bool ShouldDeferSparseSourceBackedPlanningAnchorFollowupForTests(
        ToolResults toolResults,
        string query,
        string language)
        => ShouldDeferSparseSourceBackedPlanningAnchorFollowup(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query);

    internal static bool ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
        ToolResults toolResults,
        string query,
        string language,
        bool acceptedAnyExplorationPass)
        => ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPass(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query,
            acceptedAnyExplorationPass);

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

    internal static string[] ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
        string rawJson,
        string query,
        string language = "fr",
        IEnumerable<string>? alreadyTriedQueries = null,
        string? plannedCategoryScope = null)
        => FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
                ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries),
                query,
                language,
                plannedCategoryScope,
                out _,
                out _)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static string[] DetectMissingStructuredRouterSearchAxesForTests(
        string query,
        string language,
        params string[] routerQueries)
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = language,
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = routerQueries,
                        topK = 8,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    })
                }
            }
        };

        return DetectMissingStructuredRouterSearchAxes(plan, query, language);
    }

    internal static string[] FindStructuredRouterSearchAxisRegressionsForTests(
        IReadOnlyList<string> missingBefore,
        IReadOnlyList<string> missingAfter)
        => FindStructuredRouterSearchAxisRegressions(missingBefore, missingAfter);

    internal static (bool Apply, string Reason, string[] MissingBefore, string[] MissingAfter, string[] RegressedAxes)
        ShouldApplyInitialLlmPlannerQueriesForTests(
            string query,
            string language,
            string[] currentQueries,
            string[] plannerQueries)
    {
        var apply = ShouldApplyInitialLlmPlannerQueries(
            currentQueries,
            plannerQueries,
            query,
            language,
            out var reason,
            out var missingBefore,
            out var missingAfter,
            out var regressedAxes);
        return (apply, reason, missingBefore, missingAfter, regressedAxes);
    }

    internal static (string[] Queries, string[] MissingAfter) BuildStructuredRouterSearchAxisFallbackQueriesForTests(
        string query,
        string language,
        params string[] routerQueries)
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = language,
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = routerQueries,
                        topK = 8,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    })
                }
            }
        };

        var missing = DetectMissingStructuredRouterSearchAxes(plan, query, language);
        return TryBuildStructuredRouterSearchAxisFallbackPlan(
                plan,
                query,
                language,
                missing,
                out _,
                out var fallbackQueries,
                out var missingAfter)
            ? (fallbackQueries, missingAfter)
            : (routerQueries, missing);
    }

    internal static string?[] ParseSourceBackedLlmEvidenceExplorationOriginsForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.Origin)
            .ToArray();

    internal static string BuildSourceBackedAvailableResearchSurfacesForTests()
        => BuildSourceBackedAvailableResearchSurfacesForPrompt();

    internal static string BuildSourceBackedRequestShapeForTests(string query, string language)
        => BuildSourceBackedRequestShapeForPrompt(query, language);

    internal static string BuildSourceBackedLlmEvidenceExplorationSystemPromptForTests(string language)
        => BuildSourceBackedLlmEvidenceExplorationSystemPrompt(language);

    internal static string BuildSourceBackedResearchTopicKeyForTests(string query, string language)
        => BuildSourceBackedResearchTopicKey(query, language);

    internal static string BuildSourceBackedResearchShapeKeyForTests(string query)
        => BuildSourceBackedResearchShapeKey(query);

    internal static string BuildSourceBackedLlmCategoryScopeSystemPromptForTests(string language)
        => BuildSourceBackedLlmCategoryScopeSystemPrompt(language);

    internal static string BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
        ToolResults toolResults,
        string query,
        string language,
        ToolMemory? memory = null)
    {
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, memory ?? new ToolMemory());
        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language);
        var alreadyTriedQueries = BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(toolResults, query, language);
        return sut.BuildSourceBackedLlmEvidenceExplorationUserPrompt(
            toolResults,
            analysis,
            query,
            language,
            alreadyTriedQueries);
    }

    internal static string[] BuildSourceBackedSummaryOrientationQueriesForTests(string query, string? categoryScope = null)
        => BuildSourceBackedSummaryOrientationQueries(query, categoryScope);

    internal static bool ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPassForTests(
        ToolResults toolResults,
        string query,
        string language,
        int remainingPlannerRounds)
        => ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPass(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query,
            remainingPlannerRounds);

    internal static string[] BuildSourceBackedNavigationOrientationQueriesForTests(string query, string? categoryScope = null)
        => BuildSourceBackedNavigationOrientationQueries(query, categoryScope);

    internal static bool HasExpandedSourceBackedSearchEvidenceForTests(ToolResults toolResults)
        => HasExpandedSourceBackedSearchEvidence(toolResults);

    internal static string?[] ParseSourceBackedLlmEvidenceExplorationCategoriesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.CategoryScope)
            .ToArray();

    internal static (string? CategoryScope, string? Decision, string? Confidence, string? Reason) ParseSourceBackedLlmEvidenceExplorationCategoryDecisionForTests(
        string rawJson)
    {
        var decision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(rawJson);
        return (decision.CategoryScope, decision.Decision, decision.Confidence, decision.Reason);
    }

    internal static (string?[] Categories, int UpdatedPassCount) ApplySourceBackedLlmCategoryScopeDecisionForTests(
        string rawJson,
        string categoryScope)
    {
        var passes = ParseSourceBackedLlmEvidenceExplorationPasses(rawJson);
        var updated = ApplySourceBackedLlmCategoryScopeDecision(passes, categoryScope, out var updatedPassCount);
        return (updated.Select(static pass => pass.CategoryScope).ToArray(), updatedPassCount);
    }

    internal static (
        string[] Labels,
        string?[] Categories,
        int QueryCount,
        int UpdatedPassCount,
        bool AddedScopeOnlyPass) FilterAndApplySourceBackedLlmCategoryScopeDecisionForTests(
            string rawJson,
            string query,
            string language,
            string categoryScope)
    {
        var passes = FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
            ParseSourceBackedLlmEvidenceExplorationPasses(rawJson),
            query,
            language,
            categoryScope,
            out _,
            out _);
        var updated = ApplySourceBackedLlmCategoryScopeDecisionOrCreateScopeOnlyPass(
            passes,
            categoryScope,
            "llm_planner",
            out var updatedPassCount,
            out var addedScopeOnlyPass);
        return (
            updated.Select(static pass => pass.Label).ToArray(),
            updated.Select(static pass => pass.CategoryScope).ToArray(),
            updated.Sum(static pass => pass.Queries.Length),
            updatedPassCount,
            addedScopeOnlyPass);
    }

    internal static bool ShouldRunLlmSourceBackedCategoryScopeAdjudicationForTests(
        string rawJson,
        string query,
        string language,
        string categoryHints,
        ToolResults? toolResults = null)
    {
        var passes = ParseSourceBackedLlmEvidenceExplorationPasses(rawJson);
        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults ?? new ToolResults(), query, language);
        return ShouldRunLlmSourceBackedCategoryScopeAdjudication(
            passes,
            analysis,
            query,
            language,
            categoryHints);
    }

    internal static bool ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
        string rawArgsJson,
        string query,
        string language = "fr",
        bool llmOrigin = true)
    {
        var args = JsonDocument.Parse(rawArgsJson).RootElement.Clone();
        var plan = new RouterPlan
        {
            Language = language,
            Origin = llmOrigin ? RouterPlanOrigin.Llm : RouterPlanOrigin.LocalFallback
        };
        return ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(plan, args, query);
    }

    internal static (string? Category, string? CategoryPath, bool TrustCategoryScope, string[] Queries) ApplyResolvedInitialSourceBackedCategoryScopeForTests(
        string rawArgsJson,
        string resolvedCategoryScope)
    {
        var args = JsonDocument.Parse(rawArgsJson).RootElement.Clone();
        var updated = ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(args, resolvedCategoryScope);
        return (
            TryGetStringArg(updated, "category"),
            TryGetStringArg(updated, "categoryPath"),
            GetRagTrustCategoryScopeArg(updated),
            TryGetStringArrayArg(updated, "queries").ToArray());
    }

    internal static (string Label, string? DocId, string? DocPath, int? PageStart, int? PageEnd)[] ParseSourceBackedLlmEvidenceExplorationScopesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => (pass.Label, pass.DocId, pass.DocPath, pass.PageStart, pass.PageEnd))
            .ToArray();

    internal static bool IsBetterSourceBackedEvidenceCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedEvidenceCoverage(current, candidate, query, language);

    internal static bool CandidateSourceBackedEvidenceAddsUsefulDiversityForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => CandidateSourceBackedEvidenceAddsUsefulDiversity(
            AnalyzeSourceBackedEvidenceSufficiency(current, query, language),
            AnalyzeSourceBackedEvidenceSufficiency(candidate, query, language));

    internal static bool CandidateSourceBackedEvidenceAddsUsefulOrientationForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => CandidateSourceBackedEvidenceAddsUsefulOrientation(
            current,
            candidate,
            AnalyzeSourceBackedEvidenceSufficiency(current, query, language),
            AnalyzeSourceBackedEvidenceSufficiency(candidate, query, language),
            query,
            language);

    internal static bool CandidateSourceBackedEvidenceAddsExplorationMaterialForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language,
        bool forceBroadenedExploration = false)
        => CandidateSourceBackedEvidenceAddsExplorationMaterial(
            current,
            candidate,
            query,
            language,
            forceBroadenedExploration);

    internal static int ResolveSourceBackedEvidenceExplorationRagCallBudgetForTests(
        string query,
        string language,
        bool forceBroadenedExploration = false)
    {
        var toolResults = new ToolResults();
        return ResolveSourceBackedEvidenceExplorationRagCallBudget(
            query,
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            forceBroadenedExploration);
    }

    internal static string[] BuildDocumentaryProbeRetrievalQueriesForTests(string query)
        => BuildDocumentaryProbeRetrievalQueries(query);

    internal static bool ShouldExpandDocumentaryProbeRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandDocumentaryProbeRetrieval(toolResults, query, language);

    internal static bool ShouldUseWriterForDocumentaryProbeAnswerForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForDocumentaryProbeAnswer(toolResults, query);

    internal static bool ShouldDeferDocumentaryProbeWriterForBroaderExplorationForTests(
        ToolResults toolResults,
        string query,
        string language = "fr")
        => ShouldDeferDocumentaryProbeWriterForBroaderExploration(toolResults, query, language);

    internal static bool IsBetterDocumentaryProbeCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterDocumentaryProbeCoverage(current, candidate, query, language);

    internal static string[] BuildSourceBackedEvidenceExpansionRetrievalQueriesForTests(string query)
        => BuildSourceBackedEvidenceExpansionRetrievalQueries(query);

    internal static int NormalizeSourceBackedPlanningTopKForTests(int? requestedTopK, string query)
        => NormalizeSourceBackedPlanningTopK(requestedTopK, query);

    internal static int ResolveSourceBackedPlanningTargetItemCountForTests(string query)
        => ResolveSourceBackedPlanningTargetItemCount(query);

    internal static int ResolveSourceBackedDocumentScopedAnchorFollowupLimitForTests(string query)
        => ResolveSourceBackedDocumentScopedAnchorFollowupLimit(query);

    internal static int ResolveMinimumSourceBackedPlanningCandidateCountForTests(string query, int targetSlots, bool hasStructuredAxes)
        => ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);

    internal static string RemoveTrailingModelEmittedSourceListForTests(string answer)
        => RemoveTrailingModelEmittedSourceList(answer);

    internal static bool ShouldUseWriterForBroadSourceBackedPlanningForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedPlanning(toolResults, query);

    internal static bool ShouldUseLlmSourceBackedEvidencePlannerForTests(ToolResults toolResults, string query, string language)
        => ShouldUseLlmSourceBackedEvidencePlanner(query, AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language));

    internal static bool ShouldAllowWriterForPartialSourceBackedPlanningForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language);

    internal static bool IsSourceBackedPlanningCoverageAdequateForTests(ToolResults toolResults, string query, string language = "fr")
        => EvaluateSourceBackedPlanningCoverage(toolResults, query, language).IsAdequate;

    internal static bool HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(ToolResults toolResults, string query, string language = "fr")
        => HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query);

    internal static bool ShouldUseWriterForBroadSourceBackedSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query);

    internal static bool ShouldPreferWriterForPolishedSourceBackedAnswerForTests(ToolResults toolResults, string query)
        => ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query);

    internal static bool ShouldRouteSourceBackedAnswerThroughWriterForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldRouteSourceBackedAnswerThroughWriter(toolResults, query, language);

    internal static bool ShouldAllowSourceBackedWriterRepairForCurrentTurnForTests(string query)
        => ShouldAllowSourceBackedWriterRepairForCurrentTurn(query);

    internal static bool ShouldRequireWriterForBroadDocumentaryFinalForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldRequireWriterForBroadDocumentaryFinal(toolResults, query, language);

    internal static string TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(ToolResults toolResults, string query, string language = "fr")
        => TryBuildInsufficientStructuredPlanningBeforeWriterAnswer(toolResults, query, language);

    internal static string ResolveStructuredPlanningWriterGuardBasisForTests(
        ToolResults rawToolResults,
        ToolResults writerToolResults,
        string query,
        string language = "fr")
        => ResolveStructuredPlanningWriterGuardToolResults(rawToolResults, writerToolResults, query, language).Basis;

    internal static bool RequiresStructuredSourceBackedPlanningCoverageForTests(string query)
        => RequiresStructuredSourceBackedPlanningCoverage(query);

    internal static bool ShouldGateStructuredSourceBackedPlanningCoverageForTests(string query)
        => ShouldGateStructuredSourceBackedPlanningCoverage(query);

    internal static string BuildSourceBackedStructureHintsForTests(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language = "fr")
        => BuildSourceBackedStructureHintsForPrompt(toolResults, lastSourcesUsed, query, language);

    internal static bool ShouldUseAdvisoryEvidenceGuardForBroadSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(toolResults, query);

    internal static bool ShouldOfferBroadenedSourceSearchForTests(string query)
        => ShouldOfferBroadenedSourceSearch(query);

    internal static bool LooksLikeBroadEmptySourceSearchRequestForTests(string query)
        => LooksLikeBroadEmptySourceSearchRequest(query);

    internal static bool ContainsBroadenedSourceSearchOfferForTests(string answer)
        => ContainsBroadenedSourceSearchOffer(answer);

    internal static bool LooksLikeBroadenedSourceSearchConfirmationForTests(string userMessage)
        => LooksLikeBroadenedSourceSearchConfirmation(userMessage);

    internal static string BuildAnswerShapeGuidanceForWriterForTests(string query, string language)
        => BuildAnswerShapeGuidanceForWriter(query, language);

    internal static string BuildSourceBackedCoverageHintsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCoverageHintsForWriter(toolResults, query, language);

    internal static string BuildSourceBackedWritingBriefForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedWritingBriefForWriter(toolResults, query, language);

    internal static string BuildSourceBackedResearchMapForWriterForTests(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language)
        => BuildSourceBackedResearchMapForWriter(toolResults, lastSourcesUsed, query, language);

    internal static string BuildSourceBackedRepairWriterUserPromptForTests(
        IReadOnlyList<(string role, string content)> chatHistory,
        string query,
        string language,
        ToolResults rawToolResults,
        ToolResults writerToolResults)
        => BuildSourceBackedRepairWriterUserPrompt(
            chatHistory,
            query,
            new RouterPlan { Intent = "rag.answer", Language = language, Mode = "auto" },
            rawToolResults,
            writerToolResults,
            Array.Empty<ToolMemory.SourceRef>());

    internal static string BuildWriterToolResultsPromptBlockForTests(
        ToolResults writerToolResults,
        string query,
        string language,
        bool useCleanSourceBrief)
        => BuildWriterToolResultsPromptBlock(writerToolResults, query, language, useCleanSourceBrief);

    internal static string BuildSourceBackedCandidateLeadsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCandidateLeadsForWriter(toolResults, query, language);

    internal static bool ShouldRunSourceBackedCandidateAdjudicationForWriterForTests(ToolResults toolResults, string query, string language)
        => ShouldRunSourceBackedCandidateAdjudicationForWriter(toolResults, query, language);

    internal static string BuildSourceBackedCandidateAdjudicationSystemPromptForTests(string language)
        => BuildSourceBackedCandidateAdjudicationSystemPrompt(language);

    internal static string BuildSourceBackedCandidateAdjudicationUserPromptForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCandidateAdjudicationUserPrompt(toolResults, query, language);

    internal static string? NormalizeSourceBackedCandidateAdjudicationJsonForTests(string? raw)
        => NormalizeSourceBackedCandidateAdjudicationJsonForWriter(raw);

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

    internal static bool ShouldPreferSourceBackedAnswerOverBackendClarificationForTests(ToolResults toolResults, string query)
        => ShouldPreferSourceBackedAnswerOverBackendClarification(toolResults, query);

    internal static string BuildRagEvidenceFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildRagEvidenceFallbackAnswer(toolResults, query, language);

    internal static string BuildSourceBackedSafeFallbackAnswerForTests(ToolResults toolResults, string query, string language, bool shouldAvoidRaw)
        => BuildSourceBackedSafeFallbackAnswer(toolResults, query, language, shouldAvoidRaw);

    internal static string BuildSourceBackedSafeFallbackAfterRejectedWriterForTests(
        ToolResults toolResults,
        string query,
        string writerQuery,
        string language)
        => BuildSourceBackedSafeFallbackAfterRejectedWriter(toolResults, query, writerQuery, language);

    internal static string BuildReadableSourceBackedCandidateListFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, query, language);

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

    internal static bool ShouldAvoidDeterministicSourceBackedOptionFallbackForTests(string query)
        => ShouldAvoidDeterministicSourceBackedOptionFallback(query);

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

    internal static string ResolveSourceBackedFallbackIntentQueryForTests(string query)
        => ResolveSourceBackedFallbackIntentQuery(query);

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

    internal static int ResolveRagMultiSearchQueryBudgetForTests(
        int topK,
        int availableQueries,
        string? researchMode = null,
        bool includeResearchSurfaces = false)
        => ResolveRagMultiSearchQueryBudget(topK, availableQueries, researchMode, includeResearchSurfaces);

    internal static IDisposable OverrideRagMultiSearchSourceExplorationQueryTimeoutForTests(TimeSpan timeout)
    {
        var previous = RagMultiSearchSourceExplorationQueryTimeoutOverrideForTests;
        RagMultiSearchSourceExplorationQueryTimeoutOverrideForTests = timeout;
        return new TestHookScope(() => RagMultiSearchSourceExplorationQueryTimeoutOverrideForTests = previous);
    }

    private sealed class TestHookScope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            dispose();
        }
    }

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

    internal static bool ShouldForceRagForStandaloneTopicForTests(string query, RouterPlan plan)
        => ShouldForceRagForStandaloneTopic(query, plan);

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
