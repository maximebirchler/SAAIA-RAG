using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Partial_technical_selection_keeps_incremental_inventory_and_canonical_proof_under_pressure(string protocol)
    {
        const int cap = 12_000;
        const string partial = """
            {"outcome":"insufficient_documentation",
             "answerText":"Sélection partielle. Le Capteur Alpha mesure la pression [C1]. Le Capteur Beta mesure la température [C2]. Les deux autres choix restent à établir.",
             "claims":[
               {"claimId":"C1","selectedItem":"Capteur Alpha","text":"Le Capteur Alpha mesure la pression.","evidenceIds":["E1"]},
               {"claimId":"C2","selectedItem":"Capteur Beta","text":"Le Capteur Beta mesure la température.","evidenceIds":["E2"]}]}
            """;
        var docId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        AdvancedAnalysisResolvedEvidence Body(string id, string content, string? title = null)
            => BuildEvidence(id, content, "sensors.pdf", docId, revisionId, exactTitle: title);
        var first = Body("E1", "Capteur Alpha\nCe capteur mesure la pression. Plage de mesure de 0 à 20 bar.", "Capteur Alpha");
        var second = Body("E2", "Capteur Beta\nCe capteur mesure la température. Plage de mesure de 0 à 80 degrés Celsius.", "Capteur Beta");
        var noise = Enumerable.Range(3, 24).Select(i => Body("E" + i,
            "Conditions générales de maintenance. " + new string('x', 2_300))).ToArray();
        var gateway = new SequencedToolGateway([first], new[] { second }.Concat(noise).ToArray());
        var ordinal = 0;
        var prompts = new List<JsonElement>();
        using var factory = new OmittedHandleHttpFactory(async httpRequest =>
        {
            var wire = await httpRequest.Content!.ReadAsStringAsync();
            var user = WorkspaceUser(wire);
            prompts.Add(user);
            if (ordinal > 0)
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, cap);
            var response = ordinal++ switch
            {
                0 => Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"capteurs de mesure","topK":12}]}"""),
                1 => Batch(
                    ("save_candidate_inventory", CandidateUpdate("alpha", "Capteur Alpha", "internal-source-1", "pression", "selected", [], ["E1"])),
                    ("search_corpus", """{"query":"capteur température","sourceKey":"","category":"","documentHint":"","topK":30}""")),
                2 => Batch(("save_candidate_inventory",
                    CandidateUpdate("beta", "Capteur Beta", "internal-source-1", "température", "selected", [], ["E2"]))),
                3 or 4 => Completion(partial),
                _ => throw new InvalidOperationException("Unexpected provider call.")
            };
            return await AdaptProtocolAgnosticFixtureAsync(httpRequest, response, CancellationToken.None);
        });
        System.Net.Http.HttpResponseMessage Batch(params (string Name, string Arguments)[] calls)
            => protocol == "responses" ? NativeCorrelationResponseFunctions(calls) : NativeCompletion(calls);
        var options = EarlyOptions(protocol, 5);
        options.NativeResearchWorkspaceEnabled = true;
        options.SemanticCriticEnabled = true;
        options.SynthesisPromptStyle = "contract";
        options.MaximumEvidencePromptCharacters = cap;
        var request = new AdvancedAnalysisProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Propose quatre capteurs distincts, un de pression et un de température pour chaque poste.",
                Language = "fr", OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "bounded_grid", Deliverable = "sélection de capteurs", AnswerUnitCount = 4,
                    AtomicEvidenceCount = 4, StructuredLayout = true, RowCount = 2, ColumnCount = 2,
                    RowLabels = ["poste A", "poste B"], Columns = ["pression", "température"],
                    AtomicEvidenceType = "documented_sensor", AtomicEvidenceMode = "named_item", SelectionPolicy = "distinct_structured_layout"
                },
                ResearchState = new AdvancedAnalysisResearchState { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
            }, [], []);

        AdvancedAnalysisProviderResult result;
        try
        {
            result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(request, gateway, CancellationToken.None);
        }
        catch (AdvancedAnalysisProviderException error)
        {
            throw new InvalidOperationException($"After {ordinal} calls: {error.ErrorCode}; documentary wire {error.Data["documentaryWireCharacters"]}, cap {error.Data["maximumDocumentaryWireCharacters"]}.", error);
        }

        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        Assert.True(gateway.Evidence.Count > prompts[3].GetProperty("evidence").GetArrayLength());
        var remembered = prompts[3].GetProperty("candidateInventory").GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(remembered, item => item.GetProperty("exactTitle").GetString() == "Capteur Alpha"
            && item.GetProperty("status").GetString() == "selected");
        Assert.Contains(remembered, item => item.GetProperty("exactTitle").GetString() == "Capteur Beta"
            && item.GetProperty("status").GetString() == "selected");
        foreach (var prompt in prompts.Skip(3))
        {
            var evidence = prompt.GetProperty("evidence").EnumerateArray().ToArray();
            foreach (var body in new[] { first, second })
                Assert.Equal(body.Content, Assert.Single(evidence, item =>
                    item.GetProperty("evidenceId").GetString() == body.Reference.EvidenceId).GetProperty("content").GetString());
        }
        Assert.Equal(2, prompts[4].GetProperty("candidate").GetProperty("claims").GetArrayLength());
        var validation = AdvancedAnalysisResultValidator.ValidateAndBuild("fake-internal", result, gateway.Evidence, 10, DateTimeOffset.UtcNow);
        Assert.True(validation.IsValid, validation.ErrorCode);
        Assert.Equal(new[] { "E1", "E2" }, validation.Result!.Evidence.Select(item => item.EvidenceId));
    }
}
