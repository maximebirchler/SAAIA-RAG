using System.Reflection;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task SemanticAnswerTransactionV3_RequiresResolutionFieldsInVersionedContract()
    {
        var (_, llm) = await ExecuteResolutionV3ReviewAsync(
            DirectAnswerV3Json());

        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal(
            "source_backed_semantic_answer_transaction_v3",
            contract.Name);
        var required = contract.Schema.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("requestedDeliverableComplete", required);
        Assert.Contains("missingUserInputPreventsUniqueResult", required);
        Assert.Contains("visibleContextEvidenceId", required);
        Assert.Equal(
            new[] { "E1", "NONE" },
            contract.Schema.GetProperty("properties")
                .GetProperty("visibleContextEvidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_RejectsAnswerWhenLlmDeclaresMissingUserInput()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":false,"missingUserInputPreventsUniqueResult":true,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E1","presentation":"paragraphs","claims":[{"text":"Deux options documentees restent possibles","evidenceIds":["E1"]}],"reason":"Une donnee utilisateur manque pour choisir un resultat unique."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.False(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal(
            "semantic_answer_transaction_resolution_decision_mismatch",
            V3Property<SourceBackedAgentCompletion>(review, "Completion")
                .ProtocolError);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_AcceptsClarifyWhenMissingUserInputPreventsUniqueResult()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":false,"missingUserInputPreventsUniqueResult":true,"visibleContextEvidenceId":"NONE","decision":"clarify","answerAdequacy":"user_clarification_required","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[],"reason":"Quelle variante l'utilisateur souhaite-t-il appliquer ?"}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.True(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal("clarify", V3Property<string>(review, "Decision"));
        Assert.Equal(
            "clarification",
            V3Property<string>(review, "NextCapability"));
        Assert.True(V3Property<bool>(
            review,
            "MissingUserInputPreventsUniqueResult"));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_RejectsResearchWhenLlmDeclaresVisibleContextAnchor()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":false,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"E1","decision":"research","answerAdequacy":"requested_information_missing","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[],"reason":"La preuve visible localise le contexte precis a approfondir."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.False(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal(
            "semantic_answer_transaction_resolution_decision_mismatch",
            V3Property<SourceBackedAgentCompletion>(review, "Completion")
                .ProtocolError);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_AcceptsContextWhenVisibleAnchorMatchesLead()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":false,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"E1","decision":"context","answerAdequacy":"visible_context_required","leadEvidenceId":"E1","presentation":"paragraphs","claims":[],"reason":"La preuve E1 localise le contexte precis a approfondir."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.True(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal("continue", V3Property<string>(review, "Decision"));
        Assert.Equal(
            "documents_context",
            V3Property<string>(review, "NextCapability"));
        Assert.Equal(
            new[] { "E1" },
            V3Property<IReadOnlyList<string>>(review, "LeadEvidenceIds"));
        Assert.Equal(
            "E1",
            V3Property<string>(review, "VisibleContextEvidenceId"));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_RejectsConflictingCompleteAndVisibleContextDeclarations()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"E1","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E1","presentation":"paragraphs","claims":[{"text":"La consigne documentee est disponible","evidenceIds":["E1"]}],"reason":"Le livrable est complet mais une ancre non resolue a aussi ete declaree."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.False(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal(
            "semantic_answer_transaction_resolution_decision_mismatch",
            V3Property<SourceBackedAgentCompletion>(review, "Completion")
                .ProtocolError);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_DoesNotForceClarifyWhenRequestedDeliverableIsComplete()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E1","presentation":"bullets","claims":[{"text":"La premiere variante documentee est disponible","evidenceIds":["E1"]},{"text":"La seconde variante documentee est aussi disponible","evidenceIds":["E1"]}],"reason":"La demande porte explicitement sur les deux variantes et le livrable est complet."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(
            raw,
            "Quelles sont les deux variantes documentees ?");

        Assert.True(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal("ready", V3Property<string>(review, "Decision"));
        Assert.True(V3Property<bool>(review, "RequestedDeliverableComplete"));
        Assert.False(V3Property<bool>(
            review,
            "MissingUserInputPreventsUniqueResult"));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_DoesNotForceContextWhenNoVisibleAnchorExists()
    {
        const string raw =
            """
            {"requestedDeliverableComplete":false,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"research","answerAdequacy":"requested_information_missing","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[],"reason":"Le livrable manque et aucune preuve visible ne localise un contexte a approfondir."}
            """;

        var (review, _) = await ExecuteResolutionV3ReviewAsync(raw);

        Assert.True(V3Property<bool>(review, "ProtocolValid"));
        Assert.Equal("continue", V3Property<string>(review, "Decision"));
        Assert.Equal("research", V3Property<string>(review, "NextCapability"));
        Assert.Equal(
            "NONE",
            V3Property<string>(review, "VisibleContextEvidenceId"));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_PromptAndTraceExposeResolutionDeclarations()
    {
        var llm = new ScriptedAgentLlm(Completion(DirectAnswerV3Json()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentee est directement disponible ?",
                "consigne documentee"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        var system = Assert.Single(llm.Requests)[0].Content ?? string.Empty;
        Assert.Contains("requestedDeliverableComplete", system,
            StringComparison.Ordinal);
        Assert.Contains("missingUserInputPreventsUniqueResult", system,
            StringComparison.Ordinal);
        Assert.Contains("visibleContextEvidenceId", system,
            StringComparison.Ordinal);
        Assert.Contains("livrable reellement demande", system,
            StringComparison.Ordinal);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal("true", trace.Fields["requested_deliverable_complete"]);
        Assert.Equal(
            "false",
            trace.Fields["missing_user_input_prevents_unique_result"]);
        Assert.Equal("NONE", trace.Fields["visible_context_evidence_id"]);
    }

    private static async Task<(object Review, ScriptedAgentLlm Llm)>
        ExecuteResolutionV3ReviewAsync(
            string rawOutput,
            string question = "Quelle consigne documentee est disponible ?")
    {
        var llm = new ScriptedAgentLlm(Completion(rawOutput));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            SearchResult(),
            question);
        var intake = SemanticTransactionIntake(question, "consigne documentee");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewSemanticAnswerTransactionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                intake,
                "LIVRABLE: reponse documentaire exacte",
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                6,
                CancellationToken.None
            }));
        await task;
        return (task.GetType().GetProperty("Result")!.GetValue(task)!, llm);
    }

    private static T V3Property<T>(object instance, string propertyName)
        => (T)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static string DirectAnswerV3Json()
        =>
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E1","presentation":"paragraphs","claims":[{"text":"La consigne documentee est directement disponible","evidenceIds":["E1"]}],"reason":"Le pool permet de produire le livrable demande."}
            """;
}
