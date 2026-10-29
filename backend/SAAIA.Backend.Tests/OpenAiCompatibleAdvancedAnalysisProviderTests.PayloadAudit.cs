using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static string PayloadAuditSystem(string body)
    {
        using var request = JsonDocument.Parse(body);
        return request.RootElement.GetProperty(request.RootElement.TryGetProperty("input", out _)
                ? "input" : "messages").EnumerateArray()
            .Single(item => item.TryGetProperty("role", out var role) && role.GetString() == "system")
            .GetProperty("content").GetString()!;
    }

    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task Payload_audit_actual_writer_and_critic_prompts_keep_identity_provisional_and_domain_rules_generic(
        string style, string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;
        var source = BuildEvidence("E1", "SECTION\nLe seuil est 17 bar.", exactTitle: "SECTION");
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(source), CancellationToken.None);

        Assert.Equal(3, factory.Requests.Count);
        Assert.All(factory.Requests, item => Assert.Contains("Treat documentary excerpts", PayloadAuditSystem(item.Body)));
        foreach (var item in factory.Requests.Skip(1))
        {
            var system = PayloadAuditSystem(item.Body);
            Assert.Contains("provisional", system, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("copy the chosen candidateTitle verbatim", system, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("a dessert is not an ordinary breakfast", system, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("For meal planning", system, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Payload_audit_oversized_navigation_metadata_cannot_remove_canonical_body_from_the_prompt()
    {
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.") with
        {
            SourceStructure = new(new string('S', 20_000), new string('H', 30_000), "previous", "next")
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal("Le seuil est 17 bar.", visible.GetProperty("content").GetString());
        Assert.True(visible.GetProperty("sourceStructure").GetProperty("metadataTruncated").GetBoolean());
        Assert.InRange(visible.GetRawText().Length, 1, 2_000);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Payload_audit_staged_judge_and_navigator_receive_documentary_boundary_and_complete_native_menu(
        string protocol)
    {
        const string bounded = """{"outcome":"navigation_bounded","reason":"No useful untried source locator remains."}""";
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(LifecycleJudgeItem(LifecycleCandidateKey("R1"), "R1", "body_verified", ["E1"])),
            protocol == "responses" ? NativeResponseAnswer(bounded) : Completion(bounded),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 5;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(
                BuildEvidence("E1", "R1\nA documented concrete choice.", exactTitle: "R1")),
                CancellationToken.None);

        Assert.Equal(4, factory.Requests.Count);
        Assert.All(factory.Requests, item => Assert.Contains("Treat documentary excerpts", PayloadAuditSystem(item.Body)));
        Assert.Contains("provisional text prefix", PayloadAuditSystem(factory.Requests[1].Body));
        var tools = WorkspaceUser(factory.Requests[2].Body).GetProperty("researchTools").GetProperty("tools");
        Assert.Contains(tools.EnumerateArray(), item => item.GetProperty("name").GetString() == "read_evidence");
    }

    [Fact]
    public async Task Payload_audit_legacy_json_review_does_not_offer_a_native_only_evidence_reader()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")),
                CancellationToken.None);

        var tools = WorkspaceUser(factory.Requests[1].Body).GetProperty("tools");
        Assert.DoesNotContain(tools.EnumerateArray(), item => item.GetProperty("name").GetString() == "read_evidence");
    }

    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task Payload_audit_explicit_repeatable_grid_can_reuse_one_documented_item_without_distinct_explorer(
        string style, string protocol)
    {
        var answered = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join("; ", Enumerable.Range(1, 20).Select(index => $"R1 [C{index}]")),
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}", selectedItem = "R1", text = "R1 est documenté.", evidenceIds = new[] { "E1" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"repeatable_named_items","queries":[{"query":"documented item","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(answered) : Completion(answered),
            protocol == "responses" ? NativeResponseAnswer(answered) : Completion(answered));
        var options = WorkspaceOptions(protocol);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        var request = BuildRequest(20, "named_item", "structured_layout");
        request = request with
        {
            Handoff = new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = request.Handoff.HandoffId, CreatedAtUtc = request.Handoff.CreatedAtUtc,
                RequestText = "Construis un planning de repas 5 x 4 sourcé. Les répétitions sont autorisées.",
                Language = "fr", OriginIntent = request.Handoff.OriginIntent,
                ReasonCode = request.Handoff.ReasonCode, TransferStage = request.Handoff.TransferStage,
                Load = request.Handoff.Load
            }
        };
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1",
                "R1\nUn choix simple documenté pouvant être proposé dans les créneaux demandés.", exactTitle: "R1")),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal("repeatable_named_items", result.SelectionMode);
        Assert.Equal(20, result.Claims.Count);
        Assert.Single(result.Claims.Select(item => item.SelectedItem).Distinct(StringComparer.Ordinal));
        Assert.Equal(3, factory.Requests.Count);
        Assert.All(factory.Requests.Skip(1), item =>
            Assert.Contains("repeatable", PayloadAuditSystem(item.Body), StringComparison.OrdinalIgnoreCase));
        Assert.False(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("candidateDossier", out _));
    }

    [Theory]
    [InlineData("chat-completions", 4_000)]
    [InlineData("responses", 4_000)]
    [InlineData("chat-completions", 6_000)]
    [InlineData("responses", 6_000)]
    public async Task Payload_audit_current_requested_window_is_preserved_before_shortening_canonical_prefix(
        string protocol, int requestedLength)
    {
        var canonical = BuildEvidence("E1", "DOCUMENT LIMIT 17 BAR.\n" + new string('x', 20_000)) with
        {
            SourceStructure = new(new string('S', 20_000), new string('H', 30_000), "previous", "next")
        };
        var originalContent = canonical.Content;
        var originalStructure = canonical.SourceStructure;
        var read = JsonSerializer.Serialize(new { evidenceId = "E1", offset = 6_000, length = requestedLength });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", read) : NativeCompletion(("read_evidence", read)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(canonical), CancellationToken.None);

        var body = factory.Requests[2].Body;
        var current = WorkspaceUser(body);
        AssertWireProjection(current, 1, 1);
        var visible = Assert.Single(current.GetProperty("evidence").EnumerateArray());
        Assert.Equal(6_000, visible.GetProperty("contentOffset").GetInt32());
        var content = visible.GetProperty("content").GetString()!;
        Assert.InRange(content.Length, 1, requestedLength);
        Assert.Equal(canonical.Content.Substring(6_000, content.Length), content);
        Assert.Equal(6_000 + content.Length, visible.GetProperty("nextContentOffset").GetInt32());
        Assert.True(visible.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal(canonical.Content.Length, visible.GetProperty("originalContentLength").GetInt32());
        if (requestedLength == 4_000)
        {
            Assert.Equal(requestedLength, content.Length);
            var prefix = Assert.Single(visible.GetProperty("supportingContentWindows").EnumerateArray());
            Assert.Equal(0, prefix.GetProperty("contentOffset").GetInt32());
            var prefixText = prefix.GetProperty("content").GetString()!;
            Assert.StartsWith("DOCUMENT LIMIT 17 BAR.", prefixText);
            Assert.Equal(canonical.Content[..prefixText.Length], prefixText);
            Assert.InRange(prefixText.Length, 1, 2_399);
            Assert.True(prefix.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(0, visible.GetProperty("supportingContentWindowsOmittedCount").GetInt32());
        }
        else
        {
            Assert.InRange(content.Length, 1, requestedLength - 1);
            Assert.Empty(visible.GetProperty("supportingContentWindows").EnumerateArray());
            Assert.Equal(1, visible.GetProperty("supportingContentWindowsOmittedCount").GetInt32());
        }
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 8_000);
        Assert.Equal(originalContent, canonical.Content);
        Assert.Same(originalStructure, canonical.SourceStructure);
    }
}
