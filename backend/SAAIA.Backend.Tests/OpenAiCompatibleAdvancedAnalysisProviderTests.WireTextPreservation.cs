using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string WireTextFact = "Le seuil est 17 bar.\n";
    private static string WireTextPrefix(int length)
        => WireTextFact + new string('x', length - WireTextFact.Length);

    private static HttpResponseMessage WireTextFunction(string protocol, string arguments)
        => protocol == "responses" ? NativeResponseFunction("read_evidence", arguments)
            : NativeCompletion(("read_evidence", arguments));

    private static HttpResponseMessage WireTextAnswer(string protocol)
        => protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered);

    private static void AssertWireTextWindow(JsonElement window, string canonical)
    {
        var text = window.GetProperty("content").GetString()!;
        var offset = window.GetProperty("contentOffset").GetInt32();
        Assert.DoesNotContain("\ufffd", text);
        Assert.Equal(canonical.Substring(offset, text.Length), text);
        Assert.False(text.Length > 0 && char.IsLowSurrogate(text[0]));
        Assert.False(text.Length > 0 && char.IsHighSurrogate(text[^1]));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireTextPreservation_initial_prefix_keeps_complete_unicode_and_exact_next_offset(string protocol)
    {
        var canonical = WireTextPrefix(2_399) + "😀" + new string('y', 600);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), WireTextAnswer(protocol));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);
        var evidence = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        AssertWireTextWindow(evidence, canonical);
        Assert.Equal(canonical[..2_399], evidence.GetProperty("content").GetString());
        Assert.Equal(2_399, evidence.GetProperty("nextContentOffset").GetInt32());
    }

    [Theory]
    [InlineData("chat-completions", "current")]
    [InlineData("responses", "current")]
    [InlineData("chat-completions", "prefix")]
    [InlineData("responses", "prefix")]
    [InlineData("chat-completions", "previous")]
    [InlineData("responses", "previous")]
    public async Task WireTextPreservation_requested_and_supporting_windows_keep_exact_unicode(string protocol, string surface)
    {
        var canonical = surface switch
        {
            "current" => WireTextPrefix(3_000) + new string('a', 7_999) + "😀" + new string('b', 500),
            "prefix" => WireTextPrefix(2_399) + "😀" + new string('b', 4_000),
            _ => WireTextPrefix(3_000) + new string('a', 511) + "😀" + new string('b', 7_000)
        };
        var reads = surface == "previous"
            ? new[] { """{"evidenceId":"E1","offset":3000,"length":512}""", """{"evidenceId":"E1","offset":9000,"length":256}""" }
            : new[] { JsonSerializer.Serialize(new { evidenceId = "E1", offset = 3_000, length = surface == "current" ? 8_000 : 256 }) };
        using var factory = new QueuedHttpClientFactory(new[]
            { Completion("""{"queries":[{"query":"overview","topK":12}]}""") }
            .Concat(reads.Select(read => WireTextFunction(protocol, read))).Append(WireTextAnswer(protocol)).ToArray());
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 100_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);
        var evidence = Assert.Single(WorkspaceUser(factory.Requests[^1].Body).GetProperty("evidence").EnumerateArray());
        AssertWireTextWindow(evidence, canonical);
        foreach (var window in evidence.GetProperty("supportingContentWindows").EnumerateArray())
            AssertWireTextWindow(window, canonical);
        if (surface == "current")
        {
            Assert.Equal(7_999, evidence.GetProperty("content").GetString()!.Length);
            Assert.Equal(10_999, evidence.GetProperty("nextContentOffset").GetInt32());
        }
        else
        {
            var expectedOffset = surface == "prefix" ? 0 : 3_000;
            var window = Assert.Single(evidence.GetProperty("supportingContentWindows").EnumerateArray(),
                item => item.GetProperty("contentOffset").GetInt32() == expectedOffset);
            Assert.Equal(surface == "prefix" ? 2_399 : 511, window.GetProperty("content").GetString()!.Length);
        }
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireTextPreservation_low_surrogate_offset_gets_linked_rejection_before_projection(string protocol)
    {
        var canonical = WireTextPrefix(3_000) + "😀" + new string('y', 1_000);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            WireTextFunction(protocol, """{"evidenceId":"E1","offset":3001,"length":256}"""),
            WireTextFunction(protocol, """{"evidenceId":"E1","offset":3000,"length":256}"""), WireTextAnswer(protocol));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);
        var unchanged = Assert.Single(WorkspaceUser(factory.Requests[2].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(0, unchanged.GetProperty("contentOffset").GetInt32());
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        var result = Assert.Single(request.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray(),
            item => protocol == "responses" ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var feedback = JsonDocument.Parse(result.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
        var corrected = Assert.Single(WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(3_000, corrected.GetProperty("contentOffset").GetInt32());
        AssertWireTextWindow(corrected, canonical);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireTextPreservation_serialized_budget_shortens_on_complete_unicode_boundaries(string protocol)
    {
        var canonical = WireTextFact + string.Concat(Enumerable.Repeat("😀", 2_000));
        // Exercise different exact serialized character envelopes; one extra unit can
        // otherwise admit half of a UTF-16 pair in the binary-search projection.
        for (var budget = 8_000; budget < 8_016; budget++)
        {
            using var factory = new QueuedHttpClientFactory(
                Completion("""{"queries":[{"query":"overview","topK":12}]}"""), WireTextAnswer(protocol));
            var options = WorkspaceOptions(protocol);
            options.MaximumEvidencePromptCharacters = budget;
            await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);
            var evidence = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
            AssertWireTextWindow(evidence, canonical);
            Assert.True(evidence.GetProperty("content").GetString()!.Length < 2_400);
            Assert.Equal(evidence.GetProperty("content").GetString()!.Length, evidence.GetProperty("nextContentOffset").GetInt32());
            Assert.InRange(evidence.GetRawText().Length, 1, budget - 2);
        }
    }

    [Fact]
    public async Task WireTextPreservation_responses_text_fragments_preserve_significant_boundary_spaces()
    {
        var split = NativeAnswered.IndexOf("17 bar", StringComparison.Ordinal);
        Assert.True(split > 0 && NativeAnswered[split - 1] == ' ');
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[]
                {
                    new { type = "output_text", text = NativeAnswered[..split] },
                    new { type = "output_text", text = NativeAnswered[split..] }
                } } }, usage = new { input_tokens = 100, output_tokens = 50 }
            }) });
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions("responses"), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", WireTextFact)), CancellationToken.None);
        using var expected = JsonDocument.Parse(NativeAnswered);
        Assert.Equal(expected.RootElement.GetProperty("answerText").GetString(), result.AnswerText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WireTextPreservation_checkpoint_rejects_split_character_offsets_before_synthesis(bool previous)
    {
        var canonical = WireTextPrefix(3_000) + "😀" + new string('y', 3_000);
        var gateway = new CheckpointToolGateway(BuildEvidence("E1", canonical));
        var window = previous
            ? new AdvancedAnalysisEvidenceReadCheckpoint(4_000, 256,
                [new AdvancedAnalysisEvidenceReadCheckpoint(3_001, 256)])
            : new AdvancedAnalysisEvidenceReadCheckpoint(3_001, 256);
        await gateway.SaveResearchCheckpointAsync(new AdvancedAnalysisResearchCheckpoint(
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, [], new Dictionary<string, string>(),
            EvidenceReadWindows: new Dictionary<string, AdvancedAnalysisEvidenceReadCheckpoint> { ["E1"] = window }),
            CancellationToken.None);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_research_checkpoint_invalid", error.ErrorCode);
        Assert.Single(factory.Requests);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireTextPreservation_navigation_metadata_is_shortened_without_replacement_characters(string protocol)
    {
        var section = new string('s', 239) + "😀suffix";
        var heading = new string('h', 499) + "😀suffix";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), WireTextAnswer(protocol));
        var source = BuildEvidence("E1", WireTextFact) with { SourceStructure = new(section, heading, "prior", "next") };
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(source), CancellationToken.None);
        var structure = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray())
            .GetProperty("sourceStructure");
        Assert.Equal(section[..239], structure.GetProperty("sectionTitle").GetString());
        Assert.Equal(heading[..499], structure.GetProperty("headingPath").GetString());
        Assert.True(structure.GetProperty("metadataTruncated").GetBoolean());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireTextPreservation_provisional_candidate_title_remains_an_exact_unicode_prefix(string protocol)
    {
        var title = new string('r', 239) + "😀suffix";
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.NativeCandidateExplorerEnabled = true; options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        // Stop after the Judge request has been captured. This deliberately
        // non-Judge response cannot validate or publish a provisional identity.
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(
                BuildEvidence("E1", title + "\nINGRÉDIENTS\nUne préparation complète et documentée.")), CancellationToken.None));
        Assert.Equal("advanced_candidate_judge_protocol_invalid", error.ErrorCode);
        var candidate = Assert.Single(WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("candidateInventory").GetProperty("items").EnumerateArray());
        Assert.Equal(title[..239], candidate.GetProperty("exactTitle").GetString());
    }
}
