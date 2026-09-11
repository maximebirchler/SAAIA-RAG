using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task Named_document_exact_unique_scopes_initial_action_without_changing_query()
    {
        const string query = "HX-42 inspection interval";
        var originalArguments = JsonSerializer.SerializeToElement(new
        {
            query,
            topK = 4
        });
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate(
                    "doc-hx-42",
                    "Operations/Service Bulletin HX-42.pdf")));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion(
                "The requested document identity must be clarified before research.")),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                originalArguments),
            CancellationToken.None);

        Assert.Equal(
            new[] { "Service Bulletin HX-42.pdf" },
            resolver.RequestedReferences);
        var executedArguments = Assert.Single(executor.Arguments);
        Assert.Equal(query, executedArguments.GetProperty("query").GetString());
        Assert.Equal(4, executedArguments.GetProperty("topK").GetInt32());
        Assert.Equal("doc-hx-42", executedArguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Operations/Service Bulletin HX-42.pdf",
            executedArguments.GetProperty("docPath").GetString());
        var resolutionTrace = Assert.Single(
            result.TraceEvents,
            static trace => trace.EventName
                == "source_backed_named_document_resolution.completed");
        Assert.Equal(
            "Service Bulletin HX-42.pdf",
            resolutionTrace.Fields["requested_reference"]);
        Assert.Equal("26", resolutionTrace.Fields["requested_reference_length"]);
    }

    [Fact]
    public async Task Named_document_unanchored_context_becomes_a_scoped_search_of_the_user_question()
    {
        const string question =
            "D'après le manuel, à quoi sert la commande VACUUM ?";
        var candidate = Candidate(
            "doc-postgresql",
            "Manuals/PostgreSQL_18_Manual.pdf");
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                candidate));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion("Research completed.")),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = NamedDocumentIntake(
                "PostgreSQL_18_Manual.pdf",
                JsonSerializer.SerializeToElement(new { })) with
        {
            UserQuestion = question,
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "router-context",
                    "documents_context",
                    JsonSerializer.SerializeToElement(new
                    {
                        docRef = "PostgreSQL_18_Manual.pdf",
                        before = 2,
                        after = 4,
                        limit = 5
                    }),
                    "llm_router")
            }
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal("rag.search", Assert.Single(executor.ToolNames));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal(question, arguments.GetProperty("query").GetString());
        Assert.Equal("doc-postgresql", arguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Manuals/PostgreSQL_18_Manual.pdf",
            arguments.GetProperty("docPath").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_initial_actions.prepared"
            && trace.Fields["reason"]
                == "resolved_unanchored_context_rewritten_to_search");
    }

    [Fact]
    public async Task Named_document_unanchored_context_prefers_the_router_query_hint()
    {
        const string question =
            "Dans PostgreSQL_18_Manual.pdf, explique en une phrase la fonction de VACUUM et indique ta source.";
        const string queryHint = "fonction de VACUUM dans PostgreSQL";
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate(
                    "doc-postgresql",
                    "Manuals/PostgreSQL_18_Manual.pdf")));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion("Research completed.")),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = NamedDocumentIntake(
                "PostgreSQL_18_Manual.pdf",
                JsonSerializer.SerializeToElement(new { })) with
        {
            UserQuestion = question,
            InitialToolCalls =
            [
                new SourceBackedInitialToolCall(
                    "router-context",
                    "documents_context",
                    JsonSerializer.SerializeToElement(new
                    {
                        docRef = "PostgreSQL_18_Manual.pdf",
                        before = 2,
                        after = 4,
                        limit = 5
                    }),
                    "llm_router",
                    queryHint)
            ]
        };

        await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal("rag.search", Assert.Single(executor.ToolNames));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal(queryHint, arguments.GetProperty("query").GetString());
        Assert.Equal("doc-postgresql", arguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Manuals/PostgreSQL_18_Manual.pdf",
            arguments.GetProperty("docPath").GetString());
    }

    [Fact]
    public async Task Named_document_unfiltered_navigation_receives_the_router_query_hint()
    {
        const string queryHint = "six fonctions principales du CSF 2.0";
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate("doc-nist", "Standards/NIST_CSF_2_0.pdf")));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion("Research completed.")),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = NamedDocumentIntake(
                "NIST_CSF_2_0.pdf",
                JsonSerializer.SerializeToElement(new { })) with
        {
            InitialToolCalls =
            [
                new SourceBackedInitialToolCall(
                    "router-navigation",
                    "documents_navigation",
                    JsonSerializer.SerializeToElement(new
                    {
                        docRef = "NIST_CSF_2_0.pdf",
                        limit = 5
                    }),
                    "llm_router",
                    queryHint)
            ]
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal("documents.navigation", Assert.Single(executor.ToolNames));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal(queryHint, arguments.GetProperty("q").GetString());
        Assert.Equal("doc-nist", arguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Standards/NIST_CSF_2_0.pdf",
            arguments.GetProperty("docPath").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_initial_actions.prepared"
            && trace.Fields["reason"] == "resolved_query_hint_applied");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Named_document_anchored_context_keeps_the_context_read(
        bool useChunkAnchor)
    {
        var candidate = Candidate(
            "doc-hx-42",
            "Operations/Service Bulletin HX-42.pdf");
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                candidate));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion("Research completed.")),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var contextArguments = useChunkAnchor
            ? JsonSerializer.SerializeToElement(new
            {
                chunkId = "observed-chunk",
                limit = 5
            })
            : JsonSerializer.SerializeToElement(new
            {
                pageStart = 12,
                pageEnd = 13,
                limit = 5
            });
        var intake = NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                JsonSerializer.SerializeToElement(new { })) with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "router-context",
                    "documents_context",
                    contextArguments,
                    "llm_router")
            }
        };

        await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal("documents.context", Assert.Single(executor.ToolNames));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal("doc-hx-42", arguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Operations/Service Bulletin HX-42.pdf",
            arguments.GetProperty("docPath").GetString());
        if (useChunkAnchor)
            Assert.Equal("observed-chunk", arguments.GetProperty("chunkId").GetString());
        else
            Assert.Equal(12, arguments.GetProperty("pageStart").GetInt32());
    }

    [Theory]
    [InlineData(SourceBackedDocumentResolutionStatus.NotFound, true)]
    [InlineData(SourceBackedDocumentResolutionStatus.Ambiguous, true)]
    [InlineData(SourceBackedDocumentResolutionStatus.Inconclusive, false)]
    public async Task Named_document_without_unique_complete_identity_quarantines_unscoped_action(
        SourceBackedDocumentResolutionStatus status,
        bool complete)
    {
        var originalArguments = JsonSerializer.SerializeToElement(new
        {
            query = "HX-42 inspection interval",
            topK = 4
        });
        var candidates = status == SourceBackedDocumentResolutionStatus.Ambiguous
            ? new[]
            {
                Candidate("doc-a", "Plant-A/Service Bulletin HX-42.pdf"),
                Candidate("doc-b", "Plant-B/Service Bulletin HX-42.pdf")
            }
            : Array.Empty<SourceBackedDocumentResolutionCandidate>();
        var resolver = new RecordingNamedDocumentResolver(
            Observation(status, complete, candidates));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(
                NamedDocumentTerminalDecision(status, candidates)),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                originalArguments),
            CancellationToken.None);

        Assert.Equal(
            new[] { "Service Bulletin HX-42.pdf" },
            resolver.RequestedReferences);
        Assert.Empty(executor.ToolNames);
        Assert.Empty(executor.Arguments);
    }

    [Fact]
    public async Task Named_document_ambiguity_returns_a_question_only_clarification()
    {
        var candidates = new[]
        {
            Candidate("doc-a", "Plant-A/Service Bulletin HX-42.pdf"),
            Candidate("doc-b", "Plant-B/Service Bulletin HX-42.pdf")
        };
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Ambiguous,
                complete: true,
                candidates));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(
                NamedDocumentTerminalDecision(
                    SourceBackedDocumentResolutionStatus.Ambiguous,
                    candidates)),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                JsonSerializer.SerializeToElement(new
                {
                    query = "HX-42 inspection interval",
                    topK = 4
                })),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Contains("?", clarification.Message, StringComparison.Ordinal);
        Assert.Equal(2, clarification.Options.Count);
        Assert.Empty(result.EvidenceBundle.Items);
        Assert.Empty(executor.ToolNames);
    }

    [Fact]
    public async Task Named_document_not_found_allows_only_an_explicit_alternative_scope()
    {
        const string disclosure =
            "The requested file was not found; the following facts use other documents.";
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));
        var decision = Completion(Call(
            "catalog-decision",
            "research_named_document_alternatives",
            new
            {
                capability = "rag_search",
                query = "HX-42 inspection interval",
                limit = 4,
                disclosure
            }));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(decision),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                JsonSerializer.SerializeToElement(new
                {
                    query = "original query",
                    topK = 4
                })),
            CancellationToken.None);

        Assert.Equal(
            SourceBackedDocumentScope.AlternativeSources,
            result.Intake.DocumentScope);
        Assert.Equal(disclosure, result.Intake.DocumentScopeDisclosure);
        Assert.Equal("rag.search", Assert.Single(executor.ToolNames));
        Assert.Equal(
            "HX-42 inspection interval",
            Assert.Single(executor.Arguments).GetProperty("query").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_observation.decision_completed"
            && trace.Fields["decision"] == "research_named_document_alternatives"
            && trace.Fields["document_scope"] == "AlternativeSources");
    }

    [Fact]
    public async Task Named_document_inconclusive_allows_one_catalog_retry_then_scopes_original_action()
    {
        var resolver = new SequencedNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Inconclusive,
                complete: false),
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate(
                    "doc-hx-42",
                    "Operations/Service Bulletin HX-42.pdf")));
        var retryDecision = Completion(Call(
            "catalog-decision",
            "retry_named_document_catalog",
            new { }));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(retryDecision),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                JsonSerializer.SerializeToElement(new
                {
                    query = "HX-42 inspection interval",
                    topK = 4
                })),
            CancellationToken.None);

        Assert.Equal(2, resolver.RequestedReferences.Count);
        Assert.Equal(
            "doc-hx-42",
            Assert.Single(executor.Arguments)
                .GetProperty("docId")
                .GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_observation.decision_completed"
            && trace.Fields["decision"] == "retry_catalog_resolved");
    }

    [Fact]
    public async Task Named_document_stale_memory_is_revalidated_against_current_catalog()
    {
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(NamedDocumentTerminalDecision(
                SourceBackedDocumentResolutionStatus.NotFound,
                Array.Empty<SourceBackedDocumentResolutionCandidate>())),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = NamedDocumentIntake(
            "Service Bulletin HX-42.pdf",
            JsonSerializer.SerializeToElement(new
            {
                query = "HX-42 inspection interval",
                docId = "stale-doc-id",
                topK = 4
            })) with
        {
            MemoryContext = new SourceBackedMemoryContext(
                Profile: "default",
                PreferredLanguage: "en",
                PreferredStyle: "concise",
                ActiveMode: "rag",
                PreviousUserMessage: null,
                PreviousAssistantAnswer: null,
                PreviousIntent: null,
                PreviousAnswerSource: null,
                FocusedDocumentId: "stale-doc-id",
                FocusedDocumentPath:
                    "Operations/Service Bulletin HX-42.pdf",
                FocusedDocumentName: "Service Bulletin HX-42.pdf",
                ResolvedCategoryPath: "Operations",
                PendingClarificationKind: null,
                PendingClarificationHint: null,
                PreviousSourceAnchors:
                    Array.Empty<SourceBackedMemorySourceAnchor>(),
                RecentResearchNotes:
                    Array.Empty<SourceBackedMemoryResearchNote>())
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.Single(resolver.RequestedReferences);
        Assert.Empty(executor.ToolNames);
        Assert.Equal(
            SourceBackedDocumentResolutionStatus.NotFound,
            result.Intake.RequestedDocumentResolution?.Status);
    }

    [Fact]
    public async Task Named_document_conflicting_initial_doc_id_is_quarantined()
    {
        var candidate = Candidate(
            "doc-current",
            "Operations/Service Bulletin HX-42.pdf");
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                candidate));
        var executor = new ScriptedToolExecutor(SearchResult());
        var correctionDecision = Completion(Call(
            "catalog-decision",
            "request_named_document_reference_correction",
            new
            {
                identityQuestion =
                    "Which exact document reference should be used?",
                identityCorrectionOptions = new[]
                {
                    "Use the exact current catalog identity",
                    "Provide a different exact document reference"
                },
                executionImpact =
                    "The answer determines which identity can constrain retrieval."
            }));
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(correctionDecision),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                "Service Bulletin HX-42.pdf",
                JsonSerializer.SerializeToElement(new
                {
                    query = "HX-42 inspection interval",
                    docId = "different-doc-id",
                    topK = 4
                })),
            CancellationToken.None);

        Assert.Empty(executor.ToolNames);
        Assert.NotNull(result.Clarification);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_initial_actions.prepared"
            && trace.Fields["reason"] == "resolved_identity_conflict"
            && trace.Fields["quarantined_actions"] == "1");
    }

    [Fact]
    public async Task Request_without_named_document_does_not_resolve_or_change_initial_action()
    {
        const string query = "inspection interval";
        var originalArguments = JsonSerializer.SerializeToElement(new
        {
            query,
            topK = 4
        });
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Inconclusive,
                complete: false));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = Intake("Quel est l'intervalle d'inspection documenté ?") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "router-search",
                    "rag_search",
                    originalArguments,
                    "llm_router")
            },
            InitialSemanticMission = NamedDocumentMission()
        };

        await runner.RunAsync(intake, CancellationToken.None);

        Assert.Empty(resolver.RequestedReferences);
        var executedArguments = Assert.Single(executor.Arguments);
        Assert.Equal(query, executedArguments.GetProperty("query").GetString());
        Assert.Equal(4, executedArguments.GetProperty("topK").GetInt32());
        Assert.False(executedArguments.TryGetProperty("docId", out _));
        Assert.False(executedArguments.TryGetProperty("docPath", out _));
    }

}
