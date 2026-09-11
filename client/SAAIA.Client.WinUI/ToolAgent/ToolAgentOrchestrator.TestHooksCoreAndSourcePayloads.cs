#if DEBUG || SAAIA_TEST_HOOKS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
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

    internal static IReadOnlyList<SourceBackedInitialToolCall>
        BuildSourceBackedRouterInitialActionsForTests(RouterPlan plan)
        => BuildSourceBackedRouterInitialActions(plan);

    internal static RouterPlan ApplyDocumentaryRagDefaultsForTests(RouterPlan plan, string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        sut.ApplyDocumentaryRagDefaults(plan, userMessage);
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

    internal static JsonElement NormalizeRagHitsForTests(string json, bool sourceBackedCanonical = false)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return NormalizeRagHits(doc.RootElement, sourceBackedCanonical);
    }

    internal static Task<JsonElement> EnrichSourceBackedEvidenceWindowsForTestsAsync(ApiClient api, JsonElement normalized)
        => new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory())
            .EnrichSourceBackedEvidenceWindowsAsync(normalized, CancellationToken.None);

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

    internal static bool LooksLikeMissingStandardIdentifierQuestionForTests(string? query)
        => LooksLikeMissingStandardIdentifierQuestion(query);

    internal static string BuildMissingStandardIdentifierClarificationForTests(string language)
        => BuildMissingStandardIdentifierClarification(language);

    internal static (bool RequiresAdvancedAnalysis, string ReasonCode, string PlanKind, int AnswerUnitCount)
        EvaluateLocalCapabilityBoundaryForTests(
            RouterPlan? plan,
            string? userMessage = null,
            bool hasResolvedPriorSources = false)
    {
        var decision = EvaluateLocalCapabilityBoundary(plan, userMessage, hasResolvedPriorSources);
        return (
            decision.RequiresAdvancedAnalysis,
            decision.ReasonCode,
            decision.PlanKind,
            decision.AnswerUnitCount);
    }

    internal static bool EnforcesLocalCapabilityBoundaryForTests(
        LlmProviderMode? providerMode)
        => ShouldEnforceLocalCapabilityBoundary(providerMode);

    internal static bool HasCompleteExplicitStructuredGridAxesForTests(
        string userMessage,
        string language = "fr")
        => HasCompleteExplicitStructuredGridAxes(userMessage, language);

    internal static bool
        ShouldRequestUnresolvedComparativeDocumentReferencesForTests(
            string userMessage,
            bool hasResolvedPriorSources = false)
        => ShouldRequestUnresolvedComparativeDocumentReferences(
            userMessage,
            hasResolvedPriorSources);

    internal static string
        BuildUnresolvedComparativeDocumentReferenceClarificationForTests(
            string language)
        => BuildUnresolvedComparativeDocumentReferenceClarification(language);

    internal static bool ShouldSkipExactItemPreRouterShortcutForTests(string query)
        => ShouldSkipExactItemPreRouterShortcut(query);

    internal static string ResolveTerminalSourceBackedIntentForTests(
        string pipelineIntent,
        string judgeDecision,
        bool isClarification)
        => ResolveTerminalSourceBackedIntent(
            pipelineIntent,
            judgeDecision,
            isClarification);

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

    internal static string BuildSummaryMemorySourcesForTests(
        string toolName,
        string json)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        var memory = new ToolMemory();
        var sut = new ToolAgentOrchestrator(
            api: null!,
            llm: null!,
            mem: memory);
        sut.TryBuildSummaryAnswer(toolResults);
        return JsonSerializer.Serialize(memory.LastSourcesUsed);
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

    internal static string BuildDistinctEvidenceSourcesPayloadForTests()
    {
        var sources = new List<ToolMemory.SourceRef>
        {
            new()
            {
                EvidenceId = "E1",
                DocId = "doc-1",
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "hash-active",
                RevisionId = "revision-active",
                ContentCardId = "card-1"
            },
            new()
            {
                EvidenceId = "E2",
                DocId = "doc-1",
                DocPath = "Knowledge/manual.pdf",
                DocName = "manual.pdf",
                PageStart = 16,
                PageEnd = 16,
                Label = "manual.pdf",
                SourceHash = "hash-active",
                RevisionId = "revision-active",
                ContentCardId = "card-2"
            }
        };

        return JsonSerializer.Serialize(BuildSourcesPayload("rag.answer", sources));
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
}
#endif
