using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task ContextualSuitabilityPolicy_recommendations_and_descriptive_roles_are_nonexclusive_in_writer_and_critic(
        string style, string protocol)
    {
        var request = ContextualPlacementRequest(structured: true);
        var bodies = ContextualPlacementBodies();
        var answer = ContextualPlacementAnswer(request.Handoff.Load);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, answer), EarlyReply(protocol, answer));
        var options = EarlyOptions(protocol, 5);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(bodies), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        string? firstSourceKey = null;
        foreach (var call in factory.Requests.Skip(1))
        {
            var user = WorkspaceUser(call.Body);
            Assert.Equal(request.Handoff.RequestText, user.GetProperty("request").GetString());
            Assert.False(user.TryGetProperty("createdAtUtc", out _));
            var contract = user.GetProperty("selectionContract");
            Assert.Equal(20, contract.GetProperty("requiredClaimCount").GetInt32());
            Assert.False(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
            Assert.True(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
            Assert.False(contract.TryGetProperty("season", out _));
            Assert.False(contract.TryGetProperty("date", out _));
            AssertContextualSuitabilityBoundary(PayloadAuditSystem(call.Body));
            var visible = user.GetProperty("evidence").EnumerateArray()
                .ToDictionary(item => item.GetProperty("evidenceId").GetString()!, item => item);
            Assert.Equal(4, visible.Count);
            foreach (var body in bodies)
            {
                var item = visible[body.Reference.EvidenceId];
                Assert.Equal(body.Content, item.GetProperty("content").GetString());
                Assert.Equal(body.ExactTitle, item.GetProperty("candidateTitle").GetString());
                Assert.False(item.GetProperty("contentTruncated").GetBoolean());
                var key = item.GetProperty("sourceKey").GetString();
                Assert.False(string.IsNullOrEmpty(key));
                firstSourceKey ??= key;
                Assert.Equal(firstSourceKey, key);
            }
        }
        var candidate = WorkspaceUser(factory.Requests[2].Body).GetProperty("candidate");
        Assert.Equal("answered", candidate.GetProperty("outcome").GetString());
        using var expected = JsonDocument.Parse(answer);
        Assert.Equal(expected.RootElement.GetProperty("answerText").GetString(), candidate.GetProperty("answerText").GetString());
        Assert.Equal(20, candidate.GetProperty("claims").GetArrayLength());
        foreach (var claim in result.Claims)
        {
            var index = int.Parse(claim.ClaimId[1..]);
            Assert.Equal($"Option{(char)('A' + (index - 1) % 4)}", claim.SelectedItem);
            Assert.Equal($"E{(index - 1) % 4 + 1}", Assert.Single(claim.EvidenceIds));
        }
        if (style == "contract")
        {
            var writerSystem = SelectionPromptWords(PayloadAuditSystem(factory.Requests[1].Body));
            Assert.DoesNotContain("source role or constraint", writerSystem);
            Assert.Contains("do not contradict an explicit prohibition or binding source condition", writerSystem);
            Assert.Contains("If the user asks what a source itself assigns, classifies or prescribes, that relationship remains a documentary fact and must be explicit in the evidence", writerSystem);
        }
        // Scripted replies prove the transported policy, candidate and citation
        // bindings. They do not prove a real model's semantic placement judgment.
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task ContextualSuitabilityPolicy_explicit_prerequisite_and_prohibition_remain_binding_without_forcing_answer(
        string protocol)
    {
        const string canonical = "Module Alpha\nUtilisation autorisée uniquement avec le prérequis P. Utilisation interdite dans le contexte Y.";
        const string insufficient = """{"outcome":"insufficient_documentation","answerText":"Le module exige P et interdit le contexte Y [C1]. L'utilisation demandée n'est pas établie.","claims":[{"claimId":"C1","selectedItem":"Module Alpha","text":"Le module exige P et interdit le contexte Y.","evidenceIds":["E1"]}]}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"Module Alpha","topK":12}]}"""),
            EarlyReply(protocol, insufficient), EarlyReply(protocol, insufficient));
        var options = EarlyOptions(protocol, 3);
        options.SemanticCriticEnabled = true;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(ContextualPlacementRequest(structured: false),
                new RecordingToolGateway(BuildEvidence("E1", canonical, fileName: "modules.pdf", exactTitle: "Module Alpha")),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        var claim = Assert.Single(result.Claims);
        Assert.Equal("Module Alpha", claim.SelectedItem);
        Assert.Equal("E1", Assert.Single(claim.EvidenceIds));
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        foreach (var call in factory.Requests.Skip(1))
        {
            Assert.Equal(canonical, WorkspaceUser(call.Body).GetProperty("evidence")[0].GetProperty("content").GetString());
            Assert.Contains("Do not turn conditional support into an unconditional assertion", PayloadAuditSystem(call.Body));
        }
        // This invariant also passes with the old policy. Explicit restrictions
        // remain in the model payload and an honest insufficiency is not forced
        // into an answer; no simulated reply certifies semantic understanding.
    }

    [Theory]
    [InlineData("BuildResearchReviewSystemPrompt")]
    [InlineData("BuildWriterRepairSystemPrompt")]
    [InlineData("BuildCriticRepairSystemPrompt")]
    [InlineData("BuildSynthesisRecoverySystemPrompt")]
    public void ContextualSuitabilityPolicy_review_and_repair_compose_the_same_boundary(string method)
    {
        var prompt = (string)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        AssertContextualSuitabilityBoundary(prompt);
    }

    private static void AssertContextualSuitabilityBoundary(string prompt)
    {
        var words = SelectionPromptWords(prompt);
        Assert.Contains("Descriptive categories, example contexts and positive recommendations are not exclusive restrictions unless the source establishes that exclusivity", words);
        Assert.Contains("\"Recommended for X\" does not mean \"only permitted in X\"; it does not by itself prove suitability elsewhere", words);
        Assert.Contains("judge ordinary compatibility from the documented item and the actual request", words);
        Assert.Contains("Do not impose an unstated date, season or other eligibility condition, or require a source label matching SAAIA's proposed placement", words);
        Assert.Contains("Do not turn conditional support into an unconditional assertion", words);
        Assert.Contains("prerequisites, a particular variant or an exception, retain that condition", words);
    }

    private static AdvancedAnalysisProviderRequest ContextualPlacementRequest(bool structured)
        => new(Guid.NewGuid(), Guid.NewGuid(), "test-user", new AdvancedAnalysisHandoffEnvelope
        {
            HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow, Language = "fr",
            RequestText = structured
                ? "Propose une organisation de cinq séances avec quatre créneaux, en réutilisant les modules documentés si utile."
                : "Propose un usage documenté du Module Alpha dans le contexte Y sans le prérequis P, en respectant les contraintes du guide.",
            OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = structured ? "structured_layout" : "single_item", Deliverable = "organisation sourcée",
                AnswerUnitCount = structured ? 20 : 1, AtomicEvidenceCount = structured ? 20 : 1,
                StructuredLayout = structured, RowCount = structured ? 5 : 0, ColumnCount = structured ? 4 : 0,
                RowLabels = structured ? ["S1", "S2", "S3", "S4", "S5"] : [],
                Columns = structured ? ["créneau A", "créneau B", "créneau C", "créneau D"] : [],
                AtomicEvidenceType = structured ? "documented_option" : "documented_fact",
                AtomicEvidenceMode = structured ? "named_item" : "content_claim",
                SelectionPolicy = structured ? "structured_layout" : "single_item"
            },
            ResearchState = new AdvancedAnalysisResearchState { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
        }, [], []);

    private static AdvancedAnalysisResolvedEvidence[] ContextualPlacementBodies()
    {
        var doc = Guid.NewGuid().ToString("D");
        var revision = Guid.NewGuid().ToString("D");
        return Enumerable.Range(1, 4).Select(index => BuildEvidence($"E{index}",
            $"Option{(char)('A' + index - 1)}\nModule autonome documenté avec ses composants et ses étapes d'utilisation. Recommandé pour le contexte X. Rubrique descriptive : démonstrations.",
            fileName: "modules.pdf", docId: doc, revisionId: revision, pageStart: index + 1,
            exactTitle: $"Option{(char)('A' + index - 1)}")).ToArray();
    }

    private static string ContextualPlacementAnswer(AdvancedAnalysisLoadDescriptor load)
        => JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = "Proposition d'organisation SAAIA.\n|Séance|" + string.Join("|", load.Columns)
                + "|\n|---|---|---|---|---|\n" + string.Join("\n", load.RowLabels.Select((row, rowIndex) =>
                    "|" + row + "|" + string.Join("|", Enumerable.Range(0, 4).Select(column =>
                        $"Option{(char)('A' + column)} [C{rowIndex * 4 + column + 1}]")) + "|")),
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}", selectedItem = $"Option{(char)('A' + (index - 1) % 4)}",
                text = $"Option{(char)('A' + (index - 1) % 4)} est documentée.",
                evidenceIds = new[] { $"E{(index - 1) % 4 + 1}" }
            }).ToArray()
        });
}
