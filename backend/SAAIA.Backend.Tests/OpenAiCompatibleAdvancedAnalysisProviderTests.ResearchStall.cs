using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task Repeated_complete_excerpt_read_gets_specific_feedback_and_one_bounded_final_synthesis(string protocol, bool critic)
    {
        const string read = """{"evidenceId":"E1","offset":0,"length":1000}""";
        const string partial = """{"outcome":"insufficient_documentation","answerText":"Le seuil documenté est 17 bar [C1]. Le reste reste à vérifier.","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["E1"]}]}""";
        var canonical = BuildEvidence("E1", "Le seuil est 17 bar.");
        var replies = new List<HttpResponseMessage> {
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            PostResearchFunction(protocol, "read_evidence", read),
            PostResearchFunction(protocol, "read_evidence", read),
            PostResearchFunction(protocol, "read_evidence", read),
            EarlyReply(protocol, partial) };
        if (critic) replies.Add(EarlyReply(protocol, partial));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = critic ? 6 : 5;
        options.SemanticCriticEnabled = critic;
        var gateway = new CheckpointToolGateway(canonical);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal("E1", Assert.Single(Assert.Single(result.Claims).EvidenceIds));
        Assert.Equal(critic ? 6 : 5, result.ProviderCallCount);
        var correction = WorkspaceUser(factory.Requests[3].Body).GetProperty("researchFeedback");
        Assert.Equal("evidence_window_already_available", correction.GetProperty("reasonCode").GetString());
        var window = Assert.Single(correction.GetProperty("evidenceReads").EnumerateArray());
        Assert.Equal("E1", window.GetProperty("evidenceId").GetString());
        Assert.Equal(canonical.Content.Length, window.GetProperty("originalContentLength").GetInt32());
        Assert.True(window.GetProperty("wholeExcerptVisible").GetBoolean());
        Assert.DoesNotContain(canonical.Content, correction.GetRawText(), StringComparison.Ordinal);
        using var final = JsonDocument.Parse(factory.Requests[4].Body);
        Assert.False(final.RootElement.TryGetProperty("tools", out _));
        var payload = WorkspaceUser(factory.Requests[4].Body);
        Assert.False(payload.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.False(payload.GetProperty("researchTools").GetProperty("evidenceReadingAllowed").GetBoolean());
        Assert.True(payload.GetProperty("researchFeedback").GetProperty("finalSynthesisRequired").GetBoolean());
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("Operation")?.ToString() == "llm.research_stall"
            && e.Values.GetValueOrDefault("Outcome")?.ToString() == "final_synthesis");
        if (critic) Assert.True(WorkspaceUser(factory.Requests[5].Body).TryGetProperty("candidate", out _));
    }

    [Theory]
    [InlineData("responses", "budget")]
    [InlineData("chat-completions", "budget")]
    [InlineData("responses", "tool_again")]
    [InlineData("chat-completions", "tool_again")]
    [InlineData("responses", "unknown_citation")]
    [InlineData("chat-completions", "unknown_citation")]
    public async Task Stall_finalization_cannot_extend_budget_restart_research_or_admit_unknown_citations(string protocol, string failure)
    {
        const string read = """{"evidenceId":"E1","offset":0,"length":1000}""";
        var replies = new List<HttpResponseMessage> {
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            PostResearchFunction(protocol, "read_evidence", read),
            PostResearchFunction(protocol, "read_evidence", read),
            PostResearchFunction(protocol, "read_evidence", read) };
        if (failure != "budget") replies.Add(failure == "tool_again"
            ? PostResearchFunction(protocol, "read_evidence", read)
            : EarlyReply(protocol, NativeAnswered.Replace("E1", "UNKNOWN", StringComparison.Ordinal)));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = failure == "budget" ? 4 : 5;
        var gateway = new CheckpointToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));

        Assert.Equal(failure == "budget" ? 4 : 5, factory.Requests.Count);
        // The final budgeted request already withdrew all tools. A scripted
        // provider ignoring that declaration must fail before any execution.
        if (failure is "budget" or "tool_again") Assert.Equal("advanced_native_tool_not_available", error.ErrorCode);
        using var last = JsonDocument.Parse(factory.Requests[^1].Body);
        Assert.False(last.RootElement.TryGetProperty("tools", out _));
    }
}
