using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task CandidateAudit_UsesOneBatchedDecisionForAllCandidates()
    {
        var cards = Enumerable.Range(1, 48)
            .Select(static index => new MealCard(
                "Documented option " + index,
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Give me documented options.");
        var approvedIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "candidate-audit-batch",
            approvedIds,
            Array.Empty<string>())));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumSemanticCandidatesPerAuditTurn = 48,
                MaximumSemanticCandidatesPerAuditBatch = 48
            });
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            runner,
            new object?[]
            {
                Intake("Give me documented options."),
                "LIVRABLE: documented options",
                "named documented option",
                "Le libelle exact nomme une instance autonome du type cible.",
                "",
                Array.Empty<string>(),
                Array.Empty<string>(),
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            0,
            ReadPrivateProperty<int>(execution, "LabelReviewLlmCallCount"));
        Assert.Equal(48, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Empty(Assert.Single(llm.ToolSets));
        var auditContract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal(
            "source_backed_candidate_batch_audit_v5",
            auditContract.Name);
        var decisionsSchema = auditContract.Schema
            .GetProperty("properties")
            .GetProperty("decisions");
        Assert.Equal("array", decisionsSchema.GetProperty("type").GetString());
        Assert.Equal(48, decisionsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(48, decisionsSchema.GetProperty("maxItems").GetInt32());
        var decisionItemSchema = decisionsSchema.GetProperty("items");
        Assert.Equal("integer", decisionItemSchema.GetProperty("type").GetString());
        Assert.Equal(0, decisionItemSchema.GetProperty("minimum").GetInt32());
        Assert.Equal(1, decisionItemSchema.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task CandidateAudit_ClassifiesApprovedCandidatesOneSemanticColumnAtATime()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance A2", 2),
                new MealCard("Instance A3", 3),
                new MealCard("Instance A4", 4),
                new MealCard("Instance B1", 5),
                new MealCard("Instance B2", 6),
                new MealCard("Instance B3", 7),
                new MealCard("Instance B4", 8)
            }),
            "Construis une grille generique.");
        var approvedIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "candidate-audit-columns-1",
                approvedIds.Take(6).ToArray(),
                Array.Empty<string>())),
            Completion(CandidateAuditCall(
                "candidate-audit-columns-2",
                approvedIds.Skip(6).ToArray(),
                Array.Empty<string>())))
        {
            CandidateColumnCompatibilitySelector = (role, ids) =>
                string.Equals(role, "Option A", StringComparison.Ordinal)
                    ? ids.Where(static id =>
                        int.Parse(id.AsSpan(1),
                            System.Globalization.CultureInfo.InvariantCulture)
                        <= 4).ToArray()
                    : ids.Where(static id =>
                        int.Parse(id.AsSpan(1),
                            System.Globalization.CultureInfo.InvariantCulture)
                        > 4).ToArray()
        };
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var intake = Intake("Construis une grille generique.") with
        {
            CanonicalColumnSemanticRoles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Option A"] = "Premiere famille semantique.",
                    ["Option B"] = "Deuxieme famille semantique."
                }
        };
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                intake,
                "LIVRABLE: grille generique",
                "instance documentee nommee",
                "Le libelle nomme une instance autonome.",
                "Periode",
                new[] { "Premiere", "Deuxieme" },
                new[] { "Option A", "Option B" },
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.True(ReadPrivateProperty<bool>(
            execution,
            "ColumnCompatibilityApplied"));
        Assert.True(ReadPrivateProperty<bool>(
            execution,
            "ColumnCompatibilityProtocolValid"));
        Assert.Equal(4, ReadPrivateProperty<int>(
            execution,
            "ColumnCompatibilityLlmCallCount"));
        Assert.Equal(6, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            4,
            llm.StructuredOutputContracts.Count(static contract =>
                contract.Name
                == "source_backed_candidate_column_compatibility_v1"));

        var decision = execution.GetType()
            .GetProperty("Decision")!
            .GetValue(execution)!;
        var approvals = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                decision.GetType().GetProperty("ApprovedCandidates")!.GetValue(decision))
            .Cast<object>()
            .ToArray();
        var compatibilityByEvidenceId = approvals.ToDictionary(
            approval => (string)approval.GetType()
                .GetProperty("EvidenceId")!
                .GetValue(approval)!,
            approval => ((IEnumerable<string>)approval.GetType()
                    .GetProperty("CompatibleColumnLabels")!
                    .GetValue(approval)!)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E1"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E2"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E3"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E4"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E5"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E6"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E7"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E8"]);
    }

    [Fact]
    public async Task CandidateAudit_MechanicallyRejectsAnApprovedValueThatIsExactlyALayoutAxis()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Breakfast", 1),
                new MealCard("Concrete recipe", 2)
            }),
            "Build a two-column plan.");
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "candidate-audit-axis-identity",
            new[] { "E1", "E2" },
            Array.Empty<string>())));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Build a two-column plan."),
                "LIVRABLE: a two-column plan",
                "named recipe",
                "The exact label names one concrete recipe.",
                "Day",
                new[] { "Monday" },
                new[] { "Breakfast", "Dinner" },
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.DoesNotContain("E1=", completion.Content, StringComparison.Ordinal);
        Assert.Contains("E2=Concrete recipe", completion.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateAudit_RecognizesDocumentIdentityAsNonContentEvidence()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityOnlySearchResult(),
            "Résume ANSI B11.0-2023 en sept exigences utiles.");
        var item = Assert.Single(bundle.Items);

        Assert.True(
            SourceBackedAgentV2Runner
                .IsDocumentIdentityOnlyContentCandidateForTests(
                    "content_claim",
                    item,
                    "B11.0-2023"));
        Assert.False(
            SourceBackedAgentV2Runner
                .IsDocumentIdentityOnlyContentCandidateForTests(
                    "named_item",
                    item,
                    "B11.0-2023"));
    }

    [Fact]
    public async Task CandidateAudit_BatchCanApproveAndRejectInOneSemanticDecision()
    {
        var cards = new[]
        {
            new MealCard("Concrete option A", 1),
            new MealCard("Generic collection", 2),
            new MealCard("Concrete option B", 3),
            new MealCard("Schedule position", 4)
        };
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Give me concrete documented options.");
        var llm = new ScriptedAgentLlm(Completion(
            "{\"decisions\":[1,0,1,0]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal("E1=Concrete option A\r\nE3=Concrete option B", completion.Content);
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(4, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Equal(0, ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.All(llm.ToolSets, Assert.Empty);
        Assert.All(llm.RequireToolCalls, Assert.False);
        Assert.All(llm.StructuredOutputContracts, static contract => Assert.Equal(
            "source_backed_candidate_batch_audit_v5",
            contract.Name));
        Assert.All(llm.Temperatures, static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CandidateAudit_UsesCompactOrderedSemanticClassificationsForCanonicalTitles()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option", 1),
                new MealCard("Generic collection", 2)
            }),
            "Give me concrete documented options.");
        var llm = new ScriptedAgentLlm(Completion(
            "{\"decisions\":[\"accept\",\"reject_category\"]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                SemanticCandidateLabelResolutionEnabled = true
            });

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items,
            semanticPlan:
                "LIVRABLE: concrete documented options\n"
                + "PREUVES_ATOMIQUES: concrete documented options",
            candidateObjectType: string.Empty,
            candidateEligibilityRule: string.Empty);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal("E1=Concrete option", completion.Content);
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(0, ReadPrivateProperty<int>(
            execution,
            "LabelReviewLlmCallCount"));
        Assert.Contains(
            "TYPE ATOMIQUE DECIDE PAR LE LLM: concrete documented options",
            RequestText(llm, 0));
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal("source_backed_resolved_candidate_audit_v5", contract.Name);
        var decisions = contract.Schema
            .GetProperty("properties")
            .GetProperty("decisions");
        Assert.Equal("array", decisions.GetProperty("type").GetString());
        Assert.Equal(2, decisions.GetProperty("minItems").GetInt32());
        Assert.Equal(2, decisions.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            5,
            decisions.GetProperty("items").GetProperty("enum").GetArrayLength());
    }

    [Fact]
    public async Task CandidateAudit_UnresolvedNeighbourDoesNotDowngradeCanonicalTitleBatch()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option A", 1),
                new MealCard("Concrete option B", 2),
                new MealCard("Unresolved neighbour", 3)
            }),
            "Give me concrete documented options.");
        var mixedCandidates = bundle.Items
            .Select((item, index) => index != 2
                ? item
                : item with
                {
                    SelectionHints = item.SelectionHints
                        .Where(static pair => !string.Equals(
                            pair.Key,
                            "sourceAnchorLabel",
                            StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase)
                })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion("{\"decisions\":[\"accept\",\"accept\"]}"),
            Completion("{\"decisions\":[0]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                SemanticCandidateLabelResolutionEnabled = true
            });

        var execution = await InvokeCandidateAuditAsync(runner, mixedCandidates);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal(
            "E1=Concrete option A\r\nE2=Concrete option B",
            completion.Content);
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(3, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Equal(
            new[]
            {
                "source_backed_resolved_candidate_audit_v5",
                "source_backed_candidate_batch_audit_v5"
            },
            llm.StructuredOutputContracts.Select(static contract => contract.Name));
    }

    [Fact]
    public async Task CandidateAudit_RechecksIndirectLabelsWithoutChangingSemanticOwnership()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard(
                    "Concrete option A",
                    1,
                    "Generic collection > Concrete option A"),
                new MealCard(
                    "Concrete option B",
                    2,
                    "Generic collection > Concrete option B")
            }),
            "Give me concrete documented options.");
        var unresolvedCandidates = bundle.Items
            .Select(static item => item with
            {
                SelectionHints = item.SelectionHints
                    .Where(static pair => !string.Equals(
                        pair.Key,
                        "sourceAnchorLabel",
                        StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "{\"labelIndexesByCandidateKey\":{" +
                "\"c1\":0,\"c2\":0}}"),
            Completion(
                "{\"decisionsByCandidateKey\":{" +
                "\"c1\":\"accept::Concrete option A\"," +
                "\"c2\":\"accept::Concrete option B\"}}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            unresolvedCandidates);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal(
            "E1=Concrete option A\r\nE2=Concrete option B",
            completion.Content);
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            1,
            ReadPrivateProperty<int>(execution, "LabelReviewLlmCallCount"));
        Assert.Equal(
            "source_backed_candidate_parent_review_v2",
            llm.StructuredOutputContracts[1].Name);
        Assert.Equal(
            0,
            ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.Contains(
            "CANDIDATS AUX LIBELLES DIRECTS OU PARENTS A REVERIFIER",
            RequestText(llm, 1));
        Assert.Contains("du plus direct au plus ancestral", RequestText(llm, 1));
    }

    [Fact]
    public async Task CandidateAudit_BatchFailsClosedAfterOneProtocolRepair()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option", 1)
            }),
            "Give me one concrete documented option.");
        var llm = new PromptAwareCandidateAuditLlm(
            static _ => Completion("reponse hors protocole"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var decision = ReadPrivateProperty<object>(execution, "Decision");

        Assert.Equal("protocol_error", completion.FinishReason);
        Assert.False(ReadPrivateProperty<bool>(decision, "ProtocolValid"));
        Assert.Equal(
            "candidate_batch_audit_structured_object_required",
            ReadPrivateProperty<string>(decision, "FailureReason"));
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.Contains("REPARATION DU CONTRAT", RequestText(llm, 1));
    }
}
