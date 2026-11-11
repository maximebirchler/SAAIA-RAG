using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string CitationContextPartial = """{"outcome":"insufficient_documentation","answerText":"La limite documentée est 17 bar [C2]. Les autres unités restent à établir.","claims":[{"claimId":"C2","text":"La limite est 17 bar.","evidenceIds":["E1","E2"]}]}""";

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task CriticCitationContext_complete_answer_keeps_requested_positions_outside_claims(string protocol)
    {
        var request = BuildFixedSubjectRequest(false);
        var answer = JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = "Dix faits documentés. " + string.Join(' ', Enumerable.Range(1, 10).Select(i => $"[C{i}]")),
            claims = Enumerable.Range(1, 10).Select(i => new { claimId = $"C{i}", text = $"Le fait {i} décrit l'action documentée.", evidenceIds = new[] { "E1" } })
        });
        using var factory = new QueuedHttpClientFactory(Completion("""{"selectionMode":"content_claims","selectionBasis":"fixed_subject_attributes","queries":[{"query":"control procedure steps","topK":12}]}"""),
            EarlyReply(protocol, answer), EarlyReply(protocol, answer));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = true;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", "Le technicien vérifie le seuil.")), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var prompt = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal(10, prompt.GetProperty("claimCoordinates").GetArrayLength());
        var coordinate = prompt.GetProperty("claimCoordinates")[0];
        Assert.Equal("C1", coordinate.GetProperty("claimId").GetString());
        Assert.Equal("Actor/action", coordinate.GetProperty("columnLabel").GetString());
        Assert.Equal("answered_contract_positions", prompt.GetProperty("citationReview").GetProperty("candidatePlacement").GetString());
        Assert.Equal(new[] { "claimId", "selectedItem", "text", "evidenceIds" },
            prompt.GetProperty("candidate").GetProperty("claims")[0].EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("responses", "contract")]
    [InlineData("responses", "agent")]
    [InlineData("chat-completions", "contract")]
    [InlineData("chat-completions", "agent")]
    public async Task CriticCitationContext_preserves_partial_claim_schema_without_inventing_a_placement(
        string protocol, string style)
    {
        const string body = "La limite documentée est 17 bar.";
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            EarlyReply(protocol, CitationContextPartial), EarlyReply(protocol, CitationContextPartial));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = true; options.SynthesisPromptStyle = style;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFixedSubjectRequest(false), new RecordingToolGateway(
                BuildEvidence("E1", body, "limits.pdf", "doc", "rev"),
                BuildEvidence("E2", body, "limits.pdf", "doc", "rev")), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(new[] { "E1", "E2" }, Assert.Single(result.Claims).EvidenceIds);
        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var claim = Assert.Single(prompt.GetProperty("candidate").GetProperty("claims").EnumerateArray());
        Assert.Equal("C2", claim.GetProperty("claimId").GetString());
        Assert.Equal(new[] { "claimId", "selectedItem", "text", "evidenceIds" }, claim.EnumerateObject().Select(p => p.Name));
        Assert.Equal(10, prompt.GetProperty("claimCoordinates").GetArrayLength());
        var audit = prompt.GetProperty("citationReview");
        Assert.Equal("not_inferred_for_partial_answer", audit.GetProperty("candidatePlacement").GetString());
        Assert.Equal("mechanical_context_only", audit.GetProperty("status").GetString());
        var group = Assert.Single(audit.GetProperty("identicalCompletePassages").EnumerateArray());
        Assert.Equal(new[] { "E1", "E2" }, group.GetProperty("evidenceIds").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(body, audit.GetRawText(), StringComparison.Ordinal);
        Assert.All(prompt.GetProperty("evidence").EnumerateArray(), e => Assert.Equal(body, e.GetProperty("content").GetString()));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("page")]
    [InlineData("truncated")]
    public async Task CriticCitationContext_does_not_equate_other_sources_or_unread_suffixes(string difference)
    {
        var body = difference == "truncated" ? "La limite est 17 bar. " + new string('x', 4_000) : "La limite est 17 bar.";
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            Completion(CitationContextPartial), Completion(CitationContextPartial));
        var options = WorkspaceOptions(); options.SemanticCriticEnabled = true;
        options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(
                BuildEvidence("E1", body, "limits.pdf", "doc", "rev"),
                BuildEvidence("E2", body, "limits.pdf", difference == "document" ? "other-doc" : "doc",
                    difference == "revision" ? "other-rev" : "rev", difference == "page" ? 2 : 1)), CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[2].Body);
        Assert.Empty(prompt.GetProperty("citationReview").GetProperty("identicalCompletePassages").EnumerateArray());
        Assert.False(prompt.GetProperty("candidate").GetProperty("claims")[0].TryGetProperty("requestedCoordinate", out _));
        if (difference == "truncated")
            Assert.Contains(prompt.GetProperty("evidence").EnumerateArray(), e => e.GetProperty("contentTruncated").GetBoolean());
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task CriticCitationContext_is_rebuilt_after_reading_and_present_in_protocol_repair(string protocol, bool repair)
    {
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            EarlyReply(protocol, CitationContextPartial),
            repair ? EarlyReply(protocol, "invalid json")
                : PostResearchFunction(protocol, "read_evidence", """{"evidenceId":"E1","offset":10,"length":256}"""),
            EarlyReply(protocol, CitationContextPartial));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = true;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFixedSubjectRequest(false), new RecordingToolGateway(
                BuildEvidence("E1", "La limite documentée est 17 bar.", "limits.pdf", "doc", "rev"),
                BuildEvidence("E2", "La limite documentée est 17 bar.", "limits.pdf", "doc", "rev")), CancellationToken.None);

        var first = WorkspaceUser(factory.Requests[2].Body);
        var followup = WorkspaceUser(factory.Requests[3].Body);
        Assert.Single(first.GetProperty("citationReview").GetProperty("identicalCompletePassages").EnumerateArray());
        var candidate = followup.GetProperty(repair ? "validWriterCandidate" : "candidate");
        Assert.Equal(new[] { "claimId", "selectedItem", "text", "evidenceIds" },
            candidate.GetProperty("claims")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("not_inferred_for_partial_answer", followup.GetProperty("citationReview")
            .GetProperty("candidatePlacement").GetString());
        var groups = followup.GetProperty("citationReview").GetProperty("identicalCompletePassages");
        if (repair) Assert.Single(groups.EnumerateArray());
        else
        {
            Assert.Empty(groups.EnumerateArray());
            Assert.Equal(10, followup.GetProperty("evidence").EnumerateArray()
                .Single(e => e.GetProperty("evidenceId").GetString() == "E1").GetProperty("contentOffset").GetInt32());
        }
    }
}
