using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task Named_document_not_found_exposes_only_coherent_transition_tools()
    {
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The complete current catalog contains no exact identity for the requested document."
            })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));

        var result = await runner.RunAsync(
            PortfolioIntake(),
            CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "declare_named_document_insufficiency",
                "request_named_document_reference_correction",
                "research_named_document_alternatives"
            },
            TransitionToolNames(Assert.Single(llm.ToolSets)));
        AssertTransitionSchemaBudget(Assert.Single(llm.ToolSets));
        Assert.True(Assert.Single(llm.RequireToolCalls));
        Assert.InRange(Assert.Single(llm.MaxTokens), 192, 480);
        Assert.Empty(executor.ToolNames);
        Assert.Null(result.Clarification);
        Assert.Empty(result.EvidenceBundle.Items);
    }

    [Fact]
    public async Task Explicit_pdf_not_found_exposes_only_insufficiency_and_names_the_file()
    {
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The complete current catalog contains no exact identity for the requested document."
            })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));
        var intake = PortfolioIntake() with
        {
            NamedReferenceKind = "document"
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal(
            new[] { "declare_named_document_insufficiency" },
            TransitionToolNames(Assert.Single(llm.ToolSets)));
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Null(result.Clarification);
        Assert.Empty(executor.ToolNames);
        var answer = SourceBackedTerminalAnswer.Build(result);
        Assert.Contains(
            "Service Bulletin HX-42.pdf",
            answer,
            StringComparison.Ordinal);
        Assert.Contains("corpus indexé", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_document_not_found_reference_correction_is_source_identity_clarification()
    {
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "request_named_document_reference_correction",
            new
            {
                identityQuestion =
                    "Can you provide the exact current document reference?",
                identityCorrectionOptions = new[]
                {
                    "Provide the exact file name or document identifier",
                    "Provide the complete document path"
                },
                executionImpact =
                    "The corrected identity will be checked against the current catalog."
            })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));

        var result = await runner.RunAsync(
            PortfolioIntake(),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal("source_identity", clarification.AmbiguityKind);
        Assert.Equal(2, clarification.Options.Count);
        Assert.Empty(executor.ToolNames);
    }

    [Fact]
    public async Task Named_document_ambiguity_exposes_selection_and_uses_only_observed_candidates()
    {
        var candidates = new[]
        {
            Candidate("doc-a", "Plant-A/Service Bulletin HX-42.pdf"),
            Candidate("doc-b", "Plant-B/Service Bulletin HX-42.pdf")
        };
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "request_named_document_candidate_selection",
            new
            {
                question = "Which exact current document should be used?",
                executionImpact =
                    "The selected identity will constrain all content retrieval."
            })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.Ambiguous,
                complete: true,
                candidates));

        var result = await runner.RunAsync(
            PortfolioIntake(),
            CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "declare_named_document_insufficiency",
                "request_named_document_candidate_selection",
                "request_named_document_reference_correction",
                "research_named_document_alternatives"
            },
            TransitionToolNames(Assert.Single(llm.ToolSets)));
        AssertTransitionSchemaBudget(Assert.Single(llm.ToolSets));
        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal("source_identity", clarification.AmbiguityKind);
        Assert.Equal(
            candidates.Select(static candidate => candidate.DocPath),
            clarification.Options);
        Assert.Empty(executor.ToolNames);
    }

    [Fact]
    public async Task Named_document_ambiguity_bounds_options_without_losing_exact_count()
    {
        var candidates = Enumerable.Range(1, 25)
            .Select(index => Candidate(
                "doc-" + index,
                "Plant-" + index + "/Service Bulletin HX-42.pdf"))
            .ToArray();
        var observation = Observation(
                SourceBackedDocumentResolutionStatus.Ambiguous,
                complete: true,
                candidates) with
            {
                ExactMatchCount = 25
            };
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "request_named_document_candidate_selection",
            new
            {
                question = "Which exact current document should be used?",
                executionImpact =
                    "The selected identity will constrain all content retrieval."
            })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(llm, executor, observation);

        var result = await runner.RunAsync(
            PortfolioIntake(),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(4, clarification.Options.Count);
        Assert.Equal(
            candidates.Take(4).Select(static candidate => candidate.DocPath),
            clarification.Options);
        Assert.Equal(
            25,
            result.Intake.RequestedDocumentResolution?.ExactMatchCount);
        AssertTransitionSchemaBudget(Assert.Single(llm.ToolSets));
        Assert.Empty(executor.ToolNames);
    }

    [Fact]
    public async Task Named_document_inconclusive_exposes_retry_once_and_recomputes_after_resolution()
    {
        var resolver = new SequencedNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Inconclusive,
                complete: false),
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate(
                    "doc-current",
                    "Operations/Service Bulletin HX-42.pdf")));
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "retry_named_document_catalog",
            new { })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        await runner.RunAsync(PortfolioIntake(), CancellationToken.None);

        var toolNames = TransitionToolNames(Assert.Single(llm.ToolSets));
        AssertTransitionSchemaBudget(Assert.Single(llm.ToolSets));
        Assert.Contains("retry_named_document_catalog", toolNames);
        Assert.DoesNotContain("request_named_document_candidate_selection", toolNames);
        Assert.DoesNotContain("use_resolved_named_document", toolNames);
        Assert.Equal(2, resolver.RequestedReferences.Count);
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal("doc-current", arguments.GetProperty("docId").GetString());
    }

    [Fact]
    public async Task Named_document_second_catalog_retry_is_not_exposed_and_is_repaired()
    {
        var resolver = new SequencedNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.Inconclusive,
                complete: false),
            Observation(
                SourceBackedDocumentResolutionStatus.Inconclusive,
                complete: false));
        var retry = Completion(Call(
            "transition-retry",
            "retry_named_document_catalog",
            new { }));
        var insufficiency = Completion(Call(
            "transition-stop",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The current catalog remains inconclusive after the single permitted retry."
            }));
        var llm = new ScriptedAgentLlm(retry, retry, insufficiency);
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        await runner.RunAsync(PortfolioIntake(), CancellationToken.None);

        Assert.Equal(2, resolver.RequestedReferences.Count);
        Assert.Equal(3, llm.ToolSets.Count);
        Assert.Contains(
            "retry_named_document_catalog",
            TransitionToolNames(llm.ToolSets[0]));
        Assert.DoesNotContain(
            "retry_named_document_catalog",
            TransitionToolNames(llm.ToolSets[1]));
        Assert.DoesNotContain(
            "retry_named_document_catalog",
            TransitionToolNames(llm.ToolSets[2]));
        Assert.Empty(executor.ToolNames);
    }

    [Fact]
    public async Task Named_document_resolved_conflict_can_use_canonical_identity_without_changing_query()
    {
        const string query = "HX-42 inspection interval";
        var llm = new ScriptedAgentLlm(Completion(Call(
            "transition",
            "use_resolved_named_document",
            new { })));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.Resolved,
                complete: true,
                Candidate(
                    "doc-current",
                    "Operations/Service Bulletin HX-42.pdf")));
        var intake = NamedDocumentIntake(
            "Service Bulletin HX-42.pdf",
            JsonSerializer.SerializeToElement(new
            {
                query,
                docId = "doc-stale",
                topK = 4
            }));

        await runner.RunAsync(intake, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "declare_named_document_insufficiency",
                "request_named_document_reference_correction",
                "research_named_document_alternatives",
                "use_resolved_named_document"
            },
            TransitionToolNames(Assert.Single(llm.ToolSets)));
        AssertTransitionSchemaBudget(Assert.Single(llm.ToolSets));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal(query, arguments.GetProperty("query").GetString());
        Assert.Equal(4, arguments.GetProperty("topK").GetInt32());
        Assert.Equal("doc-current", arguments.GetProperty("docId").GetString());
        Assert.Equal(
            "Operations/Service Bulletin HX-42.pdf",
            arguments.GetProperty("docPath").GetString());
    }

    [Fact]
    public async Task Named_document_tool_not_allowed_for_status_is_repaired_with_same_portfolio()
    {
        var invalidRetry = Completion(Call(
            "transition-invalid",
            "retry_named_document_catalog",
            new { }));
        var insufficiency = Completion(Call(
            "transition-valid",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The complete current catalog contains no exact requested identity."
            }));
        var llm = new ScriptedAgentLlm(invalidRetry, insufficiency);
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = NamedDocumentPortfolioRunner(
            llm,
            executor,
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));

        await runner.RunAsync(PortfolioIntake(), CancellationToken.None);

        Assert.Equal(2, llm.ToolSets.Count);
        Assert.Equal(
            TransitionToolNames(llm.ToolSets[0]),
            TransitionToolNames(llm.ToolSets[1]));
        Assert.DoesNotContain(
            "retry_named_document_catalog",
            TransitionToolNames(llm.ToolSets[0]));
        Assert.Empty(executor.ToolNames);
    }

    private static SourceBackedAgentV2Runner NamedDocumentPortfolioRunner(
        ScriptedAgentLlm llm,
        ScriptedToolExecutor executor,
        SourceBackedDocumentResolutionObservation observation)
        => new(
            llm,
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver:
                new RecordingNamedDocumentResolver(observation));

    private static SourceBackedIntake PortfolioIntake()
        => NamedDocumentIntake(
            "Service Bulletin HX-42.pdf",
            JsonSerializer.SerializeToElement(new
            {
                query = "HX-42 inspection interval",
                topK = 4
            }));

    private static string[] TransitionToolNames(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => tools
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static void AssertTransitionSchemaBudget(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        const int legacyMonolithicSchemaCharacters = 1693;
        var actual = tools.Sum(static tool =>
            tool.Parameters.GetRawText().Length);
        Assert.True(
            actual <= legacyMonolithicSchemaCharacters,
            $"Transition schemas use {actual} characters; legacy used "
            + legacyMonolithicSchemaCharacters + ".");
    }
}
