using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public void SemanticResolutionV6Contract_RequiresExplicitLeadEvidenceIds()
    {
        var contract = BuildResolutionContract("E1", "E2");
        var schema = contract.Schema;

        Assert.Equal("source_backed_semantic_resolution_v6", contract.Name);
        Assert.Equal(
            new[] { "decision", "evidenceIds", "leadEvidenceIds", "reason" },
            schema.GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            new[] { "decision", "evidenceIds", "leadEvidenceIds", "reason" },
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void SemanticResolutionV6Contract_RestrictsBothEvidenceCollectionsToPresentedPool()
    {
        var properties = BuildResolutionContract("E1", "E3")
            .Schema
            .GetProperty("properties");

        foreach (var propertyName in new[] { "evidenceIds", "leadEvidenceIds" })
        {
            var collection = properties.GetProperty(propertyName);
            Assert.Equal(0, collection.GetProperty("minItems").GetInt32());
            Assert.Equal(2, collection.GetProperty("maxItems").GetInt32());
            Assert.True(collection.GetProperty("uniqueItems").GetBoolean());
            Assert.Equal(
                new[] { "E1", "E3" },
                collection.GetProperty("items")
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
                    .ToArray());
        }
    }

    [Fact]
    public async Task SemanticResolutionV6Prompt_DefinesIndependentSelectionAndLeadRoles()
    {
        var prompt = await CaptureSemanticResolutionPolicyPromptAsync();

        Assert.Contains("SELECTION_COMPLETE_ET_LEADS:", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "evidenceIds = ensemble complet des preuves necessaires",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "leadEvidenceIds = sous-ensemble ordonne des preuves principales",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Le code n'infere jamais un lead",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_AnswerAcceptsExplicitLeadAndWriterReceivesRoles()
    {
        const string assessment =
            "E2 porte la décision principale et E1 apporte le support nécessaire.";
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(V6Resolution(
                    "answer",
                    new[] { "E1", "E2" },
                    new[] { "E2" },
                    assessment)),
                Writer(
                    "La décision principale vient de E2 et la condition de support de E1.",
                    "E2",
                    "E1"),
                SemanticReview("accept", "Les deux faits sont directement soutenus.")
            },
            TwoEvidenceResult(),
            null,
            "Quelle condition documentée doit être retenue ?",
            "condition documentée");

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(
            new[]
            {
                "source_backed_semantic_resolution_v6",
                "source_backed_flat_writer_v1"
            },
            llm.StructuredOutputContracts.Select(static contract => contract.Name).ToArray());
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("E2,E1", resolution.Fields["selected_evidence_ids"]);
        Assert.Equal("E2", resolution.Fields["lead_evidence_ids"]);
        var handoff = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.writer_handoff.completed");
        Assert.Equal("E2,E1", handoff.Fields["selected_evidence_ids"]);
        Assert.Equal("E2", handoff.Fields["lead_evidence_ids"]);

        var writerPrompt = string.Join(
            "\n",
            llm.Requests[1].Select(static message => message.Content));
        Assert.Contains(
            "EVIDENCEIDS_SELECTIONNES_PAR_LE_JUGE: E2,E1",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "LEADEVIDENCEIDS_PRINCIPAUX_PAR_LE_JUGE: E2",
            writerPrompt,
            StringComparison.Ordinal);
        var leadBlock = writerPrompt.IndexOf("[E2]", StringComparison.Ordinal);
        var supportBlock = writerPrompt.IndexOf("[E1]", StringComparison.Ordinal);
        Assert.True(leadBlock >= 0 && supportBlock > leadBlock, writerPrompt);
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_AnswerPreservesOrderedMultipleCoLeads()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(V6Resolution(
                    "answer",
                    new[] { "E1", "E2", "E3" },
                    new[] { "E3", "E2" },
                    "E3 et E2 sont co-principales; E1 complète la preuve.")),
                Writer(
                    "E3 et E2 établissent conjointement le résultat, complété par E1.",
                    "E3",
                    "E2",
                    "E1"),
                SemanticReview("accept", "Les co-leads et le support sont tous cités.")
            },
            ThreeSameWindowEvidenceSearchResult(),
            null,
            "Quel résultat ressort de la comparaison documentée ?",
            "comparaison documentée");

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("E3,E2,E1", resolution.Fields["selected_evidence_ids"]);
        Assert.Equal("E3,E2", resolution.Fields["lead_evidence_ids"]);
        var writerPrompt = string.Join(
            "\n",
            llm.Requests[1].Select(static message => message.Content));
        Assert.Contains(
            "LEADEVIDENCEIDS_PRINCIPAUX_PAR_LE_JUGE: E3,E2",
            writerPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_AnswerRejectsEmptyLeadList()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "answer",
                new[] { "E1" },
                Array.Empty<string>(),
                "La preuve répond, mais aucun lead n'est déclaré."),
            "semantic_resolution_resolution_decision_mismatch");

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsLeadOutsidePresentedPool()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "answer",
                new[] { "E1" },
                new[] { "E9" },
                "Le lead doit appartenir au pool présenté."),
            "semantic_resolution_lead_evidence_id_invalid");

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsDuplicateLeadIds()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "answer",
                new[] { "E1" },
                new[] { "E1", "E1" },
                "Chaque lead doit être unique."),
            "semantic_resolution_lead_evidence_id_invalid");

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsAnswerLeadOutsideSelection()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "answer",
                new[] { "E1" },
                new[] { "E2" },
                "Le lead présenté n'appartient pas à la sélection complète."),
            "semantic_resolution_lead_evidence_not_selected",
            TwoEvidenceResult());

    [Fact]
    public async Task SemanticResolutionV6Parser_ContextAcceptsExactlyMatchingLead()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(V6Resolution(
                    "context",
                    new[] { "E1" },
                    new[] { "E1" },
                    "Le contexte complet autour de cette ancre est requis."))
            },
            null,
            V2Options() with { MaximumSemanticCorrectionTurns = 0 },
            null,
            null);

        Assert.False(result.IsSourceVerified);
        Assert.Single(llm.Requests);
        Assert.Equal(
            "source_backed_semantic_resolution_v6",
            Assert.Single(llm.StructuredOutputContracts).Name);
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("true", resolution.Fields["protocol_valid"]);
        Assert.Equal("E1", resolution.Fields["selected_evidence_ids"]);
        Assert.Equal("E1", resolution.Fields["lead_evidence_ids"]);
        Assert.Equal("E1", resolution.Fields["visible_context_evidence_id"]);
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_ContextRejectsDifferentLead()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "context",
                new[] { "E1" },
                new[] { "E2" },
                "Une seule et même ancre doit être déclarée."),
            "semantic_resolution_resolution_decision_mismatch",
            TwoEvidenceResult());

    [Fact]
    public async Task SemanticResolutionV6Parser_ResearchAcceptsBothListsEmpty()
        => await AssertV6NonAnswerAcceptedAsync(
            "research",
            "requested_information_missing",
            "Une recherche documentaire complémentaire est requise.");

    [Fact]
    public async Task SemanticResolutionV6Parser_ClarifyAcceptsBothListsEmpty()
        => await AssertV6NonAnswerAcceptedAsync(
            "clarify",
            "user_clarification_required",
            "Quelle variante exacte souhaitez-vous examiner ?");

    [Fact]
    public async Task SemanticResolutionV6Parser_ResearchRejectsNonEmptyLeadList()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "research",
                Array.Empty<string>(),
                new[] { "E1" },
                "Aucun lead ne peut être déclaré avant la recherche."),
            "semantic_resolution_resolution_decision_mismatch");

    [Fact]
    public async Task SemanticResolutionV6Parser_ClarifyRejectsNonEmptyLeadList()
        => await AssertV6ProtocolErrorAsync(
            V6Resolution(
                "clarify",
                Array.Empty<string>(),
                new[] { "E1" },
                "Aucun lead ne peut être déclaré avant clarification."),
            "semantic_resolution_resolution_decision_mismatch");

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsLegacyPayloadWithoutLeadEvidenceIds()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            ResolutionAnswer("E1"),
            Writer("La consigne impose un serrage en croix à 85 N.m", "E1"),
            SemanticReview("accept", "Le claim est directement soutenu."));

        Assert.False(result.IsSourceVerified);
        Assert.Single(llm.Requests);
        var trace = Assert.Single(result.TraceEvents, static item =>
            item.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("false", trace.Fields["protocol_valid"]);
        Assert.Equal(
            "semantic_resolution_root_contract_invalid",
            trace.Fields["protocol_error"]);
    }

    private static string V6Resolution(
        string decision,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<string> leadEvidenceIds,
        string reason)
        => JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds,
            leadEvidenceIds,
            reason
        });

    private static async Task AssertV6ProtocolErrorAsync(
        string payload,
        string expectedError,
        ToolResults? evidence = null)
    {
        var result = await RunV2Async(payload, evidence);

        Assert.False(result.IsSourceVerified);
        var trace = Assert.Single(result.TraceEvents, static item =>
            item.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("false", trace.Fields["protocol_valid"]);
        Assert.Equal(expectedError, trace.Fields["protocol_error"]);
    }

    private static async Task AssertV6NonAnswerAcceptedAsync(
        string decision,
        string expectedAdequacy,
        string reason)
    {
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(V6Resolution(
                    decision,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    reason))
            },
            null,
            V2Options() with { MaximumSemanticCorrectionTurns = 0 },
            null,
            null);

        Assert.False(result.IsSourceVerified);
        Assert.Single(llm.Requests);
        Assert.Equal(
            "source_backed_semantic_resolution_v6",
            Assert.Single(llm.StructuredOutputContracts).Name);
        var trace = Assert.Single(result.TraceEvents, static item =>
            item.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("true", trace.Fields["protocol_valid"]);
        Assert.Equal(expectedAdequacy, trace.Fields["answer_adequacy"]);
        Assert.Equal(string.Empty, trace.Fields["selected_evidence_ids"]);
        Assert.Equal(string.Empty, trace.Fields["lead_evidence_ids"]);
    }
}
