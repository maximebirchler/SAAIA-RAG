using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string CandidateTerminal =
        """{"outcome":"insufficient_documentation","answerText":"D'autres corps restent à vérifier.","claims":[]}""";
    private const string CandidatePlanner =
        """{"selectionMode":"distinct_named_items","queries":[{"query":"recettes petit-déjeuner","topK":12}]}""";

    private static string CandidateAnswered(int count)
        => JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(", ", Enumerable.Range(1, count)
                .Select(index => $"R{index} [C{index}]")) + ".",
            claims = Enumerable.Range(1, count).Select(index => new
            {
                claimId = $"C{index}",
                selectedItem = $"R{index}",
                text = $"R{index} est documenté.",
                evidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });

    private static string CandidateUpdate(
        string key,
        string title,
        string sourceKey,
        string role,
        string status,
        string[] locatorEvidenceIds,
        string[] bodyEvidenceIds)
        => JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key,
                    exactTitle = title,
                    sourceKey,
                    targetRoles = new[] { role },
                    selectedRoles = status == "selected" ? new[] { role } : [],
                    status,
                    note = "Observed candidate retained across focus changes.",
                    locatorEvidenceIds,
                    bodyEvidenceIds
                }
            }
        });

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Structured_named_item_inventory_keeps_verified_body_and_reports_coverage(
        string protocol)
    {
        var update = CandidateUpdate(
            "omelette",
            "Omelette",
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E1"]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            protocol == "responses"
                ? NativeResponseFunction("save_candidate_inventory", update)
                : NativeCompletion(("save_candidate_inventory", update)),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateTerminal)
                : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        var evidence = BuildEvidence(
            "E1",
            "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre les œufs puis cuire l'omelette.",
            exactTitle: "Omelette");

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var inventory = prompt.GetProperty("candidateInventory");
        Assert.Equal("candidate-inventory.v1", inventory.GetProperty("version").GetString());
        var item = inventory.GetProperty("items")[0];
        Assert.Equal("Omelette", item.GetProperty("exactTitle").GetString());
        Assert.Equal("body_verified", item.GetProperty("status").GetString());
        Assert.Equal("E1", item.GetProperty("bodyEvidenceIds")[0].GetString());
        var breakfast = inventory.GetProperty("coverage").EnumerateArray()
            .Single(entry => entry.GetProperty("targetRole").GetString() == "petit-déjeuner");
        Assert.Equal(5, breakfast.GetProperty("requiredCount").GetInt32());
        Assert.Equal(1, breakfast.GetProperty("bodyVerifiedCount").GetInt32());

        using var request = JsonDocument.Parse(factory.Requests[1].Body);
        Assert.Contains(
            request.RootElement.GetProperty("tools").EnumerateArray(),
            tool => (tool.TryGetProperty("function", out var function)
                    ? function.GetProperty("name").GetString()
                    : tool.GetProperty("name").GetString())
                == "save_candidate_inventory");
    }

    [Fact]
    public async Task Candidate_inventory_upserts_without_dropping_prior_verified_items()
    {
        var first = CandidateUpdate(
            "omelette",
            "Omelette",
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E1"]);
        var second = CandidateUpdate(
            "porridge",
            "Porridge",
            "internal-source-2",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E2"]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(
                ("save_candidate_inventory", first),
                ("search_corpus", WorkspaceSearch)),
            NativeCompletion(("save_candidate_inventory", second)),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 8_000;
        var firstDoc = Guid.NewGuid().ToString("D");
        var firstRevision = Guid.NewGuid().ToString("D");
        var secondDoc = Guid.NewGuid().ToString("D");
        var secondRevision = Guid.NewGuid().ToString("D");
        var initial = BuildEvidence(
            "E1",
            "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre puis cuire.",
            docId: firstDoc,
            revisionId: firstRevision,
            exactTitle: "Omelette");
        var newcomer = BuildEvidence(
            "E2",
            "PORRIDGE\nINGRÉDIENTS\nAvoine et lait. PRÉPARATION\nCuire doucement.",
            fileName: "porridge.pdf",
            docId: secondDoc,
            revisionId: secondRevision,
            exactTitle: "Porridge");

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new SequencedToolGateway([initial], [newcomer]),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        var inventory = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateInventory");
        var items = inventory.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.GetProperty("exactTitle").GetString() == "Omelette"
            && item.GetProperty("bodyEvidenceIds")[0].GetString() == "E1");
        Assert.Contains(items, item => item.GetProperty("exactTitle").GetString() == "Porridge"
            && item.GetProperty("bodyEvidenceIds")[0].GetString() == "E2");
        Assert.Equal(
            2,
            inventory.GetProperty("coverage")[0]
                .GetProperty("bodyVerifiedCount")
                .GetInt32());
    }

    [Fact]
    public async Task Exact_source_title_is_automatically_retained_and_advanced_without_inventory_tool_call()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("search_corpus", WorkspaceSearch)),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        var docId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var locator = BuildEvidence(
            "E-NAV",
            "SOMMAIRE\nOmelette ........ 12\nPorridge ........ 14\nCrêpes ........ 16",
            docId: docId,
            revisionId: revisionId,
            exactTitle: "Omelette");
        var body = BuildEvidence(
            "E-BODY",
            "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre puis cuire.",
            docId: docId,
            revisionId: revisionId,
            exactTitle: "Omelette");

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new SequencedToolGateway([locator], [body]),
                CancellationToken.None);

        var item = WorkspaceUser(factory.Requests[2].Body)
            .GetProperty("candidateInventory")
            .GetProperty("items")[0];
        Assert.StartsWith("candidate-", item.GetProperty("key").GetString());
        Assert.Equal("Omelette", item.GetProperty("exactTitle").GetString());
        Assert.Equal("internal-source-1", item.GetProperty("sourceKey").GetString());
        Assert.Equal("body_verified", item.GetProperty("status").GetString());
        Assert.Equal("E-NAV", item.GetProperty("locatorEvidenceIds")[0].GetString());
        Assert.Equal("E-BODY", item.GetProperty("bodyEvidenceIds")[0].GetString());
    }

    [Fact]
    public async Task Writer_selections_are_reflected_in_inventory_seen_by_critic()
    {
        var answered = CandidateAnswered(4);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(answered),
            Completion(answered));
        var options = WorkspaceOptions();
        options.SemanticCriticEnabled = true;
        var evidence = Enumerable.Range(1, 4).Select(index => BuildEvidence(
            $"E{index}",
            $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nPréparer puis servir.")).ToArray();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 4,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var writerInventory = WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("candidateInventory");
        Assert.Empty(writerInventory.GetProperty("items").EnumerateArray());

        var criticInventory = WorkspaceUser(factory.Requests[2].Body)
            .GetProperty("candidateInventory");
        Assert.Equal(4, criticInventory.GetProperty("items").GetArrayLength());
        Assert.All(criticInventory.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("selected", item.GetProperty("status").GetString()));
        Assert.All(criticInventory.GetProperty("coverage").EnumerateArray(), role =>
            Assert.Equal(1, role.GetProperty("selectedCount").GetInt32()));
    }

    [Fact]
    public async Task A855_shape_keeps_model_observed_body_titles_across_focus_changes()
    {
        var porridge = CandidateUpdate(
            "porridge",
            "PORRIDGE AUX FLOCONS D'AVOINE",
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E-PORRIDGE"]);
        var pancakes = CandidateUpdate(
            "pancakes",
            "PANCAKES",
            "internal-source-2",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E-PANCAKES"]);
        var scones = CandidateUpdate(
            "scones",
            "SCONES AUX CANNEBERGES",
            "internal-source-3",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E-SCONES"]);
        const string searchPancakes =
            """{"query":"pancakes petit-déjeuner","sourceKey":"","category":"","documentHint":"","topK":20}""";
        const string searchScones =
            """{"query":"scones petit-déjeuner","sourceKey":"","category":"","documentHint":"","topK":20}""";
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(
                ("save_candidate_inventory", porridge),
                ("search_corpus", searchPancakes)),
            NativeCompletion(
                ("save_candidate_inventory", pancakes),
                ("search_corpus", searchScones)),
            NativeCompletion(("save_candidate_inventory", scones)),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        var breakfastBodies = new[]
        {
            BuildEvidence(
                "E-PORRIDGE",
                "PORRIDGE AUX FLOCONS D'AVOINE\nLe porridge est un petit-déjeuner. PRÉPARATION : cuire les flocons."),
            BuildEvidence(
                "E-PANCAKES",
                "PANCAKES\nINGRÉDIENTS puis PRÉPARATION. Servir au petit-déjeuner avec des fruits.",
                fileName: "pancakes.pdf"),
            BuildEvidence(
                "E-SCONES",
                "SCONES AUX CANNEBERGES\nINGRÉDIENTS puis PRÉPARATION. Servir au petit déjeuner ou au brunch.",
                fileName: "scones.pdf")
        };

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new SequencedToolGateway(
                    [breakfastBodies[0]],
                    [breakfastBodies[1]],
                    [breakfastBodies[2]]),
                CancellationToken.None);

        var inventory = WorkspaceUser(factory.Requests[4].Body)
            .GetProperty("candidateInventory");
        var items = inventory.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(
            ["PANCAKES", "PORRIDGE AUX FLOCONS D'AVOINE", "SCONES AUX CANNEBERGES"],
            items.Select(item => item.GetProperty("exactTitle").GetString()!)
                .Order(StringComparer.Ordinal).ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal("body_verified", item.GetProperty("status").GetString());
            Assert.Single(item.GetProperty("bodyEvidenceIds").EnumerateArray());
        });
        Assert.Equal(
            3,
            inventory.GetProperty("coverage")[0]
                .GetProperty("bodyVerifiedCount")
                .GetInt32());
    }

    [Fact]
    public async Task Candidate_checkpoint_restores_inventory_and_source_identity_after_provider_restart()
    {
        var update = CandidateUpdate(
            "porridge",
            "Porridge",
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E1"]);
        var evidence = BuildEvidence(
            "E1",
            "PORRIDGE\nINGRÉDIENTS\nAvoine et lait. PRÉPARATION\nCuire doucement.");
        var gateway = new CheckpointToolGateway(evidence);
        using (var firstFactory = new QueuedHttpClientFactory(
                   Completion(CandidatePlanner),
                   NativeCompletion(("save_candidate_inventory", update)),
                   CompletionStream(new IOException("simulated restart"))))
        {
            var firstOptions = WorkspaceOptions();
            firstOptions.LlmMaximumHttpAttempts = 1;
            await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(firstFactory, firstOptions, null)
                    .ExecuteAsync(
                        BuildRequest(
                            answerUnitCount: 20,
                            atomicEvidenceMode: "named_item",
                            selectionPolicy: "distinct_structured_layout"),
                        gateway,
                        CancellationToken.None));
        }

        Assert.NotNull(gateway.Checkpoint);
        Assert.Equal("Porridge", Assert.Single(gateway.Checkpoint!.Candidates).ExactTitle);
        Assert.Equal(
            "internal-source-1",
            Assert.Single(gateway.Checkpoint.PromptSourceKeys).Value);

        using var resumedFactory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(CandidateTerminal));
        var resumed = await new OpenAiCompatibleAdvancedAnalysisProvider(
                resumedFactory,
                WorkspaceOptions(),
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                gateway,
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", resumed.Outcome);
        var restored = WorkspaceUser(resumedFactory.Requests[1].Body)
            .GetProperty("candidateInventory")
            .GetProperty("items")[0];
        Assert.Equal("Porridge", restored.GetProperty("exactTitle").GetString());
        Assert.Equal("internal-source-1", restored.GetProperty("sourceKey").GetString());
        Assert.Equal("E1", restored.GetProperty("bodyEvidenceIds")[0].GetString());
    }

    [Fact]
    public async Task Candidate_checkpoint_fails_closed_when_it_references_unknown_evidence()
    {
        var evidence = BuildEvidence(
            "E1",
            "PORRIDGE\nINGRÉDIENTS\nAvoine et lait. PRÉPARATION\nCuire doucement.");
        var gateway = new CheckpointToolGateway(evidence);
        await gateway.SearchAsync(
            new AdvancedAnalysisSearchRequest("porridge", TopK: 5),
            CancellationToken.None);
        var sourceIdentity = string.Join(
            "|",
            evidence.Reference.DocId,
            evidence.Reference.RevisionId,
            evidence.Reference.SourceHash);
        await gateway.SaveResearchCheckpointAsync(
            new AdvancedAnalysisResearchCheckpoint(
                AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion,
                [
                    new AdvancedAnalysisCandidateCheckpoint(
                        "porridge",
                        "Porridge",
                        "internal-source-1",
                        ["petit-déjeuner"],
                        [],
                        "body_verified",
                        "Invalid evidence reference.",
                        [],
                        ["E404"])
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [sourceIdentity] = "internal-source-1"
                }),
            CancellationToken.None);

        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(
                    factory,
                    WorkspaceOptions(),
                    null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    gateway,
                    CancellationToken.None));

        Assert.Equal("advanced_research_checkpoint_invalid", error.ErrorCode);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Candidate_inventory_advances_a_locator_to_verified_body_without_losing_the_locator()
    {
        var discovered = CandidateUpdate(
            "omelette",
            "Omelette",
            "internal-source-1",
            "petit-déjeuner",
            "body_requested",
            ["E-NAV"],
            []);
        var verified = CandidateUpdate(
            "omelette",
            "Omelette",
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            [],
            ["E-BODY"]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(
                ("save_candidate_inventory", discovered),
                ("search_corpus", WorkspaceSearch)),
            NativeCompletion(("save_candidate_inventory", verified)),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        var docId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var locator = BuildEvidence(
            "E-NAV",
            "SOMMAIRE\nOmelette ........ 12\nPorridge ........ 14\nCrêpes ........ 16",
            docId: docId,
            revisionId: revisionId,
            exactTitle: "Omelette");
        var body = BuildEvidence(
            "E-BODY",
            "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre puis cuire.",
            docId: docId,
            revisionId: revisionId,
            exactTitle: "Omelette");

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new SequencedToolGateway([locator], [body]),
                CancellationToken.None);

        var item = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateInventory")
            .GetProperty("items")[0];
        Assert.Equal("body_verified", item.GetProperty("status").GetString());
        Assert.Equal("E-NAV", item.GetProperty("locatorEvidenceIds")[0].GetString());
        Assert.Equal("E-BODY", item.GetProperty("bodyEvidenceIds")[0].GetString());
    }

    [Theory]
    [InlineData("navigation_body")]
    [InlineData("verified_without_body")]
    [InlineData("unsupported_title")]
    public async Task Invalid_candidate_inventory_rejects_the_mixed_batch_before_documentary_io(
        string mutation)
    {
        var locatorIds = new[] { "E1" };
        var bodyIds = mutation == "verified_without_body" ? Array.Empty<string>() : new[] { "E1" };
        var title = mutation == "unsupported_title" ? "Titre absent" : "Omelette";
        var update = CandidateUpdate(
            "omelette",
            title,
            "internal-source-1",
            "petit-déjeuner",
            "body_verified",
            locatorIds,
            bodyIds);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(
                ("save_candidate_inventory", update),
                ("search_corpus", WorkspaceSearch)));
        var options = WorkspaceOptions();
        var content = mutation == "navigation_body"
            ? "SOMMAIRE\nOmelette ........ 12\nPorridge ........ 14\nCrêpes ........ 16"
            : "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre puis cuire.";
        var gateway = new RecordingToolGateway(BuildEvidence(
            "E1",
            content,
            exactTitle: "Omelette"));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    gateway,
                    CancellationToken.None));

        Assert.Equal("advanced_native_tool_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Searches);
    }

    private sealed class CheckpointToolGateway(
        params AdvancedAnalysisResolvedEvidence[] searchEvidence) :
        IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];

        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;

        public AdvancedAnalysisResearchCheckpoint? Checkpoint { get; private set; }

        public Task<AdvancedAnalysisResearchCheckpoint?> LoadResearchCheckpointAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Checkpoint);
        }

        public Task SaveResearchCheckpointAsync(
            AdvancedAnalysisResearchCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checkpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in searchEvidence)
                if (_evidence.All(existing =>
                        existing.Reference.EvidenceId != item.Reference.EvidenceId))
                    _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                searchEvidence,
                [],
                1,
                1));
        }
    }
}
