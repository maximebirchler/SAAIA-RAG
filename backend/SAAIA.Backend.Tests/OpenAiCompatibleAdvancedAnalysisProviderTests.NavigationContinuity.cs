using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static string NavigationCandidateKey(string title)
        => "candidate-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("internal-source-1\n" + title.ToLowerInvariant())))
            .ToLowerInvariant()[..16];

    private static string NavigationJudgeUpdate(params int[] indices)
        => NavigationJudgeUpdateForRoles(indices.Select(index => (Index: index, Role: "petit-déjeuner")));

    private static string NavigationJudgeUpdateForRoles(IEnumerable<(int Index, string Role)> candidates)
        => JsonSerializer.Serialize(new
        {
            items = candidates.Select(candidate => new
            {
                key = NavigationCandidateKey("R" + candidate.Index), exactTitle = "R" + candidate.Index,
                sourceKey = "internal-source-1", targetRoles = new[] { candidate.Role },
                selectedRoles = Array.Empty<string>(), status = "body_verified", note = "Qualified from canonical body.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E" + candidate.Index }
            })
        });

    private static HttpResponseMessage NavigatorFunction(string protocol, string callId, string arguments,
        int reasoningCharacters = 0)
        => new(HttpStatusCode.OK)
        {
            Content = protocol == "responses"
                ? JsonContent.Create(new
                {
                    status = "completed",
                    output = new object[]
                    {
                        new { type = "reasoning", id = "reasoning-" + callId,
                            summary = Array.Empty<object>(), encrypted_content = "sealed-" + callId
                                + new string('z', reasoningCharacters) },
                        new { type = "function_call", id = "function-" + callId, call_id = callId,
                            name = "find_source_text", arguments, status = "completed" }
                    },
                    usage = new { input_tokens = 100, output_tokens = 50 }
                })
                : JsonContent.Create(new
                {
                    choices = new[]
                    {
                        new { message = new { role = "assistant", content = (string?)null,
                            tool_calls = new[] { new { id = callId, type = "function",
                                function = new { name = "find_source_text", arguments } } } } }
                    },
                    usage = new { prompt_tokens = 100, completion_tokens = 50 }
                })
        };

    [Theory]
    [InlineData("chat-completions", 0)]
    [InlineData("responses", 0)]
    [InlineData("responses", 20_000)]
    [InlineData("responses", 50_000)]
    public async Task Navigation_continuity_retains_canonical_contents_after_body_projection_and_links_real_results(
        string protocol, int reasoningCharacters)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var contents = "TABLE DES MATIÈRES\n" + string.Join("\n", Enumerable.Range(1, 75)
                           .Select(index => $"Entry title {index} ........ {index + 30}"))
                       + "R1 ........ 12\nR2 ........ 24";
        var navigation = BuildEvidence("TOC", contents, docId: documentId,
            revisionId: revisionId, exactTitle: "TABLE DES MATIÈRES");
        // These untitled canonical prose chunks displace the TOC from the ordinary
        // body-focused view. The Judge must inspect and reject their provisional
        // seeds instead of the code silently deciding that they contain no items.
        var bodies = Enumerable.Range(1, 40).Select(index => BuildEvidence(
            "BODY-" + index, "Documentary discussion "
                            + string.Join(" ", Enumerable.Repeat("Canonical explanatory sentence.", 80)),
            docId: documentId, revisionId: revisionId, pageStart: index + 1)).ToArray();
        var first = """{"sourceKey":"internal-source-1","query":"R1","offset":0,"topK":60}""";
        var second = """{"sourceKey":"internal-source-1","query":"R2","offset":0,"topK":60}""";
        const string bounded = """{"outcome":"navigation_bounded","reason":"All observed routes were attempted."}""";
        HttpResponseMessage Terminal(string content) => protocol == "responses"
            ? NativeResponseAnswer(content) : Completion(content);
        using var factory = new NavigationContinuityHttpClientFactory(
            Completion(CandidatePlanner),
            NavigatorFunction(protocol, "nav-first", first, reasoningCharacters),
            NavigatorFunction(protocol, "nav-second", second, reasoningCharacters),
            Terminal(bounded),
            Terminal(CandidateTerminal),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 20;
        var gateway = new SequencedToolGateway([navigation], bodies, bodies);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.True(factory.JudgeRequestCount > 0);
        Assert.InRange(factory.Requests.Count - factory.JudgeRequestCount, 5, 6);
        var navigationRequests = factory.Requests.Where(item => item.Body.Contains(
            "You are the SAAIA Candidate Navigator", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, navigationRequests.Length);
        foreach (var call in navigationRequests)
        {
            var user = WorkspaceUser(call.Body);
            var retainedContents = user.GetProperty("evidence").EnumerateArray()
                .Single(item => item.GetProperty("evidenceId").GetString() == "TOC");
            Assert.Contains("R2 ........ 24", retainedContents.GetProperty("content").GetString());
        }
        using var next = JsonDocument.Parse(navigationRequests[2].Body);
        var input = next.RootElement.GetProperty(protocol == "responses" ? "input" : "messages");
        var outputs = input.EnumerateArray().Where(item => protocol == "responses"
            ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : item.TryGetProperty("role", out var role) && role.GetString() == "tool").ToArray();
        var retainedCallIds = reasoningCharacters == 0
            ? new[] { "nav-first", "nav-second" }
            : reasoningCharacters < 32_768 ? new[] { "nav-second" } : Array.Empty<string>();
        Assert.Equal(retainedCallIds, outputs.Select(item =>
            item.GetProperty(protocol == "responses" ? "call_id" : "tool_call_id").GetString()));
        foreach (var output in outputs)
        {
            using var resultJson = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
            Assert.Equal("executed", resultJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(40, resultJson.RootElement.GetProperty("returnedEvidenceCount").GetInt32());
            Assert.DoesNotContain("Documentary discussion", output.GetRawText());
        }
        if (protocol == "responses")
            Assert.Equal(retainedCallIds.Select(id => "sealed-" + id + new string('z', reasoningCharacters)), input.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning")
                .Select(item => item.GetProperty("encrypted_content").GetString()));
        Assert.Equal(reasoningCharacters == 0 ? 0 : reasoningCharacters < 32_768 ? 1 : 2,
            WorkspaceUser(navigationRequests[2].Body).GetProperty("projection")
                .GetProperty("nativeHistoryOmittedTurnCount").GetInt32());
    }

    private sealed class NavigationContinuityHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        public List<CapturedRequest> Requests { get; } = [];
        public int JudgeRequestCount { get; private set; }

        public NavigationContinuityHttpClientFactory(params HttpResponseMessage[] responses)
            => _client = new HttpClient(new Handler(this, responses));

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("advanced-analysis-llm", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();

        private sealed class Handler(NavigationContinuityHttpClientFactory owner,
            IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses = new(responses);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add(new(request.RequestUri!, request.Headers.Authorization, body));
                if (!body.Contains("You are the SAAIA Candidate Judge", StringComparison.Ordinal))
                    return await AdaptProtocolAgnosticFixtureAsync(request, _responses.Dequeue(), cancellationToken);

                owner.JudgeRequestCount++;
                var prompt = WorkspaceUser(body);
                Assert.All(prompt.GetProperty("evidence").EnumerateArray(), item =>
                    Assert.Contains("Documentary discussion", item.GetProperty("content").GetString()));
                var rejection = Completion(JsonSerializer.Serialize(new
                {
                    items = prompt.GetProperty("candidateInventory").GetProperty("items").EnumerateArray()
                        .Select(item => new
                        {
                            key = item.GetProperty("key").GetString(),
                            exactTitle = item.GetProperty("exactTitle").GetString(),
                            sourceKey = item.GetProperty("sourceKey").GetString(),
                            targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(),
                            status = "rejected", note = "Explanatory prose contains no requested usable named item.",
                            locatorEvidenceIds = Array.Empty<string>(),
                            bodyEvidenceIds = item.GetProperty("bodyEvidenceIds").Clone()
                        }).ToArray()
                }));
                return await AdaptProtocolAgnosticFixtureAsync(request, rejection, cancellationToken);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_batches_small_judge_queues_and_flushes_them_when_navigation_is_bounded(bool secondNavigationIsBounded)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        AdvancedAnalysisResolvedEvidence Body(int index) => BuildEvidence("E" + index,
            $"R{index}\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
            docId: documentId, revisionId: revisionId, pageStart: index, exactTitle: "R" + index);
        string Find(int index) => JsonSerializer.Serialize(new
            { sourceKey = "internal-source-1", query = "R" + index, offset = 0, topK = 20 });
        const string bounded = """{"outcome":"navigation_bounded","reason":"No further untried navigation route."}""";
        var responses = new List<HttpResponseMessage>
        {
            Completion(CandidatePlanner), Completion(NavigationJudgeUpdate(1)),
            NavigatorFunction("chat-completions", "nav-first", Find(2)),
            secondNavigationIsBounded ? Completion(bounded)
                : NavigatorFunction("chat-completions", "nav-second", Find(3)),
            Completion(secondNavigationIsBounded ? NavigationJudgeUpdate(2) : NavigationJudgeUpdate(2, 3))
        };
        if (!secondNavigationIsBounded) responses.Add(Completion(bounded));
        responses.Add(Completion(CandidateTerminal));
        responses.Add(Completion(CandidateTerminal));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 8;
        var gateway = new SequencedToolGateway([Body(1)], [Body(2)], [Body(3)]);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.True(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("researchTools", out _));
        var batch = WorkspaceUser(factory.Requests[4].Body).GetProperty("candidateInventory").GetProperty("items");
        Assert.Equal(secondNavigationIsBounded ? 1 : 2, batch.GetArrayLength());
        Assert.All(batch.EnumerateArray(), item => Assert.Contains(item.GetProperty("exactTitle").GetString(),
            secondNavigationIsBounded ? new[] { "R2" } : new[] { "R2", "R3" }));
        Assert.Equal(secondNavigationIsBounded ? 2 : 3, gateway.Searches.Count);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(33)]
    public async Task Navigation_keeps_every_remembered_canonical_source_callable_with_source_only_anchors(int sourceCount)
    {
        var evidence = Enumerable.Range(1, sourceCount).Select(index => BuildEvidence("CONTENTS-" + index,
            $"TABLE DES MATIÈRES\nItem {index} ........ 12", fileName: $"source-{index}.pdf",
            exactTitle: "TABLE DES MATIÈRES")).ToArray();
        var sourceKey = "internal-source-" + sourceCount;
        var find = JsonSerializer.Serialize(new { sourceKey, query = "Item " + sourceCount, offset = 0, topK = 20 });
        const string bounded = """{"outcome":"navigation_bounded","reason":"All visible locators attempted."}""";
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            NavigatorFunction("chat-completions", "nav-last-source", find),
            Completion(bounded), Completion(CandidateTerminal), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(evidence);

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[1].Body);
        var anchor = prompt.GetProperty("evidence").EnumerateArray()
            .Single(item => item.GetProperty("sourceKey").GetString() == sourceKey);
        Assert.Equal(string.Empty, anchor.GetProperty("content").GetString());
        Assert.True(anchor.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal("CONTENTS-" + sourceCount, anchor.GetProperty("evidenceId").GetString());
        Assert.Equal(evidence[sourceCount - 1].Reference.DocId, gateway.Searches[1].DocId);
        Assert.Equal(Math.Max(0, sourceCount - 32), prompt.GetProperty("navigationMemory").GetProperty("omittedWindowCount").GetInt32());
        Assert.Equal(Math.Min(32, sourceCount), prompt.GetProperty("navigationMemory").GetProperty("retainedWindowCount").GetInt32());
        Assert.InRange(prompt.GetProperty("navigationMemory").GetProperty("windows").GetArrayLength(), 1, 32);
        using var request = JsonDocument.Parse(factory.Requests[1].Body);
        var function = request.RootElement.GetProperty("tools").EnumerateArray()
            .Single(item => item.GetProperty("function").GetProperty("name").GetString() == "find_source_text")
            .GetProperty("function");
        Assert.Contains(sourceKey, function.GetProperty("parameters").GetProperty("properties")
            .GetProperty("sourceKey").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Navigation_preserves_prior_contents_windows_as_locator_memory_without_duplicate_current_evidence_ids()
    {
        var contents = "TABLE DES MATIÈRES\n" + string.Join("\n", Enumerable.Range(1, 150)
            .Select(index => $"Entry title {index} ........ {index + 30}"));
        var evidence = BuildEvidence("TOC", contents, exactTitle: "TABLE DES MATIÈRES");
        var offset = contents.IndexOf("Entry title 100 ", StringComparison.Ordinal);
        var read = JsonSerializer.Serialize(new { evidenceId = "TOC", offset, length = 8_000 });
        const string bounded = """{"outcome":"navigation_bounded","reason":"No further routes."}""";
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            NativeCompletion(("read_evidence", read)), Completion(bounded),
            Completion(CandidateTerminal), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(evidence);

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Single(gateway.Searches);
        var prompt = WorkspaceUser(factory.Requests[2].Body);
        Assert.Single(prompt.GetProperty("evidence").EnumerateArray());
        Assert.Equal(offset, prompt.GetProperty("evidence")[0].GetProperty("contentOffset").GetInt32());
        var windows = prompt.GetProperty("navigationMemory").GetProperty("windows").EnumerateArray().ToArray();
        Assert.Single(windows);
        Assert.Equal(2, prompt.GetProperty("navigationMemory").GetProperty("retainedWindowCount").GetInt32());
        Assert.Contains(windows, window => window.GetProperty("contentOffset").GetInt32() == 0
            && window.GetProperty("content").GetString()!.Contains("Entry title 1 ", StringComparison.Ordinal));
        Assert.Contains("Entry title 150 ", prompt.GetProperty("evidence")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_reserve_is_one_optional_cycle_that_preserves_a_complete_qualified_assignment(bool discoverReserveBody)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        AdvancedAnalysisResolvedEvidence Body(int index) => BuildEvidence("E" + index,
            $"R{index}\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
            docId: documentId, revisionId: revisionId, pageStart: index, exactTitle: "R" + index);
        var candidates = Enumerable.Range(1, 20).Select(index =>
            (Index: index, Role: roles[(index - 1) % 4]))
            .OrderBy(item => NavigationCandidateKey("R" + item.Index), StringComparer.Ordinal).ToArray();
        const string bounded = """{"outcome":"navigation_bounded","reason":"No useful reserve route remains."}""";
        var responses = new List<HttpResponseMessage>
        {
            Completion(CandidatePlanner), Completion(NavigationJudgeUpdateForRoles(candidates.Take(12))),
            Completion(NavigationJudgeUpdateForRoles(candidates.Skip(12)))
        };
        if (discoverReserveBody)
        {
            responses.Add(NavigatorFunction("chat-completions", "reserve-navigation",
                """{"sourceKey":"internal-source-1","query":"R21","offset":0,"topK":20}"""));
            responses.Add(Completion(NavigationJudgeUpdate(21)));
        }
        else responses.Add(Completion(bounded));
        responses.Add(Completion(CandidateAnswered(20)));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateExplorerReservePerRole = 2;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 7;
        var gateway = new SequencedToolGateway(Enumerable.Range(1, 20).Select(Body).ToArray(), [Body(21)]);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        var reservePrompt = WorkspaceUser(factory.Requests[3].Body);
        Assert.Equal("optional_reserve", reservePrompt.GetProperty("assignmentGap").GetProperty("researchPurpose").GetString());
        Assert.Equal(4, reservePrompt.GetProperty("assignmentGap").GetProperty("focusRoles").GetArrayLength());
        Assert.Equal(discoverReserveBody ? 6 : 5, factory.Requests.Count);
        Assert.Equal(discoverReserveBody ? 2 : 1, gateway.Searches.Count);
        var dossier = WorkspaceUser(factory.Requests[^1].Body).GetProperty("candidateDossier");
        Assert.Equal("ready", dossier.GetProperty("outcome").GetString());
        Assert.Equal(20, dossier.GetProperty("maximumAssignableCount").GetInt32());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Navigation_accepts_read_evidence_only_and_rejects_an_invalid_mixed_batch_atomically(
        bool invalidMixedBatch, bool retrievalBudgetExhausted)
    {
        var content = "R1\n" + string.Join(" ", Enumerable.Repeat("Canonical introductory discussion.", 90))
                      + "\nR1\nINGRÉDIENTS\nÉlément. PRÉPARATION\nComplete canonical body after the introductory text.";
        var evidence = BuildEvidence("E1", content, exactTitle: "R1");
        var needsBody = JsonSerializer.Serialize(new
        {
            items = new[] { new { key = NavigationCandidateKey("R1"), exactTitle = "R1",
                sourceKey = "internal-source-1", targetRoles = Array.Empty<string>(),
                selectedRoles = Array.Empty<string>(), status = "needs_body", note = "Read the omitted canonical body tail.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" } } }
        });
        var offset = content.IndexOf("\nR1\nINGRÉDIENTS", StringComparison.Ordinal) + 1;
        var read = JsonSerializer.Serialize(new { evidenceId = "E1", offset, length = 1_000 });
        const string bounded = """{"outcome":"navigation_bounded","reason":"No further routes."}""";
        var operation = invalidMixedBatch
            ? NativeCompletion(("read_evidence", read), ("read_source",
                """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":5,"topK":20}"""))
            : NativeCompletion(("read_evidence", read));
        var responses = new List<HttpResponseMessage>
            { Completion(CandidatePlanner), Completion(needsBody), operation };
        if (!invalidMixedBatch) responses.Add(Completion(NavigationJudgeUpdate(1)));
        // Even after the final tail window, a gap can remain between the
        // retained prefix and that tail. A read-only Navigator may inspect it.
        responses.Add(Completion(bounded));
        responses.Add(Completion(CandidateTerminal));
        responses.Add(Completion(CandidateTerminal));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 7;
        var gateway = new RecordingToolGateway(evidence);

        IAdvancedAnalysisToolGateway tools = retrievalBudgetExhausted
            ? new NavigationSingleRetrievalGateway(gateway) : gateway;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), tools, CancellationToken.None);

        Assert.Single(gateway.Searches);
        var next = WorkspaceUser(factory.Requests[3].Body);
        if (invalidMixedBatch)
        {
            Assert.True(next.TryGetProperty("researchTools", out _));
            Assert.Equal(0, next.GetProperty("evidence")[0].GetProperty("contentOffset").GetInt32());
            Assert.Equal("needs_body", next.GetProperty("candidateLedger")[0].GetProperty("status").GetString());
            using var request = JsonDocument.Parse(factory.Requests[3].Body);
            var result = request.RootElement.GetProperty("messages").EnumerateArray()
                .First(item => item.TryGetProperty("role", out var role) && role.GetString() == "tool");
            using var feedback = JsonDocument.Parse(result.GetProperty("content").GetString()!);
            Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
        }
        else
        {
            Assert.Equal("awaiting_review", next.GetProperty("candidateInventory").GetProperty("items")[0]
                .GetProperty("status").GetString());
            Assert.Equal(offset, next.GetProperty("evidence")[0].GetProperty("contentOffset").GetInt32());
            Assert.Contains("Complete canonical body", next.GetProperty("evidence")[0].GetProperty("content").GetString());
            if (!retrievalBudgetExhausted)
            {
                var navigator = WorkspaceUser(factory.Requests[4].Body);
                Assert.Equal("body_verified", navigator.GetProperty("candidateLedger")[0].GetProperty("status").GetString());
            }
            else
            {
                var navigator = WorkspaceUser(factory.Requests[2].Body);
                Assert.False(navigator.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
                using var request = JsonDocument.Parse(factory.Requests[2].Body);
                Assert.Equal(new[] { "read_evidence" }, request.RootElement.GetProperty("tools").EnumerateArray()
                    .Select(item => item.GetProperty("function").GetProperty("name").GetString()));
            }
        }
    }

    private sealed class NavigationSingleRetrievalGateway(RecordingToolGateway inner) : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => inner.Evidence;
        public AdvancedAnalysisToolBudget? Budget => new(1, inner.Searches.Count, 60_000, 0);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken) => inner.SearchAsync(request, cancellationToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Navigation_capacity_allows_one_existing_body_repair_without_new_admission_or_losing_a_complete_plan(
        bool repairBody, bool completePlan)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var longBody = "R64\n" + string.Join(" ", Enumerable.Repeat("Canonical introductory discussion.", 90))
                       + "\nR64\nINGRÉDIENTS\nÉlément. PRÉPARATION\nComplementary canonical body.";
        var evidence = Enumerable.Range(1, 65).Select(index => BuildEvidence("E" + index,
            index == 64 ? longBody : $"R{index}\nSubstantive canonical named body.",
            docId: documentId, revisionId: revisionId, pageStart: index, exactTitle: "R" + index)).ToArray();
        var candidates = Enumerable.Range(1, 64).Select(index => new AdvancedAnalysisCandidateCheckpoint(
            "seed-" + index, "R" + index, "internal-source-1",
            index == 64 ? [] : completePlan ? [roles[(index - 1) % 4]] : ["petit-déjeuner"],
            [], index == 64 ? "needs_body" : "body_verified", "Canonical prior assessment.", [], ["E" + index])).ToArray();
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, candidates);
        var offset = longBody.IndexOf("\nR64\nINGRÉDIENTS", StringComparison.Ordinal) + 1;
        var read = JsonSerializer.Serialize(new { evidenceId = "E64", offset, length = 1_000 });
        const string bounded = """{"outcome":"navigation_bounded","reason":"No useful repair remains."}""";
        var responses = new List<HttpResponseMessage> { Completion(CandidatePlanner) };
        if (!completePlan)
        {
            responses.Add(repairBody ? NativeCompletion(("read_evidence", read)) : Completion(bounded));
            if (repairBody)
            {
                responses.Add(Completion(JsonSerializer.Serialize(new
                {
                    items = new[] { new { key = "seed-64", exactTitle = "R64", sourceKey = "internal-source-1",
                        targetRoles = new[] { "déjeuner" }, selectedRoles = Array.Empty<string>(),
                        status = "body_verified", note = "Qualified from complementary canonical body.",
                        locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E64" } } }
                })));
            }
        }
        responses.Add(Completion(completePlan ? CandidateAnswered(20) : CandidateTerminal));
        responses.Add(Completion(CandidateTerminal));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 6;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Equal(64, gateway.Checkpoint!.Candidates.Count(item => item.Status != "rejected"));
        Assert.DoesNotContain(gateway.Checkpoint.Candidates, item => item.ExactTitle == "R65");
        if (completePlan)
        {
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(20, result.Claims.Count);
            Assert.Equal(2, factory.Requests.Count);
            Assert.Equal("ready", WorkspaceUser(factory.Requests[1].Body).GetProperty("candidateDossier").GetProperty("outcome").GetString());
        }
        else
        {
            Assert.Equal("insufficient_documentation", result.Outcome);
            var repairPrompt = WorkspaceUser(factory.Requests[1].Body);
            Assert.True(repairPrompt.GetProperty("projection").GetProperty("capacityRepairOnly").GetBoolean());
            using var request = JsonDocument.Parse(factory.Requests[1].Body);
            Assert.Equal(new[] { "read_evidence" }, request.RootElement.GetProperty("tools").EnumerateArray()
                .Select(item => item.GetProperty("function").GetProperty("name").GetString()));
            Assert.Single(repairPrompt.GetProperty("evidence").EnumerateArray());
            Assert.Equal("E64", repairPrompt.GetProperty("evidence")[0].GetProperty("evidenceId").GetString());
            Assert.Equal(repairBody ? "body_verified" : "needs_body",
                gateway.Checkpoint.Candidates.Single(item => item.Key == "seed-64").Status);
            Assert.InRange(factory.Requests.Count, repairBody ? 4 : 3, repairBody ? 5 : 4);
            var writer = factory.Requests.Select(item => WorkspaceUser(item.Body))
                .First(item => item.TryGetProperty("candidateDossier", out _));
            Assert.Contains("capacity", writer.GetProperty("candidateDossier").GetProperty("reason").GetString());
        }
    }
}
