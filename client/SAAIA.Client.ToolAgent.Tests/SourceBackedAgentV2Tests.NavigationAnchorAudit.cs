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
    public async Task NavigationAnchorAudit_ExposesOnlyAnchorsAcceptedByTheLlm()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ResolvableNavigationResult(),
            "Trouve une option documentee.");
        var evidenceIds = bundle.Items
            .Where(static item => item.SourceKind == "navigation_map")
            .Select(static item => item.EvidenceId)
            .ToArray();
        Assert.Equal(2, evidenceIds.Length);
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "navigation-anchor-audit",
            new[] { evidenceIds[1] },
            new[] { evidenceIds[0] })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumSemanticCandidatesPerAuditBatch = 6,
                SemanticCandidateAuditEnabled = true
            });
        var auditedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteNavigationAnchorEligibilityAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Trouve une option documentee."),
                "LIVRABLE: une option documentee",
                "option documentee",
                "Le libelle doit nommer une instance autonome du type cible.",
                bundle,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                auditedIds,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var annotatedBundle = ReadPrivateProperty<EvidenceBundle>(
            execution,
            "Bundle");
        Assert.True(ReadPrivateProperty<bool>(execution, "Attempted"));
        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "EligibleCount"));
        Assert.Equal(2, auditedIds.Count);
        Assert.Equal(
            "false",
            annotatedBundle.ById[evidenceIds[0]].SelectionHints[
                "semanticNavigationAnchorEligible"]);
        Assert.Equal(
            "true",
            annotatedBundle.ById[evidenceIds[1]].SelectionHints[
                "semanticNavigationAnchorEligible"]);

        var toolsMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildResearchTransitionTools",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(toolsMethod);
        var tools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(toolsMethod!.Invoke(
            null,
            new object?[]
            {
                annotatedBundle,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<RetrievalRequest>(),
                12
            }));
        var resolve = Assert.Single(
            tools,
            static tool => tool.Name == "resolve_navigation_anchors");
        Assert.Equal(
            new[] { evidenceIds[1] },
            resolve.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }
}
